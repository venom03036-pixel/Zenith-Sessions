using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.Infrastructure.Input;

/// <summary>
/// Installs WH_MOUSE_LL and WH_KEYBOARD_LL Windows hooks on a dedicated background thread
/// that runs its own message pump. Captured events are written to a
/// <see cref="ChannelWriter{T}"/> of <see cref="InputEvent"/> for downstream processing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Callback budget:</b> The hook callback must return in &lt;1 ms. Windows silently removes
/// hooks that exceed the timeout. The callback does the minimum work:
/// construct an <see cref="InputEvent"/> on the stack → <c>TryWrite</c> → return.
/// No allocations, no locks, no logging inside the callback.
/// </para>
/// <para>
/// <b>Thread model:</b> Hooks are installed from and serviced by a dedicated
/// <see cref="ThreadPriority.AboveNormal"/> thread running <c>GetMessageW</c> / <c>DispatchMessageW</c>.
/// </para>
/// <para>
/// <b>Synthesized event filtering:</b> Events tagged with <see cref="PlaybackSentinelTag"/>
/// in <c>dwExtraInfo</c> are ignored so that playback does not re-record synthesized input.
/// </para>
/// </remarks>
public sealed class LowLevelHookService : IInputRecorder
{
    /// <summary>
    /// The <c>dwExtraInfo</c> sentinel value used by <see cref="Input.Win32InputPlayer"/> to
    /// mark synthesized events. Events with this tag are skipped by this service.
    /// </summary>
    public const uint PlaybackSentinelTag = 0xBEEFCAFEu;

    private ChannelWriter<InputEvent>?  _writer;
    private Thread?                     _hookThread;
    private volatile uint               _hookThreadId;
    private volatile IntPtr             _mouseHook;
    private volatile IntPtr             _keyboardHook;
    private NativeMethods.HookProc?     _mouseProc;   // GC root
    private NativeMethods.HookProc?     _keyboardProc; // GC root
    private readonly ManualResetEventSlim _ready   = new(false);
    private volatile bool               _isRecording;
    private volatile bool               _disposed;
    private volatile Exception?         _hookThreadError;
    private CancellationTokenRegistration _startCtRegistration;
    private bool _useRawMouseInput;
    private volatile bool _rawMouseActive;
    private volatile bool _stopRequested;

    /// <summary>Opt-in physical mouse deltas for locked cameras. Changes apply between recordings.</summary>
    public bool UseRawMouseInput
    {
        get => _useRawMouseInput;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRecording || _hookThread is { IsAlive: true })
                throw new InvalidOperationException("Mouse capture mode can only change between recordings.");
            _useRawMouseInput = value;
        }
    }

    // -----------------------------------------------------------------------
    // IInputRecorder
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public bool IsRecording => _isRecording;

    /// <inheritdoc/>
    public Task StartAsync(ChannelWriter<InputEvent> writer, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_isRecording || _hookThread is { IsAlive: true })
            throw new InvalidOperationException("Already recording.");

        _ready.Reset();
        _stopRequested = false;
        _hookThreadError = null;
        _writer = writer;

        _hookThread = new Thread(HookThreadProc)
        {
            Name        = "MacroSuite.HookMessagePump",
            IsBackground = true,
            Priority     = ThreadPriority.AboveNormal,
        };
        _hookThread.Start();

        try
        {
            if (!_ready.Wait(TimeSpan.FromSeconds(5), ct))
                throw new TimeoutException("Input capture did not become ready within five seconds.");
        }
        catch
        {
            TearDownHookThread();
            throw;
        }

        if (_hookThreadError != null)
        {
            var error = _hookThreadError;
            _hookThreadError = null;
            TearDownHookThread();
            throw error;
        }

        if (ct.IsCancellationRequested)
        {
            RequestCaptureStop();
            TearDownHookThread();
            ct.ThrowIfCancellationRequested();
        }
        _isRecording = true;

        _startCtRegistration.Dispose();
        if (ct.CanBeCanceled)
        {
            _startCtRegistration = ct.Register(static state =>
            {
                var self = (LowLevelHookService)state!;
                // Cancellation can arrive on WPF's dispatcher. Request shutdown only;
                // the recording owner awaits the actual join away from the UI thread.
                self.RequestCaptureStop();
            }, this);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync()
    {
        RequestCaptureStop();
        _startCtRegistration.Dispose();
        _startCtRegistration = default;
        _isRecording = false;
        TearDownHookThread();
        return Task.CompletedTask;
    }

    private void RequestCaptureStop()
    {
        _stopRequested = true;
        _isRecording = false;
        uint threadId = _hookThreadId;
        if (threadId != 0) NativeMethods.PostThreadMessageW(threadId, NativeMethods.WM_QUIT, 0, 0);
    }

    private void TearDownHookThread()
    {
        if (_hookThread == null && _hookThreadId == 0)
        {
            _writer?.TryComplete();
            _writer = null;
            UninstallHooks();
            return;
        }

        if (_hookThreadId != 0)
        {
            NativeMethods.PostThreadMessageW(_hookThreadId, NativeMethods.WM_QUIT, 0, 0);
        }
        else
        {
            // Cancellation may race hook install: wait briefly for the thread id, then quit.
            _ready.Wait(TimeSpan.FromSeconds(1));
            if (_hookThreadId != 0)
                NativeMethods.PostThreadMessageW(_hookThreadId, NativeMethods.WM_QUIT, 0, 0);
        }

        if (_hookThread is { } thread && !thread.Join(millisecondsTimeout: 3_000))
        {
            var error = new TimeoutException("Input capture could not stop within three seconds. A new recording cannot start until the capture thread exits.");
            _writer?.TryComplete(error);
            throw error;
        }
        _hookThread = null;
        _hookThreadId = 0;
        _writer?.TryComplete();
        _writer = null;
        UninstallHooks();
    }

    // -----------------------------------------------------------------------
    // Hook thread
    // -----------------------------------------------------------------------

    private void HookThreadProc()
    {
        RawMouseCapture? rawCapture = null;
        try
        {
            // Store delegate references in fields BEFORE installing hooks to prevent GC collection
            _mouseProc    = MouseHookCallback;
            _keyboardProc = KeyboardHookCallback;

            IntPtr hMod = NativeMethods.GetModuleHandleW(null);
            _hookThreadId = NativeMethods.GetCurrentThreadId();

            _mouseHook    = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL,    _mouseProc,    hMod, 0);
            _keyboardHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, hMod, 0);

            if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                _hookThreadError = new InvalidOperationException(
                    $"Failed to install input hooks (mouse=0x{_mouseHook:X}, keyboard=0x{_keyboardHook:X}, win32={err}).");
                return;
            }

            if (_useRawMouseInput)
            {
                rawCapture = new RawMouseCapture(RecordRawMovement, FailCapture);
                _rawMouseActive = true;
            }

            // Signal that hooks and the optional raw receiver are installed.
            _ready.Set();
            if (_stopRequested) return;

            // Message pump — required to receive hook notifications
            int messageResult;
            while ((messageResult = NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0)) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessageW(ref msg);
            }
            if (messageResult == -1)
                throw new InvalidOperationException($"Input capture stopped because its Windows message pump failed (win32={Marshal.GetLastWin32Error()}). The recording is incomplete.");
        }
        catch (Exception ex)
        {
            // Never let the hook thread die with WH_*_LL still installed: Windows then
            // waits on a dead callback and system-wide input (and game FPS) collapse.
            _hookThreadError = ex;
            _writer?.TryComplete(ex);
        }
        finally
        {
            try { rawCapture?.Dispose(); }
            catch (Exception error) { _writer?.TryComplete(error); _hookThreadError = error; }
            _rawMouseActive = false;
            UninstallHooks();
            _hookThreadId = 0;
            _isRecording = false;
            _writer?.TryComplete(_hookThreadError);
            _ready.Set();
        }
    }

    private void RecordRawMovement(int dx, int dy, long timestamp)
    {
        if (_isRecording && InputModeGate.AcceptsRecording)
            _writer?.TryWrite(new InputEvent(InputEventType.MouseMoveRelative, timestamp, dx, dy, 0));
    }

    private void FailCapture(Exception error)
    {
        _writer?.TryComplete(error);
        _isRecording = false;
        if (_hookThreadId != 0)
            NativeMethods.PostThreadMessageW(_hookThreadId, NativeMethods.WM_QUIT, 0, 0);
    }

    private void UninstallHooks()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        if (_keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    // -----------------------------------------------------------------------
    // Hook callbacks — must be extremely fast (< 1 ms)
    // -----------------------------------------------------------------------

    private unsafe IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (lParam == IntPtr.Zero)
            return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        try
        {
            if (nCode >= 0 && _isRecording)
            {
                ref readonly var data =
                    ref Unsafe.AsRef<NativeMethods.MSLLHOOKSTRUCT>((void*)lParam);

                // Skip events synthesized by our own playback (Rule 4 — single input pipeline)
                if (data.dwExtraInfo != PlaybackSentinelTag
                    && InputModeGate.AcceptsRecording)
                {
                    var msg = (uint)wParam;
                    // The raw receiver is the sole motion source in raw mode. Buttons and
                    // wheel messages retain the established low-level hook path.
                    var ev = ShouldCaptureLegacyMouseMessage(_rawMouseActive, msg) ? BuildMouseEvent(msg, in data) : null;

                    if (ev.HasValue)
                        _writer?.TryWrite(ev.Value);
                }
            }
        }
        catch (Exception error)
        {
            // Report capture failure without throwing across the native callback boundary.
            FailCapture(error);
        }

        return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private unsafe IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (lParam == IntPtr.Zero)
            return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        try
        {
            if (nCode >= 0 && _isRecording)
            {
                ref readonly var data =
                    ref Unsafe.AsRef<NativeMethods.KBDLLHOOKSTRUCT>((void*)lParam);

                if (data.dwExtraInfo != PlaybackSentinelTag
                    && InputModeGate.AcceptsRecording)
                {
                    var msg  = (uint)wParam;
                    var type = msg switch
                    {
                        0x0100 => InputEventType.KeyDown, // WM_KEYDOWN
                        0x0101 => InputEventType.KeyUp,   // WM_KEYUP
                        0x0104 => InputEventType.KeyDown, // WM_SYSKEYDOWN
                        0x0105 => InputEventType.KeyUp,   // WM_SYSKEYUP
                        _      => (InputEventType)byte.MaxValue,
                    };

                    if (type != (InputEventType)byte.MaxValue)
                    {
                        var ev = new InputEvent(
                            type,
                            Stopwatch.GetTimestamp(),
                            x:         0,
                            y:         0,
                            data:      (int)data.vkCode,
                            extraInfo: (uint)data.dwExtraInfo);

                        _writer?.TryWrite(ev);
                    }
                }
            }
        }
        catch (Exception error)
        {
            // Report capture failure without throwing across the native callback boundary.
            FailCapture(error);
        }

        return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    internal static bool ShouldCaptureLegacyMouseMessage(bool rawMouseActive, uint message) =>
        !rawMouseActive || message != NativeMethods.WM_MOUSEMOVE;

    private static InputEvent? BuildMouseEvent(uint msg, ref readonly NativeMethods.MSLLHOOKSTRUCT data)
    {
        var type   = InputEventType.MouseMove;
        int evData = 0;

        switch (msg)
        {
            case NativeMethods.WM_MOUSEMOVE:
                type = InputEventType.MouseMove;
                break;

            case NativeMethods.WM_LBUTTONDOWN:
                type   = InputEventType.MouseDown;
                evData = (int)MouseButton.Left;
                break;
            case NativeMethods.WM_LBUTTONUP:
                type   = InputEventType.MouseUp;
                evData = (int)MouseButton.Left;
                break;

            case NativeMethods.WM_RBUTTONDOWN:
                type   = InputEventType.MouseDown;
                evData = (int)MouseButton.Right;
                break;
            case NativeMethods.WM_RBUTTONUP:
                type   = InputEventType.MouseUp;
                evData = (int)MouseButton.Right;
                break;

            case NativeMethods.WM_MBUTTONDOWN:
                type   = InputEventType.MouseDown;
                evData = (int)MouseButton.Middle;
                break;
            case NativeMethods.WM_MBUTTONUP:
                type   = InputEventType.MouseUp;
                evData = (int)MouseButton.Middle;
                break;

            case NativeMethods.WM_XBUTTONDOWN:
                type   = InputEventType.MouseDown;
                evData = (data.mouseData >> 16) == NativeMethods.XBUTTON1
                    ? (int)MouseButton.X1
                    : (int)MouseButton.X2;
                break;
            case NativeMethods.WM_XBUTTONUP:
                type   = InputEventType.MouseUp;
                evData = (data.mouseData >> 16) == NativeMethods.XBUTTON1
                    ? (int)MouseButton.X1
                    : (int)MouseButton.X2;
                break;

            case NativeMethods.WM_MOUSEWHEEL:
                type   = InputEventType.MouseWheel;
                evData = (short)(data.mouseData >> 16); // signed wheel delta
                break;

            case NativeMethods.WM_MOUSEHWHEEL:
                type   = InputEventType.MouseHWheel;
                evData = (short)(data.mouseData >> 16);
                break;

            default:
                return null; // Unhandled message type
        }

        return new InputEvent(
            type,
            Stopwatch.GetTimestamp(),
            data.pt.x,
            data.pt.y,
            evData,
            (uint)data.dwExtraInfo);
    }

    // -----------------------------------------------------------------------
    // IDisposable
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"LowLevelHookService.Dispose failed to stop hooks: {ex}");
        }

        // A timed-out pump still owns this signal until its finally block runs.
        if (_hookThread is not { IsAlive: true }) _ready.Dispose();
    }
}

