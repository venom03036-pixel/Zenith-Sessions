using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MacroSuite.Core.Coordinates;
using MacroSuite.Core.Input;
using MacroSuite.Infrastructure.Windows;
using DrawingRect = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;

namespace MacroSuite.App.Overlays;

/// <summary>One shared physical box rendered by one lightweight overlay per monitor. No capture loop.</summary>
public sealed class ImageSearchAreaPlacementWindow : Window
{
    private readonly MonitorDescriptor _monitor;
    private readonly Placement _state;
    private readonly BoxDrawing _drawing;
    private readonly TextBlock _sizeLabel;
    private Point _dragStart;
    private DrawingRect _original;
    private bool _dragging;
    private bool _resizing;
    private bool _closed;

    private sealed class Placement(DrawingRect area, DrawingSize minimum, DrawingRect desktop)
    {
        public DrawingRect Area = area;
        public DrawingSize Minimum = minimum;
        public DrawingRect Desktop = desktop;
        public readonly List<ImageSearchAreaPlacementWindow> Windows = new();
        public Action<bool>? Finish;
        public void Refresh() { foreach (var window in Windows) { window._drawing.InvalidateVisual(); window._sizeLabel.Text = $"X {Area.X}, Y {Area.Y} · {Area.Width} × {Area.Height} pixels"; } }
    }

    private ImageSearchAreaPlacementWindow(MonitorDescriptor monitor, Placement state)
    {
        _monitor = monitor;
        _state = state;
        Title = "Place image search area";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = monitor.Bounds.X / monitor.DpiScaleX;
        Top = monitor.Bounds.Y / monitor.DpiScaleY;
        Width = monitor.Bounds.Width / monitor.DpiScaleX;
        Height = monitor.Bounds.Height / monitor.DpiScaleY;
        var grid = new Grid { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), ClipToBounds = true };
        _drawing = new BoxDrawing(state, monitor.Bounds) { IsHitTestVisible = false };
        grid.Children.Add(_drawing);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Place image search area", Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Drag inside the box to move it. Drag its lower-right corner to enlarge it.\nThe whole template must fit. Enter applies; Esc cancels.", Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) });
        _sizeLabel = new TextBlock { Foreground = Brushes.White };
        panel.Children.Add(_sizeLabel);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var reset = new Button { Content = "Reset to image size", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        reset.Click += (_, _) =>
        {
            state.Area = ImageSearchAreaGeometry.ResetToTemplateSize(state.Area, state.Minimum, state.Desktop);
            state.Refresh();
        };
        var apply = new Button { Content = "Use this area", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        apply.SetResourceReference(StyleProperty, "PrimaryButton");
        apply.Click += (_, _) => state.Finish?.Invoke(true);
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 6, 12, 6) };
        cancel.Click += (_, _) => state.Finish?.Invoke(false);
        buttons.Children.Add(reset); buttons.Children.Add(apply); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        grid.Children.Add(new Border { Child = panel, Background = new SolidColorBrush(Color.FromArgb(245, 24, 24, 37)), CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Margin = new Thickness(16), MaxWidth = 470, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
        Content = grid;
        grid.MouseLeftButtonDown += OnDragStart;
        grid.MouseMove += OnDragMove;
        grid.MouseLeftButtonUp += (_, e) => { _dragging = false; grid.ReleaseMouseCapture(); e.Handled = true; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.Enter) { e.Handled = true; state.Finish?.Invoke(e.Key == Key.Enter); }
        };
        SourceInitialized += (_, _) => PlacePhysicalWindow();
        SizeChanged += (_, _) => _drawing.InvalidateVisual();
        Closed += (_, _) => _closed = true;
    }

    public static DrawingRect? Prompt(DrawingRect? initial, DrawingSize template)
        => Prompt(initial, template, default);

    public static DrawingRect? Prompt(DrawingRect? initial, DrawingSize template, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var geometry = Win32VirtualDesktopGeometry.Query();
        if (initial is null)
            initial = ImageSearchAreaGeometry.CenterOnDisplay(template, geometry.GetCurrentDisplayBounds(), geometry.VirtualDesktopBounds);
        var state = new Placement(ImageSearchAreaGeometry.Place(initial, template, geometry.VirtualDesktopBounds), template, geometry.VirtualDesktopBounds);
        using var hidden = ScreenCapturePrivacy.HideEngine(TimeSpan.Zero);
        DrawingRect? result = null;
        Exception? failure = null;
        bool closeFailed = false;
        try
        {
            foreach (var monitor in geometry.Monitors) state.Windows.Add(new ImageSearchAreaPlacementWindow(monitor, state));
            var primary = state.Windows.FirstOrDefault(w => w._monitor.Bounds.IntersectsWith(state.Area)) ?? state.Windows[0];
            state.Finish = accepted =>
            {
                if (accepted && !ct.IsCancellationRequested) result = state.Area;
                primary.Close();
            };
            primary.Loaded += (_, _) =>
            {
                try
                {
                    primary.PlacePhysicalWindow();
                    foreach (var window in state.Windows)
                    {
                        if (ReferenceEquals(window, primary)) continue;
                        window.Owner = primary; window.ShowActivated = false; window.Show(); window.PlacePhysicalWindow();
                    }
                    state.Refresh(); primary.Activate(); primary.Focus();
                }
                catch (Exception ex) { failure = ex; primary.Close(); }
            };
            AuthoringModalCancellation.ShowDialog(primary, ct);
        }
        catch (Exception ex) { failure = PreferPlacementFailure(failure, ex); }
        finally
        {
            state.Finish = null;
            foreach (var window in state.Windows)
            {
                try
                {
                    if (!window._closed) window.Close();
                    closeFailed |= !window._closed;
                }
                catch (Exception error)
                {
                    closeFailed = true;
                    System.Diagnostics.Trace.TraceWarning("Image placement close failed: {0}", error.GetType().Name);
                }
            }
        }
        if (closeFailed) throw new HostInputOperationCleanupException();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return ct.IsCancellationRequested ? null : result;
    }

    internal static Exception PreferPlacementFailure(Exception? first, Exception later)
        => later is HostInputOperationCleanupException ? later : first ?? later;

    private void PlacePhysicalWindow() => Win32WindowPlacement.SetPhysicalBounds(new WindowInteropHelper(this).Handle, _monitor.Bounds);
    private Point ScreenPoint(MouseEventArgs e) => PointToScreen(e.GetPosition(this));
    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        Point point = ScreenPoint(e);
        DrawingRect area = _state.Area;
        int handle = Math.Max(12, (int)Math.Round(12 * _monitor.DpiScaleX));
        _resizing = Math.Abs(point.X - area.Right) <= handle && Math.Abs(point.Y - area.Bottom) <= handle;
        if (!_resizing && !area.Contains((int)point.X, (int)point.Y)) return;
        _dragStart = point; _original = area; _dragging = true;
        ((UIElement)sender).CaptureMouse(); e.Handled = true;
    }
    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        Point point = ScreenPoint(e);
        int dx = (int)Math.Round(point.X - _dragStart.X), dy = (int)Math.Round(point.Y - _dragStart.Y);
        _state.Area = _resizing ? ImageSearchAreaGeometry.Resize(_original, dx, dy, _state.Minimum, _state.Desktop)
            : ImageSearchAreaGeometry.Move(_original, dx, dy, _state.Minimum, _state.Desktop);
        _state.Refresh(); e.Handled = true;
    }
    private sealed class BoxDrawing(Placement state, DrawingRect monitor) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            Rect box = SearchRegionPreviewGeometry.ToLocalDips(state.Area, monitor, dpi.DpiScaleX, dpi.DpiScaleY);
            var viewport = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
            dc.PushClip(viewport);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), null, new CombinedGeometry(GeometryCombineMode.Exclude, viewport, new RectangleGeometry(box)));
            dc.DrawRectangle(null, new Pen(Brushes.DeepSkyBlue, 2 / dpi.DpiScaleX), box);
            dc.DrawRectangle(Brushes.DeepSkyBlue, null, new Rect(box.Right - 6, box.Bottom - 6, 12, 12));
            dc.Pop();
        }
    }
}
