using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    [Fact]
    public Task BatchStateChangePinsTheCheckedClientsAndRetainsPartialResultsAfterPanic() => OnUi(async () =>
    {
        var monitor = new Monitor { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var vm = await Ready(monitor);
        vm.SelectClientPage.Execute(null); vm.MinimizeCheckedClients.Execute(null);
        try
        {
            await monitor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            vm.Sessions[0].IsChecked = false; vm.Sessions[1].IsChecked = false;
            vm.RestoreCheckedClients.Execute(null); await vm.RestoreCheckedClients.Execution;
            Assert.Equal(1, monitor.StateChanges); Assert.Equal(2, monitor.Requested.Count);
            Assert.Equal(ClientWindowAction.Minimize, monitor.StateAction); vm.Panic.Execute(null);
            Assert.True(monitor.OperationToken.IsCancellationRequested);
        }
        finally
        {
            monitor.Pending.TrySetResult(new([
                new(monitor.Requested[0], null, ClientWindowPlacementStatus.Applied, "Synthetic minimized"),
                new(monitor.Requested[1], null, ClientWindowPlacementStatus.Cancelled, "Synthetic cancelled")], true, "Synthetic stopped"));
        }
        await vm.MinimizeCheckedClients.Execution;
        Assert.Equal("State confirmed", vm.ClientPlacementResults[0].Outcome);
        Assert.Equal("Cancelled", vm.ClientPlacementResults[1].Outcome);
        Assert.Contains("window states", vm.ClientWindowStatus); Assert.Contains("Panic stop", vm.Notice);
        Assert.False(vm.IsArrangingClients);
    });

    [Fact]
    public Task SingleSelectedFallbackAndLockedControlsDoNotNeedAMonitorSelection() => OnUi(async () =>
    {
        var monitor = new Monitor(); await using var vm = await Ready(monitor);
        vm.SelectedClientDisplay = null; vm.SelectedSession = vm.Sessions[1];
        Assert.True(vm.CanChangeClientStates); Assert.False(vm.CanArrangeClients);
        vm.RestoreCheckedClients.Execute(null); await vm.RestoreCheckedClients.Execution;
        Assert.Equal(102, Assert.Single(monitor.Requested).ProcessId); Assert.Equal(ClientWindowAction.Restore, monitor.StateAction);
        Assert.Equal("State confirmed", Assert.Single(vm.ClientPlacementResults).Outcome);
        vm.LockDashboard(); Assert.False(vm.CanChangeClientStates);
        vm.MinimizeCheckedClients.Execute(null); await vm.MinimizeCheckedClients.Execution;
        Assert.Equal(1, monitor.StateChanges);
    });

    [Fact]
    public Task BatchOnlyProviderDoesNotAdvertiseDisplayRefresh() => OnUi(async () =>
    {
        var source = new Monitor();
        await using var vm = new Zenith.Sessions.App.ShellViewModel(_root, false, System.Windows.Threading.Dispatcher.CurrentDispatcher, new BatchOnly(source));
        await vm.InitializeAsync(); vm.Refresh.Execute(null); await vm.Refresh.Execution;
        vm.SelectedSession = vm.Sessions[0];
        Assert.True(vm.CanManageClientWindows); Assert.True(vm.CanChangeClientStates);
        Assert.False(vm.CanRefreshClientDisplays); Assert.False(vm.CanArrangeClients);
        vm.MinimizeCheckedClients.Execute(null); await vm.MinimizeCheckedClients.Execution;
        Assert.Equal(1, source.StateChanges); Assert.Empty(vm.ClientDisplays);
    });

    private sealed class BatchOnly(Monitor source) : IDesktopMonitor, IClientWindowBatchControl
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct) => source.SampleAsync(ct);
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => source.FocusAsync(session, ct);
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => source.RequestCloseAsync(session, ct);
        public Task<WindowActionResult> ChangeAsync(SessionObservation session, ClientWindowAction action, CancellationToken ct) => source.ChangeAsync(session, action, ct);
        public Task<ClientWindowArrangementResult> ChangeManyAsync(IReadOnlyList<SessionObservation> sessions, ClientWindowAction action, CancellationToken ct) => source.ChangeManyAsync(sessions, action, ct);
        public void CancelWindowOperations() => source.CancelWindowOperations();
    }
}
