using System.IO;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

/// <summary>Pure arrival tracking and slot order for automatic window layout; no native calls, timers or real clients.</summary>
public sealed class ClientArrivalLayoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static SessionObservation Client(int pid, double startedMinutesAgo) => new(pid, Now.AddMinutes(-startedMinutesAgo).UtcTicks,
        "Synthetic", (nint)(5000 + pid), "Synthetic client", false, true, 1, 1, null, Now);
    private static DesktopSnapshot Scan(bool complete, params SessionObservation[] clients) => new(
        new(Now, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 10), clients,
        complete ? null : "Synthetic scan failed", Now, 1, complete);
    // A scan that finished without a problem but that the monitor marked incomplete, as WindowsDesktopMonitor does for a row it
    // could not read, a client that ended during the scan, missing memory counters, or reaching its scan budget.
    private static DesktopSnapshot Partial(params SessionObservation[] clients) => Scan(true, clients) with { ProcessInventoryComplete = false };
    // The row WindowsDesktopMonitor reports for a process it could not open, for example another Windows user's client.
    private static SessionObservation Unreadable(int pid) => new(pid, 0, "Unavailable", 0, "Roblox", false, false, 0, 0, null, Now,
        "Synthetic process identity or access was unavailable during observation.");
    private static SessionEvidenceSnapshot Log(SessionObservation client, ObservedSessionLifecycle lifecycle, bool ambiguous = false) => new(Guid.NewGuid(),
        new(null, client.ProcessId, client.ProcessStartUtcTicks), client.Window, lifecycle, null, null, null, null,
        SessionEvidenceConfidence.UnverifiedDiagnostic, Now, ambiguous);
    private static int[] Pids(IEnumerable<SessionObservation> clients) => clients.Select(client => client.ProcessId).ToArray();
    private static LaunchOperation Launched(SessionObservation client) => new(new(Guid.NewGuid(), Guid.NewGuid(), 1, 123, 456, 0, Now),
        LaunchState.ClientStartedUnverified, LaunchProblem.None, Now, new(client.ProcessId, client.ProcessStartUtcTicks, Now));
    // Zenith started with no client running: its first complete scan lists none, so every client listed later can arrive.
    private static ClientArrivalTracker StartedWithNoClients()
    {
        var tracker = new ClientArrivalTracker();
        Assert.Empty(tracker.Observe(Scan(true), []));
        return tracker;
    }

    [Fact]
    public void ASyntheticEvidenceSequenceReportsEachClientOnceWhenItFirstConnects()
    {
        var tracker = StartedWithNoClients();
        // The second client has the lower PID but started later, so a PID order would put it first.
        var first = Client(900, 5); var second = Client(100, 2);
        Assert.Empty(tracker.Observe(Scan(true, first), [Log(first, ObservedSessionLifecycle.Joining)]));
        Assert.Equal(new[] { 900 }, Pids(tracker.Observe(Scan(true, first), [Log(first, ObservedSessionLifecycle.Connected)])));
        Assert.Empty(tracker.Observe(Scan(true, first), [Log(first, ObservedSessionLifecycle.Connected)]));
        Assert.Empty(tracker.Observe(Scan(true, first, second), [Log(first, ObservedSessionLifecycle.Connected), Log(second, ObservedSessionLifecycle.Joining)]));
        Assert.Equal(new[] { 900 }, Pids(tracker.Slots(null)));
        Assert.Equal(new[] { 100 }, Pids(tracker.Observe(Scan(true, first, second), [Log(first, ObservedSessionLifecycle.Connected), Log(second, ObservedSessionLifecycle.Connected)])));
        Assert.Equal(new[] { 900, 100 }, Pids(tracker.Slots(null)));
        // A teleport, a disconnect and a reconnect are not new arrivals.
        Assert.Empty(tracker.Observe(Scan(true, first, second), [Log(first, ObservedSessionLifecycle.Teleporting), Log(second, ObservedSessionLifecycle.Disconnected)]));
        Assert.Empty(tracker.Observe(Scan(true, first, second), [Log(first, ObservedSessionLifecycle.Connected), Log(second, ObservedSessionLifecycle.Connected)]));
        // A failed or partial scan that misses a client never makes it arrive again.
        Assert.Empty(tracker.Observe(Scan(false, second), [Log(second, ObservedSessionLifecycle.Connected)]));
        Assert.Empty(tracker.Observe(Scan(true, first, second), [Log(first, ObservedSessionLifecycle.Connected), Log(second, ObservedSessionLifecycle.Connected)]));
        // Only a complete scan shows a client left. A new process instance that reuses its PID is a new client.
        Assert.Empty(tracker.Observe(Scan(true, second), [Log(second, ObservedSessionLifecycle.Connected)]));
        Assert.Equal(new[] { 100 }, Pids(tracker.Slots(null)));
        var reused = Client(900, 0);
        Assert.Equal(new[] { 900 }, Pids(tracker.Observe(Scan(true, second, reused), [Log(second, ObservedSessionLifecycle.Connected), Log(reused, ObservedSessionLifecycle.Connected)])));
        Assert.Equal(new[] { 100, 900 }, Pids(tracker.Slots(null)));
    }

    [Fact]
    public void AmbiguousWindowlessFaultedAndUnscannedClientsDoNotArriveUntilTheyCanBeArranged()
    {
        var tracker = StartedWithNoClients();
        var windowless = Client(10, 3) with { Window = 0 };
        Assert.Empty(tracker.Observe(Scan(true, windowless), [Log(windowless, ObservedSessionLifecycle.Connected)]));
        var windowed = windowless with { Window = 777 };
        Assert.Equal(new[] { 10 }, Pids(tracker.Observe(Scan(true, windowed), [Log(windowed, ObservedSessionLifecycle.Connected)])));
        var ambiguous = Client(11, 2); var faulted = Client(12, 1) with { Problem = "Synthetic access denied" }; var unscanned = Client(13, 1);
        Assert.Empty(tracker.Observe(Scan(true, windowed, ambiguous, faulted), [Log(ambiguous, ObservedSessionLifecycle.Connected, ambiguous: true),
            Log(faulted, ObservedSessionLifecycle.Connected), Log(unscanned, ObservedSessionLifecycle.Connected)]));
        Assert.Equal(new[] { 10 }, Pids(tracker.Slots(null)));
        Assert.Equal(new[] { 11 }, Pids(tracker.Observe(Scan(true, windowed, ambiguous), [Log(ambiguous, ObservedSessionLifecycle.Connected)])));
    }

    [Fact]
    public void AClientStillListedWithoutAWindowOrWithAProblemDoesNotArriveAgainWhenItsWindowReturns()
    {
        var tracker = StartedWithNoClients();
        var client = Client(20, 3);
        Assert.Equal(new[] { 20 }, Pids(tracker.Observe(Scan(true, client), [Log(client, ObservedSessionLifecycle.Connected)])));
        // Still running but without a main window for one complete scan, for example while the client recreates it.
        Assert.Empty(tracker.Observe(Scan(true, client with { Window = 0 }), [Log(client, ObservedSessionLifecycle.Connected)]));
        Assert.Empty(tracker.Slots(null));
        Assert.Empty(tracker.Observe(Scan(true, client), [Log(client, ObservedSessionLifecycle.Connected)]));
        Assert.Equal(new[] { 20 }, Pids(tracker.Slots(null)));
        // A row that could not be read this time still shows the process is running.
        Assert.Empty(tracker.Observe(Scan(true, client with { Problem = "Synthetic access denied" }), [Log(client, ObservedSessionLifecycle.Connected)]));
        Assert.Empty(tracker.Observe(Scan(true, client), [Log(client, ObservedSessionLifecycle.Connected)]));
        // A complete scan that lists more clients than the tracker can hold forgets nothing.
        var crowd = Enumerable.Range(1000, ClientArrivalTracker.MaximumClients).Select(pid => Client(pid, 1) with { Window = 0 });
        Assert.Empty(tracker.Observe(Scan(true, [.. crowd, client with { Window = 0 }]), [Log(client, ObservedSessionLifecycle.Connected)]));
        Assert.Empty(tracker.Observe(Scan(true, client), [Log(client, ObservedSessionLifecycle.Connected)]));
        // Only a complete scan that no longer lists the process at all shows it left, and then the tracker forgets it.
        Assert.Empty(tracker.Observe(Scan(true), []));
        Assert.Equal(new[] { 20 }, Pids(tracker.Observe(Scan(true, client), [Log(client, ObservedSessionLifecycle.Connected)])));
    }

    // N-B5a-4: a minimized or hidden window cannot be moved, so it gets no tile cell. It is still a connected client: once
    // restored it takes its slot again, without arriving a second time.
    [Fact]
    public void SlotsLeaveOutMinimizedAndHiddenClientsUntilTheirWindowsAreRestored()
    {
        var tracker = StartedWithNoClients();
        var normal = Client(30, 3); var minimized = Client(31, 2) with { Minimized = true }; var hidden = Client(32, 1) with { Visible = false };
        Assert.Equal(new[] { 30, 31, 32 }, Pids(tracker.Observe(Scan(true, normal, minimized, hidden), [Log(normal, ObservedSessionLifecycle.Connected),
            Log(minimized, ObservedSessionLifecycle.Connected), Log(hidden, ObservedSessionLifecycle.Connected)])));
        Assert.Equal(new[] { 30 }, Pids(tracker.Slots(null)));
        var restored = minimized with { Minimized = false }; var shown = hidden with { Visible = true };
        Assert.Empty(tracker.Observe(Scan(true, normal, restored, shown), [Log(normal, ObservedSessionLifecycle.Connected),
            Log(restored, ObservedSessionLifecycle.Connected), Log(shown, ObservedSessionLifecycle.Connected)]));
        Assert.Equal(new[] { 30, 31, 32 }, Pids(tracker.Slots(null)));
    }

    // O-C5-4: a request that finds no slot must say why. Slots counts the connected clients it left out because their window
    // is minimized or hidden; no slot and a count of zero means no connected client still has a window at all (each closed,
    // or is listed without one). A client with a window that has not connected yet is neither a slot nor left out.
    [Fact]
    public void SlotsCountTheConnectedClientsLeftOutBecauseTheirWindowIsMinimizedOrHidden()
    {
        var tracker = StartedWithNoClients();
        var normal = Client(33, 3); var minimized = Client(34, 2) with { Minimized = true }; var hidden = Client(35, 1) with { Visible = false };
        Assert.Equal(new[] { 33, 34, 35 }, Pids(tracker.Observe(Scan(true, normal, minimized, hidden), [Log(normal, ObservedSessionLifecycle.Connected),
            Log(minimized, ObservedSessionLifecycle.Connected), Log(hidden, ObservedSessionLifecycle.Connected)])));
        Assert.Equal(new[] { 33 }, Pids(tracker.Slots(null, out int leftOut)));
        Assert.Equal(2, leftOut);
        // Every connected client minimized or hidden: no slot, and the count says they are all still there to restore.
        Assert.Empty(tracker.Observe(Scan(true, normal with { Minimized = true }, minimized, hidden), [Log(normal, ObservedSessionLifecycle.Connected),
            Log(minimized, ObservedSessionLifecycle.Connected), Log(hidden, ObservedSessionLifecycle.Connected)]));
        Assert.Empty(tracker.Slots(null, out leftOut));
        Assert.Equal(3, leftOut);
        // Two closed, one is listed without a window, and a newcomer has not connected yet: no slot, and nothing to restore.
        var newcomer = Client(36, 0.5);
        Assert.Empty(tracker.Observe(Scan(true, minimized with { Window = 0 }, newcomer), [Log(newcomer, ObservedSessionLifecycle.Joining)]));
        Assert.Empty(tracker.Slots(null, out leftOut));
        Assert.Equal(0, leftOut);
    }

    // N-B5a-5: the evidence registry keeps a Connected line for five minutes, so a client the user joined a few minutes before
    // Zenith started reports Connected on Zenith's first scans. Every client in the first complete scan was already running:
    // it never arrives, even when its log reports Connected only later, and Arrange still tiles it with the clients that arrive.
    [Fact]
    public void ClientsAlreadyRunningAtTheFirstCompleteScanNeverArriveButStillTakeTheirSlots()
    {
        var tracker = new ClientArrivalTracker();
        var joinedEarlier = Client(40, 4); var stillLoading = Client(41, 1) with { Window = 0 }; var startedLater = Client(42, 0.5);
        // A failed or partial scan cannot show which clients were already running, so nothing arrives before a complete scan.
        Assert.Empty(tracker.Observe(Scan(false, joinedEarlier), [Log(joinedEarlier, ObservedSessionLifecycle.Connected)]));
        Assert.Empty(tracker.Observe(Scan(true, joinedEarlier, stillLoading), [Log(joinedEarlier, ObservedSessionLifecycle.Connected)]));
        // A client still loading at that scan gets its window and connects afterwards: it was running already, so no arrival.
        var loaded = stillLoading with { Window = 777 };
        Assert.Empty(tracker.Observe(Scan(true, joinedEarlier, loaded), [Log(joinedEarlier, ObservedSessionLifecycle.Connected),
            Log(loaded, ObservedSessionLifecycle.Connected)]));
        Assert.Equal(new[] { 40, 41 }, Pids(tracker.Slots(null)));
        // A client that starts after that scan arrives once, when it first connects, and takes the slot after them.
        Assert.Empty(tracker.Observe(Scan(true, joinedEarlier, loaded, startedLater), [Log(startedLater, ObservedSessionLifecycle.Joining)]));
        Assert.Equal(new[] { 42 }, Pids(tracker.Observe(Scan(true, joinedEarlier, loaded, startedLater),
            [Log(joinedEarlier, ObservedSessionLifecycle.Connected), Log(loaded, ObservedSessionLifecycle.Connected), Log(startedLater, ObservedSessionLifecycle.Connected)])));
        Assert.Equal(new[] { 40, 41, 42 }, Pids(tracker.Slots(null)));
    }

    // C5 review: Windows cannot open another Windows user's client, so on that PC every scan has an unreadable row and is
    // marked incomplete. Such a scan still lists every running client, as FreshClientInventory accepts for arrival mute, so it
    // shows which clients were already running; automatic layout must not stay silently inert for the whole session.
    [Fact]
    public void AScanWhoseOnlyGapIsAPersistentUnreadableRowStillShowsWhichClientsWereAlreadyRunning()
    {
        var tracker = new ClientArrivalTracker();
        var otherUser = Unreadable(50); var joinedEarlier = Client(51, 4); var startedLater = Client(52, 0.5);
        Assert.Empty(tracker.Observe(Partial(otherUser, joinedEarlier), [Log(joinedEarlier, ObservedSessionLifecycle.Connected)]));
        Assert.False(tracker.AwaitingBaseline);
        Assert.Equal(new[] { 51 }, Pids(tracker.Slots(null)));
        Assert.Empty(tracker.Observe(Partial(otherUser, joinedEarlier, startedLater),
            [Log(joinedEarlier, ObservedSessionLifecycle.Connected), Log(startedLater, ObservedSessionLifecycle.Joining)]));
        Assert.Equal(new[] { 52 }, Pids(tracker.Observe(Partial(otherUser, joinedEarlier, startedLater),
            [Log(joinedEarlier, ObservedSessionLifecycle.Connected), Log(startedLater, ObservedSessionLifecycle.Connected)])));
        // Once only: the unreadable row never makes the later client arrive again.
        Assert.Empty(tracker.Observe(Partial(otherUser, joinedEarlier, startedLater),
            [Log(joinedEarlier, ObservedSessionLifecycle.Connected), Log(startedLater, ObservedSessionLifecycle.Connected)]));
        Assert.Equal(new[] { 51, 52 }, Pids(tracker.Slots(null)));
    }

    // A scan with any other gap may have missed a running client, so it is no baseline. While none exists, a client that
    // reports Connected cannot be told from one that was already running: the tracker says it is waiting instead.
    [Fact]
    public void ScansWithOtherGapsAreNoBaselineAndTheTrackerSaysItIsWaitingWhenAClientConnects()
    {
        var tracker = new ClientArrivalTracker();
        var joinedEarlier = Client(53, 4); var otherUser = Unreadable(54);
        var connected = new[] { Log(joinedEarlier, ObservedSessionLifecycle.Connected) };
        Assert.Empty(tracker.Observe(Scan(false, otherUser, joinedEarlier), []));
        Assert.False(tracker.AwaitingBaseline);
        Assert.Empty(tracker.Observe(Scan(false, otherUser, joinedEarlier), connected));
        Assert.True(tracker.AwaitingBaseline);
        // No unreadable row: the gap is something else, for example a client that ended during the scan.
        Assert.Empty(tracker.Observe(Partial(joinedEarlier), connected));
        Assert.True(tracker.AwaitingBaseline);
        // A readable row with a problem, a missing row, or a PID listed twice is not only an unreadable identity.
        Assert.Empty(tracker.Observe(Partial(otherUser, joinedEarlier, Client(55, 1) with { Problem = "Synthetic memory unavailable" }), connected));
        Assert.Empty(tracker.Observe(Partial(otherUser, joinedEarlier, null!), connected));
        Assert.Empty(tracker.Observe(Partial(otherUser, joinedEarlier, Client(53, 2)), connected));
        // A scan that reached its budget may have left clients out.
        var crowd = Enumerable.Range(2000, ClientArrivalTracker.MaximumClients - 2).Select(pid => Client(pid, 1) with { Window = 0 });
        Assert.Empty(tracker.Observe(Partial([.. crowd, otherUser, joinedEarlier]), connected));
        Assert.True(tracker.AwaitingBaseline);
        Assert.Empty(tracker.Slots(null));
        // The first baseline marks every client it lists as already running, so the waiting client never arrives.
        Assert.Empty(tracker.Observe(Partial(otherUser, joinedEarlier), connected));
        Assert.False(tracker.AwaitingBaseline);
        Assert.Equal(new[] { 53 }, Pids(tracker.Slots(null)));
        Assert.Empty(tracker.Observe(Scan(false, joinedEarlier), connected));
        Assert.False(tracker.AwaitingBaseline);
    }

    // A client Windows could not read at the baseline was still running then. When it becomes readable it never arrives,
    // although its key changes from the unreadable row's; a later process that reuses the PID is a new client and arrives.
    [Fact]
    public void AClientUnreadableAtTheBaselineNeverArrivesWhenItBecomesReadableButALaterReusedPidDoes()
    {
        var tracker = new ClientArrivalTracker();
        var joinedEarlier = Client(56, 4); var otherUser = Unreadable(57);
        Assert.Empty(tracker.Observe(Partial(Unreadable(56), otherUser), [Log(joinedEarlier, ObservedSessionLifecycle.Connected)]));
        Assert.Empty(tracker.Observe(Partial(joinedEarlier with { Window = 0 }, otherUser), [Log(joinedEarlier, ObservedSessionLifecycle.Connected)]));
        Assert.Empty(tracker.Observe(Partial(joinedEarlier, otherUser), [Log(joinedEarlier, ObservedSessionLifecycle.Connected)]));
        Assert.Equal(new[] { 56 }, Pids(tracker.Slots(null)));
        // The other user's client ends; a client started after the baseline scan gets its PID.
        var reused = Client(57, -1);
        Assert.Equal(new[] { 57 }, Pids(tracker.Observe(Scan(true, joinedEarlier, reused),
            [Log(joinedEarlier, ObservedSessionLifecycle.Connected), Log(reused, ObservedSessionLifecycle.Connected)])));
        Assert.Equal(new[] { 56, 57 }, Pids(tracker.Slots(null)));
    }

    [Fact]
    public void SlotsFollowLaunchQueueOrderThenProcessStartTimeAndNeverProcessIdFirst()
    {
        var queuedFirst = Client(300, 1); var queuedSecond = Client(200, 2); var outsideOld = Client(50, 10); var outsideNew = Client(40, 0.5);
        var otherInstance = Client(60, 4);
        // The queue's first request started later (a higher-priority request went first), but it keeps the first slot.
        var queue = new LaunchQueueSnapshot(false, [Launched(queuedFirst), Launched(queuedSecond),
            Launched(otherInstance with { ProcessStartUtcTicks = otherInstance.ProcessStartUtcTicks - 1 })]);
        SessionObservation[] clients = [outsideNew, queuedSecond, otherInstance, outsideOld, queuedFirst];
        // A receipt for another instance of a reused PID claims no queue slot; that client falls back to its start time.
        Assert.Equal(new[] { 300, 200, 50, 60, 40 }, Pids(ClientWindowSlotOrder.Order(clients, queue)));
        Assert.Equal(new[] { 50, 60, 200, 300, 40 }, Pids(ClientWindowSlotOrder.Order(clients, null)));
        var sameStartHigh = Client(80, 3); var sameStartLow = Client(70, 3);
        Assert.Equal(new[] { 70, 80 }, Pids(ClientWindowSlotOrder.Order([sameStartHigh, sameStartLow], LaunchQueueSnapshot.Empty)));
        Assert.Empty(ClientWindowSlotOrder.Order([], queue));
    }

    [Fact]
    public void AutomaticLayoutIsOffForSavedSettingsWithoutItAndRejectsUnknownChoices()
    {
        Assert.Equal(ClientArrivalWindowAction.None, new ClientWindowLayoutSettings().Validated().ArrivalAction);
        var older = JsonSerializer.Deserialize<SessionsSettings>("""{"Version":1,"WindowLayout":{"PreferredDisplayId":"synthetic-display"}}""")!.Validated();
        Assert.Equal(ClientArrivalWindowAction.None, older.WindowLayout!.ArrivalAction);
        var saved = new SessionsSettings(WindowLayout: new(ArrivalAction: ClientArrivalWindowAction.Minimize)).Validated();
        Assert.Equal(ClientArrivalWindowAction.Minimize,
            JsonSerializer.Deserialize<SessionsSettings>(JsonSerializer.Serialize(saved))!.Validated().WindowLayout!.ArrivalAction);
        Assert.Throws<InvalidDataException>(() => new ClientWindowLayoutSettings(ArrivalAction: (ClientArrivalWindowAction)9).Validated());
    }
}
