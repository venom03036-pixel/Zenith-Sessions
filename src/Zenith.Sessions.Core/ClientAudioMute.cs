namespace Zenith.Sessions.Core;

/// <summary>
/// Mute turns off the sound of the client's own Windows audio sessions (the Volume mixer entry), and Zenith unmutes it again
/// on Unmute, Restore or Exit. Unmute turns the sound on, also for a client Zenith did not mute, for example one that started
/// muted because Windows remembered an earlier mute; that unmute is never undone. RestoreOriginal unmutes only what Zenith muted.
/// Nothing in the client's files, settings or memory is read or changed.
/// </summary>
public enum ClientAudioMuteAction { Mute, Unmute, RestoreOriginal }
public enum ClientAudioMuteStatus
{
    Applied, Restored, Unchanged, InvalidClient, AccessDenied, NoAudioSession, Conflict, CapacityReached, Cancelled, NotConfirmed, Busy, Closed
}

/// <param name="Previous">Whether the client's sound was muted before this operation; null when Windows did not report it.</param>
/// <param name="Current">Whether the client's sound was muted after this operation; null when Windows did not report it.</param>
public sealed record ClientAudioMuteResult(SessionObservation Session, ClientAudioMuteAction Action, ClientAudioMuteStatus Status,
    bool? Previous, bool? Current, bool RestorePending)
{
    public string Explanation => Status switch
    {
        ClientAudioMuteStatus.Applied when Action == ClientAudioMuteAction.Mute
            => "Windows confirmed this client's sound is muted. Zenith unmutes it on Unmute or Exit. If the client closes while muted, Windows may start its next client muted too.",
        ClientAudioMuteStatus.Applied => "Windows confirmed this client's sound is on.",
        ClientAudioMuteStatus.Restored => "Windows confirmed the sound Zenith muted is on again.",
        ClientAudioMuteStatus.Unchanged when Action == ClientAudioMuteAction.Mute && RestorePending
            => "This client's sound was already muted, by Zenith or because Windows kept a mute Zenith made for an earlier client. Zenith unmutes it on Unmute or Exit.",
        ClientAudioMuteStatus.Unchanged when Action == ClientAudioMuteAction.Mute
            => "This client's sound was already muted. Zenith did not change it and will not unmute it on Exit.",
        ClientAudioMuteStatus.Unchanged when Action == ClientAudioMuteAction.RestoreOriginal && Previous is null
            => "Zenith did not mute this client, so there was nothing to restore.",
        ClientAudioMuteStatus.Unchanged => "This client's sound was already on.",
        ClientAudioMuteStatus.InvalidClient => "The exact selected client process is no longer available or could not be validated.",
        ClientAudioMuteStatus.AccessDenied => "Windows denied the process query or the sound change.",
        ClientAudioMuteStatus.NoAudioSession
            => "Windows reports no sound for this client yet, so nothing was changed. A client gets a Volume mixer entry when it first plays sound.",
        ClientAudioMuteStatus.Conflict
            => "This client's sound was changed outside Zenith, for example in Volume mixer, or only part of it is muted. The current state was preserved.",
        ClientAudioMuteStatus.CapacityReached => "The 128-client restore limit is full. Unmute clients Zenith muted before muting another.",
        ClientAudioMuteStatus.Cancelled => "This client was not changed because the operation was stopped.",
        ClientAudioMuteStatus.Busy => "Another sound operation is still running. No additional operation was queued.",
        ClientAudioMuteStatus.Closed => "Sound controls are closed. Clients Zenith muted stay listed for restoration.",
        _ => "Windows could not confirm the sound change. Zenith keeps any client it muted listed for restoration."
    };
}

public sealed class ClientAudioMuteBatchResult
{
    public const int MaximumProcesses = 128;

    public ClientAudioMuteBatchResult(IReadOnlyList<ClientAudioMuteResult> results, bool cancelled, int pendingRestoreCount,
        ClientAudioMuteStatus? rejection = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        int count = results.Count;
        if (count > MaximumProcesses) throw new ArgumentOutOfRangeException(nameof(results));
        if (pendingRestoreCount is < 0 or > MaximumProcesses) throw new ArgumentOutOfRangeException(nameof(pendingRestoreCount));
        if (rejection is not (null or ClientAudioMuteStatus.Busy or ClientAudioMuteStatus.Closed))
            throw new ArgumentOutOfRangeException(nameof(rejection));
        var copy = new ClientAudioMuteResult[count];
        for (int index = 0; index < count; index++)
            copy[index] = results[index] ?? throw new ArgumentException("A sound outcome must identify its client.", nameof(results));
        Results = Array.AsReadOnly(copy);
        Cancelled = cancelled;
        PendingRestoreCount = pendingRestoreCount;
        Rejection = rejection;
    }

    public IReadOnlyList<ClientAudioMuteResult> Results { get; }
    public bool Cancelled { get; }
    /// <summary>How many clients Zenith muted and still has to unmute.</summary>
    public int PendingRestoreCount { get; }
    public ClientAudioMuteStatus? Rejection { get; }
    public string Explanation => Rejection switch
    {
        ClientAudioMuteStatus.Busy => "Another sound operation is still running. No additional operation was queued.",
        ClientAudioMuteStatus.Closed => "Sound controls are closed. Clients Zenith muted stay listed for restoration.",
        _ => $"{Results.Count(result => result.Status is ClientAudioMuteStatus.Applied or ClientAudioMuteStatus.Restored or ClientAudioMuteStatus.Unchanged)} of {Results.Count} sound outcomes confirmed. {PendingRestoreCount} muted by Zenith and not yet unmuted."
    };
}

public sealed class ClientAudioMuteCleanupException(int pendingRestoreCount)
    : Exception("Windows could not confirm that every client Zenith muted was unmuted.")
{
    public int PendingRestoreCount { get; } = pendingRestoreCount;
}

/// <summary>
/// Explicit sound operations on exact process instances. A result says nothing about account identity. Cancellation prevents
/// later changes and cannot recall one Windows already accepted. Dispose joins actual work and then unmutes every client this
/// owner muted, with its own cleanup lifetime.
/// </summary>
public interface IClientAudioMuteControl : IAsyncDisposable
{
    int PendingRestoreCount { get; }
    Task<ClientAudioMuteBatchResult> ChangeAsync(IReadOnlyList<SessionObservation> sessions, ClientAudioMuteAction action, CancellationToken ct);
    void CancelOperations();
    Task<ClientAudioMuteBatchResult> RestoreAllAsync(CancellationToken ct);
}
