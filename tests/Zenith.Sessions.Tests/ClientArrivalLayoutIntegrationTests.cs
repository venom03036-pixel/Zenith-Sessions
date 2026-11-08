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
    public Task PanicCancelsAnAutomaticArrangementAndLockKeepsLaterArrivalsAlone() => OnUi(async () =>
    {
        var monitor = new Monitor();
        await using var shell = await Ready(monitor);
        shell.ClientArrivalAction = ClientArrivalWindowAction.Arrange; shell.SaveSettings.Execute(null); await shell.SaveSettings.Execution;
        var log = new ArrivalLog(shell, monitor);
        var first = log.Client(800, minutesAgo: 3); var second = log.Client(801, minutesAgo: 2); var third = log.Client(802, minutesAgo: 1);
        monitor.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await log.PresentAsync([first], (first, [ClientLogFact.Joining, ClientLogFact.Connected]));
        try
        {
            await monitor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
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
        shell.LockDashboard();
        await log.PresentAsync([first, second], (second, [ClientLogFact.Joining, ClientLogFact.Connected]));
        await shell.ClientArrivalWork;
        Assert.Equal(1, monitor.Arrangements);
        // A client that connected while locked has been seen; only a client that connects after unlocking is arranged.
        shell.Unlock.Execute(null);
        await log.PresentAsync([first, second]);
        await shell.ClientArrivalWork;
        Assert.Equal(1, monitor.Arrangements);
        await log.PresentAsync([first, second, third], (third, [ClientLogFact.Joining, ClientLogFact.Connected]));
        await shell.ClientArrivalWork;
        Assert.Equal(2, monitor.Arrangements);
        Assert.Equal(new[] { 800, 801, 802 }, monitor.Requested.Select(client => client.ProcessId));
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
                    10 * CapacityPolicy.GiB, 10), observed, ProcessObservedUtc: now, ProcessRevision: ++_revision, ProcessInventoryComplete: true);
                var diagnostics = new List<ProcessLogDiagnostic>();
                foreach (var client in observed)
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
