namespace Zenith.Sessions.Core;

public enum ClientWorkingSetTrimStatus { Trimmed, InvalidClient, AccessDenied, Cancelled, NotConfirmed, Busy, Closed }

/// <summary>Private working set and private commit, both read through the same exact process handle.</summary>
public sealed record ClientMemoryReading(long? PrivateWorkingSetBytes, long PrivateCommitBytes);

public static class ClientWorkingSetTrimPolicy
{
    public const int MaximumProcesses = 128;
    /// <summary>At most one trim per essential sample tick, so a batch never faults every client's pages at once.</summary>
    public static TimeSpan TrimSpacing => CapacityPolicy.EssentialResourceSampleInterval;
    /// <summary>The after reading is taken about this long after each client's own trim.</summary>
    public static readonly TimeSpan AfterReadingDelay = TimeSpan.FromSeconds(10);
}

public sealed record ClientWorkingSetTrimResult(SessionObservation Session, ClientWorkingSetTrimStatus Status,
    ClientMemoryReading? Before = null, ClientMemoryReading? After = null)
{
    public string Explanation => Status switch
    {
        ClientWorkingSetTrimStatus.Trimmed when Before is not null && After is not null
            => $"Private working set {Size(Before.PrivateWorkingSetBytes)} before, {Size(After.PrivateWorkingSetBytes)} about "
               + $"{ClientWorkingSetTrimPolicy.AfterReadingDelay.TotalSeconds:0} s after. Private commit {Size(Before.PrivateCommitBytes)} before, "
               + $"{Size(After.PrivateCommitBytes)} after. A trim does not free committed memory, and any benefit is unmeasured.",
        ClientWorkingSetTrimStatus.Trimmed
            => "Windows accepted the trim. No after reading was taken because the work stopped or the client exited first.",
        ClientWorkingSetTrimStatus.InvalidClient => "The exact selected client process is no longer available or could not be validated. It was not trimmed.",
        ClientWorkingSetTrimStatus.AccessDenied => "Windows denied the process query or trim. It was not trimmed.",
        ClientWorkingSetTrimStatus.Cancelled => "This client was not trimmed because the work was stopped.",
        ClientWorkingSetTrimStatus.Busy => "Another trim is still running. No additional trim was queued.",
        ClientWorkingSetTrimStatus.Closed => "Memory trim is closed.",
        _ => "Windows could not confirm the trim."
    };

    private static string Size(long? bytes) => bytes is { } value ? $"{value / (1024d * 1024):N0} MiB" : "unavailable";
}

public sealed class ClientWorkingSetTrimBatchResult
{
    public ClientWorkingSetTrimBatchResult(IReadOnlyList<ClientWorkingSetTrimResult> results, bool cancelled,
        ClientWorkingSetTrimStatus? rejection = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        int count = results.Count;
        if (count is < 0 or > ClientWorkingSetTrimPolicy.MaximumProcesses) throw new ArgumentOutOfRangeException(nameof(results));
        if (rejection is not (null or ClientWorkingSetTrimStatus.Busy or ClientWorkingSetTrimStatus.Closed))
            throw new ArgumentOutOfRangeException(nameof(rejection));
        var copy = new ClientWorkingSetTrimResult[count];
        for (int index = 0; index < count; index++)
            copy[index] = results[index] ?? throw new ArgumentException("A trim outcome must identify its client.", nameof(results));
        Results = Array.AsReadOnly(copy);
        Cancelled = cancelled;
        Rejection = rejection;
    }

    public IReadOnlyList<ClientWorkingSetTrimResult> Results { get; }
    public bool Cancelled { get; }
    public ClientWorkingSetTrimStatus? Rejection { get; }
    public string Explanation => Rejection switch
    {
        ClientWorkingSetTrimStatus.Busy => "Another trim is still running. No additional trim was queued.",
        ClientWorkingSetTrimStatus.Closed => "Memory trim is closed.",
        _ => $"{Results.Count(result => result.Status == ClientWorkingSetTrimStatus.Trimmed)} of {Results.Count} clients trimmed. "
             + "Commit is unchanged by a trim, and any benefit is unmeasured."
    };
}

/// <summary>
/// Explicit one-shot working-set trims for exact client process instances. A trim asks Windows to move a
/// client's resident pages out of its working set; it does not free commit, and pages the client touches
/// again fault back in. No benefit is claimed: before and after readings are reported, not interpreted.
/// At most one trim runs per essential sample tick. Cancellation prevents later trims and skips pending
/// after readings; it cannot recall a trim Windows already accepted. Nothing is queued while work is active.
/// </summary>
public interface IClientWorkingSetTrimControl : IAsyncDisposable
{
    Task<ClientWorkingSetTrimBatchResult> TrimAsync(IReadOnlyList<SessionObservation> sessions, CancellationToken ct);
    void CancelOperations();
}
