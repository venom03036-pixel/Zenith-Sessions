using System.Diagnostics;
using System.Drawing;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using Zenith.Sessions.Core;

namespace Zenith.MacroHost.Execution;
internal sealed class TargetedMacroInputFailure() : Exception("The controlled target input could not be submitted safely.");
internal sealed class TargetedMacroHoldCleanupFailure() : Exception("A targeted hold release requires explicit recovery.");
/// <summary>Real interpreter adapter; all input goes through the borrowed target lease.</summary>
internal sealed class TargetedMacroPlayer(TargetedInputRun input) : IInputPlayer, IClientRelativePlaybackTarget
{
    private readonly object _sync = new();
    private Task<TargetedInputRunSnapshot>? _clear;
    private bool _playing, _closed, _rejected;
    private bool _admissionOpen = true;
    private TaskCompletionSource _admission = OpenGate();
    private int _x, _y;
    internal PlaybackSessionContext? Session { get; set; }
    public IntPtr TargetWindowHandle => input.Target.Window;

    public bool IsPlaying
    {
        get
        {
            lock (_sync)
                return _playing;
        }
    }

    internal bool Rejected
    {
        get
        {
            lock (_sync)
                return _rejected;
        }
    }

    private static TaskCompletionSource OpenGate()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return gate;
    }

    internal void CloseAdmission()
    {
        lock (_sync)
        {
            if (!_admissionOpen)
                return;
            _admissionOpen = false;
            _admission = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    internal void OpenAdmission()
    {
        lock (_sync)
        {
            if (_closed)
                return;
            _admissionOpen = true;
            _admission.TrySetResult();
        }
    }

    public async Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default)
    {
        options ??= PlaybackOptions.Default;
        lock (_sync)
        {
            if (_closed || _playing)
                throw Failure();
            _playing = true;
        }

        try
        {
            if (events is null || events.Count is < 1 or > 16 || Session is null || !double.IsFinite(options.SpeedMultiplier) || options.SpeedMultiplier is < 0.1 or > 10 || options.RepeatCount != 1 || options.RecordedClientWidth is < 0 or > TargetedMacroPlan.MaximumClientDimension || options.RecordedClientHeight is < 0 or > TargetedMacroPlan.MaximumClientDimension || (options.RecordedClientWidth == 0) != (options.RecordedClientHeight == 0))
                throw Failure();
            var frozen = new InputEvent[events.Count];
            long first = events[0].TimestampTicks, previous = first;
            for (int index = 0; index < frozen.Length; index++)
            {
                var value = events[index];
                frozen[index] = value;
                if (value.TimestampTicks < previous || value.TimestampTicks - first > Stopwatch.Frequency * 60L)
                    throw Failure();
                previous = value.TimestampTicks;
                // Positional steps must have taken the explicit client-recipient seam.
                if (value.Type is InputEventType.MouseMove or InputEventType.MouseDown or InputEventType.MouseUp && options.TargetWindowHandle != TargetWindowHandle)
                    throw Failure();
                _ = Map(value, options);
            }

            await WaitReadyAsync(ct).ConfigureAwait(false);
            long origin = Session.ActiveTimestamp;
            for (int index = 0; index < frozen.Length; index++)
            {
                long offset = (long)((frozen[index].TimestampTicks - first) / options.SpeedMultiplier);
                await Session.DelayUntilAsync(origin, offset, ct).ConfigureAwait(false);
                while (true)
                {
                    await WaitReadyAsync(ct).ConfigureAwait(false);
                    var signal = Map(frozen[index], options);
                    Task<TargetedInputDispatchResult> pending;
                    lock (_sync)
                    {
                        if (!_admissionOpen || _clear is { IsCompleted: false })
                            continue;
                        pending = input.SubmitAsync([signal], ct);
                    }

                    var result = await pending.ConfigureAwait(false);
                    if (result.UncertainCount > 0)
                        throw Failure();
                    if (result.SubmittedCount == 1)
                    {
                        if (signal.Kind is TargetedInputKind.Move or TargetedInputKind.LeftDown or TargetedInputKind.LeftUp or TargetedInputKind.RightDown or TargetedInputKind.RightUp or TargetedInputKind.MiddleDown or TargetedInputKind.MiddleUp)
                        {
                            _x = signal.X;
                            _y = signal.Y;
                        }

                        break; // A queued event is never replayed merely because Pause arrived afterward.
                    }

                    if (result.SubmittedCount == 0 && result.Status is TargetedInputDispatchStatus.Inactive or TargetedInputDispatchStatus.Cancelled)
                    {
                        // The run's own stop (its runtime cap, Stop, Panic or its companion operation's stop) cancelled this
                        // send before anything went out, and the input owner stopped on the cancelled batch: that is the stop
                        // arriving, not the input being rejected.
                        ct.ThrowIfCancellationRequested();
                        Session.Control.StopToken.ThrowIfCancellationRequested();
                        Session.Control.PanicToken.ThrowIfCancellationRequested();
                        if (result.Run.State is TargetedInputRunState.Pausing or TargetedInputRunState.Paused or TargetedInputRunState.Resuming or TargetedInputRunState.Clearing)
                            continue; // Only the next unsent event waits at the owned transition.
                    }

                    throw Failure();
                }
            }
        }
        finally
        {
            lock (_sync)
                _playing = false;
        }
    }

    private async Task WaitReadyAsync(CancellationToken ct)
    {
        while (true)
        {
            Task admission;
            lock (_sync)
                admission = _admission.Task;
            await admission.WaitAsync(ct).ConfigureAwait(false);
            await DrainClearAsync().WaitAsync(ct).ConfigureAwait(false);
            await Session!.WaitUntilReadyAsync(ct).ConfigureAwait(false);
            TargetedInputRunState state;
            lock (_sync)
            {
                if (!_admissionOpen || !ReferenceEquals(admission, _admission.Task))
                    continue;
                state = input.Snapshot.State;
            }

            if (state == TargetedInputRunState.Running)
                return;
            if (state == TargetedInputRunState.Pausing)
            {
                await input.PauseAsync().WaitAsync(ct).ConfigureAwait(false);
                continue;
            }

            if (state == TargetedInputRunState.Resuming)
            {
                await input.ResumeAsync().WaitAsync(ct).ConfigureAwait(false);
                continue;
            }

            if (state == TargetedInputRunState.Clearing)
            {
                await DrainClearAsync().WaitAsync(ct).ConfigureAwait(false);
                if (input.Snapshot.State == state)
                    throw Failure();
                continue;
            }

            Session.Control.StopToken.ThrowIfCancellationRequested();
            Session.Control.PanicToken.ThrowIfCancellationRequested();
            throw Failure();
        }
    }

    private TargetedInputSignal Map(InputEvent value, PlaybackOptions options)
    {
        if (value.Type is InputEventType.KeyDown or InputEventType.KeyUp)
        {
            if (value.Data is < 1 or > 254)
                throw Failure();
            return new(value.Type == InputEventType.KeyDown ? TargetedInputKind.KeyDown : TargetedInputKind.KeyUp, Key: value.Data);
        }

        if (value.Type == InputEventType.MouseWheel)
        {
            if (value.Data is < -12000 or > 12000 || value.Data == 0)
                throw Failure();
            return new(TargetedInputKind.Wheel, X: _x, Y: _y, WheelDelta: value.Data);
        }

        int x = value.X, y = value.Y;
        if (options.RecordedClientWidth > 0)
        {
            x = (int)Math.Round(x * (input.Target.ContentBounds.Width / (double)options.RecordedClientWidth));
            y = (int)Math.Round(y * (input.Target.ContentBounds.Height / (double)options.RecordedClientHeight));
        }

        if (x < 0 || y < 0 || x >= input.Target.ContentBounds.Width || y >= input.Target.ContentBounds.Height)
            throw Failure();
        if (value.Type == InputEventType.MouseMove)
            return new(TargetedInputKind.Move, X: x, Y: y);
        if (value.Type is not (InputEventType.MouseDown or InputEventType.MouseUp))
            throw Failure();
        bool down = value.Type == InputEventType.MouseDown;
        var kind = (MouseButton)value.Data switch
        {
            MouseButton.Left => down ? TargetedInputKind.LeftDown : TargetedInputKind.LeftUp,
            MouseButton.Right => down ? TargetedInputKind.RightDown : TargetedInputKind.RightUp,
            MouseButton.Middle => down ? TargetedInputKind.MiddleDown : TargetedInputKind.MiddleUp,
            _ => throw Failure()};
        return new(kind, X: x, Y: y);
    }

    public void ReleaseAllHeldKeys()
    {
        lock (_sync)
        {
            if (_closed || _clear is { IsCompleted: false })
                return;
            try
            {
                _clear = input.ClearHoldsAsync();
            }
            catch (Exception)
            {
                _rejected = true;
                _clear = Task.FromException<TargetedInputRunSnapshot>(new TargetedMacroInputFailure());
            }
        }
    }

    internal async Task DrainClearAsync()
    {
        Task<TargetedInputRunSnapshot>? pending;
        lock (_sync)
            pending = _clear;
        if (pending is not null)
        {
            var state = await pending.ConfigureAwait(false);
            if (state.State == TargetedInputRunState.Faulted)
            {
                lock (_sync) _rejected = true;
                // Only a fault that still owns submitted holds needs explicit recovery. A fault whose
                // releases all went out (for example after the window moved) is an input rejection.
                if (TargetedMacroRun.HoldsNeedRecovery(state))
                    throw new TargetedMacroHoldCleanupFailure();
            }
        }
    }

    private TargetedMacroInputFailure Failure()
    {
        lock (_sync)
            _rejected = true;
        return new();
    }

    public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false) => throw Failure();
    public void SendRelativeMove(int dx, int dy) => throw Failure();
    public void Dispose()
    {
        lock (_sync)
            _closed = true;
    }
}

internal sealed class NoCapture : IScreenCaptureService
{
    public CaptureSource CurrentSource => CaptureSource.Gdi;
    public bool IsSupported => false;

    public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default) => throw new NotSupportedException("Visual actions are not part of this execution plan.");
    public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default) => throw new NotSupportedException("Visual actions are not part of this execution plan.");
    public void Dispose()
    {
    }
}
