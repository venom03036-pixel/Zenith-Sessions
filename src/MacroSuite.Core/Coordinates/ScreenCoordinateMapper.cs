using System.Drawing;
using MacroSuite.Core.Abstractions;

namespace MacroSuite.Core.Coordinates;

/// <summary>
/// Explicit conversion between WPF device-independent pixels and the physical virtual-desktop
/// pixel coordinates used by screen capture and input. Capture and screen coordinates are
/// intentionally identical in the current backends.
/// </summary>
public sealed class ScreenCoordinateMapper
{
    public Rectangle VirtualDesktopBounds { get; }

    public IReadOnlyList<MonitorDescriptor> Monitors { get; }

    public ScreenCoordinateMapper(Rectangle virtualDesktopBounds, IReadOnlyList<MonitorDescriptor>? monitors = null)
    {
        if (virtualDesktopBounds.Width <= 0 || virtualDesktopBounds.Height <= 0)
            throw new ArgumentException("Virtual desktop bounds must be positive.", nameof(virtualDesktopBounds));
        VirtualDesktopBounds = virtualDesktopBounds;
        Monitors = monitors ?? Array.Empty<MonitorDescriptor>();
    }

    public ScreenCoordinateMapper(IVirtualDesktopGeometry geometry)
        : this(
            (geometry ?? throw new ArgumentNullException(nameof(geometry))).VirtualDesktopBounds,
            geometry.Monitors)
    {
    }

    public Point CaptureToScreenPoint(Point capturePoint) => capturePoint;

    public Point ScreenToCapturePoint(Point screenPoint) => screenPoint;

    public Point DipToScreenPoint(double xDip, double yDip, double scaleX, double scaleY)
    {
        ValidateScale(scaleX, nameof(scaleX));
        ValidateScale(scaleY, nameof(scaleY));
        return new Point(
            checked((int)Math.Round(xDip * scaleX, MidpointRounding.AwayFromZero)),
            checked((int)Math.Round(yDip * scaleY, MidpointRounding.AwayFromZero)));
    }

    public PointF ScreenToDipPoint(Point screenPoint, double scaleX, double scaleY)
    {
        ValidateScale(scaleX, nameof(scaleX));
        ValidateScale(scaleY, nameof(scaleY));
        return new PointF(
            (float)(screenPoint.X / scaleX),
            (float)(screenPoint.Y / scaleY));
    }

    public Rectangle ClampCaptureRegion(Rectangle requested)
        => Rectangle.Intersect(requested, VirtualDesktopBounds);

    public bool TryGetMonitorAt(Point screenPoint, out MonitorDescriptor monitor)
    {
        IReadOnlyList<MonitorDescriptor> monitors = Monitors;
        for (int i = 0; i < monitors.Count; i++)
        {
            MonitorDescriptor candidate = monitors[i];
            if (candidate.Bounds.Contains(screenPoint))
            {
                monitor = candidate;
                return true;
            }
        }

        monitor = default;
        return false;
    }

    public double GetDpiScaleXAt(Point screenPoint)
        => TryGetMonitorAt(screenPoint, out MonitorDescriptor monitor) ? monitor.DpiScaleX : 1.0;

    public double GetDpiScaleYAt(Point screenPoint)
        => TryGetMonitorAt(screenPoint, out MonitorDescriptor monitor) ? monitor.DpiScaleY : 1.0;

    private static void ValidateScale(double scale, string parameterName)
    {
        if (!double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "DPI scale must be finite and positive.");
    }
}
