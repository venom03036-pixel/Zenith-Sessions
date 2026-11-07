using System.Drawing;
using MacroSuite.Core.Abstractions;

namespace MacroSuite.Core.AI;

/// <summary>
/// Native-resolution snap around a coarse AI click (Rule 8). Implementations live in
/// Infrastructure so the engine does not take an OpenCV dependency (Rule 2, Rule 3).
/// </summary>
public interface IClickRefiner
{
    Task<(int x, int y)> RefineAsync(
        IScreenCaptureService capture,
        int nativeX,
        int nativeY,
        Rectangle allowedRegion,
        CancellationToken ct = default);
}
