using System.Windows.Media;
using Xunit;
using Zenith.Sessions.App;

namespace Zenith.Sessions.Tests;

public sealed class GalleryPngTests
{
    internal static byte[] Png(string? streamFault = null, string? extraChunk = null, bool interlaced = false,
        bool splitIdat = false, int colorType = 6, int bitDepth = 8, int width = 576, int height = 324)
        => PublicAccountPresentationTests.Png(width: width, height: height, streamFault: streamFault, extraChunk: extraChunk,
            interlaced: interlaced, splitIdat: splitIdat, colorType: colorType, bitDepth: bitDepth);

    [Theory]
    [InlineData(6, 8, false)]
    [InlineData(6, 16, true)]
    [InlineData(3, 1, true)]
    public void FullGalleryDecodeUsesNonsquareGeometryAndReturnsDetachedFrozenPixels(int colorType, int bitDepth, bool interlaced)
    {
        byte[] source = Png(colorType: colorType, bitDepth: bitDepth, interlaced: interlaced, splitIdat: true);
        var image = PublicAccountPresentation.DecodeGalleryPng(source, "image/png", default);
        Assert.NotNull(image); Assert.Equal(576, image.PixelWidth); Assert.Equal(324, image.PixelHeight);
        Assert.True(image.IsFrozen); Assert.Equal(PixelFormats.Bgra32, image.Format); Assert.Equal(96, image.DpiX);
        Array.Fill(source, (byte)0);
        byte[] pixels = new byte[576 * 324 * 4]; image.CopyPixels(pixels, 576 * 4, 0);
        Assert.Equal(746496, pixels.Length); Assert.Equal(new byte[] { 30, 20, 10, 255 }, pixels[..4]); Assert.Equal(pixels[..4], pixels[^4..]);
    }

    [Fact]
    public void GalleryPolicyCannotRelaxTheExistingExactSquareEntryPoint()
    {
        var square = PublicAccountPresentationTests.Png(); var gallery = Png();
        Assert.NotNull(PublicAccountPresentation.DecodePublicPng(square, "image/png", default));
        Assert.Null(PublicAccountPresentation.DecodeGalleryPng(square, "image/png", default));
        Assert.NotNull(PublicAccountPresentation.DecodeGalleryPng(gallery, "image/png", default));
        Assert.Null(PublicAccountPresentation.DecodePublicPng(gallery, "image/png", default));
        Assert.Null(PublicAccountPresentation.DecodePublicPng(PublicAccountPresentationTests.Png(width: 151), "image/png", default));
        Assert.Null(PublicAccountPresentation.DecodeGalleryPng(Png(width: 575), "image/png", default));
        Assert.Null(PublicAccountPresentation.DecodeGalleryPng(Png(height: 323), "image/png", default));
    }

    [Fact]
    public void CardPolicyIsAThirdClosedGeometry()
    {
        var card = Png(width: 384, height: 216);
        var image = PublicAccountPresentation.DecodeCardPng(card, "image/png", default);
        Assert.NotNull(image); Assert.Equal(384, image.PixelWidth); Assert.Equal(216, image.PixelHeight); Assert.True(image.IsFrozen);
        Assert.Null(PublicAccountPresentation.DecodeCardPng(Png(), "image/png", default));
        Assert.Null(PublicAccountPresentation.DecodeCardPng(PublicAccountPresentationTests.Png(), "image/png", default));
        Assert.Null(PublicAccountPresentation.DecodeGalleryPng(card, "image/png", default));
        Assert.Null(PublicAccountPresentation.DecodePublicPng(card, "image/png", default));
    }

    [Theory]
    [InlineData("bad-crc")]
    [InlineData("truncated")]
    [InlineData("trailing")]
    [InlineData("bad-adler")]
    [InlineData("short-scanlines")]
    [InlineData("extra-scanlines")]
    [InlineData("illegal-filter")]
    [InlineData("truncated-zlib")]
    [InlineData("trailing-zlib")]
    [InlineData("second-zlib")]
    [InlineData("animated")]
    [InlineData("compressed-metadata")]
    [InlineData("unknown-critical")]
    public void GalleryRejectsMalformedEnvelopePixelsAndHostileMetadataBeforeDisplay(string fault)
    {
        byte[] bytes = fault switch
        {
            "truncated" => Png()[..^1], "trailing" => [.. Png(), 0],
            "animated" => Png(extraChunk: "acTL"), "compressed-metadata" => Png(extraChunk: "iCCP"),
            "unknown-critical" => Png(extraChunk: "ABCD"), "bad-crc" => Png(), _ => Png(streamFault: fault, interlaced: true, splitIdat: true)
        };
        if (fault == "bad-crc") bytes[29] ^= 1;
        Assert.Null(PublicAccountPresentation.DecodeGalleryPng(bytes, "image/png", default));
    }

    [Fact]
    public void GalleryRejectsWrongMediaAndEncodedBudgetOverflowAndHonorsCancellation()
    {
        byte[] bytes = Png();
        Assert.Null(PublicAccountPresentation.DecodeGalleryPng(bytes, "image/jpeg", default));
        Assert.Null(PublicAccountPresentation.DecodeGalleryPng(new byte[PublicAccountPresentation.MaximumEncodedBytes + 1], "image/png", default));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PublicAccountPresentation.DecodeGalleryPng(bytes, "image/png", stop.Token));
    }
}
