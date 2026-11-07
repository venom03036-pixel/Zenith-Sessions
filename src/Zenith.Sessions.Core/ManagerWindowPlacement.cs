namespace Zenith.Sessions.Core;

/// <summary>
/// Where the manager's own window was left: its normal (not maximized) bounds in physical virtual-desktop pixels, which stay
/// correct across displays with different scaling, and whether it was maximized. Client windows are separate (WindowLayout).
/// </summary>
public sealed record ManagerWindowPlacement(int Left, int Top, int Width, int Height, bool Maximized = false)
{
    // Windows keeps the virtual desktop well inside this range; anything outside is a damaged or hand-edited value.
    private const int CoordinateLimit = 1 << 16;
    private const double MinimumScale = 0.5, MaximumScale = 5;
    public PixelRectangle Bounds => new(Left, Top, Width, Height);
    public bool IsValid => Width is > 0 and <= CoordinateLimit && Height is > 0 and <= CoordinateLimit
        && Left is >= -CoordinateLimit and <= CoordinateLimit && Top is >= -CoordinateLimit and <= CoordinateLimit;
    public ManagerWindowPlacement Validated() => IsValid ? this : throw new InvalidDataException("The saved manager window position is invalid.");

    /// <summary>
    /// Converts the window's normal bounds in device-independent units to physical pixels with the scale of the display it is on.
    /// Null when there are no bounds (an empty or non-finite rectangle) or the result is out of range.
    /// </summary>
    public static ManagerWindowPlacement? FromLogical(double left, double top, double width, double height, double scaleX, double scaleY, bool maximized)
    {
        if (!double.IsFinite(scaleX) || !double.IsFinite(scaleY) || scaleX is < MinimumScale or > MaximumScale || scaleY is < MinimumScale or > MaximumScale)
            return null;
        double x = Math.Round(left * scaleX), y = Math.Round(top * scaleY), w = Math.Round(width * scaleX), h = Math.Round(height * scaleY);
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(w) || !double.IsFinite(h)
            || Math.Abs(x) > CoordinateLimit || Math.Abs(y) > CoordinateLimit || w is < 1 or > CoordinateLimit || h is < 1 or > CoordinateLimit) return null;
        return new((int)x, (int)y, (int)w, (int)h, maximized);
    }
}

public static class ManagerWindowPlacementPolicy
{
    /// <summary>Height of the manager's header in device-independent units; the window is dragged only by it.</summary>
    public const double HeaderDip = 44;
    private const double GripWidthDip = 120, GripHeightDip = 20;

    /// <summary>
    /// The saved placement when it still suits the current displays, or null to open at the default size centred on screen.
    /// On the display that shows its header it must be at least the window's minimum size at that display's scale, and at least
    /// 120 by 20 DIPs of the header must lie on that display's work area, so a disconnected, rearranged or rescaled display can
    /// never reopen the window out of reach or below its minimum size.
    /// </summary>
    public static ManagerWindowPlacement? Resolve(ManagerWindowPlacement? saved, IReadOnlyList<ClientDisplay> displays, double minimumWidthDip, double minimumHeightDip)
    {
        if (saved is null || !saved.IsValid) return null;
        foreach (var display in displays)
        {
            if (!display.IsValid) continue;
            double scale = display.ScalePercent / 100d;
            if (saved.Width < Math.Floor(minimumWidthDip * scale) || saved.Height < Math.Floor(minimumHeightDip * scale)) continue;
            var work = display.WorkArea;
            long header = Math.Min((long)Math.Round(HeaderDip * scale), saved.Height);
            long gripWidth = Math.Min((long)saved.Left + saved.Width, (long)work.X + work.Width) - Math.Max(saved.Left, work.X);
            long gripHeight = Math.Min((long)saved.Top + header, (long)work.Y + work.Height) - Math.Max(saved.Top, work.Y);
            if (gripWidth >= GripWidthDip * scale && gripHeight >= GripHeightDip * scale) return saved;
        }
        return null;
    }
}
