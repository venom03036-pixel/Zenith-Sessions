using System.Text.Json;
using System.Text.Json.Serialization;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// The upload limit (R-instances-18) on Windows: Zenith's QoS policies, one per Roblox client executable, created and removed
/// in the active policy store by Windows PowerShell run as administrator through the UAC prompt the user answers. One
/// operation at a time; Cancel, Panic, Lock and exit stop the wait, never a run the user already approved. The record of what
/// was asked for lives in <see cref="RecordFileName"/> in the data folder for this Windows session; the policy itself ends when
/// Windows restarts, and so does the record. Nothing about the client is changed and nothing is read from it.
/// </summary>
public sealed class WindowsUploadLimitControl : IUploadLimitControl
{
    public const string RecordFileName = UploadLimitPolicy.RecordFileName;
    public static TimeSpan HelperTimeout => UploadLimitPolicy.HelperTimeout;

    private readonly IElevatedPowerShellRunner _runner;
    private readonly UploadLimitRecordFile _file;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan> _uptime;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _closing = new();
    private CancellationTokenSource? _stop;
    private Task<UploadLimitResult>? _operation;
    private OperationState? _latest;
    private long _revision;
    private UploadLimitRecord? _current;
    private bool _unreadable, _closed;
    private sealed class OperationState(UploadLimitAction action, int rate, UploadLimitRecord? previous, bool unreadable, long revision)
    {
        public readonly UploadLimitAction Action = action;
        public readonly int Rate = rate;
        public readonly UploadLimitRecord? Previous = previous;
        public readonly bool PreviousUnreadable = unreadable;
        public readonly long Revision = revision;
        public ElevatedRunKind? NoStart;
        public bool Cancelled;
    }

    public event Action<UploadLimitReconciliation>? Reconciled;
    public long OperationRevision { get { lock (_gate) return _revision; } }

    public WindowsUploadLimitControl(string recordPath) : this(recordPath, new ElevatedPowerShellRunner(), TimeProvider.System, null) { }

    internal WindowsUploadLimitControl(string recordPath, IElevatedPowerShellRunner runner, TimeProvider clock, Func<TimeSpan>? uptime)
    {
        _file = new(recordPath ?? throw new ArgumentNullException(nameof(recordPath)));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _uptime = uptime ?? (static () => TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    public UploadLimitRecord? Current { get { lock (_gate) return _current; } }
    public bool RecordUnreadable { get { lock (_gate) return _unreadable; } }

    // Windows' own time since it started; two readings differ by far less than the tolerance a record is judged with.
    private DateTimeOffset BootUtc() => _clock.GetUtcNow() - _uptime();

    public Task<UploadLimitRecord?> LoadRecordAsync(CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var loaded = _file.Load(BootUtc());
        lock (_gate) { _current = loaded.Record; _unreadable = loaded.Unreadable; }
        return loaded.Record;
    }, ct);

    public Task<UploadLimitResult> ApplyAsync(int kilobitsPerSecond, CancellationToken ct) => RunAsync(UploadLimitAction.Apply, kilobitsPerSecond, ct);
    public Task<UploadLimitResult> RemoveAsync(CancellationToken ct) => RunAsync(UploadLimitAction.Remove, 0, ct);

    private Task<UploadLimitResult> RunAsync(UploadLimitAction action, int kilobitsPerSecond, CancellationToken ct)
    {
        if (action == UploadLimitAction.Apply && !UploadLimitPolicy.IsValidRate(kilobitsPerSecond))
            return Task.FromResult(new UploadLimitResult(action, UploadLimitStatus.InvalidRate, kilobitsPerSecond, 0, Current));
        lock (_gate)
        {
            if (_closed) return Task.FromResult(new UploadLimitResult(action, UploadLimitStatus.Closed, kilobitsPerSecond, 0, _current));
            if (_operation is { IsCompleted: false }) return Task.FromResult(new UploadLimitResult(action, UploadLimitStatus.Busy, kilobitsPerSecond, 0, _current));
            var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _closing.Token);
            _stop = stop;
            var operation = _latest = new OperationState(action, kilobitsPerSecond, _current, _unreadable, ++_revision);
            // ExecuteAsync reaches no shared state before its first await, so starting it under the gate is safe.
            return _operation = ExecuteAsync(operation, stop);
        }
    }

    private async Task<UploadLimitResult> ExecuteAsync(OperationState operation, CancellationTokenSource stop)
    {
        var action = operation.Action; int kilobitsPerSecond = operation.Rate;
        string script = action == UploadLimitAction.Apply ? UploadLimitPolicy.ApplyScript(kilobitsPerSecond) : UploadLimitPolicy.RemoveScript();
        ElevatedRunOutcome outcome;
        try { outcome = await _runner.RunAsync(script, HelperTimeout, stop.Token, outcome => ReconcileNoStart(operation, outcome)).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Finish(operation, UploadLimitStatus.Cancelled, 0); }
        finally
        {
            lock (_gate) if (ReferenceEquals(_stop, stop)) _stop = null;
            stop.Dispose();
        }
        return outcome.Kind switch
        {
            ElevatedRunKind.Declined => Finish(operation, UploadLimitStatus.Declined, 0),
            ElevatedRunKind.NotStarted => Finish(operation, UploadLimitStatus.Unavailable, 0),
            ElevatedRunKind.TimedOut => Finish(operation, UploadLimitStatus.TimedOut, 0),
            _ => Finish(operation, UploadLimitPolicy.StatusForExit(outcome.ExitCode), outcome.ExitCode)
        };
    }

    /// <summary>
    /// A confirmed Apply records the rate; a confirmed Remove clears the record. A run that failed, was not confirmed, timed out
    /// or was stopped may have changed the store partly or may still finish, so the record becomes uncertain and keeps the rate
    /// asked for (a Remove keeps the last known rate). A declined prompt or a missing PowerShell ran nothing and changes nothing.
    /// </summary>
    private UploadLimitResult Finish(OperationState operation, UploadLimitStatus status, int exitCode)
    {
        var action = operation.Action; int kilobitsPerSecond = operation.Rate;
        bool saved = true;
        UploadLimitRecord? record;
        lock (_gate)
        {
            if (status == UploadLimitStatus.Cancelled)
            {
                if (operation.NoStart is { } known)
                    status = known == ElevatedRunKind.Declined ? UploadLimitStatus.Declined : UploadLimitStatus.Unavailable;
                else operation.Cancelled = true;
            }
            switch (status)
            {
                case UploadLimitStatus.Confirmed:
                    record = action == UploadLimitAction.Apply ? new(action, kilobitsPerSecond, _clock.GetUtcNow(), true) : null;
                    saved = Save(record);
                    break;
                case UploadLimitStatus.Refused or UploadLimitStatus.NotConfirmed or UploadLimitStatus.TimedOut or UploadLimitStatus.Cancelled:
                    record = new(action, action == UploadLimitAction.Apply ? kilobitsPerSecond : _current?.KilobitsPerSecond ?? 0, _clock.GetUtcNow(), false);
                    saved = Save(record);
                    break;
                default:
                    record = _current;
                    break;
            }
        }
        return new(action, status, kilobitsPerSecond, exitCode, record, saved);
    }

    private void ReconcileNoStart(OperationState operation, ElevatedRunOutcome outcome)
    {
        if (outcome.Kind is not (ElevatedRunKind.Declined or ElevatedRunKind.NotStarted)) return;
        UploadLimitResult? result = null;
        lock (_gate)
        {
            if (!ReferenceEquals(_latest, operation) || operation.NoStart is not null) return;
            operation.NoStart = outcome.Kind;
            if (!operation.Cancelled) return; // Finish will see the answer if cancellation has not yet published.
            bool saved;
            if (operation.PreviousUnreadable)
            {
                // There was no trustworthy prior record to restore. Keep the persisted uncertainty, never claim absence.
                _unreadable = true; saved = false;
            }
            else saved = Save(operation.Previous);
            if (!_closed) result = new(operation.Action,
                outcome.Kind == ElevatedRunKind.Declined ? UploadLimitStatus.Declined : UploadLimitStatus.Unavailable,
                operation.Rate, 0, _current, saved);
        }
        if (result is not null) Reconciled?.Invoke(new(operation.Revision, result));
    }

    // Under the gate. This run keeps what it knows even when the file cannot be written; the result says the record is missing.
    private bool Save(UploadLimitRecord? record)
    {
        _current = record;
        try
        {
            if (record is null) _file.Delete(); else _file.Save(record, BootUtc());
            _unreadable = false;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    public void CancelOperations()
    {
        CancellationTokenSource? stop;
        lock (_gate) stop = _stop;
        try { stop?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        Task<UploadLimitResult>? operation;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true; operation = _operation;
        }
        _closing.Cancel();
        if (operation is not null) try { await operation.ConfigureAwait(false); } catch (OperationCanceledException) { }
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await _runner.ReleaseAsync(bound.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
        _closing.Dispose();
    }
}

/// <summary>
/// The record in one small JSON file: schema, the last action, the rate, when, whether Windows confirmed it, and when Windows
/// had started. A file from an earlier Windows session describes a policy that is gone, so it is dropped. A damaged file is
/// reported, never guessed at, and the next change replaces it. Written through a temporary file.
/// </summary>
internal sealed class UploadLimitRecordFile(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public (UploadLimitRecord? Record, bool Unreadable) Load(DateTimeOffset bootUtc)
    {
        if (!File.Exists(path)) return (null, false);
        FileShape? shape;
        try { shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllBytes(path), Options); }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { return (null, true); }
        if (shape is not { Schema: 1, KilobitsPerSecond: >= 0, AtUtc: { } at, BootUtc: { } boot }
            || !Enum.TryParse(shape.LastAction, false, out UploadLimitAction action) || !Enum.IsDefined(action))
            return (null, true);
        if (!UploadLimitPolicy.SameWindowsSession(boot, bootUtc))
        {
            try { Delete(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            return (null, false);
        }
        return (new(action, shape.KilobitsPerSecond, at, shape.Confirmed), false);
    }

    public void Save(UploadLimitRecord record, DateTimeOffset bootUtc)
    {
        var shape = new FileShape
        {
            LastAction = record.LastAction.ToString(), KilobitsPerSecond = record.KilobitsPerSecond,
            AtUtc = record.AtUtc, BootUtc = bootUtc, Confirmed = record.Confirmed
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(shape, Options));
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }

    public void Delete() { if (File.Exists(path)) File.Delete(path); }

    private sealed class FileShape
    {
        [JsonPropertyName("schema")] public int Schema { get; set; } = 1;
        [JsonPropertyName("lastAction")] public string? LastAction { get; set; }
        [JsonPropertyName("kilobitsPerSecond")] public int KilobitsPerSecond { get; set; }
        [JsonPropertyName("atUtc")] public DateTimeOffset? AtUtc { get; set; }
        [JsonPropertyName("bootUtc")] public DateTimeOffset? BootUtc { get; set; }
        [JsonPropertyName("confirmed")] public bool Confirmed { get; set; }
    }
}
