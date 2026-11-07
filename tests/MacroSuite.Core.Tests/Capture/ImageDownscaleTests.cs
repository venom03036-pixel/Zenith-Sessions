using FluentAssertions;
using MacroSuite.Core.Capture;
using MacroSuite.Core.Tests.TestHelpers;
using MacroSuite.Core.Vision;
using Xunit;

namespace MacroSuite.Core.Tests.Capture;

public sealed class ImageDownscaleTests
{
    [Fact]
    public void Fit_FourK_Becomes720p()
    {
        var fit = ImageDownscale.Fit(3840, 2160);
        fit.WasDownscaled.Should().BeTrue();
        fit.DestWidth.Should().Be(1280);
        fit.DestHeight.Should().Be(720);
        fit.ScaleX.Should().BeApproximately(3.0, 0.001);
        fit.ScaleY.Should().BeApproximately(3.0, 0.001);
    }

    [Fact]
    public void Fit_FullHd_Becomes720p()
    {
        var fit = ImageDownscale.Fit(1920, 1080);
        fit.DestWidth.Should().Be(1280);
        fit.DestHeight.Should().Be(720);
    }

    [Fact]
    public void Fit_AlreadySmall_DoesNotUpscale()
    {
        var fit = ImageDownscale.Fit(800, 600);
        fit.WasDownscaled.Should().BeFalse();
        fit.DestWidth.Should().Be(800);
        fit.DestHeight.Should().Be(600);
    }

    [Fact]
    public void Fit_Portrait_FitsInside720pBox()
    {
        var fit = ImageDownscale.Fit(1080, 1920);
        fit.DestWidth.Should().BeLessThanOrEqualTo(ImageDownscale.VisionMaxWidth);
        fit.DestHeight.Should().BeLessThanOrEqualTo(ImageDownscale.VisionMaxHeight);
        fit.DestHeight.Should().Be(720);
        Math.Abs((1080 / (double)1920) - (fit.DestWidth / (double)fit.DestHeight)).Should().BeLessThan(0.01);
    }

    [Fact]
    public void MapToSource_RestoresNativeClick()
    {
        var fit = ImageDownscale.Fit(1920, 1080);
        ImageDownscale.MapToSource(640, fit.ScaleX, origin: 0).Should().Be(960);
        ImageDownscale.MapToSource(360, fit.ScaleY, origin: 0).Should().Be(540);
        ImageDownscale.MapToSource(20, 1.0, origin: -300).Should().Be(-280);
    }

    [Fact]
    public void Downscale_SolidColor_PreservesRgb()
    {
        using var source = TestFrameFactory.CreateSolidColor(64, 48, 12, 34, 56);
        var fit = ImageDownscale.Fit(64, 48, maxWidth: 16, maxHeight: 12);
        using var dest = ImageDownscale.Downscale(source, fit);

        dest.Width.Should().Be(16);
        dest.Height.Should().Be(12);
        PixelColor sample = PixelColor.FromFrame(dest.Span, dest.GetPixelOffset(8, 6), dest.Format);
        sample.R.Should().Be(12);
        sample.G.Should().Be(34);
        sample.B.Should().Be(56);
    }
}
