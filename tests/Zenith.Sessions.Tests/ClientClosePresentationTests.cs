using System.IO;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

/// <summary>
/// R-instances-12 in the shell: Close runs the escalating close through the monitor's close control, a client that stays open
/// gets Force close offered on its own row only, the confirmed force close is recorded in recent history as a forced end, and
/// Panic, Lock and a refused request never end anything. The monitor is a fake; nothing native runs.
/// </summary>
public sealed class ClientClosePresentationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-close-ui-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public Task ACloseThatLeavesTheClientOpenOffersForceCloseOnThatRowOnlyAndRecordsTheRequest() => OnUi(async () =>
    {
        var monitor = new CloseMonitor(2) { CloseResult = session => new(ClientCloseStatus.StillOpen, 2, $"Roblox process {session.ProcessId} is still open 6 s after two close requests.") };
        await using var shell = await Ready(monitor);
        var target = shell.Sessions[0]; var other = shell.Sessions[1];
        await shell.RequestCloseAsync(target.Observation);
        Assert.Equal([target.Observation.InstanceKey], monitor.Closed.Select(session => session.InstanceKey));
        Assert.True(target.ForceCloseOffered); Assert.False(other.ForceCloseOffered);
        Assert.Contains("still open", shell.Notice); Assert.Contains("Force close", shell.Notice);
        Assert.Contains("loses unsaved game activity", shell.Notice);
        Assert.Empty(monitor.Forced);
    });

    [Fact]
    public Task AClientThatExitsIsReportedAndNothingIsOffered() => OnUi(async () =>
    {
        var monitor = new CloseMonitor(1) { CloseResult = session => new(ClientCloseStatus.Exited, 1, $"Roblox process {session.ProcessId} exited after one close request.") };
        await using var shell = await Ready(monitor);
        await shell.RequestCloseAsync(shell.Sessions[0].Observation);
        Assert.Contains("exited after one close request", shell.Notice);
        Assert.False(shell.Sessions[0].ForceCloseOffered);
    });

    [Theory]
    [InlineData(ClientCloseStatus.NotCurrent, "exited or its window changed")]
    [InlineData(ClientCloseStatus.NotSent, "Windows rejected")]
    public Task ACloseThatSentNothingSaysSoAndOffersNoForceClose(ClientCloseStatus status, string expected) => OnUi(async () =>
    {
        var monitor = new CloseMonitor(1) { CloseResult = _ => new(status, 0, "synthetic") };
        await using var shell = await Ready(monitor);
        await shell.RequestCloseAsync(shell.Sessions[0].Observation);
        Assert.StartsWith("Close was not sent", shell.Notice); Assert.Contains(expected, shell.Notice);
        Assert.False(shell.Sessions[0].ForceCloseOffered);
    });

    [Fact]
    public Task ForceCloseIsRefusedUntilACloseLeftThatClientOpen() => OnUi(async () =>
    {
        var monitor = new CloseMonitor(1);
        await using var shell = await Ready(monitor);
        await shell.ForceCloseAsync(shell.Sessions[0].Observation);
        Assert.Contains("offered only after", shell.Notice);
        Assert.Empty(monitor.Forced);
    });

    [Fact]
    public Task AConfirmedForceCloseEndsTheClientWithdrawsTheOfferAndRecordsAForcedEndInHistory() => OnUi(async () =>
    {
        // Windows accepts the termination at the real time, as the production control stamps it; history only accepts a
        // forced end within the inventory age of the client's last observation.
        DateTimeOffset? endedUtc = null;
        var monitor = new CloseMonitor(2)
        {
            CloseResult = session => new(ClientCloseStatus.StillOpen, 2, "still open"),
            ForceResult = session => new(ClientForceCloseStatus.Ended, endedUtc = DateTimeOffset.UtcNow, $"Roblox process {session.ProcessId} was ended by force.")
        };
        await using var shell = await Ready(monitor);
        var target = shell.Sessions[0]; var observation = target.Observation;
        await shell.RequestCloseAsync(observation); Assert.True(target.ForceCloseOffered);
        await shell.ForceCloseAsync(observation);
        Assert.Equal([observation.InstanceKey], monitor.Forced.Select(session => session.InstanceKey));
        Assert.False(target.ForceCloseOffered);
        Assert.Contains("ended by force", shell.Notice);
        Assert.Contains(shell.Events, line => line.Contains($"PID {observation.ProcessId}", StringComparison.Ordinal) && line.Contains("force", StringComparison.OrdinalIgnoreCase));
        // The next complete scan no longer sees it: recent history shows the forced end, separately from the close request.
        monitor.Drop(observation.ProcessId);
        await Refresh(shell); await shell.RecentClients.ActiveSave;
        var ended = Assert.Single(shell.RecentClients.Rows);
        Assert.Equal(observation.ProcessKey, ended.Entry.Process);
        Assert.NotNull(endedUtc); Assert.Equal(endedUtc, ended.Entry.ForcedEndUtc); Assert.NotNull(ended.Entry.CloseRequestedUtc);
        Assert.Equal(ClientEndCause.ForcedEnd, ended.Entry.EndCause);
        Assert.Contains("ended it by force", ended.Outcome); Assert.Contains("ended it by force", ended.Details);
        Assert.Single(shell.Sessions);
    });

    [Fact]
    public Task AForceCloseWindowsDoesNotAcceptEndsNothingAndRecordsNoForcedEnd() => OnUi(async () =>
    {
        var monitor = new CloseMonitor(1)
        {
            CloseResult = _ => new(ClientCloseStatus.StillOpen, 2, "still open"),
            ForceResult = _ => new(ClientForceCloseStatus.Denied, null, "Windows refused to end the process. Nothing was ended.")
        };
        await using var shell = await Ready(monitor);
        var observation = shell.Sessions[0].Observation;
        await shell.RequestCloseAsync(observation); await shell.ForceCloseAsync(observation);
        Assert.Contains("Nothing was ended", shell.Notice);
        // The client is still there and still open, so the offer stays for another try.
        Assert.True(shell.Sessions[0].ForceCloseOffered);
        monitor.Drop(observation.ProcessId);
        await Refresh(shell); await shell.RecentClients.ActiveSave;
        Assert.Null(Assert.Single(shell.RecentClients.Rows).Entry.ForcedEndUtc);
    });

    [Fact]
    public Task PanicStopsAWaitingCloseAndWithdrawsEveryOffer() => OnUi(async () =>
    {
        var monitor = new CloseMonitor(2) { CloseResult = _ => new(ClientCloseStatus.StillOpen, 2, "still open") };
        await using var shell = await Ready(monitor);
        await shell.RequestCloseAsync(shell.Sessions[1].Observation); Assert.True(shell.Sessions[1].ForceCloseOffered);
        monitor.HoldCloses = true;
        var waiting = shell.RequestCloseAsync(shell.Sessions[0].Observation);
        await monitor.CloseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // A second click on the same row while its close waits does not start another close.
        await shell.RequestCloseAsync(shell.Sessions[0].Observation);
        Assert.Contains("already waiting", shell.Notice);
        Assert.Equal(2, monitor.Closed.Count);
        shell.Panic.Execute(null);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(monitor.LastCloseCancelled);
        Assert.False(shell.Sessions[0].ForceCloseOffered); Assert.False(shell.Sessions[1].ForceCloseOffered);
        Assert.StartsWith("Panic stop", shell.Notice);
        Assert.Contains(shell.Events, line => line.Contains("Close stopped", StringComparison.Ordinal));
        Assert.Empty(monitor.Forced);
        Assert.Equal(1, monitor.CancelCalls);
    });

    [Fact]
    public Task LockWithdrawsOffersAndRefusesForceCloseWhileNormalCloseStaysAvailable() => OnUi(async () =>
    {
        var monitor = new CloseMonitor(1) { CloseResult = _ => new(ClientCloseStatus.StillOpen, 2, "still open") };
        await using var shell = await Ready(monitor);
        var observation = shell.Sessions[0].Observation;
        await shell.RequestCloseAsync(observation); Assert.True(shell.Sessions[0].ForceCloseOffered);
        shell.Lock.Execute(null); Assert.True(shell.IsLocked);
        Assert.False(shell.Sessions[0].ForceCloseOffered);
        await shell.ForceCloseAsync(observation);
        Assert.Empty(monitor.Forced); Assert.Contains("offered only after", shell.Notice);
        // The guarded normal close stays available under lock, but it offers no force close there.
        await shell.RequestCloseAsync(observation);
        Assert.Equal(2, monitor.Closed.Count); Assert.False(shell.Sessions[0].ForceCloseOffered);
        Assert.True(shell.IsLocked);
    });

    [Fact]
    public void TheSessionsRowShowsForceCloseOnlyWhenItWasOfferedAndAsksBeforeEndingTheClient()
    {
        using var source = typeof(ClientClosePresentationTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        var document = XDocument.Load(source);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var close = document.Descendants(presentation + "Button").Single(element => (string?)element.Attribute("Click") == "CloseSession" && (string?)element.Attribute("Content") == "Close");
        var force = document.Descendants(presentation + "Button").Single(element => (string?)element.Attribute("Click") == "ForceCloseSession");
        Assert.Equal("Force close", (string?)force.Attribute("Content"));
        Assert.Equal("{Binding ForceCloseOffered, Converter={StaticResource VisibleIf}}", (string?)force.Attribute("Visibility"));
        Assert.Same(close.Parent, force.Parent);
        Assert.Contains("Force close", (string?)force.Attribute("ToolTip"));
    }

    private async Task<ShellViewModel> Ready(CloseMonitor monitor)
    {
        var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, monitor);
        try
        {
            await shell.InitializeAsync(); await Refresh(shell);
            Assert.True(shell.IsReady, shell.Notice); Assert.Equal(monitor.Count, shell.Sessions.Count);
            return shell;
        }
        catch { await shell.DisposeAsync(); throw; }
    }
    private static async Task Refresh(ShellViewModel shell)
    { shell.Refresh.Execute(null); await shell.Refresh.Execution; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }

    private sealed class CloseMonitor(int count) : IDesktopMonitor, IClientCloseControl
    {
        private readonly List<SessionObservation> _rows = Enumerable.Range(1, count).Select(pid => new SessionObservation(pid,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks, "Synthetic", pid + 100, "Synthetic", false, true, 1, 1, 0, DateTimeOffset.UtcNow)).ToList();
        private long _revision;
        public int Count => _rows.Count;
        public List<SessionObservation> Closed { get; } = [];
        public List<SessionObservation> Forced { get; } = [];
        public Func<SessionObservation, ClientCloseResult> CloseResult = _ => new(ClientCloseStatus.NotCurrent, 0, "synthetic");
        public Func<SessionObservation, ClientForceCloseResult> ForceResult = _ => new(ClientForceCloseStatus.NotCurrent, null, "synthetic");
        public bool HoldCloses, LastCloseCancelled;
        public int CancelCalls;
        public TaskCompletionSource CloseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Drop(int pid) => _rows.RemoveAll(row => row.ProcessId == pid);

        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var now = DateTimeOffset.UtcNow;
            var rows = _rows.Select(row => row with { ObservedUtc = now }).ToArray();
            return Task.FromResult(new DesktopSnapshot(new(now, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 10),
                rows, null, now, Interlocked.Increment(ref _revision), true));
        }
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => throw new InvalidOperationException("No focus expected.");
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => throw new InvalidOperationException("The shell must use the close control when the monitor offers one.");
        public async Task<ClientCloseResult> CloseAsync(SessionObservation session, CancellationToken ct)
        {
            Closed.Add(session); CloseStarted.TrySetResult();
            if (HoldCloses)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) { LastCloseCancelled = true; return new(ClientCloseStatus.Cancelled, 1, "Close stopped after one close request. A request already sent cannot be recalled."); }
            }
            return CloseResult(session);
        }
        public Task<ClientForceCloseResult> ForceCloseAsync(SessionObservation session, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Forced.Add(session); return Task.FromResult(ForceResult(session)); }
        public void CancelCloseOperations() => CancelCalls++;
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
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-close-ui-tests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
