using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Automatic window layout as clients connect, through the real sample and presentation path with a synthetic log snapshot.
public sealed partial class ClientWindowIntegrationTests
{
    [Theory]
    [InlineData(ClientArrivalWindowAction.Arrange)]
    [InlineData(ClientArrivalWindowAction.Minimize)]
    public Task EachNewClientIsArrangedOrMinimizedExactlyOnceWhenItFirstConnects(ClientArrivalWindowAction action) => OnUi(async () =>
    {
        var monitor = new Monitor();
        await using var shell = await Ready(monitor);
        shell.ClientArrivalAction = action; shell.SaveSettings.Execute(null); await shell.SaveSettings.Execution;
        Assert.Equal("Settings saved", shell.SaveStatus);
        var log = new ArrivalLog(shell, monitor);
        await log.StartedWithNoClientsAsync();
        // The second client has the lower PID but started later, so its slot comes second.
        var first = log.Client(600, minutesAgo: 3); var second = log.Client(500, minutesAgo: 2);
        await log.PresentAsync([first], (first, [ClientLogFact.Joining]));
        Assert.Equal(0, monitor.Arrangements + monitor.StateChanges);
        await log.PresentAsync([first], (first, [ClientLogFact.Connected]));
        Assert.Equal(1, monitor.Arrangements + monitor.StateChanges);
        Assert.Equal(new[] { 600 }, monitor.Requested.Select(client => client.ProcessId));
        await log.PresentAsync([first]);
        await log.PresentAsync([first, second], (second, [ClientLogFact.Joining, ClientLogFact.Connected]));
        Assert.Equal(2, monitor.Arrangements + monitor.StateChanges);
        Assert.Equal(action == ClientArrivalWindowAction.Arrange ? new[] { 600, 500 } : new[] { 500 }, monitor.Requested.Select(client => client.ProcessId));
        // A teleport and reconnect is not a new arrival.
        await log.PresentAsync([first, second], (first, [ClientLogFact.Teleporting, ClientLogFact.Joining, ClientLogFact.Connected]));
        await shell.ClientArrivalWork;
        Assert.Equal(2, monitor.Arrangements + monitor.StateChanges);
        if (action == ClientArrivalWindowAction.Arrange)
        {
            Assert.Equal(2, monitor.Arrangements); Assert.Equal("fixture-display-1", monitor.RequestedDisplay);
            Assert.Equal(new ClientWindowTileOptions(), monitor.RequestedOptions);
        }
        else { Assert.Equal(2, monitor.StateChanges); Assert.Equal(ClientWindowAction.Minimize, monitor.StateAction); }
        Assert.Contains("A client connected", shell.ClientWindowStatus);
        Assert.Equal(action == ClientArrivalWindowAction.Arrange ? 2 : 1, shell.ClientPlacementResults.Count);
        Assert.DoesNotContain(shell.Events, e => e.Contains("Operation failed"));
    });

    [Fact]
    public Task ConnectedClientsLeaveWindowsAloneUntilAutomaticLayoutIsSaved() => OnUi(async () =>
    {
        var monitor = new Monitor();
        await using var shell = await Ready(monitor);
        Assert.Equal(ClientArrivalWindowAction.None, shell.ClientArrivalAction);
        Assert.Equal(3, shell.ClientArrivalChoices.Count);
        var log = new ArrivalLog(shell, monitor);
        await log.StartedWithNoClientsAsync();
        var first = log.Client(700, minutesAgo: 3); var second = log.Client(701, minutesAgo: 2);
        await log.PresentAsync([first], (first, [ClientLogFact.Joining, ClientLogFact.Connected]));
        shell.ClientArrivalAction = ClientArrivalWindowAction.Arrange; // An unsaved draft changes nothing.
        Assert.Contains("Unsaved", shell.SaveStatus);
        await log.PresentAsync([first, second], (second, [ClientLogFact.Joining, ClientLogFact.Connected]));
        await shell.ClientArrivalWork;
        Assert.Equal(0, monitor.Arrangements + monitor.StateChanges);
        shell.ClientArrivalAction = ClientArrivalWindowAction.None;
        Assert.Equal("Settings saved", shell.SaveStatus);
    });

    [Fact]
    public Task PanicCancelsAnAutomaticArrangementAndLaterArrivalsWaitForAnExplicitResume() => OnUi(async () =>
    {
        var monitor = new Monitor();
        await using var shell = await Ready(monitor);
        shell.ClientArrivalAction = ClientArrivalWindowAction.Arrange; shell.SaveSettings.Execute(null); await shell.SaveSettings.Execution;
        Assert.False(shell.CanResumeClientArrivalLayout);
        var log = new ArrivalLog(shell, monitor);
        await log.StartedWithNoClientsAsync();
        var first = log.Client(800, minutesAgo: 4); var second = log.Client(801, minutesAgo: 3);
        var third = log.Client(802, minutesAgo: 2); var fourth = log.Client(803, minutesAgo: 1);
        monitor.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await log.PresentAsync([first], (first, [ClientLogFact.Joining, ClientLogFact.Connected]));
        try
        {
            await monitor.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, monitor.Arrangements);
            shell.Panic.Execute(null);
            Assert.True(monitor.OperationToken.IsCancellationRequested); Assert.True(monitor.Cancellations > 0);
        }
        finally
        {
            monitor.Pending.TrySetResult(new([new(monitor.Requested[0], null, ClientWindowPlacementStatus.Cancelled, "Synthetic not dispatched")], true, "Synthetic stopped"));
        }
        await shell.ClientArrivalWork;
        Assert.Contains("Panic stop", shell.Notice);
        Assert.Contains("Stopped operation report", shell.ClientWindowStatus);
        Assert.Equal("Cancelled", Assert.Single(shell.ClientPlacementResults).Outcome);
        monitor.Pending = null;
        // Panic without Lock: clients the launch queue had already started keep joining, and none of them re-tiles anything.
        Assert.True(shell.CanResumeClientArrivalLayout);
        await log.PresentAsync([first, second], (second, [ClientLogFact.Joining, ClientLogFact.Connected]));
        await shell.ClientArrivalWork;
        await log.PresentAsync([first, second, third], (third, [ClientLogFact.Joining, ClientLogFact.Connected]));
        await shell.ClientArrivalWork;
        Assert.Equal(1, monitor.Arrangements);
        Assert.Contains("automatic layout is stopped", shell.ClientWindowStatus);
        Assert.Contains("Resume automatic layout", shell.ClientWindowStatus);
        // Resuming acts on nothing by itself; clients that connected while stopped count as seen.
        shell.ResumeClientArrivalLayout.Execute(null);
        Assert.False(shell.CanResumeClientArrivalLayout);
        Assert.Contains("Automatic layout resumed", shell.ClientWindowStatus);
        await log.PresentAsync([first, second, third]);
        await shell.ClientArrivalWork;
        Assert.Equal(1, monitor.Arrangements);
        // The next client that connects arranges every connected client again, in launch order.
        await log.PresentAsync([first, second, third, fourth], (fourth, [ClientLogFact.Joining, ClientLogFact.Connected]));
        await shell.ClientArrivalWork;
        Assert.Equal(2, monitor.Arrangements);
        Assert.Equal(new[] { 800, 801, 802, 803 }, monitor.Requested.Select(client => client.ProcessId));
    });

    [Fact]
    public Task LockLeavesArrivalsAloneAfterUnlockUntilAnExplicitResume() => OnUi(async () =>
    {
        var monitor = new Monitor();
        await using var shell = await Ready(monitor);
        shell.ClientArrivalAction = ClientArrivalWindowAction.Minimize; shell.SaveSettings.Execute(null); await shell.SaveSettings.Execution;
        var log = new ArrivalLog(shell, monitor);
        await log.StartedWithNoClientsAsync();
        var first = log.Client(900, minutesAgo: 4); var second = log.Client(901, minutesAgo: 3);
        var third = log.Client(902, minutesAgo: 2); var fourth = log.Client(903, minutesAgo: 1);
        await log.PresentAsync([first], (first, [ClientLogFact.Joining, ClientLogFact.Connected]));
        await shell.ClientArrivalWork;
        Assert.Equal(1, monitor.StateChanges);
        shell.LockDashboard();
        await log.PresentAsync([first, second], (second, [ClientLogFact.Joining, ClientLogFact.Connected]));
        await shell.ClientArrivalWork;
        // Locked: resuming is not offered and an attempt changes nothing.
        Assert.False(shell.CanResumeClientArrivalLayout);
        shell.ResumeClientArrivalLayout.Execute(null);
        shell.Unlock.Execute(null);
        Assert.Contains("Automation remains stopped", shell.Notice);
        Assert.True(shell.CanResumeClientArrivalLayout);
        await log.PresentAsync([first, second, third], (third, [ClientLogFact.Joining, ClientLogFact.Connected]));
        await shell.ClientArrivalWork;
        Assert.Equal(1, monitor.StateChanges);
        Assert.Contains("automatic layout is stopped", shell.ClientWindowStatus);
        shell.ResumeClientArrivalLayout.Execute(null);
        await log.PresentAsync([first, second, third, fourth], (fourth, [ClientLogFact.Joining, ClientLogFact.Connected]));
        await shell.ClientArrivalWork;
        Assert.Equal(2, monitor.StateChanges);
        Assert.Equal(new[] { 903 }, monitor.Requested.Select(client => client.ProcessId));
        Assert.Equal(ClientWindowAction.Minimize, monitor.StateAction);
    });

    // N-B5a-2: a stop ends only the request it interrupts. A client that connects after the stop (after Resume, for Panic)
    // while that request is still returning is minimized on its own, and is never left waiting for a later, unrelated arrival.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task AClientThatConnectsWhileAStoppedRequestReturnsIsNeverLeftWaitingForTheNextArrival(bool panic) => OnUi(async () =>
    {
        var monitor = new Monitor();
        await using var shell = await Ready(monitor);
        shell.ClientArrivalAction = ClientArrivalWindowAction.Minimize; shell.SaveSettings.Execute(null); await shell.SaveSettings.Execution;
        var log = new ArrivalLog(shell, monitor);
        await log.StartedWithNoClientsAsync();
        var first = log.Client(950, minutesAgo: 3); var second = log.Client(951, minutesAgo: 2); var third = log.Client(952, minutesAgo: 1);
        monitor.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await log.PresentAsync([first], (first, [ClientLogFact.Joining, ClientLogFact.Connected]));
        try
        {
            await monitor.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (panic)
            {
                shell.Panic.Execute(null);
                shell.ResumeClientArrivalLayout.Execute(null);
                Assert.Contains("Automatic layout resumed", shell.ClientWindowStatus);
            }
            else shell.CancelClientWindowWork.Execute(null);
            Assert.True(monitor.OperationToken.IsCancellationRequested);
            // The stopped request has not returned yet when the next client connects.
            await log.PresentAsync([first, second], (second, [ClientLogFact.Joining, ClientLogFact.Connected]));
            Assert.Equal(1, monitor.StateChanges);
        }
        finally
        {
            monitor.Pending.TrySetResult(new([new(monitor.Requested[0], null, ClientWindowPlacementStatus.Cancelled, "Synthetic not dispatched")], true, "Synthetic stopped"));
        }
        monitor.Pending = null;
        await shell.ClientArrivalWork;
        Assert.Equal(2, monitor.StateChanges);
        Assert.Equal(new[] { 951 }, monitor.Requested.Select(client => client.ProcessId));
        Assert.Contains("A client connected, so Zenith minimized it", shell.ClientWindowStatus);
        // The next arrival acts on itself only, never on a client that connected earlier.
        await log.PresentAsync([first, second, third], (third, [ClientLogFact.Joining, ClientLogFact.Connected]));
        await shell.ClientArrivalWork;
        Assert.Equal(3, monitor.StateChanges);
        Assert.Equal(new[] { 952 }, monitor.Requested.Select(client => client.ProcessId));
        Assert.Equal(ClientWindowAction.Minimize, monitor.StateAction);
    });

    // N-B5a-4: the native move refuses a minimized window, so automatic Arrange leaves it out instead of giving it a cell that
    // stays empty and a failure row on every later arrival. Restored, it is tiled again when the next client connects.
    [Fact]
    public Task AutomaticArrangeLeavesMinimizedClientsOutUntilTheyAreRestored() => OnUi(async () =>
    {
        var monitor = new Monitor();
        await using var shell = await Ready(monitor);
        shell.ClientArrivalAction = ClientArrivalWindowAction.Arrange; shell.SaveSettings.Execute(null); await shell.SaveSettings.Execution;
        var log = new ArrivalLog(shell, monitor);
        await log.StartedWithNoClientsAsync();
        var first = log.Client(400, minutesAgo: 3); var second = log.Client(401, minutesAgo: 2); var third = log.Client(402, minutesAgo: 1);
        var minimizedFirst = first with { Minimized = true };
        // The only connected client is minimized: there is nothing to tile, and the report says why.
        await log.PresentAsync([minimizedFirst], (first, [ClientLogFact.Joining, ClientLogFact.Connected]));
        await shell.ClientArrivalWork;
        Assert.Equal(0, monitor.Arrangements);
        Assert.Contains("A client connected, but every connected client is minimized or hidden", shell.ClientWindowStatus);
        await log.PresentAsync([minimizedFirst, second], (second, [ClientLogFact.Joining, ClientLogFact.Connected]));
        await shell.ClientArrivalWork;
        Assert.Equal(1, monitor.Arrangements);
        Assert.Equal(new[] { 401 }, monitor.Requested.Select(client => client.ProcessId));
        Assert.Equal("Position confirmed", Assert.Single(shell.ClientPlacementResults).Outcome);
        // Restored, the first client takes its launch-order slot again when the next client connects.
        await log.PresentAsync([first, second, third], (third, [ClientLogFact.Joining, ClientLogFact.Connected]));
        await shell.ClientArrivalWork;
        Assert.Equal(2, monitor.Arrangements);
        Assert.Equal(new[] { 400, 401, 402 }, monitor.Requested.Select(client => client.ProcessId));
        Assert.DoesNotContain(shell.Events, e => e.Contains("Operation failed"));
    });

    // N-B5a-5: reopening Zenith with Minimize saved leaves alone a client the user joined a few minutes earlier, although its
    // log still reports Connected; a client that starts afterwards is minimized when it connects.
    [Fact]
    public Task ReopeningZenithLeavesClientsThatWereAlreadyRunningAloneAndActsOnLaterOnes() => OnUi(async () =>
    {
        await using (var first = await Ready(new Monitor()))
        {
            first.ClientArrivalAction = ClientArrivalWindowAction.Minimize; first.SaveSettings.Execute(null); await first.SaveSettings.Execution;
            Assert.Equal("Settings saved", first.SaveStatus);
        }
        var monitor = new Monitor();
        await using var shell = await Ready(monitor);
        Assert.Equal(ClientArrivalWindowAction.Minimize, shell.ClientArrivalAction);
        var log = new ArrivalLog(shell, monitor);
        var joinedEarlier = log.Client(460, minutesAgo: 4); var startedLater = log.Client(461, minutesAgo: 1);
        await log.PresentAsync([joinedEarlier], (joinedEarlier, [ClientLogFact.Joining, ClientLogFact.Connected]));
        await shell.ClientArrivalWork;
        Assert.Equal(0, monitor.StateChanges);
        await log.PresentAsync([joinedEarlier, startedLater], (startedLater, [ClientLogFact.Joining, ClientLogFact.Connected]));
        await shell.ClientArrivalWork;
        Assert.Equal(1, monitor.StateChanges);
        Assert.Equal(new[] { 461 }, monitor.Requested.Select(client => client.ProcessId));
        Assert.Contains("A client connected, so Zenith minimized it", shell.ClientWindowStatus);
    });

    // N-B5a-6: a manual window operation is queued or running on the window lane when a client connects. An automatic request
    // would wait behind it, then re-tile every connected client right after the user's chosen arrangement and replace its
    // report. Zenith leaves the windows alone instead, says so, and acts again when the next client connects.
    [Theory]
    [InlineData(ClientArrivalWindowAction.Arrange)]
    [InlineData(ClientArrivalWindowAction.Minimize)]
    public Task AClientThatConnectsDuringAManualArrangeLeavesTheUsersArrangementAndReportAlone(ClientArrivalWindowAction action) => OnUi(async () =>
    {
        var monitor = new Monitor();
        await using var shell = await Ready(monitor);
        shell.ClientArrivalAction = action; shell.SaveSettings.Execute(null); await shell.SaveSettings.Execution;
        var log = new ArrivalLog(shell, monitor);
        await log.StartedWithNoClientsAsync();
        var first = log.Client(470, minutesAgo: 3); var second = log.Client(471, minutesAgo: 2); var third = log.Client(472, minutesAgo: 1);
        await log.PresentAsync([first], (first, [ClientLogFact.Joining, ClientLogFact.Connected]));
        await shell.ClientArrivalWork;
        Assert.Equal(1, monitor.Arrangements + monitor.StateChanges);
        // Then the user arranges the first client on the second monitor by hand.
        shell.Sessions.Single(row => row.Observation.ProcessId == 470).IsChecked = true;
        shell.SelectedClientDisplay = shell.ClientDisplays[1];
        monitor.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.ArrangeClients.Execute(null);
        var manual = monitor.Requested.ToArray();
        try
        {
            Assert.True(shell.IsArrangingClients);
            Assert.Equal(new[] { 470 }, manual.Select(client => client.ProcessId));
            await log.PresentAsync([first, second], (second, [ClientLogFact.Joining, ClientLogFact.Connected]));
            Assert.Equal(2, monitor.Arrangements + monitor.StateChanges);
        }
        finally
        {
            monitor.Pending.TrySetResult(new([new(manual[0], new(2000, -1000, 640, 480), ClientWindowPlacementStatus.Applied, "Synthetic manual result")],
                false, "Synthetic manual arrangement complete."));
        }
        monitor.Pending = null;
        await shell.ArrangeClients.Execution; await shell.ClientArrivalWork;
        Assert.Equal(2, monitor.Arrangements + monitor.StateChanges);
        Assert.Equal("fixture-display-2", monitor.RequestedDisplay);
        Assert.Equal("PID 470", Assert.Single(shell.ClientPlacementResults).Client);
        Assert.StartsWith("Synthetic manual arrangement complete.", shell.ClientWindowStatus);
        Assert.Contains("A client connected during this window operation, so automatic layout left the windows alone.", shell.ClientWindowStatus);
        Assert.DoesNotContain(shell.Events, e => e.Contains("Operation failed"));
        // The client that connected meanwhile counts as seen; the next client that connects gets the saved choice again.
        await log.PresentAsync([first, second, third], (third, [ClientLogFact.Joining, ClientLogFact.Connected]));
        await shell.ClientArrivalWork;
        Assert.Equal(3, monitor.Arrangements + monitor.StateChanges);
        Assert.Equal(action == ClientArrivalWindowAction.Arrange ? new[] { 470, 471, 472 } : new[] { 472 }, monitor.Requested.Select(client => client.ProcessId));
        Assert.Contains("A client connected, so Zenith", shell.ClientWindowStatus);
        Assert.DoesNotContain("during this window operation", shell.ClientWindowStatus);
    });

    // N-B5a-6: the user starts Arrange while an automatic request runs, and another client connects meanwhile. The manual
    // operation waits on the lane; the follow-up request for that client is left out instead of queueing behind it.
    [Fact]
    public Task AFollowUpRequestNeverQueuesBehindAManualArrangeStartedDuringAnAutomaticOne() => OnUi(async () =>
    {
        var monitor = new Monitor();
        await using var shell = await Ready(monitor);
        shell.ClientArrivalAction = ClientArrivalWindowAction.Arrange; shell.SaveSettings.Execute(null); await shell.SaveSettings.Execution;
        var log = new ArrivalLog(shell, monitor);
        await log.StartedWithNoClientsAsync();
        var first = log.Client(480, minutesAgo: 2); var second = log.Client(481, minutesAgo: 1);
        var automatic = monitor.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await log.PresentAsync([first], (first, [ClientLogFact.Joining, ClientLogFact.Connected]));
        var automaticClients = monitor.Requested.ToArray();
        var manual = monitor.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Assert.Equal(1, monitor.Arrangements);
            shell.Sessions.Single(row => row.Observation.ProcessId == 480).IsChecked = true;
            shell.ArrangeClients.Execute(null);
            Assert.Equal(2, monitor.Arrangements);
            await log.PresentAsync([first, second], (second, [ClientLogFact.Joining, ClientLogFact.Connected]));
            automatic.TrySetResult(new([new(automaticClients[0], new(0, 0, 320, 240), ClientWindowPlacementStatus.Applied, "Synthetic automatic result")], false, "Synthetic automatic arrangement."));
            // A follow-up request would wait on the manual one here, so bound the wait and count the requests.
            await Task.WhenAny(shell.ClientArrivalWork, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Equal(2, monitor.Arrangements);
            Assert.True(shell.ClientArrivalWork.IsCompleted);
        }
        finally
        {
            automatic.TrySetResult(new([], true, "Synthetic stopped"));
            manual.TrySetResult(new([new(automaticClients[0], new(0, 0, 320, 240), ClientWindowPlacementStatus.Applied, "Synthetic manual result")], false, "Synthetic manual arrangement complete."));
        }
        monitor.Pending = null;
        await shell.ArrangeClients.Execution; await shell.ClientArrivalWork;
        Assert.Equal(2, monitor.Arrangements);
        Assert.Equal("Synthetic manual result", Assert.Single(shell.ClientPlacementResults).Explanation);
        Assert.StartsWith("Synthetic manual arrangement complete.", shell.ClientWindowStatus);
        Assert.Contains("automatic layout left the windows alone", shell.ClientWindowStatus);
        Assert.DoesNotContain(shell.Events, e => e.Contains("Operation failed"));
    });

    // C5 review: a PC where Windows cannot read another Windows user's client never has a complete scan. Before any scan shows
    // which clients were already running, a client that reports Connected is left alone and the window report says why, so
    // the saved choice is not silently inert. A scan whose only gap is that unreadable row is a baseline: a client that
    // connects after it is minimized, while one that was already running is left alone.
    [Fact]
    public Task AutomaticLayoutSaysWhyItWaitsAndActsOnceAScanWithOnlyAnUnreadableClientShowsWhoWasRunning() => OnUi(async () =>
    {
        var monitor = new Monitor();
        await using var shell = await Ready(monitor);
        shell.ClientArrivalAction = ClientArrivalWindowAction.Minimize; shell.SaveSettings.Execute(null); await shell.SaveSettings.Execution;
        var log = new ArrivalLog(shell, monitor) { Complete = false };
        var joinedEarlier = log.Client(490, minutesAgo: 4); var startedLater = log.Client(491, minutesAgo: 1);
        var otherUser = new SessionObservation(492, 0, "Unavailable", 0, "Roblox", false, false, 0, 0, null, DateTimeOffset.UtcNow,
            "Synthetic process identity or access was unavailable during observation.");
        // Incomplete for another reason, for example a client that ended during the scan: no baseline yet.
        await log.PresentAsync([joinedEarlier], (joinedEarlier, [ClientLogFact.Joining, ClientLogFact.Connected]));
        await shell.ClientArrivalWork;
        Assert.Equal(0, monitor.StateChanges);
        Assert.Contains("no client scan has shown yet which clients were already running", shell.ClientWindowStatus);
        Assert.DoesNotContain(shell.Events, e => e.Contains("Operation failed"));
        await log.PresentAsync([otherUser, joinedEarlier]);
        await log.PresentAsync([otherUser, joinedEarlier, startedLater], (startedLater, [ClientLogFact.Joining, ClientLogFact.Connected]));
        await shell.ClientArrivalWork;
        Assert.Equal(1, monitor.StateChanges);
        Assert.Equal(new[] { 491 }, monitor.Requested.Select(client => client.ProcessId));
        Assert.Contains("A client connected, so Zenith minimized it", shell.ClientWindowStatus);
        Assert.DoesNotContain("no client scan", shell.ClientWindowStatus);
    });

    /// <summary>
    /// Publishes one synthetic process scan and log snapshot, then runs the real forced sample and presentation. A stopped
    /// correlator ignores offers, so the 1 Hz loop presents the same snapshot; a presentation queued from an older scan is
    /// dropped while the sampler is held, so no step can pair a new log revision with an old process list.
    /// </summary>
    private sealed class ArrivalLog(ShellViewModel shell, Monitor monitor)
    {
        private readonly Dictionary<int, (ClientLogStreamId Stream, long Offset)> _streams = [];
        private readonly WindowsSessionLogCorrelator _correlator = new(new ReportedProbe());
        private long _revision;
        private bool _attached;
        /// <summary>Whether the scan is marked complete; false for one that finished without a problem but with a gap.</summary>
        public bool Complete { get; init; } = true;
        /// <summary>Zenith started with no client running: its first complete scan lists none, so every later client can arrive.</summary>
        public Task StartedWithNoClientsAsync() => PresentAsync([]);
        public SessionObservation Client(int pid, int minutesAgo) => new(pid, DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).UtcTicks,
            "Synthetic", (nint)(9000 + pid), "Synthetic client", false, true, 1, 1, null, DateTimeOffset.UtcNow);
        public async Task PresentAsync(SessionObservation[] clients, params (SessionObservation Client, ClientLogFact[] Facts)[] lines)
        {
            var serial = await HoldReportedSamplerAsync(shell);
            try
            {
                if (!_attached) { await _correlator.DisposeAsync(); ReportedField(shell, "_logCorrelator").SetValue(shell, _correlator); _attached = true; }
                var now = DateTimeOffset.UtcNow;
                var observed = clients.Select(client => client with { ObservedUtc = now }).ToArray();
                monitor.Inventory = new DesktopSnapshot(new(now, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB,
                    10 * CapacityPolicy.GiB, 10), observed, ProcessObservedUtc: now, ProcessRevision: ++_revision, ProcessInventoryComplete: Complete);
                var diagnostics = new List<ProcessLogDiagnostic>();
                // A row Windows could not read names no process instance, so it has no log.
                foreach (var client in observed.Where(client => client.Problem is null))
                {
                    bool initial = !_streams.TryGetValue(client.ProcessId, out var known);
                    var stream = initial ? new ClientLogStreamId(Guid.NewGuid(), 1) : known.Stream;
                    long start = initial ? 0 : known.Offset, offset = start;
                    var facts = new List<ClientLogObservation>();
                    foreach (var fact in lines.Where(line => line.Client.ProcessId == client.ProcessId).SelectMany(line => line.Facts))
                    {
                        facts.Add(fact == ClientLogFact.Joining
                            ? new(stream, offset, offset + 10, now, now, fact, PlaceId: ReportedPlace, JobId: Guid.NewGuid())
                            : new(stream, offset, offset + 10, now, now, fact));
                        offset += 10;
                    }
                    _streams[client.ProcessId] = (stream, offset);
                    var batch = new ClientLogBatch(stream, ClientLogReadStatus.Ready, initial ? ClientLogDiscontinuity.Initial : ClientLogDiscontinuity.None,
                        offset, offset, false, 0, facts) { ReplayFloorOffset = start };
                    diagnostics.Add(new(new(null, client.ProcessId, client.ProcessStartUtcTicks), SessionLogCorrelationStatus.ResourceHolderObserved,
                        stream, batch, now, now.AddSeconds(10)));
                }
                ReportedField(_correlator, "_snapshot").SetValue(_correlator, new SessionLogDiscoverySnapshot(_revision, now, diagnostics, diagnostics.Count, false));
                ReportedField(shell, "_pendingSamplePresentation").SetValue(shell, null);
            }
            finally { serial.Release(); }
            shell.Refresh.Execute(null); await shell.Refresh.Execution;
        }
    }
}
