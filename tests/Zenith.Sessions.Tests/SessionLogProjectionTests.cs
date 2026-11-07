using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class SessionLogProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 17, 0, 0, TimeSpan.Zero);
    private static readonly ClientLogStreamId Stream = new(Guid.Parse("a88ba79a-f94a-48dd-8242-0098fe632d21"), 1);
    private static SessionObservation Process(int pid = 101) => new(pid, Now.AddSeconds(-10).UtcTicks,
        "Synthetic fixture", 0, "", false, true, 10, 10, null, Now);
    private static SessionProcessKey Key(SessionObservation process) => new(null, process.ProcessId, process.ProcessStartUtcTicks);
    private static DesktopSnapshot Desktop(IReadOnlyList<SessionObservation> processes) => new(
        new(Now, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 10 * CapacityPolicy.GiB, 10),
        processes, ProcessObservedUtc: Now, ProcessRevision: 1, ProcessInventoryComplete: true);
    private static SessionLogDiscoverySnapshot Logs(SessionObservation process, long revision = 1,
        SessionLogCorrelationStatus status = SessionLogCorrelationStatus.ResourceHolderObserved)
    {
        ClientLogObservation[] facts = [
            new(Stream, 0, 10, Now, Now, ClientLogFact.UserIdentityObserved, UserId: 90001),
            new(Stream, 10, 20, Now, Now, ClientLogFact.Joining, PlaceId: 6564161046,
                JobId: Guid.Parse("09000000-0000-0000-0000-000000000002")),
            new(Stream, 20, 30, Now, Now, ClientLogFact.Connected)];
        var batch = new ClientLogBatch(Stream, ClientLogReadStatus.Ready, ClientLogDiscontinuity.Initial, 30, 30, false, 0, facts);
        return new(revision, Now, [new(Key(process), status, Stream, batch, Now, Now.AddSeconds(10))], 1, false);
    }

    [Fact]
    public void CorrelatedSyntheticFactsAppearOnlyAsUnverifiedReportedValues()
    {
        var process = Process(); var projection = new SessionLogProjection();
        projection.Update([process], Logs(process), Now);
        var view = Assert.Single(projection.Snapshot(Now)).Value;
        Assert.Equal("90001", view.ReportedUserId);
        Assert.Equal("6564161046", view.CurrentPlace);
        Assert.Contains("unverified", view.Correlation);
        Assert.Contains("unverified", view.Explanation);
        Assert.False(view.CanAuthorizeAccountAction);
    }

    [Theory]
    [InlineData(null, false, "Disconnected")]
    [InlineData(285, false, "Disconnected · reason 285 · you left")]
    [InlineData(273, false, "Disconnected · reason 273 · account launched elsewhere")]
    [InlineData(267, true, "Disconnected · reason 267 · connection lost · Roblox asked for a human check")]
    public void DisconnectStateNamesTheReasonAndWhetherThePlayerLeft(int? reason, bool humanCheck, string expected)
    {
        var process = Process(); var projection = new SessionLogProjection(); var logs = Logs(process);
        var old = logs.Sessions[0];
        ClientLogObservation[] extra = humanCheck
            ? [new(Stream, 30, 40, Now, Now, ClientLogFact.HumanCheck), new(Stream, 40, 50, Now, Now, ClientLogFact.Disconnected, DisconnectReason: reason)]
            : [new(Stream, 30, 40, Now, Now, ClientLogFact.Disconnected, DisconnectReason: reason)];
        var batch = old.Batch! with { Observations = [.. old.Batch!.Observations, .. extra], ReadOffset = 50, CompleteLineOffset = 50 };
        projection.Update([process], logs with { Sessions = [old with { Batch = batch }] }, Now);
        Assert.Equal(expected, Assert.Single(projection.Snapshot(Now)).Value.State);
        Assert.Equal("Unknown", Assert.Single(projection.Snapshot(Now.AddSeconds(10))).Value.State); // Expired leases show no current state.
    }

    [Fact]
    public void CurrentViewCarriesTheObservedDestinationTimeForRecentlyPlayed()
    {
        var process = Process(); var projection = new SessionLogProjection();
        projection.Update([process], Logs(process), Now);
        var view = Assert.Single(projection.Snapshot(Now)).Value;
        Assert.Equal(6564161046, view.ReportedPlaceId); Assert.Equal(Now, view.ReportedObservedUtc); Assert.Null(view.ReportedUniverseId);
        var expired = Assert.Single(projection.Snapshot(Now.AddSeconds(10))).Value;
        Assert.Null(expired.ReportedPlaceId); Assert.Null(expired.ReportedObservedUtc); // Expired leases carry no current destination at all.
    }

    [Fact]
    public void ExpiredHolderLeaseClearsCurrentButKeepsTimestampedLastKnownDestination()
    {
        var process = Process(); var projection = new SessionLogProjection();
        projection.Update([process], Logs(process), Now);
        var view = Assert.Single(projection.Snapshot(Now.AddSeconds(10))).Value;
        Assert.Equal("Unknown", view.ReportedUserId);
        Assert.Equal("Unknown", view.CurrentPlace);
        Assert.Equal("Unknown", view.CurrentServer);
        Assert.Contains("6564161046", view.LastKnownDestination);
        Assert.Contains("90001", view.LastKnownPlayer);
        Assert.Contains("unverified", view.LastKnownPlayer);
    }

    [Theory]
    [InlineData(SessionLogCorrelationStatus.Ambiguous)]
    [InlineData(SessionLogCorrelationStatus.AccessDenied)]
    [InlineData(SessionLogCorrelationStatus.SourceChanged)]
    public void LostCorrelationDoesNotKeepPreviouslyCurrentClaims(SessionLogCorrelationStatus status)
    {
        var process = Process(); var projection = new SessionLogProjection();
        projection.Update([process], Logs(process), Now);
        projection.Update([process], Logs(process, 2, status) with { ObservedUtc = Now.AddSeconds(1),
            Sessions = [new(Key(process), status, Stream, null, Now.AddSeconds(1))] }, Now.AddSeconds(1));
        var view = Assert.Single(projection.Snapshot(Now.AddSeconds(1))).Value;
        Assert.Equal("Unknown", view.ReportedUserId);
        Assert.Equal("Unknown", view.CurrentPlace);
        Assert.Contains("6564161046", view.LastKnownDestination);
    }

    [Fact]
    public void ForeignOrReusedPidAndLateRevisionDoNotTransferOrRestoreIdentity()
    {
        var old = Process(); var projection = new SessionLogProjection();
        projection.Update([old], Logs(old), Now);
        var current = old with { ProcessStartUtcTicks = Now.AddSeconds(1).UtcTicks, ObservedUtc = Now.AddSeconds(2) };
        projection.Update([current], Logs(old, 2), Now.AddSeconds(2));
        var view = Assert.Single(projection.Snapshot(Now.AddSeconds(2))).Value;
        Assert.Equal("Unknown", view.ReportedUserId);
        projection.Update([current], Logs(old, 1), Now.AddSeconds(3));
        Assert.Equal("Unknown", Assert.Single(projection.Snapshot(Now.AddSeconds(3))).Value.ReportedUserId);
        projection.Update([], null, Now.AddSeconds(3));
        Assert.Empty(projection.Snapshot(Now.AddSeconds(3)));
    }

    [Fact]
    public void SkippedUiRevisionCanConsumeOriginalCumulativeFactsWithoutRefreshingTheirTimes()
    {
        var process = Process(); var projection = new SessionLogProjection();
        var original = Logs(process, 5);
        projection.Update([process with { ObservedUtc = Now.AddSeconds(2) }], original, Now.AddSeconds(2));
        var view = Assert.Single(projection.Snapshot(Now.AddSeconds(2))).Value;
        Assert.Equal("90001", view.ReportedUserId);
        Assert.Equal(Now.ToLocalTime().ToString("HH:mm:ss"), view.Time);
        projection.Update([process with { ObservedUtc = Now.AddMinutes(1) }], original, Now.AddMinutes(1));
        Assert.Equal("Unknown", Assert.Single(projection.Snapshot(Now.AddMinutes(1))).Value.CurrentPlace);
    }

    [Fact]
    public void TelemetryUpdatesKeepTheRowAndOnlyChangedDiagnosticViewRaisesANotification()
    {
        var row = new SessionRow(Process()); int changed = 0;
        row.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(SessionRow.Log)) changed++; };
        row.UpdateLog(SessionLogView.Unknown);
        Assert.Equal(0, changed);
        row.UpdateLog(SessionLogView.Unknown with { Correlation = "Synthetic fixture" });
        row.Update(row.Observation with { WorkingSetBytes = 50 });
        Assert.Equal(1, changed);
        Assert.Equal("Synthetic fixture", row.Log.Correlation);
    }

    [Fact]
    public void ConflictingIdentityIsAmbiguousRatherThanAnExpiredLeaseAndStaysUnknownOnReplay()
    {
        var process = Process(); var projection = new SessionLogProjection(); var logs = Logs(process);
        var diagnostic = logs.Sessions[0];
        var batch = diagnostic.Batch!;
        // The conflicting line is inside the batch's own coverage; an over-claiming batch is a separate case (withheld, not ambiguous).
        var conflicting = batch with { ReadOffset = 40, CompleteLineOffset = 40, Observations = [.. batch.Observations,
            new(Stream, 30, 40, Now, Now, ClientLogFact.UserIdentityObserved, UserId: 99999)] };
        logs = logs with { Sessions = [diagnostic with { Batch = conflicting }] };
        projection.Update([process], logs, Now);
        var view = Assert.Single(projection.Snapshot(Now)).Value;
        Assert.Equal("Unknown", view.ReportedUserId);
        Assert.Contains("Ambiguous", view.Correlation);
        Assert.DoesNotContain("expired", view.Correlation);
        projection.Update([process], logs with { Revision = 2 }, Now);
        Assert.Contains("Ambiguous", Assert.Single(projection.Snapshot(Now)).Value.Correlation);
    }

    [Fact]
    public void ReplacedStreamCanPublishItsFirstBoundedBatchAfterFreshCorrelation()
    {
        var process = Process(); var projection = new SessionLogProjection(); var logs = Logs(process);
        projection.Update([process], logs, Now);
        var later = Now.AddSeconds(1); var replacement = new ClientLogStreamId(Guid.NewGuid(), 1);
        var original = logs.Sessions[0].Batch!;
        var batch = original with { Stream = replacement, Observations = original.Observations.Select(fact => fact with
            { Stream = replacement, LoggedUtc = later, ReadUtc = later }).ToArray() };
        projection.Update([process], new(2, later, [new(Key(process), SessionLogCorrelationStatus.ResourceHolderObserved,
            replacement, batch, later, later.AddSeconds(10))], 1, false), later);
        var view = Assert.Single(projection.Snapshot(later)).Value;
        Assert.Equal("90001", view.ReportedUserId);
        Assert.Equal("6564161046", view.CurrentPlace);
        Assert.DoesNotContain("Ambiguous", view.Correlation);
    }

    [Fact]
    public void CopyUsesNewerDisconnectEvidenceBeforeTheVisiblePageRefreshes()
    {
        var process=Process();var projection=new SessionLogProjection();var logs=Logs(process);
        projection.Update([process],logs,Now);
        var visible=Assert.Single(projection.Snapshot(Now)).Value;
        Assert.True(visible.HasReportedDestination);
        string? original=projection.BuildCurrentLink(Key(process),Desktop([process]),logs,Now);
        Assert.Contains("gameInstanceId=",original);
        Assert.Equal(RobloxServerKind.Unknown,visible.ReportedDestination!.ServerKind);
        var later=Now.AddSeconds(1);var old=logs.Sessions[0];
        var batch=old.Batch! with {Observations=[..old.Batch!.Observations,new(Stream,30,40,later,later,ClientLogFact.Disconnected)]};
        var latest=logs with {Revision=2,ObservedUtc=later,Sessions=[old with {Batch=batch}]};
        Assert.Null(projection.BuildCurrentLink(Key(process),Desktop([process]),latest,later));
        Assert.True(visible.HasReportedDestination); // Retained UI data must not be the copy authority.
        Assert.False(Assert.Single(projection.Snapshot(later)).Value.HasReportedDestination);
    }

    [Fact]
    public void CopyRejectsExitAmbiguityAndLeaseExpiryInsteadOfUsingLastKnownValues()
    {
        var process=Process();var projection=new SessionLogProjection();var logs=Logs(process);
        projection.Update([process],logs,Now);
        Assert.Null(projection.BuildCurrentLink(Key(process),Desktop([process]),logs,Now.AddSeconds(10)));
        Assert.NotEqual("Unknown",Assert.Single(projection.Snapshot(Now.AddSeconds(10))).Value.LastKnownDestination);
        var fresh=new SessionLogProjection();fresh.Update([process],logs,Now);
        var ambiguous=logs with {Revision=2,Sessions=[logs.Sessions[0] with {Correlation=SessionLogCorrelationStatus.Ambiguous,Batch=null,ObservedUtc=Now.AddSeconds(1)}]};
        Assert.Null(fresh.BuildCurrentLink(Key(process),Desktop([process]),ambiguous,Now.AddSeconds(1)));
        Assert.Null(fresh.BuildCurrentLink(Key(process),Desktop([]),ambiguous,Now.AddSeconds(2)));
    }

    [Fact]
    public void RowViewsOfferReportedActionsOnlyWithTheClickTimeInventoryAndDiagnostic()
    {
        var process=Process();var projection=new SessionLogProjection();var logs=Logs(process);
        projection.Update([process],logs,Now);
        SessionLogView View(DesktopSnapshot? desktop,SessionLogDiscoverySnapshot? latest,DateTimeOffset at)
            =>Assert.Single(projection.Snapshot(at,desktop,latest)).Value;

        Assert.True(View(Desktop([process]),logs,Now).CanUseReportedDestination);
        Assert.False(Assert.Single(projection.Snapshot(Now)).Value.CanUseReportedDestination); // Evidence-only views never offer it.
        Assert.False(View(null,logs,Now).CanUseReportedDestination);
        Assert.False(View(Desktop([process]) with {ProcessInventoryComplete=false},logs,Now).CanUseReportedDestination);
        Assert.False(View(Desktop([process]) with {Problem="Synthetic failed scan"},logs,Now).CanUseReportedDestination);
        Assert.False(View(Desktop([process with {ProcessStartUtcTicks=process.ProcessStartUtcTicks+1}]),logs,Now).CanUseReportedDestination);
        Assert.False(View(Desktop([process]),null,Now).CanUseReportedDestination);
        var unapplied=logs with {Revision=2,ObservedUtc=Now.AddSeconds(1),Sessions=[logs.Sessions[0] with {ObservedUtc=Now.AddSeconds(1)}]};
        Assert.False(View(Desktop([process]),unapplied,Now.AddSeconds(1)).CanUseReportedDestination);
        var stale=View(Desktop([process]),logs,Now.AddSeconds(4)); // The scan is older than the click allows; the lease is not.
        Assert.True(stale.HasReportedDestination);Assert.False(stale.CanUseReportedDestination);
        Assert.Null(projection.BuildCurrentLink(Key(process),Desktop([process]),logs,Now.AddSeconds(4)));
        Assert.NotNull(projection.BuildCurrentLink(Key(process),Desktop([process]),logs,Now));
    }

    [Fact]
    public void UnchangedTypedDestinationsDoNotCauseRecordOrRowRefreshes()
    {
        var process=Process();var projection=new SessionLogProjection();projection.Update([process],Logs(process),Now);
        var first=Assert.Single(projection.Snapshot(Now)).Value;
        var second=Assert.Single(projection.Snapshot(Now.AddSeconds(1))).Value;
        Assert.Equal(first,second);
        var row=new SessionRow(process);row.UpdateLog(first);int changes=0;
        row.PropertyChanged+=(_,args)=>{if(args.PropertyName==nameof(SessionRow.Log)) changes++;};
        row.UpdateLog(second);Assert.Equal(0,changes);
    }

    [Theory]
    [InlineData("missing snapshot")]
    [InlineData("legacy")]
    [InlineData("partial")]
    [InlineData("problem")]
    [InlineData("revision")]
    [InlineData("stale scan")]
    [InlineData("future scan")]
    [InlineData("stale row")]
    [InlineData("row problem")]
    [InlineData("duplicate")]
    [InlineData("reused")]
    [InlineData("missing process")]
    public void ClickRejectsUnusableInventoryWithoutChangingThePriorRegistry(string kind)
    {
        var process = Process();
        var projection = new SessionLogProjection();
        var logs = Logs(process);
        projection.Update([process], logs, Now);
        var before = Assert.Single(projection.EvidenceSnapshot(Now));
        DesktopSnapshot? desktop = kind switch
        {
            "missing snapshot" => null,
            "legacy" => new(Desktop([process]).Resources, [process]),
            "partial" => Desktop([process]) with { ProcessInventoryComplete = false },
            "problem" => Desktop([process]) with { Problem = "Synthetic scan unavailable" },
            "revision" => Desktop([process]) with { ProcessRevision = 0 },
            "stale scan" => Desktop([process]) with { ProcessObservedUtc = Now.AddSeconds(-4) },
            "future scan" => Desktop([process]) with { ProcessObservedUtc = Now.AddTicks(1) },
            "stale row" => Desktop([process with { ObservedUtc = Now.AddSeconds(-4) }]),
            "row problem" => Desktop([process with { Problem = "Synthetic row unavailable" }]),
            "duplicate" => Desktop([process, process]),
            "reused" => Desktop([process with { ProcessStartUtcTicks = process.ProcessStartUtcTicks + 1 }]),
            _ => Desktop([])
        };

        Assert.Null(projection.BuildCurrentLink(Key(process), desktop, logs with { Revision = 99 }, Now));
        Assert.Same(before, Assert.Single(projection.EvidenceSnapshot(Now)));
        Assert.NotNull(projection.BuildCurrentLink(Key(process), Desktop([process]), logs with { Revision = 2 }, Now));
    }

    [Theory]
    [InlineData("missing snapshot")]
    [InlineData("missing diagnostic")]
    [InlineData("duplicate diagnostic")]
    [InlineData("older revision")]
    [InlineData("stale snapshot")]
    [InlineData("expired lease")]
    [InlineData("future diagnostic")]
    [InlineData("different stream")]
    [InlineData("shared stream")]
    public void ClickRequiresOneApplicableLatestDiagnostic(string kind)
    {
        var process = Process();
        var projection = new SessionLogProjection();
        var original = Logs(process, 2);
        projection.Update([process], original, Now);
        var before = Assert.Single(projection.EvidenceSnapshot(Now));
        var current = original.Sessions[0];
        SessionLogDiscoverySnapshot? latest = kind switch
        {
            "missing snapshot" => null,
            "missing diagnostic" => original with { Revision = 3, Sessions = [] },
            "duplicate diagnostic" => original with { Revision = 3, Sessions = [current, current] },
            "older revision" => original with { Revision = 1 },
            "stale snapshot" => original with { Revision = 3, ObservedUtc = Now.AddSeconds(-16) },
            "expired lease" => original with { Revision = 3, Sessions = [current with { LeaseExpiresUtc = Now }] },
            "future diagnostic" => original with { Revision = 3, Sessions = [current with { ObservedUtc = Now.AddTicks(1) }] },
            "different stream" => original with { Revision = 3, Sessions = [current with { SourceStamp = Stream with { Generation = 2 } }] },
            _ => original with { Revision = 3, Sessions = [current, current with { Process = Key(Process(202)) }] }
        };

        Assert.Null(projection.BuildCurrentLink(Key(process), Desktop([process]), latest, Now));
        Assert.Same(before, Assert.Single(projection.EvidenceSnapshot(Now)));
    }

    [Fact]
    public void SameRevisionCannotSubstituteDifferentDiagnosticContentAtTheClick()
    {
        var process = Process();
        var projection = new SessionLogProjection();
        var logs = Logs(process);
        projection.Update([process], logs, Now);
        var current = logs.Sessions[0];
        var changed = logs with { Sessions = [current with { LeaseExpiresUtc = Now.AddSeconds(20) }] };

        Assert.Null(projection.BuildCurrentLink(Key(process), Desktop([process]), changed, Now));
        Assert.NotNull(projection.BuildCurrentLink(Key(process), Desktop([process]), logs, Now));
    }

    [Fact]
    public void CurrentWindowlessProcessPreservesInt64PlaceAndJobAsAnUnverifiedRequest()
    {
        var process = Process();
        Assert.Equal((nint)0, process.Window);
        const long place = 9007199254740993;
        var logs = Logs(process);
        var diagnostic = logs.Sessions[0];
        var batch = diagnostic.Batch! with
        {
            Observations = diagnostic.Batch!.Observations.Select(fact => fact.Fact == ClientLogFact.Joining
                ? fact with { PlaceId = place } : fact).ToArray()
        };
        logs = logs with { Sessions = [diagnostic with { Batch = batch }] };
        var projection = new SessionLogProjection();
        var desktop = Desktop([process]) with { Resources = Desktop([process]).Resources with { Problem = "CPU sample unavailable" } };

        string? link = projection.BuildCurrentLink(Key(process), desktop, logs, Now);
        var parsed = RobloxDestinationParser.Parse(link);
        Assert.Equal(RobloxDestinationStatus.Resolved, parsed.Status);
        Assert.Equal(place, parsed.RequestedDestination!.PlaceId);
        Assert.Equal("09000000-0000-0000-0000-000000000002", parsed.RequestedDestination.JobId);
        Assert.Equal(RobloxServerKind.Unknown, parsed.RequestedDestination.ServerKind);
        Assert.False(Assert.Single(projection.Snapshot(Now)).Value.CanAuthorizeAccountAction);
    }
}
