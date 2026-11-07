using MacroSuite.Core.Models;

namespace MacroSuite.Core.Abstractions;

/// <summary>Creates an owned, bounded scanner without changing legacy single-match semantics.</summary>
public interface IImageScanProvider
{
    IImageScanSession CreateScanSession(IScreenCaptureService capture);
}

public interface IImageScanSession : IDisposable
{
    Task<IReadOnlyList<ImageScanResult>> ScanAsync(IReadOnlyList<ImageScanRequest> requests, CancellationToken ct);
    Task<ImageScanHit?> RevalidateAsync(ImageScanRequest request, ImageScanHit hit, CancellationToken ct);
    ImageScanPerformanceSnapshot Performance { get; }
}
