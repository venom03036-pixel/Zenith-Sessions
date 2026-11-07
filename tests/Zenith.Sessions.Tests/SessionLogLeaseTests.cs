using Zenith.Sessions.Core;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class SessionLogLeaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 17, 0, 0, TimeSpan.Zero);
    private static readonly SessionProcessKey Process = new(null, 123, Now.AddMinutes(-1).UtcTicks);
    private static readonly ClientLogStreamId Stream = new(Guid.Parse("a88ba79a-f94a-48dd-8242-0098fe632d20"), 1);
    private static SessionLogCorroboration Holder(DateTimeOffset? until) =>
        new(Process, Stream, SessionLogCorrelationKind.ResourceHolderDiagnostic, Guid.NewGuid(), Now, until);
    private static SessionEvidenceRegistry Registry()
    {
        var value = new SessionEvidenceRegistry();
        Assert.True(value.RegisterProcess(Process, Now));
        return value;
    }
    private static ClientLogObservation Fact(ClientLogFact fact, long offset = 0) => new(Stream, offset, offset + 10,
        Now, Now, fact, UserId: fact == ClientLogFact.UserIdentityObserved ? 1001 : null,
        PlaceId: fact == ClientLogFact.Joining ? 6564161046 : null,
        JobId: fact == ClientLogFact.Joining ? Guid.Parse("09000000-0000-0000-0000-000000000001") : null,
        FormatSupport: ClientLogFormatSupport.SanitizedRealFixtureVerified);

    [Fact]
    public void HolderIsDiagnosticEvenWithVerifiedParserAndCannotUpgradeToWriterProof()
    {
        var registry = Registry();
        Assert.True(registry.CorroborateLog(Holder(Now.AddSeconds(10)), Now));
        Assert.True(registry.Apply(Fact(ClientLogFact.UserIdentityObserved), Now));
        var state = Assert.Single(registry.Snapshot(Now));
        Assert.Equal(1001, state.ObservedUserId);
        Assert.Equal(SessionEvidenceConfidence.UnverifiedDiagnostic, state.Confidence);
        Assert.False(state.CanAuthorizeAccountAction);
        Assert.False(registry.CorroborateLog(Holder(null) with { Kind = SessionLogCorrelationKind.IndependentlyCorroborated }, Now));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(31)]
    public void HolderLeaseMustBeFreshAndBounded(int seconds)
    {
        var registry = Registry();
        Assert.False(registry.CorroborateLog(Holder(Now.AddSeconds(seconds)), Now));
        Assert.False(registry.CorroborateLog(Holder(null), Now));
    }

    [Fact]
    public void RenewalDoesNotRefreshDestinationOrIdentityEventTime()
    {
        var registry = Registry();
        registry.CorroborateLog(Holder(Now.AddSeconds(10)), Now);
        registry.Apply(Fact(ClientLogFact.Joining), Now);
        registry.Apply(Fact(ClientLogFact.Connected, 10), Now);
        registry.Apply(Fact(ClientLogFact.UserIdentityObserved, 20), Now);
        var later = Now.AddSeconds(5);
        Assert.True(registry.CorroborateLog(Holder(later.AddSeconds(10)) with { CorroboratedUtc = later }, later));
        var state = Assert.Single(registry.Snapshot(later));
        Assert.Equal(Now, state.CurrentDestination!.ObservedUtc);
        Assert.Equal(Now, state.UserIdentityObservedUtc);
        Assert.Null(Assert.Single(registry.Snapshot(later.AddSeconds(10))).CurrentDestination);
        state = Assert.Single(registry.Snapshot(later.AddSeconds(10)));
        Assert.Null(state.ObservedUserId);
        Assert.NotNull(state.LastKnownDestination);
        Assert.Null(state.Stream);
    }

    [Fact]
    public void ExpiredLeaseBlocksEventsWithoutNeedingSnapshotFirst()
    {
        var registry = Registry();
        registry.CorroborateLog(Holder(Now.AddSeconds(10)), Now);
        Assert.False(registry.Apply(Fact(ClientLogFact.UserIdentityObserved), Now.AddSeconds(10)));
        Assert.Null(Assert.Single(registry.Snapshot(Now.AddSeconds(10))).ObservedUserId);
    }

    [Fact]
    public void LostCorrelationInvalidatesOnlyExpectedStreamAndRejectsOldBufferedFacts()
    {
        var registry = Registry();
        registry.CorroborateLog(Holder(Now.AddSeconds(10)), Now);
        registry.Apply(Fact(ClientLogFact.UserIdentityObserved), Now);
        Assert.False(registry.InvalidateLog(Process, Stream with { Generation = 2 }, Now.AddSeconds(1)));
        Assert.Equal(1001, Assert.Single(registry.Snapshot(Now)).ObservedUserId);
        Assert.True(registry.InvalidateLog(Process, Stream, Now.AddSeconds(1)));
        Assert.Null(Assert.Single(registry.Snapshot(Now.AddSeconds(1))).ObservedUserId);
        Assert.True(registry.CorroborateLog(Holder(Now.AddSeconds(12)) with { CorroboratedUtc = Now.AddSeconds(2) }, Now.AddSeconds(2)));
        Assert.False(registry.Apply(Fact(ClientLogFact.UserIdentityObserved, 10), Now.AddSeconds(2)));
    }

    [Fact]
    public void BindingAfterReadDoesNotDiscardJustReadEventsOrRestampThem()
    {
        var registry = Registry();
        var queryTime = Now.AddMilliseconds(80);
        Assert.True(registry.CorroborateLog(Holder(queryTime.AddSeconds(10)) with { CorroboratedUtc = queryTime }, queryTime));
        Assert.True(registry.Apply(Fact(ClientLogFact.UserIdentityObserved), queryTime));
        var state = Assert.Single(registry.Snapshot(queryTime));
        Assert.Equal(Now, state.UserIdentityObservedUtc);
        Assert.Equal(Now, state.ObservedUtc);
    }

    [Fact]
    public void NewerProcessMeasurementDoesNotRejectAsynchronousLogPublication()
    {
        var registry = Registry();
        registry.CorroborateLog(Holder(Now.AddSeconds(10)), Now);
        Assert.True(registry.RegisterProcess(Process, Now.AddMilliseconds(100)));
        Assert.True(registry.Apply(Fact(ClientLogFact.UserIdentityObserved), Now.AddMilliseconds(200)));
        Assert.Equal(Now, Assert.Single(registry.Snapshot(Now.AddMilliseconds(200))).UserIdentityObservedUtc);
    }

    [Fact]
    public void ThousandsOfRetiredReadersCannotExhaustAnOtherwiseAvailableRegistry()
    {
        var registry = new SessionEvidenceRegistry(maximumSessions: 1);
        Assert.True(registry.RegisterProcess(Process, Now));
        var retired = new List<ClientLogStreamId>();
        for (int index = 0; index < 1000; index++)
        {
            var stream = new ClientLogStreamId(Guid.NewGuid(), 1);
            Assert.True(registry.CorroborateLog(Holder(Now.AddSeconds(10)) with { Stream = stream }, Now));
            Assert.True(registry.Apply(Fact(ClientLogFact.UserIdentityObserved) with { Stream = stream }, Now));
            Assert.True(registry.InvalidateLog(Process, stream, Now));
            retired.Add(stream);
        }
        foreach (var stream in retired)
            Assert.False(registry.Apply(Fact(ClientLogFact.UserIdentityObserved) with { Stream = stream }, Now));
        Assert.Null(Assert.Single(registry.Snapshot(Now)).ObservedUserId);
    }

    [Fact]
    public void RetiredReaderCleanupNeverEvictsAnotherActiveStreamOrItsGenerationFence()
    {
        var registry = new SessionEvidenceRegistry(maximumSessions: 2);
        var other = Process with { ProcessId = Process.ProcessId + 1 };
        registry.RegisterProcess(Process, Now); registry.RegisterProcess(other, Now);
        var active = Stream with { Generation = 5 };
        Assert.True(registry.CorroborateLog(Holder(Now.AddSeconds(10)) with { Process = other, Stream = active }, Now));
        for (int index = 0; index < 100; index++)
        {
            var stream = new ClientLogStreamId(Guid.NewGuid(), 1);
            Assert.True(registry.CorroborateLog(Holder(Now.AddSeconds(10)) with { Stream = stream }, Now));
            Assert.True(registry.InvalidateLog(Process, stream, Now));
        }
        Assert.False(registry.CorroborateLog(Holder(Now.AddSeconds(10)) with { Process = other, Stream = active with { Generation = 4 } }, Now));
        Assert.True(registry.Apply(Fact(ClientLogFact.UserIdentityObserved) with { Stream = active }, Now));
        Assert.Equal(1001, registry.Snapshot(Now).Single(state => state.Process == other).ObservedUserId);
    }
}
