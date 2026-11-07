using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.ExceptionServices;
using MacroSuite.Infrastructure.Windows;
using DrawingRect = System.Drawing.Rectangle;

namespace MacroSuite.App.Overlays;

/// <summary>
/// Fullscreen transparent overlay window for rubber-band screen region selection.
/// Transforms WPF DIP coordinates to physical desktop pixels for capture pipelines.
/// </summary>
public partial class RegionSelectorWindow : Window
{
    private Point _startPoint;
    private bool _isSelecting;
    private DrawingRect? _selectedRegion;
    private readonly DrawingRect _desktopBounds;
    private Exception? _placementFailure;

    public DrawingRect? SelectedRegion => _selectedRegion;

    public RegionSelectorWindow()
    {
        InitializeComponent();
        _desktopBounds = Win32VirtualDesktopGeometry.Query().VirtualDesktopBounds;
        Left = _desktopBounds.X;
        Top = _desktopBounds.Y;
        Width = _desktopBounds.Width;
        Height = _desktopBounds.Height;
        SourceInitialized += (_, _) => PlacePhysicalWindow();
    }

    /// <summary>
    /// Displays the region selector modal and returns the physical screen bounding rectangle, or null if cancelled.
    /// </summary>
    public static DrawingRect? PromptSelection(Window? owner = null)
        => PromptSelection(owner, default);

    public static DrawingRect? PromptSelection(Window? owner, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using (ScreenCapturePrivacy.HideEngine())
        {
            ct.ThrowIfCancellationRequested();
            var window = new RegionSelectorWindow();
            // Never own a collapsed Always-On-Top main window — that nested modal hangs WPF.
            if (owner != null && owner.IsVisible && owner.Visibility == Visibility.Visible)
                OverlayWindow.Attach(window, owner);
            else
                OverlayWindow.Attach(window, owner: null);
            window.WindowStartupLocation = WindowStartupLocation.Manual;

            window.Loaded += (_, _) =>
            {
                try
                {
                    window.PlacePhysicalWindow();
                    window.Activate();
                    window.Focus();
                    Keyboard.Focus(window.OverlayCanvas);
                }
                catch (Exception ex)
                {
                    window._placementFailure = ex;
                    window.Close();
                }
            };

            bool? result = AuthoringModalCancellation.ShowDialog(window, ct);
            if (window._placementFailure is not null)
                ExceptionDispatchInfo.Capture(window._placementFailure).Throw();
            return result == true && !ct.IsCancellationRequested ? window.SelectedRegion : null;
        }
    }

    private void PlacePhysicalWindow()
        => Win32WindowPlacement.SetPhysicalBounds(new WindowInteropHelper(this).Handle, _desktopBounds);

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right)
        {
            CancelSelection();
            e.Handled = true;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _startPoint = e.GetPosition(OverlayCanvas);
            _lastMovePoint = _startPoint;
            _isSelecting = true;
            OverlayCanvas.CaptureMouse();

            SelectionBox.Visibility = Visibility.Visible;
            InfoBadge.Visibility = Visibility.Visible;

            Canvas.SetLeft(SelectionBox, _startPoint.X);
            Canvas.SetTop(SelectionBox, _startPoint.Y);
            SelectionBox.Width = 0;
            SelectionBox.Height = 0;
        }
    }

    private Point _lastMovePoint;

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSelecting) return;

        var currentPoint = e.GetPosition(OverlayCanvas);
        // Skip sub-pixel chatter so a full-desktop drag does not relayout every WM_MOUSEMOVE.
        if (Math.Abs(currentPoint.X - _lastMovePoint.X) < 2 && Math.Abs(currentPoint.Y - _lastMovePoint.Y) < 2)
            return;
        _lastMovePoint = currentPoint;

        double x = Math.Min(_startPoint.X, currentPoint.X);
        double y = Math.Min(_startPoint.Y, currentPoint.Y);
        double w = Math.Abs(currentPoint.X - _startPoint.X);
        double h = Math.Abs(currentPoint.Y - _startPoint.Y);

        Canvas.SetLeft(SelectionBox, x);
        Canvas.SetTop(SelectionBox, y);
        SelectionBox.Width = w;
        SelectionBox.Height = h;

        Canvas.SetLeft(InfoBadge, x);
        Canvas.SetTop(InfoBadge, Math.Max(0, y - 28));
        InfoBadgeText.Text = $"{Math.Round(w)} × {Math.Round(h)}";
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelecting) return;
        _isSelecting = false;
        if (OverlayCanvas.IsMouseCaptured)
            OverlayCanvas.ReleaseMouseCapture();

        var currentPoint = e.GetPosition(OverlayCanvas);
        double w = Math.Abs(currentPoint.X - _startPoint.X);
        double h = Math.Abs(currentPoint.Y - _startPoint.Y);

        if (w < 4 || h < 4)
        {
            // Ignored as accidental click
            SelectionBox.Visibility = Visibility.Collapsed;
            InfoBadge.Visibility = Visibility.Collapsed;
            return;
        }

        if (!TryGetPhysicalSelection(_startPoint, currentPoint, OverlayCanvas.PointToScreen, out var region))
        {
            CancelSelection();
            return;
        }

        _selectedRegion = region;
        DialogResult = true;
    }

    internal static bool TryGetPhysicalSelection(Point start, Point end, Func<Point, Point> toScreen, out DrawingRect region)
    {
        region = default;
        Point first, last;
        try
        {
            first = toScreen(start);
            last = toScreen(end);
        }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentException) { return false; }

        double left = Math.Round(Math.Min(first.X, last.X));
        double top = Math.Round(Math.Min(first.Y, last.Y));
        double right = Math.Round(Math.Max(first.X, last.X));
        double bottom = Math.Round(Math.Max(first.Y, last.Y));
        double width = right - left, height = bottom - top;
        if (!double.IsFinite(width) || !double.IsFinite(height)
            || left < int.MinValue || top < int.MinValue || right > int.MaxValue || bottom > int.MaxValue
            || width is < 4 or > int.MaxValue || height is < 4 or > int.MaxValue) return false;
        region = new((int)left, (int)top, (int)width, (int)height);
        return true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.SystemKey == Key.Escape)
        {
            e.Handled = true;
            CancelSelection();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.SystemKey == Key.Escape)
        {
            e.Handled = true;
            CancelSelection();
        }
    }

    private void CancelSelection()
    {
        _isSelecting = false;
        if (OverlayCanvas.IsMouseCaptured)
            OverlayCanvas.ReleaseMouseCapture();
        _selectedRegion = null;
        try
        {
            DialogResult = false;
        }
        catch (InvalidOperationException)
        {
            Close();
            return;
        }
        Close();
    }
}
