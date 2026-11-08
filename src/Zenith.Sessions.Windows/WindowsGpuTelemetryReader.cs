using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>Small native boundary for deterministic counter/buffer fixtures. Starts no background work.</summary>
public interface IGpuPdhApi
{
    uint OpenQuery(out nint query);
    uint AddEnglishCounter(nint query, string path, out nint counter);
    // UTC FILETIME after the native adapter normalizes PDH's local collection timestamp.
    uint Collect(nint query, out long fileTime);
    uint GetFormattedArray(nint counter, uint format, ref uint bufferBytes, out uint itemCount, nint buffer);
    uint CloseQuery(nint query);
}

/// <summary>
/// What a caller supplies to receive per-client GPU numbers. <paramref name="Candidates"/> maps each client PID to the start
/// time its process inventory read before this collection; <paramref name="Confirm"/> reads the PID again after the collection
/// and returns true only when it still carries that start time. Rows of any other PID are never attributed.
/// </summary>
public sealed record GpuClientAttribution(IReadOnlyDictionary<int, long> Candidates, Func<int, long, CancellationToken, bool> Confirm);

/// <summary>
/// One serialized PDH query, called by the existing system sampler. Native PDH calls are synchronous
/// and not interruptible: cancellation is checked around calls; no timed-out native call is abandoned
/// and no worker/thread/timer is created here. Dispose waits for the current owned read to finish.
/// </summary>
public sealed class WindowsGpuTelemetryReader : IDisposable
{
    public const int MaximumCounterItems = 8192, MaximumBufferBytes = 2 * 1024 * 1024;
    public const int MaximumAdapters = 16, MaximumEnginesPerAdapter = 128, MaximumInstanceCharacters = 256;
    public const string EngineCounterPath = @"\GPU Engine(*)\Utilization Percentage";
    public const string DedicatedCounterPath = @"\GPU Adapter Memory(*)\Dedicated Usage";
    public const string SharedCounterPath = @"\GPU Adapter Memory(*)\Shared Usage";
    public const string ProcessMemoryCounterPath = @"\GPU Process Memory(*)\Dedicated Usage";
    private const uint MoreData = 0x800007d2, DoubleFormat = 0x200 | 0x8000, LargeFormat = 0x400 | 0x1000;
    private readonly object _gate = new();
    private readonly IGpuPdhApi _api;
    private readonly TimeProvider _clock;
    private readonly BufferHandle _buffer = new();
    private QueryHandle? _query;
    private nint _engine, _dedicated, _shared, _process;
    private bool _hasBaseline, _disposed;
    private long? _retryAfter, _processRetryAfter;
    private DateTimeOffset? _lastCollection;

    public WindowsGpuTelemetryReader(TimeProvider? timeProvider = null, IGpuPdhApi? api = null)
    { _clock = timeProvider ?? TimeProvider.System; _api = api ?? new NativePdhApi(); }

    public GpuTelemetrySnapshot Read(CancellationToken ct = default) => Read(null, ct);

    /// <summary>
    /// Adapter counters as <see cref="Read(CancellationToken)"/>, plus per-client numbers for <paramref name="clients"/> when
    /// it is given: the per-process dedicated-memory gauge is added to the open query on first use (never when the caller
    /// passes null, so the default-off option costs nothing) and each candidate with GPU rows is confirmed after the collection.
    /// </summary>
    public GpuTelemetrySnapshot Read(GpuClientAttribution? clients, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            try { return ReadOwned(clients, ct); }
            // Initialization can have added only some counters when cancellation arrives.
            // Query ownership also covers those not yet assigned to fields; close and rebuild.
            catch (OperationCanceledException) { ResetQuery(); throw; }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            { ResetQuery(); ScheduleRetry(); return GpuTelemetrySnapshot.Unavailable("Windows GPU counters are unavailable on this system."); }
        }
    }

    private GpuTelemetrySnapshot ReadOwned(GpuClientAttribution? clients, CancellationToken ct)
    {
        if (_query is not null && _retryAfter is { } partialRetry
            && _clock.GetElapsedTime(partialRetry, _clock.GetTimestamp()) >= TimeSpan.FromSeconds(30))
        {
            // Re-add only the missing counters on the open query. Rebuilding it would discard the
            // utilization baseline of a working engine counter every 30 s and publish an unknown
            // engine sample each time, which keeps an established GPU hold from ever recovering.
            // Added counters stay owned by the query; a cancelled Add is closed by Read's catch.
            bool engineWasMissing = _engine == 0;
            if (_engine == 0) _engine = Add(EngineCounterPath, ct);
            if (_dedicated == 0) _dedicated = Add(DedicatedCounterPath, ct);
            if (_shared == 0) _shared = Add(SharedCounterPath, ct);
            // A newly added rate counter has no earlier raw value: warm it up before reporting load.
            // Memory counters are gauges and need no baseline.
            if (engineWasMissing && _engine != 0) _hasBaseline = false;
            _retryAfter = _engine == 0 || _dedicated == 0 || _shared == 0 ? _clock.GetTimestamp() : null;
        }
        if (_query is null)
        {
            if (_retryAfter is { } retry && _clock.GetElapsedTime(retry, _clock.GetTimestamp()) < TimeSpan.FromSeconds(30))
                return GpuTelemetrySnapshot.Unavailable("Windows GPU counter initialization is unavailable; retry is deferred.");
            ct.ThrowIfCancellationRequested();
            uint opened = _api.OpenQuery(out nint query);
            if (query != 0) _query = new(_api, query);
            ct.ThrowIfCancellationRequested();
            if (opened != 0 || _query is null || _query.IsInvalid)
            { ResetQuery(); ScheduleRetry(); return GpuTelemetrySnapshot.Unavailable("Windows GPU counter query could not be opened."); }
            _engine = Add(EngineCounterPath, ct); _dedicated = Add(DedicatedCounterPath, ct); _shared = Add(SharedCounterPath, ct);
            if (_engine == 0 && _dedicated == 0 && _shared == 0)
            { ResetQuery(); ScheduleRetry(); return GpuTelemetrySnapshot.Unavailable("WDDM GPU utilization and memory counters are unavailable."); }
            // A partially available provider can recover without restarting the application.
            _retryAfter = _engine == 0 || _dedicated == 0 || _shared == 0 ? _clock.GetTimestamp() : null;
        }
        if (clients is not null && _process == 0 && (_processRetryAfter is not { } processRetry
            || _clock.GetElapsedTime(processRetry, _clock.GetTimestamp()) >= TimeSpan.FromSeconds(30)))
        {
            // The per-process memory gauge joins the open query only while a caller asks for client numbers; it is a gauge,
            // so it needs no baseline and the engine counter's baseline is untouched. It stays owned by the query once added
            // (closed with it); a caller that stops asking simply stops reading it. A failed add is retried every 30 s.
            _process = Add(ProcessMemoryCounterPath, ct);
            _processRetryAfter = _process == 0 ? _clock.GetTimestamp() : null;
        }
        ct.ThrowIfCancellationRequested();
        uint collected = _api.Collect(_query.DangerousGetHandle(), out long fileTime);
        ct.ThrowIfCancellationRequested();
        if (collected != 0)
        { ResetQuery(); ScheduleRetry(); return GpuTelemetrySnapshot.Unavailable("Windows GPU counter collection failed; no usage was inferred."); }
        DateTimeOffset at;
        try { at = new(DateTime.FromFileTimeUtc(fileTime)); }
        catch (ArgumentOutOfRangeException)
        { _hasBaseline = false; _lastCollection = null; return GpuTelemetrySnapshot.Unavailable("Windows GPU counter observation time was invalid or ambiguous."); }
        var now = _clock.GetUtcNow();
        if (at > now.AddSeconds(1) || now - at > TimeSpan.FromMilliseconds(GpuTelemetrySnapshot.FreshnessMilliseconds)
            || (_lastCollection is { } last && at <= last))
        { _hasBaseline = false; _lastCollection = null; return GpuTelemetrySnapshot.Unavailable("Windows GPU counter observation time was stale or non-increasing."); }
        // A current collection after a stopped/slow sampler must not relabel a long-window
        // average as fresh load. Keep memory gauges, but warm up utilization again after a gap.
        bool ratesReady = _hasBaseline && _lastCollection is { } previous
            && at - previous <= TimeSpan.FromMilliseconds(GpuTelemetrySnapshot.FreshnessMilliseconds);
        _hasBaseline = true; _lastCollection = at;
        var engines = ReadArray(_engine, DoubleFormat, ct);
        var dedicated = ReadArray(_dedicated, LargeFormat, ct);
        var shared = ReadArray(_shared, LargeFormat, ct);
        var processMemory = clients is not null && _process != 0 ? ReadArray(_process, LargeFormat, ct) : Missing;
        ct.ThrowIfCancellationRequested();
        return Aggregate(at, engines, dedicated, shared, processMemory, ratesReady, clients, ct);
    }

    private nint Add(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        uint status = _api.AddEnglishCounter(_query!.DangerousGetHandle(), path, out nint counter);
        ct.ThrowIfCancellationRequested();
        return status == 0 ? counter : 0;
    }

    private readonly record struct CounterItem(string Name, uint Status, double Percent, long Bytes);
    private sealed record CounterBatch(CounterItem[] Items, bool Available);
    private static readonly CounterBatch Missing = new([], false);

    private CounterBatch ReadArray(nint counter, uint format, CancellationToken ct)
    {
        if (counter == 0) return Missing;
        // Re-probe from zero on growth: Microsoft says the returned required size after an
        // undersized nonzero call is not reliable. Two attempts bound instance-churn work.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            uint needed = 0;
            uint status = _api.GetFormattedArray(counter, format, ref needed, out uint count, 0);
            ct.ThrowIfCancellationRequested();
            if (status == 0 && needed == 0 && count == 0) return new([], true);
            if (status != MoreData || needed == 0 || needed > MaximumBufferBytes || count > MaximumCounterItems) return Missing;
            _buffer.EnsureCapacity((int)needed);
            uint used = (uint)_buffer.Capacity;
            ct.ThrowIfCancellationRequested();
            status = _api.GetFormattedArray(counter, format, ref used, out count, _buffer.DangerousGetHandle());
            ct.ThrowIfCancellationRequested();
            if (status == MoreData) continue;
            int stride = Marshal.SizeOf<NativeItem>();
            if (status != 0 || used > _buffer.Capacity || count > MaximumCounterItems || (ulong)count * (uint)stride > used) return Missing;
            var items = new CounterItem[count];
            for (int index = 0; index < items.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var item = Marshal.PtrToStructure<NativeItem>(_buffer.DangerousGetHandle() + index * stride);
                string? name = ReadName(item.Name, (int)used, checked((int)count * stride));
                if (name is null) return Missing;
                items[index] = new(name, item.Value.Status, item.Value.Double, item.Value.Large);
            }
            return new(items, true);
        }
        return Missing;
    }

    private string? ReadName(nint pointer, int used, int minimumOffset)
    {
        nuint start = (nuint)_buffer.DangerousGetHandle(), address = (nuint)pointer;
        if (address < start || address - start < (nuint)minimumOffset || address - start >= (nuint)used
            || ((address - start) & 1) != 0) return null;
        int available = Math.Min(MaximumInstanceCharacters + 1, (used - (int)(address - start)) / 2);
        for (int chars = 0; chars < available; chars++)
            if (Marshal.ReadInt16(pointer, chars * 2) == 0) return chars == 0 ? null : Marshal.PtrToStringUni(pointer, chars);
        return null;
    }

    private readonly record struct AdapterKey(string Luid, int Physical);
    private sealed class Engine(int index, string type)
    { public readonly int Index = index; public readonly string Type = type; public double Sum; public bool Valid = true; }
    private sealed class Adapter
    {
        public readonly Dictionary<int, Engine> Engines = [];
        public ulong? Dedicated, Shared;
        public bool EnginesValid = true, DedicatedSeen, SharedSeen;
    }
    /// <summary>One candidate client's rows in this collection. Its engine value is its own maximum, never a sum.</summary>
    private sealed class Client
    {
        public readonly List<Engine> Engines = [];
        public double Busiest;
        public ulong Dedicated;
        public bool EngineSeen, EngineValid = true, DedicatedSeen, DedicatedValid = true;
    }

    private static GpuTelemetrySnapshot Aggregate(DateTimeOffset at, CounterBatch engines, CounterBatch dedicated,
        CounterBatch shared, CounterBatch processMemory, bool ratesReady, GpuClientAttribution? clients, CancellationToken ct)
    {
        var adapters = new Dictionary<AdapterKey, Adapter>();
        bool engineComplete = engines.Available, memoryComplete = dedicated.Available && shared.Available;
        bool bounded = true;
        Adapter? GetAdapter(AdapterKey key)
        {
            if (adapters.TryGetValue(key, out var existing)) return existing;
            if (adapters.Count >= MaximumAdapters) { bounded = false; return null; }
            var created = new Adapter(); adapters.Add(key, created); return created;
        }
        // Only the caller's candidates are tracked, so the per-client work is bounded by its inventory, not by the provider.
        Dictionary<int, Client>? byPid = clients is null ? null : [];
        Client? GetClient(int pid)
        {
            if (byPid is null || !clients!.Candidates.ContainsKey(pid)) return null;
            if (!byPid.TryGetValue(pid, out var client)) { client = new(); byPid.Add(pid, client); }
            return client;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in engines.Items)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryEngine(row.Name.AsSpan(), out var key, out int pid, out int index, out string type)) { engineComplete = false; continue; }
            var client = GetClient(pid);
            var adapter = GetAdapter(key);
            if (adapter is null) { if (client is not null) client.EngineValid = false; continue; }
            if (!adapter.Engines.TryGetValue(index, out var engine))
            {
                if (adapter.Engines.Count >= MaximumEnginesPerAdapter)
                { adapter.EnginesValid = false; bounded = false; if (client is not null) client.EngineValid = false; continue; }
                engine = new(index, type); adapter.Engines.Add(index, engine);
            }
            bool valid = seen.Add(row.Name) && engine.Type == type && ValidStatus(row.Status)
                && double.IsFinite(row.Percent) && row.Percent is >= 0 and <= 100 && ratesReady;
            if (!valid) engine.Valid = false;
            else engine.Sum += row.Percent;
            // Only processes sharing this exact physical engine are added. Independent engines
            // execute in parallel, so neither their sum nor a sum of each PID's max is a total.
            if (engine.Sum > 100 || !double.IsFinite(engine.Sum)) engine.Valid = false;
            if (client is null) continue;
            // A client's number is the largest of its own rows (one per engine it uses); a row this collection cannot trust
            // makes the whole client unknown, as does an engine whose contributors add up to more than 100 (checked below).
            client.EngineSeen = true; client.Engines.Add(engine);
            if (valid) client.Busiest = Math.Max(client.Busiest, row.Percent); else client.EngineValid = false;
        }
        bool processMemoryRecognized = true;
        if (byPid is not null && processMemory.Available)
            foreach (var row in processMemory.Items)
            {
                ct.ThrowIfCancellationRequested();
                // One instance outside the recognized convention could hide part of any client's memory: all become unknown.
                if (!TryProcessMemory(row.Name.AsSpan(), out int pid, out _)) { processMemoryRecognized = false; continue; }
                var client = GetClient(pid); if (client is null) continue;
                client.DedicatedSeen = true;
                bool valid = seen.Add(row.Name) && ValidStatus(row.Status) && row.Bytes >= 0;
                if (!valid) client.DedicatedValid = false;
                else client.Dedicated = client.Dedicated > ulong.MaxValue - (ulong)row.Bytes ? ulong.MaxValue : client.Dedicated + (ulong)row.Bytes;
            }
        void ReadMemory(CounterBatch batch, bool isDedicated)
        {
            foreach (var row in batch.Items)
            {
                ct.ThrowIfCancellationRequested();
                if (!TryMemory(row.Name.AsSpan(), out var key)) { memoryComplete = false; continue; }
                var adapter = GetAdapter(key); if (adapter is null) continue;
                bool already = isDedicated ? adapter.DedicatedSeen : adapter.SharedSeen;
                bool valid = !already && ValidStatus(row.Status) && row.Bytes >= 0;
                if (isDedicated) { adapter.DedicatedSeen = true; adapter.Dedicated = valid ? (ulong)row.Bytes : null; }
                else { adapter.SharedSeen = true; adapter.Shared = valid ? (ulong)row.Bytes : null; }
                if (!valid) memoryComplete = false;
            }
        }
        ReadMemory(dedicated, true); ReadMemory(shared, false);
        var keys = new List<AdapterKey>(adapters.Keys);
        keys.Sort(static (a, b) => { int order = string.CompareOrdinal(a.Luid, b.Luid); return order != 0 ? order : a.Physical.CompareTo(b.Physical); });
        var output = new GpuAdapterTelemetry[keys.Count];
        bool complete = bounded && engineComplete && memoryComplete && ratesReady;
        for (int position = 0; position < keys.Count; position++)
        {
            ct.ThrowIfCancellationRequested();
            var key = keys[position]; var adapter = adapters[key];
            var indexes = new List<int>(adapter.Engines.Keys); indexes.Sort();
            var values = new GpuEngineTelemetry[indexes.Count];
            bool engineValid = engineComplete && bounded && adapter.EnginesValid && ratesReady && values.Length > 0;
            double busiest = 0;
            for (int index = 0; index < indexes.Count; index++)
            {
                var engine = adapter.Engines[indexes[index]];
                double? percent = engine.Valid ? engine.Sum : null;
                values[index] = new(engine.Index, engine.Type, percent);
                if (percent is { } known) busiest = Math.Max(busiest, known); else engineValid = false;
            }
            bool adapterComplete = engineValid && adapter.Dedicated is not null && adapter.Shared is not null;
            complete &= adapterComplete;
            output[position] = new(key.Luid, key.Physical, Array.AsReadOnly(values), engineValid ? busiest : null,
                adapter.Dedicated, adapter.Shared, engineValid,
                adapterComplete ? null : "Some GPU counters are unavailable, warming up or invalid; unknown values were not replaced with zero.");
        }
        var confirmed = ConfirmClients(byPid, clients, engineComplete && bounded && ratesReady,
            processMemory.Available && processMemoryRecognized, ct);
        if (output.Length == 0) return new(at, Array.Empty<GpuAdapterTelemetry>(), "No recognized WDDM GPU adapter counters were available.", confirmed);
        return new(at, Array.AsReadOnly(output), complete ? null : "GPU telemetry is partial; unavailable counters and unrecognized formats remain unknown.", confirmed);
    }

    /// <summary>
    /// The second half of the identity bracket. Each candidate with rows is checked once, after the collection: the caller
    /// re-reads the PID and answers whether it still carries the start time its inventory saw before the collection. A
    /// process alive with one start time on both sides was alive throughout, so the rows collected in between are its own;
    /// a refused PID (ended, reused, unreadable) gets no row at all rather than a guess.
    /// </summary>
    private static IReadOnlyList<GpuClientTelemetry>? ConfirmClients(Dictionary<int, Client>? byPid, GpuClientAttribution? clients,
        bool enginesTrusted, bool memoryTrusted, CancellationToken ct)
    {
        if (byPid is null || clients is null || byPid.Count == 0) return null;
        var pids = new List<int>(byPid.Keys); pids.Sort();
        var rows = new List<GpuClientTelemetry>(pids.Count);
        foreach (int pid in pids)
        {
            ct.ThrowIfCancellationRequested();
            var client = byPid[pid]; long start = clients.Candidates[pid];
            if (!clients.Confirm(pid, start, ct)) continue;
            bool engineKnown = enginesTrusted && client.EngineSeen && client.EngineValid;
            for (int index = 0; engineKnown && index < client.Engines.Count; index++) engineKnown = client.Engines[index].Valid;
            bool memoryKnown = memoryTrusted && client.DedicatedSeen && client.DedicatedValid;
            rows.Add(new(pid, start, engineKnown ? client.Busiest : null, memoryKnown ? client.Dedicated : null));
        }
        return rows.Count == 0 ? null : Array.AsReadOnly(rows.ToArray());
    }

    private static bool ValidStatus(uint status) => status is 0 or 1; // PDH_CSTATUS_VALID_DATA / NEW_DATA.

    // The recognized WDDM instance convention is checked strictly, not an OS/driver naming promise. Unknown layouts are
    // rejected. The PID in an instance name distinguishes contributors on an engine; on its own it cannot prove which
    // process incarnation it names, so rows are attached to a client only through GpuClientAttribution's two-sided check.
    private static bool TryEngine(ReadOnlySpan<char> value, out AdapterKey key, out int pid, out int engine, out string type)
    {
        key = default; pid = 0; engine = 0; type = "";
        if (!Consume(ref value, "pid_") || !Number(ref value, "_luid_", out pid)
            || !Luid(ref value, out string luid) || !Consume(ref value, "_phys_")
            || !Number(ref value, "_eng_", out int physical) || physical > 255
            || !Number(ref value, "_engtype_", out engine) || engine > 4095 || value.Length is 0 or > 64) return false;
        foreach (char character in value) if (!char.IsAsciiLetterOrDigit(character) && character != '_') return false;
        key = new(luid, physical); type = value.ToString(); return true;
    }
    // GPU Process Memory instances name the process and adapter: pid_<pid>_luid_<high>_<low>_phys_<n>.
    private static bool TryProcessMemory(ReadOnlySpan<char> value, out int pid, out AdapterKey key)
    {
        key = default; pid = 0;
        if (!Consume(ref value, "pid_") || !Number(ref value, "_luid_", out pid) || !Luid(ref value, out string luid)
            || !Consume(ref value, "_phys_")
            || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int physical) || physical is < 0 or > 255) return false;
        key = new(luid, physical); return true;
    }
    private static bool TryMemory(ReadOnlySpan<char> value, out AdapterKey key)
    {
        key = default;
        if (!Consume(ref value, "luid_") || !Luid(ref value, out string luid) || !Consume(ref value, "_phys_")
            || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int physical) || physical is < 0 or > 255) return false;
        key = new(luid, physical); return true;
    }
    private static bool Luid(ref ReadOnlySpan<char> value, out string luid)
    {
        luid = "";
        if (value.Length < 21 || !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || value[10] != '_' || !value.Slice(11, 2).Equals("0x", StringComparison.OrdinalIgnoreCase)
            || !uint.TryParse(value.Slice(2, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint high)
            || !uint.TryParse(value.Slice(13, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint low)) return false;
        luid = $"0x{high:x8}_0x{low:x8}"; value = value[21..]; return true;
    }
    private static bool Number(ref ReadOnlySpan<char> value, ReadOnlySpan<char> separator, out int number)
    {
        int split = value.IndexOf(separator, StringComparison.Ordinal);
        if (split <= 0 || !int.TryParse(value[..split], NumberStyles.None, CultureInfo.InvariantCulture, out number)) { number = 0; return false; }
        value = value[(split + separator.Length)..]; return true;
    }
    private static bool Consume(ref ReadOnlySpan<char> value, ReadOnlySpan<char> prefix)
    { if (!value.StartsWith(prefix, StringComparison.Ordinal)) return false; value = value[prefix.Length..]; return true; }

    private void ScheduleRetry() => _retryAfter = _clock.GetTimestamp();
    private void ResetQuery()
    {
        _query?.Dispose(); _query = null; _engine = _dedicated = _shared = _process = 0;
        _hasBaseline = false; _lastCollection = null; _processRetryAfter = null;
    }
    public void Dispose()
    { lock (_gate) { if (_disposed) return; _disposed = true; ResetQuery(); _buffer.Dispose(); } }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct NativeValue
    { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Double; [FieldOffset(8)] public long Large; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeItem { public nint Name; public NativeValue Value; }
    private sealed class QueryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly IGpuPdhApi _owner;
        public QueryHandle(IGpuPdhApi owner, nint query) : base(true) { _owner = owner; SetHandle(query); }
        protected override bool ReleaseHandle() => _owner.CloseQuery(handle) == 0;
    }
    private sealed class BufferHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public int Capacity { get; private set; }
        public BufferHandle() : base(true) { }
        public void EnsureCapacity(int size)
        {
            if (size <= Capacity) return;
            nint next = Marshal.AllocHGlobal(size);
            if (!IsInvalid) Marshal.FreeHGlobal(handle);
            SetHandle(next); Capacity = size;
        }
        protected override bool ReleaseHandle() { Marshal.FreeHGlobal(handle); Capacity = 0; return true; }
    }
    private sealed class NativePdhApi : IGpuPdhApi
    {
        // Language-neutral paths, wildcard-array sizing/format/status, original collection time,
        // and query-owned counter lifetimes are documented by Microsoft:
        // https://learn.microsoft.com/windows/win32/api/pdh/nf-pdh-pdhaddenglishcounterw
        // https://learn.microsoft.com/windows/win32/api/pdh/nf-pdh-pdhgetformattedcounterarrayw
        // https://learn.microsoft.com/windows/win32/api/pdh/nf-pdh-pdhcollectquerydatawithtime
        // https://devblogs.microsoft.com/directx/gpus-in-the-task-manager/
        public uint OpenQuery(out nint query) => PdhOpenQueryW(null, 0, out query);
        public uint AddEnglishCounter(nint query, string path, out nint counter) => PdhAddEnglishCounterW(query, path, 0, out counter);
        public uint Collect(nint query, out long fileTime)
        {
            uint status=PdhCollectQueryDataWithTime(query,out long localFileTime);
            fileTime=status==0 && TryConvertPdhTimestamp(localFileTime,TimeZoneInfo.Local,out long utc)?utc:-1;
            return status;
        }
        public uint GetFormattedArray(nint counter, uint format, ref uint bufferBytes, out uint itemCount, nint buffer)
            => PdhGetFormattedCounterArrayW(counter, format, ref bufferBytes, out itemCount, buffer);
        public uint CloseQuery(nint query) => PdhCloseQuery(query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint PdhOpenQueryW(string? source, nuint userData, out nint query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint PdhAddEnglishCounterW(nint query, string path, nuint userData, out nint counter);
        [DllImport("pdh.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint PdhCollectQueryDataWithTime(nint query, out long fileTime);
        [DllImport("pdh.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferBytes, out uint count, nint buffer);
        [DllImport("pdh.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint PdhCloseQuery(nint query);
    }

    // PDH timestamps use local FILETIME: https://learn.microsoft.com/windows/win32/api/pdh/ns-pdh-pdh_raw_counter
    // A native two-collection probe also verified this on the reference machine. Treating those
    // wall-clock fields as UTC wrongly ages every sample by the machine's time-zone offset.
    // Ambiguous/invalid DST times stay unknown rather than choosing an offset that could renew stale evidence.
    internal static bool TryConvertPdhTimestamp(long localFileTime,TimeZoneInfo zone,out long utcFileTime)
    {
        utcFileTime=-1;
        try
        {
            var wall=DateTime.SpecifyKind(DateTime.FromFileTimeUtc(localFileTime),DateTimeKind.Unspecified);
            if(zone.IsInvalidTime(wall)||zone.IsAmbiguousTime(wall)) return false;
            utcFileTime=TimeZoneInfo.ConvertTimeToUtc(wall,zone).ToFileTimeUtc();
            return true;
        }
        catch(ArgumentException){return false;}
    }
}
