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
    private bool _unreadable, _closed, _loaded;
    private sealed class OperationState(UploadLimitAction action, int rate, UploadLimitRecord? previous, bool unreadable, long revision)
    {
        public readonly UploadLimitAction Action = action;
        public readonly int Rate = rate;
        public UploadLimitRecord? Previous = previous;
        public bool PreviousUnreadable = unreadable;
        public readonly long Revision = revision;
        public ElevatedRunOutcome? Completion;
        public bool Abandoned, OwnershipRecorded;
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

    // The UTC estimate is display metadata only. Uptime is monotonic and unaffected by wall-clock correction.
    private (DateTimeOffset BootUtc, TimeSpan Uptime) WindowsSession()
    {
        var uptime = _uptime();
        return (_clock.GetUtcNow() - uptime, uptime);
    }

    public Task<UploadLimitRecord?> LoadRecordAsync(CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var loaded = _file.Load(WindowsSession());
            _current = loaded.Record; _unreadable = loaded.Unreadable; _loaded = true;
            return loaded.Record;
        }
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
            // A refused retry must not replace the exact owner whose eventual completion can resolve this hold.
            if (_current?.HelperUnresolved == true) return Task.FromResult(new UploadLimitResult(action, UploadLimitStatus.HelperUnresolved, kilobitsPerSecond, 0, _current));
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
        try
        {
            // Persist the uncertainty before ShellExecute can create an external writer, including an unanswered UAC prompt.
            var refused = await Task.Run(() => PrepareExternalOwner(operation, stop.Token), stop.Token).ConfigureAwait(false);
            if (refused is not null) return refused;
            outcome = await _runner.RunAsync(script, HelperTimeout, stop.Token, outcome => ReconcileCompletion(operation, outcome)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return Finish(operation, UploadLimitStatus.Cancelled, 0); }
        catch
        {
            // A wait/handle failure proves no exit. Keep the durable hold; a later observed exit may still reconcile it.
            lock (_gate)
            {
                operation.Abandoned = true;
                if (operation.Completion is not null) Finish(operation, UploadLimitStatus.Cancelled, 0);
            }
            throw;
        }
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

    private UploadLimitResult? PrepareExternalOwner(OperationState operation, CancellationToken ct)
    {
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (!_loaded)
            {
                var loaded = _file.Load(WindowsSession()); _current = loaded.Record; _unreadable = loaded.Unreadable; _loaded = true;
            }
            operation.Previous = _current; operation.PreviousUnreadable = _unreadable;
            if (_unreadable) return new(operation.Action, UploadLimitStatus.OwnershipUnavailable, operation.Rate, 0, _current, false);
            if (_current?.HelperUnresolved == true) return new(operation.Action, UploadLimitStatus.HelperUnresolved, operation.Rate, 0, _current);
            var pending = new UploadLimitRecord(operation.Action, operation.Action == UploadLimitAction.Apply ? operation.Rate : _current?.KilobitsPerSecond ?? 0,
                _clock.GetUtcNow(), false, HelperUnresolved: true);
            if (!Save(pending))
            {
                _current = operation.Previous;
                return new(operation.Action, UploadLimitStatus.OwnershipUnavailable, operation.Rate, 0, _current, false);
            }
            operation.OwnershipRecorded = true;
            return null;
        }
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
            if (!operation.OwnershipRecorded) return new(action, status, kilobitsPerSecond, exitCode, _current);
            if (status is UploadLimitStatus.Cancelled or UploadLimitStatus.TimedOut)
            {
                operation.Abandoned = true;
                if (operation.Completion is { } known)
                {
                    status = known.Kind == ElevatedRunKind.Exited ? UploadLimitPolicy.StatusForExit(known.ExitCode)
                        : known.Kind == ElevatedRunKind.Declined ? UploadLimitStatus.Declined : UploadLimitStatus.Unavailable;
                    exitCode = known.ExitCode;
                }
            }
            switch (status)
            {
                case UploadLimitStatus.Confirmed:
                    record = action == UploadLimitAction.Apply ? new(action, kilobitsPerSecond, _clock.GetUtcNow(), true) : null;
                    saved = Save(record);
                    break;
                case UploadLimitStatus.Refused or UploadLimitStatus.NotConfirmed or UploadLimitStatus.TimedOut or UploadLimitStatus.Cancelled:
                    record = new(action, action == UploadLimitAction.Apply ? kilobitsPerSecond : operation.Previous?.KilobitsPerSecond ?? 0,
                        _clock.GetUtcNow(), false, status is UploadLimitStatus.Cancelled or UploadLimitStatus.TimedOut);
                    saved = Save(record);
                    break;
                default:
                    record = operation.Previous;
                    saved = Save(record);
                    break;
            }
        }
        return new(action, status, kilobitsPerSecond, exitCode, record, saved);
    }

    private void ReconcileCompletion(OperationState operation, ElevatedRunOutcome outcome)
    {
        if (outcome.Kind is not (ElevatedRunKind.Declined or ElevatedRunKind.NotStarted or ElevatedRunKind.Exited)) return;
        UploadLimitResult? result = null;
        lock (_gate)
        {
            if (!ReferenceEquals(_latest, operation) || operation.Completion is not null) return;
            operation.Completion = outcome;
            if (!operation.Abandoned) return; // Finish consumes this answer if cancellation has not yet published.
            var completed = Finish(operation, UploadLimitStatus.Cancelled, outcome.ExitCode);
            if (!_closed) result = completed;
        }
        if (result is not null) Reconciled?.Invoke(new(operation.Revision, result));
    }

    // Under the gate. This run keeps what it knows even when the file cannot be written; the result says the record is missing.
    private bool Save(UploadLimitRecord? record)
    {
        _current = record;
        try
        {
            if (record is null) _file.Delete(); else _file.Save(record, WindowsSession());
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
/// had started, plus observed monotonic uptime. A proven uptime reset drops the old record. Ambiguous restarts retain it. A damaged file is
/// reported and blocks new changes because prior helper ownership cannot be established. Flushed through a temporary file.
/// </summary>
internal sealed class UploadLimitRecordFile(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public (UploadLimitRecord? Record, bool Unreadable) Load((DateTimeOffset BootUtc, TimeSpan Uptime) session)
    {
        if (!File.Exists(path)) return (null, Directory.Exists(path));
        FileShape? shape;
        try
        {
            if (new FileInfo(path).Length > 16384) return (null, true);
            shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllBytes(path), Options);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { return (null, true); }
        if (shape is not { Schema: 1, KilobitsPerSecond: >= 0, AtUtc: { } at, BootUtc: { } boot }
            || !Enum.TryParse(shape.LastAction, false, out UploadLimitAction action) || !Enum.IsDefined(action)
            || shape.UptimeTicks is < 0 || session.Uptime < TimeSpan.Zero)
            return (null, true);
        // Old unconfirmed records cannot prove that their former helper stopped.
        bool unresolved = shape.HelperUnresolved ?? !shape.Confirmed;
        if (unresolved && shape.Confirmed) return (null, true);
        // A UTC correction can change the apparent boot time while the old process is still alive.
        // Only a decrease in monotonic uptime proves that a reboot occurred after our observation.
        if (shape.UptimeTicks is { } observedUptime && session.Uptime.Ticks < observedUptime)
        {
            try { Delete(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            return (null, false);
        }

        var record = new UploadLimitRecord(action, shape.KilobitsPerSecond, at, shape.Confirmed, unresolved);
        if (shape.UptimeTicks is null)
        {
            // Upgrade observation metadata without releasing the hold. A later uptime reset can then prove restart.
            try { Save(record, session); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return (record, true); }
        }
        return (record, false);
    }

    public void Save(UploadLimitRecord record, (DateTimeOffset BootUtc, TimeSpan Uptime) session)
    {
        var shape = new FileShape
        {
            LastAction = record.LastAction.ToString(), KilobitsPerSecond = record.KilobitsPerSecond,
            AtUtc = record.AtUtc, BootUtc = session.BootUtc, UptimeTicks = session.Uptime.Ticks, Confirmed = record.Confirmed, HelperUnresolved = record.HelperUnresolved
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            output.Write(JsonSerializer.SerializeToUtf8Bytes(shape, Options));
            output.Flush(flushToDisk: true);
        }
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
        [JsonPropertyName("uptimeTicks")] public long? UptimeTicks { get; set; }
        [JsonPropertyName("confirmed")] public bool Confirmed { get; set; }
        [JsonPropertyName("helperUnresolved")] public bool? HelperUnresolved { get; set; }
    }
}
