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
/// Clients already running when Zenith starts never arrive: the evidence registry keeps a Connected line for five minutes,
/// so a client joined just before reports Connected on the first scans. Nothing arrives until a baseline scan, and every
/// client that scan lists counts as already arrived, so automatic Arrange still tiles it when a later client connects. A
/// baseline is a complete scan, or, as FreshClientInventory accepts for arrival mute, a scan without a problem whose only
/// gap is rows Windows could not read (for example another Windows user's client, which no scan ever reads) and that lists
/// fewer clients than the scan budget. Such a row still names its PID, so a client unreadable at the baseline that becomes
/// readable later, having started before that scan, never arrives either.
/// Bounded and allocation-light: one caller thread (the dispatcher's presentation step), no timers, no native calls.
/// </summary>
public sealed class ClientArrivalTracker
{
    public const int MaximumClients = 128;
    private readonly HashSet<SessionProcessKey> _arrived = [];
    private readonly Dictionary<SessionProcessKey, SessionObservation> _current = [];
    private readonly HashSet<SessionProcessKey> _listed = [];
    // Before the baseline: the PIDs the scan lists. After it: the PIDs it could not read that no scan has read since.
    private readonly HashSet<int> _pids = [];
    private readonly Predicate<SessionProcessKey> _departed;
    private long _baselineUtcTicks;
    private bool _seeded;

    public ClientArrivalTracker() => _departed = key => !_listed.Contains(key);

    /// <summary>
    /// No baseline scan yet, and in the latest observation a listed client with a window reported Connected. It cannot be
    /// told from a client that was already running, so it did not arrive; the caller says so instead of staying silent.
    /// </summary>
    public bool AwaitingBaseline { get; private set; }

    /// <returns>The clients that reached Connected for the first time in this observation; usually none.</returns>
    public IReadOnlyList<SessionObservation> Observe(DesktopSnapshot desktop, IReadOnlyList<SessionEvidenceSnapshot> evidence)
    {
        ArgumentNullException.ThrowIfNull(desktop); ArgumentNullException.ThrowIfNull(evidence);
        _current.Clear(); _listed.Clear();
        if (!_seeded) _pids.Clear();
        bool listedAll = true, onlyUnreadableGaps = true; int unreadable = 0;
        if (desktop.Sessions is { } sessions)
            foreach (var session in sessions)
            {
                if (session is null) { onlyUnreadableGaps = false; continue; }
                var key = new SessionProcessKey(null, session.ProcessId, session.ProcessStartUtcTicks);
                if (_listed.Count >= MaximumClients && !_listed.Contains(key)) { listedAll = false; break; }
                // Every listed process is still running, even without a window or a readable row; only a readable row
                // with a window can arrive or be arranged.
                _listed.Add(key);
                if (session is { Problem: null } && session.Window != 0) _current.TryAdd(key, session);
                if (!_seeded)
                {
                    // As FreshClientInventory: each row is readable or names only its PID, and no PID is listed twice.
                    if (session.ProcessId <= 0 || !_pids.Add(session.ProcessId)) onlyUnreadableGaps = false;
                    else if (session is { Problem: not null, ProcessStartUtcTicks: 0 }) unreadable++;
                    else if (session.Problem is not null || session.ProcessStartUtcTicks <= 0) onlyUnreadableGaps = false;
                }
                // Readable again: it was running at the baseline when it started before that scan; a reused PID was not.
                else if (_pids.Count > 0 && session is { Problem: null, ProcessStartUtcTicks: > 0 } && _pids.Remove(session.ProcessId)
                    && session.ProcessStartUtcTicks <= _baselineUtcTicks) _arrived.Add(key);
            }
        bool complete = desktop is { Problem: null, ProcessInventoryComplete: true } && listedAll;
        AwaitingBaseline = false;
        // Only a baseline shows which clients were already running; the first one marks them all as arrived.
        if (!_seeded)
        {
            if (!complete && (desktop.Problem is not null || !listedAll || !onlyUnreadableGaps || unreadable == 0
                || desktop.Sessions!.Count >= MaximumClients))
            {
                foreach (var item in evidence)
                    if (item is { Lifecycle: ObservedSessionLifecycle.Connected, Ambiguous: false } && _current.ContainsKey(item.Process))
                    { AwaitingBaseline = true; break; }
                return [];
            }
            _arrived.UnionWith(_listed); _seeded = true;
            // Keep only the PIDs this scan could not read. Without a scan time, each counts as already running when it turns
            // readable, which fails safe: no window change.
            _pids.Clear();
            if (unreadable > 0)
                foreach (var key in _listed) if (key.ProcessStartUtcTicks == 0) _pids.Add(key.ProcessId);
            _baselineUtcTicks = desktop.ProcessObservedUtc?.UtcTicks ?? long.MaxValue;
        }
        // Keys only ever belong to listed clients, so pruning to the current scan keeps the set within twice the client cap.
        if (complete || _arrived.Count >= MaximumClients * 2) _arrived.RemoveWhere(_departed);
        List<SessionObservation>? arrived = null;
        foreach (var item in evidence)
            if (item is { Lifecycle: ObservedSessionLifecycle.Connected, Ambiguous: false } && _current.TryGetValue(item.Process, out var session)
                && _arrived.Count < MaximumClients * 2 && _arrived.Add(item.Process)) (arrived ??= []).Add(session);
        return arrived is null ? [] : arrived.AsReadOnly();
    }

    /// <summary>
    /// Every client in the latest scan that has connected at least once, or was already running at the baseline scan,
    /// and whose window is visible and not minimized, in slot order. The move refuses a minimized or hidden window, so it gets
    /// no cell; restored, it takes its slot again. The scan does not report maximized or fullscreen windows, so those still
    /// get a cell that the move refuses.
    /// </summary>
    public SessionObservation[] Slots(LaunchQueueSnapshot? queue) => Slots(queue, out _);

    /// <param name="minimizedOrHidden">How many such clients were left out because their window is minimized or hidden. No slot
    /// and zero here means no connected client still has a window: each has closed, or is listed without one.</param>
    /// <inheritdoc cref="Slots(LaunchQueueSnapshot?)"/>
    public SessionObservation[] Slots(LaunchQueueSnapshot? queue, out int minimizedOrHidden)
    {
        minimizedOrHidden = 0;
        var connected = new List<SessionObservation>(_current.Count);
        foreach (var (key, session) in _current)
        {
            if (!_arrived.Contains(key)) continue;
            if (session is { Minimized: false, Visible: true }) connected.Add(session); else minimizedOrHidden++;
        }
        return ClientWindowSlotOrder.Order(connected, queue);
    }
}
