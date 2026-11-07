namespace Zenith.Sessions.Core;

public enum NativeTrialProcessPresence { Unavailable, NoReceipt, InvalidReceipt, Incomplete, Stale, NotObserved, PidReused, Ambiguous, Observed }
public enum NativeTrialReportedStatus { Unknown, Uncorrelated, Ambiguous, Stale, LastKnownOnly, NotRequested, ReportedMatchUnverified, ReportedMismatchUnverified, ReadingLog }
public enum NativeTrialEvidenceOutcome { Unknown, ReportedMatchUnverified, ReportedMismatchUnverified }
public sealed record NativeTrialReportedUser(long? UserId, DateTimeOffset? ObservedUtc, NativeTrialReportedStatus Status);
public sealed record NativeTrialReportedDestination(long? PlaceId, Guid? JobId, DateTimeOffset? ObservedUtc,
    NativeTrialReportedStatus PlaceStatus, NativeTrialReportedStatus JobStatus);
public sealed record NativeTrialEvidenceView(Guid OperationId, long? ExpectedUserId, long? RequestedPlaceId, string? RequestedJobId,
    LaunchProcessReceipt? ProcessReceipt, NativeTrialProcessPresence Presence, DateTimeOffset? ProcessObservedUtc, DateTimeOffset? ProcessMeasurementUtc,
    NativeTrialReportedUser ReportedUser, NativeTrialReportedDestination ReportedDestination, NativeTrialReportedUser LastKnownUser,
    NativeTrialReportedDestination LastKnownDestination, NativeTrialEvidenceOutcome Outcome, string Explanation)
{
    public bool CanAuthorizeAccountAction => false;
    public bool ArrivalVerified => false;
    public bool CanRouteInput => false;
}

/// <summary>
/// Bounded trial feedback only. A process using a log does not prove who wrote it. Even an exact
/// fresh process and matching reported IDs never establish account selection, arrival or an input target.
/// </summary>
public static class NativeTrialEvidence
{
    public const int MaximumOperations = 1101, MaximumObservations = 4096, MaximumLogViews = 128;
    public static readonly TimeSpan MaximumProcessAge = TimeSpan.FromSeconds(3), MaximumLogAge = TimeSpan.FromSeconds(15);
    private static readonly NativeTrialReportedUser UnknownUser = new(null, null, NativeTrialReportedStatus.Unknown);
    private static readonly NativeTrialReportedDestination UnknownDestination = new(null, null, null, NativeTrialReportedStatus.Unknown, NativeTrialReportedStatus.Unknown);

    public static NativeTrialEvidenceView Project(LaunchQueueSnapshot queue, Guid operationId, DesktopSnapshot? desktop,
        IReadOnlyList<SessionEvidenceSnapshot> evidence, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(queue); ArgumentNullException.ThrowIfNull(evidence);
        if (queue.Operations is null) throw new ArgumentException("A bounded launch queue is required.");
        int operationCount = queue.Operations.Count, logCount = evidence.Count, rowCount = desktop?.Sessions?.Count ?? 0;
        if (operationCount is < 0 or > MaximumOperations || logCount is < 0 or > MaximumLogViews || rowCount is < 0 or > MaximumObservations || now == default)
            throw new ArgumentException("Trial feedback requires bounded queue, process and diagnostic snapshots and a current time.");
        LaunchOperation? selected = null; int selections = 0; var operations = new LaunchOperation[operationCount];
        for (int index = 0; index < operationCount; index++)
        {
            var item = queue.Operations[index];
            if (item?.Intent is null) throw new ArgumentException("The queue contains an invalid operation.");
            operations[index] = item;
            if (item.Intent.OperationId == operationId) { selected = item; selections++; }
        }
        if (operationId == Guid.Empty || selections != 1 || selected is null)
            return Empty(operationId, selections > 1 ? NativeTrialProcessPresence.Ambiguous : NativeTrialProcessPresence.Unavailable,
                "The selected trial is unavailable or ambiguous in the current queue. No account, process or arrival is established.");
        var intent = selected.Intent; var receipt = selected.ProcessReceipt;
        long? user = intent.UserId > 0 ? intent.UserId : null, place = intent.PlaceId > 0 ? intent.PlaceId : null;
        string? job = null;
        if (intent.RequestedJobId is { } requested)
        {
            if (!RobloxRequestedDestination.TryNormalizeJobId(requested, out job) || job != requested)
                return Empty(operationId, NativeTrialProcessPresence.Unavailable, "The selected trial destination is invalid. No reported values were associated.")
                    with { ExpectedUserId = user, RequestedPlaceId = place, ProcessReceipt = receipt };
        }
        DateTimeOffset? inventoryAt = desktop?.ProcessObservedUtc, measuredAt = null;
        var presence = NativeTrialProcessPresence.NoReceipt;
        if (receipt is not null)
        {
            if (!receipt.IsValid || receipt.RecordedUtc > now) presence = NativeTrialProcessPresence.InvalidReceipt;
            else
            {
                int receipts = 0;
                for (int index = 0; index < operationCount; index++)
                    if (operations[index].ProcessReceipt is { IsValid: true } sibling && sibling.ProcessId == receipt.ProcessId
                        && sibling.ProcessStartUtcTicks == receipt.ProcessStartUtcTicks) receipts++;
                presence = receipts != 1 ? NativeTrialProcessPresence.Ambiguous : Presence(desktop, rowCount, receipt, now, out measuredAt);
            }
        }
        var view = Empty(operationId, presence, Explanation(presence)) with
        { ExpectedUserId = user, RequestedPlaceId = place, RequestedJobId = job, ProcessReceipt = receipt, ProcessObservedUtc = inventoryAt, ProcessMeasurementUtc = measuredAt };
        if (receipt is not { IsValid: true } || presence is NativeTrialProcessPresence.Ambiguous or NativeTrialProcessPresence.InvalidReceipt) return view;
        SessionEvidenceSnapshot? facts = null; int matches = 0;
        for (int index = 0; index < logCount; index++)
        {
            var item = evidence[index];
            if (item is not null && item.Process.HostId is null && item.Process.ProcessId == receipt.ProcessId && item.Process.ProcessStartUtcTicks == receipt.ProcessStartUtcTicks)
            { facts = item; matches++; }
        }
        if (matches > 1) return WithUnknown(view, NativeTrialReportedStatus.Ambiguous,
            "Multiple diagnostic views match this process. Reported identity and destination remain unknown; no account or arrival is established.");
        if (facts is null) return view;
        if (facts.Stream is { } selectedStream)
            for (int index = 0; index < logCount; index++)
                if (evidence[index] is { } other && other.Stream == selectedStream && other.Process != facts.Process)
                    return WithUnknown(view, NativeTrialReportedStatus.Ambiguous, "A diagnostic stream has conflicting process mappings. No reported account or destination is associated.");
        bool unsupportedMapping = facts.CorrelationKind is SessionLogCorrelationKind.TimingOnly or SessionLogCorrelationKind.ManualLabel;
        if (!unsupportedMapping)
        {
            bool canRememberCurrent = !facts.AwaitingLogTail && facts.SessionId != Guid.Empty && facts.Stream is { IsValid: true } && facts.Process.IsValid
                && facts.Confidence is SessionEvidenceConfidence.UnverifiedDiagnostic or SessionEvidenceConfidence.CorroboratedDiagnostic
                && facts.CorrelationKind is SessionLogCorrelationKind.ResourceHolderDiagnostic or SessionLogCorrelationKind.IndependentlyCorroborated;
            var knownUser = LatestUser(facts, receipt, now, canRememberCurrent);
            var knownDestination = LatestDestination(facts, receipt, now, canRememberCurrent);
            view = view with { LastKnownUser = knownUser, LastKnownDestination = knownDestination };
        }
        if (facts.Ambiguous || !Enum.IsDefined(facts.Confidence) || !Enum.IsDefined(facts.Lifecycle))
            return WithUnknown(view, NativeTrialReportedStatus.Ambiguous, "Diagnostic correlation is ambiguous. Last-known values are separate and unverified.");
        bool sourceFresh = FactTime(facts.ObservedUtc, receipt, now) && Fresh(facts.ObservedUtc, now, MaximumLogAge);
        bool correlated = !unsupportedMapping && facts.SessionId != Guid.Empty && facts.Process.IsValid && facts.Stream is { IsValid: true }
            && (facts.AwaitingLogTail || facts.Confidence is SessionEvidenceConfidence.UnverifiedDiagnostic or SessionEvidenceConfidence.CorroboratedDiagnostic)
            && facts.CorrelationKind is SessionLogCorrelationKind.ResourceHolderDiagnostic or SessionLogCorrelationKind.IndependentlyCorroborated;
        bool leased = facts.CorrelationKind != SessionLogCorrelationKind.ResourceHolderDiagnostic
            ? facts.CorrelationExpiresUtc is null || facts.CorrelationExpiresUtc > now
            : facts.CorrelationExpiresUtc is { } expiry && expiry > now && expiry - now <= TimeSpan.FromSeconds(30);
        if (!correlated || !leased) return WithUnknown(view, NativeTrialReportedStatus.Uncorrelated,
            "A current diagnostic relationship is unavailable. File use or a manual label does not prove who wrote log values.");
        if (!sourceFresh || presence != NativeTrialProcessPresence.Observed)
            return WithUnknown(view, NativeTrialReportedStatus.Stale, "Current process or diagnostic evidence is not fresh. Original last-known timestamps are retained without an arrival claim.");
        if (facts.AwaitingLogTail)
            return WithUnknown(view, NativeTrialReportedStatus.ReadingLog, "More log data is waiting to be read. Current player and destination are unknown.");
        bool active = facts.Lifecycle is ObservedSessionLifecycle.Connected or ObservedSessionLifecycle.Joining;
        var reportedUser = UnknownUser;
        if (active && facts.ObservedUserId is > 0 && facts.UserIdentityObservedUtc is { } userAt
            && FactTime(userAt, receipt, now) && userAt <= facts.ObservedUtc && Fresh(userAt, now, MaximumLogAge))
            reportedUser = new(facts.ObservedUserId, userAt, Compare(facts.ObservedUserId, user));
        else if (view.LastKnownUser.UserId is not null) reportedUser = new(null, facts.UserIdentityObservedUtc, active ? NativeTrialReportedStatus.Stale : NativeTrialReportedStatus.LastKnownOnly);
        var reportedDestination = UnknownDestination;
        if (facts.Lifecycle == ObservedSessionLifecycle.Connected && ValidDestination(facts.CurrentDestination, receipt, now) is { } current
            && current.ObservedUtc <= facts.ObservedUtc && Fresh(current.ObservedUtc, now, MaximumLogAge))
            reportedDestination = new(current.PlaceId, current.JobId, current.ObservedUtc, Compare(current.PlaceId, place),
                job is null ? NativeTrialReportedStatus.NotRequested : Compare(current.JobId.ToString("D"), job));
        else if (view.LastKnownDestination.PlaceId is not null)
            reportedDestination = new(null, null, facts.CurrentDestination?.ObservedUtc, facts.Lifecycle == ObservedSessionLifecycle.Connected
                ? NativeTrialReportedStatus.Stale : NativeTrialReportedStatus.LastKnownOnly, facts.Lifecycle == ObservedSessionLifecycle.Connected
                ? NativeTrialReportedStatus.Stale : NativeTrialReportedStatus.LastKnownOnly);
        var outcome = reportedUser.Status == NativeTrialReportedStatus.ReportedMismatchUnverified
            || reportedDestination.PlaceStatus == NativeTrialReportedStatus.ReportedMismatchUnverified || reportedDestination.JobStatus == NativeTrialReportedStatus.ReportedMismatchUnverified
            ? NativeTrialEvidenceOutcome.ReportedMismatchUnverified
            : reportedUser.Status == NativeTrialReportedStatus.ReportedMatchUnverified && reportedDestination.PlaceStatus == NativeTrialReportedStatus.ReportedMatchUnverified
                && (job is null || reportedDestination.JobStatus == NativeTrialReportedStatus.ReportedMatchUnverified)
                ? NativeTrialEvidenceOutcome.ReportedMatchUnverified : NativeTrialEvidenceOutcome.Unknown;
        return view with { ReportedUser = reportedUser, ReportedDestination = reportedDestination, Outcome = outcome,
            Explanation = outcome == NativeTrialEvidenceOutcome.ReportedMismatchUnverified
                ? "Unverified reported values differ from the expected account or destination. This is diagnostic evidence, not an authenticated account mapping."
                : "Reported values are unverified diagnostics. Even matching IDs and an observed process do not prove account selection or arrival." };
    }

    private static NativeTrialProcessPresence Presence(DesktopSnapshot? desktop, int count, LaunchProcessReceipt receipt, DateTimeOffset now, out DateTimeOffset? measured)
    {
        measured = null;
        if (desktop is null || desktop.Sessions is null) return NativeTrialProcessPresence.Unavailable;
        if (desktop.Problem is not null || !desktop.ProcessInventoryComplete || desktop.ProcessRevision <= 0 || desktop.ProcessObservedUtc is null)
            return NativeTrialProcessPresence.Incomplete;
        if (!Fresh(desktop.ProcessObservedUtc.Value, now, MaximumProcessAge)) return NativeTrialProcessPresence.Stale;
        int pids = 0; SessionObservation? found = null; bool invalid = false;
        var seen = new HashSet<int>();
        for (int index = 0; index < count; index++)
        {
            var row = desktop.Sessions[index];
            if (row is null || row.ProcessId <= 0 || row.ProcessStartUtcTicks <= 0 || row.Problem is not null || row.ObservedUtc == default
                || row.ProcessStartUtcTicks > row.ObservedUtc.UtcTicks || row.ObservedUtc > desktop.ProcessObservedUtc || !Fresh(row.ObservedUtc, now, MaximumProcessAge)) invalid = true;
            if (row is null) continue;
            if (!seen.Add(row.ProcessId)) invalid = true;
            if (row.ProcessId == receipt.ProcessId) { pids++; found = row; }
        }
        if (pids > 1) return NativeTrialProcessPresence.Ambiguous;
        if (invalid) return NativeTrialProcessPresence.Incomplete;
        if (found is null) return NativeTrialProcessPresence.NotObserved;
        measured = found.ObservedUtc;
        return found.ProcessStartUtcTicks == receipt.ProcessStartUtcTicks ? NativeTrialProcessPresence.Observed : NativeTrialProcessPresence.PidReused;
    }
    private static NativeTrialReportedUser LatestUser(SessionEvidenceSnapshot facts, LaunchProcessReceipt receipt, DateTimeOffset now, bool canRememberCurrent)
    {
        long? user = null; DateTimeOffset? at = null;
        if (facts.LastKnownUserId is > 0 && facts.LastKnownUserIdentityUtc is { } known && FactTime(known, receipt, now) && known <= facts.ObservedUtc) { user = facts.LastKnownUserId; at = known; }
        if (canRememberCurrent && !facts.Ambiguous && facts.ObservedUserId is > 0 && facts.UserIdentityObservedUtc is { } current
            && FactTime(current, receipt, now) && current <= facts.ObservedUtc && (at is null || current > at)) { user = facts.ObservedUserId; at = current; }
        return user is null ? UnknownUser : new(user, at, NativeTrialReportedStatus.LastKnownOnly);
    }
    private static NativeTrialReportedDestination LatestDestination(SessionEvidenceSnapshot facts, LaunchProcessReceipt receipt, DateTimeOffset now, bool canRememberCurrent)
    {
        var known = ValidDestination(facts.LastKnownDestination, receipt, now);
        if (known?.ObservedUtc > facts.ObservedUtc) known = null;
        var current = facts.Ambiguous || !canRememberCurrent ? null : ValidDestination(facts.CurrentDestination, receipt, now);
        if (current?.ObservedUtc > facts.ObservedUtc) current = null;
        if (current is not null && (known is null || current.ObservedUtc > known.ObservedUtc)) known = current;
        return known is null ? UnknownDestination : new(known.PlaceId, known.JobId, known.ObservedUtc, NativeTrialReportedStatus.LastKnownOnly, NativeTrialReportedStatus.LastKnownOnly);
    }
    private static ObservedSessionDestination? ValidDestination(ObservedSessionDestination? destination, LaunchProcessReceipt receipt, DateTimeOffset now)
        => destination is { PlaceId: > 0 } && destination.JobId != Guid.Empty && destination.UniverseId is not <= 0 && FactTime(destination.ObservedUtc, receipt, now) ? destination : null;
    private static bool FactTime(DateTimeOffset value, LaunchProcessReceipt receipt, DateTimeOffset now) => value != default && value <= now && value.UtcTicks >= receipt.ProcessStartUtcTicks;
    private static bool Fresh(DateTimeOffset value, DateTimeOffset now, TimeSpan age) => value != default && value <= now && now - value <= age;
    private static NativeTrialReportedStatus Compare(long? reported, long? expected) => expected is null ? NativeTrialReportedStatus.Unknown
        : reported == expected ? NativeTrialReportedStatus.ReportedMatchUnverified : NativeTrialReportedStatus.ReportedMismatchUnverified;
    private static NativeTrialReportedStatus Compare(string? reported, string? expected) => expected is null ? NativeTrialReportedStatus.Unknown
        : string.Equals(reported, expected, StringComparison.Ordinal) ? NativeTrialReportedStatus.ReportedMatchUnverified : NativeTrialReportedStatus.ReportedMismatchUnverified;
    private static NativeTrialEvidenceView Empty(Guid id, NativeTrialProcessPresence presence, string explanation) => new(id, null, null, null, null, presence,
        null, null, UnknownUser, UnknownDestination, UnknownUser, UnknownDestination, NativeTrialEvidenceOutcome.Unknown, explanation);
    private static NativeTrialEvidenceView WithUnknown(NativeTrialEvidenceView view, NativeTrialReportedStatus status, string explanation) => view with
    { ReportedUser = new(null, null, status), ReportedDestination = new(null, null, null, status, status), Outcome = NativeTrialEvidenceOutcome.Unknown, Explanation = explanation };
    private static string Explanation(NativeTrialProcessPresence presence) => presence switch
    {
        NativeTrialProcessPresence.Observed => "The exact process instance is currently observed. Account and destination remain unverified; no reported log facts are available.",
        NativeTrialProcessPresence.NotObserved => "The exact process was not observed in this fresh complete inventory. Exit cause and account or arrival are unknown.",
        NativeTrialProcessPresence.PidReused => "The PID now describes a different process start. Its windows, labels and log values cannot identify the original trial process.",
        NativeTrialProcessPresence.Ambiguous => "Process receipts or observations are ambiguous. No process-to-account association is made.",
        _ => "A current exact process observation is unavailable. A receipt is historical, and unknown or stale evidence is not arrival proof."
    };
}
