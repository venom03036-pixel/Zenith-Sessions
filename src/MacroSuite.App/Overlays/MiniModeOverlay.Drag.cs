using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MacroSuite.App.Common;
using MacroSuite.Core.Input;
using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.App.Overlays;

public partial class MiniModeOverlay
{
    private readonly MiniPointerGesture _pointerGesture = new();
    private string _gestureState = string.Empty;
    private void InitializeDragInput()
    {
        AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler(OnAnywhereMouseDown), true);
        AddHandler(PreviewMouseMoveEvent, new MouseEventHandler(OnAnywhereMouseMove), true);
        AddHandler(PreviewMouseUpEvent, new MouseButtonEventHandler(OnAnywhereMouseUp), true);
        IsVisibleChanged += (_, _) => InvalidatePointerGesture();
        Deactivated += (_, _) => InvalidatePointerGesture();
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(MiniWindowHook);
    }
    private IntPtr MiniWindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Clicking transport buttons must not steal keyboard focus from the target being recorded.
        if (message == 0x0021) { handled = true; return new IntPtr(3); } // WM_MOUSEACTIVATE / MA_NOACTIVATE
        return IntPtr.Zero;
    }
    private void OnAnywhereMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsClickThrough || InputModeGate.IsResourceCapture) return;
        Point point = PointToScreen(e.GetPosition(this));
        _pointerGesture.Begin(point.X, point.Y);
        _gestureState = _miniVm?.InteractionState ?? string.Empty;
    }
    private void OnAnywhereMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || IsClickThrough || InputModeGate.IsResourceCapture) return;
        Point point = PointToScreen(e.GetPosition(this));
        var dpi = VisualTreeHelper.GetDpi(this);
        if (!_pointerGesture.Move(point.X, point.Y, SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX,
            SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY)) return;
        // A Button may have captured the press. Cancelling capture disarms its release click
        // before entering Windows' modal move loop, including for Panic and Menu.
        Mouse.Capture(null);
        e.Handled = true;
        _userMoved = true;
        try { DragMove(); }
        catch (InvalidOperationException) { /* the initiating button was released before the native loop */ }
        finally { Win32WindowPlacement.ClampToNearestWorkArea(new WindowInteropHelper(this).Handle); }
    }
    private void OnAnywhereMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (_gestureState != (_miniVm?.InteractionState ?? string.Empty) || InputModeGate.IsResourceCapture || IsClickThrough)
            _pointerGesture.Cancel();
        if (_pointerGesture.End()) { Mouse.Capture(null); e.Handled = true; }
    }
    private void OnGestureStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MiniModeViewModel.InteractionState) && _gestureState != _miniVm?.InteractionState)
            InvalidatePointerGesture();
    }
    private void InvalidatePointerGesture()
    {
        bool moving = _pointerGesture.IsDragging;
        _pointerGesture.Cancel();
        if (Mouse.Captured is DependencyObject captured && Window.GetWindow(captured) == this) Mouse.Capture(null);
        if (moving) Win32WindowPlacement.CancelMove(new WindowInteropHelper(this).Handle);
    }
}
