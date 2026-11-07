using Zenith.Sessions.Core;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class SessionEvidenceTests
{
    private static readonly DateTimeOffset Now = SyntheticClientLogFixtures.Time;
    private static SessionProcessKey Key(int pid = 41, string? host = null, int ageSeconds = 60) => new(host, pid, Now.AddSeconds(-ageSeconds).UtcTicks);
    private static ClientLogStreamId Stream(long generation = 1, Guid? reader = null) => new(reader ?? Guid.NewGuid(), generation);
    private static SessionLogCorroboration Proof(SessionProcessKey key, ClientLogStreamId stream,
        SessionLogCorrelationKind kind = SessionLogCorrelationKind.IndependentlyCorroborated) => new(key, stream, kind, Guid.NewGuid(), Now);
    private static ClientLogObservation Observation(ClientLogStreamId stream, ClientLogFact fact, long offset = 0) => new(stream,
        offset, offset + 10, Now, Now, fact,
        UserId: fact == ClientLogFact.UserIdentityObserved ? SyntheticClientLogFixtures.User : null,
        PlaceId: fact == ClientLogFact.Joining ? SyntheticClientLogFixtures.Place : null,
        JobId: fact == ClientLogFact.Joining ? SyntheticClientLogFixtures.Job : null);

    private static ClientLogBatch Batch(ClientLogStreamId stream, long sequence, long offset, params ClientLogObservation[] facts)
        => new(stream, ClientLogReadStatus.Ready, ClientLogDiscontinuity.None, offset, offset, false, 0, facts)
        { ReadSequence = sequence };

    [Fact]
    public void TimingManualLabelAndUnassociatedLogCannotBindOrAuthorizeAProcess()
    {
        var registry = new SessionEvidenceRegistry(); var key = Key(); var stream = Stream();
        Assert.True(registry.RegisterProcess(key, Now));
        Assert.False(registry.CorroborateLog(Proof(key, stream, SessionLogCorrelationKind.TimingOnly), Now));
        Assert.False(registry.CorroborateLog(Proof(key, stream, SessionLogCorrelationKind.ManualLabel), Now));
        Assert.False(registry.Apply(Observation(stream, ClientLogFact.UserIdentityObserved), Now));
        var state = Assert.Single(registry.Snapshot(Now));
        Assert.Null(state.ObservedUserId); Assert.Null(state.CurrentDestination); Assert.False(state.CanAuthorizeAccountAction);
    }
    [Theory]
    [InlineData(ClientLogFact.Joining)]
    [InlineData(ClientLogFact.Connected)]
    [InlineData(ClientLogFact.UserIdentityObserved)]
    public void ReadingUndatedHistoricalBytesCannotMakeThemCurrentPositiveEvidence(ClientLogFact fact)
    {
        var registry=new SessionEvidenceRegistry();var key=Key();var stream=Stream();registry.RegisterProcess(key,Now);registry.CorroborateLog(Proof(key,stream),Now);
        Assert.False(registry.Apply(Observation(stream,fact) with {LoggedUtc=null},Now));
        var state=Assert.Single(registry.Snapshot(Now));Assert.Null(state.CurrentDestination);Assert.Null(state.ObservedUserId);Assert.Equal(ObservedSessionLifecycle.Unknown,state.Lifecycle);
    }
    [Fact]
    public void ProcessLivenessAndFreshIdentityLinesCannotRenewAnOldServerClaim()
    {
        var registry=new SessionEvidenceRegistry();var key=Key();var stream=Stream();registry.RegisterProcess(key,Now);registry.CorroborateLog(Proof(key,stream),Now);
        registry.Apply(Observation(stream,ClientLogFact.Joining),Now);registry.Apply(Observation(stream,ClientLogFact.Connected,10),Now);
        var later=Now.AddMinutes(4);registry.RegisterProcess(key,later);
        Assert.True(registry.Apply(Observation(stream,ClientLogFact.UserIdentityObserved,20) with {LoggedUtc=later,ReadUtc=later},later));
        var state=Assert.Single(registry.Snapshot(Now.AddMinutes(6)));
        Assert.Null(state.CurrentDestination);Assert.Equal(SyntheticClientLogFixtures.Job,state.LastKnownDestination!.JobId);
        Assert.Equal(Now,state.LastKnownDestination.ObservedUtc);Assert.Equal(ObservedSessionLifecycle.Unknown,state.Lifecycle);Assert.Null(state.Stream);
    }
    [Fact]
    public void IdentityEvidenceExpiresEvenWhenTheObservedProcessStillExists()
    {
        var registry=new SessionEvidenceRegistry();var key=Key();var stream=Stream();registry.RegisterProcess(key,Now);registry.CorroborateLog(Proof(key,stream),Now);
        registry.Apply(Observation(stream,ClientLogFact.UserIdentityObserved),Now);registry.RegisterProcess(key,Now.AddMinutes(6));
        var state=Assert.Single(registry.Snapshot(Now.AddMinutes(6)));Assert.Null(state.ObservedUserId);Assert.Null(state.UserIdentityObservedUtc);
    }

    [Fact]
    public void CurrentAndTimestampedLastKnownDestinationAreDistinctDuringTeleportAndDisconnect()
    {
        var registry = new SessionEvidenceRegistry(); var key = Key(); var stream = Stream();
        registry.RegisterProcess(key, Now); Assert.True(registry.CorroborateLog(Proof(key, stream), Now));
        Assert.True(registry.Apply(Observation(stream, ClientLogFact.Joining), Now));
        Assert.Null(Assert.Single(registry.Snapshot(Now)).CurrentDestination);
        Assert.True(registry.Apply(Observation(stream, ClientLogFact.Connected, 10), Now));
        Assert.Equal(SyntheticClientLogFixtures.Job, Assert.Single(registry.Snapshot(Now)).CurrentDestination!.JobId);
        Assert.True(registry.Apply(Observation(stream, ClientLogFact.Connected, 20), Now));
        Assert.NotNull(Assert.Single(registry.Snapshot(Now)).CurrentDestination);
        Assert.True(registry.Apply(Observation(stream, ClientLogFact.Teleporting, 30), Now));
        var teleporting = Assert.Single(registry.Snapshot(Now));
        Assert.Equal(ObservedSessionLifecycle.Teleporting, teleporting.Lifecycle); Assert.Null(teleporting.CurrentDestination);
        Assert.Equal(SyntheticClientLogFixtures.Job, teleporting.LastKnownDestination!.JobId);
        Assert.Equal(Now, teleporting.LastKnownDestination.ObservedUtc);
        Assert.True(registry.Apply(Observation(stream, ClientLogFact.Disconnected, 40), Now));
        var disconnected = Assert.Single(registry.Snapshot(Now));
        Assert.Equal(ObservedSessionLifecycle.Disconnected, disconnected.Lifecycle); Assert.Null(disconnected.CurrentDestination);
        Assert.Equal(teleporting.LastKnownDestination, disconnected.LastKnownDestination);
        Assert.Equal(SessionEvidenceConfidence.UnverifiedDiagnostic, disconnected.Confidence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialCoveragePreservesReducerStateWithoutPublishingCurrentClaims(bool unfinishedLine)
    {
        var registry = new SessionEvidenceRegistry();
        var key = Key();
        var stream = Stream();
        registry.RegisterProcess(key, Now);
        registry.CorroborateLog(Proof(key, stream), Now);
        var joined = Observation(stream, ClientLogFact.Joining);
        var connected = Observation(stream, ClientLogFact.Connected, 10);
        var identity = Observation(stream, ClientLogFact.UserIdentityObserved, 20);
        var partial = Batch(stream, 1, 30, joined, connected, identity) with
        { HasMore = !unfinishedLine, ReadOffset = unfinishedLine ? 34 : 30 };

        Assert.Equal(3, registry.ApplyBatch(partial, Now));
        var pending = Assert.Single(registry.Snapshot(Now));
        Assert.True(pending.AwaitingLogTail);
        Assert.Equal(ObservedSessionLifecycle.Unknown, pending.Lifecycle);
        Assert.Null(pending.CurrentDestination);
        Assert.Null(pending.ObservedUserId);
        Assert.Equal(SyntheticClientLogFixtures.Job, pending.LastKnownDestination?.JobId);
        Assert.Equal(identity.UserId, pending.LastKnownUserId);

        Assert.Equal(0, registry.ApplyBatch(Batch(stream, 2, 40), Now.AddSeconds(1)));
        var complete = Assert.Single(registry.Snapshot(Now.AddSeconds(1)));
        Assert.False(complete.AwaitingLogTail);
        Assert.Equal(ObservedSessionLifecycle.Connected, complete.Lifecycle);
        Assert.Equal(SyntheticClientLogFixtures.Job, complete.CurrentDestination?.JobId);
        Assert.Equal(Now, complete.CurrentDestination?.ObservedUtc);
        Assert.Equal(identity.UserId, complete.ObservedUserId);
    }

    [Fact]
    public void OlderBatchesCannotClearPendingCoverageOrUndoACompleteTail()
    {
        var registry = new SessionEvidenceRegistry();
        var key = Key();
        var stream = Stream();
        registry.RegisterProcess(key, Now);
        registry.CorroborateLog(Proof(key, stream), Now);
        var original = Batch(stream, 1, 20, Observation(stream, ClientLogFact.Joining),
            Observation(stream, ClientLogFact.Connected, 10));
        Assert.Equal(2, registry.ApplyBatch(original, Now));
        var pending = Batch(stream, 2, 20) with { HasMore = true };
        Assert.Equal(0, registry.ApplyBatch(pending, Now));
        Assert.Equal(0, registry.ApplyBatch(original, Now));
        Assert.Equal(0, registry.ApplyBatch(Batch(stream, 2, 20), Now));
        Assert.Null(Assert.Single(registry.Snapshot(Now)).CurrentDestination);

        var complete = Batch(stream, 3, 20);
        Assert.Equal(0, registry.ApplyBatch(complete, Now));
        Assert.Equal(0, registry.ApplyBatch(pending, Now));
        var state = Assert.Single(registry.Snapshot(Now));
        Assert.False(state.AwaitingLogTail);
        Assert.NotNull(state.CurrentDestination);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplayFloorDetectsMissedTransitionsWithoutDiscardingAlreadyConsumedCoverage(bool missed)
    {
        var registry = new SessionEvidenceRegistry();
        var key = Key();
        var stream = Stream();
        registry.RegisterProcess(key, Now);
        registry.CorroborateLog(Proof(key, stream), Now);
        Assert.Equal(2, registry.ApplyBatch(Batch(stream, 1, 20, Observation(stream, ClientLogFact.Joining),
            Observation(stream, ClientLogFact.Connected, 10)), Now));
        var next = Batch(stream, 3, 40, Observation(stream, ClientLogFact.Connected, 30)) with
        { ReplayFloorOffset = missed ? 30 : 20 };
        Assert.Equal(1, registry.ApplyBatch(next, Now));
        var state = Assert.Single(registry.Snapshot(Now));
        if (missed)
        {
            Assert.Null(state.CurrentDestination);
            Assert.Equal(SyntheticClientLogFixtures.Job, state.LastKnownDestination?.JobId);
        }
        else Assert.Equal(SyntheticClientLogFixtures.Job, state.CurrentDestination?.JobId);
    }

    [Fact]
    public void ReplayGapDoesNotPermitAConflictingPlayerOnTheSameLogStream()
    {
        var registry = new SessionEvidenceRegistry();
        var key = Key();
        var stream = Stream();
        registry.RegisterProcess(key, Now);
        registry.CorroborateLog(Proof(key, stream), Now);
        var identity = Observation(stream, ClientLogFact.UserIdentityObserved);
        Assert.Equal(1, registry.ApplyBatch(Batch(stream, 1, 10, identity), Now));
        var conflict = Observation(stream, ClientLogFact.UserIdentityObserved, 20) with
        { UserId = SyntheticClientLogFixtures.User + 1 };
        Assert.Equal(0, registry.ApplyBatch(Batch(stream, 3, 30, conflict) with { ReplayFloorOffset = 20 }, Now));
        var state = Assert.Single(registry.Snapshot(Now));
        Assert.True(state.Ambiguous);
        Assert.Null(state.ObservedUserId);
        Assert.Equal(identity.UserId, state.LastKnownUserId);
    }

    [Fact]
    public void InvalidCoverageCannotAdvanceTheAcceptedByteOrSequenceWatermark()
    {
        var registry = new SessionEvidenceRegistry();
        var key = Key();
        var stream = Stream();
        registry.RegisterProcess(key, Now);
        registry.CorroborateLog(Proof(key, stream), Now);
        Assert.Equal(1, registry.ApplyBatch(Batch(stream, 1, 10, Observation(stream, ClientLogFact.Joining))
            with { HasMore = true }, Now));
        var invalid = Batch(stream, 100, 20, Observation(stream, ClientLogFact.Connected, 20));
        Assert.Equal(0, registry.ApplyBatch(invalid, Now));
        Assert.True(Assert.Single(registry.Snapshot(Now)).AwaitingLogTail);
        Assert.Equal(1, registry.ApplyBatch(Batch(stream, 2, 20, Observation(stream, ClientLogFact.Connected, 10)), Now));
        Assert.NotNull(Assert.Single(registry.Snapshot(Now)).CurrentDestination);
    }

    [Fact]
    public void SharedLogOwnershipIsAmbiguousForBothProcessesInsteadOfSwappingAccounts()
    {
        var registry = new SessionEvidenceRegistry(); var first = Key(); var second = Key(42); var stream = Stream();
        registry.RegisterProcess(first, Now); registry.RegisterProcess(second, Now);
        Assert.True(registry.CorroborateLog(Proof(first, stream), Now));
        Assert.True(registry.Apply(Observation(stream, ClientLogFact.UserIdentityObserved), Now));
        Assert.False(registry.CorroborateLog(Proof(second, stream), Now));
        Assert.All(registry.Snapshot(Now), state => { Assert.True(state.Ambiguous); Assert.Null(state.ObservedUserId); Assert.Null(state.Stream); });
        Assert.False(registry.Apply(Observation(stream, ClientLogFact.Connected, 10), Now));
        Assert.False(registry.CorroborateLog(Proof(first, stream), Now));
    }

    [Fact]
    public void CompetingLogSourcesForOneProcessRemainUnknown()
    {
        var registry = new SessionEvidenceRegistry(); var key = Key(); var first = Stream(); var second = Stream();
        registry.RegisterProcess(key, Now); registry.CorroborateLog(Proof(key, first), Now);
        Assert.False(registry.CorroborateLog(Proof(key, second), Now));
        Assert.True(Assert.Single(registry.Snapshot(Now)).Ambiguous);
        Assert.False(registry.Apply(Observation(first, ClientLogFact.Connected), Now));
        Assert.False(registry.Apply(Observation(second, ClientLogFact.Connected), Now));
    }

    [Fact]
    public void OutputJoinWhileConnectedCannotReplaceAnObservedServerWithoutATransition()
    {
        var registry = new SessionEvidenceRegistry(); var key = Key(); var stream = Stream();
        registry.RegisterProcess(key, Now); registry.CorroborateLog(Proof(key, stream), Now);
        registry.Apply(Observation(stream, ClientLogFact.Joining), Now);
        registry.Apply(Observation(stream, ClientLogFact.Connected, 10), Now);
        Assert.False(registry.Apply(Observation(stream, ClientLogFact.Joining, 20) with { PlaceId = 999, JobId = Guid.NewGuid() }, Now));
        var state = Assert.Single(registry.Snapshot(Now));
        Assert.True(state.Ambiguous); Assert.Null(state.CurrentDestination);
        Assert.Equal(SyntheticClientLogFixtures.Job, state.LastKnownDestination!.JobId);
    }

    [Fact]
    public void ReplacementRequiresNewCorroborationAndRejectsAllOlderGenerations()
    {
        var registry = new SessionEvidenceRegistry(); var key = Key(); var first = Stream(); var next = first with { Generation = 2 };
        registry.RegisterProcess(key, Now); registry.CorroborateLog(Proof(key, first), Now);
        registry.Apply(Observation(first, ClientLogFact.UserIdentityObserved), Now);
        var batch = new ClientLogBatch(next, ClientLogReadStatus.Ready, ClientLogDiscontinuity.Replaced, 10, 10, false, 0,
            new[] { Observation(next, ClientLogFact.Connected) });
        Assert.Equal(0, registry.ApplyBatch(batch, Now)); Assert.Null(Assert.Single(registry.Snapshot(Now)).ObservedUserId);
        Assert.False(registry.CorroborateLog(Proof(key, first), Now));
        Assert.False(registry.Apply(Observation(first, ClientLogFact.Connected, 10), Now));
        Assert.True(registry.CorroborateLog(Proof(key, next), Now));
        Assert.Equal(1, registry.ApplyBatch(batch, Now));
        Assert.Equal(0, registry.ApplyBatch(batch with { Stream = first, Status = ClientLogReadStatus.Missing }, Now));
        Assert.Equal(next, Assert.Single(registry.Snapshot(Now)).Stream);
    }

    [Fact]
    public void OutOfOrderDuplicateStaleFutureAndMalformedEventsAreRejected()
    {
        var registry = new SessionEvidenceRegistry(); var key = Key(ageSeconds: 600); var stream = Stream();
        registry.RegisterProcess(key, Now); registry.CorroborateLog(Proof(key, stream), Now);
        var identity = Observation(stream, ClientLogFact.UserIdentityObserved, 20);
        Assert.True(registry.Apply(identity, Now)); Assert.False(registry.Apply(identity, Now));
        Assert.False(registry.Apply(Observation(stream, ClientLogFact.Connected), Now));
        Assert.False(registry.Apply(identity with { StartOffset = 30, EndOffset = 40, LoggedUtc = Now.AddMinutes(-6) }, Now));
        Assert.False(registry.Apply(identity with { StartOffset = 30, EndOffset = 40, LoggedUtc = Now.AddSeconds(3) }, Now));
        Assert.False(registry.Apply(identity with { StartOffset = 30, EndOffset = 40, ReadUtc = Now.AddMinutes(-6) }, Now));
        Assert.False(registry.Apply(identity with { StartOffset = 30, EndOffset = 40, PlaceId = 1 }, Now));
        Assert.False(registry.Apply(identity with { StartOffset = 30, EndOffset = 40, UserId = -1 }, Now));
        Assert.False(registry.Apply(identity with { StartOffset = -1 }, Now));
    }

    [Fact]
    public void ProcessStartAndHostPreventPidReuseAndOldDiscoveryFromRevivingExitedEvidence()
    {
        var registry = new SessionEvidenceRegistry(); var old = Key(ageSeconds: 120); var current = Key(ageSeconds: 30); var stream = Stream();
        registry.RegisterProcess(old, Now); registry.CorroborateLog(Proof(old, stream), Now);
        Assert.True(registry.RegisterProcess(current, Now));
        Assert.Equal(ObservedSessionLifecycle.Exited, registry.Snapshot(Now).Single(s => s.Process == old).Lifecycle);
        Assert.False(registry.Apply(Observation(stream, ClientLogFact.Connected), Now));
        Assert.False(registry.RegisterProcess(Key(ageSeconds: 180), Now));
        Assert.True(registry.RegisterProcess(Key(host: "other-owned-host"), Now));
        Assert.Equal(3, registry.Snapshot(Now).Count);
        Assert.True(registry.MarkExited(current, Now)); Assert.False(registry.RegisterProcess(current, Now));
    }

    [Fact]
    public void IdentityConflictAndMissingLogClearCurrentIdentityInsteadOfGuessing()
    {
        var registry = new SessionEvidenceRegistry(); var key = Key(); var stream = Stream();
        registry.RegisterProcess(key, Now); registry.CorroborateLog(Proof(key, stream), Now);
        registry.Apply(Observation(stream, ClientLogFact.UserIdentityObserved), Now);
        Assert.False(registry.Apply(Observation(stream, ClientLogFact.UserIdentityObserved, 10) with { UserId = 999 }, Now));
        Assert.True(Assert.Single(registry.Snapshot(Now)).Ambiguous); Assert.Null(Assert.Single(registry.Snapshot(Now)).ObservedUserId);
        var next = stream with { Generation = 2 }; Assert.True(registry.CorroborateLog(Proof(key, next), Now));
        registry.Apply(Observation(next, ClientLogFact.UserIdentityObserved), Now);
        var missing = new ClientLogBatch(next, ClientLogReadStatus.AccessDenied, ClientLogDiscontinuity.None, 10, 10, false, 0, []);
        Assert.Equal(0, registry.ApplyBatch(missing, Now));
        var state = Assert.Single(registry.Snapshot(Now));
        Assert.Null(state.ObservedUserId); Assert.Null(state.CurrentDestination); Assert.Null(state.Stream);
    }

    [Fact]
    public void RegistryIsBoundedAndRemovalAllowsAnExplicitNewProcess()
    {
        var registry = new SessionEvidenceRegistry(maximumSessions: 1);
        Assert.True(registry.RegisterProcess(Key(), Now)); Assert.False(registry.RegisterProcess(Key(42), Now));
        Assert.True(registry.Remove(Key())); Assert.True(registry.RegisterProcess(Key(42), Now));
        Assert.False(registry.RegisterProcess(new(null, 0, Now.UtcTicks), Now));
        Assert.False(registry.RegisterProcess(new("\n", 43, Now.UtcTicks), Now));
    }
}
