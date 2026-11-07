using FluentAssertions;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.Usage;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public partial class UsageDashboardViewModelTests
{
    [Fact]
    public async Task Dispose_CancelsAndJoinsRefresh_AndUnsubscribesUsageEvents()
    {
        var repo = new PendingUsageRepository();
        var tracker = new UsageTracker(repo, new PermissiveFeatureGate());
        var vm = WithoutContext(() => new UsageDashboardViewModel(tracker, repo));
        await repo.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        vm.Dispose();

        repo.Cancelled.Should().BeTrue();
        int queriesAfterDispose = repo.Queries;
        await tracker.RecordAsync(new UsageRecord());
        await vm.RefreshDataAsync();
        repo.Queries.Should().Be(queriesAfterDispose);
        vm.RecentRecords.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshFailure_IsReported_WithoutFaultingBackgroundTask()
    {
        var repo = new PendingUsageRepository { FailImmediately = true };
        using var vm = WithoutContext(() => new UsageDashboardViewModel(new UsageTracker(repo, new PermissiveFeatureGate()), repo));

        await vm.RefreshDataAsync().WaitAsync(TimeSpan.FromSeconds(5));

        vm.StatusMessage.Should().Contain("Could not refresh usage history").And.Contain("test storage unavailable");
    }

    [Fact]
    public void SaveCaps_PersistenceFailureKeepsPreviousLimits_AndDoesNotClaimSuccess()
    {
        var repo = new MockUsageRepository();
        var tracker = new UsageTracker(repo, new PermissiveFeatureGate()) { DailySpendCapUsd = 5m, MonthlySpendCapUsd = 50m };
        using var vm = WithoutContext(() => new UsageDashboardViewModel(tracker, repo, (_, _) => false));
        vm.DailySpendCap = 20m;
        vm.MonthlySpendCap = 200m;

        vm.SaveCapsCommand.Execute(null);

        tracker.DailySpendCapUsd.Should().Be(5m);
        tracker.MonthlySpendCapUsd.Should().Be(50m);
        vm.StatusMessage.Should().StartWith("Could not save budgets");
    }

    [Fact]
    public async Task Dispose_DoesNotWaitForQueuedUiUpdate_AndQueuedCallbackCannotMutateState()
    {
        var context = new QueuedUsageContext();
        var repo = new MockUsageRepository();
        repo.Records.Add(new UsageRecord { EstimatedCostUsd = 1m, TimestampUtc = DateTimeOffset.UtcNow });
        SynchronizationContext? previous = SynchronizationContext.Current;
        UsageDashboardViewModel vm;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            vm = new UsageDashboardViewModel(new UsageTracker(repo, new PermissiveFeatureGate()), repo);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await context.Posted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        vm.Dispose();
        context.RunQueued();

        vm.RecentRecords.Should().BeEmpty();
        vm.TotalSpentToday.Should().Be(0m);
    }

    private static T WithoutContext<T>(Func<T> create)
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            return create();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class QueuedUsageContext : SynchronizationContext
    {
        private SendOrPostCallback? _callback;
        private object? _state;
        public TaskCompletionSource Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Post(SendOrPostCallback d, object? state)
        {
            _callback = d;
            _state = state;
            Posted.TrySetResult();
        }
        public void RunQueued() => _callback?.Invoke(_state);
    }

    private sealed class PendingUsageRepository : IUsageRepository
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public bool FailImmediately { get; init; }
        public int Queries { get; private set; }
        public Task RecordAsync(UsageRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<decimal> GetTotalEstimatedCostAsync(DateTimeOffset since, CancellationToken ct = default) => Task.FromResult(0m);
        public Task<IReadOnlyList<UsageRecord>> GetRecentAsync(int count, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UsageRecord>>(Array.Empty<UsageRecord>());
        public async Task<UsageTotals> GetTotalsAsync(DateTimeOffset since, CancellationToken ct = default)
        {
            Queries++;
            Started.TrySetResult();
            if (FailImmediately) throw new IOException("test storage unavailable");
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Cancelled = true;
                throw;
            }
            return new UsageTotals(0m, 0, 0, 0);
        }
        public void Dispose() { }
    }
}
