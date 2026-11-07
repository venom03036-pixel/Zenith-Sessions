using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Vision;

/// <summary>
/// Packed BGRA32 RGB Chebyshev scan. Processes 8 or 4 pixels per register when the
/// hardware supports it, then finishes the row with scalar remainder (Rule 7, Rule 17).
/// </summary>
internal static class ColorScanSimd
{
    public static bool TryScanBgraRgb(
        ReadOnlySpan<byte> pixels,
        int stride,
        int left,
        int top,
        int width,
        int height,
        PixelColor target,
        double threshold,
        int captureOriginX,
        int captureOriginY,
        out int sampled,
        out int matched,
        out double totalDistance,
        out double bestDistance,
        out PixelColor bestColor,
        out int bestX,
        out int bestY,
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
        bestX = -1;
        bestY = -1;
        matchMinX = int.MaxValue;
        matchMinY = int.MaxValue;
        matchMaxX = int.MinValue;
        matchMaxY = int.MinValue;
        matchR = 0;
        matchG = 0;
        matchB = 0;

        if (width <= 0 || height <= 0)
            return false;

        byte tb = target.B;
        byte tg = target.G;
        byte tr = target.R;
        byte tol = (byte)Math.Clamp(threshold, 0, 255);
        int rowBytes = width * 4;

        for (int y = 0; y < height; y++)
        {
            int rowOffset = ((top + y) * stride) + (left * 4);
            ReadOnlySpan<byte> row = pixels.Slice(rowOffset, rowBytes);
            int screenY = captureOriginY + top + y;
            int x = 0;

            if (Avx2.IsSupported && Vector256.IsHardwareAccelerated)
                ScanAvx2(row, ref x, width, tb, tg, tr, tol, captureOriginX + left, screenY,
                    ref sampled, ref matched, ref totalDistance, ref bestDistance, ref bestColor, ref bestX, ref bestY,
                    ref matchMinX, ref matchMinY, ref matchMaxX, ref matchMaxY, ref matchR, ref matchG, ref matchB);

            if (Sse2.IsSupported && Vector128.IsHardwareAccelerated)
                ScanVector128(row, ref x, width, tb, tg, tr, tol, captureOriginX + left, screenY,
                    ref sampled, ref matched, ref totalDistance, ref bestDistance, ref bestColor, ref bestX, ref bestY,
                    ref matchMinX, ref matchMinY, ref matchMaxX, ref matchMaxY, ref matchR, ref matchG, ref matchB);

            ScanScalarTail(row, ref x, width, tb, tg, tr, tol, captureOriginX + left, screenY,
                ref sampled, ref matched, ref totalDistance, ref bestDistance, ref bestColor, ref bestX, ref bestY,
                ref matchMinX, ref matchMinY, ref matchMaxX, ref matchMaxY, ref matchR, ref matchG, ref matchB);
        }

        return sampled > 0;
    }

    private static void ScanAvx2(
        ReadOnlySpan<byte> row,
        ref int x,
        int width,
        byte tb,
        byte tg,
        byte tr,
        byte tol,
        int originX,
        int screenY,
        ref int sampled,
        ref int matched,
        ref double totalDistance,
        ref double bestDistance,
        ref PixelColor bestColor,
        ref int bestX,
        ref int bestY,
        ref int matchMinX,
        ref int matchMinY,
        ref int matchMaxX,
        ref int matchMaxY,
        ref long matchR,
        ref long matchG,
        ref long matchB)
    {
        Vector256<byte> packed = Vector256.Create(tb, tg, tr, 0, tb, tg, tr, 0, tb, tg, tr, 0, tb, tg, tr, 0,
            tb, tg, tr, 0, tb, tg, tr, 0, tb, tg, tr, 0, tb, tg, tr, 0);
        Vector256<byte> rgbMask = Vector256.Create(
            (byte)255, 255, 255, 0, 255, 255, 255, 0, 255, 255, 255, 0, 255, 255, 255, 0,
            255, 255, 255, 0, 255, 255, 255, 0, 255, 255, 255, 0, 255, 255, 255, 0);

        while (x + 8 <= width)
        {
            Vector256<byte> loaded = Vector256.Create(row.Slice(x * 4, 32));
            Vector256<byte> abs = Vector256.BitwiseAnd(
                Vector256.Max(Avx2.SubtractSaturate(loaded, packed), Avx2.SubtractSaturate(packed, loaded)),
                rgbMask);
            for (int i = 0; i < 8; i++)
            {
                int px = x + i;
                int o = px * 4;
                int baseIndex = i * 4;
                int dist = Math.Max(
                    abs.GetElement(baseIndex),
                    Math.Max(abs.GetElement(baseIndex + 1), abs.GetElement(baseIndex + 2)));
                Accumulate(
                    originX + px, screenY, row[o + 2], row[o + 1], row[o], dist, tol,
                    ref sampled, ref matched, ref totalDistance, ref bestDistance, ref bestColor, ref bestX, ref bestY,
                    ref matchMinX, ref matchMinY, ref matchMaxX, ref matchMaxY, ref matchR, ref matchG, ref matchB);
            }

            x += 8;
        }
    }

    private static void ScanVector128(
        ReadOnlySpan<byte> row,
        ref int x,
        int width,
        byte tb,
        byte tg,
        byte tr,
        byte tol,
        int originX,
        int screenY,
        ref int sampled,
        ref int matched,
        ref double totalDistance,
        ref double bestDistance,
        ref PixelColor bestColor,
        ref int bestX,
        ref int bestY,
        ref int matchMinX,
        ref int matchMinY,
        ref int matchMaxX,
        ref int matchMaxY,
        ref long matchR,
        ref long matchG,
        ref long matchB)
    {
        Vector128<byte> packed = Vector128.Create(tb, tg, tr, 0, tb, tg, tr, 0, tb, tg, tr, 0, tb, tg, tr, 0);
        Vector128<byte> rgbMask = Vector128.Create((byte)255, 255, 255, 0, 255, 255, 255, 0, 255, 255, 255, 0, 255, 255, 255, 0);

        while (x + 4 <= width)
        {
            Vector128<byte> loaded = Vector128.Create(row.Slice(x * 4, 16));
            Vector128<byte> abs = Vector128.BitwiseAnd(
                Vector128.Max(Sse2.SubtractSaturate(loaded, packed), Sse2.SubtractSaturate(packed, loaded)),
                rgbMask);
            for (int i = 0; i < 4; i++)
            {
                int px = x + i;
                int o = px * 4;
                int baseIndex = i * 4;
                int dist = Math.Max(
                    abs.GetElement(baseIndex),
                    Math.Max(abs.GetElement(baseIndex + 1), abs.GetElement(baseIndex + 2)));
                Accumulate(
                    originX + px, screenY, row[o + 2], row[o + 1], row[o], dist, tol,
                    ref sampled, ref matched, ref totalDistance, ref bestDistance, ref bestColor, ref bestX, ref bestY,
                    ref matchMinX, ref matchMinY, ref matchMaxX, ref matchMaxY, ref matchR, ref matchG, ref matchB);
            }

            x += 4;
        }
    }

    private static void ScanScalarTail(
        ReadOnlySpan<byte> row,
        ref int x,
        int width,
        byte tb,
        byte tg,
        byte tr,
        byte tol,
        int originX,
        int screenY,
        ref int sampled,
        ref int matched,
        ref double totalDistance,
        ref double bestDistance,
        ref PixelColor bestColor,
        ref int bestX,
        ref int bestY,
        ref int matchMinX,
        ref int matchMinY,
        ref int matchMaxX,
        ref int matchMaxY,
        ref long matchR,
        ref long matchG,
        ref long matchB)
    {
        for (; x < width; x++)
        {
            int o = x * 4;
            int dist = Math.Max(Math.Abs(row[o + 2] - tr), Math.Max(Math.Abs(row[o + 1] - tg), Math.Abs(row[o] - tb)));
            Accumulate(
                originX + x, screenY, row[o + 2], row[o + 1], row[o], dist, tol,
                ref sampled, ref matched, ref totalDistance, ref bestDistance, ref bestColor, ref bestX, ref bestY,
                ref matchMinX, ref matchMinY, ref matchMaxX, ref matchMaxY, ref matchR, ref matchG, ref matchB);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Accumulate(
        int screenX,
        int screenY,
        byte r,
        byte g,
        byte b,
        int dist,
        byte tol,
        ref int sampled,
        ref int matched,
        ref double totalDistance,
        ref double bestDistance,
        ref PixelColor bestColor,
        ref int bestX,
        ref int bestY,
        ref int matchMinX,
        ref int matchMinY,
        ref int matchMaxX,
        ref int matchMaxY,
        ref long matchR,
        ref long matchG,
        ref long matchB)
    {
        sampled++;
        totalDistance += dist;
        if (dist <= tol)
        {
            matched++;
            if (screenX < matchMinX) matchMinX = screenX;
            if (screenY < matchMinY) matchMinY = screenY;
            if (screenX > matchMaxX) matchMaxX = screenX;
            if (screenY > matchMaxY) matchMaxY = screenY;
            matchR += r;
            matchG += g;
            matchB += b;
        }
        if (dist < bestDistance)
        {
            bestDistance = dist;
            bestColor = new PixelColor(r, g, b);
            bestX = screenX;
            bestY = screenY;
        }
    }
}
