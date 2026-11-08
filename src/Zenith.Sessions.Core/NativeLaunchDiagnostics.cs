namespace Zenith.Sessions.Core;

public enum NativeLaunchDiagnosticStage { VerifyingExecutable, PreparingHandoff, Prepared, Dispatching, Finished, Stopped }
public enum NativeLaunchCleanupStatus { NotStarted, Pending, Complete, Failed }
public static class NativeLaunchDiagnosticLimits { public const int MaximumSnapshots = 128; }

/// <summary>Fixed capture return facts only (the mode that produced them included); no capture, credential, candidate, URI, ticket or protected mutation ID.</summary>
public sealed record NativeBrowserPreparationFacts(RobloxHandoffProbeOutcome Outcome, NativeHandoffCaptureStatus? ShapeStatus = null,
    DateTimeOffset? ObservedUtc = null, NativeLaunchMode Mode = NativeLaunchMode.OfficialPage)
{
    public NativeBrowserPreparationFacts Validated()
    {
        if (!Enum.IsDefined(Outcome) || ShapeStatus.HasValue && !Enum.IsDefined(ShapeStatus.Value) || ObservedUtc is { } time && time == default
            || !Enum.IsDefined(Mode))
            throw new ArgumentException("The native browser diagnostic facts are invalid.");
        return this;
    }
}

/// <summary>
/// Transient facts for LaunchIntent.OperationId only, never NativeHandoffContext.OperationId.
/// Null outcomes mean not reached or not recorded. Times are actual wall-clock records; Revision
/// supplies ordering across clock adjustments. No field grants launch, account, arrival or input authority.
/// </summary>
public sealed record NativeLaunchDiagnosticSnapshot(Guid OperationId, long Revision, DateTimeOffset StartedUtc,
    DateTimeOffset UpdatedUtc, NativeLaunchDiagnosticStage Stage, NativeLaunchCleanupStatus CleanupStatus,
    bool Stopped = false, ClientExecutableTrustStatus? TrustStatus = null, ClientExecutableTrustReason? TrustReason = null,
    NativeHandoffPreparationStatus? PreparationStatus = null, RobloxHandoffProbeOutcome? BrowserOutcome = null,
    NativeHandoffCaptureStatus? ShapeStatus = null, NativeHandoffAttemptStatus? DispatchStatus = null,
    DateTimeOffset? ExecutableObservedUtc = null, DateTimeOffset? PreparationObservedUtc = null,
    DateTimeOffset? BrowserObservedUtc = null, DateTimeOffset? DispatchObservedUtc = null, DateTimeOffset? CleanupObservedUtc = null,
    NativeLaunchMode? LaunchMode = null)
{
    public NativeLaunchDiagnosticSnapshot Validated()
    {
        if (OperationId == Guid.Empty || Revision <= 0 || StartedUtc == default || UpdatedUtc == default
            || !Enum.IsDefined(Stage) || !Enum.IsDefined(CleanupStatus) || Stopped != (Stage == NativeLaunchDiagnosticStage.Stopped)
            || LaunchMode.HasValue && !Enum.IsDefined(LaunchMode.Value)
            || TrustStatus.HasValue != TrustReason.HasValue
            || TrustStatus.HasValue && !Enum.IsDefined(TrustStatus.Value)
            || TrustReason.HasValue && !Enum.IsDefined(TrustReason.Value)
            || TrustStatus == ClientExecutableTrustStatus.VerifiedRobloxPublisher && TrustReason != ClientExecutableTrustReason.Verified
            || TrustReason == ClientExecutableTrustReason.Verified && TrustStatus != ClientExecutableTrustStatus.VerifiedRobloxPublisher
            || PreparationStatus.HasValue && !Enum.IsDefined(PreparationStatus.Value)
            || BrowserOutcome.HasValue && !Enum.IsDefined(BrowserOutcome.Value)
            || ShapeStatus.HasValue && !Enum.IsDefined(ShapeStatus.Value)
            || DispatchStatus.HasValue && !Enum.IsDefined(DispatchStatus.Value)
            || ExecutableObservedUtc == default(DateTimeOffset) || PreparationObservedUtc == default(DateTimeOffset)
            || BrowserObservedUtc == default(DateTimeOffset) || DispatchObservedUtc == default(DateTimeOffset)
            || CleanupObservedUtc == default(DateTimeOffset))
            throw new ArgumentException("The native launch diagnostic snapshot is invalid.");
        return this;
    }
}

/// <summary>
/// Read-only optional current-run evidence. Reading never starts, cancels, validates or restamps work.
/// Retained immutable metadata remains readable after provider disposal; no native resource is revived.
/// </summary>
public interface INativeLaunchDiagnosticSource
{
    NativeLaunchDiagnosticSnapshot? GetDiagnostic(Guid operationId);
}
