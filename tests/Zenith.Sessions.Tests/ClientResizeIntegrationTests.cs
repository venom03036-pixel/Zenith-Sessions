using System.IO;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class ClientResizeIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-resize-ui-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public Task RecipientsAndPhysicalContentSizeArePinnedAndShareTheWindowOperationOwner() => OnUi(async () =>
    {
        var monitor = new CombinedMonitor { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var vm = await Ready(monitor);
        vm.Sessions[0].IsChecked = true; vm.SelectedSession = vm.Sessions[1];
        vm.ClientContentWidth = "800"; vm.ClientContentHeight = "600";
        vm.ResizeCheckedClients.Execute(null);
        try
        {
            await monitor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            vm.Sessions[0].IsChecked = false; vm.Sessions[1].IsChecked = true;
            vm.ClientContentWidth = "320"; vm.ClientContentHeight = "240";
            Assert.Equal(101, Assert.Single(monitor.Requested).ProcessId);
            Assert.Equal(new ClientContentSize(800, 600), monitor.RequestedSize);
            Assert.False(vm.CanResizeClients); Assert.False(vm.CanEditClientSize);
            Assert.False(vm.CanChangeClientStates);
            vm.MinimizeCheckedClients.Execute(null); await vm.MinimizeCheckedClients.Execution;
            vm.ResizeCheckedClients.Execute(null);
            Assert.Equal(0, monitor.StateChanges); Assert.Equal(1, monitor.Resizes);
        }
        finally { monitor.Pending.TrySetResult(monitor.Result()); }
        await vm.ResizeCheckedClients.Execution;
        var row = Assert.Single(vm.ClientPlacementResults);
        Assert.Equal("PID 101", row.Client); Assert.Equal("Content size confirmed", row.Outcome);
        Assert.True(row.Result.RequestedBounds.HasValue);
        var outer = row.Result.RequestedBounds.GetValueOrDefault(); // Outer frame is not relabelled as content.
        Assert.Equal(816, outer.Width); Assert.Equal(639, outer.Height);
        Assert.True(vm.CanResizeClients); Assert.True(vm.CanEditClientSize);
    });

    [Fact]
    public Task SelectedFallbackAndAvailabilityNotificationsFollowCurrentShellState() => OnUi(async () =>
    {
        var monitor = new ResizeMonitor();
        await using var vm = await Ready(monitor);
        var changes = new List<string?>(); vm.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        Assert.True(vm.CanEditClientSize); Assert.False(vm.CanResizeClients);
        vm.SelectedSession = vm.Sessions[1];
        Assert.True(vm.CanResizeClients); Assert.Contains(nameof(vm.CanResizeClients), changes);
        vm.ResizeCheckedClients.Execute(null); await vm.ResizeCheckedClients.Execution;
        Assert.Equal(102, Assert.Single(monitor.Requested).ProcessId);
        Assert.Equal(new ClientContentSize(640, 480), monitor.RequestedSize);
        vm.SelectedSession = null; Assert.False(vm.CanResizeClients);
        vm.Sessions[0].IsChecked = true; Assert.True(vm.CanResizeClients);
        vm.LockDashboard(); Assert.False(vm.CanEditClientSize); Assert.False(vm.CanResizeClients);
        vm.Unlock.Execute(null); Assert.True(vm.CanEditClientSize); Assert.True(vm.CanResizeClients);
        Assert.Contains(nameof(vm.CanEditClientSize), changes);
    });

    [Fact]
    public Task TinyOnlyFillsTemporaryDraftsAndEvenInvalidDraftsNeverBecomeSavedPreferences() => OnUi(async () =>
    {
        var monitor = new ResizeMonitor();
        await using (var vm = await Ready(monitor))
        {
            Assert.Equal("640", vm.ClientContentWidth); Assert.Equal("480", vm.ClientContentHeight);
            string savedStatus = vm.SaveStatus;
            vm.UseTinyClientSize.Execute(null);
            Assert.Equal("320", vm.ClientContentWidth); Assert.Equal("240", vm.ClientContentHeight);
            Assert.Equal(0, monitor.Resizes); Assert.Equal(savedStatus, vm.SaveStatus);
            vm.ClientContentWidth = "invalid"; vm.ClientContentHeight = "-1";
            Assert.Equal(savedStatus, vm.SaveStatus);
            vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
            Assert.Equal("Settings saved", vm.SaveStatus);
            vm.ApplyTheme.Execute("ocean-blue"); await vm.ApplyTheme.Execution;
            Assert.Equal("invalid", vm.ClientContentWidth); Assert.Equal("-1", vm.ClientContentHeight);
        }
        await using var reopened = await Ready(new ResizeMonitor());
        Assert.Equal("640", reopened.ClientContentWidth); Assert.Equal("480", reopened.ClientContentHeight);
    });

    [Theory]
    [InlineData("159", "480")]
    [InlineData("640", "119")]
    [InlineData("8193", "480")]
    [InlineData("640", "8193")]
    [InlineData("2147483648", "480")]
    [InlineData("640.5", "480")]
    [InlineData("", "480")]
    [InlineData("640", "wrong")]
    public Task InvalidDimensionsNeverCallTheProvider(string width, string height) => OnUi(async () =>
    {
        var monitor = new ResizeMonitor(); await using var vm = await Ready(monitor);
        vm.Sessions[0].IsChecked = true;
        vm.ClientContentWidth = width; vm.ClientContentHeight = height;
        vm.ResizeCheckedClients.Execute(null); await vm.ResizeCheckedClients.Execution;
        Assert.Equal(0, monitor.Resizes); Assert.False(vm.IsArrangingClients);
        Assert.Empty(vm.ClientPlacementResults); Assert.Contains("physical pixels", vm.ClientWindowStatus);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task LockAndPanicCancelTheResizerOnlyProviderAndPreserveTruthfulLatePartialOutcomes(bool locked) => OnUi(async () =>
    {
        var monitor = new ResizeMonitor { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var vm = await Ready(monitor);
        vm.SelectClientPage.Execute(null); vm.ResizeCheckedClients.Execute(null);
        try
        {
            await monitor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (locked) vm.LockDashboard(); else vm.Panic.Execute(null);
            Assert.True(monitor.OperationToken.IsCancellationRequested); Assert.True(monitor.Cancellations > 0);
        }
        finally
        {
            monitor.Pending.TrySetResult(new([
                new(monitor.Requested[0], new(0, 0, 656, 519), ClientWindowPlacementStatus.Applied, "Synthetic content confirmed before stop"),
                new(monitor.Requested[1], null, ClientWindowPlacementStatus.Cancelled, "Synthetic not dispatched")], true, "Synthetic stopped"));
        }
        await vm.ResizeCheckedClients.Execution;
        Assert.Equal(2, vm.ClientPlacementResults.Count);
        Assert.Equal("Content size confirmed", vm.ClientPlacementResults[0].Outcome);
        Assert.Equal("Cancelled", vm.ClientPlacementResults[1].Outcome);
        Assert.Contains("Stopped operation report: 1 of 2 content sizes", vm.ClientWindowStatus);
        Assert.Contains(locked ? "Controls locked" : "Panic stop", vm.Notice);
        Assert.False(vm.IsArrangingClients); Assert.Equal(1, monitor.Resizes);
    });

    [Fact]
    public Task CancelRetainsOwnershipUntilTheActualResizeFinishes() => OnUi(async () =>
    {
        var monitor = new ResizeMonitor { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var vm = await Ready(monitor);
        vm.SelectedSession = vm.Sessions[0]; vm.ResizeCheckedClients.Execute(null);
        try
        {
            await monitor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            vm.CancelClientWindowWork.Execute(null);
            Assert.True(monitor.OperationToken.IsCancellationRequested);
            Assert.True(vm.IsArrangingClients); Assert.False(vm.CanResizeClients);
            Assert.False(vm.ResizeCheckedClients.Execution.IsCompleted);
            vm.ResizeCheckedClients.Execute(null); Assert.Equal(1, monitor.Resizes);
        }
        finally { monitor.Pending.TrySetResult(monitor.Result()); }
        await vm.ResizeCheckedClients.Execution;
        Assert.Contains("Stopped operation report", vm.ClientWindowStatus);
        Assert.True(vm.CanResizeClients);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task LateResizeFailurePreservesTheLockOrPanicNotice(bool locked) => OnUi(async () =>
    {
        var monitor = new ResizeMonitor { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var vm = await Ready(monitor);
        vm.SelectedSession = vm.Sessions[0]; vm.ResizeCheckedClients.Execute(null);
        try
        {
            await monitor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (locked) vm.LockDashboard(); else vm.Panic.Execute(null);
        }
        finally { monitor.Pending.TrySetException(new IOException("Synthetic delayed failure")); }
        await vm.ResizeCheckedClients.Execution;
        Assert.Empty(vm.ClientPlacementResults); Assert.False(vm.IsArrangingClients);
        Assert.Contains("Final client sizes could not be confirmed", vm.ClientWindowStatus);
        Assert.Contains(locked ? "Controls locked" : "Panic stop", vm.Notice);
        Assert.DoesNotContain("Synthetic delayed failure", vm.ClientWindowStatus);
    });

    [Fact]
    public Task StartupSamplesAndUnsupportedProvidersCannotResize() => OnUi(async () =>
    {
        var monitor = new ResizeMonitor();
        await using (var starting = new ShellViewModel(Path.Combine(_root, "startup"), false, Dispatcher.CurrentDispatcher, monitor))
        {
            var changes = new List<string?>(); starting.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
            Assert.False(starting.CanEditClientSize); Assert.False(starting.CanResizeClients);
            starting.ResizeCheckedClients.Execute(null); await starting.ResizeCheckedClients.Execution;
            Assert.Equal(0, monitor.Resizes);
            await starting.InitializeAsync(); starting.Refresh.Execute(null); await starting.Refresh.Execution;
            Assert.True(starting.CanEditClientSize); Assert.Contains(nameof(starting.CanEditClientSize), changes);
        }
        var sampleMonitor = new ResizeMonitor();
        await using (var sample = await Ready(sampleMonitor, sample: true, folder: "sample"))
        {
            sample.SelectedSession = sample.Sessions[0];
            Assert.False(sample.CanManageClientWindows); Assert.False(sample.CanEditClientSize); Assert.False(sample.CanResizeClients);
            sample.UseTinyClientSize.Execute(null); sample.ResizeCheckedClients.Execute(null); await sample.ResizeCheckedClients.Execution;
            Assert.Equal("640", sample.ClientContentWidth); Assert.Equal(0, sampleMonitor.Resizes);
        }
        await using var unsupported = await Ready(new PlainMonitor(), folder: "unsupported");
        unsupported.SelectedSession = unsupported.Sessions[0];
        Assert.False(unsupported.CanEditClientSize); Assert.False(unsupported.CanResizeClients);
        unsupported.ResizeCheckedClients.Execute(null); await unsupported.ResizeCheckedClients.Execution;
        Assert.Contains("supported live provider", unsupported.ClientWindowStatus);
    });

    [Fact]
    public Task InconsistentResizeSuccessCannotPublishContentConfirmation() => OnUi(async () =>
    {
        var monitor = new ResizeMonitor { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var vm = await Ready(monitor);
        vm.SelectedSession = vm.Sessions[0]; vm.ResizeCheckedClients.Execute(null);
        await monitor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        monitor.Pending.TrySetResult(new([new(monitor.Requested[0], null, ClientWindowPlacementStatus.Applied, "Synthetic inconsistent result")], false, "Synthetic"));
        await vm.ResizeCheckedClients.Execution;
        Assert.Empty(vm.ClientPlacementResults); Assert.Contains("could not finish", vm.ClientWindowStatus);
    });

    [Fact]
    public Task DisposeCancelsAndJoinsTheActualResizeBeforeDisposingItsProvider() => OnUi(async () =>
    {
        var monitor = new ResizeMonitor { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var vm = await Ready(monitor); Task? disposing = null;
        try
        {
            vm.SelectedSession = vm.Sessions[0]; vm.ResizeCheckedClients.Execute(null);
            await monitor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            disposing = vm.DisposeAsync().AsTask();
            Assert.True(monitor.OperationToken.IsCancellationRequested);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(disposing.IsCompleted); Assert.False(monitor.Disposed);
        }
        finally
        {
            monitor.Pending.TrySetResult(monitor.Result());
            await (disposing ?? vm.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(monitor.Disposed); Assert.Empty(vm.ClientPlacementResults);
    });

    [Fact]
    public void ProductionSizeControlsUseWrappingPhysicalUnitsAndExplicitApply()
    {
        using var source = typeof(ClientResizeIntegrationTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        var document = XDocument.Load(source);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation", xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var tools = document.Descendants(presentation + "StackPanel").Single(element => (string?)element.Attribute(xaml + "Name") == "ClientContentSizeTools");
        var fields = tools.Descendants(presentation + "TextBox").ToArray(); Assert.Equal(2, fields.Length);
        Assert.Contains(fields, element => ((string?)element.Attribute("Text"))?.StartsWith("{Binding ClientContentWidth,", StringComparison.Ordinal) == true);
        Assert.Contains(fields, element => ((string?)element.Attribute("Text"))?.StartsWith("{Binding ClientContentHeight,", StringComparison.Ordinal) == true);
        Assert.All(fields, field =>
        {
            Assert.Equal("136", (string?)field.Attribute("Width"));
            Assert.Contains(field.Ancestors(), element => element.Name == presentation + "WrapPanel");
            Assert.Contains("physical pixels", (string?)field.Attribute("AutomationProperties.Name") ?? "");
        });
        Assert.Equal(2, tools.Descendants(presentation + "TextBlock").Count(element => (string?)element.Attribute("Text") == "px"));
        var tiny = tools.Descendants(presentation + "Button").Single(element => (string?)element.Attribute("Command") == "{Binding UseTinyClientSize}");
        Assert.Equal("{Binding CanEditClientSize}", (string?)tiny.Attribute("IsEnabled"));
        var apply = tools.Descendants(presentation + "Button").Single(element => (string?)element.Attribute("Command") == "{Binding ResizeCheckedClients}");
        Assert.Equal("{Binding CanResizeClients}", (string?)apply.Attribute("IsEnabled"));
        Assert.Contains(tools.Ancestors(presentation + "ScrollViewer"), element => (string?)element.Attribute("VerticalScrollBarVisibility") == "Auto");
    }

    private async Task<ShellViewModel> Ready(IDesktopMonitor monitor, bool sample = false, string? folder = null)
    {
        var vm = new ShellViewModel(folder is null ? _root : Path.Combine(_root, folder), sample, Dispatcher.CurrentDispatcher, monitor);
        await vm.InitializeAsync(); vm.Refresh.Execute(null); await vm.Refresh.Execution;
        return vm;
    }

    private class PlainMonitor : IDesktopMonitor, IDisposable
    {
        public SessionObservation[] Processes = [Observation(101), Observation(102)];
        public bool Disposed;
        private static SessionObservation Observation(int pid) => new(pid, DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks,
            "Synthetic", pid, "", false, true, 1, 1, null, DateTimeOffset.UtcNow);
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct) => Task.FromResult(new DesktopSnapshot(
            new(DateTimeOffset.UtcNow, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 10), Processes));
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
        public void Dispose() => Disposed = true;
    }

    private class ResizeMonitor : PlainMonitor, IClientWindowResizer
    {
        public IReadOnlyList<SessionObservation> Requested = [];
        public ClientContentSize? RequestedSize;
        public CancellationToken OperationToken;
        public int Resizes, Cancellations;
        public TaskCompletionSource<ClientWindowArrangementResult>? Pending;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ClientWindowArrangementResult> ResizeAsync(IReadOnlyList<SessionObservation> sessions, ClientContentSize size, CancellationToken ct)
        {
            Resizes++; Requested = sessions; RequestedSize = size; OperationToken = ct; Started.TrySetResult();
            // This fixture deliberately ignores cancellation to test the actual completion owner.
            return Pending?.Task ?? Task.FromResult(Result());
        }
        public ClientWindowArrangementResult Result() => new(Requested.Select((session, index) => new ClientWindowPlacementResult(
            session, new(index * 1000, 0, (RequestedSize?.Width ?? 640) + 16, (RequestedSize?.Height ?? 480) + 39), ClientWindowPlacementStatus.Applied,
            "Synthetic physical content confirmed; no real client changed")).ToArray(), false, "Synthetic resize completed");
        public void CancelWindowOperations() => Cancellations++;
    }

    private sealed class CombinedMonitor : ResizeMonitor, IClientWindowBatchControl
    {
        public int StateChanges;
        public Task<WindowActionResult> ChangeAsync(SessionObservation session, ClientWindowAction action, CancellationToken ct) => throw new InvalidOperationException("Unexpected single-client operation");
        public Task<ClientWindowArrangementResult> ChangeManyAsync(IReadOnlyList<SessionObservation> sessions, ClientWindowAction action, CancellationToken ct)
        {
            StateChanges++;
            return Task.FromResult(new ClientWindowArrangementResult(sessions.Select(session => new ClientWindowPlacementResult(
                session, null, ClientWindowPlacementStatus.Applied, "Synthetic state confirmed")).ToArray(), false, "Synthetic state completed"));
        }
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
            || !Path.GetFileName(full).StartsWith("zenith-resize-ui-tests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
