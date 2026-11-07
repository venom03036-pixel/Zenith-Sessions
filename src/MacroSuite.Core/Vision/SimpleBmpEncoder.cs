using MacroSuite.Core.Models;

namespace MacroSuite.Core.Vision;

/// <summary>
/// Minimal uncompressed 32-bit BMP encoder for AI fallback when no JPEG encoder is wired.
/// </summary>
public static class SimpleBmpEncoder
{
    public static byte[] EncodeBgra32(CaptureFrame frame)
    {
        int width = frame.Width;
        int height = frame.Height;
        int rowStride = ((width * 4) + 3) & ~3;
        int pixelBytes = rowStride * height;
        int fileSize = 54 + pixelBytes;
        byte[] bmp = new byte[fileSize];

        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.TryWriteBytes(bmp.AsSpan(2, 4), fileSize);
        BitConverter.TryWriteBytes(bmp.AsSpan(10, 4), 54);
        BitConverter.TryWriteBytes(bmp.AsSpan(14, 4), 40);
        BitConverter.TryWriteBytes(bmp.AsSpan(18, 4), width);
        BitConverter.TryWriteBytes(bmp.AsSpan(22, 4), -height); // top-down
        BitConverter.TryWriteBytes(bmp.AsSpan(26, 2), (short)1);
        BitConverter.TryWriteBytes(bmp.AsSpan(28, 2), (short)32);

        for (int y = 0; y < height; y++)
        {
            int destRow = 54 + (y * rowStride);
            for (int x = 0; x < width; x++)
            {
                if (!PerceptualColorMatcher.TryReadRgb(frame, x, y, out byte r, out byte g, out byte b))
                    continue;
                int o = destRow + (x * 4);
                bmp[o] = b;
                bmp[o + 1] = g;
                bmp[o + 2] = r;
                bmp[o + 3] = 255;
            }
        }

        return bmp;
    }
}
