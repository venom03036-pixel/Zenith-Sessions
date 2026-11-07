using FluentAssertions;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Usage;
using Xunit;

namespace MacroSuite.Core.Tests.Usage;

public sealed class UsageTrackerTests
{
    private sealed class InMemoryUsageRepository : IUsageRepository
    {
        private readonly List<UsageRecord> _records = new();

        public Task RecordAsync(UsageRecord record, CancellationToken ct = default)
        {
            _records.Add(record);
            return Task.CompletedTask;
        }

        public Task<decimal> GetTotalEstimatedCostAsync(DateTimeOffset since, CancellationToken ct = default)
        {
            decimal sum = _records
                .Where(r => r.TimestampUtc >= since)
                .Sum(r => r.EstimatedCostUsd);
            return Task.FromResult(sum);
        }

        public Task<IReadOnlyList<UsageRecord>> GetRecentAsync(int count, CancellationToken ct = default)
        {
            IReadOnlyList<UsageRecord> recent = _records
                .OrderByDescending(r => r.TimestampUtc)
                .Take(count)
                .ToList();
            return Task.FromResult(recent);
        }

        public Task<UsageTotals> GetTotalsAsync(DateTimeOffset since, CancellationToken ct = default)
        {
            var slice = _records.Where(r => r.TimestampUtc >= since).ToList();
            return Task.FromResult(new UsageTotals(
                slice.Sum(r => r.EstimatedCostUsd),
                slice.Sum(r => r.InputTokens),
                slice.Sum(r => r.OutputTokens),
                slice.Count));
        }

        public void Dispose() { }
    }

    private static (UsageTracker Tracker, InMemoryUsageRepository Repo) CreateTracker(
        decimal? monthlyCap = null,
        decimal? dailyCap = null,
        bool isUsageTrackingLicensed = true)
    {
        var repo = new InMemoryUsageRepository();
        var license = new LicenseInfo
        {
            LicenseKey = "TEST-KEY",
            Type = LicenseType.Enterprise,
            LicensedFeatures = isUsageTrackingLicensed
                ? new HashSet<FeatureId> { FeatureId.UsageTracking }
                : new HashSet<FeatureId>()
        };

        var licenseService = new MockLicenseService(license);
        var gate = new FeatureGate(licenseService);
        var tracker = new UsageTracker(repo, gate, monthlyCap, dailyCap);
        return (tracker, repo);
    }

    private sealed class MockLicenseService : ILicenseService
    {
        private readonly LicenseInfo _license;
        public event EventHandler<LicenseChangedEventArgs>? LicenseChanged { add { } remove { } }

        public MockLicenseService(LicenseInfo license) => _license = license;
        public LicenseInfo GetCurrentLicense() => _license;
        public Task<LicenseValidationResult> ValidateAsync(CancellationToken ct = default) =>
            Task.FromResult(LicenseValidationResult.Success(_license));
        public bool IsFeatureLicensed(FeatureId feature) => _license.IsFeatureLicensed(feature);
    }

    [Fact]
    public async Task PreflightCheckAsync_WithinCap_Succeeds()
    {
        var (tracker, _) = CreateTracker(monthlyCap: 10.0m, dailyCap: 2.0m);

        var act = () => tracker.PreflightCheckAsync(0.50m);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PreflightCheckAsync_ExceedsMonthlyCap_ThrowsSpendCapExceededException()
    {
        var (tracker, repo) = CreateTracker(monthlyCap: 5.0m);

        // Record $4.00 already spent this month
        await repo.RecordAsync(new UsageRecord
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            EstimatedCostUsd = 4.00m,
            Feature = FeatureId.AIVision
        });

        // Proposed cost: $1.50 -> Total: $5.50 > $5.00
        var act = () => tracker.PreflightCheckAsync(1.50m);
        var ex = await act.Should().ThrowAsync<SpendCapExceededException>();
        ex.Which.CurrentSpendUsd.Should().Be(4.00m);
        ex.Which.AttemptedCostUsd.Should().Be(1.50m);
        ex.Which.SpendCapUsd.Should().Be(5.00m);
    }

    [Fact]
    public async Task PreflightCheckAsync_ExceedsDailyCap_ThrowsSpendCapExceededException()
    {
        var (tracker, repo) = CreateTracker(dailyCap: 1.00m);

        await repo.RecordAsync(new UsageRecord
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            EstimatedCostUsd = 0.80m,
            Feature = FeatureId.AIVision
        });

        // Proposed cost: $0.30 -> Total: $1.10 > $1.00
        var act = () => tracker.PreflightCheckAsync(0.30m);
        await act.Should().ThrowAsync<SpendCapExceededException>();
    }

    [Fact]
    public async Task PreflightCheckAsync_WhenFeatureNotLicensed_ThrowsFeatureNotLicensedException()
    {
        var (tracker, _) = CreateTracker(isUsageTrackingLicensed: false);

        var act = () => tracker.PreflightCheckAsync(0.10m);
        await act.Should().ThrowAsync<FeatureNotLicensedException>();
    }

    [Fact]
    public async Task ReserveAsync_AppliesEstimateBuffer()
    {
        var (tracker, _) = CreateTracker(monthlyCap: 10.0m, dailyCap: 2.0m);
        UsageReservation reservation = await tracker.ReserveAsync(0.50m);
        reservation.EstimatedCostUsd.Should().Be(0.5750m);
        reservation.IsCommitted.Should().BeFalse();
    }

    [Fact]
    public async Task CommitAsync_RecordsActualAndMarksReservationCommitted()
    {
        var (tracker, _) = CreateTracker(monthlyCap: 10.0m, dailyCap: 5.0m);
        UsageReservation reservation = await tracker.ReserveAsync(0.05m);
        var record = new UsageRecord
        {
            EstimatedCostUsd = 0.12m,
            Feature = FeatureId.AIVision
        };

        await tracker.CommitAsync(reservation, record);

        reservation.IsCommitted.Should().BeTrue();
        (await tracker.GetTotalSpendAsync(DateTimeOffset.UtcNow.AddMinutes(-1))).Should().Be(0.12m);
    }

    [Fact]
    public async Task ThrowIfCapsExceededAsync_AfterActualOvershoot_Throws()
    {
        var (tracker, _) = CreateTracker(dailyCap: 1.00m);
        await tracker.RecordAsync(new UsageRecord
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            EstimatedCostUsd = 1.20m,
            Feature = FeatureId.AIVision
        });

        var act = () => tracker.ThrowIfCapsExceededAsync();
        await act.Should().ThrowAsync<SpendCapExceededException>()
            .WithMessage("*Daily burst*");
    }

    [Fact]
    public async Task PreflightCheckAsync_DailyBurstLeavesMonthlyHeadroomUntilCeiling()
    {
        var (tracker, repo) = CreateTracker(monthlyCap: 50.0m, dailyCap: 5.0m);
        await repo.RecordAsync(new UsageRecord
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            EstimatedCostUsd = 4.80m,
            Feature = FeatureId.AIVision
        });

        await tracker.Awaiting(t => t.PreflightCheckAsync(0.30m)).Should().ThrowAsync<SpendCapExceededException>()
            .WithMessage("*Daily burst*");
        await tracker.Awaiting(t => t.PreflightCheckAsync(0.10m)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task RecordAsync_And_GetRecentAsync_PersistsRecordsCorrectly()
    {
        var (tracker, _) = CreateTracker();

        var record1 = new UsageRecord
        {
            ProviderId = "anthropic",
            ModelId = "claude-3-5-sonnet",
            InputTokens = 1200,
            OutputTokens = 300,
            EstimatedCostUsd = 0.0081m,
            Feature = FeatureId.AIVision
        };

        var record2 = new UsageRecord
        {
            ProviderId = "openai",
            ModelId = "gpt-4o",
            InputTokens = 800,
            OutputTokens = 150,
            EstimatedCostUsd = 0.0055m,
            Feature = FeatureId.AIVision
        };

        await tracker.RecordAsync(record1);
        await tracker.RecordAsync(record2);

        var recent = await tracker.GetRecentAsync(10);
        recent.Should().HaveCount(2);

        var total = await tracker.GetTotalSpendAsync(DateTimeOffset.UtcNow.AddMinutes(-10));
        total.Should().Be(0.0081m + 0.0055m);
    }

    [Fact]
    public async Task RecordAsync_RaisesUsageRecorded()
    {
        var (tracker, _) = CreateTracker();
        UsageRecord? observed = null;
        tracker.UsageRecorded += r => observed = r;

        var record = new UsageRecord
        {
            ProviderId = "gemini",
            ModelId = "gemini-2.5-flash",
            InputTokens = 40,
            OutputTokens = 8,
            EstimatedCostUsd = 0.0001m,
            Feature = FeatureId.AIVision
        };

        await tracker.RecordAsync(record);

        observed.Should().BeSameAs(record);
    }
}
