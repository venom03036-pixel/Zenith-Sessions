namespace Zenith.Sessions.Core;

public sealed record SystemTelemetryPoint(DateTimeOffset ObservedUtc, double? CpuPercent,
    ulong? TotalPhysicalBytes, ulong? UsedPhysicalBytes, ulong? CommitUsedBytes, ulong? CommitLimitBytes,
    bool IsGap = false);
public sealed record ClientTelemetryPoint(DateTimeOffset ObservedUtc, double? CpuPercent,
    long? WorkingSetBytes, long? PrivateCommitBytes, double? PageFaultsPerSecond,
    ProcessIoRates? IoRates = null, bool IsGap = false);
public sealed record SystemTelemetrySnapshot(long Revision, IReadOnlyList<SystemTelemetryPoint> Points);
public sealed record ClientTelemetrySnapshot(SessionProcessKey Process, long Revision, IReadOnlyList<ClientTelemetryPoint> Points);

/// <summary>
/// In-memory measurement history only. No account association, lifecycle inference, admission decision or IO.
/// Retention is relative to the latest accepted measurement; callers age/clip unchanged snapshots by their
/// original timestamps. Cached frames never renew a measurement or allocate a replacement snapshot.
/// </summary>
public sealed class TelemetryHistory
{
    public const int MaximumClients = 128, MaximumPoints = 120;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaximumSystemAge = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MaximumClientAge = TimeSpan.FromSeconds(10);
    private readonly object _gate = new();
    private readonly List<SystemTelemetryPoint> _systemPoints = [];
    private readonly Dictionary<SessionProcessKey, ClientEntry> _clients = [];
    private SystemTelemetrySnapshot? _system = new(0, Array.AsReadOnly(Array.Empty<SystemTelemetryPoint>()));
    private long _systemRevision;
    private DateTimeOffset _lastSystem;
    private long _processRevision;
    private DateTimeOffset _processObserved;

    private sealed class ClientEntry
    {
        public readonly List<ClientTelemetryPoint> Points = [];
        public DateTimeOffset LastObserved, LastIoObserved;
        public long Revision;
        public ClientTelemetrySnapshot? Snapshot;
    }

    public SystemTelemetrySnapshot GetSystemSnapshot()
    {
        lock (_gate)
        {
            _system ??= new(_systemRevision, Array.AsReadOnly(_systemPoints.ToArray()));
            return _system;
        }
    }
    public ClientTelemetrySnapshot? GetClientSnapshot(SessionProcessKey process)
    {
        lock (_gate)
        {
            if (!_clients.TryGetValue(process, out var entry)) return null;
            entry.Snapshot ??= new(process, entry.Revision, Array.AsReadOnly(entry.Points.ToArray()));
            return entry.Snapshot;
        }
    }

    public bool Observe(DesktopSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            bool changed = ObserveSystem(snapshot.Resources, now);
            if (snapshot.Problem is not null || snapshot.Sessions is null || snapshot.Sessions.Count > MaximumClients) return changed;
            DateTimeOffset completed = snapshot.ProcessObservedUtc ?? now;
            if (!Fresh(completed, now, MaximumClientAge)) return changed;
            if (snapshot.ProcessObservedUtc.HasValue && (snapshot.ProcessRevision <= 0
                || snapshot.ProcessRevision <= _processRevision || completed < _processObserved)) return changed;
            // Conflicting rows cannot be used to choose between two identities for one PID.
            var pids = new HashSet<int>();
            foreach (var row in snapshot.Sessions)
            {
                if (row is null) return changed;
                var key = new SessionProcessKey(null, row.ProcessId, row.ProcessStartUtcTicks);
                if (!key.IsValid || !pids.Add(row.ProcessId) || key.ProcessStartUtcTicks > row.ObservedUtc.UtcTicks
                    || row.ObservedUtc > completed || !Fresh(row.ObservedUtc, now, MaximumClientAge)) return changed;
            }
            if (snapshot.ProcessObservedUtc.HasValue)
            { _processRevision = snapshot.ProcessRevision; _processObserved = completed; }
            // Incomplete inventories may contribute present measurements. Absence never means exit here.
            foreach (var row in snapshot.Sessions)
                changed |= ObserveClient(row, completed, now);
            return changed;
        }
    }

    private bool ObserveSystem(ResourceSnapshot? sample, DateTimeOffset now)
    {
        if (sample is null || !Fresh(sample.ObservedUtc, now, MaximumSystemAge) || sample.ObservedUtc <= _lastSystem) return false;
        bool valid = sample.IsValid && ValidRate(sample.CpuPercent, 100);
        if (_lastSystem != default && sample.ObservedUtc - _lastSystem > MaximumSystemAge)
            _systemPoints.Add(new(_lastSystem.ToUniversalTime() + MaximumSystemAge, null, null, null, null, null, true));
        _systemPoints.Add(valid
            ? new(sample.ObservedUtc, sample.CpuPercent, sample.TotalPhysical, sample.TotalPhysical - sample.AvailablePhysical, sample.CommitUsed, sample.CommitLimit)
            : new(sample.ObservedUtc, null, null, null, null, null, true));
        _lastSystem = sample.ObservedUtc;
        Trim(_systemPoints, sample.ObservedUtc, static point => point.ObservedUtc);
        _systemRevision++; _system = null;
        return true;
    }

    private bool ObserveClient(SessionObservation row, DateTimeOffset completed, DateTimeOffset now)
    {
        var key = new SessionProcessKey(null, row.ProcessId, row.ProcessStartUtcTicks);
        if (!key.IsValid || key.ProcessStartUtcTicks > row.ObservedUtc.UtcTicks || row.ObservedUtc > completed
            || !Fresh(row.ObservedUtc, now, MaximumClientAge)) return false;
        if (_clients.TryGetValue(key, out var entry) && row.ObservedUtc <= entry.LastObserved) return false;
        if (entry is null)
        {
            if (_clients.Count == MaximumClients)
            {
                // Deterministic bounded LRU by measurement time; this is eviction, never an exit claim.
                SessionProcessKey oldest = default; DateTimeOffset oldestTime = DateTimeOffset.MaxValue; bool found = false;
                foreach (var pair in _clients)
                    if (!found || pair.Value.LastObserved < oldestTime) { oldest = pair.Key; oldestTime = pair.Value.LastObserved; found = true; }
                _clients.Remove(oldest);
            }
            entry = new(); _clients.Add(key, entry);
        }
        bool valid = row.Problem is null && row.WorkingSetBytes >= 0 && row.PrivateCommitBytes >= 0
            && ValidRate(row.CpuPercent, 100) && ValidRate(row.PageFaultsPerSecond);
        ProcessIoRates? io = valid ? AcceptIo(row.IoRates, entry, key, completed, now) : null;
        if (entry.LastObserved != default && row.ObservedUtc - entry.LastObserved > MaximumClientAge)
            entry.Points.Add(new(entry.LastObserved.ToUniversalTime() + MaximumClientAge, null, null, null, null, IsGap: true));
        entry.Points.Add(valid
            ? new(row.ObservedUtc, row.CpuPercent, row.WorkingSetBytes, row.PrivateCommitBytes, row.PageFaultsPerSecond, io)
            : new(row.ObservedUtc, null, null, null, null, IsGap: true));
        entry.LastObserved = row.ObservedUtc;
        Trim(entry.Points, row.ObservedUtc, static point => point.ObservedUtc);
        entry.Revision++; entry.Snapshot = null;
        return true;
    }

    private static ProcessIoRates? AcceptIo(ProcessIoRates? rates, ClientEntry entry, SessionProcessKey process,
        DateTimeOffset completed, DateTimeOffset now)
    {
        if (rates is null || rates.ObservedUtc <= entry.LastIoObserved || rates.ObservedUtc.UtcTicks < process.ProcessStartUtcTicks
            || rates.ObservedUtc > completed || !Fresh(rates.ObservedUtc, now, MaximumClientAge)) return null;
        entry.LastIoObserved = rates.ObservedUtc;
        // Each unsupported/reset/malformed counter remains unknown independently. Never reuse a cached rate.
        return new(rates.ObservedUtc, SafeRate(rates.ReadBytesPerSecond), SafeRate(rates.WriteBytesPerSecond),
            SafeRate(rates.OtherBytesPerSecond), SafeRate(rates.ReadOperationsPerSecond),
            SafeRate(rates.WriteOperationsPerSecond), SafeRate(rates.OtherOperationsPerSecond));
    }

    private static bool Fresh(DateTimeOffset observed, DateTimeOffset now, TimeSpan age) => observed != default && observed <= now && now - observed <= age;
    private static bool ValidRate(double? value, double maximum = double.MaxValue) => value is null || double.IsFinite(value.Value) && value.Value >= 0 && value.Value <= maximum;
    private static double? SafeRate(double? value) => ValidRate(value) ? value : null;
    private static void Trim<T>(List<T> points, DateTimeOffset latest, Func<T, DateTimeOffset> timestamp)
    {
        DateTimeOffset cutoff = new(Math.Max(0, latest.UtcTicks - Window.Ticks), TimeSpan.Zero); int remove = 0;
        while (remove < points.Count && timestamp(points[remove]) < cutoff) remove++;
        remove = Math.Max(remove, points.Count - MaximumPoints);
        if (remove > 0) points.RemoveRange(0, remove);
    }
}
