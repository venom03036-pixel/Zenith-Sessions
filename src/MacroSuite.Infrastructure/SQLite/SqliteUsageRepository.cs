using System.Globalization;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Usage;
using Microsoft.Data.Sqlite;

namespace MacroSuite.Infrastructure.SQLite;

/// <summary>
/// SQLite-backed persistent repository for usage and spend records (Rule 18).
/// Employs WAL (Write-Ahead Logging) mode for concurrent reader/writer stability.
/// </summary>
public sealed class SqliteUsageRepository : IUsageRepository
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private bool _initialized;
    private bool _isDisposed;

    public SqliteUsageRepository(string? customDbPath = null)
    {
        string dbPath = customDbPath ?? Path.Combine(AppDataPathResolver.GetUsageDirectory(), "usage.db");

        if (dbPath != ":memory:" && !dbPath.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
        {
            string? dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            _connectionString = $"Data Source={dbPath};";
        }
        else
        {
            _connectionString = dbPath.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase) ? dbPath : $"Data Source={dbPath};";
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);

            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA busy_timeout = 5000;

                CREATE TABLE IF NOT EXISTS usage_records (
                    id TEXT PRIMARY KEY,
                    timestamp_utc TEXT NOT NULL,
                    provider_id TEXT NOT NULL,
                    model_id TEXT NOT NULL,
                    provider_request_id TEXT,
                    input_tokens INTEGER NOT NULL,
                    output_tokens INTEGER NOT NULL,
                    estimated_cost_usd TEXT NOT NULL,
                    feature TEXT NOT NULL,
                    macro_id TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_usage_records_timestamp ON usage_records(timestamp_utc);
                CREATE INDEX IF NOT EXISTS idx_usage_records_feature ON usage_records(feature);
            """;

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task RecordAsync(UsageRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);

            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO usage_records (
                    id, timestamp_utc, provider_id, model_id, provider_request_id,
                    input_tokens, output_tokens, estimated_cost_usd, feature, macro_id
                ) VALUES (
                    @id, @timestamp, @providerId, @modelId, @providerReqId,
                    @inputTokens, @outputTokens, @cost, @feature, @macroId
                );
            """;

            command.Parameters.AddWithValue("@id", record.Id.ToString());
            command.Parameters.AddWithValue("@timestamp", record.TimestampUtc.ToString("o", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@providerId", record.ProviderId);
            command.Parameters.AddWithValue("@modelId", record.ModelId);
            command.Parameters.AddWithValue("@providerReqId", (object?)record.ProviderRequestId ?? DBNull.Value);
            command.Parameters.AddWithValue("@inputTokens", record.InputTokens);
            command.Parameters.AddWithValue("@outputTokens", record.OutputTokens);
            command.Parameters.AddWithValue("@cost", record.EstimatedCostUsd.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@feature", record.Feature.ToString());
            command.Parameters.AddWithValue("@macroId", record.MacroId.HasValue ? record.MacroId.Value.ToString() : DBNull.Value);

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<decimal> GetTotalEstimatedCostAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        UsageTotals totals = await GetTotalsAsync(since, ct).ConfigureAwait(false);
        return totals.CostUsd;
    }

    /// <inheritdoc/>
    public async Task<UsageTotals> GetTotalsAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT estimated_cost_usd, input_tokens, output_tokens
                FROM usage_records
                WHERE timestamp_utc >= @since;
            """;

            command.Parameters.AddWithValue("@since", since.ToString("o", CultureInfo.InvariantCulture));

            decimal total = 0m;
            int inputTokens = 0;
            int outputTokens = 0;
            int requests = 0;
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                requests++;
                string rawCost = reader.GetString(0);
                if (decimal.TryParse(rawCost, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal cost))
                    total += cost;
                inputTokens += reader.GetInt32(1);
                outputTokens += reader.GetInt32(2);
            }

            return new UsageTotals(total, inputTokens, outputTokens, requests);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<UsageRecord>> GetRecentAsync(int count, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (count <= 0) return Array.Empty<UsageRecord>();

        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, timestamp_utc, provider_id, model_id, provider_request_id,
                       input_tokens, output_tokens, estimated_cost_usd, feature, macro_id
                FROM usage_records
                ORDER BY timestamp_utc DESC
                LIMIT @count;
            """;

            command.Parameters.AddWithValue("@count", count);

            var list = new List<UsageRecord>(count);
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                Guid id = Guid.Parse(reader.GetString(0));
                DateTimeOffset timestamp = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
                string providerId = reader.GetString(2);
                string modelId = reader.GetString(3);
                string? providerReqId = reader.IsDBNull(4) ? null : reader.GetString(4);
                int inputTokens = reader.GetInt32(5);
                int outputTokens = reader.GetInt32(6);
                decimal cost = decimal.Parse(reader.GetString(7), CultureInfo.InvariantCulture);
                FeatureId feature = Enum.Parse<FeatureId>(reader.GetString(8));
                Guid? macroId = reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9));

                list.Add(new UsageRecord
                {
                    Id = id,
                    TimestampUtc = timestamp,
                    ProviderId = providerId,
                    ModelId = modelId,
                    ProviderRequestId = providerReqId,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    EstimatedCostUsd = cost,
                    Feature = feature,
                    MacroId = macroId
                });
            }

            return list;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _dbLock.Dispose();
    }
}
