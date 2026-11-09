namespace Zenith.MacroHost.Contracts;

/// <summary>Separate from the metadata endpoint. Wire integers are stable within protocol v1.</summary>
public static class ForegroundControlProtocol
{
    public const string PipeName = "Zenith.Macros.Personal.Foreground.v1";
}

public readonly record struct ForegroundControlRequest(Guid HostSessionId, Guid MacroId, Guid SavedRevision,
    long Window, int ProcessId, long CreationFileTime)
{
    public bool IsValid => HostSessionId != Guid.Empty && MacroId != Guid.Empty && SavedRevision != Guid.Empty
        && Window > 0 && Window != 0xffff && ProcessId > 0 && CreationFileTime is > 0 and <= 2_650_467_743_999_999_999;
}

public enum ForegroundControlState : byte
{
    Rejected = 0, Preparing = 1, Running = 2, Paused = 3, Stopping = 4, Finished = 5
}

public enum ForegroundControlOutcome : byte
{
    None = 0, InvalidRequest = 1, Busy = 2, Stopped = 3, PreparationRejected = 4, TargetUnavailable = 5,
    InputHeld = 6, LicenseRequired = 7, Completed = 8, Faulted = 9, CleanupFailed = 10
}

public readonly record struct ForegroundControlSnapshot(ForegroundControlState State,
    ForegroundControlOutcome Outcome = ForegroundControlOutcome.None);

public readonly record struct ForegroundControlAdmission(IForegroundControlOperation? Operation, ForegroundControlOutcome Rejection);

/// <summary>One operation owned by one authorized connection. Completion joins its actual cleanup.</summary>
public interface IForegroundControlOperation
{
    Guid Id { get; }
    ForegroundControlSnapshot Snapshot { get; }
    Task Completion { get; }
    Task<bool> PauseAsync(CancellationToken ct);
    Task<bool> ResumeAsync(CancellationToken ct);
    void Stop();
}
