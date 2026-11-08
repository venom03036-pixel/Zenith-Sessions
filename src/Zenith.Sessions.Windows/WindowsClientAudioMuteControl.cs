using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <param name="Muted">Whether every audio session of the client is muted; null unless <paramref name="Status"/> is Unchanged.</param>
internal sealed record ClientAudioRead(ClientAudioMuteStatus Status, bool? Muted = null, bool Retired = false);
internal sealed record ClientAudioWrite(bool Accepted, ClientAudioMuteStatus Status, bool? Muted = null, bool Retired = false);
internal sealed record ClientAudioOpen(ClientAudioMuteStatus Status, IClientAudioHandle? Handle = null, bool Retired = false);
internal interface IClientAudioHandle : IDisposable
{
    /// <summary>
    /// Unchanged with <see cref="ClientAudioRead.Muted"/> when the exact client has audio sessions that all agree; NoAudioSession
    /// when it has none; Conflict when only some are muted.
    /// </summary>
    ClientAudioRead Read();
    /// <summary>
    /// A mute sets every session muted only while all still read <paramref name="expected"/>. Turning the sound on sets every session
    /// on, also when only some are muted or <paramref name="expected"/> is null (the client was read partly muted); Conflict with
    /// <see cref="ClientAudioWrite.Muted"/> false when the whole client is found already on instead of the muted state read before.
    /// </summary>
    ClientAudioWrite Change(bool? expected, bool desired, Func<bool> stopped);
}
internal interface IClientAudioAdapter
{
    ClientAudioOpen Open(SessionObservation session);
}

/// <summary>
/// One actual worker and no queued requests. No native operation runs on the caller's thread. The restore map holds every exact
/// client instance this owner muted, is bounded at 128 and never evicts one it still has to unmute: Windows may remember a mute for
/// later clients of the same program, so Zenith unmutes what it muted on Unmute, Restore and Exit. Windows does this when a muted
/// client closes (a client started while another is muted starts with its sound on), so a client that started after this owner's
/// first mute and is found already muted is owned too: Zenith's own mute may be why it is muted.
/// </summary>
public sealed class WindowsClientAudioMuteControl : IClientAudioMuteControl
{
    private readonly object _gate = new();
    private readonly IClientAudioAdapter _adapter;
    private readonly TimeProvider _time;
    private readonly Dictionary<SessionProcessKey, SessionObservation> _muted = [];
    private long _firstMuteUtcTicks = long.MaxValue; // Under _gate: when this owner's first possibly accepted mute was sent.
    private Task<ClientAudioMuteBatchResult>? _active;
    private Task? _dispose;
    private long _generation;
    private bool _closed;

    public WindowsClientAudioMuteControl() : this(new NativeClientAudioMuteAdapter()) { }
    internal WindowsClientAudioMuteControl(IClientAudioAdapter adapter, TimeProvider? time = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _time = time ?? TimeProvider.System;
    }

    public int PendingRestoreCount { get { lock (_gate) return _muted.Count; } }
    public void CancelOperations() => Interlocked.Increment(ref _generation);

    public Task<ClientAudioMuteBatchResult> ChangeAsync(IReadOnlyList<SessionObservation> sessions, ClientAudioMuteAction action, CancellationToken ct)
    {
        long generation = Interlocked.Read(ref _generation);
        ArgumentNullException.ThrowIfNull(sessions);
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        int count = sessions.Count;
        if (count > ClientAudioMuteBatchResult.MaximumProcesses)
            throw new ArgumentException("Select at most 128 client processes in one sound operation.", nameof(sessions));
        var pinned = new SessionObservation[count];
        for (int index = 0; index < count; index++)
            pinned[index] = sessions[index] ?? throw new ArgumentException("Each selected client must be present.", nameof(sessions));
        lock (_gate)
        {
            if (_closed) return Task.FromResult(Refused(pinned, action, ClientAudioMuteStatus.Closed));
            if (_active is { IsCompleted: false }) return Task.FromResult(Refused(pinned, action, ClientAudioMuteStatus.Busy));
            if (pinned.Length == 0) return Task.FromResult(new ClientAudioMuteBatchResult([], Stopped(generation, ct, false), _muted.Count));
            return _active = Task.Run(() => Run(pinned, action, generation, ct), CancellationToken.None);
        }
    }

    public Task<ClientAudioMuteBatchResult> RestoreAllAsync(CancellationToken ct)
    {
        long generation = Interlocked.Read(ref _generation);
        lock (_gate)
        {
            var pinned = _muted.Values.ToArray();
            if (_closed) return Task.FromResult(Refused(pinned, ClientAudioMuteAction.RestoreOriginal, ClientAudioMuteStatus.Closed));
            if (_active is { IsCompleted: false }) return Task.FromResult(Refused(pinned, ClientAudioMuteAction.RestoreOriginal, ClientAudioMuteStatus.Busy));
            if (pinned.Length == 0) return Task.FromResult(new ClientAudioMuteBatchResult([], Stopped(generation, ct, false), 0));
            return _active = Task.Run(() => Run(pinned, ClientAudioMuteAction.RestoreOriginal, generation, ct, ownedSelection: true), CancellationToken.None);
        }
    }

    private bool Stopped(long generation, CancellationToken ct, bool cleanup)
        => !cleanup && (ct.IsCancellationRequested || generation != Interlocked.Read(ref _generation));

    private ClientAudioMuteBatchResult Run(SessionObservation[] pinned, ClientAudioMuteAction action, long generation, CancellationToken ct,
        bool ownedSelection = false, bool cleanup = false)
    {
        // Restore ownership can hold two different starts of a recycled PID; each stored instance is checked on its own.
        var duplicatePids = ownedSelection ? null
            : pinned.GroupBy(session => session.ProcessId).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        var results = new List<ClientAudioMuteResult>(pinned.Length);
        foreach (var session in pinned)
        {
            if (Stopped(generation, ct, cleanup))
                results.Add(Result(session, action, ClientAudioMuteStatus.Cancelled));
            else if (!Key(session).IsValid || session.Problem is not null || duplicatePids?.Contains(session.ProcessId) == true)
                results.Add(Result(session, action, ClientAudioMuteStatus.InvalidClient));
            else
                results.Add(ChangeOne(session, action, () => Stopped(generation, ct, cleanup)));
        }
        return new(results, Stopped(generation, ct, cleanup), PendingRestoreCount);
    }

    private ClientAudioMuteResult ChangeOne(SessionObservation session, ClientAudioMuteAction action, Func<bool> stopped)
    {
        var key = Key(session);
        bool owned;
        lock (_gate) owned = _muted.ContainsKey(key);
        if (action == ClientAudioMuteAction.RestoreOriginal && !owned) return Result(session, action, ClientAudioMuteStatus.Unchanged);
        ClientAudioOpen opened;
        try { opened = _adapter.Open(session); }
        catch (Exception) { return Result(session, action, ClientAudioMuteStatus.NotConfirmed); }
        using var handle = opened.Handle;
        if (handle is null)
        {
            if (opened.Retired) Forget(key);
            return Result(session, action, Failure(opened.Status));
        }
        if (stopped()) return Result(session, action, ClientAudioMuteStatus.Cancelled);
        ClientAudioRead before;
        try { before = handle.Read(); }
        catch (Exception) { return Result(session, action, ClientAudioMuteStatus.NotConfirmed); }
        if (before.Retired)
        {
            Forget(key);
            return Result(session, action, ClientAudioMuteStatus.InvalidClient);
        }
        bool desired = action == ClientAudioMuteAction.Mute; // Unmute and RestoreOriginal both turn the sound on.
        // Only some sessions muted (Read's Conflict): turning the sound on sets every one on, the safe direction the user asked for;
        // a mute leaves it as found.
        bool partly = !desired && before.Status == ClientAudioMuteStatus.Conflict;
        if (!partly && (before.Status != ClientAudioMuteStatus.Unchanged || before.Muted is null))
            return Result(session, action, Failure(before.Status));
        bool? muted = before.Muted; // Null when only part of the client's sound is muted.
        // Sound Zenith muted that is on again was turned on outside Zenith: there is nothing left to restore.
        if (owned && muted == false)
        {
            Forget(key);
            owned = false;
            if (action == ClientAudioMuteAction.RestoreOriginal) return Result(session, action, ClientAudioMuteStatus.Unchanged, false, false);
        }
        if (stopped()) return Result(session, action, ClientAudioMuteStatus.Cancelled, muted, muted);
        if (muted == desired)
        {
            // Found muted after starting later than this owner's first mute: Windows may have kept Zenith's mute for it, so it is
            // unmuted on Unmute and Exit like a client Zenith muted. Nothing is written.
            if (desired && !owned)
                lock (_gate)
                    if (session.ProcessStartUtcTicks > _firstMuteUtcTicks)
                    {
                        if (_muted.Count == ClientAudioMuteBatchResult.MaximumProcesses)
                            return Result(session, action, ClientAudioMuteStatus.CapacityReached, muted, muted);
                        _muted[key] = session;
                    }
            return Result(session, action, ClientAudioMuteStatus.Unchanged, muted, muted);
        }
        long dispatched = _time.GetUtcNow().UtcTicks;
        if (desired)
        {
            lock (_gate)
            {
                if (_muted.Count == ClientAudioMuteBatchResult.MaximumProcesses)
                    return Result(session, action, ClientAudioMuteStatus.CapacityReached, muted, muted);
                // Reserve ownership before native dispatch, including a mute Windows accepts just before an error.
                _muted[key] = session;
            }
        }
        ClientAudioWrite write;
        try { write = handle.Change(muted, desired, stopped); }
        catch (Exception)
        {
            if (desired) NoteMute(dispatched);
            return Result(session, action, ClientAudioMuteStatus.NotConfirmed, muted);
        }
        if (desired && write.Accepted) NoteMute(dispatched);
        if (!write.Accepted)
        {
            // Nothing was written: a refused mute owns nothing, and a refused unmute keeps ownership unless the client is gone
            // or its sound is already on.
            bool alreadyOn = !desired && write.Status == ClientAudioMuteStatus.Conflict && write.Muted == false;
            if (desired || write.Retired || alreadyOn) Forget(key);
            return Result(session, action, alreadyOn ? ClientAudioMuteStatus.Unchanged : Failure(write.Status), muted, write.Muted);
        }
        // Cancellation, or an error after one session was set, cannot erase an accepted change: always read it back.
        ClientAudioRead after;
        try { after = handle.Read(); }
        catch (Exception) { return Result(session, action, ClientAudioMuteStatus.NotConfirmed, muted); }
        if (after.Retired)
        {
            Forget(key);
            return Result(session, action, ClientAudioMuteStatus.InvalidClient, muted);
        }
        if (after.Status != ClientAudioMuteStatus.Unchanged || after.Muted != desired)
            return Result(session, action, ClientAudioMuteStatus.NotConfirmed, muted, after.Muted);
        if (!desired) Forget(key);
        return Result(session, action, action == ClientAudioMuteAction.RestoreOriginal
            ? ClientAudioMuteStatus.Restored : ClientAudioMuteStatus.Applied, muted, desired);
    }

    private ClientAudioMuteResult Result(SessionObservation session, ClientAudioMuteAction action, ClientAudioMuteStatus status,
        bool? previous = null, bool? current = null)
    {
        lock (_gate) return new(session, action, status, previous, current, _muted.ContainsKey(Key(session)));
    }
    private ClientAudioMuteBatchResult Refused(SessionObservation[] pinned, ClientAudioMuteAction action, ClientAudioMuteStatus status)
        => new(pinned.Select(session => Result(session, action, status)).ToArray(), false, _muted.Count, status);
    private void Forget(SessionProcessKey key) { lock (_gate) _muted.Remove(key); }
    /// <summary>A mute Windows accepted, or may have accepted, was dispatched at <paramref name="utcTicks"/>.</summary>
    private void NoteMute(long utcTicks) { lock (_gate) _firstMuteUtcTicks = Math.Min(_firstMuteUtcTicks, utcTicks); }
    private static SessionProcessKey Key(SessionObservation session) => new(null, session.ProcessId, session.ProcessStartUtcTicks);
    private static ClientAudioMuteStatus Failure(ClientAudioMuteStatus status)
        => status is ClientAudioMuteStatus.InvalidClient or ClientAudioMuteStatus.AccessDenied or ClientAudioMuteStatus.NoAudioSession
            or ClientAudioMuteStatus.Conflict or ClientAudioMuteStatus.Cancelled ? status : ClientAudioMuteStatus.NotConfirmed;

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            _closed = true; CancelOperations();
            return new(_dispose = DisposeCoreAsync(_active));
        }
    }
    private async Task DisposeCoreAsync(Task<ClientAudioMuteBatchResult>? active)
    {
        if (active is not null) await active.ConfigureAwait(false);
        Task<ClientAudioMuteBatchResult> restore;
        lock (_gate)
        {
            var pinned = _muted.Values.ToArray();
            if (pinned.Length == 0) return;
            restore = _active = Task.Run(() => Run(pinned, ClientAudioMuteAction.RestoreOriginal,
                Interlocked.Read(ref _generation), CancellationToken.None, ownedSelection: true, cleanup: true), CancellationToken.None);
        }
        var result = await restore.ConfigureAwait(false);
        if (result.PendingRestoreCount > 0) throw new ClientAudioMuteCleanupException(result.PendingRestoreCount);
    }
}
