using System.Buffers.Binary;
using MacroSuite.Infrastructure.OpenCV;
using OpenCvSharp;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.OpenCV;

public sealed class BoundedTemplateDecoderTests
{
    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".bmp")]
    public void SupportedTemplatesDecodeAtTheirDeclaredGrayscaleSize(string extension)
    {
        using var original = new Mat(9, 17, MatType.CV_8UC3, new Scalar(25, 90, 170));
        Cv2.ImEncode(extension, original, out byte[] bytes);
        using var decoded = BoundedTemplateDecoder.Decode(null, bytes);
        Assert.Equal(17, decoded.Width); Assert.Equal(9, decoded.Height); Assert.Equal(MatType.CV_8UC1, decoded.Type());
        var failure = Assert.Throws<InvalidDataException>(() => BoundedTemplateDecoder.Decode(null, bytes, 17 * 9 - 1));
        Assert.Contains("budget", failure.Message);
    }

    [Fact]
    public void OversizedEncodedFileIsRejectedBeforeReadingItsPayload()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ZenithTemplateTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "large.png");
        using (var stream = File.Create(file)) stream.SetLength(BoundedTemplateDecoder.MaxEncodedBytes + 1L);
        try { Assert.Contains("32 MB", Assert.Throws<InvalidDataException>(() => BoundedTemplateDecoder.Decode(file)).Message); }
        finally { File.Delete(file); }
    }

    [Fact]
    public void OversizedPngDimensionsAreRejectedBeforeNativeDecodeEvenWithoutPixelData()
    {
        byte[] bytes = new byte[24]; new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13); "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), 100_000); BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), 100_000);
        Assert.Contains("4096", Assert.Throws<InvalidDataException>(() => BoundedTemplateDecoder.Decode(null, bytes)).Message);
    }

    [Fact]
    public void OversizedJpegDimensionsAreRejectedBeforeNativeDecodeEvenWithoutPixelData()
    {
        byte[] bytes = [255, 216, 255, 192, 0, 11, 8, 255, 255, 255, 255, 1, 1, 17, 0, 255, 218];
        Assert.Contains("4096", Assert.Throws<InvalidDataException>(() => BoundedTemplateDecoder.Decode(null, bytes)).Message);
    }

    [Fact]
    public void OversizedAndEmbeddedCompressedBmpHeadersAreRejectedBeforeNativeDecode()
    {
        byte[] bytes = new byte[54]; bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18, 4), 5000); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22, 4), -5000);
        Assert.Contains("4096", Assert.Throws<InvalidDataException>(() => BoundedTemplateDecoder.Decode(null, bytes)).Message);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(30, 4), 5);
        Assert.Contains("unsupported", Assert.Throws<InvalidDataException>(() => BoundedTemplateDecoder.Decode(null, bytes)).Message);
    }

    [Fact]
    public void UnsupportedAndTruncatedFormatsHaveClearDiagnostics()
    {
        Assert.Contains("PNG, JPEG, or BMP", Assert.Throws<InvalidDataException>(() => BoundedTemplateDecoder.Decode(null, "GIF89a"u8.ToArray())).Message);
        Assert.Contains("PNG, JPEG, or BMP", Assert.Throws<InvalidDataException>(() => BoundedTemplateDecoder.Decode(null, "RIFF0000WEBPVP8 "u8.ToArray())).Message);
        Assert.Contains("JPEG", Assert.Throws<InvalidDataException>(() => BoundedTemplateDecoder.Decode(null, new byte[] { 255, 216, 255, 192, 0, 255 })).Message);
    }
}
