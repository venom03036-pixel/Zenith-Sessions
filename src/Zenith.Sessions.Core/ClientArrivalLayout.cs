namespace Zenith.Sessions.Core;

/// <summary>
/// Tile slot order: clients the launch queue started keep the queue's request order; every other client follows, oldest
/// process start first. PID and window only break ties, so the order no longer looks random. Pure; no native calls.
/// </summary>
public static class ClientWindowSlotOrder
{
    public static SessionObservation[] Order(IReadOnlyList<SessionObservation> sessions, LaunchQueueSnapshot? queue)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (sessions.Count == 0) return [];
        // A receipt names one exact process instance; a reused PID with another start time claims no queue slot.
        Dictionary<(int ProcessId, long StartTicks), int>? queued = null;
        if (queue?.Operations is { } operations)
            for (int index = 0; index < operations.Count; index++)
                if (operations[index]?.ProcessReceipt is { IsValid: true } receipt)
                    (queued ??= []).TryAdd((receipt.ProcessId, receipt.ProcessStartUtcTicks), index);
        int Slot(SessionObservation session) => queued is not null
            && queued.TryGetValue((session.ProcessId, session.ProcessStartUtcTicks), out int index) ? index : int.MaxValue;
        return sessions.OrderBy(Slot).ThenBy(session => session.ProcessStartUtcTicks)
            .ThenBy(session => session.ProcessId).ThenBy(session => session.Window.ToInt64()).ToArray();
    }
}

/// <summary>
/// Notices when a client first reports Connected in its log: once per process instance, so a teleport, disconnect or
/// reconnect never counts again. Connected is an unverified log diagnostic, so it may gate only a window move or minimize,
/// never an account action. A client counts once it is in the latest scan with a window and no problem. A client left only
/// when a complete scan no longer lists its process at all: one that is still listed without a window, or that could not
/// be read this time, is still running and does not arrive twice when its window returns. A failed or partial scan, or
/// one that lists more clients than the tracker holds, forgets nothing, so a client it missed does not arrive twice.
/// Bounded and allocation-light: one caller thread (the dispatcher's presentation step), no timers, no native calls.
/// </summary>
public sealed class ClientArrivalTracker
{
    public const int MaximumClients = 128;
    private readonly HashSet<SessionProcessKey> _arrived = [];
    private readonly Dictionary<SessionProcessKey, SessionObservation> _current = [];
    private readonly HashSet<SessionProcessKey> _listed = [];
    private readonly Predicate<SessionProcessKey> _departed;

    public ClientArrivalTracker() => _departed = key => !_listed.Contains(key);

    /// <returns>The clients that reached Connected for the first time in this observation; usually none.</returns>
    public IReadOnlyList<SessionObservation> Observe(DesktopSnapshot desktop, IReadOnlyList<SessionEvidenceSnapshot> evidence)
    {
        ArgumentNullException.ThrowIfNull(desktop); ArgumentNullException.ThrowIfNull(evidence);
        _current.Clear(); _listed.Clear();
        bool listedAll = true;
        if (desktop.Sessions is { } sessions)
            foreach (var session in sessions)
            {
                if (session is null) continue;
                var key = new SessionProcessKey(null, session.ProcessId, session.ProcessStartUtcTicks);
                if (_listed.Count >= MaximumClients && !_listed.Contains(key)) { listedAll = false; break; }
                // Every listed process is still running, even without a window or a readable row; only a readable row
                // with a window can arrive or be arranged.
                _listed.Add(key);
                if (session is { Problem: null } && session.Window != 0) _current.TryAdd(key, session);
            }
        // Keys only ever belong to listed clients, so pruning to the current scan keeps the set within twice the client cap.
        if ((desktop is { Problem: null, ProcessInventoryComplete: true } && listedAll) || _arrived.Count >= MaximumClients * 2)
            _arrived.RemoveWhere(_departed);
        List<SessionObservation>? arrived = null;
        foreach (var item in evidence)
            if (item is { Lifecycle: ObservedSessionLifecycle.Connected, Ambiguous: false } && _current.TryGetValue(item.Process, out var session)
                && _arrived.Count < MaximumClients * 2 && _arrived.Add(item.Process)) (arrived ??= []).Add(session);
        return arrived is null ? [] : arrived.AsReadOnly();
    }

    /// <summary>Every client in the latest scan that has connected at least once, in slot order.</summary>
    public SessionObservation[] Slots(LaunchQueueSnapshot? queue)
    {
        var connected = new List<SessionObservation>(_current.Count);
        foreach (var (key, session) in _current) if (_arrived.Contains(key)) connected.Add(session);
        return ClientWindowSlotOrder.Order(connected, queue);
    }
}
