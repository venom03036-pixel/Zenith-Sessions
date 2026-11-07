using System.Drawing;
using System.Diagnostics;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Vision;

/// <summary>
/// High-performance, zero-allocation color detection algorithms on raw frame buffers (Rule 7).
/// Uses pure integer arithmetic on <see cref="ReadOnlySpan{T}"/>.
/// </summary>
public static class ColorDetector
{
    public const int DefaultMaximumSamples = 250_000;

    [ThreadStatic]
    private static PixelColor[]? t_singleTarget;

    private static PixelColor[] ResolveTargets(PixelColor primary, IReadOnlyList<PixelColor>? additional)
    {
        if (additional is null || additional.Count == 0)
        {
            PixelColor[] slot = t_singleTarget ??= new PixelColor[1];
            slot[0] = primary;
            return slot;
        }

        return ColorPalette.Combine(primary, additional);
    }

    public static int ComputeSampleStride(int width, int height, int maximumSamples = DefaultMaximumSamples)
    {
        if (width <= 0 || height <= 0)
            return 1;
        maximumSamples = Math.Max(1, maximumSamples);
        long pixels = (long)width * height;
        if (pixels <= maximumSamples)
            return 1;
        return Math.Clamp((int)Math.Ceiling(Math.Sqrt(pixels / (double)maximumSamples)), 1, 64);
    }

    public static ColorDetectionResult DetectPixel(
        CaptureFrame frame,
        int x,
        int y,
        PixelColor target,
        ColorMatchSettings settings,
        Rectangle captureRegion = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(settings);
        long started = Stopwatch.GetTimestamp();
        if (frame.IsDisposed || (uint)x >= (uint)frame.Width || (uint)y >= (uint)frame.Height)
        {
            return WithFrame(new ColorDetectionResult
            {
                Matched = false,
                Target = target,
                Threshold = settings.Tolerance,
                DetectionMethod = settings.Mode.ToString(),
                PixelFormat = frame.Format.ToString(),
                CaptureRegion = captureRegion,
                DetectionTime = Stopwatch.GetElapsedTime(started),
                Error = "Sample point is outside the captured frame."
            }, frame);
        }

        PixelColor actual = PixelColor.FromFrame(frame.Span, frame.GetPixelOffset(x, y), frame.Format);
        PixelColor[] targets = ResolveTargets(target, settings.AdditionalTargets);
        ColorDetectionResult compared = CompareAgainstTargets(actual, targets, settings);
        Point screenPoint = new(captureRegion.X + x, captureRegion.Y + y);
        return WithFrame(compared with
        {
            BestMatchPoint = screenPoint,
            CaptureRegion = captureRegion,
            PixelFormat = frame.Format.ToString(),
            DetectionTime = Stopwatch.GetElapsedTime(started),
            DominantColor = actual,
            MatchingBounds = compared.Matched ? new Rectangle(screenPoint.X, screenPoint.Y, 1, 1) : null
        }, frame);
    }

    public static ColorDetectionResult DetectRegion(
        CaptureFrame frame,
        Rectangle localRegion,
        PixelColor target,
        ColorMatchSettings settings,
        Rectangle captureRegion = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(settings);
        long started = Stopwatch.GetTimestamp();
        Rectangle clamped = ClampRegion(localRegion, frame.Width, frame.Height);
        if (frame.IsDisposed || clamped.Width <= 0 || clamped.Height <= 0)
        {
            return WithFrame(new ColorDetectionResult
            {
                Matched = false,
                Target = target,
                Threshold = settings.Tolerance,
                DetectionMethod = settings.Mode.ToString(),
                PixelFormat = frame.Format.ToString(),
                CaptureRegion = captureRegion,
                DetectionTime = Stopwatch.GetElapsedTime(started),
                Error = "Detection region is empty or outside the captured frame."
            }, frame);
        }

        int step = Math.Max(
            Math.Clamp(settings.SampleStride, 1, 64),
            ComputeSampleStride(clamped.Width, clamped.Height));
        if (step != settings.SampleStride)
            settings = settings with { SampleStride = step };

        PixelColor[] targets = ResolveTargets(target, settings.AdditionalTargets);
        ReadOnlySpan<byte> span = frame.Span;
        int bytesPerPixel = CaptureFrame.GetBytesPerPixel(frame.Format);
        double threshold = ColorComparison.GetThreshold(settings.Mode, settings.Tolerance);
        int sampled;
        int matched;
        double bestDistance;
        double totalDistance;
        PixelColor bestColor;
        Point? bestPoint;
        int matchMinX = int.MaxValue;
        int matchMinY = int.MaxValue;
        int matchMaxX = int.MinValue;
        int matchMaxY = int.MinValue;
        long matchR = 0;
        long matchG = 0;
        long matchB = 0;

        if (!TryDetectRegionSimd(
            frame,
            span,
            clamped,
            targets,
            settings,
            threshold,
            captureRegion,
            out sampled,
            out matched,
            out totalDistance,
            out bestDistance,
            out bestColor,
            out bestPoint,
            out matchMinX,
            out matchMinY,
            out matchMaxX,
            out matchMaxY,
            out matchR,
            out matchG,
            out matchB))
        {
            sampled = 0;
            matched = 0;
            bestDistance = double.MaxValue;
            totalDistance = 0;
            bestColor = default;
            bestPoint = null;
            matchMinX = int.MaxValue;
            matchMinY = int.MaxValue;
            matchMaxX = int.MinValue;
            matchMaxY = int.MinValue;
            matchR = 0;
            matchG = 0;
            matchB = 0;

            for (int y = clamped.Top; y < clamped.Bottom; y += step)
            {
                int rowOffset = (y * frame.Stride) + (clamped.Left * bytesPerPixel);
                for (int x = clamped.Left; x < clamped.Right; x += step)
                {
                    int pixelOffset = rowOffset + ((x - clamped.Left) * bytesPerPixel);
                    PixelColor actual = PixelColor.FromFrame(span, pixelOffset, frame.Format);
                    double distance = MinDistance(actual, targets, settings.Mode);
                    sampled++;
                    totalDistance += distance;
                    if (distance <= threshold)
                    {
                        matched++;
                        int screenX = captureRegion.X + x;
                        int screenY = captureRegion.Y + y;
                        if (screenX < matchMinX) matchMinX = screenX;
                        if (screenY < matchMinY) matchMinY = screenY;
                        if (screenX > matchMaxX) matchMaxX = screenX;
                        if (screenY > matchMaxY) matchMaxY = screenY;
                        matchR += actual.R;
                        matchG += actual.G;
                        matchB += actual.B;
                    }
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestColor = actual;
                        bestPoint = new Point(captureRegion.X + x, captureRegion.Y + y);
                    }
                }
            }
        }

        double percentage = sampled == 0 ? 0 : matched / (double)sampled;
        double required = Math.Clamp(settings.MinimumMatchPercentage, 0, 1);
        double confidence = required <= 0
            ? percentage
            : Math.Clamp(percentage / required, 0, 1);
        PixelColor dominant = matched > 0
            ? new PixelColor((byte)(matchR / matched), (byte)(matchG / matched), (byte)(matchB / matched))
            : bestColor;
        Rectangle? matchingBounds = matched > 0
            ? new Rectangle(matchMinX, matchMinY, matchMaxX - matchMinX + 1, matchMaxY - matchMinY + 1)
            : null;
        return WithFrame(new ColorDetectionResult
        {
            Matched = sampled > 0 && percentage >= required,
            Distance = sampled == 0 ? double.MaxValue : totalDistance / sampled,
            Threshold = threshold,
            Actual = bestColor,
            Target = target,
            Confidence = confidence,
            MatchPercentage = percentage,
            MatchingPixels = matched,
            SampledPixels = sampled,
            MatchedRegion = captureRegion,
            BestMatchPoint = bestPoint,
            DetectionTime = Stopwatch.GetElapsedTime(started),
            DetectionMethod = settings.Mode.ToString(),
            PixelFormat = frame.Format.ToString(),
            CaptureRegion = captureRegion,
            DominantColor = dominant,
            MatchingBounds = matchingBounds
        }, frame);
    }

    /// <summary>
    /// Samples the center pixel of <paramref name="localRegion"/>.
    /// </summary>
    public static ColorDetectionResult DetectCenter(
        CaptureFrame frame,
        Rectangle localRegion,
        PixelColor target,
        ColorMatchSettings settings,
        Rectangle captureRegion = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(settings);
        Rectangle clamped = ClampRegion(localRegion, frame.Width, frame.Height);
        if (clamped.Width <= 0 || clamped.Height <= 0)
        {
            return DetectPixel(frame, -1, -1, target, settings, captureRegion);
        }

        int cx = clamped.X + (clamped.Width / 2);
        int cy = clamped.Y + (clamped.Height / 2);
        return DetectPixel(frame, cx, cy, target, settings, captureRegion);
    }

    /// <summary>
    /// Evaluates discrete local sample points. A point matches if it is within tolerance of any palette color.
    /// The overall result matches when the fraction of successful points meets <see cref="ColorMatchSettings.MinimumMatchPercentage"/>.
    /// </summary>
    public static ColorDetectionResult DetectPoints(
        CaptureFrame frame,
        ReadOnlySpan<Point> localPoints,
        PixelColor target,
        ColorMatchSettings settings,
        Rectangle captureRegion = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(settings);
        long started = Stopwatch.GetTimestamp();
        PixelColor[] targets = ResolveTargets(target, settings.AdditionalTargets);
        double threshold = ColorComparison.GetThreshold(settings.Mode, settings.Tolerance);
        int sampled = 0;
        int matched = 0;
        double totalDistance = 0;
        double bestDistance = double.MaxValue;
        PixelColor bestColor = default;
        Point? bestPoint = null;
        int matchMinX = int.MaxValue;
        int matchMinY = int.MaxValue;
        int matchMaxX = int.MinValue;
        int matchMaxY = int.MinValue;
        long matchR = 0;
        long matchG = 0;
        long matchB = 0;

        for (int i = 0; i < localPoints.Length; i++)
        {
            Point p = localPoints[i];
            if ((uint)p.X >= (uint)frame.Width || (uint)p.Y >= (uint)frame.Height)
                continue;

            PixelColor actual = PixelColor.FromFrame(frame.Span, frame.GetPixelOffset(p.X, p.Y), frame.Format);
            double distance = MinDistance(actual, targets, settings.Mode);
            sampled++;
            totalDistance += distance;
            int screenX = captureRegion.X + p.X;
            int screenY = captureRegion.Y + p.Y;
            if (distance <= threshold)
            {
                matched++;
                if (screenX < matchMinX) matchMinX = screenX;
                if (screenY < matchMinY) matchMinY = screenY;
                if (screenX > matchMaxX) matchMaxX = screenX;
                if (screenY > matchMaxY) matchMaxY = screenY;
                matchR += actual.R;
                matchG += actual.G;
                matchB += actual.B;
            }

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestColor = actual;
                bestPoint = new Point(screenX, screenY);
            }
        }

        double percentage = sampled == 0 ? 0 : matched / (double)sampled;
        double required = Math.Clamp(settings.MinimumMatchPercentage, 0, 1);
        return WithFrame(new ColorDetectionResult
        {
            Matched = sampled > 0 && percentage >= required,
            Distance = sampled == 0 ? double.MaxValue : totalDistance / sampled,
            Threshold = threshold,
            Actual = bestColor,
            Target = target,
            Confidence = required <= 0 ? percentage : Math.Clamp(percentage / required, 0, 1),
            MatchPercentage = percentage,
            MatchingPixels = matched,
            SampledPixels = sampled,
            MatchedRegion = captureRegion,
            BestMatchPoint = bestPoint,
            DetectionTime = Stopwatch.GetElapsedTime(started),
            DetectionMethod = settings.Mode.ToString(),
            PixelFormat = frame.Format.ToString(),
            CaptureRegion = captureRegion,
            DominantColor = matched > 0
                ? new PixelColor((byte)(matchR / matched), (byte)(matchG / matched), (byte)(matchB / matched))
                : bestColor,
            MatchingBounds = matched > 0
                ? new Rectangle(matchMinX, matchMinY, matchMaxX - matchMinX + 1, matchMaxY - matchMinY + 1)
                : null,
            Error = sampled == 0 ? "No sample points were inside the captured frame." : null
        }, frame);
    }

    /// <summary>
    /// Checks whether the pixel at byte <paramref name="offset"/> in <paramref name="span"/> matches (R, G, B)
    /// within the given color tolerance. Zero heap allocations.
    /// </summary>
    public static bool IsMatch(
        ReadOnlySpan<byte> span,
        int offset,
        FramePixelFormat format,
        byte r,
        byte g,
        byte b,
        int tolerance = 0)
    {
        int bytesPerPixel = CaptureFrame.GetBytesPerPixel(format);
        if ((uint)(offset + bytesPerPixel) > (uint)span.Length)
        {
            return false;
        }

        byte actualR, actualG, actualB;

        switch (format)
        {
            case FramePixelFormat.Bgra32:
            case FramePixelFormat.Bgr24:
                // Memory order: B, G, R, [A]
                actualB = span[offset];
                actualG = span[offset + 1];
                actualR = span[offset + 2];
                break;

            case FramePixelFormat.Rgba32:
                // Memory order: R, G, B, [A]
                actualR = span[offset];
                actualG = span[offset + 1];
                actualB = span[offset + 2];
                break;

            default:
                return false;
        }

        if (tolerance <= 0)
        {
            return actualR == r && actualG == g && actualB == b;
        }

        // Manhattan color distance check per architecture specification
        int dist = Math.Abs(actualR - r)
                 + Math.Abs(actualG - g)
                 + Math.Abs(actualB - b);

        return dist <= tolerance * 3;
    }

    /// <summary>
    /// Checks whether a single pixel at (x, y) in the frame matches the target color within tolerance.
    /// </summary>
    public static bool MatchesPixel(
        CaptureFrame frame,
        int x,
        int y,
        byte r,
        byte g,
        byte b,
        int tolerance = 0)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.IsDisposed) return false;

        if ((uint)x >= (uint)frame.Width || (uint)y >= (uint)frame.Height)
        {
            return false;
        }

        int offset = frame.GetPixelOffset(x, y);
        return IsMatch(frame.Span, offset, frame.Format, r, g, b, tolerance);
    }

    /// <summary>
    /// Checks whether every pixel within the specified region matches the target color within tolerance.
    /// </summary>
    public static bool MatchesRegion(
        CaptureFrame frame,
        Rectangle region,
        byte r,
        byte g,
        byte b,
        int tolerance = 0)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.IsDisposed) return false;

        Rectangle clamped = ClampRegion(region, frame.Width, frame.Height);
        if (clamped.Width <= 0 || clamped.Height <= 0) return false;

        ReadOnlySpan<byte> span = frame.Span;
        int bytesPerPixel = CaptureFrame.GetBytesPerPixel(frame.Format);
        int stride = frame.Stride;
        FramePixelFormat format = frame.Format;

        for (int y = clamped.Top; y < clamped.Bottom; y++)
        {
            int rowOffset = (y * stride) + (clamped.Left * bytesPerPixel);
            for (int x = 0; x < clamped.Width; x++)
            {
                int pixelOffset = rowOffset + (x * bytesPerPixel);
                if (!IsMatch(span, pixelOffset, format, r, g, b, tolerance))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Counts how many pixels in the specified region match the target color within tolerance.
    /// </summary>
    public static int CountMatches(
        CaptureFrame frame,
        Rectangle region,
        byte r,
        byte g,
        byte b,
        int tolerance = 0)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.IsDisposed) return 0;

        Rectangle clamped = ClampRegion(region, frame.Width, frame.Height);
        if (clamped.Width <= 0 || clamped.Height <= 0) return 0;

        ReadOnlySpan<byte> span = frame.Span;
        int bytesPerPixel = CaptureFrame.GetBytesPerPixel(frame.Format);
        int stride = frame.Stride;
        FramePixelFormat format = frame.Format;

        int matchCount = 0;
        for (int y = clamped.Top; y < clamped.Bottom; y++)
        {
            int rowOffset = (y * stride) + (clamped.Left * bytesPerPixel);
            for (int x = 0; x < clamped.Width; x++)
            {
                int pixelOffset = rowOffset + (x * bytesPerPixel);
                if (IsMatch(span, pixelOffset, format, r, g, b, tolerance))
                {
                    matchCount++;
                }
            }
        }

        return matchCount;
    }

    /// <summary>
    /// Determines whether the percentage of matching pixels in the region meets or exceeds <paramref name="minPercentage"/> (0.0 to 1.0).
    /// </summary>
    public static bool MatchesRegionPercentage(
        CaptureFrame frame,
        Rectangle region,
        byte r,
        byte g,
        byte b,
        int tolerance,
        double minPercentage)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.IsDisposed) return false;

        Rectangle clamped = ClampRegion(region, frame.Width, frame.Height);
        int totalPixels = clamped.Width * clamped.Height;
        if (totalPixels <= 0) return false;

        int requiredMatches = (int)Math.Ceiling(totalPixels * Math.Clamp(minPercentage, 0.0, 1.0));
        int matches = CountMatches(frame, clamped, r, g, b, tolerance);

        return matches >= requiredMatches;
    }

    private static Rectangle ClampRegion(Rectangle region, int frameWidth, int frameHeight)
    {
        int x = Math.Max(0, region.X);
        int y = Math.Max(0, region.Y);
        int right = Math.Min(frameWidth, region.Right);
        int bottom = Math.Min(frameHeight, region.Bottom);

        int width = Math.Max(0, right - x);
        int height = Math.Max(0, bottom - y);

        return new Rectangle(x, y, width, height);
    }

    private static bool TryDetectRegionSimd(
        CaptureFrame frame,
        ReadOnlySpan<byte> span,
        Rectangle clamped,
        PixelColor[] targets,
        ColorMatchSettings settings,
        double threshold,
        Rectangle captureRegion,
        out int sampled,
        out int matched,
        out double totalDistance,
        out double bestDistance,
        out PixelColor bestColor,
        out Point? bestPoint,
        out int matchMinX,
        out int matchMinY,
        out int matchMaxX,
        out int matchMaxY,
        out long matchR,
        out long matchG,
        out long matchB)
    {
        sampled = 0;
        matched = 0;
        totalDistance = 0;
        bestDistance = double.MaxValue;
        bestColor = default;
        bestPoint = null;
        matchMinX = int.MaxValue;
        matchMinY = int.MaxValue;
        matchMaxX = int.MinValue;
        matchMaxY = int.MinValue;
        matchR = 0;
        matchG = 0;
        matchB = 0;

        if (!settings.UseSimd
            || settings.Mode != ColorComparisonMode.Rgb
            || frame.Format != FramePixelFormat.Bgra32
            || settings.SampleStride > 1
            || targets.Length != 1)
        {
            return false;
        }

        if (!ColorScanSimd.TryScanBgraRgb(
            span,
            frame.Stride,
            clamped.Left,
            clamped.Top,
            clamped.Width,
            clamped.Height,
            targets[0],
            threshold,
            captureRegion.X,
            captureRegion.Y,
            out sampled,
            out matched,
            out totalDistance,
            out bestDistance,
            out bestColor,
            out int bestX,
            out int bestY,
            out matchMinX,
            out matchMinY,
            out matchMaxX,
            out matchMaxY,
            out matchR,
            out matchG,
            out matchB))
        {
            return false;
        }

        bestPoint = sampled > 0 ? new Point(bestX, bestY) : null;
        return true;
    }

    private static ColorDetectionResult WithFrame(ColorDetectionResult result, CaptureFrame frame)
        => result with
        {
            CaptureSource = frame.Source,
            FrameWidth = frame.Width,
            FrameHeight = frame.Height,
            PixelFormat = string.IsNullOrEmpty(result.PixelFormat) ? frame.Format.ToString() : result.PixelFormat
        };

    private static ColorDetectionResult CompareAgainstTargets(
        PixelColor actual,
        PixelColor[] targets,
        ColorMatchSettings settings)
    {
        ColorDetectionResult best = ColorComparison.CompareColor(actual, targets[0], settings);
        for (int i = 1; i < targets.Length; i++)
        {
            ColorDetectionResult next = ColorComparison.CompareColor(actual, targets[i], settings);
            if (next.Distance < best.Distance)
                best = next;
        }

        return best;
    }

    private static double MinDistance(PixelColor actual, PixelColor[] targets, ColorComparisonMode mode)
    {
        double best = ColorComparison.GetDistance(actual, targets[0], mode);
        for (int i = 1; i < targets.Length; i++)
        {
            double distance = ColorComparison.GetDistance(actual, targets[i], mode);
            if (distance < best)
                best = distance;
        }

        return best;
    }
}
