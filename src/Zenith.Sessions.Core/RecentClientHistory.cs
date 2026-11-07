namespace Zenith.Sessions.Core;

/// <summary>Presence history only; reported identity/destination are last-known unverified diagnostics.</summary>
public sealed record RecentClientHistoryEntry(Guid EntryId, SessionProcessKey Process, DateTimeOffset FirstObservedUtc,
    DateTimeOffset LastObservedUtc, DateTimeOffset? NoLongerObservedUtc = null, DateTimeOffset? CloseRequestedUtc = null,
    long? LastKnownUserId = null, DateTimeOffset? LastKnownUserIdentityUtc = null,
    ObservedSessionDestination? LastKnownDestination = null)
{
    public string Outcome => NoLongerObservedUtc is null ? "Currently observed" : "No longer observed";
    public string Cause => "Unknown";
    public bool CanAuthorizeAccountAction => false;

    public RecentClientHistoryEntry Validated(bool ended)
    {
        if (EntryId == Guid.Empty || !Process.IsValid || Process.HostId is { } host && !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            || FirstObservedUtc.UtcTicks < Process.ProcessStartUtcTicks
            || LastObservedUtc < FirstObservedUtc || ended != NoLongerObservedUtc.HasValue
            || NoLongerObservedUtc is { } absent && absent < LastObservedUtc
            || CloseRequestedUtc is { } close && (close.UtcTicks < Process.ProcessStartUtcTicks
                || NoLongerObservedUtc is { } until && close - until > RecentClientHistoryReducer.MaximumInventoryAge)
            || LastKnownUserId.HasValue != LastKnownUserIdentityUtc.HasValue || LastKnownUserId is <= 0
            || LastKnownUserIdentityUtc is { } userTime && !ValidFactTime(userTime)
            || LastKnownDestination is { } destination && (destination.PlaceId <= 0 || destination.JobId == Guid.Empty || destination.UniverseId is <= 0 || !ValidFactTime(destination.ObservedUtc)))
            throw new ArgumentException("The client presence history entry is inconsistent.");
        return this;
    }
    private bool ValidFactTime(DateTimeOffset value) => value.UtcTicks >= Process.ProcessStartUtcTicks && value <= LastObservedUtc;
}

/// <summary>Only ended observations are persisted; never a saved running-state inventory.</summary>
public sealed record RecentClientHistorySnapshot(int Version, long Revision, IReadOnlyList<RecentClientHistoryEntry> Entries)
{
    public static RecentClientHistorySnapshot Empty { get; } = new(1, 0, Array.AsReadOnly(Array.Empty<RecentClientHistoryEntry>()));
    public RecentClientHistorySnapshot Validated()
    {
        if (Version != 1 || Revision < 0 || Entries is null || Entries.Count > RecentClientHistoryReducer.MaximumEnded || Revision == 0 && Entries.Count > 0)
            throw new ArgumentException("The client presence history snapshot is unsupported or inconsistent.");
        var ids = new HashSet<Guid>(); var processes = new HashSet<SessionProcessKey>();
        foreach (var entry in Entries)
            if (entry is null || !ids.Add(entry.EntryId) || !processes.Add(entry.Process))
                throw new ArgumentException("The client presence history contains duplicate or invalid identities.");
            else entry.Validated(ended: true);
        return this;
    }
}
public sealed record RecentClientHistoryView(long Revision, IReadOnlyList<RecentClientHistoryEntry> Active,
    IReadOnlyList<RecentClientHistoryEntry> Ended);
public interface ISessionHistoryStore
{
    Task<RecentClientHistorySnapshot> LoadAsync(CancellationToken ct);
    Task SaveAsync(RecentClientHistorySnapshot snapshot, CancellationToken ct);
}

/// <summary>Single locked reducer; no IO, timers or actions. Revision changes only when persisted ended history changes.</summary>
public sealed class RecentClientHistoryReducer
{
    public const int MaximumActive = 128, MaximumEnded = 100;
    public static readonly TimeSpan MaximumInventoryAge = TimeSpan.FromSeconds(10);
    private readonly object _gate = new();
    private readonly Dictionary<SessionProcessKey, RecentClientHistoryEntry> _active = [];
    private readonly List<RecentClientHistoryEntry> _ended = [];
    private long _revision, _processRevision;
    private DateTimeOffset _processObserved;
    private bool _started;

    public void Restore(RecentClientHistorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot); snapshot.Validated();
        lock (_gate)
        {
            if (_started) throw new InvalidOperationException("Restore history only once, before observing processes.");
            _ended.AddRange(snapshot.Entries); _revision = snapshot.Revision; _started = true;
        }
    }
    public RecentClientHistoryView Snapshot()
    { lock (_gate) return new(_revision, Array.AsReadOnly(_active.Values.OrderBy(item => item.FirstObservedUtc).ThenBy(item => item.Process.ProcessId).ToArray()), EndedCopy()); }
    public RecentClientHistorySnapshot PersistedSnapshot()
    { lock (_gate) return new(1, _revision, EndedCopy()); }
    private IReadOnlyList<RecentClientHistoryEntry> EndedCopy() => Array.AsReadOnly(_ended.OrderByDescending(item => item.NoLongerObservedUtc).ThenBy(item => item.EntryId).ToArray());

    public bool Observe(DesktopSnapshot snapshot, IReadOnlyList<SessionEvidenceSnapshot> evidence, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(evidence);
        lock (_gate)
        {
            _started = true;
            if (snapshot.Problem is not null || !snapshot.ProcessInventoryComplete || snapshot.ProcessObservedUtc is not { } observed || observed == default
                || snapshot.ProcessRevision <= _processRevision || observed < _processObserved
                || observed - now > TimeSpan.FromSeconds(1) || now - observed > MaximumInventoryAge
                || snapshot.Sessions is null || snapshot.Sessions.Count > MaximumActive || evidence.Count > 256) return false;
            var present = new Dictionary<SessionProcessKey, SessionObservation>();
            var pids = new HashSet<int>();
            foreach (var session in snapshot.Sessions)
            {
                if (session is null) return false;
                var key = new SessionProcessKey(null, session.ProcessId, session.ProcessStartUtcTicks);
                if (!key.IsValid || session.Problem is not null || key.ProcessStartUtcTicks > session.ObservedUtc.UtcTicks || !pids.Add(key.ProcessId)
                    || session.ObservedUtc > observed || observed - session.ObservedUtc > MaximumInventoryAge) return false;
                present.Add(key, session);
            }
            // The full inventory is validated before mutating active or ended observations.
            if (_revision == long.MaxValue && (_active.Keys.Any(key => !present.ContainsKey(key)) || _ended.Any(item => present.ContainsKey(item.Process))))
                throw new InvalidOperationException("Client history revision is exhausted.");
            _processRevision = snapshot.ProcessRevision; _processObserved = observed;
            bool changed = false, persisted = false;
            foreach (var key in _active.Keys.ToArray())
                if (!present.ContainsKey(key))
                {
                    _ended.RemoveAll(item => item.Process == key);
                    _ended.Add(_active[key] with { NoLongerObservedUtc = observed }); _active.Remove(key);
                    changed = true; persisted = true;
                }
            foreach (var key in present.Keys)
            {
                var presence = present[key].ObservedUtc;
                if (!_active.TryGetValue(key, out var entry))
                {
                    int old = _ended.FindIndex(item => item.Process == key);
                    if (old >= 0)
                    {
                        entry = _ended[old].LastObservedUtc <= presence ? _ended[old] with { NoLongerObservedUtc = null } : new(Guid.NewGuid(), key, presence, presence);
                        _ended.RemoveAt(old); persisted = true;
                    }
                    else entry = new(Guid.NewGuid(), key, presence, presence);
                }
                var next = entry with { LastObservedUtc = presence > entry.LastObservedUtc ? presence : entry.LastObservedUtc };
                SessionEvidenceSnapshot? matching = null; bool ambiguous = false;
                foreach (var item in evidence)
                    if (item is not null && item.Process == key)
                    { if (matching is not null) { ambiguous = true; break; } matching = item; }
                if (!ambiguous && matching is { Ambiguous: false } facts && facts.Confidence != SessionEvidenceConfidence.Unknown
                    && Enum.IsDefined(facts.Confidence) && facts.Stream is { IsValid: true }
                    && facts.CorrelationKind is SessionLogCorrelationKind.ResourceHolderDiagnostic or SessionLogCorrelationKind.IndependentlyCorroborated
                    && facts.ObservedUtc.UtcTicks >= key.ProcessStartUtcTicks && facts.ObservedUtc <= observed)
                {
                    long? user = facts.ObservedUserId ?? facts.LastKnownUserId;
                    var userTime = facts.ObservedUserId.HasValue ? facts.UserIdentityObservedUtc : facts.LastKnownUserIdentityUtc;
                    if (user is > 0 && userTime is { } time && time.UtcTicks >= key.ProcessStartUtcTicks && time <= next.LastObservedUtc
                        && (next.LastKnownUserIdentityUtc is null || time >= next.LastKnownUserIdentityUtc))
                        next = next with { LastKnownUserId = user, LastKnownUserIdentityUtc = time };
                    var destination = facts.CurrentDestination ?? facts.LastKnownDestination;
                    if (destination is { PlaceId: > 0 } target && target.JobId != Guid.Empty && target.UniverseId is not <= 0 && target.ObservedUtc.UtcTicks >= key.ProcessStartUtcTicks
                        && target.ObservedUtc <= next.LastObservedUtc && (next.LastKnownDestination is null || target.ObservedUtc >= next.LastKnownDestination.ObservedUtc))
                        next = next with { LastKnownDestination = target };
                }
                if (entry != next || !_active.ContainsKey(key)) changed = true;
                _active[key] = next;
            }
            while (_ended.Count > MaximumEnded)
            { _ended.Remove(_ended.MinBy(item => item.NoLongerObservedUtc)!); persisted = true; }
            if (persisted) Advance();
            return changed;
        }
    }
    public bool RecordCloseRequest(SessionProcessKey process, DateTimeOffset requestedUtc)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(process, out var entry) || requestedUtc.UtcTicks < process.ProcessStartUtcTicks
                || (requestedUtc - entry.LastObservedUtc).Duration() > MaximumInventoryAge
                || entry.CloseRequestedUtc is { } previous && requestedUtc <= previous) return false;
            _active[process] = entry with { CloseRequestedUtc = requestedUtc }; return true;
        }
    }
    public bool ClearEnded()
    { lock (_gate) { if (_ended.Count == 0) return false; Advance(); _ended.Clear(); return true; } }
    private void Advance()
    { if (_revision == long.MaxValue) throw new InvalidOperationException("Client history revision is exhausted."); _revision++; }
}
