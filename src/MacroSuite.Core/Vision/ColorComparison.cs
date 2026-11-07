using System.Drawing;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Vision;

public readonly record struct PixelColor(byte R, byte G, byte B, byte A = 255)
{
    public static PixelColor FromFrame(ReadOnlySpan<byte> pixels, int offset, FramePixelFormat format)
    {
        int bytesPerPixel = CaptureFrame.GetBytesPerPixel(format);
        if ((uint)(offset + bytesPerPixel) > (uint)pixels.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));

        return format switch
        {
            FramePixelFormat.Bgra32 => new PixelColor(
                pixels[offset + 2], pixels[offset + 1], pixels[offset], pixels[offset + 3]),
            FramePixelFormat.Bgr24 => new PixelColor(
                pixels[offset + 2], pixels[offset + 1], pixels[offset]),
            FramePixelFormat.Rgba32 => new PixelColor(
                pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported pixel format.")
        };
    }

    public HsvColor ToHsv() => ColorComparison.ToHsv(this);

    public LabColor ToLab() => SrgbLabConvert.ToLab(R, G, B);
}

public readonly record struct HsvColor(double H, double S, double V);

public enum ColorSpace
{
    Rgb = 0,
    Hsv = 1,
    Lab = 2
}

/// <summary>
/// Canonical match-mode name from the reliability plan. Numeric values match
/// <see cref="ColorComparisonMode"/> so existing JSON continues to round-trip.
/// </summary>
public enum ColorMatchMode
{
    Rgb = 0,
    Hsv = 1,
    Lab = 2,
    LabDe2000 = 3
}

public enum ColorComparisonMode
{
    Rgb = 0,
    Hsv = 1,
    Cie76 = 2,
    CieDe2000 = 3
}

public sealed record ColorMatchSettings
{
    public ColorComparisonMode Mode { get; init; } = ColorComparisonMode.Rgb;
    public double Tolerance { get; init; } = 20;
    public double MinimumMatchPercentage { get; init; } = 0.05;
    public int SampleStride { get; init; } = 1;
    public IReadOnlyList<PixelColor>? AdditionalTargets { get; init; }
    public bool UseSimd { get; init; } = true;
}

public sealed record ColorDetectionResult
{
    public bool Matched { get; init; }
    public double Distance { get; init; }
    public double Threshold { get; init; }
    public PixelColor Actual { get; init; }
    public PixelColor Target { get; init; }
    public double Confidence { get; init; }
    public double MatchPercentage { get; init; }
    public int MatchingPixels { get; init; }
    public int SampledPixels { get; init; }
    public Rectangle? MatchedRegion { get; init; }
    public Point? BestMatchPoint { get; init; }
    public TimeSpan DetectionTime { get; init; }
    public string DetectionMethod { get; init; } = nameof(ColorComparisonMode.Rgb);
    public string PixelFormat { get; init; } = string.Empty;
    public Rectangle CaptureRegion { get; init; }
    public PixelColor DominantColor { get; init; }
    public Rectangle? MatchingBounds { get; init; }
    public CaptureSource CaptureSource { get; init; }
    public int FrameWidth { get; init; }
    public int FrameHeight { get; init; }
    public string? Error { get; init; }
}

public static class ColorComparison
{
    public static ColorComparisonMode ToComparisonMode(ColorMatchMode mode) => (ColorComparisonMode)(int)mode;

    public static ColorSpace SpaceFor(ColorComparisonMode mode) => mode switch
    {
        ColorComparisonMode.Hsv => ColorSpace.Hsv,
        ColorComparisonMode.Cie76 or ColorComparisonMode.CieDe2000 => ColorSpace.Lab,
        _ => ColorSpace.Rgb
    };

    public static ColorDetectionResult CompareColor(
        PixelColor actual,
        PixelColor target,
        ColorMatchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        double threshold = GetThreshold(settings.Mode, settings.Tolerance);
        double distance = GetDistance(actual, target, settings.Mode);
        double confidence = threshold <= 0
            ? (distance <= 0 ? 1 : 0)
            : Math.Clamp(1 - (distance / threshold), 0, 1);

        return new ColorDetectionResult
        {
            Matched = distance <= threshold,
            Distance = distance,
            Threshold = threshold,
            Actual = actual,
            Target = target,
            Confidence = confidence,
            MatchPercentage = distance <= threshold ? 1 : 0,
            MatchingPixels = distance <= threshold ? 1 : 0,
            SampledPixels = 1,
            DetectionMethod = settings.Mode.ToString()
        };
    }

    public static double GetDistance(PixelColor actual, PixelColor target, ColorComparisonMode mode)
        => mode switch
        {
            ColorComparisonMode.Hsv => HsvDistance(ToHsv(actual), ToHsv(target)),
            ColorComparisonMode.Cie76 => SrgbLabConvert.DeltaE76(
                SrgbLabConvert.ToLab(actual.R, actual.G, actual.B),
                SrgbLabConvert.ToLab(target.R, target.G, target.B)),
            ColorComparisonMode.CieDe2000 => SrgbLabConvert.DeltaE2000(
                SrgbLabConvert.ToLab(actual.R, actual.G, actual.B),
                SrgbLabConvert.ToLab(target.R, target.G, target.B)),
            _ => Math.Max(
                Math.Abs(actual.R - target.R),
                Math.Max(Math.Abs(actual.G - target.G), Math.Abs(actual.B - target.B)))
        };

    public static HsvColor ToHsv(PixelColor color)
    {
        double r = color.R / 255d;
        double g = color.G / 255d;
        double b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double hue = 0;
        if (delta > 0)
        {
            if (max == r)
                hue = 60 * (((g - b) / delta) % 6);
            else if (max == g)
                hue = 60 * (((b - r) / delta) + 2);
            else
                hue = 60 * (((r - g) / delta) + 4);
        }

        if (hue < 0)
            hue += 360;
        double saturation = max <= 0 ? 0 : delta / max;
        return new HsvColor(hue, saturation, max);
    }

    private static double HsvDistance(HsvColor a, HsvColor b)
    {
        double hue = Math.Abs(a.H - b.H);
        hue = Math.Min(hue, 360 - hue) / 180d;
        double saturation = a.S - b.S;
        double value = a.V - b.V;
        return Math.Sqrt((hue * hue) + (saturation * saturation) + (value * value)) * 100d;
    }

    public static double GetThreshold(ColorComparisonMode mode, double tolerance)
    {
        tolerance = Math.Max(0, tolerance);
        return mode switch
        {
            ColorComparisonMode.Rgb => Math.Min(255, tolerance),
            ColorComparisonMode.Hsv => Math.Min(100, tolerance),
            _ => Math.Min(100, tolerance)
        };
    }
}
