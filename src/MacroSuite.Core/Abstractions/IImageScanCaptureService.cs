using System.Drawing;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Abstractions;

/// <summary>Optional sampled capture, scheduled by the same owner as native capture.</summary>
public interface IImageScanCaptureService
{
    bool SupportsImageScanCapture => true;
    Task<ImageScanFrame> CaptureImageAsync(Rectangle? region, ImageScanResolution resolution, CancellationToken ct = default);
}
