using FluentAssertions;
using MacroSuite.Core.Audit;
using MacroSuite.Infrastructure.SQLite;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Audit;

public class AsyncAuditLoggerTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly string _tempThumbnailsDir;

    public AsyncAuditLoggerTests()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), $"audit_test_{Guid.NewGuid():N}");
        _tempDbPath = Path.Combine(baseDir, "test_audit.db");
        _tempThumbnailsDir = Path.Combine(baseDir, "thumbnails");
    }

    public void Dispose()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_tempDbPath);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task EnqueueAndFlush_WritesRecordsToSqlite()
    {
        await using var logger = new AsyncAuditLogger(_tempDbPath, _tempThumbnailsDir);

        var entry = new AuditEntry
        {
            ProviderId = "openai",
            ModelId = "gpt-4o",
            Prompt = "Click the Login button",
            ProposedAction = "click",
            ExecutedAction = "click (500, 300)",
            InputTokens = 150,
            OutputTokens = 25,
            EstimatedCostUsd = 0.002m
        };

        logger.Enqueue(entry);
        await logger.FlushAsync();

        var recent = await logger.GetRecentEntriesAsync(10);
        recent.Should().HaveCount(1);
        recent[0].ProviderId.Should().Be("openai");
        recent[0].Prompt.Should().Be("Click the Login button");
        recent[0].ExecutedAction.Should().Be("click (500, 300)");
        recent[0].EstimatedCostUsd.Should().Be(0.002m);
    }

    [Fact]
    public async Task Rule10_SecretHygiene_SanitizesApiKeysFromAuditEntries()
    {
        await using var logger = new AsyncAuditLogger(_tempDbPath, _tempThumbnailsDir);

        const string leakKey = "sk-proj-1234567890abcdefghijklmn";
        var entry = new AuditEntry
        {
            ProviderId = "openai",
            ModelId = "gpt-4o",
            Prompt = $"Using key {leakKey} for request",
            Error = $"Unauthorized token: {leakKey}"
        };

        logger.Enqueue(entry);
        await logger.FlushAsync();

        var recent = await logger.GetRecentEntriesAsync(10);
        recent.Should().HaveCount(1);

        // Assert that raw key was redacted and never persisted to the database
        recent[0].Prompt.Should().NotContain(leakKey);
        recent[0].Prompt.Should().Contain("[REDACTED_API_KEY]");
        recent[0].Error.Should().NotContain(leakKey);
        recent[0].Error.Should().Contain("[REDACTED_API_KEY]");
    }

    [Fact]
    public async Task SaveThumbnailAsync_PersistsImageFile()
    {
        await using var logger = new AsyncAuditLogger(_tempDbPath, _tempThumbnailsDir);

        byte[] dummyBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
        var entryId = Guid.NewGuid();

        string? path = await logger.SaveThumbnailAsync(dummyBytes, entryId);

        path.Should().NotBeNull();
        File.Exists(path!).Should().BeTrue();
        (await File.ReadAllBytesAsync(path!)).Should().BeEquivalentTo(dummyBytes);
    }

    [Fact]
    public async Task QuotaEnforcement_DeletesOldThumbnailsWhenExceedingQuota()
    {
        // Set very small quota (500 bytes)
        await using var logger = new AsyncAuditLogger(_tempDbPath, _tempThumbnailsDir, maxQuotaBytes: 500);

        // Create several thumbnails
        byte[] payload = new byte[300];
        Array.Fill(payload, (byte)0x42);

        await logger.SaveThumbnailAsync(payload, Guid.NewGuid());
        await Task.Delay(10);
        await logger.SaveThumbnailAsync(payload, Guid.NewGuid());
        await Task.Delay(10);
        await logger.SaveThumbnailAsync(payload, Guid.NewGuid());

        // Enqueue an entry to trigger batch write and quota enforcement
        logger.Enqueue(new AuditEntry { ProviderId = "openai", ModelId = "gpt-4o", Prompt = "test" });
        await logger.FlushAsync();

        var remainingFiles = Directory.GetFiles(_tempThumbnailsDir, "*.jpg");
        // Should have pruned oldest files to fit within quota
        remainingFiles.Length.Should().BeLessThan(3);
    }

    [Fact]
    public async Task FlushAsync_FailsWhenEventsAreDropped()
    {
        await using var logger = new AsyncAuditLogger(
            _tempDbPath,
            _tempThumbnailsDir,
            maxQuotaBytes: 500L * 1024 * 1024,
            channelCapacity: 4,
            holdWriter: true);
        for (int i = 0; i < 40; i++)
        {
            logger.Enqueue(new AuditEntry
            {
                ProviderId = "openai",
                ModelId = "gpt-4o",
                Prompt = $"drop-{i}"
            });
        }

        logger.ChannelCapacity.Should().Be(4);
        logger.DroppedCount.Should().BeGreaterThan(0, "writer is held so the bounded channel must reject overflow");
        logger.Health.Should().Be(AuditLoggerHealth.Degraded);
        var act = () => logger.FlushAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not written*");
    }
}
