using System.Windows;
using MacroSuite.Infrastructure.Windows;
using DrawingRect = System.Drawing.Rectangle;

namespace MacroSuite.App.Overlays;

/// <summary>Physical screen coordinates become local DIPs only at the overlay boundary.</summary>
public static class SearchRegionPreviewGeometry
{
    public static DrawingRect ClipToDesktop(DrawingRect region, DrawingRect desktop)
    {
        Win32WindowPlacement.ValidateBounds(region);
        Win32WindowPlacement.ValidateBounds(desktop);
        DrawingRect clipped = DrawingRect.Intersect(region, desktop);
        if (clipped.Width <= 0 || clipped.Height <= 0)
            throw new ArgumentException("This search area is outside the connected displays. Choose an on-screen area.", nameof(region));
        return clipped;
    }

    public static Rect ToLocalDips(DrawingRect region, DrawingRect monitor, double scaleX, double scaleY)
    {
        Win32WindowPlacement.ValidateBounds(region);
        Win32WindowPlacement.ValidateBounds(monitor);
        if (!double.IsFinite(scaleX) || scaleX <= 0)
            throw new ArgumentOutOfRangeException(nameof(scaleX));
        if (!double.IsFinite(scaleY) || scaleY <= 0)
            throw new ArgumentOutOfRangeException(nameof(scaleY));

        // Do not clip to this monitor: drawing the whole rectangle and clipping the visual
        // avoids inventing a border where a search area crosses a monitor seam.
        return new Rect(((double)region.X - monitor.X) / scaleX,
            ((double)region.Y - monitor.Y) / scaleY,
            region.Width / scaleX, region.Height / scaleY);
    }
}
