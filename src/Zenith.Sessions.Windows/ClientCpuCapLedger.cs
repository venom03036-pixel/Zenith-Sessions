using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zenith.Sessions.Windows;

/// <summary>One exact client instance (PID and start time) whose CPU a run of Zenith capped and has not cleared.</summary>
internal sealed record ClientCpuCapRecord(int ProcessId, long StartUtcTicks);

/// <summary>
/// The clients whose CPU Zenith has capped and not yet cleared, by exact instance, kept outside memory. A process stays in
/// its job for life and a job can be reached only through a handle, so a cap outlives a Zenith that crashed and no later
/// run can lift it (a job's name is unlinked when its last handle closes). The record lets the next start say which clients
/// still carry such a cap, and drop the records of clients that have exited. A record holds no secret: a PID and a start time.
/// </summary>
internal interface IClientCpuCapLedger
{
    const int Capacity = 128;
    /// <summary>True when the saved record could not be read at this start, so clients an earlier run left capped cannot be named.</summary>
    bool Unreadable { get; }
    IReadOnlyList<ClientCpuCapRecord> Records { get; }
    bool Contains(int processId, long startUtcTicks);
    /// <summary>Saves the record before the cap is set. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>
    /// when it cannot be saved, and <see cref="InvalidOperationException"/> when <see cref="Capacity"/> records are already held.</summary>
    void Remember(ClientCpuCapRecord record);
    void Forget(int processId, long startUtcTicks);
}

/// <summary>Records kept for this process only: for tests, and for a control whose record need not outlive its own run.</summary>
internal sealed class InMemoryClientCpuCapLedger : IClientCpuCapLedger
{
    private readonly object _gate = new();
    private readonly List<ClientCpuCapRecord> _records = [];
    public bool Unreadable => false;
    public IReadOnlyList<ClientCpuCapRecord> Records { get { lock (_gate) return _records.ToArray(); } }
    public bool Contains(int processId, long startUtcTicks)
    {
        lock (_gate) return _records.Exists(record => record.ProcessId == processId && record.StartUtcTicks == startUtcTicks);
    }
    public void Remember(ClientCpuCapRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            if (_records.Contains(record)) return;
            if (_records.Count >= IClientCpuCapLedger.Capacity) throw new InvalidOperationException("The CPU cap record is full.");
            _records.Add(record);
        }
    }
    public void Forget(int processId, long startUtcTicks)
    {
        lock (_gate) _records.RemoveAll(record => record.ProcessId == processId && record.StartUtcTicks == startUtcTicks);
    }
}

/// <summary>
/// The records in one small JSON file in Zenith's data folder, read once on first use and written through on every change
/// (a temporary file replaces the old one). A damaged file is reported through <see cref="Unreadable"/> and replaced by the
/// next change instead of being guessed at. Every call runs on the priority control's single worker.
/// </summary>
internal sealed class ClientCpuCapLedgerFile(string path, DateTimeOffset? bootUtc = null) : IClientCpuCapLedger
{
    private readonly string _path = path ?? throw new ArgumentNullException(nameof(path));
    private readonly DateTimeOffset _bootUtc = bootUtc ?? DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
    private readonly object _gate = new();
    private List<ClientCpuCapRecord>? _records;
    private bool _unreadable;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public bool Unreadable { get { lock (_gate) { Load(); return _unreadable; } } }
    public IReadOnlyList<ClientCpuCapRecord> Records { get { lock (_gate) return Load().ToArray(); } }
    public bool Contains(int processId, long startUtcTicks)
    {
        lock (_gate) return Load().Exists(record => record.ProcessId == processId && record.StartUtcTicks == startUtcTicks);
    }
    public void Remember(ClientCpuCapRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            var records = Load();
            if (records.Contains(record)) return;
            if (records.Count >= IClientCpuCapLedger.Capacity) throw new InvalidOperationException("The CPU cap record is full.");
            var kept = new List<ClientCpuCapRecord>(records) { record };
            Save(kept);
        }
    }
    public void Forget(int processId, long startUtcTicks)
    {
        lock (_gate)
        {
            var kept = new List<ClientCpuCapRecord>(Load());
            if (kept.RemoveAll(record => record.ProcessId == processId && record.StartUtcTicks == startUtcTicks) > 0) Save(kept);
        }
    }

    private List<ClientCpuCapRecord> Load()
    {
        if (_records is not null) return _records;
        var records = new List<ClientCpuCapRecord>();
        if (File.Exists(_path))
        {
            try
            {
                var file = JsonSerializer.Deserialize<LedgerFile>(File.ReadAllBytes(_path), Options);
                if (file is not { Schema: 1 }) _unreadable = true;
                // Jobs cannot survive a Windows restart. Legacy schema-1 records have no boot stamp; retain only
                // instances whose start could belong to this boot. Two minutes allow normal clock-reading jitter.
                else if (file.BootUtc is null || (file.BootUtc.Value - _bootUtc).Duration() <= TimeSpan.FromMinutes(2))
                    foreach (var row in file.Caps ?? [])
                    if (row is { ProcessId: > 0, StartUtcTicks: > 0 } && records.Count < IClientCpuCapLedger.Capacity
                        && (file.BootUtc is not null || row.StartUtcTicks >= _bootUtc.AddMinutes(-2).UtcTicks))
                        records.Add(new(row.ProcessId, row.StartUtcTicks));
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { _unreadable = true; }
        }
        return _records = records;
    }

    private void Save(List<ClientCpuCapRecord> records)
    {
        var file = new LedgerFile { BootUtc = _bootUtc, Caps = records.Select(record => new LedgerRow { ProcessId = record.ProcessId, StartUtcTicks = record.StartUtcTicks }).ToArray() };
        string folder = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(folder);
        string temporary = _path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(file, Options));
        if (File.Exists(_path)) File.Replace(temporary, _path, null);
        else File.Move(temporary, _path);
        _records = records;
        _unreadable = false;
    }

    private sealed class LedgerFile
    {
        [JsonPropertyName("schema")] public int Schema { get; set; } = 1;
        [JsonPropertyName("bootUtc")] public DateTimeOffset? BootUtc { get; set; }
        [JsonPropertyName("caps")] public LedgerRow[]? Caps { get; set; }
    }
    private sealed class LedgerRow
    {
        [JsonPropertyName("processId")] public int ProcessId { get; set; }
        [JsonPropertyName("startUtcTicks")] public long StartUtcTicks { get; set; }
    }
}
