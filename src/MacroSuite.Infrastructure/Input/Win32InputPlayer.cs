using System.Diagnostics;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.Infrastructure.Input;

/// <summary>
/// Replays a sequence of <see cref="InputEvent"/> records via Win32 <c>SendInput</c>, using a
/// Stopwatch spin-wait hybrid for sub-millisecond timing precision.
/// </summary>
/// <remarks>
/// <para>
/// <b>Timing strategy:</b>
/// <list type="bullet">
///   <item><description>Gap &gt; 15 ms → <c>Thread.Sleep(gap - 5 ms)</c> then spin-wait remainder.</description></item>
///   <item><description>Gap 2–15 ms  → <c>Thread.Sleep(1)</c> loop until ≤ 2 ms remain.</description></item>
///   <item><description>Gap ≤ 2 ms   → spin-wait (<c>Thread.SpinWait</c>).</description></item>
/// </list>
/// Coarse waits avoid continuous spinning. Actual lateness depends on Windows scheduling
/// and load; timing distributions are measured by the fake-receiver stress tests.
/// </para>
/// <para>
/// <b>Coordinate normalization:</b> <c>SendInput</c> with <c>MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK</c>
/// requires coordinates in the range [0, 65535] mapped across the entire virtual screen.
/// Coordinates are normalized at the start of each <see cref="PlayAsync"/> call.
/// </para>
/// <para>
/// <b>Key tracking:</b> The player tracks which virtual keys and mouse buttons it has pressed
/// so that <see cref="ReleaseAllHeldKeys"/> can synthesize matching release events on panic.
/// </para>
/// </remarks>
public sealed class Win32InputPlayer : IInputPlayer, IPlaybackSessionAwarePlayer, IPlaybackSessionBoundSink, IPlaybackHoldOwnership, IHostInputOperationAwarePlayer
{
    private sealed class HostAttachment(Win32InputPlayer player, HostInputOperationLease lease) : IDisposable
    {
        public HostInputOperationLease Lease { get; } = lease;
        public void Dispose()
        {
            lock (player._heldStateLock)
                if (ReferenceEquals(player._hostAttachment, this)) player._hostAttachment = null;
        }
    }
    private HostAttachment? _hostAttachment;
    private HostInputOperationArbiter? _hostArbiter;
    private bool _hostManaged;

    public IDisposable AttachHostOperation(HostInputOperationLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_heldStateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!lease.OwnsCleanup) throw new InvalidOperationException("The host operation no longer owns input cleanup.");
            BindHostArbiter(lease);
            if (_hostAttachment is { } prior && !ReferenceEquals(prior.Lease, lease) && prior.Lease.OwnsCleanup)
                throw new InvalidOperationException("Another host operation owns the input player.");
            return _hostAttachment = new HostAttachment(this, lease);
        }
    }

    private void BindHostArbiter(HostInputOperationLease lease)
    {
        if (_hostArbiter is not null && !ReferenceEquals(_hostArbiter, lease.Arbiter))
            throw new InvalidOperationException("The input player belongs to another host input arbiter.");
        _hostArbiter = lease.Arbiter;
        _hostManaged = true;
    }

    private HostInputOperationLease? CaptureHostOperation(PlaybackSessionContext? owner = null)
    {
        lock (_heldStateLock)
        {
            var lease = owner is null ? _hostAttachment?.Lease : owner.HostOperation;
            if (lease is not null)
            {
                if (!lease.CanContinue) throw new OperationCanceledException("The captured host input operation was stopped.", lease.StopToken);
                BindHostArbiter(lease);
            }
            RequireHostOperation(lease, cleanup: false);
            return lease;
        }
    }

    private void RequireHostOperation(HostInputOperationLease? lease, bool cleanup)
    {
        if (!_hostManaged && lease is null) return;
        if (lease is null || !ReferenceEquals(lease.Arbiter, _hostArbiter) || !(cleanup ? lease.OwnsCleanup : lease.CanContinue))
            throw new OperationCanceledException("The captured host input operation is no longer permitted to submit input.", lease?.StopToken ?? CancellationToken.None);
    }
    private PlaybackSessionContext? _session;
    private PlaybackOptions _sessionOptions = PlaybackOptions.Default;
    public PlaybackSessionContext? Session { get => _session; set { _session = value; value?.BindSink(this); } }
    public ValueTask EmitSessionInputAsync(InputEvent input, bool cleanup, CancellationToken ct)
        => Session is { } owner ? EmitSessionInputAsync(owner, input, cleanup, ct)
            : EmitSessionInputCore(null, _sessionOptions, input, cleanup, ct);

    public ValueTask EmitSessionInputAsync(PlaybackSessionContext owner, InputEvent input, bool cleanup, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return EmitSessionInputCore(owner, owner.InputOptions, input, cleanup, ct);
    }

    private ValueTask EmitSessionInputCore(PlaybackSessionContext? owner, PlaybackOptions options, InputEvent input, bool cleanup, CancellationToken ct)
    {
        HostInputOperationLease? operation;
        lock (_heldStateLock)
        {
            operation = owner is null ? _hostAttachment?.Lease : owner.HostOperation;
            if (operation is not null)
            {
                if (!(cleanup ? operation.OwnsCleanup : operation.CanContinue))
                    throw new OperationCanceledException("The captured host input operation is unavailable.", operation.StopToken);
                BindHostArbiter(operation);
            }
            RequireHostOperation(operation, cleanup);
        }
        if (!cleanup && owner?.Target is { Window: { } pinned } target && target.Rule.Mode != MacroTargetMode.Screen
            && options.TargetWindowHandle != IntPtr.Zero && options.TargetWindowHandle != pinned.Handle)
            throw new ArgumentException("Playback options do not match the session's pinned target window.");
        NativeMethods.INPUT[] one = SingleInput();
        bool suppressed;
        try
        {
            suppressed = !cleanup && input.Type == InputEventType.MouseMove && ShouldSuppressAbsoluteLookWarp();
            if (!suppressed)
            {
                (int X, int Y)? resolvedAbsolute = null;
                if (!cleanup && owner is not null && input.Type == InputEventType.MouseMove) CacheVirtualScreenMetrics();
                if (!cleanup && owner is { Target.Window: not null } session
                    && input.Type is InputEventType.MouseMove or InputEventType.MouseDown or InputEventType.MouseWheel or InputEventType.MouseHWheel)
                {
                    var bounds = session.GetTargetClientBounds();
                    var point = input.Type == InputEventType.MouseMove ? ResolveCoordinates(input.X, input.Y, options) : _native.CursorPosition;
                    if (!bounds.Contains(point.Item1, point.Item2)) throw new UnsafeForegroundInputException("The pointer action is outside the selected window. No input was sent.");
                    if (input.Type == InputEventType.MouseMove) resolvedAbsolute = point;
                }
                if (!cleanup && owner is not null && input.Type == InputEventType.MouseMove && resolvedAbsolute is null)
                    resolvedAbsolute = ResolveCoordinates(input.X, input.Y, options);
                uint tag = options.TagSynthesizedEvents ? options.SynthesizedEventTag : 0;
                one[0] = resolvedAbsolute is { } absolute ? BuildMouseMoveInput(absolute.X, absolute.Y, tag)
                    : BuildInput(input, tag, options);
            }
        }
        catch
        {
            // A preparation callback can both invalidate the target and fail. With no native submission
            // yet, current owner denial wins over that pointer error. Never reinterpret an actual Send failure.
            if (!cleanup && (owner is not null || _hostManaged || operation is not null)) RequireFinalAdmission(owner, operation, ct);
            throw;
        }
        // Suppression is a completed zero-input event only after the bound owner has rechecked admission.
        if (!suppressed || owner is not null || _hostManaged) SendChecked(one, suppressed ? 0 : 1, ct, cleanup, owner, operation);
        return ValueTask.CompletedTask;
    }
    public bool OwnsHeldInput(InputEvent input)
    {
        lock (_heldStateLock) return input.Type switch
        {
            InputEventType.KeyDown => _heldVirtualKeys.Contains((ushort)input.Data),
            InputEventType.MouseDown => _heldMouseButtons.Contains(input.Data),
            InputEventType.Unicode => true,
            _ => false
        };
    }
    // Thresholds for the timing hybrid (in Stopwatch ticks)
    private static readonly long s_spinThresholdTicks  = 2  * Stopwatch.Frequency / 1000; // 2 ms
    private static readonly long s_sleepThresholdTicks = 15 * Stopwatch.Frequency / 1000; // 15 ms

    private readonly object _playLock = new();
    private readonly object _heldStateLock = new();

    // Tracks currently held keys/buttons for panic release
    private readonly HashSet<ushort> _heldVirtualKeys   = [];
    private readonly HashSet<int>    _heldMouseButtons   = [];
    private readonly HashSet<int> _borrowedInputs = [];
    private readonly HashSet<int> _borrowedAdditions = [];
    private readonly HashSet<int> _borrowedRemovals = [];
    private readonly HashSet<int> _pendingDowns = [];
    private readonly INativeInputSink _native;
    private readonly CancellationTokenSource _lifetimeCts = new();

    private readonly IWindowCoordinateService _coordService;
    private readonly IForegroundInputGuard? _foregroundGuard;

    private bool _isPlaying;
    private bool _disposed;

    [ThreadStatic]
    private static NativeMethods.INPUT[]? t_singleInput;

    private static NativeMethods.INPUT[] SingleInput()
        => t_singleInput ??= new NativeMethods.INPUT[1];

    [ThreadStatic]
    private static NativeMethods.INPUT[]? t_batchInput;

    private static NativeMethods.INPUT[] BatchInputs(int size)
    {
        NativeMethods.INPUT[]? buffer = t_batchInput;
        if (buffer is null || buffer.Length < size)
        {
            int cap = 16;
            while (cap < size)
                cap *= 2;
            buffer = new NativeMethods.INPUT[cap];
            t_batchInput = buffer;
        }

        return buffer;
    }

    public Win32InputPlayer(
        IWindowCoordinateService? coordService = null,
        IForegroundInputGuard? foregroundGuard = null)
        : this(new Win32NativeInputSink(), coordService, foregroundGuard)
    {
    }

    internal Win32InputPlayer(INativeInputSink native,
        IWindowCoordinateService? coordService = null,
        IForegroundInputGuard? foregroundGuard = null)
    {
        _native = native;
        _coordService = coordService ?? WindowCoordinateService.Default;
        _foregroundGuard = foregroundGuard;
    }

    // Virtual screen metrics — cached once per PlayAsync call
    private int _virtualOriginX;
    private int _virtualOriginY;
    private int _virtualWidth;
    private int _virtualHeight;

    /// <inheritdoc/>
    public bool IsPlaying => _isPlaying;

    /// <inheritdoc/>
    public async Task PlayAsync(
        IReadOnlyList<InputEvent> events,
        PlaybackOptions? options = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(events);
        ct.ThrowIfCancellationRequested();

        options = (options ?? PlaybackOptions.Default).Validate();
        var sessionOwner = options.Session ?? Session;
        var operation = CaptureHostOperation(sessionOwner);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
        ct = linkedCts.Token;

        lock (_playLock)
        {
            if (_isPlaying)
                throw new InvalidOperationException("Playback is already in progress.");
            _isPlaying = true;
        }

        try
        {
            if (sessionOwner is { } session)
            {
                _sessionOptions = options;
                try { await PlaybackSessionRunner.RunAsync(events, options, session, this, ct).ConfigureAwait(false); }
                finally { _sessionOptions = session.InputOptions; } // Retain legacy mapping scalars, never a completed owner's reference.
                return;
            }
            if (_hostManaged) RequireBoundInputAdmission(null, operation, ct); else ThrowIfForegroundUnsafe();

            // Recorded macros play one Down / Move / Up per step. A new thread
            // per event made camera look stutter and hid held-key state.
            if (events.Count <= 256)
            {
                PlaybackWorker(events, options, ct, operation);
                return;
            }

            await Task.Factory.StartNew(
                () => PlaybackWorker(events, options, ct, operation),
                ct,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ConfigureAwait(false);
        }
        catch
        {
            ReleaseCapturedInputs(operation);
            throw;
        }
        finally
        {
            lock (_playLock) { _isPlaying = false; }
        }
    }

    /// <inheritdoc/>
    public void ReleaseAllHeldKeys()
    {
        lock (_heldStateLock)
        {
            if (_hostManaged)
            {
                if (_heldVirtualKeys.Count == 0 && _heldMouseButtons.Count == 0 && _borrowedInputs.Count == 0) return;
                throw new InvalidOperationException("Managed input cleanup requires its captured host operation.");
            }
            ReleaseHeldInputs();
        }
    }

    public void ReleaseHostOperationInputs(HostInputOperationLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_heldStateLock)
        {
            if (!lease.OwnsCleanup) throw new OperationCanceledException("The captured host input operation no longer owns cleanup.", lease.StopToken);
            BindHostArbiter(lease);
            RequireHostOperation(lease, cleanup: true);
            ReleaseHeldInputs();
        }
    }

    private void ReleaseCapturedInputs(HostInputOperationLease? lease)
    {
        if (lease is not null) ReleaseHostOperationInputs(lease);
        else ReleaseAllHeldKeys();
    }

    private void ReleaseHeldInputs()
    {
        // Serialize cleanup with emission and ownership updates. A concurrent panic cannot
        // release before a successful down has been registered.
        lock (_heldStateLock)
        {
            var inputs = new NativeMethods.INPUT[_heldVirtualKeys.Count + _heldMouseButtons.Count];
            int count = 0;
            foreach (ushort vk in _heldVirtualKeys)
                inputs[count++] = BuildKeyInput(vk, keyUp: true, LowLevelHookService.PlaybackSentinelTag);
            foreach (int button in _heldMouseButtons)
                inputs[count++] = BuildMouseButtonInput((MouseButton)button, buttonDown: false, LowLevelHookService.PlaybackSentinelTag);
            _borrowedInputs.Clear();
            if (count == 0) return;
            // Cleanup must work even after focus becomes unsafe. Keep unaccepted releases
            // owned, so another cleanup can retry instead of claiming they were released.
            uint sent = _native.Send(inputs, count);
            for (int i = 0; i < Math.Min(sent, (uint)count); i++)
                UpdateHeldState(inputs[i]);
            if (sent != count)
                throw new InvalidOperationException($"Input cleanup failed: Windows accepted {sent} of {count} releases.");
        }
    }

    /// <inheritdoc/>
    public void SendRelativeMove(int dx, int dy)
        => SendRelativeMove(dx, dy, dualBackend: false);

    public void SendRelativeMove(int dx, int dy, bool dualBackend)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var operation = CaptureHostOperation(Session);
        if (dx == 0 && dy == 0) { RequireFinalAdmission(Session, operation, _lifetimeCts.Token); return; }
        if (Session != null) { PlayAsync([new(InputEventType.MouseMoveRelative, Stopwatch.GetTimestamp(), dx, dy, 0)]).GetAwaiter().GetResult(); return; }
        // The compatibility argument no longer duplicates the same physical motion.
        NativeMethods.INPUT[] input = SingleInput();
        input[0] = BuildRelativeMouseMoveInput(dx, dy, LowLevelHookService.PlaybackSentinelTag);
        SendChecked(input, 1, _lifetimeCts.Token, operation: operation);
    }

    /// <inheritdoc/>
    public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var operation = CaptureHostOperation(Session);
        if (Session != null)
        {
            long now = Stopwatch.GetTimestamp(); var events = new List<InputEvent>(5);
            if (x != -1 || y != -1) events.Add(new(InputEventType.MouseMove, now, x, y, 0));
            for (int i = 0; i < (doubleClick ? 2 : 1); i++)
            { events.Add(new(InputEventType.MouseDown, now, x, y, (int)button)); events.Add(new(InputEventType.MouseUp, now, x, y, (int)button)); }
            PlayAsync(events).GetAwaiter().GetResult(); return;
        }
        uint sentinel = LowLevelHookService.PlaybackSentinelTag;
        NativeMethods.INPUT[] one = SingleInput();
        if (x != -1 || y != -1)
        {
            try { CacheVirtualScreenMetrics(); one[0] = BuildMouseMoveInput(x, y, sentinel); }
            catch { if (_hostManaged) RequireFinalAdmission(null, operation, _lifetimeCts.Token); throw; }
            SendChecked(one, 1, _lifetimeCts.Token, operation: operation);
        }

        for (int click = 0; click < (doubleClick ? 2 : 1); click++)
        {
            try
            {
                one[0] = BuildMouseButtonInput(button, buttonDown: true, sentinel);
                SendChecked(one, 1, _lifetimeCts.Token, operation: operation);
                Thread.Sleep(2);
            }
            finally
            {
                // A focus change after down still requires the matching owned release.
                lock (_heldStateLock)
                {
                    RequireHostOperation(operation, cleanup: true);
                    if (_heldMouseButtons.Contains((int)button))
                    {
                        one[0] = BuildMouseButtonInput(button, buttonDown: false, sentinel);
                        SendChecked(one, 1, CancellationToken.None, cleanup: true, operation: operation);
                    }
                    _borrowedInputs.Remove(MouseVirtualKey(button));
                }
            }
            if (doubleClick && click == 0) Thread.Sleep(5);
        }
    }

    private void SendChecked(NativeMethods.INPUT[] inputs, int count, CancellationToken ct, bool cleanup = false,
        PlaybackSessionContext? owner = null, HostInputOperationLease? operation = null)
    {
        lock (_heldStateLock)
        {
            if (!cleanup)
            {
                ct.ThrowIfCancellationRequested();
                // Preserve the legacy no-session guard ordering. Bound emissions perform it after all probes.
                if (owner is null && !_hostManaged && operation is null) ThrowIfForegroundUnsafe();
            }
            else RequireHostOperation(operation, cleanup: true);
            _pendingDowns.Clear();
            _borrowedAdditions.Clear(); _borrowedRemovals.Clear();
            try
            {
                int written = 0;
                try
                {
                for (int i = 0; i < count; i++)
                {
                    NativeMethods.INPUT input = inputs[i];
                    if (!cleanup && TryGetHeldIdentity(input, out int key, out bool down))
                    {
                        bool owned = IsOwned(key) || _pendingDowns.Contains(key);
                        if (down)
                        {
                            if (!owned && (IsStagedBorrowed(key) || _native.IsPressed(key)))
                            {
                                StageBorrowedAddition(key);
                                continue;
                            }
                            _pendingDowns.Add(key);
                        }
                        else
                        {
                            if (StageBorrowedRemoval(key) || (!owned && _native.IsPressed(key)))
                                continue;
                            _pendingDowns.Remove(key);
                        }
                    }
                    inputs[written++] = input;
                }
                }
                catch
                {
                    if (!cleanup && (owner is not null || _hostManaged || operation is not null)) RequireFinalAdmission(owner, operation, ct);
                    throw;
                }
                if (!cleanup && (owner is not null || _hostManaged || operation is not null))
                    RequireBoundInputAdmission(owner, operation, ct);
                if (!cleanup)
                {
                    foreach (int key in _borrowedRemovals) _borrowedInputs.Remove(key);
                    foreach (int key in _borrowedAdditions) _borrowedInputs.Add(key);
                }
                // Even a completely borrowed/suppressed event cannot commit its logical filtering state
                // before the owner has checked fresh identity, pause and stop after every external probe.
                if (written == 0) return;
                uint sent = _native.Send(inputs, written);
                for (int i = 0; i < Math.Min(sent, (uint)written); i++)
                    UpdateHeldState(inputs[i]);
                if (sent != written)
                    throw new InvalidOperationException($"Input playback failed: Windows accepted {sent} of {written} events. Playback was stopped.");
            }
            finally { _borrowedAdditions.Clear(); _borrowedRemovals.Clear(); _pendingDowns.Clear(); }
        }
    }

    private bool IsStagedBorrowed(int key)
        => _borrowedAdditions.Contains(key) || !_borrowedRemovals.Contains(key) && _borrowedInputs.Contains(key);

    private void StageBorrowedAddition(int key)
    {
        _borrowedRemovals.Remove(key);
        if (!_borrowedInputs.Contains(key)) _borrowedAdditions.Add(key);
    }

    private bool StageBorrowedRemoval(int key)
    {
        if (!IsStagedBorrowed(key)) return false;
        _borrowedAdditions.Remove(key);
        if (_borrowedInputs.Contains(key)) _borrowedRemovals.Add(key);
        return true;
    }

    private static void RequireFinalSessionAdmission(PlaybackSessionContext owner, CancellationToken ct)
    {
        PlaybackInputAdmission admission = owner.InspectFinalInputAdmission(ct);
        switch (admission)
        {
            case PlaybackInputAdmission.Allowed: return;
            case PlaybackInputAdmission.Paused:
            case PlaybackInputAdmission.TargetUnavailable:
                throw new PlaybackInputDeferredException(admission);
            case PlaybackInputAdmission.Stopped:
                throw new OperationCanceledException("Playback was stopped before native input submission.", ct);
            default: throw new InvalidOperationException("The playback session returned invalid input admission.");
        }
    }

    private void RequireFinalAdmission(PlaybackSessionContext? owner, HostInputOperationLease? operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireHostOperation(operation, cleanup: false);
        try { if (owner is not null) RequireFinalSessionAdmission(owner, ct); }
        catch { RequireHostOperation(operation, cleanup: false); throw; }
        RequireHostOperation(operation, cleanup: false);
    }

    private void RequireBoundInputAdmission(PlaybackSessionContext? owner, HostInputOperationLease? operation, CancellationToken ct)
    {
        bool allowed = true;
        string? reason = null;
        try
        {
            if (_foregroundGuard is not null) allowed = _foregroundGuard.TryAllowSynthesizedInput(out reason);
        }
        catch
        {
            // The guard can fail after a callback stopped or invalidated this owner. No native work has
            // happened, so that current session denial takes priority; otherwise preserve the guard error.
            RequireFinalAdmission(owner, operation, ct);
            throw;
        }
        RequireFinalAdmission(owner, operation, ct);
        if (!allowed)
            throw new UnsafeForegroundInputException(reason ?? "Foreground window is not a safe input target.");
    }

    private bool IsOwned(int key) => key is 1 or 2 or 4 or 5 or 6
        ? _heldMouseButtons.Contains((int)MouseButtonFromVirtualKey(key))
        : _heldVirtualKeys.Contains((ushort)key);

    private static int MouseVirtualKey(MouseButton button) => button switch
    {
        MouseButton.Left => 1, MouseButton.Right => 2, MouseButton.Middle => 4,
        MouseButton.X1 => 5, MouseButton.X2 => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(button))
    };

    private static MouseButton MouseButtonFromVirtualKey(int key) => key switch
    {
        1 => MouseButton.Left, 2 => MouseButton.Right, 4 => MouseButton.Middle,
        5 => MouseButton.X1, 6 => MouseButton.X2,
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };

    private static bool TryGetHeldIdentity(in NativeMethods.INPUT input, out int key, out bool down)
    {
        if (input.type == NativeMethods.INPUT_KEYBOARD && (input.ui.ki.dwFlags & NativeMethods.KEYEVENTF_UNICODE) == 0)
        {
            key = input.ui.ki.wVk;
            down = (input.ui.ki.dwFlags & NativeMethods.KEYEVENTF_KEYUP) == 0;
            return true;
        }
        uint flags = input.ui.mi.dwFlags;
        key = (flags & (NativeMethods.MOUSEEVENTF_LEFTDOWN | NativeMethods.MOUSEEVENTF_LEFTUP)) != 0 ? 1
            : (flags & (NativeMethods.MOUSEEVENTF_RIGHTDOWN | NativeMethods.MOUSEEVENTF_RIGHTUP)) != 0 ? 2
            : (flags & (NativeMethods.MOUSEEVENTF_MIDDLEDOWN | NativeMethods.MOUSEEVENTF_MIDDLEUP)) != 0 ? 4
            : (flags & (NativeMethods.MOUSEEVENTF_XDOWN | NativeMethods.MOUSEEVENTF_XUP)) != 0
                ? (input.ui.mi.mouseData == NativeMethods.XBUTTON1 ? 5 : 6) : 0;
        down = (flags & (NativeMethods.MOUSEEVENTF_LEFTDOWN | NativeMethods.MOUSEEVENTF_RIGHTDOWN
            | NativeMethods.MOUSEEVENTF_MIDDLEDOWN | NativeMethods.MOUSEEVENTF_XDOWN)) != 0;
        return input.type == NativeMethods.INPUT_MOUSE && key != 0;
    }
    private void ThrowIfForegroundUnsafe()
    {
        if (_foregroundGuard is null)
            return;
        if (_foregroundGuard.TryAllowSynthesizedInput(out string? reason))
            return;

        throw new UnsafeForegroundInputException(
            reason ?? "Foreground window is not a safe input target.");
    }

    private bool IsForegroundSafe()
        => _foregroundGuard is null || _foregroundGuard.TryAllowSynthesizedInput(out _);

    // -----------------------------------------------------------------------
    // Private: playback worker (runs on LongRunning task, dedicated thread)
    // -----------------------------------------------------------------------

    private void PlaybackWorker(
        IReadOnlyList<InputEvent> events,
        PlaybackOptions options,
        CancellationToken ct,
        HostInputOperationLease? operation)
    {
        Thread currentThread = Thread.CurrentThread;
        ThreadPriority originalPriority = currentThread.Priority;

        try
        {
            currentThread.Priority = ThreadPriority.AboveNormal;

            // Cache virtual screen dimensions for coordinate normalization
            CacheVirtualScreenMetrics();

            double speedMultiplier = options.SpeedMultiplier;
            uint   sentinelTag     = options.TagSynthesizedEvents ? options.SynthesizedEventTag : 0;
            int    inputSize       = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>();

            for (int repeat = 0; repeat < options.RepeatCount; repeat++)
            {
                if (ct.IsCancellationRequested) break;
                if (events.Count == 0) break;

                long originTick    = Stopwatch.GetTimestamp();
                long firstEventTick = events[0].TimestampTicks;

                var (currentMouseX, currentMouseY) = _native.CursorPosition;

                // Group consecutive events with the same timestamp into batches for SendInput
                int i = 0;
                while (i < events.Count)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_hostManaged) RequireBoundInputAdmission(null, operation, ct); else ThrowIfForegroundUnsafe();

                    // Compute when this event should fire (scaled by speed multiplier)
                    var ev = events[i];
                    long scaledOffsetTicks = (long)((ev.TimestampTicks - firstEventTick) / speedMultiplier);
                    long targetTick = originTick + scaledOffsetTicks;

                    // Collect all events with the same original timestamp into one SendInput call
                    int batchStart = i;
                    while (i < events.Count && events[i].TimestampTicks == ev.TimestampTicks)
                        i++;

                    int batchSize = i - batchStart;

                    // Locked-cursor games (Roblox camera) treat an absolute warp
                    // as one huge relative look. Skip those while the cursor is
                    // clipped or a button is held. Relative packets still play.
                    if (batchSize == 1 && events[batchStart].Type == InputEventType.MouseMove
                        && ShouldSuppressAbsoluteLookWarp())
                    {
                        PrecisionWait(targetTick, ct);
                        if (_hostManaged) SendChecked(SingleInput(), 0, ct, operation: operation);
                        continue;
                    }

                    // Smooth mouse interpolation: if single mouse move event with noticeable distance and time
                    if (batchSize == 1 &&
                        events[batchStart].Type == InputEventType.MouseMove &&
                        options.InterpolateMouseMoves)
                    {
                        var (destX, destY) = ResolveCoordinates(events[batchStart].X, events[batchStart].Y, options);
                        int dx = destX - currentMouseX;
                        int dy = destY - currentMouseY;
                        double dist = Math.Sqrt((double)dx * dx + (double)dy * dy);

                        long currentTick = Stopwatch.GetTimestamp();
                        long waitTicks = targetTick - currentTick;
                        double waitMs = waitTicks * 1000.0 / Stopwatch.Frequency;

                        if (dist > 8 && waitMs >= 16)
                        {
                            int stepMs = Math.Max(4, options.MouseInterpolationStepMs);
                            int steps = Math.Clamp((int)(waitMs / stepMs), 2, 60);

                            for (int s = 1; s < steps; s++)
                            {
                                ct.ThrowIfCancellationRequested();
                                double u = (double)s / steps;
                                double smoothU = u * u * (3.0 - 2.0 * u);
                                int stepX = currentMouseX + (int)Math.Round(dx * smoothU);
                                int stepY = currentMouseY + (int)Math.Round(dy * smoothU);

                                long stepTargetTick = currentTick + (long)(waitTicks * u);
                                PrecisionWait(stepTargetTick, ct);

                                NativeMethods.INPUT[] stepInput = SingleInput();
                                stepInput[0] = BuildMouseMoveInput(stepX, stepY, sentinelTag);
                                SendChecked(stepInput, 1, ct, operation: operation);
                            }
                        }

                        PrecisionWait(targetTick, ct);
                        NativeMethods.INPUT[] finalMove = SingleInput();
                        finalMove[0] = BuildMouseMoveInput(destX, destY, sentinelTag);
                        SendChecked(finalMove, 1, ct, operation: operation);

                        currentMouseX = destX;
                        currentMouseY = destY;
                    }
                    else
                    {
                        // Wait until it's time
                        PrecisionWait(targetTick, ct);

                        NativeMethods.INPUT[] batch = BatchInputs(batchSize);
                        bool suppressAbsoluteMoves = ShouldSuppressAbsoluteLookWarp();
                        int inputCount = BuildPlaybackBatch(events, batchStart, batchSize,
                            suppressAbsoluteMoves, sentinelTag, options, batch);
                        if (inputCount > 0 || _hostManaged)
                            SendChecked(batch, inputCount, ct, operation: operation);
                        for (int b = 0; b < batchSize; b++)
                        {
                            var deliveredEvent = events[batchStart + b];
                            if (suppressAbsoluteMoves && deliveredEvent.Type == InputEventType.MouseMove)
                                continue;

                            // Every delivered move contributes to the next interpolation origin.
                            if (deliveredEvent.Type == InputEventType.MouseMoveRelative)
                            {
                                currentMouseX += deliveredEvent.X;
                                currentMouseY += deliveredEvent.Y;
                            }
                            else if (deliveredEvent.Type == InputEventType.MouseMove)
                            {
                                (currentMouseX, currentMouseY) = ResolveCoordinates(deliveredEvent.X, deliveredEvent.Y, options);
                            }
                        }
                    }

                }
            }
        }
        finally
        {
            try
            {
                // Only release on cancel/panic. A successful Down must stay held
                // until a later Up step — each recorded action is its own PlayAsync.
                if (ct.IsCancellationRequested)
                    ReleaseCapturedInputs(operation);
            }
            finally
            {
                currentThread.Priority = originalPriority;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Coordinate Resolution
    // -----------------------------------------------------------------------

    private bool ShouldSuppressAbsoluteLookWarp() => _native.SuppressAbsoluteMoves;

    private (int x, int y) ResolveCoordinates(int x, int y, PlaybackOptions options)
    {
        if (options.TargetWindowHandle != IntPtr.Zero && options.UseWindowRelativeCoordinates)
        {
            if (!_coordService.IsWindowValid(options.TargetWindowHandle)) throw new InvalidOperationException("The target window is no longer available.");
            if (options.RecordedClientWidth > 0 && options.RecordedClientHeight > 0)
            {
                var size = _coordService.GetClientSize(options.TargetWindowHandle);
                if (size.width <= 0 || size.height <= 0) throw new InvalidOperationException("The target window has no valid client area.");
                x = (int)Math.Round(x * (size.width / (double)options.RecordedClientWidth));
                y = (int)Math.Round(y * (size.height / (double)options.RecordedClientHeight));
            }
            return _coordService.ClientToScreen(options.TargetWindowHandle, x, y);
        }
        return (x, y);
    }

    // -----------------------------------------------------------------------
    // Timing
    // -----------------------------------------------------------------------

    private static void PrecisionWait(long targetTick, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        long remaining = targetTick - Stopwatch.GetTimestamp();
        if (remaining <= 0) return;

        // Phase 1: coarse sleep for the bulk of the wait
        while (remaining > s_sleepThresholdTicks)
        {
            ct.ThrowIfCancellationRequested();
            int sleepMs = (int)Math.Clamp((remaining - s_sleepThresholdTicks) * 1000 / Stopwatch.Frequency, 1, 8);
            Thread.Sleep(sleepMs);
            remaining = targetTick - Stopwatch.GetTimestamp();
        }

        // Phase 2: fine sleep (1ms at a time)
        while (remaining > s_spinThresholdTicks)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(1);
            remaining = targetTick - Stopwatch.GetTimestamp();
        }

        // Phase 3: spin for final precision (≤ 2ms remaining — acceptable CPU burn)
        while (Stopwatch.GetTimestamp() < targetTick)
        {
            ct.ThrowIfCancellationRequested();
            Thread.SpinWait(4);
        }
        ct.ThrowIfCancellationRequested();
    }

    // -----------------------------------------------------------------------
    // INPUT construction
    // -----------------------------------------------------------------------

    private int BuildPlaybackBatch(IReadOnlyList<InputEvent> events, int start, int count,
        bool suppressAbsoluteMoves, uint sentinelTag, PlaybackOptions options, NativeMethods.INPUT[] destination)
    {
        int written = 0;
        for (int i = start; i < start + count; i++)
        {
            var ev = events[i];
            if (suppressAbsoluteMoves && ev.Type == InputEventType.MouseMove)
                continue;
            destination[written++] = BuildInput(ev, sentinelTag, options);
        }
        return written;
    }

    private NativeMethods.INPUT BuildInput(in InputEvent ev, uint sentinelTag, PlaybackOptions options)
    {
        var (x, y) = ev.Type == InputEventType.MouseMove ? ResolveCoordinates(ev.X, ev.Y, options) : (ev.X, ev.Y);
        return ev.Type switch
        {
            InputEventType.MouseMove  => BuildMouseMoveInput(x, y, sentinelTag),
            InputEventType.MouseMoveRelative => BuildRelativeMouseMoveInput(ev.X, ev.Y, sentinelTag),
            InputEventType.MouseDown  => BuildMouseButtonInput((MouseButton)ev.Data, buttonDown: true,  sentinelTag),
            InputEventType.MouseUp    => BuildMouseButtonInput((MouseButton)ev.Data, buttonDown: false, sentinelTag),
            InputEventType.KeyDown    => BuildKeyInput((ushort)ev.Data, keyUp: false, sentinelTag),
            InputEventType.KeyUp      => BuildKeyInput((ushort)ev.Data, keyUp: true,  sentinelTag),
            InputEventType.MouseWheel => BuildMouseWheelInput(ev.Data, sentinelTag),
            InputEventType.MouseHWheel => BuildMouseHWheelInput(ev.Data, sentinelTag),
            InputEventType.Unicode => BuildUnicodeInput((ushort)ev.Data, keyUp: false, sentinelTag),
            InputEventType.UnicodeUp => BuildUnicodeInput((ushort)ev.Data, keyUp: true, sentinelTag),
            _ => throw new ArgumentOutOfRangeException(nameof(ev), "Unsupported input event type."),
        };
    }

    private static NativeMethods.INPUT BuildRelativeMouseMoveInput(int dx, int dy, uint extraInfo = 0)
    {
        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            ui   = new NativeMethods.INPUT_UNION
            {
                mi = new NativeMethods.MOUSEINPUT
                {
                    dx          = dx,
                    dy          = dy,
                    mouseData   = 0,
                    dwFlags     = NativeMethods.MOUSEEVENTF_MOVE
                                | NativeMethods.MOUSEEVENTF_MOVE_NOCOALESCE,
                    time        = 0,
                    dwExtraInfo = extraInfo,
                }
            }
        };
    }

    private NativeMethods.INPUT BuildMouseMoveInput(int x, int y, uint extraInfo = 0)
    {
        // Normalize to [0, 65535] across the virtual screen
        int normX = (_virtualWidth  > 1)
            ? (x - _virtualOriginX) * 65535 / (_virtualWidth  - 1)
            : 0;
        int normY = (_virtualHeight > 1)
            ? (y - _virtualOriginY) * 65535 / (_virtualHeight - 1)
            : 0;

        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            ui   = new NativeMethods.INPUT_UNION
            {
                mi = new NativeMethods.MOUSEINPUT
                {
                    dx          = normX,
                    dy          = normY,
                    mouseData   = 0,
                    dwFlags     = NativeMethods.MOUSEEVENTF_MOVE
                                | NativeMethods.MOUSEEVENTF_ABSOLUTE
                                | NativeMethods.MOUSEEVENTF_VIRTUALDESK,
                    time        = 0,
                    dwExtraInfo = extraInfo,
                }
            }
        };
    }

    private static NativeMethods.INPUT BuildMouseButtonInput(
        MouseButton button,
        bool        buttonDown,
        uint        extraInfo = 0)
    {
        uint flags = (button, buttonDown) switch
        {
            (MouseButton.Left,   true)  => NativeMethods.MOUSEEVENTF_LEFTDOWN,
            (MouseButton.Left,   false) => NativeMethods.MOUSEEVENTF_LEFTUP,
            (MouseButton.Right,  true)  => NativeMethods.MOUSEEVENTF_RIGHTDOWN,
            (MouseButton.Right,  false) => NativeMethods.MOUSEEVENTF_RIGHTUP,
            (MouseButton.Middle, true)  => NativeMethods.MOUSEEVENTF_MIDDLEDOWN,
            (MouseButton.Middle, false) => NativeMethods.MOUSEEVENTF_MIDDLEUP,
            (MouseButton.X1 or MouseButton.X2, true)  => NativeMethods.MOUSEEVENTF_XDOWN,
            (MouseButton.X1 or MouseButton.X2, false) => NativeMethods.MOUSEEVENTF_XUP,
            _ => 0,
        };

        uint mouseData = button switch
        {
            MouseButton.X1 => NativeMethods.XBUTTON1,
            MouseButton.X2 => NativeMethods.XBUTTON2,
            _              => 0,
        };

        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            ui   = new NativeMethods.INPUT_UNION
            {
                mi = new NativeMethods.MOUSEINPUT
                {
                    dx          = 0,
                    dy          = 0,
                    mouseData   = mouseData,
                    dwFlags     = flags,
                    time        = 0,
                    dwExtraInfo = extraInfo,
                }
            }
        };
    }

    private static NativeMethods.INPUT BuildKeyInput(ushort vk, bool keyUp, uint extraInfo = 0)
    {
        ushort scan = (ushort)NativeMethods.MapVirtualKeyW(vk, NativeMethods.MAPVK_VK_TO_VSC);
        uint flags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0;
        if (IsExtendedKey(vk))
            flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;

        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            ui   = new NativeMethods.INPUT_UNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk         = vk,
                    wScan       = scan,
                    dwFlags     = flags,
                    time        = 0,
                    dwExtraInfo = extraInfo,
                }
            }
        };
    }

    private static NativeMethods.INPUT BuildUnicodeInput(ushort codeUnit, bool keyUp, uint extraInfo = 0)
    {
        uint flags = NativeMethods.KEYEVENTF_UNICODE;
        if (keyUp)
            flags |= NativeMethods.KEYEVENTF_KEYUP;

        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            ui   = new NativeMethods.INPUT_UNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk         = 0,
                    wScan       = codeUnit,
                    dwFlags     = flags,
                    time        = 0,
                    dwExtraInfo = extraInfo,
                }
            }
        };
    }

    private static bool IsExtendedKey(ushort vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 or // Page/Home/End
        0x25 or 0x26 or 0x27 or 0x28 or // Arrows
        0x2D or 0x2E or                 // Insert/Delete
        0x5B or 0x5C or                 // Win
        0xA3 or 0xA5 or                 // Right Ctrl/Alt
        0x6F;                           // Numpad /

    private static NativeMethods.INPUT BuildMouseWheelInput(int delta, uint extraInfo = 0)
        => BuildWheelInput(delta, NativeMethods.MOUSEEVENTF_WHEEL, extraInfo);

    private static NativeMethods.INPUT BuildMouseHWheelInput(int delta, uint extraInfo = 0)
        => BuildWheelInput(delta, NativeMethods.MOUSEEVENTF_HWHEEL, extraInfo);

    private static NativeMethods.INPUT BuildWheelInput(int delta, uint flags, uint extraInfo)
    {
        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            ui   = new NativeMethods.INPUT_UNION
            {
                mi = new NativeMethods.MOUSEINPUT
                {
                    dx          = 0,
                    dy          = 0,
                    mouseData   = (uint)(short)delta,
                    dwFlags     = flags,
                    time        = 0,
                    dwExtraInfo = extraInfo,
                }
            }
        };
    }

    // -----------------------------------------------------------------------
    // Held-key tracking
    // -----------------------------------------------------------------------

    private void UpdateHeldState(in NativeMethods.INPUT input)
    {
        if (!TryGetHeldIdentity(input, out int key, out bool down)) return;
        if (input.type == NativeMethods.INPUT_KEYBOARD)
        {
            if (down) _heldVirtualKeys.Add((ushort)key);
            else _heldVirtualKeys.Remove((ushort)key);
        }
        else
        {
            int button = (int)MouseButtonFromVirtualKey(key);
            if (down) _heldMouseButtons.Add(button);
            else _heldMouseButtons.Remove(button);
        }
    }
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private void CacheVirtualScreenMetrics()
        => (_virtualOriginX, _virtualOriginY, _virtualWidth, _virtualHeight) = _native.VirtualScreen;

    // -----------------------------------------------------------------------
    // IDisposable
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        HostInputOperationLease? operation;
        lock (_heldStateLock) operation = _hostAttachment?.Lease;
        _disposed = true;
        _lifetimeCts.Cancel();
        ReleaseCapturedInputs(operation);
    }
}
