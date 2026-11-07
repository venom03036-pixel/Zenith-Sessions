using FluentAssertions;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Usage;
using MacroSuite.Infrastructure.SQLite;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Hardening;

public sealed class SpendCapPersistenceTests : IDisposable
{
    private readonly string _testDbPath;

    public SpendCapPersistenceTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"Zenith_SpendCap_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { }
        }
    }

    private sealed class PermissiveFeatureGate : IFeatureGate
    {
        public bool IsAvailable(FeatureId feature) => true;
        public void Require(FeatureId feature) { }
    }

    [Fact]
    public async Task RestartSimulation_MaintainsUsageHistory_AndBlocksOverLimitRequests()
    {
        // -------------------------------------------------------------
        // SESSION 1: Initial app run — record usage
        // -------------------------------------------------------------
        using (var repoSession1 = new SqliteUsageRepository(_testDbPath))
        {
            var trackerSession1 = new UsageTracker(repoSession1, new PermissiveFeatureGate())
            {
                DailySpendCapUsd = 1.00m,
                MonthlySpendCapUsd = 10.00m
            };

            await trackerSession1.RecordAsync(new UsageRecord
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                ProviderId = "openai",
                ModelId = "gpt-4o",
                InputTokens = 1000,
                OutputTokens = 500,
                EstimatedCostUsd = 0.90m,
                Feature = FeatureId.AIVision
            });

            // Preflight check with small cost still passes (0.90 + 0.05 = 0.95 <= 1.00)
            await trackerSession1.PreflightCheckAsync(0.05m);
        }

        // -------------------------------------------------------------
        // SESSION 2: Simulated process restart — new repository & tracker
        // -------------------------------------------------------------
        using (var repoSession2 = new SqliteUsageRepository(_testDbPath))
        {
            var trackerSession2 = new UsageTracker(repoSession2, new PermissiveFeatureGate())
            {
                DailySpendCapUsd = 1.00m,
                MonthlySpendCapUsd = 10.00m
            };

            // Preflight check with 0.15 should exceed daily cap (0.90 + 0.15 = 1.05 > 1.00)
            var act = () => trackerSession2.PreflightCheckAsync(0.15m);

            var ex = await act.Should().ThrowAsync<SpendCapExceededException>();
            ex.Which.SpendCapUsd.Should().Be(1.00m);
            ex.Which.AttemptedCostUsd.Should().Be(0.15m);
            ex.Which.CurrentSpendUsd.Should().Be(0.90m);
        }
    }
}
