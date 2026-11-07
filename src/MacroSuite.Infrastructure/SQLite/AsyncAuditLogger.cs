using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using MacroSuite.Core.Audit;
using MacroSuite.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MacroSuite.Infrastructure.SQLite;

/// <summary>
/// High-throughput, non-blocking audit logging engine implementing <see cref="IAuditLogger"/> (Rule 10, Rule 18).
/// Enqueues audit events to an in-memory channel drained by a dedicated background writer task into SQLite (WAL mode).
/// Stores screenshot thumbnails on the filesystem with an active 500MB quota guard.
/// </summary>
public sealed class AsyncAuditLogger : IAuditLogger
{
    private static readonly Regex s_apiKeyPattern = new(
        @"(?:sk-[a-zA-Z0-9\-_]{20,}|AIza[0-9A-Za-z\-_]{35}|Bearer\s+[a-zA-Z0-9\-\._~+/]+=*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly string _connectionString;
    private readonly string _thumbnailsDirectory;
    private readonly long _maxQuotaBytes;
    private readonly Channel<AuditEntry> _channel;
    private readonly CancellationTokenSource _cts = new();
    private Task _writerTask = Task.CompletedTask;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private int _inFlightCount;
    private int _droppedCount;
    private int _consecutiveBatchFailures;
    private int _health = (int)AuditLoggerHealth.Running;
    private int _writerStarted;
    private bool _initialized;
    private bool _disposed;

    public AuditLoggerHealth Health => (AuditLoggerHealth)Volatile.Read(ref _health);

    internal int DroppedCount => Volatile.Read(ref _droppedCount);
    internal int ChannelCapacity { get; }

    public AsyncAuditLogger(
        string? customDbPath = null,
        string? customThumbnailsDir = null,
        long maxQuotaBytes = 500L * 1024 * 1024,
        int channelCapacity = 10_000)
        : this(customDbPath, customThumbnailsDir, maxQuotaBytes, channelCapacity, holdWriter: false)
    {
    }

    /// <summary>
    /// Test constructor. When <paramref name="holdWriter"/> is true the background writer is not started
    /// so overflow/drop behavior is deterministic.
    /// </summary>
    internal AsyncAuditLogger(
        string? customDbPath,
        string? customThumbnailsDir,
        long maxQuotaBytes,
        int channelCapacity,
        bool holdWriter)
    {
        string dbPath = customDbPath ?? Path.Combine(AppDataPathResolver.Default.AuditDirectory, "audit.db");
        _thumbnailsDirectory = customThumbnailsDir ?? Path.Combine(AppDataPathResolver.Default.AuditDirectory, "thumbnails");
        _maxQuotaBytes = maxQuotaBytes;
        ChannelCapacity = Math.Max(1, channelCapacity);

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

        Directory.CreateDirectory(_thumbnailsDirectory);

        _channel = Channel.CreateBounded<AuditEntry>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true
        });

        if (holdWriter)
        {
            _writerTask = Task.CompletedTask;
        }
        else
        {
            StartWriter();
        }
    }

    internal void ReleaseWriterForTests() => StartWriter();

    private void StartWriter()
    {
        if (Interlocked.CompareExchange(ref _writerStarted, 1, 0) != 0)
        {
            return;
        }

        _writerTask = Task.Run(ProcessQueueAsync);
    }

    /// <inheritdoc/>
    public void Enqueue(AuditEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entry);

        // Sanitize entry per Rule 10 before queueing
        var sanitized = SanitizeEntry(entry);
        Interlocked.Increment(ref _inFlightCount);
        if (!_channel.Writer.TryWrite(sanitized))
        {
            Interlocked.Decrement(ref _inFlightCount);
            Interlocked.Increment(ref _droppedCount);
            SetHealth(AuditLoggerHealth.Degraded);
        }
    }

    /// <summary>
    /// Saves screenshot thumbnail bytes to disk and returns the relative file path for audit tracking.
    /// </summary>
    public async Task<string?> SaveThumbnailAsync(byte[]? thumbnailBytes, Guid entryId, CancellationToken ct = default)
    {
        if (thumbnailBytes == null || thumbnailBytes.Length == 0)
        {
            return null;
        }

        string fileName = $"{entryId:N}.jpg";
        string filePath = Path.Combine(_thumbnailsDirectory, fileName);

        await File.WriteAllBytesAsync(filePath, thumbnailBytes, ct).ConfigureAwait(false);
        return filePath;
    }

    /// <inheritdoc/>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Health == AuditLoggerHealth.Faulted || Volatile.Read(ref _droppedCount) > 0)
        {
            throw new InvalidOperationException("Audit logger is faulted; one or more events were not written.");
        }

        while (Volatile.Read(ref _inFlightCount) > 0 && !ct.IsCancellationRequested)
        {
            if (Health == AuditLoggerHealth.Faulted || Volatile.Read(ref _droppedCount) > 0)
                throw new InvalidOperationException("Audit logger is faulted; one or more events were not written.");
            if (_writerTask.IsCompleted)
                throw new InvalidOperationException("Audit logger worker stopped before all events were written.");
            await Task.Delay(5, ct).ConfigureAwait(false);
        }

        if (Volatile.Read(ref _droppedCount) > 0 || Volatile.Read(ref _inFlightCount) > 0)
        {
            throw new InvalidOperationException("Audit flush failed: one or more events were dropped or not written.");
        }
    }

    /// <summary>
    /// Retrieves recent audit log entries from the database.
    /// </summary>
    public async Task<IReadOnlyList<AuditEntry>> GetRecentEntriesAsync(int count = 100, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await _dbLock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, timestamp_utc, run_id, macro_id, step_id, provider_id, model_id,
                       prompt, proposed_action, validation_result, approval_result,
                       executed_action, screenshot_path, error, input_tokens, output_tokens,
                       estimated_cost_usd
                FROM audit_log
                ORDER BY timestamp_utc DESC
                LIMIT @limit;
            """;
            cmd.Parameters.AddWithValue("@limit", count);

            var list = new List<AuditEntry>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(new AuditEntry
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    TimestampUtc = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                    RunId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                    MacroId = reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
                    StepId = reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                    ProviderId = reader.GetString(5),
                    ModelId = reader.GetString(6),
                    Prompt = reader.GetString(7),
                    ProposedAction = reader.IsDBNull(8) ? null : reader.GetString(8),
                    ValidationResult = reader.IsDBNull(9) ? null : reader.GetString(9),
                    ApprovalResult = reader.IsDBNull(10) ? null : reader.GetString(10),
                    ExecutedAction = reader.IsDBNull(11) ? null : reader.GetString(11),
                    ScreenshotPath = reader.IsDBNull(12) ? null : reader.GetString(12),
                    Error = reader.IsDBNull(13) ? null : reader.GetString(13),
                    InputTokens = reader.GetInt32(14),
                    OutputTokens = reader.GetInt32(15),
                    EstimatedCostUsd = decimal.Parse(reader.GetString(16), CultureInfo.InvariantCulture)
                });
            }

            return list;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    private async Task ProcessQueueAsync()
    {
        SetHealth(AuditLoggerHealth.Running);
        try
        {
            var batch = new List<AuditEntry>(64);
            var reader = _channel.Reader;

            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await EnsureInitializedAsync(_cts.Token).ConfigureAwait(false);

                    if (!await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                        break;

                    batch.Clear();
                    while (batch.Count < 64 && reader.TryRead(out var item))
                    {
                        batch.Add(item);
                    }

                    if (batch.Count == 0)
                        continue;

                    bool written = false;
                    for (int attempt = 0; attempt < 3 && !written; attempt++)
                    {
                        try
                        {
                            await WriteBatchAsync(batch, _cts.Token).ConfigureAwait(false);
                            written = true;
                            _consecutiveBatchFailures = 0;
                            if (Health == AuditLoggerHealth.Degraded && Volatile.Read(ref _droppedCount) == 0)
                                SetHealth(AuditLoggerHealth.Running);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception)
                        {
                            _consecutiveBatchFailures++;
                            SetHealth(_consecutiveBatchFailures >= 3
                                ? AuditLoggerHealth.Faulted
                                : AuditLoggerHealth.Degraded);
                            if (_consecutiveBatchFailures < 3)
                                await Task.Delay(50 * (attempt + 1), _cts.Token).ConfigureAwait(false);
                        }
                    }

                    if (written)
                    {
                        Interlocked.Add(ref _inFlightCount, -batch.Count);
                    }
                    else
                    {
                        SetHealth(AuditLoggerHealth.Faulted);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    _consecutiveBatchFailures++;
                    SetHealth(_consecutiveBatchFailures >= 3
                        ? AuditLoggerHealth.Faulted
                        : AuditLoggerHealth.Degraded);
                    try
                    {
                        await Task.Delay(100, _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetHealth(_cts.IsCancellationRequested ? AuditLoggerHealth.Stopped : Health);
        }
    }

    private void SetHealth(AuditLoggerHealth health)
        => Interlocked.Exchange(ref _health, (int)health);

    private async Task WriteBatchAsync(List<AuditEntry> entries, CancellationToken ct)
    {
        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var tx = conn.BeginTransaction();

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO audit_log (
                    id, timestamp_utc, run_id, macro_id, step_id, provider_id, model_id,
                    prompt, proposed_action, validation_result, approval_result,
                    executed_action, screenshot_path, error, input_tokens, output_tokens,
                    estimated_cost_usd
                ) VALUES (
                    @id, @timestamp, @runId, @macroId, @stepId, @providerId, @modelId,
                    @prompt, @proposedAction, @validationResult, @approvalResult,
                    @executedAction, @screenshotPath, @error, @inputTokens, @outputTokens,
                    @estimatedCost
                );
            """;

            var pId = cmd.Parameters.Add("@id", SqliteType.Text);
            var pTime = cmd.Parameters.Add("@timestamp", SqliteType.Text);
            var pRunId = cmd.Parameters.Add("@runId", SqliteType.Text);
            var pMacroId = cmd.Parameters.Add("@macroId", SqliteType.Text);
            var pStepId = cmd.Parameters.Add("@stepId", SqliteType.Text);
            var pProviderId = cmd.Parameters.Add("@providerId", SqliteType.Text);
            var pModelId = cmd.Parameters.Add("@modelId", SqliteType.Text);
            var pPrompt = cmd.Parameters.Add("@prompt", SqliteType.Text);
            var pProposed = cmd.Parameters.Add("@proposedAction", SqliteType.Text);
            var pValidation = cmd.Parameters.Add("@validationResult", SqliteType.Text);
            var pApproval = cmd.Parameters.Add("@approvalResult", SqliteType.Text);
            var pExecuted = cmd.Parameters.Add("@executedAction", SqliteType.Text);
            var pScreenshot = cmd.Parameters.Add("@screenshotPath", SqliteType.Text);
            var pError = cmd.Parameters.Add("@error", SqliteType.Text);
            var pInputTokens = cmd.Parameters.Add("@inputTokens", SqliteType.Integer);
            var pOutputTokens = cmd.Parameters.Add("@outputTokens", SqliteType.Integer);
            var pCost = cmd.Parameters.Add("@estimatedCost", SqliteType.Text);

            foreach (var e in entries)
            {
                pId.Value = e.Id.ToString();
                pTime.Value = e.TimestampUtc.ToString("o", CultureInfo.InvariantCulture);
                pRunId.Value = e.RunId.HasValue ? e.RunId.Value.ToString() : DBNull.Value;
                pMacroId.Value = e.MacroId.HasValue ? e.MacroId.Value.ToString() : DBNull.Value;
                pStepId.Value = e.StepId.HasValue ? e.StepId.Value.ToString() : DBNull.Value;
                pProviderId.Value = e.ProviderId;
                pModelId.Value = e.ModelId;
                pPrompt.Value = e.Prompt;
                pProposed.Value = (object?)e.ProposedAction ?? DBNull.Value;
                pValidation.Value = (object?)e.ValidationResult ?? DBNull.Value;
                pApproval.Value = (object?)e.ApprovalResult ?? DBNull.Value;
                pExecuted.Value = (object?)e.ExecutedAction ?? DBNull.Value;
                pScreenshot.Value = (object?)e.ScreenshotPath ?? DBNull.Value;
                pError.Value = (object?)e.Error ?? DBNull.Value;
                pInputTokens.Value = e.InputTokens;
                pOutputTokens.Value = e.OutputTokens;
                pCost.Value = e.EstimatedCostUsd.ToString(CultureInfo.InvariantCulture);

                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);

            EnforceQuotaInternal();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    private void EnforceQuotaInternal()
    {
        try
        {
            if (!Directory.Exists(_thumbnailsDirectory)) return;

            var dirInfo = new DirectoryInfo(_thumbnailsDirectory);
            var files = dirInfo.GetFiles("*.jpg").OrderBy(f => f.CreationTimeUtc).ToList();
            long totalBytes = files.Sum(f => f.Length);

            if (totalBytes > _maxQuotaBytes)
            {
                long bytesToRemove = totalBytes - (_maxQuotaBytes * 8 / 10); // Prune to 80% of quota
                long removed = 0;

                foreach (var f in files)
                {
                    if (removed >= bytesToRemove) break;
                    try
                    {
                        long size = f.Length;
                        f.Delete();
                        removed += size;
                    }
                    catch
                    {
                        // Ignore busy file deletion
                    }
                }
            }
        }
        catch
        {
            // Ignore quota background maintenance errors
        }
    }

    private static AuditEntry SanitizeEntry(AuditEntry entry)
    {
        return new AuditEntry
        {
            Id = entry.Id,
            TimestampUtc = entry.TimestampUtc,
            RunId = entry.RunId,
            MacroId = entry.MacroId,
            StepId = entry.StepId,
            ProviderId = entry.ProviderId,
            ModelId = entry.ModelId,
            Prompt = SanitizeString(entry.Prompt),
            ProposedAction = SanitizeString(entry.ProposedAction),
            ValidationResult = SanitizeString(entry.ValidationResult),
            ApprovalResult = entry.ApprovalResult,
            ExecutedAction = SanitizeString(entry.ExecutedAction),
            ScreenshotPath = entry.ScreenshotPath,
            Error = SanitizeString(entry.Error),
            InputTokens = entry.InputTokens,
            OutputTokens = entry.OutputTokens,
            EstimatedCostUsd = entry.EstimatedCostUsd
        };
    }

    private static string SanitizeString(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return s_apiKeyPattern.Replace(value, "[REDACTED_API_KEY]");
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA busy_timeout = 5000;

                CREATE TABLE IF NOT EXISTS audit_log (
                    id TEXT PRIMARY KEY,
                    timestamp_utc TEXT NOT NULL,
                    run_id TEXT,
                    macro_id TEXT,
                    step_id TEXT,
                    provider_id TEXT NOT NULL,
                    model_id TEXT NOT NULL,
                    prompt TEXT NOT NULL,
                    proposed_action TEXT,
                    validation_result TEXT,
                    approval_result TEXT,
                    executed_action TEXT,
                    screenshot_path TEXT,
                    error TEXT,
                    input_tokens INTEGER NOT NULL,
                    output_tokens INTEGER NOT NULL,
                    estimated_cost_usd TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_audit_timestamp ON audit_log(timestamp_utc);
            """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        SetHealth(AuditLoggerHealth.Stopping);
        _channel.Writer.TryComplete();
        _cts.Cancel();

        try
        {
            await _writerTask.ConfigureAwait(false);
        }
        catch
        {
            // Suppress background task cancellation
        }

        _cts.Dispose();
        _dbLock.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
