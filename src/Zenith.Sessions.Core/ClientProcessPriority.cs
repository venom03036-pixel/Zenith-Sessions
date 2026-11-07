namespace Zenith.Sessions.Core;

public enum ClientProcessPriorityAction { Normal, BelowNormal, RestoreOriginal }
public enum ClientProcessPriorityClass { Normal, BelowNormal, Unsupported }
public enum ClientProcessPriorityStatus
{
    Applied, Restored, Unchanged, InvalidClient, AccessDenied, UnsupportedPriority,
    Conflict, CapacityReached, Cancelled, NotConfirmed, Busy, Closed
}

public sealed record ClientProcessPriorityResult(SessionObservation Session, ClientProcessPriorityAction Action,
    ClientProcessPriorityStatus Status, ClientProcessPriorityClass? Previous, ClientProcessPriorityClass? Requested,
    ClientProcessPriorityClass? Current, bool RestorePending)
{
    public string Explanation => Status switch
    {
        ClientProcessPriorityStatus.Applied => "The requested process priority was confirmed by Windows.",
        ClientProcessPriorityStatus.Restored => "The original process priority was confirmed by Windows.",
        ClientProcessPriorityStatus.Unchanged when Action == ClientProcessPriorityAction.RestoreOriginal && Requested is null
            => "No original priority was tracked for this process.",
        ClientProcessPriorityStatus.Unchanged => "No priority change was needed.",
        ClientProcessPriorityStatus.InvalidClient => "The exact selected client process is no longer available or could not be validated.",
        ClientProcessPriorityStatus.AccessDenied => "Windows denied the required process query or priority change.",
        ClientProcessPriorityStatus.UnsupportedPriority => "The current priority is outside Normal and Below normal. It was preserved.",
        ClientProcessPriorityStatus.Conflict => "The observed priority changed. The current setting was preserved.",
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
/// Explicit process operations only. A result establishes neither account identity nor a CPU,
/// memory or FPS benefit. Cancellation prevents later mutations and cannot recall an accepted set.
/// Dispose joins actual work and attempts restoration with an independent cleanup lifetime.
/// </summary>
public interface IClientProcessPriorityControl : IAsyncDisposable
{
    int PendingRestoreCount { get; }
    Task<ClientProcessPriorityBatchResult> ChangeAsync(IReadOnlyList<SessionObservation> sessions,
        ClientProcessPriorityAction action, CancellationToken ct);
    void CancelOperations();
    Task<ClientProcessPriorityBatchResult> RestoreAllAsync(CancellationToken ct);
}
