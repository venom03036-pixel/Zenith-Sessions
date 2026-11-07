using FluentAssertions;
using MacroSuite.Core.AI;
using Xunit;

namespace MacroSuite.Core.Tests.AI;

public sealed class VisionEncodePlannerTests
{
    [Fact]
    public void OpenAiTiled_FullHd_ShortSideIs768()
    {
        var (w, h) = VisionEncodePlanner.OpenAiEffectiveSize(1920, 1080);
        h.Should().Be(768);
        w.Should().Be(1365);
        var plan = VisionEncodePlanner.OpenAiTiled(1920, 1080, "gpt-4o");
        plan.SentWidth.Should().Be(1365);
        plan.SentHeight.Should().Be(768);
        plan.DetailParameter.Should().Be("high");
        plan.Space.Should().Be(VisionCoordinateSpace.PixelRelativeToSentImage);
    }

    [Fact]
    public void OpenAiTiled_DoesNotUpscaleSmallCrops()
    {
        var (w, h) = VisionEncodePlanner.OpenAiEffectiveSize(400, 300);
        w.Should().Be(400);
        h.Should().Be(300);
    }

    [Fact]
    public void OpenAiPatches_Fits768Box()
    {
        var plan = VisionEncodePlanner.OpenAiTiled(1920, 1080, "gpt-5-mini");
        plan.SentWidth.Should().Be(768);
        plan.SentHeight.Should().Be(432);
        plan.DetailParameter.Should().BeNull();
    }

    [Fact]
    public void ClaudeStandard_RespectsTokenBudget()
    {
        var (w, h) = VisionEncodePlanner.ClaudeStandardSize(1920, 1080);
        VisionEncodePlanner.ClaudeTokens(w, h).Should().BeLessThanOrEqualTo(1568);
        w.Should().BeLessThanOrEqualTo(1568);
        h.Should().BeLessThanOrEqualTo(1568);
        ((double)w / h).Should().BeApproximately(1920 / 1080.0, 0.05);

        var plan = VisionEncodePlanner.Claude(1920, 1080, "claude-3-5-sonnet-20241022");
        plan.Space.Should().Be(VisionCoordinateSpace.PixelRelativeToSentImage);
    }

    [Fact]
    public void ClaudeOpus47_IsPixelAbsolute()
    {
        var plan = VisionEncodePlanner.Claude(1920, 1080, "claude-opus-4-7");
        plan.SentWidth.Should().Be(1920);
        plan.SentHeight.Should().Be(1080);
        plan.Space.Should().Be(VisionCoordinateSpace.PixelAbsolute);
    }

    [Fact]
    public void Gemini_Fits1920x1080_AndNormalizes()
    {
        var plan = VisionEncodePlanner.Gemini(3840, 2160);
        plan.SentWidth.Should().Be(1920);
        plan.SentHeight.Should().Be(1080);
        plan.Space.Should().Be(VisionCoordinateSpace.Normalized);
        plan.NormalizedMax.Should().Be(1000);
        plan.SwapXY.Should().BeTrue();
    }

    [Fact]
    public void NvidiaLlama_CapsAt1120()
    {
        var plan = VisionEncodePlanner.NvidiaLlama(3840, 2160);
        plan.SentWidth.Should().BeLessThanOrEqualTo(1120);
        plan.SentHeight.Should().BeLessThanOrEqualTo(1120);
    }
}
