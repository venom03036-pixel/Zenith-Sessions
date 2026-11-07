using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MacroSuite.App.Themes;

/// <summary>Pauses decorative work during native movement and viewport gestures, without touching capture or settings.</summary>
public sealed class WindowInteractionGuard : IDisposable
{
    private readonly Window _window;
    private readonly Action _changed;
    private readonly DispatcherTimer _settled;
    private HwndSource? _source;
    private bool _moving, _dragging, _disposed;
    public bool IsInteracting { get; private set; }
    public WindowInteractionGuard(Window window, Action changed)
    {
        _window = window; _changed = changed;
        _settled = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        _settled.Tick += Settled;
        window.SourceInitialized += Attach;
        window.PreviewMouseWheel += Wheel;
        window.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(DragStarted), true);
        window.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(DragCompleted), true);
        window.IsVisibleChanged += VisibilityChanged;
        Attach(null, EventArgs.Empty);
    }
    private void Attach(object? sender, EventArgs args)
    {
        if (_source != null || _disposed) return;
        nint handle = new WindowInteropHelper(_window).Handle;
        if (handle == 0) return; // SourceInitialized will attach once the native window exists.
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowMessage);
    }
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // Keep default WPF/native movement and DPI processing. Only reduce decoration.
        if (message == 0x0231) { _moving = true; Pulse(); } // WM_ENTERSIZEMOVE
        else if (message == 0x0232) { _moving = false; Pulse(); } // WM_EXITSIZEMOVE
        else if (message == 0x02E0) Pulse(); // WM_DPICHANGED
        return 0;
    }
    private void Wheel(object sender, MouseWheelEventArgs args) => Pulse();
    private void DragStarted(object sender, DragStartedEventArgs args) { _dragging = true; Pulse(); }
    private void DragCompleted(object sender, DragCompletedEventArgs args) { _dragging = false; Pulse(); }
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    { if (!_window.IsVisible) { _moving = _dragging = false; _settled.Stop(); Set(false); } }
    private void Pulse()
    {
        if (_disposed) return;
        Set(true);
        _settled.Stop();
        if (!_moving && !_dragging) _settled.Start();
    }
    private void Settled(object? sender, EventArgs args) { _settled.Stop(); Set(false); }
    private void Set(bool value) { if (IsInteracting == value) return; IsInteracting = value; _changed(); }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _settled.Stop(); _settled.Tick -= Settled;
        _source?.RemoveHook(WindowMessage); _source = null;
        _window.SourceInitialized -= Attach; _window.PreviewMouseWheel -= Wheel;
        _window.RemoveHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(DragStarted));
        _window.RemoveHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(DragCompleted));
        _window.IsVisibleChanged -= VisibilityChanged; IsInteracting = false;
    }
}
