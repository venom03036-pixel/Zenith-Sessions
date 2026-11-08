using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class RecentClientHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
    private static SessionObservation Session(int pid = 123, long? start = null, DateTimeOffset? time = null)
        => new(pid, start ?? Now.AddSeconds(-10).UtcTicks, "unused-version", 0, "SYNTHETIC_SECRET_TITLE", false, true, 0, 0, null, time ?? Now);
    private static SessionProcessKey Key(SessionObservation value) => new(null, value.ProcessId, value.ProcessStartUtcTicks);
    private static DesktopSnapshot Frame(long revision, DateTimeOffset time, params SessionObservation[] rows)
        => new(new(time, 1, 1, 1, 0, null), rows, ProcessObservedUtc: time, ProcessRevision: revision, ProcessInventoryComplete: true);
    private static SessionEvidenceSnapshot Evidence(SessionObservation session, long user = 900_000_000_000_000_002)
        => new(Guid.NewGuid(), Key(session), 0, ObservedSessionLifecycle.Connected, user,
            new(900_000_000_000_000_001, Guid.Parse("11111111-2222-4333-8444-000000000001"), null, Now), null,
            new(Guid.NewGuid(), 1), SessionEvidenceConfidence.UnverifiedDiagnostic, Now, false, Now,
            SessionLogCorrelationKind.ResourceHolderDiagnostic, Now.AddSeconds(10));

    [Fact]
    public void OnlyNewCompleteInventoriesEndPresenceAndHistoryIsNeverAnExitOrAuthorization()
    {
        var reducer = new RecentClientHistoryReducer(); var session = Session();
        Assert.True(reducer.Observe(Frame(1, Now, session), [Evidence(session)], Now));
        Assert.Equal(0, reducer.PersistedSnapshot().Revision); Assert.Empty(reducer.PersistedSnapshot().Entries);
        var active = Assert.Single(reducer.Snapshot().Active);
        Assert.Equal(Now, active.FirstObservedUtc); Assert.Null(active.NoLongerObservedUtc);
        Assert.Equal(900_000_000_000_000_002, active.LastKnownUserId); Assert.NotNull(active.LastKnownDestination);
        Assert.False(reducer.Observe(Frame(1, Now.AddSeconds(1)), [], Now.AddSeconds(1)));
        Assert.True(reducer.Observe(Frame(2, Now.AddSeconds(1)), [], Now.AddSeconds(1)));
        var ended = Assert.Single(reducer.Snapshot().Ended);
        Assert.Empty(reducer.Snapshot().Active); Assert.Equal("No longer observed", ended.Outcome); Assert.Equal("Unknown", ended.Cause);
        Assert.False(ended.CanAuthorizeAccountAction); Assert.Equal(Now, ended.LastObservedUtc);
        Assert.Equal(Now.AddSeconds(1), ended.NoLongerObservedUtc); Assert.Equal(Now, ended.LastKnownUserIdentityUtc);
        Assert.DoesNotContain("SYNTHETIC_SECRET_TITLE", JsonSerializer.Serialize(reducer.PersistedSnapshot()));
    }

    [Fact]
    public void PartialStaleFutureUnorderedAndMalformedInventoriesCannotRetireAnActiveClient()
    {
        var reducer = new RecentClientHistoryReducer(); var row = Session(); reducer.Observe(Frame(1, Now, row), [], Now);
        var invalid = new[] { Frame(2, Now.AddSeconds(1)) with { ProcessInventoryComplete = false },
            Frame(2, Now) with { Problem = "SYNTHETIC_INVENTORY_FAILURE" },
            Frame(2, Now.AddSeconds(-1)), Frame(2, Now.AddSeconds(2)), Frame(2, Now.AddSeconds(-11)),
            Frame(2, Now) with { ProcessObservedUtc = null }, Frame(2, Now, row, row),
            Frame(2, Now, row with { ProcessId = 0 }), Frame(2, Now, row, row with { ProcessStartUtcTicks = Now.UtcTicks }),
            Frame(2, Now, row with { Problem = "SYNTHETIC_UNHEALTHY" }),
            Frame(2, Now, row with { ObservedUtc = Now.AddSeconds(-9), ProcessStartUtcTicks = Now.AddSeconds(-8).UtcTicks }),
            Frame(2, Now, row with { ObservedUtc = Now.AddSeconds(-11) }) };
        foreach (var frame in invalid)
        {
            Assert.False(reducer.Observe(frame, [], Now)); Assert.Single(reducer.Snapshot().Active); Assert.Empty(reducer.Snapshot().Ended);
        }
        Assert.False(reducer.Observe(Frame(2, Now), [], Now.AddSeconds(11)));
        Assert.True(reducer.Observe(Frame(2, Now.AddSeconds(1)), [], Now.AddSeconds(1)));
    }

    [Fact]
    public void PidReuseRetiresOnlyTheOldStartAndCannotCarryItsReportedIdentity()
    {
        var reducer = new RecentClientHistoryReducer(); var old = Session(); reducer.Observe(Frame(1, Now, old), [Evidence(old)], Now);
        var next = old with { ProcessStartUtcTicks = Now.AddSeconds(-1).UtcTicks, ObservedUtc = Now.AddSeconds(1) };
        reducer.Observe(Frame(2, Now.AddSeconds(1), next), [Evidence(old)], Now.AddSeconds(1));
        Assert.Equal(Key(old), Assert.Single(reducer.Snapshot().Ended).Process);
        var current = Assert.Single(reducer.Snapshot().Active); Assert.Equal(Key(next), current.Process); Assert.Null(current.LastKnownUserId);
        Assert.False(reducer.RecordCloseRequest(Key(old), Now.AddSeconds(1)));
    }

    [Fact]
    public void AmbiguousForeignDuplicateAndNewerFactTimesDoNotBecomeHistoricalIdentity()
    {
        foreach (int mode in Enumerable.Range(0, 8))
        {
            var reducer = new RecentClientHistoryReducer(); var row = Session(); var fact = Evidence(row);
            IReadOnlyList<SessionEvidenceSnapshot> facts = mode switch
            {
                0 => [fact with { Ambiguous = true }], 1 => [fact with { Process = new(null, 456, row.ProcessStartUtcTicks) }],
                2 => [fact, fact], 3 => [fact with { ObservedUtc = Now.AddSeconds(1) }],
                4 => [fact with { Confidence = SessionEvidenceConfidence.Unknown }], 5 => [fact with { Confidence = (SessionEvidenceConfidence)999 }],
                6 => [fact with { Stream = null }], _ => [fact with { ObservedUtc = Now.AddMinutes(-1) }]
            };
            reducer.Observe(Frame(1, Now, row), facts, Now); var entry = Assert.Single(reducer.Snapshot().Active);
            Assert.Null(entry.LastKnownUserId); Assert.Null(entry.LastKnownDestination);
        }
    }

    [Fact]
    public void CloseRequestIsSeparateFromDisappearanceAndClearDoesNotReplayOldFrames()
    {
        var reducer = new RecentClientHistoryReducer(); var row = Session(); reducer.Observe(Frame(1, Now, row), [], Now);
        Assert.True(reducer.RecordCloseRequest(Key(row), Now.AddMilliseconds(500)));
        Assert.Single(reducer.Snapshot().Active); Assert.Empty(reducer.Snapshot().Ended);
        reducer.Observe(Frame(2, Now.AddSeconds(1)), [], Now.AddSeconds(1));
        Assert.Equal(Now.AddMilliseconds(500), Assert.Single(reducer.Snapshot().Ended).CloseRequestedUtc);
        Assert.True(reducer.ClearEnded()); long cleared = reducer.PersistedSnapshot().Revision;
        Assert.False(reducer.Observe(Frame(1, Now, row), [], Now.AddSeconds(1)));
        Assert.False(reducer.Observe(Frame(2, Now.AddSeconds(1)), [], Now.AddSeconds(1)));
        Assert.Empty(reducer.Snapshot().Ended); Assert.Equal(cleared, reducer.PersistedSnapshot().Revision);
        var next = Session(456, time: Now.AddSeconds(2)); reducer.Observe(Frame(3, Now.AddSeconds(2), next), [], Now.AddSeconds(2));
        Assert.False(reducer.ClearEnded()); Assert.Single(reducer.Snapshot().Active);
    }

    [Fact]
    public void RestartRestoresOnlyEndedHistoryAndSnapshotsAreImmutableAndBounded()
    {
        var reducer = new RecentClientHistoryReducer(); long revision = 1;
        for (int index = 0; index < 150; index++)
        {
            var time = Now.AddSeconds(index * 2); reducer.Observe(Frame(revision++, time, Session(1000 + index, time: time)), [], time);
            reducer.Observe(Frame(revision++, time.AddSeconds(1)), [], time.AddSeconds(1));
        }
        Assert.Equal(100, reducer.Snapshot().Ended.Count);
        var saved = reducer.PersistedSnapshot(); var restarted = new RecentClientHistoryReducer(); restarted.Restore(saved);
        Assert.Empty(restarted.Snapshot().Active); Assert.Equal(saved.Entries, restarted.Snapshot().Ended);
        Assert.Throws<NotSupportedException>(() => ((IList<RecentClientHistoryEntry>)saved.Entries).Clear());
        Assert.Throws<InvalidOperationException>(() => restarted.Restore(saved));
        var tooMany = Enumerable.Range(1, 129).Select(pid => Session(pid)).ToArray();
        Assert.False(new RecentClientHistoryReducer().Observe(Frame(1, Now, tooMany), [], Now));
    }

    [Fact]
    public void ClearRetainsOtherActiveClientsAndTheirLaterDisappearanceIsANewEndedObservation()
    {
        var reducer = new RecentClientHistoryReducer(); var first = Session(); var second = Session(456);
        reducer.Observe(Frame(1, Now, first, second), [], Now);
        reducer.Observe(Frame(2, Now.AddSeconds(1), second with { ObservedUtc = Now.AddSeconds(1) }), [], Now.AddSeconds(1));
        Assert.Equal(Key(first), Assert.Single(reducer.Snapshot().Ended).Process);
        Assert.True(reducer.ClearEnded()); Assert.Equal(Key(second), Assert.Single(reducer.Snapshot().Active).Process);
        reducer.Observe(Frame(3, Now.AddSeconds(2)), [], Now.AddSeconds(2));
        Assert.Equal(Key(second), Assert.Single(reducer.Snapshot().Ended).Process);
    }

    [Fact]
    public void AnExactIdentityRediscoveredAfterAbsenceIsActiveAgainWithoutDuplicatingEndedHistory()
    {
        var reducer = new RecentClientHistoryReducer(); var row = Session();
        reducer.Observe(Frame(1, Now, row), [], Now); var id = Assert.Single(reducer.Snapshot().Active).EntryId;
        reducer.Observe(Frame(2, Now.AddSeconds(1)), [], Now.AddSeconds(1));
        reducer.Observe(Frame(3, Now.AddSeconds(2), row with { ObservedUtc = Now.AddSeconds(2) }), [], Now.AddSeconds(2));
        Assert.Empty(reducer.Snapshot().Ended); Assert.Equal(id, Assert.Single(reducer.Snapshot().Active).EntryId);
        Assert.Equal(Now, Assert.Single(reducer.Snapshot().Active).FirstObservedUtc);
    }

    [Fact]
    public void InventoryCompletionDoesNotRenewPerProcessPresenceOrFutureDiagnosticFactTimes()
    {
        var reducer = new RecentClientHistoryReducer(); var row = Session();
        var futureFact = Evidence(row) with { UserIdentityObservedUtc = Now.AddMilliseconds(500),
            CurrentDestination = Evidence(row).CurrentDestination! with { ObservedUtc = Now.AddMilliseconds(500) } };
        reducer.Observe(Frame(1, Now.AddSeconds(1), row), [futureFact], Now.AddSeconds(1));
        var first = Assert.Single(reducer.Snapshot().Active);
        Assert.Equal(Now, first.FirstObservedUtc); Assert.Equal(Now, first.LastObservedUtc);
        Assert.Null(first.LastKnownUserId); Assert.Null(first.LastKnownDestination);
        reducer.Observe(Frame(2, Now.AddSeconds(2), row), [], Now.AddSeconds(2));
        Assert.Equal(Now, Assert.Single(reducer.Snapshot().Active).LastObservedUtc);
    }

    [Fact]
    public void CloseRequestStartedBeforeANewerSampleStillRecordsItsOriginalRequestTime()
    {
        var reducer = new RecentClientHistoryReducer(); var row = Session();
        reducer.Observe(Frame(1, Now, row), [], Now);
        reducer.Observe(Frame(2, Now.AddSeconds(1), row with { ObservedUtc = Now.AddSeconds(1) }), [], Now.AddSeconds(1));
        Assert.True(reducer.RecordCloseRequest(Key(row), Now.AddMilliseconds(500)));
        Assert.Equal(Now.AddMilliseconds(500), Assert.Single(reducer.Snapshot().Active).CloseRequestedUtc);
    }

    [Fact]
    public void EndCauseNamesTheStrongestRecordedEvidenceAndACloseRequestAloneStaysUnknown()
    {
        var key = new SessionProcessKey(null, 123, Now.AddSeconds(-10).UtcTicks);
        var ended = new RecentClientHistoryEntry(Guid.NewGuid(), key, Now, Now, Now.AddSeconds(1));
        var check = new ClientFaultCheck(Now.AddSeconds(7), true);
        Assert.Equal(ClientEndCause.Unknown, ended.EndCause); Assert.Equal("Unknown", ended.Cause);
        Assert.Equal(ClientEndCause.Unknown, (ended with { CloseRequestedUtc = Now }).EndCause);
        Assert.Equal(ClientEndCause.Unknown, (ended with { FaultCheck = check }).EndCause);
        Assert.Equal(ClientEndCause.PossibleCrash, (ended with { FaultCheck = check with { CrashDumps = 1 } }).EndCause);
        var disconnected = ended with { DisconnectedUtc = Now, DisconnectReason = 277, FaultCheck = check with { CrashDumps = 1 } };
        Assert.Equal(ClientEndCause.Disconnected, disconnected.EndCause); Assert.Equal("Disconnected", disconnected.Cause);
        Assert.Equal(ClientEndCause.LeftGame, (disconnected with { DisconnectReason = 285 }).EndCause);
        // Reason 273 is the same account launching in another client, not this player leaving.
        var elsewhere = disconnected with { DisconnectReason = 273 };
        Assert.Equal(ClientEndCause.LaunchedElsewhere, elsewhere.EndCause); Assert.Equal("Account launched elsewhere", elsewhere.Cause);
        var hung = disconnected with { NotRespondingUtc = Now };
        Assert.Equal(ClientEndCause.NotResponding, hung.EndCause);
        var crashed = hung with { FaultCheck = check with { FaultRecordedUtc = Now.AddSeconds(1), ExceptionCode = 0xC0000005 } };
        Assert.Equal(ClientEndCause.Crashed, crashed.EndCause); Assert.Equal("Crashed", crashed.Cause);
        Assert.Same(crashed, crashed.Validated(ended: true)); Assert.False(crashed.CanAuthorizeAccountAction);
        Assert.Equal(ClientEndCause.Unknown, (crashed with { NoLongerObservedUtc = null, FaultCheck = null }).EndCause);
        // Inconsistent evidence is rejected instead of being shown as a cause.
        Assert.Throws<ArgumentException>(() => (ended with { DisconnectReason = 277 }).Validated(ended: true));
        Assert.Throws<ArgumentException>(() => (ended with { NotRespondingUtc = Now.AddSeconds(1) }).Validated(ended: true));
        Assert.Throws<ArgumentException>(() => (ended with { FaultCheck = check with { FaultRecordedUtc = Now.AddMinutes(-1) } }).Validated(ended: true));
        Assert.Throws<ArgumentException>(() => (ended with { FaultCheck = check with { ExceptionCode = 1 } }).Validated(ended: true));
        Assert.Throws<ArgumentException>(() => (ended with { FaultCheck = check with { CheckedUtc = Now } }).Validated(ended: true));
        Assert.Throws<ArgumentException>(() => (ended with { NoLongerObservedUtc = null, FaultCheck = check }).Validated(ended: false));
    }

    [Fact]
    public void TheLastObservedHangAndLoggedDisconnectCarryIntoTheEndedEntry()
    {
        var reducer = new RecentClientHistoryReducer(); var hung = Session(); var dropped = Session(456);
        var disconnect = Evidence(dropped) with { Lifecycle = ObservedSessionLifecycle.Disconnected, CurrentDestination = null, DisconnectReason = 277 };
        reducer.Observe(Frame(1, Now, hung with { NotResponding = true }, dropped), [disconnect], Now);
        reducer.Observe(Frame(2, Now.AddSeconds(1)), [], Now.AddSeconds(1));
        var ended = reducer.Snapshot().Ended;
        var frozen = ended.Single(item => item.Process == Key(hung));
        Assert.Equal(ClientEndCause.NotResponding, frozen.EndCause); Assert.Equal(Now, frozen.NotRespondingUtc);
        var gone = ended.Single(item => item.Process == Key(dropped));
        Assert.Equal(ClientEndCause.Disconnected, gone.EndCause); Assert.Equal(277, gone.DisconnectReason); Assert.Equal(Now, gone.DisconnectedUtc);

        // A client that answered again and rejoined before it ended carries no hang or disconnect.
        var recovered = new RecentClientHistoryReducer(); var row = Session(789);
        recovered.Observe(Frame(1, Now, row with { NotResponding = true }),
            [Evidence(row) with { Lifecycle = ObservedSessionLifecycle.Disconnected, CurrentDestination = null, DisconnectReason = 277 }], Now);
        Assert.Equal(Now, Assert.Single(recovered.Snapshot().Active).DisconnectedUtc);
        recovered.Observe(Frame(2, Now.AddSeconds(1), row with { ObservedUtc = Now.AddSeconds(1) }), [Evidence(row)], Now.AddSeconds(1));
        recovered.Observe(Frame(3, Now.AddSeconds(2)), [], Now.AddSeconds(2));
        var later = Assert.Single(recovered.Snapshot().Ended);
        Assert.Null(later.NotRespondingUtc); Assert.Null(later.DisconnectedUtc); Assert.Null(later.DisconnectReason);
        Assert.Equal(ClientEndCause.Unknown, later.EndCause);
    }

    [Fact]
    public void EachEndedClientGetsOneSettledCrashRecordCheckThatIsPersisted()
    {
        var reducer = new RecentClientHistoryReducer(); var row = Session();
        reducer.Observe(Frame(1, Now, row), [], Now);
        var gone = Now.AddSeconds(1); reducer.Observe(Frame(2, gone), [], gone);
        Assert.Null(reducer.NextFaultCheck(gone.AddSeconds(4)));
        Assert.Null(reducer.NextFaultCheck(gone + RecentClientHistoryReducer.MaximumFaultCheckAge + TimeSpan.FromSeconds(1)));
        var due = gone + RecentClientHistoryReducer.FaultCheckDelay;
        var ended = reducer.NextFaultCheck(due); Assert.NotNull(ended); Assert.Equal(Key(row), ended!.Process);
        long revision = reducer.PersistedSnapshot().Revision;
        Assert.Throws<ArgumentException>(() => reducer.RecordFaultCheck(ended, new(due, true, Now.AddMinutes(-5), 0xC0000005)));
        Assert.Equal(revision, reducer.PersistedSnapshot().Revision);
        var found = new ClientFaultCheck(due, true, Now, 0xC0000005);
        Assert.True(reducer.RecordFaultCheck(ended, found));
        Assert.Equal(revision + 1, reducer.PersistedSnapshot().Revision);
        var saved = Assert.Single(reducer.PersistedSnapshot().Entries);
        Assert.Equal(found, saved.FaultCheck); Assert.Equal(ClientEndCause.Crashed, saved.EndCause);
        Assert.Null(reducer.NextFaultCheck(due)); Assert.False(reducer.RecordFaultCheck(ended, found));

        // Reappearance drops the check; a late result for the earlier end cannot attach to the next one.
        var back = Now.AddSeconds(8); reducer.Observe(Frame(3, back, row with { ObservedUtc = back }), [], back);
        Assert.Null(Assert.Single(reducer.Snapshot().Active).FaultCheck);
        var again = back.AddSeconds(1); reducer.Observe(Frame(4, again), [], again);
        Assert.False(reducer.RecordFaultCheck(ended, found with { CheckedUtc = again.AddSeconds(5) }));
        Assert.NotNull(reducer.NextFaultCheck(again + RecentClientHistoryReducer.FaultCheckDelay));
    }

    [Fact]
    public void AForcedEndIsRecordedForTheExactActiveClientAndNamesTheCauseAboveEveryOtherEvidence()
    {
        // R-instances-12: a confirmed force close is the one end Zenith caused itself, so it outranks a hang, a disconnect
        // and a fault record (TerminateProcess leaves none; any other evidence describes the state before it).
        var reducer = new RecentClientHistoryReducer(); var row = Session(); reducer.Observe(Frame(1, Now, row), [], Now);
        Assert.False(reducer.RecordForcedEnd(Key(Session(456)), Now.AddSeconds(1)));
        Assert.False(reducer.RecordForcedEnd(Key(row), Now.AddSeconds(-11)));
        Assert.False(reducer.RecordForcedEnd(Key(row), Now.AddSeconds(11)));
        Assert.True(reducer.RecordForcedEnd(Key(row), Now.AddSeconds(1)));
        Assert.False(reducer.RecordForcedEnd(Key(row), Now.AddSeconds(1)));
        var active = Assert.Single(reducer.Snapshot().Active);
        Assert.Equal(Now.AddSeconds(1), active.ForcedEndUtc); Assert.Null(active.CloseRequestedUtc);
        Assert.Equal(ClientEndCause.Unknown, active.EndCause); // Still observed: not ended yet.
        reducer.Observe(Frame(2, Now.AddSeconds(2)), [], Now.AddSeconds(2));
        var ended = Assert.Single(reducer.Snapshot().Ended);
        Assert.Equal(ClientEndCause.ForcedEnd, ended.EndCause); Assert.Equal("Ended by force from Zenith", ended.Cause);
        var check = new ClientFaultCheck(Now.AddSeconds(9), true, Now.AddSeconds(1), 0xC0000005, 1);
        var everything = ended with { NotRespondingUtc = Now, DisconnectedUtc = Now, DisconnectReason = 277, FaultCheck = check, CloseRequestedUtc = Now };
        Assert.Equal(ClientEndCause.ForcedEnd, everything.Validated(ended: true).EndCause);
        Assert.Equal(ClientEndCause.Crashed, (everything with { ForcedEndUtc = null }).EndCause);
        Assert.False(ended.CanAuthorizeAccountAction);
        Assert.DoesNotContain("SYNTHETIC_SECRET_TITLE", JsonSerializer.Serialize(reducer.PersistedSnapshot()));
        // A forced end before the process started, or long after it was last seen, is inconsistent evidence.
        Assert.Throws<ArgumentException>(() => (ended with { ForcedEndUtc = Now.AddSeconds(-11) }).Validated(ended: true));
        Assert.Throws<ArgumentException>(() => (ended with { ForcedEndUtc = Now.AddSeconds(13) }).Validated(ended: true));
    }

    [Fact]
    public void AForcedEndRecordedJustAfterTheScanRetiredTheClientStillReachesItsEndedEntry()
    {
        // The termination and the scan that no longer sees the client race on different threads; the record must not be lost.
        var reducer = new RecentClientHistoryReducer(); var row = Session(); var other = Session(456);
        reducer.Observe(Frame(1, Now, row, other), [], Now);
        reducer.Observe(Frame(2, Now.AddSeconds(1)), [], Now.AddSeconds(1));
        long before = reducer.PersistedSnapshot().Revision;
        Assert.True(reducer.RecordForcedEnd(Key(row), Now.AddMilliseconds(900)));
        var ended = Assert.Single(reducer.Snapshot().Ended, item => item.Process == Key(row));
        Assert.Equal(Now.AddMilliseconds(900), ended.ForcedEndUtc); Assert.Equal(ClientEndCause.ForcedEnd, ended.EndCause);
        Assert.Equal(before + 1, reducer.PersistedSnapshot().Revision); // Persisted history changed.
        Assert.False(reducer.RecordForcedEnd(Key(row), Now.AddMilliseconds(900)));
        // An entry that ended long ago is not rewritten by a late record.
        Assert.False(reducer.RecordForcedEnd(Key(other), Now.AddSeconds(30)));
        Assert.Null(Assert.Single(reducer.Snapshot().Ended, item => item.Process == Key(other)).ForcedEndUtc);
    }
}
