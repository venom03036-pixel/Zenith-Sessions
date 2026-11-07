using System.IO;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-window-ui-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public Task SelectionDisplayAndOptionsArePinnedUntilTheOperationEnds() => OnUi(async () =>
    {
        var monitor = new Monitor { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var vm = await Ready(monitor);
        vm.Sessions[0].IsChecked = true;
        vm.ArrangeClients.Execute(null);
        try
        {
            await monitor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            vm.Sessions[0].IsChecked = false; vm.Sessions[1].IsChecked = true;
            vm.SelectedClientDisplay = vm.ClientDisplays[1]; vm.TileMinimumWidth = "480";
            Assert.Equal(101, Assert.Single(monitor.Requested).ProcessId);
            Assert.Equal("fixture-display-1", monitor.RequestedDisplay);
            Assert.Equal(320, monitor.RequestedOptions!.MinimumWidthDip);
        }
        finally { monitor.Pending.TrySetResult(monitor.Result()); }
        await vm.ArrangeClients.Execution;
        Assert.Equal("PID 101", Assert.Single(vm.ClientPlacementResults).Client);
        Assert.Equal("1 client checked", vm.CheckedClientCount);
    });

    [Fact]
    public Task PanicCancelsAndPreservesThePinnedPartialReportWithoutReplacingThePanicNotice() => OnUi(async () =>
    {
        var monitor = new Monitor { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var vm = await Ready(monitor);
        vm.SelectClientPage.Execute(null); vm.ArrangeClients.Execute(null);
        try
        {
            await monitor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            vm.Panic.Execute(null);
            Assert.True(monitor.OperationToken.IsCancellationRequested);
            Assert.True(monitor.Cancellations > 0);
        }
        finally
        {
            monitor.Pending.TrySetResult(new([
                new(monitor.Requested[0], new(0,0,320,240), ClientWindowPlacementStatus.Applied, "Synthetic confirmed before stop"),
                new(monitor.Requested[1], null, ClientWindowPlacementStatus.Cancelled, "Synthetic not dispatched")], true, "Synthetic stopped"));
        }
        await vm.ArrangeClients.Execution;
        Assert.Equal(2, vm.ClientPlacementResults.Count);
        Assert.Equal("Position confirmed", vm.ClientPlacementResults[0].Outcome);
        Assert.Equal("Cancelled", vm.ClientPlacementResults[1].Outcome);
        Assert.Contains("Panic stop", vm.Notice);
        Assert.Contains("Stopped operation report", vm.ClientWindowStatus);
        Assert.False(vm.IsArrangingClients); Assert.Equal(1, monitor.Arrangements);
    });

    [Fact]
    public Task LayoutValidationAndSettingsPersistWithoutLeakingLaterDraftsIntoThemeAutosave() => OnUi(async () =>
    {
        var monitor = new Monitor();
        await using (var vm = await Ready(monitor))
        {
            vm.Sessions[0].IsChecked = true; vm.TileGap = "-1";
            vm.ArrangeClients.Execute(null); await vm.ArrangeClients.Execution;
            Assert.Equal(0, monitor.Arrangements); Assert.Contains("Gap", vm.ClientWindowStatus);
            vm.TileGap = "12"; vm.TileColumns = "2"; vm.TileMinimumWidth = "480";
            vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
            Assert.Equal("Settings saved", vm.SaveStatus);
            vm.TileGap = "13"; vm.ApplyTheme.Execute("ocean-blue"); await vm.ApplyTheme.Execution;
            using var store = new SettingsStore(_root);
            var saved = (await store.LoadAsync(default)).Settings;
            Assert.Equal(12, saved.WindowLayout!.Tile!.GapDip);
            Assert.Equal(2, saved.WindowLayout.Tile.Columns); Assert.Equal(480, saved.WindowLayout.Tile.MinimumWidthDip);
            Assert.Contains("Unsaved", vm.SaveStatus);
            vm.TileGap = "12"; Assert.Equal("Settings saved", vm.SaveStatus);
        }
        await using var reopened = await Ready(new Monitor());
        Assert.Equal("12", reopened.TileGap); Assert.Equal("2", reopened.TileColumns); Assert.Equal("480", reopened.TileMinimumWidth);
    });

    [Fact]
    public Task InvalidTileDraftIsReportedInlineWithoutRecordingAnOperationFailure() => OnUi(async () =>
    {
        var monitor = new Monitor();
        await using var vm = await Ready(monitor);
        vm.Sessions[0].IsChecked = true; vm.TileGap = "70";
        vm.ArrangeClients.Execute(null); await vm.ArrangeClients.Execution;
        Assert.Equal(0, monitor.Arrangements);
        Assert.Contains("Gap must be between 0 and 64", vm.ClientWindowStatus);
        Assert.DoesNotContain("could not finish", vm.ClientWindowStatus);
        Assert.DoesNotContain(vm.Events, e => e.Contains("Operation failed"));
        vm.TileGap = "8"; vm.TileColumns = "abc";
        vm.ArrangeClients.Execute(null); await vm.ArrangeClients.Execution;
        Assert.Equal(0, monitor.Arrangements);
        Assert.Contains("Enter a whole number for columns", vm.ClientWindowStatus);
        Assert.DoesNotContain(vm.Events, e => e.Contains("Operation failed"));
    });

    [Fact]
    public Task MonitorListLoadsAtStartupAndRestoresTheSavedDisplayWithoutAManualRefresh() => OnUi(async () =>
    {
        await using (var vm = await OpenedWithoutDisplayRefresh(new Monitor()))
        {
            await vm.RefreshClientDisplays.Execution;
            Assert.Equal(2, vm.ClientDisplays.Count);
            Assert.Equal("fixture-display-1", vm.SelectedClientDisplay?.Display.Id);
            vm.Sessions[0].IsChecked = true;
            Assert.True(vm.CanArrangeClients);
            vm.SelectedClientDisplay = vm.ClientDisplays[1];
            vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
            Assert.Equal("Settings saved", vm.SaveStatus);
        }
        await using var reopened = await OpenedWithoutDisplayRefresh(new Monitor());
        await reopened.RefreshClientDisplays.Execution;
        Assert.Equal("fixture-display-2", reopened.SelectedClientDisplay?.Display.Id);
        Assert.Equal("Settings saved", reopened.SaveStatus); // Restoring the saved monitor is not an edit.
    });

    [Fact]
    public Task MonitorListLoadsOnUnlockWhenStartupFinishedWhileLocked() => OnUi(async () =>
    {
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor());
        await using (vm)
        {
            vm.LockDashboard();
            await vm.InitializeAsync(); await vm.RefreshClientDisplays.Execution;
            Assert.Empty(vm.ClientDisplays);
            vm.Unlock.Execute(null); await vm.RefreshClientDisplays.Execution;
            Assert.Equal(2, vm.ClientDisplays.Count);
            Assert.Equal("fixture-display-1", vm.SelectedClientDisplay?.Display.Id);
        }
    });

    [Fact]
    public Task ReusedPidDoesNotInheritCheckboxesOrAnOldSelectedWindow() => OnUi(async () =>
    {
        var monitor = new Monitor(); await using var vm = await Ready(monitor);
        vm.Sessions[0].IsChecked = true; vm.SelectedSession = vm.Sessions[0];
        var old = vm.Sessions[0];
        monitor.Processes = [monitor.Processes[0] with { ProcessStartUtcTicks = DateTimeOffset.UtcNow.UtcTicks, ObservedUtc = DateTimeOffset.UtcNow }];
        vm.Refresh.Execute(null); await vm.Refresh.Execution;
        var current = Assert.Single(vm.Sessions);
        Assert.NotSame(old, current); Assert.False(current.IsChecked); Assert.Null(vm.SelectedSession);
        vm.ArrangeClients.Execute(null); await vm.ArrangeClients.Execution;
        Assert.Equal(0, monitor.Arrangements);
    });

    [Fact]
    public Task IncompleteAndDuplicateProviderResultsCannotBeDisplayedAsSuccessful() => OnUi(async () =>
    {
        var monitor = new Monitor { DuplicateResults = true }; await using var vm = await Ready(monitor);
        vm.SelectClientPage.Execute(null); vm.ArrangeClients.Execute(null); await vm.ArrangeClients.Execution;
        Assert.Empty(vm.ClientPlacementResults); Assert.Contains("could not finish", vm.ClientWindowStatus);
    });

    [Fact]
    public Task UnlockWaitsForCancelledFavoritesReadAndThenLoadsTheSavedList() => OnUi(async () =>
    {
        var store = new Favorites(); var monitor = new Monitor();
        await using var vm = await Ready(monitor, store);
        Assert.Single(vm.DestinationFavorites.Favorites);
        store.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.DestinationFavorites.Refresh.Execute(null);
        try
        {
            await store.Gated.Task.WaitAsync(TimeSpan.FromSeconds(2));
            vm.LockDashboard(); vm.Unlock.Execute(null);
            Assert.Empty(vm.DestinationFavorites.Favorites);
        }
        finally { store.Gate.TrySetResult(store.Snapshot); }
        await vm.DestinationFavorites.Refresh.Execution;
        await store.Reloaded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Single(vm.DestinationFavorites.Favorites);
        Assert.Equal(3, store.Loads);
        Assert.False(vm.IsLocked);
    });

    [Fact]
    public Task GamePageLinkIsOnlyTheRobloxGamePage() => OnUi(async () =>
    {
        const string job="0f000000-0000-0000-0000-000000000009";
        await using var vm=await Ready(new Monitor());
        Assert.Null(vm.BuildRequestedGamePageLink());
        vm.ServerBrowser.UsePreferredPlace(16732694052);Assert.Equal("https://www.roblox.com/games/16732694052",vm.BuildRequestedGamePageLink());
        vm.ServerBrowser.LinkInput=$"https://www.roblox.com/games/start?placeId=16732694052&gameInstanceId={job}";
        Assert.Null(vm.BuildRequestedGamePageLink()); // A pending draft is neither used nor linked.
        vm.ServerBrowser.ResolveInput();Assert.Equal(job,vm.ServerBrowser.Destination!.JobId);
        Assert.Equal("https://www.roblox.com/games/16732694052",vm.BuildRequestedGamePageLink()); // The server job never reaches the public page link.
        Assert.Contains(job,vm.BuildRequestedDestinationLink());
        vm.LockDashboard();Assert.Null(vm.BuildRequestedGamePageLink());Assert.Empty(vm.Launches);
    });

    [Fact]
    public Task FavoriteTargetsSurviveRestartAndCopyingRemainsExplicitRequestedOnly() => OnUi(async () =>
    {
        const string job="0f000000-0000-0000-0000-000000000009";
        await using(var vm=await Ready(new Monitor()))
        {
            vm.ServerBrowser.LinkInput=$"https://www.roblox.com/games/start?placeId=6564161046&gameInstanceId={job}";
            vm.ServerBrowser.ResolveInput();
            await vm.DestinationFavorites.SaveCurrentDestinationAsync("Coloseum request");
            Assert.Single(vm.DestinationFavorites.Favorites);
            Assert.Contains(job,vm.BuildRequestedDestinationLink());
            vm.LockDashboard();Assert.Null(vm.BuildRequestedDestinationLink());
        }
        await using(var reopened=await Ready(new Monitor()))
        {
            Assert.Null(reopened.BuildRequestedDestinationLink());
            reopened.DestinationFavorites.SelectedFavorite=Assert.Single(reopened.DestinationFavorites.Favorites);
            reopened.DestinationFavorites.UseSelectedFavorite();
            Assert.Equal(job,reopened.ServerBrowser.Destination!.JobId);
            Assert.Equal(RobloxServerKind.Unknown,reopened.ServerBrowser.Destination.ServerKind);
            Assert.Contains("placeId=6564161046",reopened.BuildRequestedDestinationLink());
            Assert.Empty(reopened.Launches);
        }
    });

    private async Task<ShellViewModel> Ready(Monitor monitor, IFavoriteDestinationStore? store = null)
    {
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, monitor, favoriteStore: store);
        await vm.InitializeAsync(); vm.Refresh.Execute(null); await vm.Refresh.Execution;
        vm.RefreshClientDisplays.Execute(null); await vm.RefreshClientDisplays.Execution;
        return vm;
    }
    private async Task<ShellViewModel> OpenedWithoutDisplayRefresh(Monitor monitor)
    {
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, monitor);
        await vm.InitializeAsync(); vm.Refresh.Execute(null); await vm.Refresh.Execution;
        return vm;
    }
    private sealed class Favorites : IFavoriteDestinationStore
    {
        public int Loads;
        public TaskCompletionSource<FavoriteDestinationSnapshot>? Gate;
        public TaskCompletionSource Gated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Reloaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FavoriteDestinationSnapshot Snapshot { get; } = new(1, [new(Guid.NewGuid(), "Synthetic destination", 6564161046, Revision: 1, ChangeId: Guid.NewGuid())]);
        public Task<FavoriteDestinationSnapshot> LoadAsync(CancellationToken ct)
        {
            Loads++;
            if (Loads == 2 && Gate is not null) { Gated.TrySetResult(); return Gate.Task; }
            if (Loads >= 3) Reloaded.TrySetResult();
            return Task.FromResult(Snapshot);
        }
        public Task<FavoriteDestination> SaveAsync(FavoriteDestination item, CancellationToken ct) => throw new InvalidOperationException("Unexpected synthetic save");
        public Task DeleteAsync(Guid id, long revision, Guid changeId, CancellationToken ct) => throw new InvalidOperationException("Unexpected synthetic delete");
    }
    private sealed class Monitor : IDesktopMonitor, IClientWindowArranger, IClientWindowBatchControl
    {
        public SessionObservation[] Processes = [Observation(101), Observation(102)];
        public IReadOnlyList<SessionObservation> Requested = [];
        public string? RequestedDisplay;
        public ClientWindowTileOptions? RequestedOptions;
        public CancellationToken OperationToken;
        public int Arrangements, Cancellations;
        public int StateChanges;
        public ClientWindowAction? StateAction;
        public bool DuplicateResults;
        public TaskCompletionSource<ClientWindowArrangementResult>? Pending;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static SessionObservation Observation(int pid) => new(pid, DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks,
            "Synthetic", pid, "", false, true, 1, 1, null, DateTimeOffset.UtcNow);
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct) => Task.FromResult(new DesktopSnapshot(
            new(DateTimeOffset.UtcNow, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 10), Processes));
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
        public Task<WindowActionResult> ChangeAsync(SessionObservation session, ClientWindowAction action, CancellationToken ct) => throw new InvalidOperationException("Unexpected single operation");
        public Task<ClientWindowArrangementResult> ChangeManyAsync(IReadOnlyList<SessionObservation> sessions, ClientWindowAction action, CancellationToken ct)
        {
            StateChanges++; Requested = sessions; StateAction = action; OperationToken = ct; Started.TrySetResult();
            return Pending?.Task ?? Task.FromResult(new ClientWindowArrangementResult(sessions.Select(session => new ClientWindowPlacementResult(session, null, ClientWindowPlacementStatus.Applied, "Synthetic state confirmed")).ToArray(), false, "Synthetic state operation complete"));
        }
        public Task<ClientDisplayList> GetDisplaysAsync(CancellationToken ct) => Task.FromResult(new ClientDisplayList([
            new("fixture-display-1", "Synthetic display 1", new(-2000, 0, 4000, 2400), 125, true),
            new("fixture-display-2", "Synthetic display 2", new(2000, -1000, 3000, 2000), 150, false)], "Synthetic displays only"));
        public Task<ClientWindowArrangementResult> ArrangeAsync(IReadOnlyList<SessionObservation> sessions, string displayId, ClientWindowTileOptions options, CancellationToken ct)
        {
            Arrangements++; Requested = sessions; RequestedDisplay = displayId; RequestedOptions = options; OperationToken = ct;
            Started.TrySetResult(); return Pending?.Task ?? Task.FromResult(Result());
        }
        public ClientWindowArrangementResult Result() => new(Requested.Select((process, index) => new ClientWindowPlacementResult(
            DuplicateResults ? Requested[0] : process, new(index*400,0,320,240), ClientWindowPlacementStatus.Applied, "Synthetic result only")).ToArray(), false, "Synthetic completed");
        public void CancelWindowOperations() => Cancellations++;
    }
    private static Task OnUi(Func<Task> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; Task operation = Task.CompletedTask;
            dispatcher.BeginInvoke(() => operation = Run());
            async Task Run()
            {
                try { await work(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
            Dispatcher.Run(); operation.GetAwaiter().GetResult();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-window-ui-tests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
