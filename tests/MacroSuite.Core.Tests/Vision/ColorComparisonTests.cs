using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Coordinates;
using MacroSuite.Core.Models;
using MacroSuite.Core.Tests.TestHelpers;
using MacroSuite.Core.Vision;
using Xunit;

namespace MacroSuite.Core.Tests.Vision;

public sealed class ColorComparisonTests
{
    [Theory]
    [InlineData(FramePixelFormat.Bgra32)]
    [InlineData(FramePixelFormat.Bgr24)]
    [InlineData(FramePixelFormat.Rgba32)]
    public void PixelColor_FromFrame_UsesDeclaredMemoryLayout(FramePixelFormat format)
    {
        using var frame = TestFrameFactory.CreateSolidColor(1, 1, 240, 30, 12, format);
        PixelColor actual = PixelColor.FromFrame(frame.Span, 0, format);
        actual.Should().Be(new PixelColor(240, 30, 12));
    }

    [Fact]
    public void RgbComparison_UsesPerChannelToleranceBoundary()
    {
        var settings = new ColorMatchSettings { Mode = ColorComparisonMode.Rgb, Tolerance = 10 };
        ColorComparison.CompareColor(new PixelColor(245, 10, 0), new PixelColor(255, 0, 0), settings)
            .Matched.Should().BeTrue();
        ColorComparison.CompareColor(new PixelColor(244, 0, 0), new PixelColor(255, 0, 0), settings)
            .Matched.Should().BeFalse();
    }

    [Fact]
    public void HsvComparison_WrapsHueAcrossZeroDegrees()
    {
        var nearRedA = new PixelColor(255, 0, 4);
        var nearRedB = new PixelColor(255, 4, 0);
        double distance = ColorComparison.GetDistance(nearRedA, nearRedB, ColorComparisonMode.Hsv);
        distance.Should().BeLessThan(5);
    }

    [Fact]
    public void PerceptualComparison_DetectsNearColor()
    {
        var settings = new ColorMatchSettings { Mode = ColorComparisonMode.CieDe2000, Tolerance = 5 };
        var result = ColorComparison.CompareColor(
            new PixelColor(248, 12, 17),
            new PixelColor(255, 0, 0),
            settings);
        result.Matched.Should().BeTrue();
        result.Confidence.Should().BeInRange(0, 1);
    }

    [Fact]
    public void DetectRegion_ReturnsRichDiagnostics()
    {
        using var frame = TestFrameFactory.CreateSolidColor(10, 5, 250, 5, 5);
        var result = ColorDetector.DetectRegion(
            frame,
            new Rectangle(0, 0, 10, 5),
            new PixelColor(255, 0, 0),
            new ColorMatchSettings
            {
                Mode = ColorComparisonMode.Rgb,
                Tolerance = 5,
                MinimumMatchPercentage = 0.08
            },
            new Rectangle(-100, 200, 10, 5));

        result.Matched.Should().BeTrue();
        result.MatchPercentage.Should().Be(1);
        result.SampledPixels.Should().Be(50);
        result.BestMatchPoint.Should().Be(new Point(-100, 200));
        result.PixelFormat.Should().Be(nameof(FramePixelFormat.Bgra32));
    }

    [Fact]
    public void DetectRegion_RejectsZeroSizeRegion()
    {
        using var frame = TestFrameFactory.CreateSolidColor(10, 10, 0, 0, 0);
        var result = ColorDetector.DetectRegion(
            frame,
            Rectangle.Empty,
            new PixelColor(0, 0, 0),
            new ColorMatchSettings());
        result.Matched.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void DetectRegion_SimdAndScalar_AgreeOnMixedBgraFrame()
    {
        using var frame = TestFrameFactory.CreateBgra32(17, 5, (x, y) =>
            x == 13 && y == 2
                ? new PixelColor(255, 0, 0)
                : new PixelColor(0, 40, 80));
        var target = new PixelColor(255, 0, 0);
        var simd = new ColorMatchSettings
        {
            Mode = ColorComparisonMode.Rgb,
            Tolerance = 5,
            MinimumMatchPercentage = 0.01,
            UseSimd = true
        };
        var scalar = simd with { UseSimd = false };

        var simdResult = ColorDetector.DetectRegion(frame, new Rectangle(0, 0, 17, 5), target, simd, new Rectangle(10, 20, 17, 5));
        var scalarResult = ColorDetector.DetectRegion(frame, new Rectangle(0, 0, 17, 5), target, scalar, new Rectangle(10, 20, 17, 5));

        simdResult.SampledPixels.Should().Be(scalarResult.SampledPixels).And.Be(85);
        simdResult.MatchingPixels.Should().Be(scalarResult.MatchingPixels).And.Be(1);
        simdResult.BestMatchPoint.Should().Be(scalarResult.BestMatchPoint).And.Be(new Point(23, 22));
        simdResult.Matched.Should().BeTrue();
        scalarResult.Matched.Should().BeTrue();
    }

    [Fact]
    public void DetectRegion_PaletteMatchesSecondaryColor()
    {
        using var frame = TestFrameFactory.CreateSolidColor(8, 8, 0, 200, 0);
        var result = ColorDetector.DetectRegion(
            frame,
            new Rectangle(0, 0, 8, 8),
            new PixelColor(255, 0, 0),
            new ColorMatchSettings
            {
                Mode = ColorComparisonMode.Rgb,
                Tolerance = 10,
                MinimumMatchPercentage = 0.5,
                AdditionalTargets = [new PixelColor(0, 200, 0)]
            });

        result.Matched.Should().BeTrue();
        result.MatchingPixels.Should().Be(64);
    }

    [Fact]
    public void DetectPixel_CopiesCaptureSourceAndFrameSize()
    {
        using var frame = TestFrameFactory.CreateSolidColor(6, 4, 10, 20, 30, source: CaptureSource.Gdi);
        var result = ColorDetector.DetectPixel(
            frame,
            1,
            1,
            new PixelColor(10, 20, 30),
            new ColorMatchSettings { Mode = ColorComparisonMode.Rgb, Tolerance = 0 },
            new Rectangle(100, 200, 6, 4));

        result.CaptureSource.Should().Be(CaptureSource.Gdi);
        result.FrameWidth.Should().Be(6);
        result.FrameHeight.Should().Be(4);
        result.Matched.Should().BeTrue();
    }

    [Fact]
    public void ColorPalette_ParseAndFormat_RoundTripHex()
    {
        var extras = ColorPalette.ParseExtras("#FF8800, 00aabb");
        extras.Should().Equal(new PixelColor(255, 136, 0), new PixelColor(0, 170, 187));
        ColorPalette.FormatExtras(extras).Should().Be("#FF8800,#00AABB");
    }

    [Theory]
    [InlineData(1.0, 100)]
    [InlineData(1.25, 125)]
    [InlineData(1.5, 150)]
    [InlineData(1.75, 175)]
    [InlineData(2.0, 200)]
    public void CoordinateMapper_UsesExplicitDpiScale(double scale, int expected)
    {
        var mapper = new ScreenCoordinateMapper(new Rectangle(-1920, 0, 5760, 2160));
        mapper.DipToScreenPoint(100, 100, scale, scale).Should().Be(new Point(expected, expected));
        mapper.CaptureToScreenPoint(new Point(-100, 50)).Should().Be(new Point(-100, 50));
    }

    [Fact]
    public void CoordinateMapper_ClampsNegativeMonitorRegion()
    {
        var mapper = new ScreenCoordinateMapper(new Rectangle(-1920, 0, 3840, 1080));
        mapper.ClampCaptureRegion(new Rectangle(-2000, 100, 200, 100))
            .Should().Be(new Rectangle(-1920, 100, 120, 100));
    }

    [Fact]
    public void CoordinateMapper_ResolvesPerMonitorDpiOnNegativeOrigin()
    {
        var left = new MonitorDescriptor(new Rectangle(-1920, 0, 1920, 1080), new Rectangle(-1920, 0, 1920, 1040), 1.25, 1.25, false);
        var primary = new MonitorDescriptor(new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), 1.0, 1.0, true);
        var mapper = new ScreenCoordinateMapper(new Rectangle(-1920, 0, 3840, 1080), [left, primary]);

        mapper.TryGetMonitorAt(new Point(-100, 10), out MonitorDescriptor monitor).Should().BeTrue();
        monitor.DpiScaleX.Should().Be(1.25);
        mapper.GetDpiScaleXAt(new Point(10, 10)).Should().Be(1.0);
        mapper.ClampCaptureRegion(new Rectangle(-2000, -10, 200, 20))
            .Should().Be(new Rectangle(-1920, 0, 120, 10));
    }

    [Fact]
    public void ColorMatchMode_MapsOntoComparisonMode()
    {
        ColorComparison.ToComparisonMode(ColorMatchMode.Rgb).Should().Be(ColorComparisonMode.Rgb);
        ColorComparison.ToComparisonMode(ColorMatchMode.Hsv).Should().Be(ColorComparisonMode.Hsv);
        ColorComparison.ToComparisonMode(ColorMatchMode.Lab).Should().Be(ColorComparisonMode.Cie76);
        ColorComparison.ToComparisonMode(ColorMatchMode.LabDe2000).Should().Be(ColorComparisonMode.CieDe2000);
        ColorComparison.SpaceFor(ColorComparisonMode.Hsv).Should().Be(ColorSpace.Hsv);
        ColorComparison.SpaceFor(ColorComparisonMode.Cie76).Should().Be(ColorSpace.Lab);
    }

    [Fact]
    public void DetectCenterAndPoints_ReportDominantColorAndBounds()
    {
        using var frame = TestFrameFactory.CreateBgra32(6, 4, (x, y) =>
            x is >= 2 and <= 3 && y == 1
                ? new PixelColor(255, 0, 0)
                : new PixelColor(0, 0, 40));
        var settings = new ColorMatchSettings
        {
            Mode = ColorComparisonMode.Rgb,
            Tolerance = 5,
            MinimumMatchPercentage = 0.2,
            UseSimd = false
        };

        var region = ColorDetector.DetectRegion(
            frame,
            new Rectangle(0, 0, 6, 4),
            new PixelColor(255, 0, 0),
            settings,
            new Rectangle(-50, 20, 6, 4));
        region.Matched.Should().BeFalse();
        region.MatchingPixels.Should().Be(2);
        region.MatchingBounds.Should().Be(new Rectangle(-48, 21, 2, 1));
        region.DominantColor.Should().Be(new PixelColor(255, 0, 0));

        ColorDetector.DetectCenter(frame, new Rectangle(0, 0, 6, 4), new PixelColor(0, 0, 40), settings)
            .Matched.Should().BeTrue();

        Span<Point> points = stackalloc Point[] { new(2, 1), new(0, 0), new(5, 3) };
        var multi = ColorDetector.DetectPoints(frame, points, new PixelColor(255, 0, 0), settings with { MinimumMatchPercentage = 0.3 });
        multi.Matched.Should().BeTrue();
        multi.MatchingPixels.Should().Be(1);
    }
}
