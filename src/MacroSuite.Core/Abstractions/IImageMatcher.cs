using System.Drawing;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Abstractions;

/// <summary>
/// Abstraction for template matching algorithms (e.g. OpenCV).
/// Isolated behind interface per Rule 3 &amp; Rule 9 so the engine does not depend directly on native libraries.
/// </summary>
public interface IImageMatcher : IDisposable
{
    /// <summary>
    /// Searches for the specified template file within <paramref name="frame"/>.
    /// </summary>
    Task<MatchResult> MatchAsync(
        CaptureFrame frame,
        string templatePath,
        double minConfidence = 0.8,
        Rectangle? searchRegion = null,
        CancellationToken ct = default);

    /// <summary>
    /// Searches for the specified template image bytes within <paramref name="frame"/>.
    /// </summary>
    Task<MatchResult> MatchAsync(
        CaptureFrame frame,
        byte[] templateImageBytes,
        double minConfidence = 0.8,
        Rectangle? searchRegion = null,
        CancellationToken ct = default);
}
