using System.Buffers;
using MacroSuite.Core.Models;
using MacroSuite.Core.Vision;

namespace MacroSuite.Core.Tests.TestHelpers;

public static class TestFrameFactory
{
    private sealed class ArrayMemoryOwner : IMemoryOwner<byte>
    {
        private byte[]? _array;
        public Memory<byte> Memory => _array ?? Memory<byte>.Empty;

        public ArrayMemoryOwner(int size)
        {
            _array = new byte[size];
        }

        public void Dispose()
        {
            _array = null;
        }
    }

    public static CaptureFrame CreateSolidColor(
        int width,
        int height,
        byte r,
        byte g,
        byte b,
        FramePixelFormat format = FramePixelFormat.Bgra32,
        CaptureSource source = CaptureSource.Dxgi,
        int originX = 0,
        int originY = 0)
    {
        int bytesPerPixel = CaptureFrame.GetBytesPerPixel(format);
        int stride = width * bytesPerPixel;
        int totalBytes = stride * height;

        var owner = new ArrayMemoryOwner(totalBytes);
        var span = owner.Memory.Span;

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * stride;
            for (int x = 0; x < width; x++)
            {
                int offset = rowOffset + (x * bytesPerPixel);
                switch (format)
                {
                    case FramePixelFormat.Bgra32:
                        span[offset] = b;
                        span[offset + 1] = g;
                        span[offset + 2] = r;
                        span[offset + 3] = 255;
                        break;
                    case FramePixelFormat.Bgr24:
                        span[offset] = b;
                        span[offset + 1] = g;
                        span[offset + 2] = r;
                        break;
                    case FramePixelFormat.Rgba32:
                        span[offset] = r;
                        span[offset + 1] = g;
                        span[offset + 2] = b;
                        span[offset + 3] = 255;
                        break;
                }
            }
        }

        return new CaptureFrame(width, height, stride, format, source, owner, originX: originX, originY: originY);
    }

    public static CaptureFrame CreateUnpopulatedDxgi(int width = 8, int height = 8)
    {
        int stride = width * 4;
        var owner = new ArrayMemoryOwner(stride * height);
        return new CaptureFrame(width, height, stride, FramePixelFormat.Bgra32, CaptureSource.Dxgi, owner);
    }

    public static CaptureFrame CreateBgra32(int width, int height, Func<int, int, PixelColor> pixel, CaptureSource source = CaptureSource.Dxgi)
    {
        int stride = width * 4;
        var owner = new ArrayMemoryOwner(stride * height);
        var span = owner.Memory.Span;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                PixelColor color = pixel(x, y);
                int offset = (y * stride) + (x * 4);
                span[offset] = color.B;
                span[offset + 1] = color.G;
                span[offset + 2] = color.R;
                span[offset + 3] = 255;
            }
        }

        return new CaptureFrame(width, height, stride, FramePixelFormat.Bgra32, source, owner);
    }
}
