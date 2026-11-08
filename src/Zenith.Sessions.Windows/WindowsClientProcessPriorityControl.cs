using System.Numerics;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

internal sealed record ClientPriorityRead(ClientProcessPriorityStatus Status, ClientProcessPriorityClass? Current = null, bool Retired = false,
    ClientProcessSettings? Settings = null, ulong? SystemAffinity = null);
internal sealed record ClientPriorityWrite(bool Accepted, ClientProcessPriorityStatus Status, ClientProcessPriorityClass? Current = null, bool Retired = false,
    ClientProcessSettings? Settings = null);
internal sealed record ClientPriorityOpen(ClientProcessPriorityStatus Status, IClientPriorityHandle? Handle = null, bool Retired = false);
internal interface IClientPriorityHandle : IDisposable
{
    ClientPriorityRead Read();
    ClientPriorityWrite Change(ClientProcessSettings expected, ClientProcessSettings desired, Func<bool> stopped);
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
        for (int index = 0; index < pinned.Length; index++)
        {
            var session = pinned[index];
            var key = Key(session);
            if (Stopped(generation, ct, cleanup))
                results.Add(Result(session, action, ClientProcessPriorityStatus.Cancelled));
            else if (!key.IsValid || session.Problem is not null || duplicatePids?.Contains(session.ProcessId) == true)
                results.Add(Result(session, action, ClientProcessPriorityStatus.InvalidClient));
            else
                results.Add(ChangeOne(session, action, () => Stopped(generation, ct, cleanup), index, pinned.Length));
        }
        return new(results, Stopped(generation, ct, cleanup), PendingRestoreCount);
    }

    private ClientProcessPriorityResult ChangeOne(SessionObservation session, ClientProcessPriorityAction action, Func<bool> stopped,
        int index, int count)
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
        if (before.Status != ClientProcessPriorityStatus.Unchanged || before.Settings is not { } current)
            return Result(session, action, Failure(before.Status), current: before.Current);
        var currentClass = current.Class;
        if (currentClass is not (ClientProcessPriorityClass.Normal or ClientProcessPriorityClass.BelowNormal))
            return Result(session, action, ClientProcessPriorityStatus.UnsupportedPriority, currentClass, current: currentClass);
        if (action == ClientProcessPriorityAction.RestoreOriginal && owned is not null && current == owned.Original)
        {
            Forget(key);
            return Result(session, action, ClientProcessPriorityStatus.Unchanged, currentClass, owned.Original.Class, currentClass);
        }
        // Every value this owner wrote must still be what it last applied; any other observed change is preserved.
        if (owned is not null && current != owned.LastApplied)
            return Result(session, action, ClientProcessPriorityStatus.Conflict, currentClass, current: currentClass);
        if (Desired(action, current, owned, before.SystemAffinity, index, count, out var refusal) is not { } desired)
            return Result(session, action, refusal, currentClass, current: currentClass);
        if (stopped()) return Result(session, action, ClientProcessPriorityStatus.Cancelled, currentClass, desired.Class, currentClass);
        if (current == desired)
        {
            if (owned is not null && desired == owned.Original) Forget(key);
            return Result(session, action, ClientProcessPriorityStatus.Unchanged, currentClass, desired.Class, currentClass);
        }
        lock (_gate)
        {
            if (owned is null && _owned.Count == ClientProcessPriorityBatchResult.MaximumProcesses)
                return Result(session, action, ClientProcessPriorityStatus.CapacityReached, currentClass, desired.Class, currentClass);
            // Reserve ownership before native dispatch, including a set that succeeds then throws.
            _owned[key] = new(session, owned?.Original ?? current, desired);
        }
        ClientPriorityWrite write;
        try { write = handle.Change(current, desired, stopped); }
        catch (Exception) { return Result(session, action, ClientProcessPriorityStatus.NotConfirmed, currentClass, desired.Class); }
        if (!write.Accepted)
        {
            bool originalAlreadyCurrent = action == ClientProcessPriorityAction.RestoreOriginal && owned is not null
                && write.Status == ClientProcessPriorityStatus.Conflict && write.Settings == owned.Original;
            lock (_gate)
            {
                if (owned is null || write.Retired || originalAlreadyCurrent) _owned.Remove(key);
                else _owned[key] = owned;
            }
            return Result(session, action, originalAlreadyCurrent ? ClientProcessPriorityStatus.Unchanged : Failure(write.Status),
                currentClass, desired.Class, write.Current);
        }
        // Cancellation, or a refusal after an earlier value was accepted, cannot erase an accepted change.
        // Always finish its actual readback; only an exact match of every value confirms it.
        ClientPriorityRead after;
        try { after = handle.Read(); }
        catch (Exception) { return Result(session, action, ClientProcessPriorityStatus.NotConfirmed, currentClass, desired.Class); }
        if (after.Retired) Forget(key);
        if (after.Status != ClientProcessPriorityStatus.Unchanged || after.Settings != desired)
        {
            // Windows accepted only part of the change. Own exactly what the readback shows, so a later Restore or Exit compares
            // against Zenith's own partial write instead of calling it another change, and returns each original value.
            // A change made elsewhere between the write and this readback cannot be told apart, so Restore would return it too.
            // Values that read back as the original keep the reservation: Restore retires it without a write while they stay so.
            var original = owned?.Original ?? current;
            if (after.Status == ClientProcessPriorityStatus.Unchanged && after.Settings is { } actual && actual != original)
                lock (_gate) _owned[key] = new(session, original, actual);
            return Result(session, action, ClientProcessPriorityStatus.NotConfirmed, currentClass, desired.Class, after.Current);
        }
        if (desired == (owned?.Original ?? current)) Forget(key);
        return Result(session, action, action == ClientProcessPriorityAction.RestoreOriginal
            ? ClientProcessPriorityStatus.Restored : ClientProcessPriorityStatus.Applied, currentClass, desired.Class, after.Current);
    }

    /// <summary>The values an action asks for: the current ones with only that action's values replaced.</summary>
    private static ClientProcessSettings? Desired(ClientProcessPriorityAction action, ClientProcessSettings current, OwnedPriority? owned,
        ulong? systemAffinity, int index, int count, out ClientProcessPriorityStatus refusal)
    {
        refusal = ClientProcessPriorityStatus.SettingUnavailable;
        switch (action)
        {
            case ClientProcessPriorityAction.Normal: return current with { Priority = ClientProcessSettings.NormalClass };
            case ClientProcessPriorityAction.BelowNormal: return current with { Priority = ClientProcessSettings.BelowNormalClass };
            case ClientProcessPriorityAction.Efficiency:
                // An original Windows did not report could never be restored, so it is never changed.
                if (current.Throttling is not { } throttling || current.MemoryPriority is null) return null;
                const uint ecoQos = ClientProcessSettings.ExecutionSpeedThrottling;
                return current with
                {
                    Priority = ClientProcessSettings.BelowNormalClass,
                    Throttling = new(throttling.Control | ecoQos, throttling.State | ecoQos),
                    MemoryPriority = ClientProcessSettings.VeryLowMemoryPriority
                };
            case ClientProcessPriorityAction.SpreadCores:
                if (current.Affinity is null || systemAffinity is not { } system) return null;
                refusal = ClientProcessPriorityStatus.NotEnoughCores;
                return CoreShare(index, count, system) is { } share ? current with { Affinity = share } : null;
            default: return owned!.Original;
        }
    }

    /// <summary>
    /// Client <paramref name="index"/> of <paramref name="count"/> gets every count-th core of <paramref name="system"/>,
    /// so the shares of one operation never overlap. Null unless there are at least two clients and two cores for each.
    /// </summary>
    internal static ulong? CoreShare(int index, int count, ulong system)
    {
        if (count < 2 || index < 0 || index >= count || BitOperations.PopCount(system) < 2 * count) return null;
        ulong share = 0;
        int position = 0;
        for (int bit = 0; bit < 64; bit++)
        {
            ulong core = 1UL << bit;
            if ((system & core) == 0) continue;
            if (position++ % count == index) share |= core;
        }
        return share;
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
    /// <summary>The client's own values before this owner's first change, and every value this owner last applied.</summary>
    private sealed record OwnedPriority(SessionObservation Session, ClientProcessSettings Original, ClientProcessSettings LastApplied);
}
