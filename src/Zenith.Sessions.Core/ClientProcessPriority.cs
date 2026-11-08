namespace Zenith.Sessions.Core;

/// <summary>
/// Normal and BelowNormal change only the priority class. Efficiency also turns on EcoQoS power throttling and very low
/// memory priority. SpreadCores divides the CPU cores among the selected clients it can change, so the shares of one operation
/// never overlap; clients spread in separate operations can share cores. CapCpu puts the client in a Windows job this run
/// creates for it and caps the job at a share of all CPU time (<see cref="IClientProcessPriorityControl.CapCpuAsync"/>
/// carries the share). RestoreOriginal returns every value this owner changed to the client's own original; for a cap
/// that clears the job's rate control, because Windows never takes a process out of a job.
/// </summary>
public enum ClientProcessPriorityAction { Normal, BelowNormal, RestoreOriginal, Efficiency, SpreadCores, CapCpu }
public enum ClientProcessPriorityClass { Normal, BelowNormal, Unsupported }
public enum ClientProcessPriorityStatus
{
    Applied, Restored, Unchanged, InvalidClient, AccessDenied, UnsupportedPriority,
    Conflict, CapacityReached, Cancelled, NotConfirmed, Busy, Closed, SettingUnavailable, NotEnoughCores,
    /// <summary>The client already belongs to a Windows job this run did not create (another program's, or one an earlier run of Zenith left), so its CPU cannot be capped from here.</summary>
    AlreadyInJob
}

public sealed record ClientProcessPriorityResult(SessionObservation Session, ClientProcessPriorityAction Action,
    ClientProcessPriorityStatus Status, ClientProcessPriorityClass? Previous, ClientProcessPriorityClass? Requested,
    ClientProcessPriorityClass? Current, bool RestorePending)
{
    public string Explanation => Status switch
    {
        ClientProcessPriorityStatus.Applied when Action == ClientProcessPriorityAction.Efficiency
            => "Windows confirmed efficiency mode: Below normal priority, EcoQoS power throttling and very low memory priority.",
        ClientProcessPriorityStatus.Applied when Action == ClientProcessPriorityAction.SpreadCores
            => "Windows confirmed this client's share of the CPU cores. No other client changed in the same operation uses them.",
        ClientProcessPriorityStatus.Applied when Action == ClientProcessPriorityAction.CapCpu
            => "Windows confirmed the CPU cap: this client's threads together get at most the chosen share of all CPU time on this PC. Restore original lifts it.",
        ClientProcessPriorityStatus.Applied => "The requested process priority was confirmed by Windows.",
        ClientProcessPriorityStatus.Restored => "Windows confirmed the client's original priority, efficiency, core and CPU cap settings.",
        ClientProcessPriorityStatus.Unchanged when Action == ClientProcessPriorityAction.RestoreOriginal && Requested is null
            => "No original setting was tracked for this process.",
        ClientProcessPriorityStatus.Unchanged => "No change was needed.",
        ClientProcessPriorityStatus.InvalidClient => "The exact selected client process is no longer available or could not be validated.",
        ClientProcessPriorityStatus.AccessDenied => "Windows denied the required process query or setting change.",
        ClientProcessPriorityStatus.UnsupportedPriority => "The current priority is outside Normal and Below normal. It was preserved.",
        ClientProcessPriorityStatus.Conflict => "Another change to this client's priority, efficiency, core or CPU cap setting was observed. The current settings were preserved.",
        ClientProcessPriorityStatus.SettingUnavailable => "Windows did not report this client's current efficiency, core or CPU cap setting, so it was not changed.",
        ClientProcessPriorityStatus.AlreadyInJob => "This client already belongs to a Windows job this run of Zenith did not create (another program's, or one left by an earlier run of Zenith), so its CPU cannot be capped from here. Windows keeps a process in its job for life; it was left as it is.",
        ClientProcessPriorityStatus.NotEnoughCores => "Spreading needs at least two selected clients that can be changed, and two CPU cores for each. Nothing was changed.",
        ClientProcessPriorityStatus.CapacityReached => "The 128-process restore limit is full. Restore existing settings before changing another process.",
        ClientProcessPriorityStatus.Cancelled => "This client was not changed because the operation was stopped.",
        ClientProcessPriorityStatus.Busy => "Another priority operation still owns its actual work. No additional operation was queued.",
        ClientProcessPriorityStatus.Closed => "Priority controls are closed. Outstanding restoration ownership remains visible.",
        _ => "Windows could not confirm the requested priority. Any outstanding restoration ownership was retained."
    };
}

public sealed class ClientProcessPriorityBatchResult
{
    public ClientProcessPriorityBatchResult(IReadOnlyList<ClientProcessPriorityResult> results, bool cancelled, int pendingRestoreCount,
        ClientProcessPriorityStatus? rejection = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        int count = results.Count;
        if (count is < 0 or > MaximumProcesses) throw new ArgumentOutOfRangeException(nameof(results));
        if (pendingRestoreCount is < 0 or > MaximumProcesses) throw new ArgumentOutOfRangeException(nameof(pendingRestoreCount));
        if (rejection is not (null or ClientProcessPriorityStatus.Busy or ClientProcessPriorityStatus.Closed))
            throw new ArgumentOutOfRangeException(nameof(rejection));
        var copy = new ClientProcessPriorityResult[count];
        for (int index = 0; index < count; index++)
            copy[index] = results[index] ?? throw new ArgumentException("A priority outcome must identify its client.", nameof(results));
        Results = Array.AsReadOnly(copy);
        Cancelled = cancelled;
        PendingRestoreCount = pendingRestoreCount;
        Rejection = rejection;
    }

    public const int MaximumProcesses = 128;
    public IReadOnlyList<ClientProcessPriorityResult> Results { get; }
    public bool Cancelled { get; }
    public int PendingRestoreCount { get; }
    public ClientProcessPriorityStatus? Rejection { get; }
    public bool AllApplied => Rejection is null && Results.Count > 0 && Results.All(result => result.Status is
        ClientProcessPriorityStatus.Applied or ClientProcessPriorityStatus.Restored or ClientProcessPriorityStatus.Unchanged);
    public string Explanation => Rejection switch
    {
        ClientProcessPriorityStatus.Busy => "Another priority operation still owns its actual work. No additional operation was queued.",
        ClientProcessPriorityStatus.Closed => "Priority controls are closed. Outstanding restoration ownership remains visible.",
        _ => $"{Results.Count(result => result.Status is ClientProcessPriorityStatus.Applied or ClientProcessPriorityStatus.Restored or ClientProcessPriorityStatus.Unchanged)} of {Results.Count} priority outcomes confirmed. {PendingRestoreCount} original settings remain pending restoration."
    };
}

public sealed class ClientProcessPriorityCleanupException(int pendingRestoreCount)
    : Exception("Some original client priorities could not be restored. Outstanding restoration ownership was retained.")
{
    public int PendingRestoreCount { get; } = pendingRestoreCount;
}

/// <summary>
/// What one start found of the CPU caps an earlier run recorded and left behind: clients still running in the job that run
/// capped them in (a cap no later run can lift, because a job loses its name and can be reached only through a handle the
/// crashed run held), clients that could not be checked, whether the record could be read, and whether the pass was stopped.
/// </summary>
public sealed record ClientCpuCapLeftovers(int StillCapped, int NotChecked, bool RecordUnreadable, bool Stopped);

/// <summary>
/// Explicit process operations only. A result establishes neither account identity nor a CPU,
/// memory or FPS benefit. Cancellation prevents later mutations and cannot recall an accepted set.
/// Dispose joins actual work and attempts restoration with an independent cleanup lifetime.
/// </summary>
public interface IClientProcessPriorityControl : IAsyncDisposable
{
    int PendingRestoreCount { get; }
    /// <summary>Every action except <see cref="ClientProcessPriorityAction.CapCpu"/>, which needs its share and goes through <see cref="CapCpuAsync"/>.</summary>
    Task<ClientProcessPriorityBatchResult> ChangeAsync(IReadOnlyList<SessionObservation> sessions,
        ClientProcessPriorityAction action, CancellationToken ct);
    /// <summary>
    /// Caps each client's threads, together, at <paramref name="percentOfAllCpus"/> (<see cref="MinimumCpuCapPercent"/> to
    /// <see cref="MaximumCpuCapPercent"/>) of all CPU time on this PC, through a Windows job this run creates for that client,
    /// keeps a handle to while the client lives, and records so a later start can name clients it left capped. A client already
    /// in a job this run did not create is refused as <see cref="ClientProcessPriorityStatus.AlreadyInJob"/>. Restore original
    /// clears the cap.
    /// </summary>
    Task<ClientProcessPriorityBatchResult> CapCpuAsync(IReadOnlyList<SessionObservation> sessions, int percentOfAllCpus, CancellationToken ct);
    /// <summary>Once, at startup: names the recorded clients that still run in the job an earlier run capped them in, and drops the records of clients that have exited. Nothing is changed.</summary>
    Task<ClientCpuCapLeftovers> CheckLeftoverCpuCapsAsync(CancellationToken ct);
    void CancelOperations();
    Task<ClientProcessPriorityBatchResult> RestoreAllAsync(CancellationToken ct);
    const int MinimumCpuCapPercent = 5, MaximumCpuCapPercent = 95;
}
