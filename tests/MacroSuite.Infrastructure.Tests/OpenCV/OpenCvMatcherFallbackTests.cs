using System.Buffers;
using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.OpenCV;
using OpenCvSharp;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.OpenCV;

public sealed class OpenCvMatcherFallbackTests
{
    [Fact]
    public async Task FullResolution_FindsFineDetail_EvenWhenHalfSizeTemplateBecomesFlat()
    {
        using var template = CreateBalancedFineDetail(20, 20);
        using var half = HalfSize(template);
        Cv2.MeanStdDev(half, out _, out Scalar deviation);
        deviation.Val0.Should().Be(0);
        using var scene = CreateScene(640, 480);
        Embed(scene, template, 500, 300);
        using var frame = FrameFrom(scene);
        using var matcher = new OpenCvMatcher();

        var result = await matcher.MatchAsync(frame, Encode(template), 0.99);

        result.Success.Should().BeTrue();
        result.BoundingBox.Should().Be(new Rectangle(500, 300, 20, 20));
    }

    [Fact]
    public async Task FullResolution_FindsExactOddAlignedTemplate_WhenCoarseScoreIsBelowPrefilterThreshold()
    {
        using var template = CreateNoiseTemplate(32, 32);
        using var scene = CreateScene(640, 480);
        Embed(scene, template, 501, 303);
        var coarse = CoarseMaximum(scene, template);
        coarse.Score.Should().BeLessThan(0.99 * 0.72, "the regression must exercise the old early rejection");
        using var frame = FrameFrom(scene);
        using var matcher = new OpenCvMatcher();

        var result = await matcher.MatchAsync(frame, Encode(template), 0.99);

        result.Success.Should().BeTrue();
        result.BoundingBox.Should().Be(new Rectangle(501, 303, 32, 32));
    }

    [Fact]
    public async Task FullResolution_FindsTrueTemplate_WhenCoarseWinnerIsALookalike()
    {
        using var template = CreateNoiseTemplate(32, 32);
        using var half = HalfSize(template);
        using var lookalike = new Mat();
        Cv2.Resize(half, lookalike, template.Size(), 0, 0, InterpolationFlags.Nearest);
        using var scene = CreateScene(640, 480);
        Embed(scene, lookalike, 40, 40);
        Embed(scene, template, 501, 303);
        var coarse = CoarseMaximum(scene, template);
        coarse.Location.Should().Be(new OpenCvSharp.Point(20, 20));
        coarse.Score.Should().BeGreaterThan(0.99);
        using var frame = FrameFrom(scene);
        using var matcher = new OpenCvMatcher();

        var result = await matcher.MatchAsync(frame, Encode(template), 0.99);

        result.Success.Should().BeTrue();
        result.BoundingBox.Should().Be(new Rectangle(501, 303, 32, 32));
    }

    [Fact]
    public async Task FullResolutionFallback_RespectsSearchRegion_AndReturnsFrameRelativeCoordinates()
    {
        using var template = CreateBalancedFineDetail(20, 20);
        using var scene = CreateScene(800, 600);
        Embed(scene, template, 10, 10); // An exact match outside the allowed region.
        Embed(scene, template, 600, 400);
        using var frame = FrameFrom(scene);
        using var matcher = new OpenCvMatcher();

        var result = await matcher.MatchAsync(frame, Encode(template), 0.99, new Rectangle(200, 100, 500, 400));

        result.Success.Should().BeTrue();
        result.BoundingBox.Should().Be(new Rectangle(600, 400, 20, 20));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    [InlineData(255)]
    public async Task FlatTemplate_FailsClearly_InsteadOfMatchingEveryScreenLocation(int intensity)
    {
        using var template = new Mat(20, 20, MatType.CV_8UC4, new Scalar(intensity, intensity, intensity, 255));
        using var scene = CreateScene(640, 480);
        using var frame = FrameFrom(scene);
        using var matcher = new OpenCvMatcher();

        var result = await matcher.MatchAsync(frame, Encode(template), 0.8);

        result.Success.Should().BeFalse();
        result.Status.Should().Be(MatchOutcome.Failed);
        result.ErrorCode.Should().Be("ImageTemplateHasNoDetail");
    }

    [Fact]
    public async Task FineDetailAbsent_FallbackDoesNotInventAMatch()
    {
        using var template = CreateBalancedFineDetail(20, 20);
        using var scene = CreateScene(640, 480);
        using var frame = FrameFrom(scene);
        using var matcher = new OpenCvMatcher();

        var result = await matcher.MatchAsync(frame, Encode(template), 0.99);

        result.Success.Should().BeFalse();
        result.Status.Should().NotBe(MatchOutcome.Failed);
    }

    [Fact]
    public async Task CancelledMatch_DoesNotEnterFullResolutionSearch()
    {
        using var template = CreateBalancedFineDetail(20, 20);
        using var scene = CreateScene(640, 480);
        using var frame = FrameFrom(scene);
        using var matcher = new OpenCvMatcher();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Func<Task> match = () => matcher.MatchAsync(frame, Encode(template), 0.99, ct: cancellation.Token);

        await match.Should().ThrowAsync<OperationCanceledException>();
    }

    private static Mat CreateScene(int width, int height) =>
        new(height, width, MatType.CV_8UC4, new Scalar(42, 42, 42, 255));

    private static Mat CreateNoiseTemplate(int width, int height)
    {
        var random = new Random(10232026);
        var template = new Mat(height, width, MatType.CV_8UC4);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                byte value = (byte)random.Next(256);
                template.Set(y, x, new Vec4b(value, value, value, 255));
            }
        return template;
    }

    private static Mat CreateBalancedFineDetail(int width, int height)
    {
        var random = new Random(2926);
        var template = new Mat(height, width, MatType.CV_8UC4);
        for (int y = 0; y < height; y += 2)
            for (int x = 0; x < width; x += 2)
            {
                // Every 2×2 block has the same mean but a different full-size pattern.
                int pattern = random.Next(3);
                for (int row = 0; row < 2; row++)
                    for (int column = 0; column < 2; column++)
                    {
                        bool white = pattern switch { 0 => row == 0, 1 => column == 0, _ => row == column };
                        byte value = white ? (byte)255 : (byte)0;
                        template.Set(y + row, x + column, new Vec4b(value, value, value, 255));
                    }
            }
        return template;
    }

    private static Mat HalfSize(Mat image)
    {
        var resized = new Mat();
        Cv2.Resize(image, resized, new OpenCvSharp.Size(image.Width / 2, image.Height / 2), 0, 0, InterpolationFlags.Area);
        return resized;
    }

    private static (double Score, OpenCvSharp.Point Location) CoarseMaximum(Mat scene, Mat template)
    {
        using var sceneGray = new Mat();
        using var templateGray = new Mat();
        Cv2.CvtColor(scene, sceneGray, ColorConversionCodes.BGRA2GRAY);
        Cv2.CvtColor(template, templateGray, ColorConversionCodes.BGRA2GRAY);
        using var sceneHalf = HalfSize(sceneGray);
        using var templateHalf = HalfSize(templateGray);
        using var result = new Mat();
        Cv2.MatchTemplate(sceneHalf, templateHalf, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out _, out double score, out _, out OpenCvSharp.Point location);
        return (score, location);
    }

    private static void Embed(Mat scene, Mat template, int x, int y)
    {
        using var target = new Mat(scene, new OpenCvSharp.Rect(x, y, template.Width, template.Height));
        template.CopyTo(target);
    }

    private static byte[] Encode(Mat template)
    {
        Cv2.ImEncode(".png", template, out byte[] bytes);
        return bytes;
    }

    private static unsafe CaptureFrame FrameFrom(Mat scene)
    {
        int stride = scene.Width * 4;
        var owner = new ArrayOwner(stride * scene.Height);
        fixed (byte* destination = owner.Memory.Span)
            Buffer.MemoryCopy((void*)scene.Data, destination, owner.Memory.Length, owner.Memory.Length);
        return new CaptureFrame(scene.Width, scene.Height, stride, FramePixelFormat.Bgra32, CaptureSource.Gdi, owner);
    }

    private sealed class ArrayOwner(int size) : IMemoryOwner<byte>
    {
        public Memory<byte> Memory { get; } = new byte[size];
        public void Dispose() { }
    }
}
