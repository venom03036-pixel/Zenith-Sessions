using System.Globalization;

namespace MacroSuite.Core.AI;

/// <summary>
/// Maps a vision-model coordinate back to native virtual-desktop pixels.
/// Crop, scale, normalization, and optional (y, x) swap live here so the input
/// pipeline never special-cases a provider (Rule 8, Rule 9).
/// </summary>
public sealed record CaptureTransform(
    int NativeWidth,
    int NativeHeight,
    int CropX,
    int CropY,
    int CropW,
    int CropH,
    int SentWidth,
    int SentHeight,
    VisionCoordinateSpace Space,
    int? NormalizedMax = null,
    bool SwapXY = false)
{
    public double ScaleX => CropW <= 0 || SentWidth <= 0 ? 1.0 : SentWidth / (double)CropW;

    public double ScaleY => CropH <= 0 || SentHeight <= 0 ? 1.0 : SentHeight / (double)CropH;

    public static CaptureTransform FromCrop(
        int cropX,
        int cropY,
        int cropW,
        int cropH,
        int sentWidth,
        int sentHeight,
        VisionEncodePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new CaptureTransform(
            NativeWidth: cropW,
            NativeHeight: cropH,
            CropX: cropX,
            CropY: cropY,
            CropW: Math.Max(1, cropW),
            CropH: Math.Max(1, cropH),
            SentWidth: Math.Max(1, sentWidth),
            SentHeight: Math.Max(1, sentHeight),
            Space: plan.Space,
            NormalizedMax: plan.NormalizedMax,
            SwapXY: plan.SwapXY);
    }

    public (int x, int y) ToNative(double modelX, double modelY)
    {
        double mx = modelX;
        double my = modelY;
        if (SwapXY)
            (mx, my) = (my, mx);

        if (Space == VisionCoordinateSpace.Normalized)
        {
            int n = NormalizedMax.GetValueOrDefault(1000);
            if (n <= 0)
                n = 1000;
            mx = mx / n * CropW;
            my = my / n * CropH;
        }
        else
        {
            if (SentWidth > 0)
                mx *= CropW / (double)SentWidth;
            if (SentHeight > 0)
                my *= CropH / (double)SentHeight;
        }

        int x = (int)Math.Round(CropX + mx, MidpointRounding.AwayFromZero);
        int y = (int)Math.Round(CropY + my, MidpointRounding.AwayFromZero);
        int maxX = CropX + Math.Max(1, CropW) - 1;
        int maxY = CropY + Math.Max(1, CropH) - 1;
        x = Math.Clamp(x, CropX, maxX);
        y = Math.Clamp(y, CropY, maxY);
        return (x, y);
    }

    public string FormatLog(double? modelX, double? modelY, int? nativeX, int? nativeY)
    {
        string model = modelX.HasValue && modelY.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $" model=({modelX.Value},{modelY.Value})")
            : string.Empty;
        string native = nativeX.HasValue && nativeY.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $" native=({nativeX.Value},{nativeY.Value})")
            : string.Empty;
        string norm = NormalizedMax is { } n ? $" n={n}" : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"crop=({CropX},{CropY},{CropW},{CropH}) sent={SentWidth}x{SentHeight} space={Space}{norm} swapXY={SwapXY}{model}{native}");
    }
}
