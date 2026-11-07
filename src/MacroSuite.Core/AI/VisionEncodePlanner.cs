using MacroSuite.Core.Capture;

namespace MacroSuite.Core.AI;

/// <summary>
/// Pre-scale math so we send the size each provider actually uses internally.
/// Letting the provider silently resize is what makes click coordinates miss.
/// </summary>
public static class VisionEncodePlanner
{
    public static VisionEncodePlan Fit720p(int cropWidth, int cropHeight)
    {
        var fit = ImageDownscale.Fit(cropWidth, cropHeight);
        return VisionEncodePlan.PixelImage(fit.DestWidth, fit.DestHeight);
    }

    public static VisionEncodePlan OpenAiTiled(int cropWidth, int cropHeight, string? modelId = null)
    {
        if (IsOpenAiPatchesModel(modelId))
        {
            var patches = ImageDownscale.Fit(cropWidth, cropHeight, 768, 768);
            return VisionEncodePlan.PixelImage(patches.DestWidth, patches.DestHeight);
        }

        var (w, h) = OpenAiEffectiveSize(cropWidth, cropHeight);
        return VisionEncodePlan.PixelImage(w, h, detail: "high");
    }

    public static VisionEncodePlan Claude(int cropWidth, int cropHeight, string? modelId = null)
    {
        if (IsClaudeOpus47(modelId))
        {
            var fit = ImageDownscale.Fit(cropWidth, cropHeight, 2576, 2576);
            long pixels = (long)fit.DestWidth * fit.DestHeight;
            const long maxPixels = 3_750_000;
            if (pixels > maxPixels)
            {
                double s = Math.Sqrt(maxPixels / (double)pixels);
                int w = Math.Max(1, (int)Math.Floor(fit.DestWidth * s));
                int h = Math.Max(1, (int)Math.Floor(fit.DestHeight * s));
                return new VisionEncodePlan(w, h, VisionCoordinateSpace.PixelAbsolute);
            }

            return new VisionEncodePlan(fit.DestWidth, fit.DestHeight, VisionCoordinateSpace.PixelAbsolute);
        }

        var (cw, ch) = ClaudeStandardSize(cropWidth, cropHeight);
        return VisionEncodePlan.PixelImage(cw, ch);
    }

    public static VisionEncodePlan Gemini(int cropWidth, int cropHeight)
    {
        var fit = ImageDownscale.Fit(cropWidth, cropHeight, 1920, 1080);
        return new VisionEncodePlan(
            fit.DestWidth,
            fit.DestHeight,
            VisionCoordinateSpace.Normalized,
            NormalizedMax: 1000,
            SwapXY: true);
    }

    public static VisionEncodePlan NvidiaLlama(int cropWidth, int cropHeight)
    {
        var fit = ImageDownscale.Fit(cropWidth, cropHeight, 1120, 1120);
        return VisionEncodePlan.PixelImage(fit.DestWidth, fit.DestHeight);
    }

    public static VisionEncodePlan NvidiaNemotron(int cropWidth, int cropHeight)
    {
        var fit = ImageDownscale.Fit(cropWidth, cropHeight, 2048, 1536);
        return new VisionEncodePlan(
            fit.DestWidth,
            fit.DestHeight,
            VisionCoordinateSpace.Normalized,
            NormalizedMax: 1000,
            SwapXY: false);
    }

    public static (int w, int h) OpenAiEffectiveSize(int w, int h)
    {
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        double s1 = Math.Min(1.0, Math.Min(2048.0 / w, 2048.0 / h));
        int w1 = Math.Max(1, (int)Math.Round(w * s1));
        int h1 = Math.Max(1, (int)Math.Round(h * s1));

        double s2 = 768.0 / Math.Min(w1, h1);
        if (s2 > 1.0)
            s2 = 1.0;
        return (Math.Max(1, (int)Math.Round(w1 * s2)), Math.Max(1, (int)Math.Round(h1 * s2)));
    }

    public static (int w, int h) ClaudeStandardSize(int w, int h)
    {
        const int MaxEdge = 1568;
        const int MaxTokens = 1568;
        w = Math.Max(1, w);
        h = Math.Max(1, h);

        double scale = 1.0;
        if (w > MaxEdge || h > MaxEdge)
            scale = Math.Min((double)MaxEdge / w, (double)MaxEdge / h);

        int cw = Math.Max(1, (int)Math.Floor(w * scale));
        int ch = Math.Max(1, (int)Math.Floor(h * scale));
        int guard = 0;
        while (ClaudeTokens(cw, ch) > MaxTokens && guard < 250 && (cw > 28 || ch > 28))
        {
            scale *= 0.99;
            cw = Math.Max(1, (int)Math.Floor(w * scale));
            ch = Math.Max(1, (int)Math.Floor(h * scale));
            guard++;
        }

        return (cw, ch);
    }

    public static int ClaudeTokens(int width, int height)
        => (int)Math.Ceiling(Math.Max(1, width) / 28.0) * (int)Math.Ceiling(Math.Max(1, height) / 28.0);

    public static bool IsClaudeOpus47(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return false;
        string m = modelId.Trim();
        return m.Contains("opus-4-7", StringComparison.OrdinalIgnoreCase)
               || m.Contains("opus-4.7", StringComparison.OrdinalIgnoreCase)
               || m.Contains("opus_4_7", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOpenAiPatchesModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return false;
        string m = modelId.Trim();
        bool gpt5 = m.Contains("gpt-5", StringComparison.OrdinalIgnoreCase);
        bool tiny = m.Contains("mini", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("nano", StringComparison.OrdinalIgnoreCase);
        return gpt5 && tiny;
    }
}
