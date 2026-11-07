using System.Diagnostics;
using System.Drawing;
using System.Runtime.ExceptionServices;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Input;

/// <summary>Optional first-party capability; the public IInputPlayer/SDK contract remains unchanged.</summary>
public interface IPlaybackSessionAwarePlayer { PlaybackSessionContext? Session { get; set; } }

/// <summary>The existing native executor implements this sink; it is never a second injection pipeline.</summary>
public interface IPlaybackSessionSink
{
    ValueTask EmitSessionInputAsync(InputEvent input, bool cleanup, CancellationToken ct);
}
/// <summary>Allows an executor to distinguish emitted holds from physical user input it deliberately borrowed.</summary>
public interface IPlaybackHoldOwnership { bool OwnsHeldInput(InputEvent input); }

public interface IMacroControlNotifications { event EventHandler? StateChanged; }
public sealed class TargetWindowUnavailableException(string message) : InvalidOperationException(message);

/// <summary>One execution's active clock, pinned target and intended/physical hold ownership.</summary>
public sealed class PlaybackSessionContext : IAsyncDisposable
{
    private readonly SemaphoreSlim _emission = new(1, 1);
    private readonly object _transitionLock = new();
    private readonly Dictionary<(InputEventType, int), InputEvent> _desired = new();
    private readonly Dictionary<(InputEventType, int), InputEvent> _physical = new();
    private readonly CancellationTokenSource _lifetime;
    private IPlaybackSessionSink? _sink;
    private Task _transition = Task.CompletedTask;
    private Task _targetPause = Task.CompletedTask;
    private Task _targetPauseWorker = Task.CompletedTask;
    private Task _targetPauseControl = Task.CompletedTask;
    private Task? _disposeTask;
    private PlaybackOptions _inputOptions = PlaybackOptions.Default;
    private HostInputOperationLease? _hostOperation;
    private CancellationTokenRegistration _hostStopRegistration;
    private Task _hostStopCancellation = Task.CompletedTask;
    private volatile bool _disposed;
    public IMacroControl Control { get; }
    public ITargetWindowService? Targets { get; }
    public ResolvedMacroTarget? Target { get; }
    public string? PauseReason { get; private set; }
    public event EventHandler? TargetPaused;
    /// <summary>Current batch's immutable scalar options; session identity is supplied separately to bound sinks.</summary>
    public PlaybackOptions InputOptions => Volatile.Read(ref _inputOptions);
    public HostInputOperationLease? HostOperation => Volatile.Read(ref _hostOperation);
    public long ActiveTimestamp => Stopwatch.GetTimestamp() - (long)(Control.TotalPausedDuration.TotalSeconds * Stopwatch.Frequency);

    // Linked-token callbacks may run after a paused waiter is awakened by Stop.
    // Check the owning controller directly so callback ordering cannot authorize stale input.
    private bool StopRequested => _disposed || _lifetime.IsCancellationRequested || Control.StopToken.IsCancellationRequested
        || Control.PanicToken.IsCancellationRequested || Control.IsPanicTriggered
        || HostOperation is { CanContinue: false };
    private void ThrowIfStopped(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_disposed) throw new OperationCanceledException("The playback session is closing.");
        if (HostOperation is { CanContinue: false } operation)
            throw new OperationCanceledException("The owning input operation was stopped.", operation.StopToken);
        Control.StopToken.ThrowIfCancellationRequested();
        Control.PanicToken.ThrowIfCancellationRequested();
        if (Control.IsPanicTriggered) throw new OperationCanceledException("Playback was stopped by Panic.");
        _lifetime.Token.ThrowIfCancellationRequested();
    }

    public PlaybackSessionContext(IMacroControl control, ITargetWindowService? targets = null,
        ResolvedMacroTarget? target = null, CancellationToken ct = default)
    {
        Control = control ?? throw new ArgumentNullException(nameof(control)); Targets = targets; Target = target;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, control.StopToken, control.PanicToken);
        if (control is IMacroControlNotifications notifications) notifications.StateChanged += ControlChanged;
        if (targets != null) targets.Changed += TargetChanged;
    }

    /// <summary>Bind once before execution. Stop callbacks only signal; disposal joins their actual cancellation.</summary>
    public void BindHostOperation(HostInputOperationLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_transitionLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_hostOperation is not null)
            {
                if (!ReferenceEquals(_hostOperation, lease)) throw new InvalidOperationException("A playback session has one host operation owner.");
                return;
            }
            if (!lease.CanContinue) throw new OperationCanceledException("The owning input operation is unavailable.", lease.StopToken);
            Volatile.Write(ref _hostOperation, lease);
            _hostStopRegistration = lease.StopToken.UnsafeRegister(static state =>
            {
                var session = (PlaybackSessionContext)state!;
                // The registered callback is signal-only: never wait for session/lease finalization here.
                lock (session._transitionLock) session._hostStopCancellation = session._lifetime.CancelAsync();
            }, this);
        }
    }

    public IDisposable Attach(IInputPlayer player)
    {
        if (player is not IPlaybackSessionAwarePlayer aware)
            throw new NotSupportedException("This input executor does not support controlled playback sessions.");
        var previous = aware.Session; aware.Session = this;
        return new Attachment(aware, previous, this);
    }
    private sealed class Attachment(IPlaybackSessionAwarePlayer player, PlaybackSessionContext? previous, PlaybackSessionContext owner) : IDisposable
    {
        public void Dispose() { if (ReferenceEquals(player.Session, owner)) player.Session = previous; }
    }

    public void BindSink(IPlaybackSessionSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_transitionLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sink != null && !ReferenceEquals(_sink, sink)) throw new InvalidOperationException("A playback session has one input executor.");
            _sink = sink;
        }
    }

    internal async Task ConfigureInputOptionsAsync(PlaybackOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (options.Session is not null && !ReferenceEquals(options.Session, this))
            throw new ArgumentException("Playback options belong to a different session.", nameof(options));
        if (Target is { Rule.Mode: not MacroTargetMode.Screen } && options.TargetWindowHandle != IntPtr.Zero
            && options.TargetWindowHandle != Target.Window?.Handle)
            throw new ArgumentException("Playback options do not match the pinned target window.", nameof(options));
        var snapshot = new PlaybackOptions
        {
            Session = null, SpeedMultiplier = options.SpeedMultiplier, RepeatCount = options.RepeatCount,
            TagSynthesizedEvents = options.TagSynthesizedEvents, SynthesizedEventTag = options.SynthesizedEventTag,
            InterpolateMouseMoves = options.InterpolateMouseMoves, MouseInterpolationStepMs = options.MouseInterpolationStepMs,
            UseWindowRelativeCoordinates = options.UseWindowRelativeCoordinates, TargetWindowHandle = options.TargetWindowHandle,
            RecordedClientWidth = options.RecordedClientWidth, RecordedClientHeight = options.RecordedClientHeight
        };
        ThrowIfStopped(ct);
        using var linked = LinkCaller(ct);
        await _emission.WaitAsync(linked?.Token ?? _lifetime.Token).ConfigureAwait(false);
        try { ThrowIfStopped(ct); Volatile.Write(ref _inputOptions, snapshot); }
        finally { _emission.Release(); }
    }

    /// <summary>Queries fresh pinned identity and controller state without pausing, notifying or acquiring the transition gate.</summary>
    public PlaybackInputAdmission InspectFinalInputAdmission(CancellationToken ct = default)
    {
        var admission = InspectControlAdmission(ct);
        if (admission != PlaybackInputAdmission.Allowed) return admission;
        bool allowed = TargetAllowsInput();
        admission = InspectControlAdmission(ct);
        return admission != PlaybackInputAdmission.Allowed ? admission
            : allowed ? PlaybackInputAdmission.Allowed : PlaybackInputAdmission.TargetUnavailable;
    }

    private PlaybackInputAdmission InspectControlAdmission(CancellationToken ct)
    {
        if (ct.IsCancellationRequested || StopRequested) return PlaybackInputAdmission.Stopped;
        if (Control.IsPaused) return PlaybackInputAdmission.Paused;
        if (!Volatile.Read(ref _targetPause).IsCompletedSuccessfully || !Volatile.Read(ref _targetPauseWorker).IsCompletedSuccessfully
            || !Volatile.Read(ref _targetPauseControl).IsCompletedSuccessfully)
            return PlaybackInputAdmission.TargetUnavailable;
        return PlaybackInputAdmission.Allowed;
    }

    private bool TargetAllowsInput()
    {
        try
        {
            if (Target is not null)
            {
                if (Target.Rule is null || !Enum.IsDefined(Target.Rule.Mode)) return false;
                if (Target.Rule.Mode != MacroTargetMode.Screen && (Targets is null || Target.Window is null)) return false;
                if (Targets is not null) return Targets.Inspect(Target).AllowsInput;
            }
            return true;
        }
        catch (Exception) { return false; }
    }

    private void RequireInputAdmission(CancellationToken ct)
        => RequireAdmission(InspectFinalInputAdmission(ct), ct);

    private void RequireAdmission(PlaybackInputAdmission admission, CancellationToken ct)
    {
        if (admission == PlaybackInputAdmission.Stopped)
        { ThrowIfStopped(ct); throw new OperationCanceledException("Playback was stopped before input submission."); }
        if (admission != PlaybackInputAdmission.Allowed) throw new PlaybackInputDeferredException(admission);
    }

    private CancellationTokenSource? LinkCaller(CancellationToken ct)
        => ct.CanBeCanceled && ct != _lifetime.Token ? CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token) : null;

    private ValueTask DispatchAsync(IPlaybackSessionSink sink, InputEvent input, bool cleanup, CancellationToken ct)
        => sink is IPlaybackSessionBoundSink bound ? bound.EmitSessionInputAsync(this, input, cleanup, ct)
            : sink.EmitSessionInputAsync(input, cleanup, ct);

    private void ControlChanged(object? sender, EventArgs e)
    {
        if (Control.IsPaused || Control.StopToken.IsCancellationRequested || Control.IsPanicTriggered)
            QueueRelease();
        else if (!Volatile.Read(ref _targetPause).IsCompletedSuccessfully
            || !Volatile.Read(ref _targetPauseWorker).IsCompletedSuccessfully
            || !Volatile.Read(ref _targetPauseControl).IsCompletedSuccessfully)
            _ = RequestTargetPause(DeferredReason(PlaybackInputAdmission.TargetUnavailable));
        else lock (_transitionLock) if (!_disposed) _transition = RestoreAfterAsync(_transition);
    }
    private void TargetChanged(object? sender, EventArgs e)
    {
        if (_disposed || Target?.Rule.Mode is null or MacroTargetMode.Screen || Control.IsPaused || _lifetime.IsCancellationRequested) return;
        // Observers already publish a cached snapshot. Never perform native queries in their callback.
        TargetWindowInfo? foreground = Targets!.Snapshot.Foreground;
        var pinned = Target.Window;
        if (pinned != null && foreground != null && SameIdentity(pinned, foreground) && !foreground.IsOwnWindow) return;
        RequestTargetPause("The selected application lost focus. Focus it, then resume manually.");
    }
    private Task RequestTargetPause(string reason)
    {
        lock (_transitionLock)
        {
            if (_disposed) return Task.CompletedTask;
            PauseReason = reason;
            if (!_targetPause.IsCompleted) return _targetPause;
            if (_targetPauseWorker.IsFaulted) return _targetPauseWorker;
            if (_targetPauseControl.IsFaulted) return _targetPauseControl;
            if (Control.IsPaused) return _targetPause;
            if (!_targetPauseControl.IsCompleted) return _targetPauseControl;
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _targetPause = barrier.Task;
            if (_targetPauseWorker.IsCompleted) _targetPauseWorker = RequestTargetPauseAsync(barrier, notify: true);
            else _targetPauseControl = RequestTargetPauseAsync(barrier, notify: false);
            return _targetPause;
        }
    }
    private async Task RequestTargetPauseAsync(TaskCompletionSource barrier, bool notify)
    {
        // Publish the tracked handle before controller or notification callbacks can reenter.
        await Task.Yield();
        try
        {
            if (StopRequested) { barrier.TrySetResult(); return; }
            await Control.PauseAsync(_lifetime.Token).ConfigureAwait(false);
            // Observers may await release. Reentrant resume is refused until this notification worker finishes.
            barrier.TrySetResult();
            if (notify && !StopRequested) TargetPaused?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (StopRequested) { barrier.TrySetResult(); }
        catch (ObjectDisposedException) when (StopRequested) { barrier.TrySetResult(); }
        catch (Exception error) { barrier.TrySetException(error); throw; }
    }
    public static bool SameIdentity(TargetWindowInfo a, TargetWindowInfo b)
        => a.Handle == b.Handle && a.ProcessId == b.ProcessId && a.ProcessInstance == b.ProcessInstance;

    private void QueueRelease()
    {
        lock (_transitionLock)
        {
            if (_disposed) return;
            _transition = ReleaseAfterAsync(_transition);
        }
    }
    private async Task ReleaseAfterAsync(Task previous)
    {
        // Never enter a sink synchronously while a notification is publishing this transition.
        await Task.Yield();
        List<Exception>? failures = null;
        try { await previous.ConfigureAwait(false); }
        catch (Exception error) { (failures ??= new()).Add(error); }
        await _emission.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_sink != null)
            {
                foreach (var pair in _physical.ToArray())
                {
                    try
                    {
                        await DispatchAsync(_sink, Release(pair.Value), cleanup: true, CancellationToken.None).ConfigureAwait(false);
                        _physical.Remove(pair.Key);
                    }
                    catch (Exception error) { (failures ??= new()).Add(error); }
                }
            }
            if (_lifetime.IsCancellationRequested) _desired.Clear();
        }
        finally { _emission.Release(); }
        ThrowFailures(failures);
    }

    private async Task RestoreAfterAsync(Task previous)
    {
        // Publish the tracked transition before callbacks can enqueue another transition.
        await Task.Yield();
        await previous.ConfigureAwait(false);
        PlaybackInputAdmission? deferred = null;
        bool acquired = false;
        try
        {
            await _emission.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            acquired = true;
            if (_sink == null || Control.IsPaused || StopRequested) return;
            RequireAdmission(InspectControlAdmission(_lifetime.Token), _lifetime.Token);
            foreach (var pair in _desired)
            {
                if (_physical.ContainsKey(pair.Key)) continue;
                RequireInputAdmission(_lifetime.Token);
                await DispatchAsync(_sink, pair.Value, cleanup: false, _lifetime.Token).ConfigureAwait(false);
                if (_sink is not IPlaybackHoldOwnership ownership || ownership.OwnsHeldInput(pair.Value)) _physical[pair.Key] = pair.Value;
            }
        }
        catch (PlaybackInputDeferredException error) { deferred = error.Admission; }
        catch (TargetWindowUnavailableException) { deferred = PlaybackInputAdmission.TargetUnavailable; }
        catch (OperationCanceledException) when (StopRequested) { }
        finally { if (acquired) _emission.Release(); }
        if (deferred is { } admission)
            _ = RequestTargetPause(DeferredReason(admission)); // Never await a release that can itself await this restore.
    }

    public async Task WaitUntilReadyAsync(CancellationToken ct = default)
    {
        while (true)
        {
            ThrowIfStopped(ct);
            Task targetPause; lock (_transitionLock) targetPause = _targetPause;
            if (!targetPause.IsCompletedSuccessfully)
                await targetPause.WaitAsync(ct).ConfigureAwait(false);
            ThrowIfStopped(ct);
            if (Control.IsPaused)
            {
                using var paused = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
                await Control.WaitIfPausedAsync(paused.Token).ConfigureAwait(false);
            }
            ThrowIfStopped(ct);
            Task transition; lock (_transitionLock) transition = _transition;
            if (!transition.IsCompletedSuccessfully)
            {
                using var releasing = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
                await transition.WaitAsync(releasing.Token).ConfigureAwait(false);
            }
            if (Target != null)
            {
                var snapshot = Targets?.Snapshot ?? TargetWindowSnapshot.Unavailable;
                bool cachedAllowed = snapshot.IsReady && Target.Rule is not null && (Target.Rule.Mode == MacroTargetMode.Screen
                    ? snapshot.Foreground?.IsOwnWindow != true
                    : Target.Window != null && snapshot.Foreground is { IsOwnWindow: false } foreground && SameIdentity(Target.Window, foreground));
                if (!cachedAllowed && InspectFinalInputAdmission(ct) == PlaybackInputAdmission.TargetUnavailable)
                {
                    await RequestTargetPause(DeferredReason(PlaybackInputAdmission.TargetUnavailable)).ConfigureAwait(false);
                    continue;
                }
            }
            ThrowIfStopped(ct);
            if (!Control.IsPaused) { PauseReason = null; return; }
        }
    }

    /// <summary>Offsets use active time. Pauses never turn delayed events into a catch-up burst.</summary>
    public async Task DelayUntilAsync(long activeOrigin, long offsetTicks, CancellationToken ct = default)
    {
        if (offsetTicks < 0 || offsetTicks > Stopwatch.Frequency * 86400L) throw new InvalidDataException("Recorded input contains an invalid timestamp.");
        while (true)
        {
            await WaitUntilReadyAsync(ct).ConfigureAwait(false);
            long remaining = offsetTicks - (ActiveTimestamp - activeOrigin);
            if (remaining <= 0) return;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(remaining / (double)Stopwatch.Frequency, .025)), ct).ConfigureAwait(false);
        }
    }
    public async Task DelayAsync(TimeSpan duration, CancellationToken ct = default)
    {
        while (duration > TimeSpan.Zero)
        {
            TimeSpan slice = duration > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : duration;
            await DelayUntilAsync(ActiveTimestamp, (long)(slice.TotalSeconds * Stopwatch.Frequency), ct).ConfigureAwait(false);
            duration -= slice;
        }
    }

    public async ValueTask EmitAsync(InputEvent input, CancellationToken ct = default)
    {
        ThrowIfStopped(ct);
        using var linked = LinkCaller(ct);
        ct = linked?.Token ?? _lifetime.Token;
        while (true)
        {
            await WaitUntilReadyAsync(ct).ConfigureAwait(false);
            await _emission.WaitAsync(ct).ConfigureAwait(false);
            PlaybackInputAdmission deferred;
            try
            {
                ThrowIfStopped(ct);
                RequireInputAdmission(ct);
                var sink = _sink ?? throw new InvalidOperationException("Attach the execution's input player before sending input.");
                if (Hold(input, out var pendingIdentity, out bool pendingDown) && pendingDown
                    && !_desired.ContainsKey(pendingIdentity) && _desired.Count >= 512)
                    throw new InvalidDataException("Playback exceeded the held-input limit.");
                // Restore ongoing holds once, rather than replaying previously executed actions.
                foreach (var pair in _desired)
                {
                    if (_physical.ContainsKey(pair.Key)) continue;
                    RequireInputAdmission(ct);
                    await DispatchAsync(sink, pair.Value, cleanup: false, ct).ConfigureAwait(false);
                    if (sink is not IPlaybackHoldOwnership ownership || ownership.OwnsHeldInput(pair.Value)) _physical[pair.Key] = pair.Value;
                }
                RequireAdmission(InspectControlAdmission(ct), ct);
                await DispatchAsync(sink, input, cleanup: false, ct).ConfigureAwait(false);
                if (Hold(input, out var identity, out bool down))
                {
                    if (down && (sink is not IPlaybackHoldOwnership ownership || ownership.OwnsHeldInput(input)))
                    { _desired[identity] = input; _physical[identity] = input; }
                    else if (down) { _desired.Remove(identity); _physical.Remove(identity); }
                    else { _desired.Remove(identity); _physical.Remove(identity); }
                }
                return;
            }
            catch (PlaybackInputDeferredException error) { deferred = error.Admission; }
            catch (TargetWindowUnavailableException) { deferred = PlaybackInputAdmission.TargetUnavailable; }
            finally { _emission.Release(); }
            await RequestTargetPause(DeferredReason(deferred)).ConfigureAwait(false);
        }
    }

    public Rectangle GetTargetClientBounds()
    {
        if (Target?.Window == null || Targets == null) throw new NotSupportedException("Choose a window target to use target-window image search.");
        if (!TryGetTargetClientBounds(out Rectangle bounds)) throw new TargetWindowUnavailableException(PauseReason ?? "The selected target is unavailable.");
        return bounds;
    }
    public bool TryGetTargetClientBounds(out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (Target?.Window == null || Targets == null) return false;
        try
        {
            var result = Targets.Inspect(Target);
            if (result.IsValid) { bounds = result.ClientBounds; return true; }
        }
        catch (Exception) { }
        _ = RequestTargetPause(DeferredReason(PlaybackInputAdmission.TargetUnavailable));
        return false;
    }

    public Task AwaitReleasedAsync(CancellationToken ct = default)
        => AwaitStableReleaseAsync(ct);

    private async Task AwaitStableReleaseAsync(CancellationToken ct)
    {
        while (true)
        {
            Task pause; lock (_transitionLock) pause = _targetPause;
            await pause.WaitAsync(ct).ConfigureAwait(false);
            Task transition; lock (_transitionLock) transition = _transition;
            await transition.WaitAsync(ct).ConfigureAwait(false);
            Task? failedWorker;
            lock (_transitionLock)
            {
                if (!ReferenceEquals(pause, _targetPause) || !ReferenceEquals(transition, _transition)) continue;
                failedWorker = _targetPauseWorker.IsFaulted ? _targetPauseWorker : null;
            }
            if (failedWorker is not null) await failedWorker.ConfigureAwait(false);
            return;
        }
    }
    public async Task<bool> TryResumeAsync(bool inputIsNeutral, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!inputIsNeutral || StopRequested) return false;
        Task worker, control;
        lock (_transitionLock) { worker = _targetPauseWorker; control = _targetPauseControl; }
        if (!worker.IsCompleted || !control.IsCompleted)
        {
            // A notification may reenter here. Never wait on that notification's own worker.
            if (!Control.IsPaused) await RequestTargetPause(DeferredReason(PlaybackInputAdmission.TargetUnavailable)).ConfigureAwait(false);
            return false;
        }
        await AwaitReleasedAsync(ct).ConfigureAwait(false);
        if (!TargetAllowsInput())
        { PauseReason = "Focus the selected target and release the control keys before resuming."; return false; }
        ct.ThrowIfCancellationRequested();
        if (StopRequested) return false;
        await Control.ResumeAsync().ConfigureAwait(false);
        await AwaitReleasedAsync(ct).ConfigureAwait(false);
        var admission = InspectFinalInputAdmission(ct);
        if (admission == PlaybackInputAdmission.TargetUnavailable)
        {
            await RequestTargetPause(DeferredReason(admission)).ConfigureAwait(false);
            await AwaitReleasedAsync(ct).ConfigureAwait(false);
        }
        return admission == PlaybackInputAdmission.Allowed && !Control.IsPaused && !StopRequested;
    }
    private static bool Hold(InputEvent input, out (InputEventType, int) identity, out bool down)
    {
        down = input.Type is InputEventType.KeyDown or InputEventType.MouseDown or InputEventType.Unicode;
        var kind = input.Type switch { InputEventType.KeyUp => InputEventType.KeyDown, InputEventType.MouseUp => InputEventType.MouseDown,
            InputEventType.UnicodeUp => InputEventType.Unicode, _ => input.Type };
        identity = (kind, input.Data);
        return kind is InputEventType.KeyDown or InputEventType.MouseDown or InputEventType.Unicode;
    }
    private static InputEvent Release(InputEvent input) => new(input.Type switch
        { InputEventType.KeyDown => InputEventType.KeyUp, InputEventType.MouseDown => InputEventType.MouseUp, _ => InputEventType.UnicodeUp },
        Stopwatch.GetTimestamp(), input.X, input.Y, input.Data, input.ExtraInfo);

    private static string DeferredReason(PlaybackInputAdmission admission) => admission == PlaybackInputAdmission.Paused
        ? "Playback was paused before input submission. Resume manually to continue."
        : "The selected target is unavailable. Focus it, then resume manually.";

    private static void ThrowFailures(List<Exception>? failures)
    {
        if (failures is null) return;
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException("Playback transition or cleanup failed.", failures);
    }

    public ValueTask DisposeAsync()
    {
        lock (_transitionLock)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            return new(_disposeTask = DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        List<Exception>? failures = null;
        if (Control is IMacroControlNotifications notifications) notifications.StateChanged -= ControlChanged;
        if (Targets != null) Targets.Changed -= TargetChanged;
        try { _lifetime.Cancel(); }
        catch (Exception error) { (failures ??= new()).Add(error); }
        Task transition; lock (_transitionLock) transition = _transition;
        try
        {
            try
            {
                await _hostStopRegistration.DisposeAsync().ConfigureAwait(false);
                await _hostStopCancellation.ConfigureAwait(false);
            }
            catch (Exception error) { (failures ??= new()).Add(error); }
            try { await _targetPauseWorker.ConfigureAwait(false); }
            catch (Exception error) { (failures ??= new()).Add(error); }
            try { await _targetPauseControl.ConfigureAwait(false); }
            catch (Exception error) { (failures ??= new()).Add(error); }
            try { await ReleaseAfterAsync(transition).ConfigureAwait(false); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        finally { _desired.Clear(); _lifetime.Dispose(); _emission.Dispose(); }
        ThrowFailures(failures);
    }
}
