using System.Text.Json;

namespace Zenith.Sessions.Windows;

public sealed record ReleaseUpdateSettings(string? FeedUri = null, string? PublisherCertificateSha256 = null, bool CheckDaily = false);
public sealed record ReleaseUpdateState(ReleaseUpdateSettings Settings, DateTimeOffset? LastAttemptUtc = null,
    StagedUpdate? Pending = null, bool InstallOnNextStart = false);
public interface IReleaseUpdateBackend
{
    Task<StagedUpdate> StageAsync(ReleaseUpdateSettings settings, CancellationToken cancellationToken);
}

/// <summary>One owned optional daily loop, one serialized writer/check, no network until explicitly configured.</summary>
public sealed class ReleaseUpdateSession : IAsyncDisposable
{
    private readonly string _path;
    private readonly IReleaseUpdateBackend _backend;
    private readonly TimeProvider _time;
    private readonly Func<CancellationToken, Task> _changed;
    private readonly Func<bool> _canCheck;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _scheduleStop, _checkStop;
    private Task _schedule = Task.CompletedTask, _check = Task.CompletedTask;
    private volatile bool _paused, _disposed;
    private bool _initialized;
    private long _stopGeneration;
    public ReleaseUpdateState State { get; private set; } = new(new());
    public string Status { get; private set; } = "Updates are off until a release feed and publisher are configured.";
    public bool IsChecking { get; private set; }
    public ReleaseUpdateSession(string root, IReleaseUpdateBackend backend, Func<CancellationToken, Task>? changed = null, TimeProvider? time = null, Func<bool>? canCheck = null)
    { _path = Path.Combine(root, "release-updates.json"); _backend = backend; _time = time ?? TimeProvider.System; _changed = changed ?? (_ => Task.CompletedTask); _canCheck = canCheck ?? (() => true); }
    public async Task InitializeAsync(CancellationToken ct)
    {
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized) return;
            State = ReadState(_path); Validate(State.Settings);
            _initialized = true;
            Status = State.Pending is {} pending ? $"Version {pending.Version} is staged; it will be reverified before installation."
                : Configured(State.Settings) ? "The configured update feed is ready for an explicit check." : "Updates are off until a release feed and publisher are configured.";
        }
        finally { _serial.Release(); }
        StartSchedule();
    }
    public async Task ConfigureAsync(ReleaseUpdateSettings settings, CancellationToken ct)
    {
        Validate(settings); long requestedAt = StopAndCaptureGeneration();
        await _check.ConfigureAwait(false); await StopScheduleAsync().ConfigureAwait(false);
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var next = new ReleaseUpdateState(settings); Save(next); State = next;
            // A later Panic/Lock wins over this save even when an old helper delays configuration.
            lock (_gate) if (_stopGeneration == requestedAt && _canCheck()) _paused = false;
            Status = Configured(settings) ? "Update settings saved. Installation always requires approval." : "Updates are off.";
        }
        finally { _serial.Release(); }
        await _changed(ct).ConfigureAwait(false); StartSchedule();
    }
    public Task CheckNowAsync(CancellationToken ct) => StartCheck(ct, resume: true);
    private Task StartCheck(CancellationToken ct, bool resume)
    {
        lock (_gate)
        {
            if (!resume && (_disposed || _paused)) return Task.CompletedTask;
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_canCheck()) return Task.CompletedTask;
            if (!_check.IsCompleted) return _check;
            if (resume) _paused = false;
            return _check = CheckAsync(ct);
        }
    }
    private async Task CheckAsync(CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        lock (_gate) _checkStop = stop;
        bool acquired = false;
        try
        {
            await _serial.WaitAsync(stop.Token).ConfigureAwait(false); acquired = true;
            if (!Configured(State.Settings)) { Status = "Updates are off until a release feed and publisher are configured."; return; }
            IsChecking = true; Status = "Checking the configured signed release feed…";
            var attempt = State with { LastAttemptUtc = _time.GetUtcNow() }; Save(attempt); State = attempt;
            await _changed(stop.Token).ConfigureAwait(false);
            stop.Token.ThrowIfCancellationRequested();
            if (!_canCheck()) { Status = "Update checks are paused while the dashboard is unavailable or locked."; return; }
            var staged = await _backend.StageAsync(State.Settings, stop.Token).ConfigureAwait(false);
            stop.Token.ThrowIfCancellationRequested();
            var ready = State with { Pending = staged, InstallOnNextStart = false }; Save(ready); State = ready;
            Status = $"Version {staged.Version} verified and staged. Approve installation after the queue is idle.";
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Status = "Update check stopped. Installed files were unchanged."; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Status = $"Update check did not complete ({error.GetType().Name}). Installed files were unchanged."; }
        finally
        {
            IsChecking = false; lock (_gate) _checkStop = null;
            if (acquired) _serial.Release();
            if (!_stop.IsCancellationRequested) await _changed(_stop.Token).ConfigureAwait(false);
        }
    }
    public async Task ApproveNextStartAsync(bool queueIdle, CancellationToken ct)
    {
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!queueIdle || State.Pending is null || !Configured(State.Settings)) throw new InvalidOperationException("A verified staged update and an idle queue are required.");
            var approved = State with { InstallOnNextStart = true }; Save(approved); State = approved;
            Status = "Update approved for the next start. Zenith will ask before opening the attended installer.";
        }
        finally { _serial.Release(); }
        await _changed(ct).ConfigureAwait(false);
    }
    public async Task CancelApprovalAsync(CancellationToken ct)
    {
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(_disposed, this); var next = State with { InstallOnNextStart = false }; Save(next); State = next; }
        finally { _serial.Release(); }
        await _changed(ct).ConfigureAwait(false);
    }
    public async Task DiscardPendingAsync(CancellationToken ct)
    {
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State.Pending is {} pending && Directory.Exists(pending.Directory))
            {
                string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_path)!, "Updates"));
                string stage = Path.GetFullPath(pending.Directory), name = Path.GetFileName(stage);
                if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(stage), root) || !name.StartsWith("update-", StringComparison.Ordinal)
                    || !Guid.TryParseExact(name[7..], "N", out _) || !File.Exists(Path.Combine(stage, "signed-update.json"))) throw new IOException("The pending folder is not an owned update stage.");
                SignedUpdateStager.AssertNoRedirects(stage);
                if (Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Any(file => Path.GetExtension(file).ToLowerInvariant() is ".db" or ".dpapi" or ".zmacro" or ".zlicense" or ".pem"))
                    throw new IOException("Potential user data was found in the stage; preserve it for manual recovery.");
                Directory.Delete(stage, true);
            }
            var next = State with { Pending = null, InstallOnNextStart = false }; Save(next); State = next;
            Status = "The pending download was discarded. Installed files and account data were unchanged.";
        }
        finally { _serial.Release(); }
        await _changed(ct).ConfigureAwait(false);
    }
    public void CancelWork() => StopAndCaptureGeneration();
    private long StopAndCaptureGeneration()
    { lock (_gate) { _paused = true; _checkStop?.Cancel(); return ++_stopGeneration; } }
    private void StartSchedule()
    {
        if (_disposed || !Configured(State.Settings) || !State.Settings.CheckDaily) return;
        _scheduleStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _schedule = ScheduleAsync(_scheduleStop.Token);
    }
    private async Task ScheduleAsync(CancellationToken ct)
    {
        try
        {
            var next = (State.LastAttemptUtc ?? _time.GetUtcNow().AddDays(-1)).AddDays(1);
            var delay = next - _time.GetUtcNow();
            if (delay > TimeSpan.FromDays(1)) delay = TimeSpan.FromDays(1);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                if (State.Pending is null) await StartCheck(ct, resume: false).ConfigureAwait(false);
                // Even a failed persistence/check attempt waits a day; errors cannot become a retry spin.
                await Task.Delay(TimeSpan.FromDays(1), _time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    private async Task StopScheduleAsync()
    {
        _scheduleStop?.Cancel(); await _schedule.ConfigureAwait(false); _scheduleStop?.Dispose(); _scheduleStop = null;
    }
    private void Save(ReleaseUpdateState state)
    {
        SignedUpdateStager.AssertNoRedirects(_path, inspectChildren: false);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(state)); File.Move(temporary, _path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static ReleaseUpdateState ReadState(string path)
    {
        SignedUpdateStager.AssertNoRedirects(path, inspectChildren: false);
        if (!File.Exists(path)) return new(new());
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("Update settings exceed their bound.");
        var state = JsonSerializer.Deserialize<ReleaseUpdateState>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Invalid update settings.");
        Validate(state.Settings); return state;
    }
    public static bool Configured(ReleaseUpdateSettings settings) => !string.IsNullOrWhiteSpace(settings.FeedUri);
    public static void Validate(ReleaseUpdateSettings settings)
    {
        if (!Configured(settings)) { if (settings.CheckDaily) throw new ArgumentException("A feed is required for daily checks."); return; }
        if (settings.FeedUri!.Length > 2048 || !Uri.TryCreate(settings.FeedUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0 || !SignedUpdateVerifier.IsHash(settings.PublisherCertificateSha256))
            throw new ArgumentException("Configure a public HTTPS feed and the separately approved publisher certificate SHA-256.");
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true; CancelWork(); _stop.Cancel();
        await StopScheduleAsync().ConfigureAwait(false); await _check.ConfigureAwait(false);
        _stop.Dispose(); _serial.Dispose();
    }
}
