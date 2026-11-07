using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;

namespace MacroSuite.Infrastructure.Windows;

/// <summary>One owned WinEvent message pump publishes cached metadata outside input hooks.</summary>
public sealed class Win32TargetWindowService : TargetWindowServiceBase, IAsyncDisposable
{
    private readonly WindowCoordinateService _coordinates = new();
    private readonly ManualResetEventSlim _started = new();
    private readonly Task _worker;
    private readonly WinEventCallback _callback;
    private uint _thread;
    private Exception? _startupError;
    private readonly object _disposeSync = new();
    private Task? _disposeTask;
    private readonly Action? _requestStop;
    private delegate void WinEventCallback(nint hook, uint ev, nint window, int objectId, int child, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMethods.MSG message, IntPtr window, uint first, uint last, uint remove);
    public override bool SupportsWindowTargeting => true;
    public Win32TargetWindowService()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows target services require Windows.");
        _callback = (_, _, _, objectId, _, _, _) => { if (objectId == 0 && !Disposed) Observe(); };
        _worker = Task.Factory.StartNew(Pump, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _started.Wait();
        if (_startupError != null) { _started.Dispose(); throw new IOException("Desktop target observation could not start.", _startupError); }
    }
    // Isolated lifecycle seam: no WinEvent hook or desktop observer is installed by this constructor.
    internal Win32TargetWindowService(Task worker, Action requestStop)
    {
        _worker = worker; _requestStop = requestStop;
        _callback = (_, _, _, _, _, _, _) => { };
        _started.Set();
    }
    internal TargetWindowInfo? InspectWindowMetadata(IntPtr window) => ReadWindow(window);
    private void Pump()
    {
        nint foreground = 0, objects = 0;
        try
        {
            _thread = NativeMethods.GetCurrentThreadId();
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // make shutdown postable before publishing readiness
            foreground = SetWinEventHook(3, 3, 0, _callback, 0, 0, 0);
            objects = SetWinEventHook(0x8000, 0x800c, 0, _callback, 0, 0, 0);
            if (foreground == 0 || objects == 0) throw new IOException("Windows did not create the target observer.");
            Observe(); _started.Set();
            while (NativeMethods.GetMessageW(out var message, 0, 0, 0) > 0)
            { NativeMethods.TranslateMessage(ref message); NativeMethods.DispatchMessageW(ref message); }
        }
        catch (Exception ex) { _startupError = ex; _started.Set(); }
        finally { if (foreground != 0) UnhookWinEvent(foreground); if (objects != 0) UnhookWinEvent(objects); }
    }
    protected override nint ReadForeground() => NativeMethods.GetForegroundWindow();
    protected override bool IsOwnForeground()
    { NativeMethods.GetWindowThreadProcessId(ReadForeground(), out uint pid); return pid == Environment.ProcessId; }
    protected override Rectangle ReadScreenBounds() => new(NativeMethods.GetSystemMetrics(76), NativeMethods.GetSystemMetrics(77), NativeMethods.GetSystemMetrics(78), NativeMethods.GetSystemMetrics(79));
    protected override TargetWindowInfo? ReadWindow(nint window)
    {
        if (window == 0 || !NativeMethods.IsWindowVisible(window) || !_coordinates.IsWindowValid(window)) return null;
        NativeMethods.GetWindowThreadProcessId(window, out uint pid); if (pid == 0) return null;
        // Own windows are excluded targets. GetWindowText/Length would synchronously send
        // WM_GETTEXT to their WPF thread, potentially deadlocking shutdown's observer drain.
        if (pid == Environment.ProcessId) return new(window, (int)pid, 0, "ZenithMacros", "Zenith Macros", Rectangle.Empty, 0, true);
        try
        {
            using var process = Process.GetProcessById(checked((int)pid));
            int length = Math.Min(2048, NativeMethods.GetWindowTextLengthW(window)); var title = new StringBuilder(length + 1);
            NativeMethods.GetWindowTextW(window, title, title.Capacity);
            var size = _coordinates.GetClientSize(window); var origin = _coordinates.ClientToScreen(window, 0, 0);
            return new(window, (int)pid, process.StartTime.ToUniversalTime().Ticks, process.ProcessName, title.ToString(),
                new(origin.screenX, origin.screenY, size.width, size.height), 0);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }
    protected override IReadOnlyList<TargetWindowInfo> ReadWindows()
    {
        var result = new List<TargetWindowInfo>();
        NativeMethods.EnumWindows((window, _) => { if (ReadWindow(window) is { } info) result.Add(info); return result.Count < 512; }, 0);
        return result;
    }
    protected override bool ActivateWindow(nint window) => SetForegroundWindow(window);
    public override void Dispose()
    {
        if (_thread != 0 && NativeMethods.GetCurrentThreadId() == _thread)
            throw new InvalidOperationException("Await DisposeAsync outside the observer callback to finish its own worker.");
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    public ValueTask DisposeAsync()
    {
        lock (_disposeSync) return new(_disposeTask ??= StopObserverAsync());
    }
    private async Task StopObserverAsync()
    {
        Disposed = true;
        if (_requestStop != null) _requestStop();
        else if (_thread != 0) NativeMethods.PostThreadMessageW(_thread, 0x12, 0, 0);
        // Completion remains owned and awaited. No detached cleanup or false successful disposal.
        await _worker.ConfigureAwait(false);
        _started.Dispose();
    }
}
