using System.Buffers;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Capture;

/// <summary>
/// Downscales captured frames for AI vision and screenshot payloads only.
/// The live desktop and template-matching captures stay at native resolution.
/// Aspect ratio is preserved; coordinates map back with <see cref="FitResult.ScaleX"/> /
/// <see cref="FitResult.ScaleY"/>.
/// </summary>
public static class ImageDownscale
{
    /// <summary>720p landscape width. 4K 16:9 fits to 1280×720.</summary>
    public const int VisionMaxWidth = 1280;

    /// <summary>720p landscape height.</summary>
    public const int VisionMaxHeight = 720;

    /// <summary>JPEG quality for vision/screenshot payloads. High enough to keep UI text readable.</summary>
    public const int VisionJpegQuality = 85;

    public readonly record struct FitResult(int SourceWidth, int SourceHeight, int DestWidth, int DestHeight)
    {
        public bool WasDownscaled => DestWidth < SourceWidth || DestHeight < SourceHeight;

        public double ScaleX => DestWidth <= 0 ? 1.0 : SourceWidth / (double)DestWidth;

        public double ScaleY => DestHeight <= 0 ? 1.0 : SourceHeight / (double)DestHeight;
    }

    public static FitResult Fit(
        int width,
        int height,
        int maxWidth = VisionMaxWidth,
        int maxHeight = VisionMaxHeight)
    {
        int srcW = Math.Max(1, width);
        int srcH = Math.Max(1, height);
        if (maxWidth <= 0 || maxHeight <= 0 || (srcW <= maxWidth && srcH <= maxHeight))
            return new FitResult(srcW, srcH, srcW, srcH);

        double scale = Math.Min(maxWidth / (double)srcW, maxHeight / (double)srcH);
        int destW = Math.Max(1, (int)Math.Round(srcW * scale));
        int destH = Math.Max(1, (int)Math.Round(srcH * scale));
        return new FitResult(srcW, srcH, destW, destH);
    }

    public static int MapToSource(int imageLocal, double sourcePerDest, int origin)
        => origin + (int)Math.Round(imageLocal * sourcePerDest);

    /// <summary>
    /// Box-filter downsample used when OpenCV is unavailable. Averages source blocks so
    /// shrunk UI still represents the original colors instead of dropping pixels.
    /// </summary>
    public static CaptureFrame Downscale(CaptureFrame source, FitResult fit)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.IsDisposed)
            throw new ObjectDisposedException(nameof(source));
        if (source.Width != fit.SourceWidth || source.Height != fit.SourceHeight)
        {
            throw new ArgumentException(
                $"Fit source {fit.SourceWidth}x{fit.SourceHeight} does not match frame {source.Width}x{source.Height}.",
                nameof(fit));
        }

        if (!fit.WasDownscaled)
        {
            throw new ArgumentException("Fit does not require downscale.", nameof(fit));
        }

        int bpp = CaptureFrame.GetBytesPerPixel(source.Format);
        int destStride = checked(fit.DestWidth * bpp);
        int destBytes = checked(destStride * fit.DestHeight);
        IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(destBytes);
        try
        {
            BoxFilter(source, fit, owner.Memory.Span[..destBytes], destStride, bpp);
            return new CaptureFrame(
                fit.DestWidth,
                fit.DestHeight,
                destStride,
                source.Format,
                source.Source,
                owner,
                originX: source.OriginX,
                originY: source.OriginY);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    private static void BoxFilter(
        CaptureFrame source,
        FitResult fit,
        Span<byte> dest,
        int destStride,
        int bpp)
    {
        ReadOnlySpan<byte> src = source.Span;
        int srcW = fit.SourceWidth;
        int srcH = fit.SourceHeight;
        int destW = fit.DestWidth;
        int destH = fit.DestHeight;
        int srcStride = source.Stride;
        bool writeAlpha = bpp == 4;

        for (int dy = 0; dy < destH; dy++)
        {
            int y0 = dy * srcH / destH;
            int y1 = Math.Max(y0 + 1, (dy + 1) * srcH / destH);
            if (y1 > srcH) y1 = srcH;

            int destRow = dy * destStride;
            for (int dx = 0; dx < destW; dx++)
            {
                int x0 = dx * srcW / destW;
                int x1 = Math.Max(x0 + 1, (dx + 1) * srcW / destW);
                if (x1 > srcW) x1 = srcW;

                long c0 = 0, c1 = 0, c2 = 0, count = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    int row = sy * srcStride;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int i = row + (sx * bpp);
                        c0 += src[i];
                        c1 += src[i + 1];
                        c2 += src[i + 2];
                        count++;
                    }
                }

                if (count == 0)
                    count = 1;

                int di = destRow + (dx * bpp);
                dest[di] = (byte)(c0 / count);
                dest[di + 1] = (byte)(c1 / count);
                dest[di + 2] = (byte)(c2 / count);
                if (writeAlpha)
                    dest[di + 3] = 255;
            }
        }
    }
}
