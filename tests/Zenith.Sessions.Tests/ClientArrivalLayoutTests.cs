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
    private static SessionEvidenceSnapshot Log(SessionObservation client, ObservedSessionLifecycle lifecycle, bool ambiguous = false) => new(Guid.NewGuid(),
        new(null, client.ProcessId, client.ProcessStartUtcTicks), client.Window, lifecycle, null, null, null, null,
        SessionEvidenceConfidence.UnverifiedDiagnostic, Now, ambiguous);
    private static int[] Pids(IEnumerable<SessionObservation> clients) => clients.Select(client => client.ProcessId).ToArray();
    private static LaunchOperation Launched(SessionObservation client) => new(new(Guid.NewGuid(), Guid.NewGuid(), 1, 123, 456, 0, Now),
        LaunchState.ClientStartedUnverified, LaunchProblem.None, Now, new(client.ProcessId, client.ProcessStartUtcTicks, Now));

    [Fact]
    public void ASyntheticEvidenceSequenceReportsEachClientOnceWhenItFirstConnects()
    {
        var tracker = new ClientArrivalTracker();
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
        var tracker = new ClientArrivalTracker();
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
        var tracker = new ClientArrivalTracker();
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
        var tracker = new ClientArrivalTracker();
        var normal = Client(30, 3); var minimized = Client(31, 2) with { Minimized = true }; var hidden = Client(32, 1) with { Visible = false };
        Assert.Equal(new[] { 30, 31, 32 }, Pids(tracker.Observe(Scan(true, normal, minimized, hidden), [Log(normal, ObservedSessionLifecycle.Connected),
            Log(minimized, ObservedSessionLifecycle.Connected), Log(hidden, ObservedSessionLifecycle.Connected)])));
        Assert.Equal(new[] { 30 }, Pids(tracker.Slots(null)));
        var restored = minimized with { Minimized = false }; var shown = hidden with { Visible = true };
        Assert.Empty(tracker.Observe(Scan(true, normal, restored, shown), [Log(normal, ObservedSessionLifecycle.Connected),
            Log(restored, ObservedSessionLifecycle.Connected), Log(shown, ObservedSessionLifecycle.Connected)]));
        Assert.Equal(new[] { 30, 31, 32 }, Pids(tracker.Slots(null)));
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
