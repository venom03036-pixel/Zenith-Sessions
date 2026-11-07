using FluentAssertions;
using MacroSuite.Core.Audit;
using MacroSuite.Infrastructure.SQLite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Hardening;

public sealed class SecretAbsenceInLogsTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly string _testThumbnailsDir;

    public SecretAbsenceInLogsTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"Zenith_SecretAudit_{Guid.NewGuid():N}.db");
        _testThumbnailsDir = Path.Combine(Path.GetTempPath(), $"Zenith_SecretThumbnails_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
            if (Directory.Exists(_testThumbnailsDir)) Directory.Delete(_testThumbnailsDir, true);
        }
        catch { }
    }

    [Fact]
    public async Task AuditLogPersistence_NeverStoresApiKeysOrTokensInPlaintext_Rule10()
    {
        const string openAiKey = "sk-proj-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789";
        const string anthropicKey = "sk-ant-api03-abcdefghijklmnopqrstuvwxyz01234567890123456789";
        const string geminiKey = "AIzaSyD-1234567890abcdefghijklmnopqrstuv";

        await using (var logger = new AsyncAuditLogger(_testDbPath, _testThumbnailsDir))
        {
            logger.Enqueue(new AuditEntry
            {
                ProviderId = "openai",
                ModelId = "gpt-4o",
                Prompt = $"Find button using key {openAiKey} on screen",
                ProposedAction = $"click with token {anthropicKey}",
                ValidationResult = "Passed",
                Error = $"Failed to authenticate with key {geminiKey}: 401 Unauthorized"
            });

            await logger.FlushAsync();
        }

        // Direct raw SQLite inspection across all rows and columns
        using var conn = new SqliteConnection($"Data Source={_testDbPath};");
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT prompt, proposed_action, error FROM audit_log;";

        using var reader = await cmd.ExecuteReaderAsync();
        reader.HasRows.Should().BeTrue();

        while (await reader.ReadAsync())
        {
            string prompt = reader.GetString(0);
            string proposedAction = reader.GetString(1);
            string error = reader.GetString(2);

            // Assert NO API key patterns leaked
            prompt.Should().NotContain(openAiKey);
            prompt.Should().Contain("[REDACTED_API_KEY]");

            proposedAction.Should().NotContain(anthropicKey);
            proposedAction.Should().Contain("[REDACTED_API_KEY]");

            error.Should().NotContain(geminiKey);
            error.Should().Contain("[REDACTED_API_KEY]");
        }
    }
}
