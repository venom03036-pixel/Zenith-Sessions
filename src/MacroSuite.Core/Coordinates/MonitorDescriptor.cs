using System.Drawing;

namespace MacroSuite.Core.Coordinates;

/// <summary>
/// Physical bounds and DPI of one display in virtual-desktop coordinates.
/// Capture and input stay in physical pixels; DIP conversion happens only at WPF boundaries.
/// </summary>
public readonly record struct MonitorDescriptor(
    Rectangle Bounds,
    Rectangle WorkArea,
    double DpiScaleX,
    double DpiScaleY,
    bool IsPrimary);
