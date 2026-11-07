using System.Buffers;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.OpenCV;
using OpenCvSharp;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.OpenCV;

// Real in-memory encoding/decoding only: no desktop capture or input.
public sealed class OpenCvImageEncoderFormatTests
{
    [Theory]
    [InlineData(FramePixelFormat.Bgr24, false)]
    [InlineData(FramePixelFormat.Bgra32, false)]
    [InlineData(FramePixelFormat.Rgba32, false)]
    [InlineData(FramePixelFormat.Bgr24, true)]
    [InlineData(FramePixelFormat.Bgra32, true)]
    [InlineData(FramePixelFormat.Rgba32, true)]
    public void PngPreservesDeclaredChannelOrderPaddingAndAlpha(FramePixelFormat format, bool resize)
    {
        var owner = new Owner(24 * (32 * CaptureFrame.GetBytesPerPixel(format) + 9));
        using var frame = CreateFrame(format, owner);
        var encoder = new OpenCvImageEncoder();
        byte[] encoded = resize ? encoder.EncodePng(frame, 8, 6) : encoder.EncodePng(frame);
        using var decoded = Cv2.ImDecode(encoded, ImreadModes.Unchanged);
        Assert.Equal(resize ? 8 : 32, decoded.Width); Assert.Equal(resize ? 6 : 24, decoded.Height);
        if (format == FramePixelFormat.Bgr24)
        {
            Assert.Equal(3, decoded.Channels());
            Assert.Equal(new Vec3b(7, 91, 203), decoded.At<Vec3b>(0, 0));
            Assert.Equal(new Vec3b(7, 91, 203), decoded.At<Vec3b>(decoded.Height - 1, decoded.Width - 1));
        }
        else
        {
            Assert.Equal(4, decoded.Channels());
            Assert.Equal(new Vec4b(7, 91, 203, 173), decoded.At<Vec4b>(0, 0));
            Assert.Equal(new Vec4b(7, 91, 203, 173), decoded.At<Vec4b>(decoded.Height - 1, decoded.Width - 1));
        }
        Assert.Equal(0, owner.Disposals); Assert.False(frame.IsDisposed);
    }

    [Theory]
    [InlineData(FramePixelFormat.Bgr24, false)]
    [InlineData(FramePixelFormat.Bgra32, false)]
    [InlineData(FramePixelFormat.Rgba32, false)]
    [InlineData(FramePixelFormat.Bgr24, true)]
    [InlineData(FramePixelFormat.Bgra32, true)]
    [InlineData(FramePixelFormat.Rgba32, true)]
    public void JpegPreservesDeclaredChannelOrderWithoutTakingFrameOwnership(FramePixelFormat format, bool resize)
    {
        var owner = new Owner(24 * (32 * CaptureFrame.GetBytesPerPixel(format) + 9));
        using var frame = CreateFrame(format, owner);
        var encoder = new OpenCvImageEncoder();
        byte[] encoded = resize ? encoder.EncodeJpeg(frame, 100, 8, 6) : encoder.EncodeJpeg(frame, 100);
        using var decoded = Cv2.ImDecode(encoded, ImreadModes.Color);
        Assert.Equal(resize ? 8 : 32, decoded.Width); Assert.Equal(resize ? 6 : 24, decoded.Height);
        Vec3b p = decoded.At<Vec3b>(0, 0);
        Assert.InRange((int)p.Item0, 4, 10); Assert.InRange((int)p.Item1, 88, 94); Assert.InRange((int)p.Item2, 200, 206);
        Assert.Equal(0, owner.Disposals); Assert.False(frame.IsDisposed);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void InvalidFormatOrShortRowsAreRejectedBeforeNativeEncoding(bool png, bool invalidFormat)
    {
        const int width = 4, height = 4;
        int stride = invalidFormat ? width * 4 : width * 4 - 1;
        var owner = new Owner(height * stride);
        using var frame = new CaptureFrame(width, height, stride,
            invalidFormat ? (FramePixelFormat)255 : FramePixelFormat.Bgra32, CaptureSource.Dxgi, owner);
        var encoder = new OpenCvImageEncoder();
        Assert.Throws<ArgumentException>(() => { _ = png ? encoder.EncodePng(frame) : encoder.EncodeJpeg(frame); });
        Assert.Equal(0, owner.Disposals);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DisposedFrameCannotBeEncoded(bool png)
    {
        var owner = new Owner(24 * (32 * 4 + 9));
        var frame = CreateFrame(FramePixelFormat.Rgba32, owner); frame.Dispose();
        var encoder = new OpenCvImageEncoder();
        Assert.Throws<ObjectDisposedException>(() => { _ = png ? encoder.EncodePng(frame) : encoder.EncodeJpeg(frame); });
        Assert.Equal(1, owner.Disposals);
    }

    private static CaptureFrame CreateFrame(FramePixelFormat format, Owner owner)
    {
        int pixel = CaptureFrame.GetBytesPerPixel(format), stride = 32 * pixel + 9;
        owner.Memory.Span.Fill(247); // Padding must not become image pixels.
        for (int y = 0; y < 24; y++)
        for (int x = 0; x < 32; x++)
        {
            int at = y * stride + x * pixel;
            owner.Memory.Span[at] = format == FramePixelFormat.Rgba32 ? (byte)203 : (byte)7;
            owner.Memory.Span[at + 1] = 91;
            owner.Memory.Span[at + 2] = format == FramePixelFormat.Rgba32 ? (byte)7 : (byte)203;
            if (pixel == 4) owner.Memory.Span[at + 3] = 173;
        }
        return new CaptureFrame(32, 24, stride, format, CaptureSource.Dxgi, owner, originX: -17, originY: 13);
    }

    private sealed class Owner(int count) : IMemoryOwner<byte>
    {
        private readonly byte[] _bytes = new byte[count];
        public Memory<byte> Memory => _bytes;
        public int Disposals;
        public void Dispose() => Disposals++;
    }
}
