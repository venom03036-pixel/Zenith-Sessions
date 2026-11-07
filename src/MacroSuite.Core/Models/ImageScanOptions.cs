using System.Drawing;

namespace MacroSuite.Core.Models;

public enum ImageSearchScope { Legacy = 0, PlacedArea = 1, TargetWindow = 2, Anywhere = 3 }
public enum ImageScanResolution { Original = 0, P480 = 480, P720 = 720, P1080 = 1080 }
public enum ImageMatchHandling { Single = 0, All = 1 }

/// <summary>Explicit image-search choices. Null on ImageCondition preserves older macro behavior.</summary>
public sealed record ImageScanOptions
{
    public ImageSearchScope SearchScope { get; init; } = ImageSearchScope.Legacy;
    public Rectangle? MonitorBounds { get; init; }
    public ImageScanResolution Resolution { get; init; } = ImageScanResolution.Original;
    public ImageMatchHandling MatchHandling { get; init; } = ImageMatchHandling.Single;

    public static ImageScanOptions ForPlacedArea() => new()
    {
        SearchScope = ImageSearchScope.PlacedArea,
        MatchHandling = ImageMatchHandling.All
    };
}
