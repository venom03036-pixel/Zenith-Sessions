using System.Drawing;
using System.Runtime.InteropServices;

namespace MacroSuite.Infrastructure.Input;

/// <summary>Native observations are sampled by one owned worker, never by input hook callbacks.</summary>
internal sealed class RecordingDesktopSnapshot : IAsyncDisposable
{
    internal sealed record State(bool OwnForeground, Rectangle[] OwnBounds, int HeldButtons, bool PointerConstrained);
    private State _current = new(true, [], 0, false);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly IRecordingDesktopState _desktop;
    private readonly bool _native;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    public State Current => Volatile.Read(ref _current);
    public RecordingDesktopSnapshot(IRecordingDesktopState desktop)
    {
        _desktop = desktop; _native = desktop is RecordingDesktopState;
        Sample(); _worker = Task.Run(ObserveAsync);
    }
    private void Sample()
    {
        var rectangles = new List<Rectangle>();
        if (_native) EnumWindows((window, _) => {
            GetWindowThreadProcessId(window, out uint pid);
            if (pid == Environment.ProcessId && IsWindowVisible(window) && !IsIconic(window) && GetWindowRect(window, out var rect))
                rectangles.Add(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
            return true;
        }, IntPtr.Zero);
        int buttons = 0;
        foreach (var button in Enum.GetValues<MacroSuite.Core.Models.MouseButton>())
            if (_desktop.IsMouseButtonDown(button)) buttons |= 1 << (int)button;
        Volatile.Write(ref _current, new(_desktop.IsOwnForegroundWindow(), rectangles.ToArray(), buttons, _desktop.IsPointerConstrained()));
    }
    private async Task ObserveAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        try { while (await timer.WaitForNextTickAsync(_stop.Token)) Sample(); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public ValueTask DisposeAsync()
    { lock (_disposeGate) return new ValueTask(_disposeTask ??= StopAsync()); }
    private async Task StopAsync() { _stop.Cancel(); await _worker.ConfigureAwait(false); _stop.Dispose(); }
    private delegate bool EnumProc(IntPtr window, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
}
