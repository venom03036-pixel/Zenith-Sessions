namespace Zenith.Sessions.Windows;

public enum DiagnosticLevel { Info, Warning, Error }

/// <summary>Typed observations only. Summary text is application-owned and never includes payloads.</summary>
public sealed record ManagerDiagnosticEvent(long Sequence, DateTimeOffset Utc, ManagerEventKind Kind, string? ErrorType, long Count = 0)
{
    public DiagnosticLevel Level => Kind switch
    {
        ManagerEventKind.OperationFailed => DiagnosticLevel.Error,
        ManagerEventKind.Panic or ManagerEventKind.DroppedEvents => DiagnosticLevel.Warning,
        _ => DiagnosticLevel.Info
    };
    public string Summary => Kind switch
    {
        ManagerEventKind.Started => "Manager started.",
        ManagerEventKind.ProfileSaved => "Saved profile metadata.",
        ManagerEventKind.ProfilesRemoved => "Removed saved profile metadata.",
        ManagerEventKind.ProfilesImported => "Imported profile metadata.",
        ManagerEventKind.MetadataExported => "Exported profile metadata.",
        ManagerEventKind.SettingsSaved => "Saved manager settings.",
        ManagerEventKind.AuthorizationLinked => "Roblox sign-in saved after identity verification.",
        ManagerEventKind.AuthorizationChecked => "Saved sign-in re-verified and saved again.",
        ManagerEventKind.HandoffInspected => "Native handoff inspected and cancelled; no client started.",
        ManagerEventKind.HandoffNotCompleted => "Handoff inspection was closed, busy or timed out; no client started.",
        ManagerEventKind.AuthorizationDisconnected => "Local authorization disconnected.",
        ManagerEventKind.OperationFailed => "An operation failed or needs review.",
        ManagerEventKind.Panic => "Emergency stop requested.",
        ManagerEventKind.ControlsLocked => "Manager controls locked.",
        ManagerEventKind.ControlsUnlocked => "Manager controls unlocked.",
        ManagerEventKind.Shutdown => "Manager shutdown recorded.",
        ManagerEventKind.DroppedEvents => "Some disk diagnostic events were skipped.",
        _ => "Unrecognized diagnostic event."
    };
}

/// <summary>
/// Counts cover accepted observations since this instance started, without disk replay. Events are
/// an immutable ascending-sequence copy of at most 512 newest observations. DroppedEvents counts
/// observations not confirmed written to disk; those observations remain eligible for the live ring.
/// </summary>
public sealed record ManagerDiagnosticSnapshot(long LatestSequence, long TotalEvents, long InfoCount, long WarningCount,
    long ErrorCount, long DroppedEvents, string? Failure, IReadOnlyList<ManagerDiagnosticEvent> Events);
