using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MacroSuite.Core.Coordinates;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Vision;
using MacroSuite.Infrastructure.Windows;
using DrawingRect = System.Drawing.Rectangle;

namespace MacroSuite.App.Overlays;

/// <summary>
/// Transparent click-through overlay of the physical search rectangle and best match point.
/// Displays diagnostics only — it does not capture, click, or send input (Rule 1).
/// </summary>
public partial class LiveWatchOverlay : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private ScreenCoordinateMapper? _mapper;
    private DateTime _mapperCachedAt = DateTime.MinValue;
    private bool _attached;

    public LiveWatchOverlay()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
        SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
    }

    public void SetWatchRegion(DrawingRect region, string? label = null)
    {
        EnsureShown();
        PlaceOverPhysicalRect(region);

        if (!string.IsNullOrWhiteSpace(label))
            StatusText.Text = label;
    }

    public void UpdateStatus(string status) => StatusText.Text = status;

    public void SetDetectionResult(ColorDetectionResult result)
        => SetStatus(new ExecutionStatusSnapshot
        {
            Phase = "ColorDetection",
            Detection = result,
            Elapsed = result.DetectionTime,
            Checks = 1,
            ConsecutiveTrue = result.Matched ? 1 : 0,
            ConsecutiveFalse = result.Matched ? 0 : 1
        });

    private bool _lastMatched;
    private bool _hasLastMatched;
    private long _lastStatusTicks;
    private static readonly long StatusMinIntervalTicks = Stopwatch.Frequency / 10;

    public void SetStatus(ExecutionStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        bool matched = snapshot.Detection?.Matched == true;
        long now = Stopwatch.GetTimestamp();
        if (_hasLastMatched
            && matched == _lastMatched
            && now - _lastStatusTicks < StatusMinIntervalTicks)
        {
            return;
        }

        _hasLastMatched = true;
        _lastMatched = matched;
        _lastStatusTicks = now;

        EnsureShown();

        ColorDetectionResult? detection = snapshot.Detection;
        DrawingRect region = detection?.CaptureRegion ?? default;
        if (region.Width <= 0 || region.Height <= 0)
            region = new DrawingRect(8, 8, 320, 120);

        PlaceOverPhysicalRect(region);
        StatusText.Text = detection is null
            ? snapshot.Phase
            : $"{(detection.Matched ? "MATCH" : "NO MATCH")}  {detection.MatchPercentage:P1}";
        DiagnosticsText.Text = FormatDiagnostics(snapshot, region);

        if (detection?.BestMatchPoint is not { } point)
        {
            CrosshairHorizontal.Visibility = Visibility.Collapsed;
            CrosshairVertical.Visibility = Visibility.Collapsed;
            return;
        }

        GetDipScale(out double dpiX, out double dpiY);
        double localX = (point.X - region.X) / dpiX;
        double localY = (point.Y - region.Y) / dpiY;
        CrosshairHorizontal.Visibility = Visibility.Visible;
        CrosshairVertical.Visibility = Visibility.Visible;
        Canvas.SetLeft(CrosshairHorizontal, localX);
        Canvas.SetTop(CrosshairHorizontal, localY);
        Canvas.SetLeft(CrosshairVertical, localX);
        Canvas.SetTop(CrosshairVertical, localY);
    }

    public void StopWatching()
    {
        Hide();
        OverlayWindow.DetachOwner(this);
        _attached = false;
    }

    private void EnsureShown()
    {
        if (!_attached)
        {
            OverlayWindow.Attach(this);
            _attached = true;
        }

        if (!IsVisible)
            Show();
    }

    private void PlaceOverPhysicalRect(DrawingRect region)
    {
        GetDipScale(out double dpiX, out double dpiY);
        Left = region.X / dpiX;
        Top = region.Y / dpiY;
        Width = Math.Max(80, region.Width / dpiX);
        Height = Math.Max(48, region.Height / dpiY);
    }

    private void GetDipScale(out double dpiX, out double dpiY)
    {
        var source = PresentationSource.FromVisual(this);
        dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        if (dpiX <= 0) dpiX = 1;
        if (dpiY <= 0) dpiY = 1;
    }

    private string FormatDiagnostics(ExecutionStatusSnapshot snapshot, DrawingRect region)
    {
        var text = new StringBuilder(256);
        ColorDetectionResult? d = snapshot.Detection;
        if (d is not null)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"Target RGB({d.Target.R},{d.Target.G},{d.Target.B})  Actual RGB({d.Actual.R},{d.Actual.G},{d.Actual.B})\n");
            text.Append(CultureInfo.InvariantCulture,
                $"Distance {d.Distance:F2} / {d.Threshold:F2}  Match {d.MatchPercentage:P1}\n");
            if (d.BestMatchPoint is { } point)
                text.Append(CultureInfo.InvariantCulture, $"Best ({point.X},{point.Y})  ");
            text.Append(CultureInfo.InvariantCulture, $"Region ({region.X},{region.Y} {region.Width}×{region.Height})\n");
            AppendMonitorLine(text, d.BestMatchPoint ?? new System.Drawing.Point(region.X, region.Y));
            string source = d.CaptureSource.ToString().ToUpperInvariant();
            int fw = d.FrameWidth > 0 ? d.FrameWidth : region.Width;
            int fh = d.FrameHeight > 0 ? d.FrameHeight : region.Height;
            text.Append(CultureInfo.InvariantCulture,
                $"Capture {source} {fw}×{fh}  {d.PixelFormat}  {d.DetectionMethod}\n");
        }
        else
        {
            text.AppendLine(snapshot.Message ?? snapshot.Phase);
        }

        text.Append(CultureInfo.InvariantCulture,
            $"Checks {snapshot.Checks}  True {snapshot.ConsecutiveTrue}  False {snapshot.ConsecutiveFalse}\n");
        text.Append(CultureInfo.InvariantCulture,
            $"Elapsed {snapshot.Elapsed.TotalMilliseconds:F0} ms");
        if (d is not null)
            text.Append(CultureInfo.InvariantCulture, $"  Detect {d.DetectionTime.TotalMilliseconds:F1} ms");
        if (snapshot.Visual is not null)
            text.Append(CultureInfo.InvariantCulture, $"  {snapshot.Visual.Algorithm}");
        return text.ToString();
    }

    private void AppendMonitorLine(StringBuilder text, System.Drawing.Point point)
    {
        ScreenCoordinateMapper mapper = GetMapper();
        if (mapper.TryGetMonitorAt(point, out MonitorDescriptor monitor))
        {
            int dpi = (int)Math.Round(monitor.DpiScaleX * 96);
            text.Append(CultureInfo.InvariantCulture,
                $"Monitor {(monitor.IsPrimary ? "primary" : "secondary")} {monitor.Bounds.Width}×{monitor.Bounds.Height}  DPI {dpi} ({monitor.DpiScaleX:0.##}x)\n");
            return;
        }

        text.Append("Monitor unknown\n");
    }

    private ScreenCoordinateMapper GetMapper()
    {
        if (_mapper is not null && (DateTime.UtcNow - _mapperCachedAt) < TimeSpan.FromSeconds(2))
            return _mapper;

        try
        {
            _mapper = Win32VirtualDesktopGeometry.Query().CreateMapper();
        }
        catch (InvalidOperationException)
        {
            _mapper = new ScreenCoordinateMapper(new System.Drawing.Rectangle(0, 0, 1920, 1080));
        }

        _mapperCachedAt = DateTime.UtcNow;
        return _mapper;
    }
}
