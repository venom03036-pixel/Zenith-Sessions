using System.Collections;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// Pure synthetic queue, process and registry facts. No files, processes, browser or accounts.
public sealed class NativeTrialEvidenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Job = Guid.Parse("12345678-1234-4234-8234-123456789abc"), OtherJob = Guid.Parse("a2345678-1234-4234-8234-123456789abc");
    private static readonly long Start = Now.AddMinutes(-10).UtcTicks;
    private static LaunchOperation Operation(string? job = "12345678-1234-4234-8234-123456789abc") => new(
        new(Guid.NewGuid(), Guid.NewGuid(), 1, 123, 42, 0, Now.AddMinutes(-6), 7, job, job is null ? RobloxServerKind.Unknown : RobloxServerKind.Public, true),
        LaunchState.ClientStartedUnverified, LaunchProblem.ClientStartedUnverified, Now, new(700001, Start, Now.AddMinutes(-5)));
    private static SessionObservation Row() => new(700001, Start, "Synthetic version", (nint)100, "Unrelated label", false, true, 1, 1, null, Now.AddMilliseconds(-100));
    private static DesktopSnapshot Desktop(params SessionObservation[] rows) => new(new(Now, 32, 20, 64, 10, 10), rows,
        ProcessObservedUtc: Now, ProcessRevision: 1, ProcessInventoryComplete: true);
    private static SessionEvidenceSnapshot Facts() => new(Guid.NewGuid(), new(null, 700001, Start), (nint)100, ObservedSessionLifecycle.Connected,
        123, new(42, Job, null, Now.AddMilliseconds(-200)), null, new(Guid.NewGuid(), 1), SessionEvidenceConfidence.UnverifiedDiagnostic,
        Now.AddMilliseconds(-200), false, Now.AddMilliseconds(-300), SessionLogCorrelationKind.ResourceHolderDiagnostic, Now.AddSeconds(5));
    private static NativeTrialEvidenceView Project(LaunchOperation operation, DesktopSnapshot? desktop, params SessionEvidenceSnapshot[] facts)
        => NativeTrialEvidence.Project(new(false, [operation]), operation.Intent.OperationId, desktop, facts, Now);
    private static void NoAuthority(NativeTrialEvidenceView view)
    { Assert.False(view.CanAuthorizeAccountAction); Assert.False(view.ArrivalVerified); Assert.False(view.CanRouteInput); }

    [Fact]
    public void MatchingPlayerAndPlaceCannotQualifyPrivateServerArrivalOrOrdinaryLaunching()
    {
        var ordinary=Operation(null);
        var operation=ordinary with {Intent=ordinary.Intent with{RequestedServerKind=RobloxServerKind.Private,PrivateServer=new(Guid.NewGuid(),2)}};
        var view=Project(operation,Desktop(Row()),Facts());
        Assert.Equal(NativeTrialReportedStatus.ReportedMatchUnverified,view.ReportedUser.Status);
        Assert.Equal(NativeTrialEvidenceOutcome.Unknown,view.Outcome);
        Assert.Contains("private server",view.Explanation);
        Assert.False(NativeLaunchVerification.CanRecord(operation,view));NoAuthority(view);
    }

    [Fact]
    public void ExactObservedProcessAndMatchingReportedIdsRemainUnverifiedAndKeepOriginalTimes()
    {
        var operation = Operation(); var facts = Facts(); var view = Project(operation, Desktop(Row()), facts);
        Assert.Same(operation.ProcessReceipt, view.ProcessReceipt); Assert.Equal(123, view.ExpectedUserId); Assert.Equal(42, view.RequestedPlaceId); Assert.Equal(Job.ToString("D"), view.RequestedJobId);
        Assert.Equal(NativeTrialProcessPresence.Observed, view.Presence); Assert.Equal(Row().ObservedUtc, view.ProcessMeasurementUtc); Assert.Equal(Now, view.ProcessObservedUtc);
        Assert.Equal(123, view.ReportedUser.UserId); Assert.Equal(facts.UserIdentityObservedUtc, view.ReportedUser.ObservedUtc);
        Assert.Equal(42, view.ReportedDestination.PlaceId); Assert.Equal(Job, view.ReportedDestination.JobId); Assert.Equal(facts.CurrentDestination!.ObservedUtc, view.ReportedDestination.ObservedUtc);
        Assert.Equal(NativeTrialEvidenceOutcome.ReportedMatchUnverified, view.Outcome); NoAuthority(view);
        Assert.Contains("unverified", view.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadingLogKeepsLastKnownFactsAndDeclinesAnExactArrivalComparison()
    {
        var current = Facts();
        var facts = current with { AwaitingLogTail = true, LastKnownDestination = current.CurrentDestination,
            LastKnownUserId = current.ObservedUserId, LastKnownUserIdentityUtc = current.UserIdentityObservedUtc };
        var view = Project(Operation(), Desktop(Row()), facts);
        Assert.Equal(NativeTrialReportedStatus.ReadingLog, view.ReportedUser.Status);
        Assert.Equal(NativeTrialReportedStatus.ReadingLog, view.ReportedDestination.PlaceStatus);
        Assert.Null(view.ReportedUser.UserId);
        Assert.Null(view.ReportedDestination.JobId);
        Assert.Equal(123, view.LastKnownUser.UserId);
        Assert.Equal(Job, view.LastKnownDestination.JobId);
        Assert.Equal(NativeTrialEvidenceOutcome.Unknown, view.Outcome);
        NoAuthority(view);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("place")]
    [InlineData("job")]
    public void ReportedMismatchesDoNotRewriteExpectedTargetsOrBecomeAuthenticatedIdentity(string mismatch)
    {
        var operation = Operation(); var facts = Facts();
        facts = mismatch switch
        {
            "user" => facts with { ObservedUserId = 456 },
            "place" => facts with { CurrentDestination = facts.CurrentDestination! with { PlaceId = 99 } },
            _ => facts with { CurrentDestination = facts.CurrentDestination! with { JobId = OtherJob } }
        };
        var view = Project(operation, Desktop(Row()), facts);
        Assert.Equal(NativeTrialEvidenceOutcome.ReportedMismatchUnverified, view.Outcome); NoAuthority(view);
        Assert.Equal(123, view.ExpectedUserId); Assert.Equal(42, view.RequestedPlaceId); Assert.Equal(Job.ToString("D"), view.RequestedJobId);
        Assert.Equal(NativeTrialReportedStatus.ReportedMismatchUnverified, mismatch switch { "user" => view.ReportedUser.Status, "place" => view.ReportedDestination.PlaceStatus, _ => view.ReportedDestination.JobStatus });
        Assert.Contains("differ", view.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnyServerRequestDoesNotInventAnExactJobRequirement()
    {
        var view = Project(Operation(null), Desktop(Row()), Facts() with { CurrentDestination = Facts().CurrentDestination! with { JobId = OtherJob } });
        Assert.Null(view.RequestedJobId); Assert.Equal(OtherJob, view.ReportedDestination.JobId);
        Assert.Equal(NativeTrialReportedStatus.NotRequested, view.ReportedDestination.JobStatus); Assert.Equal(NativeTrialEvidenceOutcome.ReportedMatchUnverified, view.Outcome); NoAuthority(view);
    }

    [Fact]
    public void AProcessReceiptIsHistoricalAndDoesNotRenewOrGuessCurrentPresence()
    {
        var operation = Operation(); var view = Project(operation, null, Facts());
        Assert.Same(operation.ProcessReceipt, view.ProcessReceipt); Assert.Equal(NativeTrialProcessPresence.Unavailable, view.Presence);
        Assert.Null(view.ReportedUser.UserId); Assert.Equal(NativeTrialEvidenceOutcome.Unknown, view.Outcome); NoAuthority(view);
        var noReceipt = Project(operation with { ProcessReceipt = null }, Desktop(Row()), Facts());
        Assert.Equal(NativeTrialProcessPresence.NoReceipt, noReceipt.Presence); Assert.Null(noReceipt.ReportedUser.UserId); Assert.Null(noReceipt.LastKnownUser.UserId);
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("start")]
    [InlineData("future-recorded")]
    public void InvalidReceiptNeverAssociatesNearbyProcessesOrLogFacts(string invalid)
    {
        var operation = Operation(); operation = operation with { ProcessReceipt = invalid switch
        { "pid" => operation.ProcessReceipt! with { ProcessId = 0 }, "start" => operation.ProcessReceipt! with { ProcessStartUtcTicks = 0 }, _ => operation.ProcessReceipt! with { RecordedUtc = Now.AddTicks(1) } } };
        var view = Project(operation, Desktop(Row()), Facts()); Assert.Equal(NativeTrialProcessPresence.InvalidReceipt, view.Presence);
        Assert.Same(operation.ProcessReceipt, view.ProcessReceipt); Assert.Null(view.ReportedUser.UserId); Assert.Equal(NativeTrialEvidenceOutcome.Unknown, view.Outcome); NoAuthority(view);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("pid-reused")]
    [InlineData("duplicate")]
    [InlineData("conflicting-starts")]
    [InlineData("incomplete")]
    [InlineData("legacy")]
    [InlineData("revision")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("bad-row")]
    public void CurrentProcessEvidenceDistinguishesMissingReusedStaleAndAmbiguousCases(string condition)
    {
        var row = Row(); var desktop = Desktop(row);
        desktop = condition switch
        {
            "absent" => Desktop(), "pid-reused" => Desktop(row with { ProcessStartUtcTicks = Start + 1 }),
            "duplicate" => Desktop(row, row), "conflicting-starts" => Desktop(row, row with { ProcessStartUtcTicks = Start + 1 }),
            "incomplete" => desktop with { ProcessInventoryComplete = false }, "legacy" => new(desktop.Resources, desktop.Sessions),
            "revision" => desktop with { ProcessRevision = 0 }, "stale" => desktop with { ProcessObservedUtc = Now.AddSeconds(-4) },
            "future" => desktop with { ProcessObservedUtc = Now.AddTicks(1) }, _ => Desktop(row with { Problem = "synthetic failure" })
        };
        var view = Project(Operation(), desktop, Facts());
        var expected = condition switch
        {
            "absent" => NativeTrialProcessPresence.NotObserved, "pid-reused" => NativeTrialProcessPresence.PidReused,
            "duplicate" or "conflicting-starts" => NativeTrialProcessPresence.Ambiguous, "stale" or "future" => NativeTrialProcessPresence.Stale,
            _ => NativeTrialProcessPresence.Incomplete
        };
        Assert.Equal(expected, view.Presence); Assert.Null(view.ReportedUser.UserId); Assert.Null(view.ReportedDestination.PlaceId);
        Assert.Equal(NativeTrialEvidenceOutcome.Unknown, view.Outcome); NoAuthority(view);
    }

    [Fact]
    public void ProcessPresenceDoesNotDependOnResourceAccountingOrWindowLabels()
    {
        var desktop = Desktop(Row() with { Window = 0, WindowTitle = "", WorkingSetBytes = -1, PrivateCommitBytes = -1 })
            with { Resources = Desktop().Resources with { Problem = "synthetic memory unavailable" } };
        var view = Project(Operation(), desktop, Facts()); Assert.Equal(NativeTrialProcessPresence.Observed, view.Presence);
        Assert.Equal(NativeTrialEvidenceOutcome.ReportedMatchUnverified, view.Outcome); NoAuthority(view);
    }

    [Fact]
    public void TwoQueueReceiptsForOneInstanceAreAmbiguousRegardlessOfProfileLabelsOrTerminalState()
    {
        var operation = Operation(); var sibling = operation with { Intent = operation.Intent with { OperationId = Guid.NewGuid(), ProfileId = Guid.NewGuid() }, State = LaunchState.Cancelled };
        var view = NativeTrialEvidence.Project(new(false, [operation, sibling]), operation.Intent.OperationId, Desktop(Row()), [Facts()], Now);
        Assert.Equal(NativeTrialProcessPresence.Ambiguous, view.Presence); Assert.Null(view.ReportedUser.UserId); NoAuthority(view);
        var duplicateSelection = NativeTrialEvidence.Project(new(false, [operation, operation]), operation.Intent.OperationId, Desktop(Row()), [Facts()], Now);
        Assert.Equal(NativeTrialProcessPresence.Ambiguous, duplicateSelection.Presence); Assert.Null(duplicateSelection.ExpectedUserId);
    }

    [Fact]
    public void ReusedPidDoesNotTransferEvidenceBetweenOldAndNewReceiptInstances()
    {
        var old = Operation(); var next = Operation() with { ProcessReceipt = Operation().ProcessReceipt! with { ProcessStartUtcTicks = Start + 1 } };
        var desktop = Desktop(Row() with { ProcessStartUtcTicks = Start + 1 }); var nextFacts = Facts() with { Process = new(null, 700001, Start + 1) };
        var queue = new LaunchQueueSnapshot(false, [old, next]);
        Assert.Equal(NativeTrialProcessPresence.PidReused, NativeTrialEvidence.Project(queue, old.Intent.OperationId, desktop, [nextFacts], Now).Presence);
        var view = NativeTrialEvidence.Project(queue, next.Intent.OperationId, desktop, [Facts()], Now);
        Assert.Equal(NativeTrialProcessPresence.Observed, view.Presence); Assert.Null(view.ReportedUser.UserId); Assert.Equal(NativeTrialEvidenceOutcome.Unknown, view.Outcome);
        Assert.Equal(NativeTrialEvidenceOutcome.ReportedMatchUnverified, NativeTrialEvidence.Project(queue, next.Intent.OperationId, desktop, [nextFacts], Now).Outcome);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("pid")]
    [InlineData("start")]
    [InlineData("timing")]
    [InlineData("manual")]
    [InlineData("stream")]
    [InlineData("unknown-confidence")]
    [InlineData("no-correlation")]
    [InlineData("session-id")]
    public void WeakOrForeignLogMappingCannotBeUsedForCurrentComparisons(string mapping)
    {
        var facts = Facts(); facts = mapping switch
        {
            "host" => facts with { Process = facts.Process with { HostId = "other-host" } }, "pid" => facts with { Process = facts.Process with { ProcessId = 700002 } },
            "start" => facts with { Process = facts.Process with { ProcessStartUtcTicks = Start + 1 } },
            "timing" => facts with { CorrelationKind = SessionLogCorrelationKind.TimingOnly }, "manual" => facts with { CorrelationKind = SessionLogCorrelationKind.ManualLabel },
            "stream" => facts with { Stream = null }, "unknown-confidence" => facts with { Confidence = SessionEvidenceConfidence.Unknown },
            "session-id" => facts with { SessionId = Guid.Empty }, _ => facts with { CorrelationKind = null }
        };
        var view = Project(Operation(), Desktop(Row()), facts);
        Assert.Null(view.ReportedUser.UserId); Assert.Null(view.ReportedDestination.PlaceId); Assert.Equal(NativeTrialEvidenceOutcome.Unknown, view.Outcome); NoAuthority(view);
        Assert.Null(view.LastKnownUser.UserId);
    }

    [Fact]
    public void DuplicateOrAmbiguousLogViewsNeverChooseTheFirstReportedIdentity()
    {
        var first = Facts(); var second = Facts() with { ObservedUserId = 456 };
        var view = Project(Operation(), Desktop(Row()), first, second);
        Assert.Equal(NativeTrialReportedStatus.Ambiguous, view.ReportedUser.Status); Assert.Null(view.ReportedUser.UserId); Assert.Null(view.LastKnownUser.UserId); NoAuthority(view);
        var marked = Project(Operation(), Desktop(Row()), first with { Ambiguous = true });
        Assert.Equal(NativeTrialReportedStatus.Ambiguous, marked.ReportedUser.Status); Assert.Null(marked.ReportedUser.UserId);
    }

    [Fact]
    public void OneDiagnosticStreamClaimedByDifferentProcessesIsNotAnAccountMapping()
    {
        var facts = Facts(); var conflicting = facts with { Process = facts.Process with { ProcessId = 700002 }, ObservedUserId = 456 };
        var view = Project(Operation(), Desktop(Row()), facts, conflicting);
        Assert.Equal(NativeTrialReportedStatus.Ambiguous, view.ReportedUser.Status); Assert.Null(view.ReportedUser.UserId);
        Assert.Equal(NativeTrialEvidenceOutcome.Unknown, view.Outcome); NoAuthority(view);
    }

    [Theory]
    [InlineData("lease-expired")]
    [InlineData("lease-missing")]
    [InlineData("lease-implausible")]
    [InlineData("source-stale")]
    [InlineData("source-future")]
    [InlineData("user-stale")]
    [InlineData("destination-stale")]
    [InlineData("user-future")]
    [InlineData("destination-future")]
    public void LeaseAndOriginalFactFreshnessAreNotRenewedByAProcessOrPanelRefresh(string condition)
    {
        var facts = Facts(); facts = condition switch
        {
            "lease-expired" => facts with { CorrelationExpiresUtc = Now }, "lease-missing" => facts with { CorrelationExpiresUtc = null },
            "lease-implausible" => facts with { CorrelationExpiresUtc = Now.AddHours(1) }, "source-stale" => facts with { ObservedUtc = Now.AddSeconds(-16) },
            "source-future" => facts with { ObservedUtc = Now.AddTicks(1) }, "user-stale" => facts with { UserIdentityObservedUtc = Now.AddSeconds(-16) },
            "destination-stale" => facts with { CurrentDestination = facts.CurrentDestination! with { ObservedUtc = Now.AddSeconds(-16) } },
            "user-future" => facts with { UserIdentityObservedUtc = Now.AddTicks(1) }, _ => facts with { CurrentDestination = facts.CurrentDestination! with { ObservedUtc = Now.AddTicks(1) } }
        };
        var view = Project(Operation(), Desktop(Row()), facts); Assert.Equal(NativeTrialProcessPresence.Observed, view.Presence);
        Assert.Equal(NativeTrialEvidenceOutcome.Unknown, view.Outcome); NoAuthority(view);
        if (condition.StartsWith("user", StringComparison.Ordinal)) Assert.Null(view.ReportedUser.UserId);
        else if (condition.StartsWith("destination", StringComparison.Ordinal)) Assert.Null(view.ReportedDestination.PlaceId);
        else { Assert.Null(view.ReportedUser.UserId); Assert.Null(view.ReportedDestination.PlaceId); }
    }

    [Theory]
    [InlineData(ObservedSessionLifecycle.Teleporting)]
    [InlineData(ObservedSessionLifecycle.Disconnected)]
    [InlineData(ObservedSessionLifecycle.Exited)]
    [InlineData(ObservedSessionLifecycle.Unknown)]
    [InlineData(ObservedSessionLifecycle.Joining)]
    public void TransitionsNeverShowAnOldServerAsTheCurrentRequestedDestination(ObservedSessionLifecycle lifecycle)
    {
        var facts = Facts() with { Lifecycle = lifecycle };
        var view = Project(Operation(), Desktop(Row()), facts);
        Assert.Null(view.ReportedDestination.PlaceId); Assert.Null(view.ReportedDestination.JobId);
        Assert.Equal(42, view.LastKnownDestination.PlaceId); Assert.Equal(Job, view.LastKnownDestination.JobId);
        Assert.Equal(facts.CurrentDestination!.ObservedUtc, view.LastKnownDestination.ObservedUtc);
        Assert.Equal(NativeTrialReportedStatus.LastKnownOnly, view.ReportedDestination.PlaceStatus); Assert.Equal(NativeTrialEvidenceOutcome.Unknown, view.Outcome); NoAuthority(view);
    }

    [Fact]
    public void InvalidatedRegistryFactsRetainOnlyExplicitHistoricalFieldsWithOriginalTimes()
    {
        var at = Now.AddMinutes(-2); var facts = Facts() with { Stream = null, CorrelationKind = null, Confidence = SessionEvidenceConfidence.Unknown,
            Lifecycle = ObservedSessionLifecycle.Unknown, ObservedUserId = null, UserIdentityObservedUtc = null, CurrentDestination = null,
            LastKnownUserId = 123, LastKnownUserIdentityUtc = at, LastKnownDestination = new(42, Job, null, at) };
        var view = Project(Operation(), Desktop(Row()), facts);
        Assert.Null(view.ReportedUser.UserId); Assert.Null(view.ReportedDestination.PlaceId); Assert.Equal(123, view.LastKnownUser.UserId);
        Assert.Equal(at, view.LastKnownUser.ObservedUtc); Assert.Equal(at, view.LastKnownDestination.ObservedUtc); Assert.Equal(NativeTrialEvidenceOutcome.Unknown, view.Outcome);
    }

    [Fact]
    public void IndependentOwnershipStillProducesOnlyUnverifiedReportedComparisons()
    {
        var facts = Facts() with { CorrelationKind = SessionLogCorrelationKind.IndependentlyCorroborated, CorrelationExpiresUtc = null, Confidence = SessionEvidenceConfidence.CorroboratedDiagnostic };
        var view = Project(Operation(), Desktop(Row()), facts); Assert.Equal(NativeTrialEvidenceOutcome.ReportedMatchUnverified, view.Outcome); NoAuthority(view);
    }

    [Fact]
    public void MissingSelectedOperationAndMalformedTargetDoNotExposeArbitraryInput()
    {
        var operation = Operation(); var absent = NativeTrialEvidence.Project(new(false, [operation]), Guid.NewGuid(), Desktop(Row()), [Facts()], Now);
        Assert.Null(absent.ExpectedUserId); Assert.Null(absent.ProcessReceipt); Assert.Equal(NativeTrialEvidenceOutcome.Unknown, absent.Outcome);
        var malformed = operation with { Intent = operation.Intent with { RequestedJobId = "synthetic-secret-not-a-job" } };
        var view = Project(malformed, Desktop(Row()), Facts()); Assert.Null(view.RequestedJobId); Assert.Equal(NativeTrialEvidenceOutcome.Unknown, view.Outcome);
        Assert.DoesNotContain("synthetic-secret", JsonSerializer.Serialize(view)); NoAuthority(view);
    }

    [Fact]
    public void BoundsUseIndexedSnapshotsAndRejectOversizeInputsWithoutNativeOrBackgroundWork()
    {
        var operation = Operation(); var operations = Enumerable.Range(0, 1100).Select(_ => Operation() with { ProcessReceipt = null }).Append(operation).ToArray();
        var rows = Enumerable.Range(0, 4095).Select(i => Row() with { ProcessId = i + 1 }).Append(Row()).ToArray();
        var facts = Enumerable.Range(0, 127).Select(i => Facts() with { Process = new(null, i + 1, Start) }).Append(Facts()).ToArray();
        var queue = new LaunchQueueSnapshot(false, new IndexedOnly<LaunchOperation>(operations)); var desktop = Desktop() with { Sessions = new IndexedOnly<SessionObservation>(rows) };
        var view = NativeTrialEvidence.Project(queue, operation.Intent.OperationId, desktop, new IndexedOnly<SessionEvidenceSnapshot>(facts), Now);
        Assert.Equal(NativeTrialProcessPresence.Observed, view.Presence); Assert.Equal(NativeTrialEvidenceOutcome.ReportedMatchUnverified, view.Outcome); NoAuthority(view);
        Assert.Throws<ArgumentException>(() => NativeTrialEvidence.Project(queue with { Operations = operations.Append(Operation()).ToArray() }, operation.Intent.OperationId, desktop, facts, Now));
        Assert.Throws<ArgumentException>(() => Project(operation, desktop with { Sessions = rows.Append(Row()).ToArray() }, facts));
        Assert.Throws<ArgumentException>(() => Project(operation, Desktop(Row()), facts.Append(Facts()).ToArray()));
    }

    [Fact]
    public void ProjectManyMatchesProjectPerOperation()
    {
        var observed = Operation();
        var shared = Operation() with { ProcessReceipt = new(700002, Start, Now.AddMinutes(-5)) };
        var sharedSibling = shared with { Intent = shared.Intent with { OperationId = Guid.NewGuid(), ProfileId = Guid.NewGuid() } };
        var duplicated = Operation() with { ProcessReceipt = null };
        var invalid = Operation(); invalid = invalid with { Intent = invalid.Intent with { RequestedJobId = "synthetic-not-a-job" }, ProcessReceipt = new(700004, Start, Now.AddMinutes(-5)) };
        var reused = Operation() with { ProcessReceipt = new(700003, Start + 1, Now.AddMinutes(-5)) };
        var noReceipt = Operation() with { ProcessReceipt = null };
        var queue = new LaunchQueueSnapshot(false, [observed, shared, sharedSibling, duplicated, duplicated, invalid, reused, noReceipt]);
        var desktop = Desktop(Row(), Row() with { ProcessId = 700002 }, Row() with { ProcessId = 700003 });
        SessionEvidenceSnapshot[] facts = [Facts()];
        Guid[] ids = [observed.Intent.OperationId, shared.Intent.OperationId, sharedSibling.Intent.OperationId, duplicated.Intent.OperationId,
            invalid.Intent.OperationId, reused.Intent.OperationId, noReceipt.Intent.OperationId, Guid.Empty, Guid.NewGuid(), observed.Intent.OperationId];
        var views = NativeTrialEvidence.ProjectMany(queue, ids, desktop, facts, Now);
        Assert.Equal(ids.Length, views.Count);
        for (int index = 0; index < ids.Length; index++) Assert.Equal(NativeTrialEvidence.Project(queue, ids[index], desktop, facts, Now), views[index]);
        Assert.Equal(NativeTrialProcessPresence.Observed, views[0].Presence); Assert.Equal(NativeTrialEvidenceOutcome.ReportedMatchUnverified, views[0].Outcome);
        Assert.Equal(NativeTrialProcessPresence.Ambiguous, views[1].Presence); Assert.Equal(NativeTrialProcessPresence.Ambiguous, views[2].Presence);
        Assert.Equal(NativeTrialProcessPresence.Ambiguous, views[3].Presence); Assert.Null(views[3].ExpectedUserId);
        Assert.Null(views[4].RequestedJobId); Assert.Equal(NativeTrialProcessPresence.Unavailable, views[4].Presence); Assert.Same(invalid.ProcessReceipt, views[4].ProcessReceipt);
        Assert.Equal(NativeTrialProcessPresence.PidReused, views[5].Presence); Assert.Equal(NativeTrialProcessPresence.NoReceipt, views[6].Presence);
        Assert.Equal(NativeTrialProcessPresence.Unavailable, views[7].Presence); Assert.Equal(NativeTrialProcessPresence.Unavailable, views[8].Presence);
        Assert.Equal(views[0], views[9]); foreach (var view in views) NoAuthority(view);
        Assert.Throws<ArgumentException>(() => NativeTrialEvidence.ProjectMany(queue, new Guid[NativeTrialEvidence.MaximumOperations + 1], desktop, facts, Now));
    }

    [Fact]
    public void ProjectManyReadsEachQueueAndProcessRowOnceForEveryRequestedTrial()
    {
        // Mechanical cost check: one batch reads each shared snapshot row once, not once per requested trial.
        var operations = Enumerable.Range(0, 64).Select(i => Operation() with { ProcessReceipt = new(800000 + i, Start, Now.AddMinutes(-5)) }).ToArray();
        var rows = operations.Select(operation => Row() with { ProcessId = operation.ProcessReceipt!.ProcessId }).ToArray();
        var queueRows = new CountingIndexed<LaunchOperation>(operations); var processRows = new CountingIndexed<SessionObservation>(rows);
        var ids = operations.Take(32).Select(operation => operation.Intent.OperationId).ToArray();
        var views = NativeTrialEvidence.ProjectMany(new(false, queueRows), ids, Desktop() with { Sessions = processRows }, [], Now);
        Assert.Equal(32, views.Count); Assert.All(views, view => Assert.Equal(NativeTrialProcessPresence.Observed, view.Presence));
        Assert.Equal(operations.Length, queueRows.Reads); Assert.Equal(rows.Length, processRows.Reads);
    }

    private sealed class IndexedOnly<T>(IReadOnlyList<T> items) : IReadOnlyList<T>
    {
        public int Count => items.Count; public T this[int index] => items[index];
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Unbounded enumeration is forbidden in this fixture.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class CountingIndexed<T>(IReadOnlyList<T> items) : IReadOnlyList<T>
    {
        public int Reads { get; private set; }
        public int Count => items.Count; public T this[int index] { get { Reads++; return items[index]; } }
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Unbounded enumeration is forbidden in this fixture.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
