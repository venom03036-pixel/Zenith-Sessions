namespace Zenith.Sessions.Core;

public enum NativeLaunchDiagnosticStage { VerifyingExecutable, PreparingHandoff, Prepared, Dispatching, Finished, Stopped }
public enum NativeLaunchCleanupStatus { NotStarted, Pending, Complete, Failed }
public static class NativeLaunchDiagnosticLimits { public const int MaximumSnapshots = 128; }

/// <summary>Fixed capture return facts only (the mode that produced them included); no capture, credential, candidate, URI, ticket or protected mutation ID.</summary>
public sealed record NativeBrowserPreparationFacts(RobloxHandoffProbeOutcome Outcome, NativeHandoffCaptureStatus? ShapeStatus = null,
    DateTimeOffset? ObservedUtc = null, NativeLaunchMode Mode = NativeLaunchMode.OfficialPage, NativeHandoffRejectionReason? RejectionReason = null)
{
    public NativeBrowserPreparationFacts Validated()
    {
        if (!Enum.IsDefined(Outcome) || ShapeStatus.HasValue && !Enum.IsDefined(ShapeStatus.Value) || ObservedUtc is { } time && time == default
            || !Enum.IsDefined(Mode) || !NativeHandoffRejectionDiagnostics.IsValid(ShapeStatus, RejectionReason))
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
    NativeLaunchMode? LaunchMode = null, NativeHandoffRejectionReason? RejectionReason = null)
{
    public NativeLaunchDiagnosticSnapshot Validated()
    {
        if (OperationId == Guid.Empty || Revision <= 0 || StartedUtc == default || UpdatedUtc == default
            || !Enum.IsDefined(Stage) || !Enum.IsDefined(CleanupStatus) || Stopped != (Stage == NativeLaunchDiagnosticStage.Stopped)
            || LaunchMode.HasValue && !Enum.IsDefined(LaunchMode.Value)
            || TrustStatus.HasValue != TrustReason.HasValue
            || TrustStatus.HasValue && !Enum.IsDefined(TrustStatus.Value)
            || TrustReason.HasValue && !Enum.IsDefined(TrustReason.Value)
            // Verified pairs only with a verified status: the Roblox player's publisher, or a bootstrapper's accepted publisher (R-launch-17).
            || TrustStatus is ClientExecutableTrustStatus.VerifiedRobloxPublisher or ClientExecutableTrustStatus.VerifiedAcceptedPublisher && TrustReason != ClientExecutableTrustReason.Verified
            || TrustReason == ClientExecutableTrustReason.Verified && TrustStatus is not (ClientExecutableTrustStatus.VerifiedRobloxPublisher or ClientExecutableTrustStatus.VerifiedAcceptedPublisher)
            || PreparationStatus.HasValue && !Enum.IsDefined(PreparationStatus.Value)
            || BrowserOutcome.HasValue && !Enum.IsDefined(BrowserOutcome.Value)
            || ShapeStatus.HasValue && !Enum.IsDefined(ShapeStatus.Value)
            || !NativeHandoffRejectionDiagnostics.IsValid(ShapeStatus, RejectionReason)
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
