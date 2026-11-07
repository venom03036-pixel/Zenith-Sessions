using FluentAssertions;
using MacroSuite.Core.AI;
using Xunit;

namespace MacroSuite.Core.Tests.AI;

public sealed class CaptureTransformTests
{
    [Fact]
    public void PixelRelative_UndoesUniformDownscaleAndCrop()
    {
        var plan = VisionEncodePlan.PixelImage(400, 300);
        var transform = CaptureTransform.FromCrop(100, 50, 800, 600, 400, 300, plan);

        (int x, int y) = transform.ToNative(200, 150);

        x.Should().Be(500);
        y.Should().Be(350);
        transform.ScaleX.Should().BeApproximately(0.5, 0.0001);
        transform.ScaleY.Should().BeApproximately(0.5, 0.0001);
    }

    [Fact]
    public void PixelAbsolute_OneToOne_AddsCropOffset()
    {
        var plan = new VisionEncodePlan(800, 600, VisionCoordinateSpace.PixelAbsolute);
        var transform = CaptureTransform.FromCrop(-300, 200, 800, 600, 800, 600, plan);

        (int x, int y) = transform.ToNative(20, 30);

        x.Should().Be(-280);
        y.Should().Be(230);
    }

    [Fact]
    public void Normalized_MapsThousandthsOntoCrop()
    {
        var plan = new VisionEncodePlan(1920, 1080, VisionCoordinateSpace.Normalized, 1000, SwapXY: false);
        var transform = CaptureTransform.FromCrop(0, 0, 1920, 1080, 1920, 1080, plan);

        (int x, int y) = transform.ToNative(500, 500);

        x.Should().Be(960);
        y.Should().Be(540);
    }

    [Fact]
    public void GeminiSwapXY_TreatsFirstValueAsVertical()
    {
        var plan = VisionEncodePlanner.Gemini(1920, 1080);
        var transform = CaptureTransform.FromCrop(0, 0, 1920, 1080, plan.SentWidth, plan.SentHeight, plan);

        (int x, int y) = transform.ToNative(472, 592);

        x.Should().Be(1137);
        y.Should().Be(510);
        transform.SwapXY.Should().BeTrue();
        transform.Space.Should().Be(VisionCoordinateSpace.Normalized);
    }

    [Fact]
    public void NormalizedMax_ClampsToCropInclusive()
    {
        var plan = new VisionEncodePlan(1920, 1080, VisionCoordinateSpace.Normalized, 1000, false);
        var transform = CaptureTransform.FromCrop(0, 0, 1920, 1080, 1920, 1080, plan);

        (int x, int y) = transform.ToNative(1000, 1000);

        x.Should().Be(1919);
        y.Should().Be(1079);
    }

    [Fact]
    public void FormatLog_IncludesSpaceAndSwap()
    {
        var plan = VisionEncodePlanner.Gemini(800, 600);
        var transform = CaptureTransform.FromCrop(10, 20, 800, 600, plan.SentWidth, plan.SentHeight, plan);
        string log = transform.FormatLog(1, 2, 3, 4);

        log.Should().Contain("space=Normalized");
        log.Should().Contain("swapXY=True");
        log.Should().Contain("n=1000");
        log.Should().Contain("model=(1,2)");
        log.Should().Contain("native=(3,4)");
    }
}
