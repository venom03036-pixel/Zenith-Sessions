using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;

namespace MacroSuite.App.Overlays;

/// <summary>
/// Click-through HUD that follows the cursor and shows live screen coordinates
/// while a location pick is armed. Confirmation is Tab; cancel is Escape.
/// </summary>
public partial class CoordinatePickOverlay : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private readonly DispatcherTimer _timer;

    public CoordinatePickOverlay()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _timer.Tick += (_, _) => FollowCursor();
        Loaded += (_, _) =>
        {
            _timer.Start();
            FollowCursor();
        };
        Closed += (_, _) => _timer.Stop();
    }

    public static Task<ScreenPoint?> PromptAsync(ILocationPicker picker, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(picker);
        return RunPromptAsync(picker.ArmAndWaitAsync, ShowPrompt, ct);
    }

    public static async Task<ScreenPoint?> PromptAsync(ILocationPicker picker, HostInputOperationLease parent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentNullException.ThrowIfNull(parent);
        if (!parent.CanContinue || !parent.Arbiter.Owns(parent)
            || parent.Kind is not (HostInputOperationKind.Picker or HostInputOperationKind.CaptureTest))
            throw new InvalidOperationException("The location overlay requires its exact current interactive parent.");
        InputMode previous = InputModeGate.Current;
        InputModeGate.Set(InputMode.PickingLocation);
        try
        {
            return await RunPromptAsync(token => picker is IHostInputOperationBoundPicker bound
                ? bound.ArmAndWaitAsync(parent, token) : picker.ArmAndWaitAsync(token), ShowPrompt, ct);
        }
        finally
        {
            if (parent.OwnsCleanup) InputModeGate.Set(parent.CanContinue ? previous : InputMode.Normal);
        }
    }

    // Setup can fail after the child has armed. Always signal and join that exact child, rather
    // than cancelling whichever operation a shared picker happens to expose later.
    internal static async Task<ScreenPoint?> RunPromptAsync(
        Func<CancellationToken, Task<ScreenPoint?>> arm, Func<IDisposable> show, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<ScreenPoint?>? operation = null;
        IDisposable? visual = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            operation = arm(stop.Token);
            visual = show();
            var point = await operation;
            return stop.IsCancellationRequested ? null : point;
        }
        finally
        {
            bool clean = true;
            try
            {
                try { await stop.CancelAsync(); }
                catch (Exception) { clean = false; }
                if (operation is not null)
                {
                    try { await operation; }
                    catch (HostInputOperationCleanupException) { clean = false; }
                    catch (Exception) { /* The original await/setup failure is already propagating. */ }
                }
            }
            finally
            {
                try { visual?.Dispose(); }
                catch (Exception) { clean = false; }
            }
            if (!clean) throw new HostInputOperationCleanupException();
        }
    }

    private static IDisposable ShowPrompt()
    {
        CoordinatePickOverlay? overlay = null;
        IDisposable? hide = null;
        try
        {
            overlay = new CoordinatePickOverlay();
            OverlayWindow.Attach(overlay, owner: null);
            hide = ScreenCapturePrivacy.HideEngine();
            overlay.Show();
            return new PromptVisual(overlay, hide);
        }
        catch
        {
            bool clean = true;
            try { overlay?.Close(); } catch (Exception) { clean = false; }
            try { hide?.Dispose(); } catch (Exception) { clean = false; }
            if (!clean) throw new HostInputOperationCleanupException();
            throw;
        }
    }

    private sealed class PromptVisual(CoordinatePickOverlay overlay, IDisposable hide) : IDisposable
    {
        public void Dispose()
        {
            try { overlay.Close(); }
            finally { hide.Dispose(); }
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        nint style = GetWindowLongPtr(hwnd, GwlExStyle);
        SetWindowLongPtr(hwnd, GwlExStyle, style | WsExTransparent | WsExNoActivate | WsExToolWindow);
    }

    private void FollowCursor()
    {
        if (!GetCursorPos(out var pt))
            return;

        CoordsText.Text = $"X {pt.X}   Y {pt.Y}";

        var dpi = VisualTreeHelper.GetDpi(this);
        double scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        double scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        double width = ActualWidth > 0 ? ActualWidth : 180;
        double height = ActualHeight > 0 ? ActualHeight : 64;

        double cursorX = pt.X / scaleX;
        double cursorY = pt.Y / scaleY;
        double left = cursorX + 20;
        double top = cursorY + 24;
        double virtLeft = SystemParameters.VirtualScreenLeft;
        double virtTop = SystemParameters.VirtualScreenTop;
        double virtRight = virtLeft + SystemParameters.VirtualScreenWidth;
        double virtBottom = virtTop + SystemParameters.VirtualScreenHeight;
        if (left + width > virtRight)
            left = cursorX - width - 16;
        if (top + height > virtBottom)
            top = cursorY - height - 16;
        if (left < virtLeft)
            left = virtLeft + 8;
        if (top < virtTop)
            top = virtTop + 8;

        Left = left;
        Top = top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private static nint GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

    private static nint SetWindowLongPtr(IntPtr hWnd, int nIndex, nint value)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, value) : SetWindowLong32(hWnd, nIndex, (int)value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr64(IntPtr hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
}
