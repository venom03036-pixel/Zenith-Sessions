using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

internal sealed record ClientPriorityRead(ClientProcessPriorityStatus Status, ClientProcessPriorityClass? Current = null, bool Retired = false);
internal sealed record ClientPriorityWrite(bool Accepted, ClientProcessPriorityStatus Status, ClientProcessPriorityClass? Current = null, bool Retired = false);
internal sealed record ClientPriorityOpen(ClientProcessPriorityStatus Status, IClientPriorityHandle? Handle = null, bool Retired = false);
internal interface IClientPriorityHandle : IDisposable
{
    ClientPriorityRead Read();
    ClientPriorityWrite Change(ClientProcessPriorityClass expected, ClientProcessPriorityClass desired, Func<bool> stopped);
}
internal interface IClientPriorityAdapter
{
    ClientPriorityOpen Open(SessionObservation session);
}

/// <summary>
/// One actual worker and no queued requests. The adapter is borrowed. No native operation runs on
/// the caller's thread. A set and its unconditional readback share the same short-lived handle.
/// The restore map is bounded and never evicts unresolved ownership.
/// </summary>
public sealed class WindowsClientProcessPriorityControl : IClientProcessPriorityControl
{
    private readonly object _gate = new();
    private readonly IClientPriorityAdapter _adapter;
    private readonly Dictionary<SessionProcessKey, OwnedPriority> _owned = [];
    private Task<ClientProcessPriorityBatchResult>? _active;
    private Task? _dispose;
    private long _generation;
    private bool _closed;

    public WindowsClientProcessPriorityControl() : this(new NativeClientProcessPriorityAdapter()) { }
    internal WindowsClientProcessPriorityControl(IClientPriorityAdapter adapter)
        => _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));

    public int PendingRestoreCount { get { lock (_gate) return _owned.Count; } }
    public void CancelOperations() => Interlocked.Increment(ref _generation);

    public Task<ClientProcessPriorityBatchResult> ChangeAsync(IReadOnlyList<SessionObservation> sessions,
        ClientProcessPriorityAction action, CancellationToken ct)
    {
        long generation = Interlocked.Read(ref _generation);
        ArgumentNullException.ThrowIfNull(sessions);
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        int count = sessions.Count;
        if (count is < 0 or > ClientProcessPriorityBatchResult.MaximumProcesses)
            throw new ArgumentException("Select at most 128 client processes in one priority operation.", nameof(sessions));
        var pinned = new SessionObservation[count];
        for (int index = 0; index < count; index++)
            pinned[index] = sessions[index] ?? throw new ArgumentException("Each selected client must be present.", nameof(sessions));
        lock (_gate)
        {
            if (_closed) return Task.FromResult(Refused(pinned, action, ClientProcessPriorityStatus.Closed));
            if (_active is { IsCompleted: false }) return Task.FromResult(Refused(pinned, action, ClientProcessPriorityStatus.Busy));
            if (pinned.Length == 0) return Task.FromResult(new ClientProcessPriorityBatchResult([], Stopped(generation, ct, false), _owned.Count));
            return _active = Task.Run(() => Run(pinned, action, generation, ct), CancellationToken.None);
        }
    }

    public Task<ClientProcessPriorityBatchResult> RestoreAllAsync(CancellationToken ct)
    {
        long generation = Interlocked.Read(ref _generation);
        lock (_gate)
        {
            var pinned = RestoreSelection();
            if (_closed) return Task.FromResult(Refused(pinned, ClientProcessPriorityAction.RestoreOriginal, ClientProcessPriorityStatus.Closed));
            if (_active is { IsCompleted: false }) return Task.FromResult(Refused(pinned, ClientProcessPriorityAction.RestoreOriginal, ClientProcessPriorityStatus.Busy));
            if (pinned.Length == 0) return Task.FromResult(new ClientProcessPriorityBatchResult([], Stopped(generation, ct, false), 0));
            return _active = Task.Run(() => Run(pinned, ClientProcessPriorityAction.RestoreOriginal, generation, ct, ownedSelection: true), CancellationToken.None);
        }
    }

    private bool Stopped(long generation, CancellationToken ct, bool cleanup)
        => !cleanup && (ct.IsCancellationRequested || generation != Interlocked.Read(ref _generation));

    private ClientProcessPriorityBatchResult Run(SessionObservation[] pinned, ClientProcessPriorityAction action,
        long generation, CancellationToken ct, bool ownedSelection = false, bool cleanup = false)
    {
        // Exact restore ownership can include two different starts of a recycled PID. Each
        // stored instance must be checked separately so the replacement can still be restored.
        var duplicatePids = ownedSelection ? null
            : pinned.GroupBy(session => session.ProcessId).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        var results = new List<ClientProcessPriorityResult>(pinned.Length);
        foreach (var session in pinned)
        {
            var key = Key(session);
            if (Stopped(generation, ct, cleanup))
                results.Add(Result(session, action, ClientProcessPriorityStatus.Cancelled));
            else if (!key.IsValid || session.Problem is not null || duplicatePids?.Contains(session.ProcessId) == true)
                results.Add(Result(session, action, ClientProcessPriorityStatus.InvalidClient));
            else
                results.Add(ChangeOne(session, action, () => Stopped(generation, ct, cleanup)));
        }
        return new(results, Stopped(generation, ct, cleanup), PendingRestoreCount);
    }

    private ClientProcessPriorityResult ChangeOne(SessionObservation session, ClientProcessPriorityAction action, Func<bool> stopped)
    {
        var key = Key(session);
        OwnedPriority? owned;
        lock (_gate) _owned.TryGetValue(key, out owned);
        if (action == ClientProcessPriorityAction.RestoreOriginal && owned is null)
            return Result(session, action, ClientProcessPriorityStatus.Unchanged);
        ClientPriorityOpen opened;
        try { opened = _adapter.Open(session); }
        catch (Exception) { return Result(session, action, ClientProcessPriorityStatus.NotConfirmed); }
        using var handle = opened.Handle;
        if (handle is null)
        {
            if (opened.Retired) Forget(key);
            return Result(session, action, Failure(opened.Status));
        }
        if (stopped()) return Result(session, action, ClientProcessPriorityStatus.Cancelled);
        ClientPriorityRead before;
        try { before = handle.Read(); }
        catch (Exception) { return Result(session, action, ClientProcessPriorityStatus.NotConfirmed); }
        if (before.Retired)
        {
            Forget(key);
            return Result(session, action, ClientProcessPriorityStatus.InvalidClient);
        }
        if (before.Status != ClientProcessPriorityStatus.Unchanged || before.Current is null)
            return Result(session, action, Failure(before.Status), current: before.Current);
        var current = before.Current.Value;
        if (current is not (ClientProcessPriorityClass.Normal or ClientProcessPriorityClass.BelowNormal))
            return Result(session, action, ClientProcessPriorityStatus.UnsupportedPriority, current, current: current);
        if (action == ClientProcessPriorityAction.RestoreOriginal && owned is not null && current == owned.Original)
        {
            Forget(key);
            return Result(session, action, ClientProcessPriorityStatus.Unchanged, current, owned.Original, current);
        }
        if (owned is not null && current != owned.LastApplied)
            return Result(session, action, ClientProcessPriorityStatus.Conflict, current, current: current);
        var desired = action switch
        {
            ClientProcessPriorityAction.Normal => ClientProcessPriorityClass.Normal,
            ClientProcessPriorityAction.BelowNormal => ClientProcessPriorityClass.BelowNormal,
            _ => owned!.Original
        };
        if (stopped()) return Result(session, action, ClientProcessPriorityStatus.Cancelled, current, desired, current);
        if (current == desired)
        {
            if (owned is not null && desired == owned.Original) Forget(key);
            return Result(session, action, ClientProcessPriorityStatus.Unchanged, current, desired, current);
        }
        lock (_gate)
        {
            if (owned is null && _owned.Count == ClientProcessPriorityBatchResult.MaximumProcesses)
                return Result(session, action, ClientProcessPriorityStatus.CapacityReached, current, desired, current);
            // Reserve ownership before native dispatch, including a set that succeeds then throws.
            _owned[key] = new(session, owned?.Original ?? current, desired);
        }
        ClientPriorityWrite write;
        try { write = handle.Change(current, desired, stopped); }
        catch (Exception) { return Result(session, action, ClientProcessPriorityStatus.NotConfirmed, current, desired); }
        if (!write.Accepted)
        {
            bool originalAlreadyCurrent = action == ClientProcessPriorityAction.RestoreOriginal && owned is not null
                && write.Status == ClientProcessPriorityStatus.Conflict && write.Current == owned.Original;
            lock (_gate)
            {
                if (owned is null || write.Retired || originalAlreadyCurrent) _owned.Remove(key);
                else _owned[key] = owned;
            }
            return Result(session, action, originalAlreadyCurrent ? ClientProcessPriorityStatus.Unchanged : Failure(write.Status),
                current, desired, write.Current);
        }
        // Cancellation cannot erase an accepted change. Always finish its actual readback.
        ClientPriorityRead after;
        try { after = handle.Read(); }
        catch (Exception) { return Result(session, action, ClientProcessPriorityStatus.NotConfirmed, current, desired); }
        if (after.Retired) Forget(key);
        if (after.Status != ClientProcessPriorityStatus.Unchanged || after.Current != desired)
            return Result(session, action, ClientProcessPriorityStatus.NotConfirmed, current, desired, after.Current);
        if (desired == (owned?.Original ?? current)) Forget(key);
        return Result(session, action, action == ClientProcessPriorityAction.RestoreOriginal
            ? ClientProcessPriorityStatus.Restored : ClientProcessPriorityStatus.Applied, current, desired, after.Current);
    }

    private ClientProcessPriorityResult Result(SessionObservation session, ClientProcessPriorityAction action,
        ClientProcessPriorityStatus status, ClientProcessPriorityClass? previous = null,
        ClientProcessPriorityClass? requested = null, ClientProcessPriorityClass? current = null)
    {
        lock (_gate) return new(session, action, status, previous, requested, current, _owned.ContainsKey(Key(session)));
    }
    private ClientProcessPriorityBatchResult Refused(SessionObservation[] pinned, ClientProcessPriorityAction action, ClientProcessPriorityStatus status)
        => new(pinned.Select(session => Result(session, action, status)).ToArray(), false, _owned.Count, status);
    private SessionObservation[] RestoreSelection() => _owned.Values.Select(owned => owned.Session).ToArray();
    private void Forget(SessionProcessKey key) { lock (_gate) _owned.Remove(key); }
    private static SessionProcessKey Key(SessionObservation session) => new(null, session.ProcessId, session.ProcessStartUtcTicks);
    private static ClientProcessPriorityStatus Failure(ClientProcessPriorityStatus status)
        => status is ClientProcessPriorityStatus.InvalidClient or ClientProcessPriorityStatus.AccessDenied
            or ClientProcessPriorityStatus.UnsupportedPriority or ClientProcessPriorityStatus.Conflict or ClientProcessPriorityStatus.Cancelled
            ? status : ClientProcessPriorityStatus.NotConfirmed;

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            _closed = true; CancelOperations();
            return new(_dispose = DisposeCoreAsync(_active));
        }
    }
    private async Task DisposeCoreAsync(Task<ClientProcessPriorityBatchResult>? active)
    {
        if (active is not null) await active.ConfigureAwait(false);
        Task<ClientProcessPriorityBatchResult> restore;
        lock (_gate)
        {
            var pinned = RestoreSelection();
            if (pinned.Length == 0) return;
            restore = _active = Task.Run(() => Run(pinned, ClientProcessPriorityAction.RestoreOriginal,
                Interlocked.Read(ref _generation), CancellationToken.None, ownedSelection: true, cleanup: true), CancellationToken.None);
        }
        var result = await restore.ConfigureAwait(false);
        if (result.PendingRestoreCount > 0) throw new ClientProcessPriorityCleanupException(result.PendingRestoreCount);
    }
    private sealed record OwnedPriority(SessionObservation Session, ClientProcessPriorityClass Original, ClientProcessPriorityClass LastApplied);
}
