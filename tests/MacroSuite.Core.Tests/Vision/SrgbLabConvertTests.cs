using FluentAssertions;
using MacroSuite.Core.Vision;
using Xunit;

namespace MacroSuite.Core.Tests.Vision;

public sealed class SrgbLabConvertTests
{
    [Fact]
    public void SimilarityMapping_IsMonotonicAndDocumented()
    {
        SrgbLabConvert.SimilarityPercentToDeltaE76(99).Should().BeApproximately(1.0 / 3.0, 0.0001);
        SrgbLabConvert.SimilarityPercentToDeltaE76(90).Should().BeApproximately(10.0 / 3.0, 0.0001);
        SrgbLabConvert.SimilarityPercentToDeltaE76(70).Should().BeApproximately(10.0, 0.0001);
        SrgbLabConvert.SimilarityPercentToDeltaE76(90).Should().BeLessThan(SrgbLabConvert.SimilarityPercentToDeltaE76(70));
    }

    [Fact]
    public void IdenticalRgb_HasNearZeroDeltaE76()
    {
        var a = SrgbLabConvert.ToLab(63, 167, 214);
        var b = SrgbLabConvert.ToLab(63, 167, 214);
        SrgbLabConvert.DeltaE76(in a, in b).Should().BeApproximately(0, 0.0001);
    }

    [Fact]
    public void SlightlyDifferentRgb_IsBelow90PercentThreshold()
    {
        var a = SrgbLabConvert.ToLab(63, 167, 214);
        var b = SrgbLabConvert.ToLab(64, 167, 214);
        double de = SrgbLabConvert.DeltaE76(in a, in b);
        de.Should().BeGreaterThan(0);
        de.Should().BeLessThan(SrgbLabConvert.SimilarityPercentToDeltaE76(90));
    }

    [Fact]
    public void OppositeColors_Exceed70PercentThreshold()
    {
        var white = SrgbLabConvert.ToLab(255, 255, 255);
        var black = SrgbLabConvert.ToLab(0, 0, 0);
        SrgbLabConvert.DeltaE76(in white, in black).Should().BeGreaterThan(SrgbLabConvert.SimilarityPercentToDeltaE76(70));
    }
}
