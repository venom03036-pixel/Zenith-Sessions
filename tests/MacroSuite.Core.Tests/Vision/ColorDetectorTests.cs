using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Models;
using MacroSuite.Core.Tests.TestHelpers;
using MacroSuite.Core.Vision;
using Xunit;

namespace MacroSuite.Core.Tests.Vision;

public sealed class ColorDetectorTests
{
    [Fact]
    public void ExactMatch_WithToleranceZero_ReturnsTrue()
    {
        using var frame = TestFrameFactory.CreateSolidColor(10, 10, 255, 128, 64);

        bool match = ColorDetector.MatchesPixel(frame, 5, 5, 255, 128, 64, tolerance: 0);

        match.Should().BeTrue();
    }

    [Fact]
    public void MismatchedColor_WithToleranceZero_ReturnsFalse()
    {
        using var frame = TestFrameFactory.CreateSolidColor(10, 10, 255, 128, 64);

        bool match = ColorDetector.MatchesPixel(frame, 5, 5, 254, 128, 64, tolerance: 0);

        match.Should().BeFalse();
    }

    [Fact]
    public void MismatchedColor_WithinTolerance_ReturnsTrue()
    {
        // Target is 255, 128, 64. Frame is 250, 125, 60.
        // Diff = |250-255| + |125-128| + |60-64| = 5 + 3 + 4 = 12.
        // Tolerance = 5 => tolerance * 3 = 15. Since 12 <= 15, should match.
        using var frame = TestFrameFactory.CreateSolidColor(10, 10, 250, 125, 60);

        bool match = ColorDetector.MatchesPixel(frame, 2, 2, 255, 128, 64, tolerance: 5);

        match.Should().BeTrue();
    }

    [Fact]
    public void MismatchedColor_ExceedingTolerance_ReturnsFalse()
    {
        // Diff = 12. Tolerance = 3 => tolerance * 3 = 9. Since 12 > 9, should not match.
        using var frame = TestFrameFactory.CreateSolidColor(10, 10, 250, 125, 60);

        bool match = ColorDetector.MatchesPixel(frame, 2, 2, 255, 128, 64, tolerance: 3);

        match.Should().BeFalse();
    }

    [Fact]
    public void Formats_Bgr24AndRgba32_MatchCorrectly()
    {
        using var bgrFrame = TestFrameFactory.CreateSolidColor(5, 5, 200, 100, 50, FramePixelFormat.Bgr24);
        using var rgbaFrame = TestFrameFactory.CreateSolidColor(5, 5, 200, 100, 50, FramePixelFormat.Rgba32);

        ColorDetector.MatchesPixel(bgrFrame, 2, 2, 200, 100, 50).Should().BeTrue();
        ColorDetector.MatchesPixel(rgbaFrame, 2, 2, 200, 100, 50).Should().BeTrue();
    }

    [Fact]
    public void MatchesRegion_UniformColor_ReturnsTrue()
    {
        using var frame = TestFrameFactory.CreateSolidColor(20, 20, 10, 20, 30);

        bool match = ColorDetector.MatchesRegion(frame, new Rectangle(5, 5, 10, 10), 10, 20, 30);

        match.Should().BeTrue();
    }

    [Fact]
    public void MatchesRegionPercentage_SufficientMatches_ReturnsTrue()
    {
        using var frame = TestFrameFactory.CreateSolidColor(10, 10, 100, 100, 100);

        // All 100 pixels match, requiring 80% should pass
        bool match = ColorDetector.MatchesRegionPercentage(
            frame,
            new Rectangle(0, 0, 10, 10),
            100, 100, 100,
            tolerance: 0,
            minPercentage: 0.8);

        match.Should().BeTrue();
    }

    [Fact]
    public void MatchesPixel_OutOfBounds_ReturnsFalse()
    {
        using var frame = TestFrameFactory.CreateSolidColor(10, 10, 50, 50, 50);

        ColorDetector.MatchesPixel(frame, -1, 5, 50, 50, 50).Should().BeFalse();
        ColorDetector.MatchesPixel(frame, 10, 5, 50, 50, 50).Should().BeFalse();
        ColorDetector.MatchesPixel(frame, 5, -1, 50, 50, 50).Should().BeFalse();
        ColorDetector.MatchesPixel(frame, 5, 10, 50, 50, 50).Should().BeFalse();
    }

    [Fact]
    public void DisposedFrame_ReturnsFalse()
    {
        var frame = TestFrameFactory.CreateSolidColor(10, 10, 50, 50, 50);
        frame.Dispose();

        ColorDetector.MatchesPixel(frame, 0, 0, 50, 50, 50).Should().BeFalse();
        ColorDetector.MatchesRegion(frame, new Rectangle(0, 0, 5, 5), 50, 50, 50).Should().BeFalse();
    }
}
