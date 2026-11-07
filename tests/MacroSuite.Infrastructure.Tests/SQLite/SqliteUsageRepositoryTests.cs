using FluentAssertions;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Usage;
using MacroSuite.Infrastructure.SQLite;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.SQLite;

public sealed class SqliteUsageRepositoryTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly SqliteUsageRepository _repository;

    public SqliteUsageRepositoryTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"ZenithMacros_Test_Usage_{Guid.NewGuid():N}.db");
        _repository = new SqliteUsageRepository(_testDbPath);
    }

    [Fact]
    public async Task RecordAsync_PersistsRecord_AndCalculatesTotalCost()
    {
        var record1 = new UsageRecord
        {
            ProviderId = "google",
            ModelId = "gemini-1.5-flash",
            InputTokens = 1500,
            OutputTokens = 250,
            EstimatedCostUsd = 0.00125m,
            Feature = FeatureId.AIVision
        };

        var record2 = new UsageRecord
        {
            ProviderId = "openai",
            ModelId = "gpt-4o",
            InputTokens = 2000,
            OutputTokens = 400,
            EstimatedCostUsd = 0.01500m,
            Feature = FeatureId.AIVision
        };

        await _repository.RecordAsync(record1);
        await _repository.RecordAsync(record2);

        decimal total = await _repository.GetTotalEstimatedCostAsync(DateTimeOffset.UtcNow.AddHours(-1));
        total.Should().Be(0.00125m + 0.01500m);
    }

    [Fact]
    public async Task GetRecentAsync_ReturnsOrderedRecordsDescending()
    {
        var now = DateTimeOffset.UtcNow;
        var r1 = new UsageRecord
        {
            TimestampUtc = now.AddMinutes(-5),
            ProviderId = "p1",
            ModelId = "m1",
            EstimatedCostUsd = 0.01m,
            Feature = FeatureId.AIVision
        };

        var r2 = new UsageRecord
        {
            TimestampUtc = now,
            ProviderId = "p2",
            ModelId = "m2",
            EstimatedCostUsd = 0.02m,
            Feature = FeatureId.AIVision
        };

        await _repository.RecordAsync(r1);
        await _repository.RecordAsync(r2);

        var recent = await _repository.GetRecentAsync(10);
        recent.Should().HaveCount(2);
        recent[0].ProviderId.Should().Be("p2");
        recent[1].ProviderId.Should().Be("p1");
    }

    [Fact]
    public async Task GetTotalEstimatedCostAsync_RespectsSinceTimestamp()
    {
        var past = DateTimeOffset.UtcNow.AddDays(-10);
        var oldRecord = new UsageRecord
        {
            TimestampUtc = past,
            EstimatedCostUsd = 5.00m,
            Feature = FeatureId.AIVision
        };

        var newRecord = new UsageRecord
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            EstimatedCostUsd = 2.00m,
            Feature = FeatureId.AIVision
        };

        await _repository.RecordAsync(oldRecord);
        await _repository.RecordAsync(newRecord);

        decimal totalSinceYesterday = await _repository.GetTotalEstimatedCostAsync(DateTimeOffset.UtcNow.AddDays(-1));
        totalSinceYesterday.Should().Be(2.00m);
    }

    public void Dispose()
    {
        _repository.Dispose();
        try
        {
            if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
            string walPath = $"{_testDbPath}-wal";
            string shmPath = $"{_testDbPath}-shm";
            if (File.Exists(walPath)) File.Delete(walPath);
            if (File.Exists(shmPath)) File.Delete(shmPath);
        }
        catch
        {
            // Best effort cleanup in tests
        }
    }
}
