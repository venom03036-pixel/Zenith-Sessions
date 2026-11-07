using System.Buffers;
using FluentAssertions;
using MacroSuite.Core.Capture;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.OpenCV;
using OpenCvSharp;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.OpenCV;

public class OpenCvImageEncoderTests
{
    private sealed class SimpleMemoryOwner<T> : IMemoryOwner<T>
    {
        public Memory<T> Memory { get; }
        public SimpleMemoryOwner(int size) => Memory = new T[size];
        public void Dispose() { }
    }

    [Fact]
    public void EncodeJpeg_ProducesValidJpegMagicHeader()
    {
        int width = 64;
        int height = 64;
        int stride = width * 4;
        var owner = new SimpleMemoryOwner<byte>(height * stride);

        // Fill with dummy color pattern
        var span = owner.Memory.Span;
        for (int i = 0; i < span.Length; i += 4)
        {
            span[i] = 120;     // B
            span[i + 1] = 60;  // G
            span[i + 2] = 200; // R
            span[i + 3] = 255; // A
        }

        using var frame = new CaptureFrame(width, height, stride, FramePixelFormat.Bgra32, CaptureSource.Dxgi, owner);
        var encoder = new OpenCvImageEncoder();

        byte[] jpegBytes = encoder.EncodeJpeg(frame, quality: 85);

        jpegBytes.Should().NotBeNull();
        jpegBytes.Length.Should().BeGreaterThan(50);

        // JPEG SOI marker
        jpegBytes[0].Should().Be(0xFF);
        jpegBytes[1].Should().Be(0xD8);
    }

    [Fact]
    public void EncodePng_ProducesValidPngMagicHeader()
    {
        int width = 32;
        int height = 32;
        int stride = width * 4;
        var owner = new SimpleMemoryOwner<byte>(height * stride);

        using var frame = new CaptureFrame(width, height, stride, FramePixelFormat.Bgra32, CaptureSource.Dxgi, owner);
        var encoder = new OpenCvImageEncoder();

        byte[] pngBytes = encoder.EncodePng(frame);

        pngBytes.Should().NotBeNull();
        pngBytes.Length.Should().BeGreaterThan(50);

        // PNG 8-byte signature: 137 80 78 71 13 10 26 10
        pngBytes[0].Should().Be(0x89);
        pngBytes[1].Should().Be(0x50); // P
        pngBytes[2].Should().Be(0x4E); // N
        pngBytes[3].Should().Be(0x47); // G
    }

    [Fact]
    public void EncodeJpeg_FullHd_FitsInside720p()
    {
        int width = 1920;
        int height = 1080;
        int stride = width * 4;
        var owner = new SimpleMemoryOwner<byte>(height * stride);
        var span = owner.Memory.Span;
        for (int i = 0; i < span.Length; i += 4)
        {
            span[i] = 40;
            span[i + 1] = 80;
            span[i + 2] = 120;
            span[i + 3] = 255;
        }

        using var frame = new CaptureFrame(width, height, stride, FramePixelFormat.Bgra32, CaptureSource.Dxgi, owner);
        var encoder = new OpenCvImageEncoder();
        byte[] jpegBytes = encoder.EncodeJpeg(
            frame,
            ImageDownscale.VisionJpegQuality,
            ImageDownscale.VisionMaxWidth,
            ImageDownscale.VisionMaxHeight);

        jpegBytes[0].Should().Be(0xFF);
        jpegBytes[1].Should().Be(0xD8);
        jpegBytes.Length.Should().BeLessThan(width * height);

        using var decoded = Cv2.ImDecode(jpegBytes, ImreadModes.Color);
        decoded.Empty().Should().BeFalse();
        decoded.Width.Should().Be(1280);
        decoded.Height.Should().Be(720);
    }

    [Fact]
    public void EncodePng_RespectsMaxBounds()
    {
        int width = 400;
        int height = 300;
        int stride = width * 4;
        var owner = new SimpleMemoryOwner<byte>(height * stride);
        using var frame = new CaptureFrame(width, height, stride, FramePixelFormat.Bgra32, CaptureSource.Dxgi, owner);
        var encoder = new OpenCvImageEncoder();
        byte[] pngBytes = encoder.EncodePng(frame, maxWidth: 100, maxHeight: 75);

        pngBytes[0].Should().Be(0x89);
        using var decoded = Cv2.ImDecode(pngBytes, ImreadModes.Unchanged);
        decoded.Width.Should().Be(100);
        decoded.Height.Should().Be(75);
    }
}
