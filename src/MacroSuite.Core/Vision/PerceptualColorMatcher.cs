using System.Drawing;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Vision;

/// <summary>
/// Last visual-match evaluation, sampled for crash/debug diagnostics (not a hot-path log).
/// </summary>
public sealed class VisualMatchDiagnostics
{
    public string Mode { get; init; } = string.Empty;
    public string Algorithm { get; init; } = "CIE76";
    public int SimilarityPercent { get; init; }
    public double DeltaEThreshold { get; init; }
    public double SimilarityScore { get; init; }
    public bool Matched { get; init; }
    public int SampledPixels { get; init; }
    public int ComparedPixels { get; init; }
    public int RegionWidth { get; init; }
    public int RegionHeight { get; init; }
    public string? ReferenceHex { get; init; }
}

/// <summary>
/// Perceptual pixel and region matching with cached Lab reference, early rejection,
/// and deterministic spatially-distributed sampling.
/// </summary>
public static class PerceptualColorMatcher
{
    public static bool MatchPixel(
        CaptureFrame frame,
        int x,
        int y,
        in LabColor referenceLab,
        int similarityPercent,
        bool useCieDe2000,
        string? referenceHex = null)
        => MatchPixel(frame, x, y, in referenceLab, similarityPercent, useCieDe2000, referenceHex, out _);

    public static bool MatchPixel(
        CaptureFrame frame,
        int x,
        int y,
        in LabColor referenceLab,
        int similarityPercent,
        bool useCieDe2000,
        string? referenceHex,
        out VisualMatchDiagnostics diagnostics)
    {
        if (!TryReadRgb(frame, x, y, out byte r, out byte g, out byte b))
        {
            diagnostics = Fail("Pixel", similarityPercent, referenceHex);
            return false;
        }

        var live = SrgbLabConvert.ToLab(r, g, b);
        double threshold = SrgbLabConvert.SimilarityPercentToDeltaE76(similarityPercent);
        double delta = useCieDe2000
            ? SrgbLabConvert.DeltaE2000(in referenceLab, in live)
            : SrgbLabConvert.DeltaE76(in referenceLab, in live);

        bool matched = delta <= threshold;
        double score = DeltaEToSimilarityScore(delta);
        diagnostics = new VisualMatchDiagnostics
        {
            Mode = "Pixel",
            Algorithm = useCieDe2000 ? "CIEDE2000" : "CIE76",
            SimilarityPercent = similarityPercent,
            DeltaEThreshold = threshold,
            SimilarityScore = score,
            Matched = matched,
            SampledPixels = 1,
            ComparedPixels = 1,
            RegionWidth = 1,
            RegionHeight = 1,
            ReferenceHex = referenceHex
        };
        return matched;
    }

    public static bool TryReadRgb(CaptureFrame frame, int x, int y, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (frame.IsDisposed) return false;
        if ((uint)x >= (uint)frame.Width || (uint)y >= (uint)frame.Height) return false;

        int offset = frame.GetPixelOffset(x, y);
        ReadOnlySpan<byte> span = frame.Span;
        int bpp = CaptureFrame.GetBytesPerPixel(frame.Format);
        if ((uint)(offset + bpp) > (uint)span.Length) return false;

        switch (frame.Format)
        {
            case FramePixelFormat.Bgra32:
            case FramePixelFormat.Bgr24:
                b = span[offset];
                g = span[offset + 1];
                r = span[offset + 2];
                return true;
            case FramePixelFormat.Rgba32:
                r = span[offset];
                g = span[offset + 1];
                b = span[offset + 2];
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Neighborhood match: a pixel matches if enough surrounding pixels are within ΔE.
    /// Default required fraction is 55% (robust to anti-aliasing, not all-or-nothing).
    /// </summary>
    public static bool MatchNeighborhood(
        CaptureFrame frame,
        int cx,
        int cy,
        int neighborhoodSize,
        in LabColor referenceLab,
        int similarityPercent,
        bool useCieDe2000,
        double requiredFraction = 0.55,
        string? referenceHex = null)
        => MatchNeighborhood(
            frame, cx, cy, neighborhoodSize, in referenceLab, similarityPercent, useCieDe2000,
            requiredFraction, referenceHex, out _);

    public static bool MatchNeighborhood(
        CaptureFrame frame,
        int cx,
        int cy,
        int neighborhoodSize,
        in LabColor referenceLab,
        int similarityPercent,
        bool useCieDe2000,
        double requiredFraction,
        string? referenceHex,
        out VisualMatchDiagnostics diagnostics)
    {
        int size = neighborhoodSize <= 1 ? 1 : (neighborhoodSize | 1);
        if (size == 1)
            return MatchPixel(frame, cx, cy, in referenceLab, similarityPercent, useCieDe2000, referenceHex, out diagnostics);

        int half = size / 2;
        int total = 0;
        int matches = 0;
        double threshold = SrgbLabConvert.SimilarityPercentToDeltaE76(similarityPercent);
        int maxMismatches = (int)Math.Floor(size * size * (1.0 - Math.Clamp(requiredFraction, 0.1, 1.0)));

        for (int dy = -half; dy <= half; dy++)
        {
            for (int dx = -half; dx <= half; dx++)
            {
                int x = cx + dx;
                int y = cy + dy;
                if (!TryReadRgb(frame, x, y, out byte r, out byte g, out byte b))
                    continue;

                total++;
                var live = SrgbLabConvert.ToLab(r, g, b);
                double delta = useCieDe2000
                    ? SrgbLabConvert.DeltaE2000(in referenceLab, in live)
                    : SrgbLabConvert.DeltaE76(in referenceLab, in live);
                if (delta <= threshold)
                    matches++;
                else if ((total - matches) > maxMismatches)
                {
                    diagnostics = new VisualMatchDiagnostics
                    {
                        Mode = "Neighborhood",
                        Algorithm = useCieDe2000 ? "CIEDE2000" : "CIE76",
                        SimilarityPercent = similarityPercent,
                        DeltaEThreshold = threshold,
                        SimilarityScore = total == 0 ? 0 : 100.0 * matches / total,
                        Matched = false,
                        SampledPixels = total,
                        ComparedPixels = total,
                        RegionWidth = size,
                        RegionHeight = size,
                        ReferenceHex = referenceHex
                    };
                    return false;
                }
            }
        }

        bool matched = total > 0 && (matches / (double)total) >= requiredFraction;
        diagnostics = new VisualMatchDiagnostics
        {
            Mode = "Neighborhood",
            Algorithm = useCieDe2000 ? "CIEDE2000" : "CIE76",
            SimilarityPercent = similarityPercent,
            DeltaEThreshold = threshold,
            SimilarityScore = total == 0 ? 0 : 100.0 * matches / total,
            Matched = matched,
            SampledPixels = total,
            ComparedPixels = total,
            RegionWidth = size,
            RegionHeight = size,
            ReferenceHex = referenceHex
        };
        return matched;
    }

    /// <summary>
    /// Coarse scan of a captured rectangle for a target color (CIE76). Used by presence conditions.
    /// </summary>
    public static bool MatchColorPresent(
        CaptureFrame frame,
        in LabColor referenceLab,
        int similarityPercent,
        double requiredPixelFraction)
        => MatchColorPresent(frame, in referenceLab, similarityPercent, requiredPixelFraction, out _);

    public static bool MatchColorPresent(
        CaptureFrame frame,
        in LabColor referenceLab,
        int similarityPercent,
        double requiredPixelFraction,
        out VisualMatchDiagnostics diagnostics)
    {
        if (frame.IsDisposed || frame.Width <= 0 || frame.Height <= 0)
        {
            diagnostics = Fail("ColorPresent", similarityPercent, null);
            return false;
        }

        double threshold = SrgbLabConvert.SimilarityPercentToDeltaE76(similarityPercent);
        int stepX = Math.Max(1, frame.Width / 12);
        int stepY = Math.Max(1, frame.Height / 12);
        int compared = 0;
        int matches = 0;
        double required = Math.Clamp(requiredPixelFraction, 0.02, 1.0);

        for (int y = 0; y < frame.Height; y += stepY)
        {
            for (int x = 0; x < frame.Width; x += stepX)
            {
                if (!TryReadRgb(frame, x, y, out byte r, out byte g, out byte b))
                    continue;
                compared++;
                var live = SrgbLabConvert.ToLab(r, g, b);
                if (SrgbLabConvert.DeltaE76(in referenceLab, in live) <= threshold)
                    matches++;
            }
        }

        bool matched = compared > 0 && (matches / (double)compared) >= required;
        diagnostics = new VisualMatchDiagnostics
        {
            Mode = "ColorPresent",
            Algorithm = "CIE76",
            SimilarityPercent = similarityPercent,
            DeltaEThreshold = threshold,
            SimilarityScore = compared == 0 ? 0 : 100.0 * matches / compared,
            Matched = matched,
            SampledPixels = compared,
            ComparedPixels = compared,
            RegionWidth = frame.Width,
            RegionHeight = frame.Height
        };
        return matched;
    }

    /// <summary>
    /// Structural region match: spatially distributed samples, CIE76 ΔE per sample,
    /// required matching-pixel fraction (separate from color tolerance).
    /// </summary>
    public static bool MatchRegion(
        CaptureFrame candidate,
        int destX,
        int destY,
        byte[] templateBgra,
        int templateWidth,
        int templateHeight,
        int similarityPercent,
        double requiredPixelFraction,
        bool maximumAccuracy)
        => MatchRegion(
            candidate, destX, destY, templateBgra, templateWidth, templateHeight,
            similarityPercent, requiredPixelFraction, maximumAccuracy, out _);

    public static bool MatchRegion(
        CaptureFrame candidate,
        int destX,
        int destY,
        byte[] templateBgra,
        int templateWidth,
        int templateHeight,
        int similarityPercent,
        double requiredPixelFraction,
        bool maximumAccuracy,
        out VisualMatchDiagnostics diagnostics)
    {
        if (candidate.IsDisposed || templateBgra.Length < templateWidth * templateHeight * 4)
        {
            diagnostics = Fail("Region", similarityPercent, null);
            return false;
        }

        if (destX < 0 || destY < 0 ||
            destX + templateWidth > candidate.Width ||
            destY + templateHeight > candidate.Height)
        {
            diagnostics = Fail("Region", similarityPercent, null);
            return false;
        }

        int sampleCount = ChooseSampleCount(templateWidth, templateHeight, maximumAccuracy);
        GetSampleGrid(templateWidth, templateHeight, sampleCount, out int gridX, out int gridY);
        double threshold = SrgbLabConvert.SimilarityPercentToDeltaE76(similarityPercent);
        double required = Math.Clamp(requiredPixelFraction, 0.5, 1.0);
        int total = gridX * gridY;
        int allowedMismatches = (int)Math.Floor(total * (1.0 - required));
        int matches = 0;
        int compared = 0;

        // Stage 1 — cheap rejection: corners + center
        if (!CoarseAccept(candidate, destX, destY, templateBgra, templateWidth, templateHeight, threshold))
        {
            diagnostics = new VisualMatchDiagnostics
            {
                Mode = "Region",
                Algorithm = "CIE76",
                SimilarityPercent = similarityPercent,
                DeltaEThreshold = threshold,
                SimilarityScore = 0,
                Matched = false,
                SampledPixels = 5,
                ComparedPixels = 5,
                RegionWidth = templateWidth,
                RegionHeight = templateHeight
            };
            return false;
        }

        for (int gy = 0; gy < gridY; gy++)
        {
            int ty = gridY == 1 ? 0 : gy * (templateHeight - 1) / (gridY - 1);
            for (int gx = 0; gx < gridX; gx++)
            {
                int tx = gridX == 1 ? 0 : gx * (templateWidth - 1) / (gridX - 1);
                int tOff = ((ty * templateWidth) + tx) * 4;
                byte tb = templateBgra[tOff];
                byte tg = templateBgra[tOff + 1];
                byte tr = templateBgra[tOff + 2];
                var refLab = SrgbLabConvert.ToLab(tr, tg, tb);

                if (!TryReadRgb(candidate, destX + tx, destY + ty, out byte cr, out byte cg, out byte cb))
                    continue;

                compared++;
                var live = SrgbLabConvert.ToLab(cr, cg, cb);
                if (SrgbLabConvert.DeltaE76(in refLab, in live) <= threshold)
                    matches++;
                else if ((compared - matches) > allowedMismatches)
                {
                    diagnostics = RegionDiag(similarityPercent, threshold, matches, compared, false, templateWidth, templateHeight);
                    return false;
                }
            }
        }

        bool matched = compared > 0 && (matches / (double)compared) >= required;
        if (!matched)
        {
            diagnostics = RegionDiag(similarityPercent, threshold, matches, compared, false, templateWidth, templateHeight);
            return false;
        }

        // CIE76 passed — optional CIEDE2000 + structure verification (not every pixel).
        if (maximumAccuracy)
        {
            if (!VerifyCieDe2000Subset(candidate, destX, destY, templateBgra, templateWidth, templateHeight, threshold)
                || !VerifyStructure(candidate, destX, destY, templateBgra, templateWidth, templateHeight))
            {
                VisualMatchDiagnostics failed = RegionDiag(similarityPercent, threshold, matches, compared, false, templateWidth, templateHeight);
                diagnostics = new VisualMatchDiagnostics
                {
                    Mode = "Region",
                    Algorithm = "CIEDE2000",
                    SimilarityPercent = similarityPercent,
                    DeltaEThreshold = threshold,
                    SimilarityScore = failed.SimilarityScore,
                    Matched = false,
                    SampledPixels = compared,
                    ComparedPixels = compared,
                    RegionWidth = templateWidth,
                    RegionHeight = templateHeight
                };
                return false;
            }
        }

        diagnostics = RegionDiag(similarityPercent, threshold, matches, compared, true, templateWidth, templateHeight);
        return true;
    }

    public static byte[] CopyRegionBgra(CaptureFrame frame)
    {
        int w = frame.Width;
        int h = frame.Height;
        var dest = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (!TryReadRgb(frame, x, y, out byte r, out byte g, out byte b))
                    continue;
                int o = ((y * w) + x) * 4;
                dest[o] = b;
                dest[o + 1] = g;
                dest[o + 2] = r;
                dest[o + 3] = 255;
            }
        }
        return dest;
    }

    public static Rectangle InflateClamped(Rectangle region, int radius, int screenW, int screenH)
    {
        int x = Math.Max(0, region.X - radius);
        int y = Math.Max(0, region.Y - radius);
        int r = Math.Min(screenW, region.Right + radius);
        int b = Math.Min(screenH, region.Bottom + radius);
        return new Rectangle(x, y, Math.Max(1, r - x), Math.Max(1, b - y));
    }

    private static bool CoarseAccept(
        CaptureFrame candidate,
        int destX,
        int destY,
        byte[] template,
        int tw,
        int th,
        double threshold)
    {
        ReadOnlySpan<(int x, int y)> probes =
        [
            (0, 0),
            (tw - 1, 0),
            (0, th - 1),
            (tw - 1, th - 1),
            (tw / 2, th / 2)
        ];

        int hits = 0;
        foreach (var (px, py) in probes)
        {
            int tOff = ((py * tw) + px) * 4;
            var refLab = SrgbLabConvert.ToLab(template[tOff + 2], template[tOff + 1], template[tOff]);
            if (!TryReadRgb(candidate, destX + px, destY + py, out byte r, out byte g, out byte b))
                continue;
            var live = SrgbLabConvert.ToLab(r, g, b);
            if (SrgbLabConvert.DeltaE76(in refLab, in live) <= threshold * 1.5)
                hits++;
        }

        return hits >= 3;
    }

    private static bool VerifyCieDe2000Subset(
        CaptureFrame candidate,
        int destX,
        int destY,
        byte[] template,
        int tw,
        int th,
        double threshold76)
    {
        double threshold2000 = threshold76 * 1.15;
        int gx = Math.Min(4, tw);
        int gy = Math.Min(4, th);
        int ok = 0;
        int n = 0;
        for (int iy = 0; iy < gy; iy++)
        {
            int ty = gy == 1 ? 0 : iy * (th - 1) / (gy - 1);
            for (int ix = 0; ix < gx; ix++)
            {
                int tx = gx == 1 ? 0 : ix * (tw - 1) / (gx - 1);
                int tOff = ((ty * tw) + tx) * 4;
                var refLab = SrgbLabConvert.ToLab(template[tOff + 2], template[tOff + 1], template[tOff]);
                if (!TryReadRgb(candidate, destX + tx, destY + ty, out byte r, out byte g, out byte b))
                    continue;
                n++;
                var live = SrgbLabConvert.ToLab(r, g, b);
                if (SrgbLabConvert.DeltaE2000(in refLab, in live) <= threshold2000)
                    ok++;
            }
        }

        return n > 0 && ok / (double)n >= 0.75;
    }

    private static bool VerifyStructure(
        CaptureFrame candidate,
        int destX,
        int destY,
        byte[] template,
        int tw,
        int th)
    {
        long tBright = 0, cBright = 0;
        int tEdge = 0, cEdge = 0;
        int samples = 0;
        int stepX = Math.Max(1, tw / 8);
        int stepY = Math.Max(1, th / 8);
        for (int y = 0; y < th; y += stepY)
        {
            for (int x = 0; x < tw; x += stepX)
            {
                int tOff = ((y * tw) + x) * 4;
                int tb = template[tOff] + template[tOff + 1] + template[tOff + 2];
                if (!TryReadRgb(candidate, destX + x, destY + y, out byte r, out byte g, out byte b))
                    continue;
                int cb = r + g + b;
                tBright += tb;
                cBright += cb;
                samples++;
                if (x + stepX < tw)
                {
                    int tOff2 = ((y * tw) + x + stepX) * 4;
                    int tb2 = template[tOff2] + template[tOff2 + 1] + template[tOff2 + 2];
                    if (TryReadRgb(candidate, destX + x + stepX, destY + y, out byte r2, out byte g2, out byte b2))
                    {
                        int tSign = Math.Sign(tb2 - tb);
                        int cSign = Math.Sign((r2 + g2 + b2) - cb);
                        if (tSign == cSign) tEdge++;
                        cEdge++;
                    }
                }
            }
        }

        if (samples == 0) return false;
        double tMean = tBright / (double)samples;
        double cMean = cBright / (double)samples;
        if (tMean < 1) tMean = 1;
        double brightRatio = cMean / tMean;
        if (brightRatio < 0.82 || brightRatio > 1.22)
            return false;
        if (cEdge == 0) return true;
        return tEdge / (double)cEdge >= 0.65;
    }

    /// <summary>
    /// Coarse-to-fine screen/nearby search: 4× downsample, brightness candidates, then full MatchRegion.
    /// </summary>
    public static bool FindRegion(
        CaptureFrame haystack,
        byte[] templateBgra,
        int templateWidth,
        int templateHeight,
        int similarityPercent,
        double requiredPixelFraction,
        bool maximumAccuracy,
        CancellationToken ct)
        => FindRegion(
            haystack, templateBgra, templateWidth, templateHeight, similarityPercent,
            requiredPixelFraction, maximumAccuracy, ct, out _);

    public static bool FindRegion(
        CaptureFrame haystack,
        byte[] templateBgra,
        int templateWidth,
        int templateHeight,
        int similarityPercent,
        double requiredPixelFraction,
        bool maximumAccuracy,
        CancellationToken ct,
        out VisualMatchDiagnostics diagnostics)
    {
        diagnostics = Fail("Region", similarityPercent, null);
        if (haystack.Width < templateWidth || haystack.Height < templateHeight)
            return false;

        const int scale = 4;
        int step = Math.Max(scale, Math.Min(templateWidth, templateHeight) / 6);
        var candidates = new List<(int x, int y, int score)>(24);
        int tMean = MeanBrightness(templateBgra, templateWidth, templateHeight);

        for (int y = 0; y <= haystack.Height - templateHeight; y += step)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x <= haystack.Width - templateWidth; x += step)
            {
                if (!TryReadRgb(haystack, x + (templateWidth / 2), y + (templateHeight / 2), out byte r, out byte g, out byte b))
                    continue;
                int cMean = r + g + b;
                int diff = Math.Abs(cMean - tMean);
                if (diff > 90)
                    continue;
                InsertCandidate(candidates, x, y, 90 - diff, 20);
            }
        }

        if (candidates.Count == 0)
        {
            int sparse = Math.Max(step, 8);
            for (int y = 0; y <= haystack.Height - templateHeight; y += sparse)
            {
                ct.ThrowIfCancellationRequested();
                for (int x = 0; x <= haystack.Width - templateWidth; x += sparse)
                {
                    if (MatchRegion(haystack, x, y, templateBgra, templateWidth, templateHeight,
                            similarityPercent, requiredPixelFraction, maximumAccuracy, out diagnostics))
                        return true;
                }
            }

            return false;
        }

        foreach (var (x, y, _) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (MatchRegion(haystack, x, y, templateBgra, templateWidth, templateHeight,
                    similarityPercent, requiredPixelFraction, maximumAccuracy, out diagnostics))
                return true;
        }

        return false;
    }

    private static int MeanBrightness(byte[] bgra, int w, int h)
    {
        long sum = 0;
        int n = 0;
        int sx = Math.Max(1, w / 6);
        int sy = Math.Max(1, h / 6);
        for (int y = 0; y < h; y += sy)
        {
            for (int x = 0; x < w; x += sx)
            {
                int o = ((y * w) + x) * 4;
                sum += bgra[o] + bgra[o + 1] + bgra[o + 2];
                n++;
            }
        }

        return n == 0 ? 0 : (int)(sum / n);
    }

    private static void InsertCandidate(List<(int x, int y, int score)> list, int x, int y, int score, int cap)
    {
        if (list.Count == cap && score <= list[^1].score)
            return;
        list.Add((x, y, score));
        list.Sort(static (a, b) => b.score.CompareTo(a.score));
        if (list.Count > cap)
            list.RemoveAt(list.Count - 1);
    }

    private static int ChooseSampleCount(int w, int h, bool maximumAccuracy)
    {
        int pixels = w * h;
        if (pixels <= 1024)
            return Math.Max(w * h, 16);
        if (maximumAccuracy)
            return Math.Min(pixels, 4096);
        if (pixels <= 10000)
            return 256;
        return 512;
    }

    private static void GetSampleGrid(int width, int height, int sampleCount, out int gridX, out int gridY)
    {
        int target = Math.Max(4, sampleCount);
        gridX = Math.Max(2, (int)Math.Round(Math.Sqrt(target * (width / (double)Math.Max(1, height)))));
        gridY = Math.Max(2, target / gridX);
        gridX = Math.Min(gridX, width);
        gridY = Math.Min(gridY, height);
    }

    private static double DeltaEToSimilarityScore(double deltaE)
    {
        // Inverse of SimilarityPercentToDeltaE76, clamped to 0–100.
        double pct = 100.0 - (deltaE * 3.0);
        return Math.Clamp(pct, 0, 100);
    }

    private static VisualMatchDiagnostics Fail(string mode, int pct, string? hex) => new()
    {
        Mode = mode,
        Algorithm = "CIE76",
        SimilarityPercent = pct,
        Matched = false,
        ReferenceHex = hex
    };

    private static VisualMatchDiagnostics RegionDiag(
        int pct, double threshold, int matches, int compared, bool matched, int w, int h) => new()
    {
        Mode = "Region",
        Algorithm = "CIE76",
        SimilarityPercent = pct,
        DeltaEThreshold = threshold,
        SimilarityScore = compared == 0 ? 0 : 100.0 * matches / compared,
        Matched = matched,
        SampledPixels = compared,
        ComparedPixels = compared,
        RegionWidth = w,
        RegionHeight = h
    };
}
