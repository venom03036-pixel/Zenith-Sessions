using FluentAssertions;
using MacroSuite.Core.Tests.TestHelpers;
using MacroSuite.Core.Vision;
using Xunit;

namespace MacroSuite.Core.Tests.Vision;

public sealed class PerceptualColorMatcherTests
{
    [Fact]
    public void MatchPixel_ExactColor_ReturnsTrue()
    {
        using var frame = TestFrameFactory.CreateSolidColor(8, 8, 63, 167, 214);
        var lab = SrgbLabConvert.ToLab(63, 167, 214);
        PerceptualColorMatcher.MatchPixel(frame, 3, 3, in lab, 90, useCieDe2000: false, null, out var diagnostics).Should().BeTrue();
        diagnostics.Matched.Should().BeTrue();
    }

    [Fact]
    public void MatchPixel_VeryDifferentColor_ReturnsFalseAt90()
    {
        using var frame = TestFrameFactory.CreateSolidColor(8, 8, 10, 10, 10);
        var lab = SrgbLabConvert.ToLab(63, 167, 214);
        PerceptualColorMatcher.MatchPixel(frame, 0, 0, in lab, 90, useCieDe2000: false).Should().BeFalse();
    }

    [Fact]
    public void MatchRegion_SameTemplate_ReturnsTrue()
    {
        using var frame = TestFrameFactory.CreateSolidColor(20, 10, 40, 80, 120);
        byte[] template = PerceptualColorMatcher.CopyRegionBgra(frame);
        PerceptualColorMatcher.MatchRegion(frame, 0, 0, template, 20, 10, 90, 0.85, maximumAccuracy: false)
            .Should().BeTrue();
    }

    [Fact]
    public void MatchRegion_DifferentColor_ReturnsFalse()
    {
        using var reference = TestFrameFactory.CreateSolidColor(12, 8, 200, 10, 10);
        using var candidate = TestFrameFactory.CreateSolidColor(12, 8, 10, 10, 200);
        byte[] template = PerceptualColorMatcher.CopyRegionBgra(reference);
        PerceptualColorMatcher.MatchRegion(candidate, 0, 0, template, 12, 8, 90, 0.85, maximumAccuracy: false)
            .Should().BeFalse();
    }

    [Fact]
    public void FindRegion_FindsMatchingPatchWithoutFullPixelSweep()
    {
        using var haystack = TestFrameFactory.CreateSolidColor(64, 48, 40, 80, 120);
        using var patch = TestFrameFactory.CreateSolidColor(8, 8, 40, 80, 120);
        byte[] template = PerceptualColorMatcher.CopyRegionBgra(patch);
        PerceptualColorMatcher.FindRegion(haystack, template, 8, 8, 90, 0.85, maximumAccuracy: false, CancellationToken.None)
            .Should().BeTrue();
    }

    [Fact]
    public void FindRegion_RejectsIncompatibleBrightness()
    {
        using var haystack = TestFrameFactory.CreateSolidColor(64, 48, 10, 10, 10);
        using var patch = TestFrameFactory.CreateSolidColor(8, 8, 240, 240, 240);
        byte[] template = PerceptualColorMatcher.CopyRegionBgra(patch);
        PerceptualColorMatcher.FindRegion(haystack, template, 8, 8, 90, 0.85, maximumAccuracy: false, CancellationToken.None)
            .Should().BeFalse();
    }
}
