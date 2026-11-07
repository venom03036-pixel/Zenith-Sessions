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
}
