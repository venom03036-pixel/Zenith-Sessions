using FluentAssertions;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Usage;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class UsageDashboardViewModelTests
{
    private sealed class MockUsageRepository : IUsageRepository
    {
        public List<UsageRecord> Records { get; } = new();

        public Task RecordAsync(UsageRecord record, CancellationToken ct = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<decimal> GetTotalEstimatedCostAsync(DateTimeOffset since, CancellationToken ct = default)
        {
            var sum = Records.Where(r => r.TimestampUtc >= since).Sum(r => r.EstimatedCostUsd);
            return Task.FromResult(sum);
        }

        public Task<IReadOnlyList<UsageRecord>> GetRecentAsync(int count, CancellationToken ct = default)
        {
            var list = Records.OrderByDescending(r => r.TimestampUtc).Take(count).ToList();
            return Task.FromResult<IReadOnlyList<UsageRecord>>(list);
        }

        public Task<UsageTotals> GetTotalsAsync(DateTimeOffset since, CancellationToken ct = default)
        {
            var slice = Records.Where(r => r.TimestampUtc >= since).ToList();
            return Task.FromResult(new UsageTotals(
                slice.Sum(r => r.EstimatedCostUsd),
                slice.Sum(r => r.InputTokens),
                slice.Sum(r => r.OutputTokens),
                slice.Count));
        }

        public void Dispose() { }
    }

    private sealed class PermissiveFeatureGate : IFeatureGate
    {
        public bool IsAvailable(FeatureId feature) => true;
        public void Require(FeatureId feature) { }
    }

    [Fact]
    public async Task RefreshData_CalculatesTotalsAndPopulatesRecords()
    {
        var repo = new MockUsageRepository();
        repo.Records.Add(new UsageRecord
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            ProviderId = "openai",
            ModelId = "gpt-4o",
            InputTokens = 200,
            OutputTokens = 50,
            EstimatedCostUsd = 0.05m
        });
        repo.Records.Add(new UsageRecord
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            ProviderId = "anthropic",
            ModelId = "claude-3-5-sonnet",
            InputTokens = 400,
            OutputTokens = 100,
            EstimatedCostUsd = 0.12m
        });

        var tracker = new UsageTracker(repo, new PermissiveFeatureGate())
        {
            DailySpendCapUsd = 10.00m,
            MonthlySpendCapUsd = 100.00m
        };

        using var vm = new UsageDashboardViewModel(tracker, repo);
        await vm.RefreshDataAsync();

        vm.DailySpendCap.Should().Be(10.00m);
        vm.MonthlySpendCap.Should().Be(100.00m);
        vm.TotalSpentToday.Should().Be(0.17m);
        vm.TotalSpentMonth.Should().Be(0.17m);
        vm.TokensInToday.Should().Be(600);
        vm.TokensOutToday.Should().Be(150);
        vm.RequestsToday.Should().Be(2);
        vm.RemainingDailyBudget.Should().Be(10.00m - 0.17m);
        vm.RecentRecords.Should().HaveCount(2);
        vm.HasRecords.Should().BeTrue();
    }

    [Fact]
    public void SaveCaps_UpdatesUsageTracker()
    {
        var repo = new MockUsageRepository();
        var tracker = new UsageTracker(repo, new PermissiveFeatureGate())
        {
            DailySpendCapUsd = 5.00m,
            MonthlySpendCapUsd = 50.00m
        };

        using var vm = new UsageDashboardViewModel(tracker, repo);
        vm.DailySpendCap = 15.50m;
        vm.MonthlySpendCap = 150.00m;

        vm.SaveCapsCommand.Execute(null);

        tracker.DailySpendCapUsd.Should().Be(15.50m);
        tracker.MonthlySpendCapUsd.Should().Be(150.00m);
        vm.StatusMessage.Should().Contain("saved");
    }

    [Fact]
    public async Task UnappliedLimitEdits_DoNotChangeTheActiveRemainingAllowance()
    {
        var repo = new MockUsageRepository();
        var tracker = new UsageTracker(repo, new PermissiveFeatureGate()) { DailySpendCapUsd = 5m, MonthlySpendCapUsd = 50m };
        using var vm = new UsageDashboardViewModel(tracker, repo, (_, _) => false);
        await vm.RefreshDataAsync();
        vm.DailySpendCap = 100m;
        vm.MonthlySpendCap = 1000m;
        vm.RemainingDailyBudget.Should().Be(5m);
        vm.RemainingMonthlyBudget.Should().Be(50m);
        vm.SaveCapsCommand.Execute(null);
        vm.RemainingDailyBudget.Should().Be(5m);
        tracker.DailySpendCapUsd.Should().Be(5m);
        vm.StatusMessage.Should().Contain("previous limits are still active");
    }
}
