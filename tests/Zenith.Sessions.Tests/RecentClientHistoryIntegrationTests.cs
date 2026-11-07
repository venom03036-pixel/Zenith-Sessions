using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class DirectoryIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ShellRecordsNormalCloseOnlyWhenTheProviderAcceptedItAndRestoresOnlyEndedHistory(bool closeAccepted) => OnUi(async () =>
    {
        var monitor = new HistoryMonitor(closeAccepted); var observation = monitor.Current.Sessions[0];
        await using (var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, monitor, new PublicDirectory(), new Servers()))
        {
            await vm.InitializeAsync(); await RefreshHistoryFrame(vm);
            Assert.Single(vm.Sessions); Assert.Empty(vm.RecentClients.Rows);
            Assert.Contains("complete scan", vm.ProcessInventoryStatus);
            await vm.RequestCloseAsync(observation); Assert.Equal(1, monitor.CloseCalls);
            monitor.Current = monitor.Current with { Sessions = Array.Empty<SessionObservation>(), ProcessRevision = 2,
                ProcessObservedUtc = DateTimeOffset.UtcNow, ProcessInventoryComplete = false };
            await RefreshHistoryFrame(vm); Assert.Empty(vm.RecentClients.Rows);
            Assert.Contains("incomplete", vm.ProcessInventoryStatus);
            monitor.Current = monitor.Current with { ProcessRevision = 3, ProcessObservedUtc = DateTimeOffset.UtcNow, ProcessInventoryComplete = true };
            await RefreshHistoryFrame(vm); await vm.RecentClients.ActiveSave;
            var ended = Assert.Single(vm.RecentClients.Rows);
            Assert.Equal(closeAccepted, ended.Entry.CloseRequestedUtc.HasValue); Assert.Contains("cause unknown", ended.Outcome);
            Assert.Equal("Unknown", ended.LastReportedDestination); Assert.False(ended.Entry.CanAuthorizeAccountAction);
        }
        await using (var reopened = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), new PublicDirectory(), new Servers()))
        {
            await reopened.InitializeAsync(); var restored = Assert.Single(reopened.RecentClients.Rows);
            Assert.Equal(observation.ProcessStartUtcTicks, restored.Entry.Process.ProcessStartUtcTicks);
            Assert.Empty(reopened.Launches); Assert.Empty(reopened.Sessions);
        }
    });

    [Fact]
    public Task LockedDashboardPreservesMonitoringHistoryAndRejectsClear() => OnUi(async () =>
    {
        var monitor = new HistoryMonitor(true);
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, monitor, new PublicDirectory(), new Servers());
        await vm.InitializeAsync(); await RefreshHistoryFrame(vm); vm.LockDashboard();
        monitor.Current = monitor.Current with { Sessions = Array.Empty<SessionObservation>(), ProcessRevision = 2, ProcessObservedUtc = DateTimeOffset.UtcNow };
        await RefreshHistoryFrame(vm); await vm.RecentClients.ActiveSave;
        Assert.Single(vm.RecentClients.Rows); Assert.False(vm.RecentClients.CanClear);
        vm.RecentClients.Clear.Execute(null); Assert.Single(vm.RecentClients.Rows);
        vm.Unlock.Execute(null); Assert.True(vm.RecentClients.CanClear);
        vm.RecentClients.Clear.Execute(null); await vm.RecentClients.ActiveSave; Assert.Empty(vm.RecentClients.Rows);
    });

    private static async Task RefreshHistoryFrame(ShellViewModel vm)
    {
        vm.Refresh.Execute(null); await vm.Refresh.Execution; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private sealed class HistoryMonitor : IDesktopMonitor
    {
        public DesktopSnapshot Current;
        public int CloseCalls;
        private readonly bool _closeAccepted;
        public HistoryMonitor(bool closeAccepted)
        {
            _closeAccepted = closeAccepted; var now = DateTimeOffset.UtcNow;
            Current = new(new(now, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 20),
                [new(10001, now.AddMinutes(-1).UtcTicks, "Synthetic", 1, "Synthetic", false, true, 1, 1, null, now)],
                ProcessObservedUtc: now, ProcessRevision: 1, ProcessInventoryComplete: true);
        }
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct) => Task.FromResult(Current);
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct)
        { CloseCalls++; return Task.FromResult(_closeAccepted); }
    }
}
