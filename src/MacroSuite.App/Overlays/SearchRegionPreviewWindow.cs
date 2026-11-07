using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MacroSuite.Core.Coordinates;
using MacroSuite.Infrastructure.Windows;
using DrawingRect = System.Drawing.Rectangle;

namespace MacroSuite.App.Overlays;

/// <summary>A static, dismissible preview. It never captures the screen or sends input.</summary>
public sealed class SearchRegionPreviewWindow : Window
{
    private readonly DrawingRect _monitor;
    private readonly RegionDrawing _drawing;
    private Action? _dismiss;

    private SearchRegionPreviewWindow(MonitorDescriptor monitor, DrawingRect region, string description)
    {
        _monitor = monitor.Bounds;
        Title = "Search area preview";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = monitor.Bounds.X / monitor.DpiScaleX;
        Top = monitor.Bounds.Y / monitor.DpiScaleY;
        Width = monitor.Bounds.Width / monitor.DpiScaleX;
        Height = monitor.Bounds.Height / monitor.DpiScaleY;

        var grid = new Grid { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), ClipToBounds = true };
        _drawing = new RegionDrawing(region, monitor.Bounds) { IsHitTestVisible = false };
        grid.Children.Add(_drawing);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = description, TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = "The outlined area is checked. Press Esc or click anywhere to close.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, Margin = new Thickness(0, 6, 0, 8)
        });
        var close = new Button { Content = "Close preview", Padding = new Thickness(12, 5, 12, 5), HorizontalAlignment = HorizontalAlignment.Left };
        close.Click += (_, _) => _dismiss?.Invoke();
        panel.Children.Add(close);
        grid.Children.Add(new Border
        {
            Child = panel, Background = new SolidColorBrush(Color.FromArgb(245, 24, 24, 37)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(137, 180, 250)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Margin = new Thickness(16),
            MaxWidth = 500, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        });
        Content = grid;
        MouseDown += (_, e) => { e.Handled = true; _dismiss?.Invoke(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape && e.SystemKey != Key.Escape) return;
            e.Handled = true;
            _dismiss?.Invoke();
        };
        SourceInitialized += (_, _) => PlacePhysicalWindow();
        SizeChanged += (_, _) => _drawing.InvalidateVisual();
    }

    public static void ShowPreview(DrawingRect region, string label, Window? owner = null)
    {
        var geometry = Win32VirtualDesktopGeometry.Query();
        DrawingRect visibleRegion = SearchRegionPreviewGeometry.ClipToDesktop(region, geometry.VirtualDesktopBounds);
        var monitors = geometry.Monitors;
        int primaryIndex = -1;
        long largestArea = 0;
        for (int i = 0; i < monitors.Count; i++)
        {
            DrawingRect intersection = DrawingRect.Intersect(visibleRegion, monitors[i].Bounds);
            long area = Math.Max(0L, intersection.Width) * Math.Max(0L, intersection.Height);
            if (area <= largestArea) continue;
            largestArea = area;
            primaryIndex = i;
        }
        if (primaryIndex < 0)
            throw new ArgumentException("This search area falls between connected displays. Choose an on-screen area.", nameof(region));

        string description = $"{label}\nX {region.X}, Y {region.Y}  ·  {region.Width} × {region.Height} pixels";
        if (visibleRegion != region)
            description += $"\nVisible area: X {visibleRegion.X}, Y {visibleRegion.Y}  ·  {visibleRegion.Width} × {visibleRegion.Height} pixels";

        using var hidden = ScreenCapturePrivacy.HideEngine(TimeSpan.Zero);
        var windows = new List<SearchRegionPreviewWindow>(monitors.Count);
        Exception? failure = null;
        try
        {
            for (int i = 0; i < monitors.Count; i++)
                windows.Add(new SearchRegionPreviewWindow(monitors[i], visibleRegion, description));
            SearchRegionPreviewWindow primary = windows[primaryIndex];
            OverlayWindow.Attach(primary, owner);
            primary.WindowStartupLocation = WindowStartupLocation.Manual;
            foreach (SearchRegionPreviewWindow window in windows)
                window._dismiss = primary.Close;

            primary.Loaded += (_, _) =>
            {
                try
                {
                    primary.PlacePhysicalWindow();
                    // Create companion HWNDs after ShowDialog disables pre-existing windows.
                    // They stay clickable and owned by the modal window, with one shared close.
                    foreach (SearchRegionPreviewWindow window in windows)
                    {
                        if (ReferenceEquals(window, primary)) continue;
                        window.Owner = primary;
                        window.ShowActivated = false;
                        window.Show();
                        window.PlacePhysicalWindow();
                    }
                    primary.Activate();
                    primary.Focus();
                }
                catch (Exception ex)
                {
                    failure = ex;
                    primary.Close();
                }
            };
            primary.ShowDialog();
        }
        catch (Exception ex)
        {
            failure ??= ex;
        }
        finally
        {
            foreach (SearchRegionPreviewWindow window in windows)
            {
                window._dismiss = null;
                try { window.Close(); }
                catch (Exception ex) { failure ??= ex; }
            }
        }
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void PlacePhysicalWindow()
    {
        Win32WindowPlacement.SetPhysicalBounds(new WindowInteropHelper(this).Handle, _monitor);
        _drawing.InvalidateVisual();
    }

    private sealed class RegionDrawing(DrawingRect region, DrawingRect monitor) : FrameworkElement
    {
        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            Rect localRegion = SearchRegionPreviewGeometry.ToLocalDips(region, monitor, dpi.DpiScaleX, dpi.DpiScaleY);
            var viewport = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
            var selected = new RectangleGeometry(localRegion);
            drawingContext.PushClip(viewport);
            drawingContext.DrawGeometry(new SolidColorBrush(Color.FromArgb(115, 0, 0, 0)), null,
                new CombinedGeometry(GeometryCombineMode.Exclude, viewport, selected));
            drawingContext.DrawGeometry(new SolidColorBrush(Color.FromArgb(20, 137, 180, 250)), null, selected);
            drawingContext.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(137, 180, 250)), 2 / dpi.DpiScaleX), localRegion);
            drawingContext.Pop();
        }
    }
}
