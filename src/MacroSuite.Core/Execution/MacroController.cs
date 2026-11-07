using System.Diagnostics;
using System.Runtime.ExceptionServices;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Input;

namespace MacroSuite.Core.Execution;

/// <summary>
/// Triple-tier macro execution controller implementing Pause, Stop, and Panic (Rule 11).
/// Guarantees that panic termination unblocks pause gates and releases held keys unconditionally.
/// </summary>
public sealed class MacroController : IMacroControl, MacroSuite.Core.Input.IMacroControlNotifications
{
    [ThreadStatic] private static MacroController? s_publishingState;
    public event EventHandler? StateChanged;
    private readonly IInputPlayer? _inputPlayer;
    private readonly HostInputOperationLease? _inputOperation;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly CancellationTokenSource _panicCts = new();
    private readonly object _stateLock = new();

    private TaskCompletionSource _pauseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _isPaused;
    private volatile bool _isPanic;
    private bool _disposed;
    private long _pausedTicks;
    private long _pauseStartedTick;
    private int _signalPanicStarted;
    private readonly TaskCompletionSource _signalPanicCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _signalPanicWorker = Task.CompletedTask;
    private Task _signalPanicCancellation = Task.CompletedTask;
    private Task _signalStopCancellation = Task.CompletedTask;
    private int _normalStopStarted;
    private readonly TaskCompletionSource _normalStopCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _normalStopWorker = Task.CompletedTask;
    private Task _normalStopCancellation = Task.CompletedTask;

    /// <inheritdoc/>
    public bool IsPaused => _isPaused;

    /// <inheritdoc/>
    public TimeSpan TotalPausedDuration
    {
        get
        {
            lock (_stateLock)
            {
                long ticks = _pausedTicks + (_isPaused ? Stopwatch.GetTimestamp() - _pauseStartedTick : 0);
                return TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
            }
        }
    }

    /// <inheritdoc/>
    public bool IsPanicTriggered => _isPanic;

    /// <summary>Stop cancellation. Callbacks must only signal; never synchronously dispose or wait for this controller.</summary>
    public CancellationToken StopToken => _stopCts.Token;

    /// <summary>Panic cancellation. Callbacks must only signal; never synchronously dispose or wait for this controller.</summary>
    public CancellationToken PanicToken => _panicCts.Token;

    /// <summary>
    /// Constructs a controller with an optional input player for panic key release.
    /// </summary>
    public MacroController(IInputPlayer? inputPlayer = null) : this(inputPlayer, null) { }

    public MacroController(IInputPlayer? inputPlayer, HostInputOperationLease? inputOperation)
    {
        _inputPlayer = inputPlayer;
        _inputOperation = inputOperation;
        // Initially unpaused: gate is open
        _pauseGate.TrySetResult();
    }

    /// <inheritdoc/>
    public Task PauseAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isPanic || _stopCts.IsCancellationRequested) return Task.CompletedTask;

        lock (_stateLock)
        {
            if (!_isPaused)
            {
                _pauseStartedTick = Stopwatch.GetTimestamp();
                _isPaused = true;
                _pauseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        PublishStateChanged();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ResumeAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateLock)
        {
            if (_isPaused)
            {
                FinishPause();
                _pauseGate.TrySetResult();
            }
        }

        PublishStateChanged();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task WaitIfPausedAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_isPaused || _isPanic)
        {
            return;
        }

        Task gateTask;
        lock (_stateLock)
        {
            gateTask = _pauseGate.Task;
        }

        try
        {
            // Link with cancellation tokens
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopCts.Token, _panicCts.Token);
            await gateTask.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Clean unblock when stop or panic is requested
        }
    }

    /// <inheritdoc/>
    public Task StopAsync()
    {
        if (_disposed) return Task.CompletedTask;
        _inputOperation?.SignalStop();

        lock (_stateLock)
        {
            FinishPause();
            if (!_stopCts.IsCancellationRequested)
            {
                _stopCts.Cancel();
            }

            // Unblock pause gate if paused so executor can exit cleanly
            _pauseGate.TrySetResult();
        }

        PublishStateChanged();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Panic()
    {
        if (_disposed) return;
        _inputOperation?.SignalStop();

        lock (_stateLock)
        {
            if (_disposed) return;
            _isPanic = true;
            FinishPause();

            // 1. Immediately signal panic token (Rule 11)
            try
            {
                if (!_panicCts.IsCancellationRequested)
                {
                    _panicCts.Cancel();
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Failed to signal panic cancellation: {ex}");
            }

            // 2. Also signal normal stop token
            try
            {
                if (!_stopCts.IsCancellationRequested)
                {
                    _stopCts.Cancel();
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Failed to signal stop cancellation during panic: {ex}");
            }

            // 3. Immediately unblock pause gate
            _pauseGate.TrySetResult();

            // 4. Synthesize release events for all held keys and mouse buttons (Rule 11)
            try
            {
                if (_inputOperation is null) _inputPlayer?.ReleaseAllHeldKeys();
                else if (_inputOperation.OwnsCleanup)
                {
                    if (_inputPlayer is IHostInputOperationAwarePlayer owned) owned.ReleaseHostOperationInputs(_inputOperation);
                    else _inputPlayer?.ReleaseAllHeldKeys();
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Failed to release held input during panic: {ex}");
            }
        }
        PublishStateChanged();
    }

    /// <summary>Hook-safe normal Stop. Revocation is immediate; notifications run on one tracked worker.</summary>
    public void SignalStop()
    {
        if (_disposed || Interlocked.CompareExchange(ref _normalStopStarted, 1, 0) != 0) return;
        _inputOperation?.SignalStop();
        _pauseGate.TrySetResult();
        try { _normalStopCancellation = _stopCts.CancelAsync(); }
        catch (ObjectDisposedException) { }
        _normalStopWorker = Task.Run(async () =>
        {
            try
            {
                await _normalStopCancellation.ConfigureAwait(false);
                await StopAsync().ConfigureAwait(false);
                _normalStopCompleted.TrySetResult();
            }
            catch (Exception error) { _normalStopCompleted.TrySetException(error); }
        });
    }

    public Task WaitForStopSignalAsync(CancellationToken ct = default)
        => Volatile.Read(ref _normalStopStarted) == 0 ? Task.CompletedTask : _normalStopCompleted.Task.WaitAsync(ct);

    /// <summary>Hook-safe panic signal. Native release and callbacks run on one tracked worker, never in the hook.</summary>
    public void SignalPanic()
    {
        if (_disposed || Interlocked.CompareExchange(ref _signalPanicStarted, 1, 0) != 0) return;
        _inputOperation?.SignalStop();
        _isPanic = true;
        _pauseGate.TrySetResult();
        try
        {
            _signalPanicCancellation = _panicCts.CancelAsync();
            _signalStopCancellation = _stopCts.CancelAsync();
        }
        catch (ObjectDisposedException) { }
        _signalPanicWorker = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(_signalPanicCancellation, _signalStopCancellation).ConfigureAwait(false);
                Panic();
                _signalPanicCompleted.TrySetResult();
            }
            catch (Exception ex) { _signalPanicCompleted.TrySetException(ex); }
        });
    }
    public Task WaitForPanicReleaseAsync(CancellationToken ct = default)
        => Volatile.Read(ref _signalPanicStarted) == 0 ? Task.CompletedTask : _signalPanicCompleted.Task.WaitAsync(ct);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        if (ReferenceEquals(s_publishingState, this)
            && (Volatile.Read(ref _normalStopStarted) != 0 && !_normalStopCompleted.Task.IsCompleted
                || Volatile.Read(ref _signalPanicStarted) != 0 && !_signalPanicCompleted.Task.IsCompleted))
            throw new InvalidOperationException("Complete controller disposal after the state-change handler returns.");
        List<Exception>? failures = null;
        try { if (Volatile.Read(ref _normalStopStarted) != 0) _normalStopCompleted.Task.GetAwaiter().GetResult(); }
        catch (Exception error) { (failures ??= new()).Add(error); }
        try { if (Volatile.Read(ref _signalPanicStarted) != 0) _signalPanicCompleted.Task.GetAwaiter().GetResult(); }
        catch (Exception error) { (failures ??= new()).Add(error); }

        lock (_stateLock)
        {
            if (_disposed) return;
            FinishPause();
            _disposed = true;

            _pauseGate.TrySetResult();

            try
            {
                _stopCts.Cancel();
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Failed to cancel stop token during disposal: {ex}");
            }

            try
            {
                _panicCts.Cancel();
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Failed to cancel panic token during disposal: {ex}");
            }

            _stopCts.Dispose();
            _panicCts.Dispose();
        }
        if (failures?.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null) throw new AggregateException("Controller signal completion failed; controller resources were disposed.", failures);
    }

    private void PublishStateChanged()
    {
        var previous = s_publishingState;
        s_publishingState = this;
        try { StateChanged?.Invoke(this, EventArgs.Empty); }
        finally { s_publishingState = previous; }
    }

    // Called only under _stateLock; repeated Resume/Stop/Panic cannot count a pause twice.
    private void FinishPause()
    {
        if (!_isPaused) return;
        _pausedTicks += Stopwatch.GetTimestamp() - _pauseStartedTick;
        _isPaused = false;
    }
}
