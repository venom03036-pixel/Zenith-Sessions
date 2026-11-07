using System.Buffers;
using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.OpenCV;
using OpenCvSharp;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.OpenCV;

public sealed class OpenCvMatcherTests
{
    private sealed class ArrayMemoryOwner : IMemoryOwner<byte>
    {
        public Memory<byte> Memory { get; }

        public ArrayMemoryOwner(int size)
        {
            Memory = new byte[size];
        }

        public void Dispose() { }
    }

    private static byte[] CreateTestPngBytes(int width, int height, Scalar color)
    {
        using var mat = new Mat(height, width, MatType.CV_8UC4, color);
        Cv2.ImEncode(".png", mat, out byte[] bytes);
        return bytes;
    }

    [Fact]
    public void TemplateCache_GetOrAdd_CachesAndReusesInstance()
    {
        using var cache = new TemplateCache();
        byte[] png = CreateTestPngBytes(20, 20, new Scalar(255, 0, 0, 255));

        var t1 = cache.GetOrAdd(png);
        var t2 = cache.GetOrAdd(png);

        t1.Should().BeSameAs(t2);
        t1.Width.Should().Be(20);
        t1.Height.Should().Be(20);
        cache.Count.Should().Be(1);
    }

    private static void CopyMatToOwner(Mat mat, IMemoryOwner<byte> owner)
    {
        int totalBytes = (int)(mat.Total() * mat.ElemSize());
        unsafe
        {
            fixed (byte* pDest = owner.Memory.Span)
            {
                Buffer.MemoryCopy((void*)mat.Data, pDest, totalBytes, totalBytes);
            }
        }
    }

    [Fact]
    public async Task OpenCvMatcher_FindsEmbeddedTemplate_ReturnsCorrectCoordinates()
    {
        int sceneW = 100;
        int sceneH = 100;
        int tplW = 20;
        int tplH = 20;
        int targetX = 35;
        int targetY = 45;

        // 1. Create a template Mat with a distinct pattern (red background with a green circle)
        using var tplMat = new Mat(tplH, tplW, MatType.CV_8UC4, new Scalar(0, 0, 255, 255));
        Cv2.Circle(tplMat, new OpenCvSharp.Point(tplW / 2, tplH / 2), tplW / 4, new Scalar(0, 255, 0, 255), -1);
        Cv2.ImEncode(".png", tplMat, out byte[] tplBytes);

        // 2. Create scene Mat with background noise/black, and embed the exact template at (targetX, targetY)
        using var sceneMat = new Mat(sceneH, sceneW, MatType.CV_8UC4, new Scalar(50, 50, 50, 255));
        using (var targetRoi = new Mat(sceneMat, new OpenCvSharp.Rect(targetX, targetY, tplW, tplH)))
        {
            tplMat.CopyTo(targetRoi);
        }

        // 3. Convert sceneMat to CaptureFrame
        int stride = sceneW * 4;
        var owner = new ArrayMemoryOwner(stride * sceneH);
        CopyMatToOwner(sceneMat, owner);

        using var frame = new CaptureFrame(sceneW, sceneH, stride, FramePixelFormat.Bgra32, CaptureSource.Dxgi, owner);
        using var matcher = new OpenCvMatcher();

        MatchResult result = await matcher.MatchAsync(frame, tplBytes, minConfidence: 0.85);

        result.Success.Should().BeTrue();
        result.Confidence.Should().BeGreaterThanOrEqualTo(0.85);
        result.BoundingBox.X.Should().Be(targetX);
        result.BoundingBox.Y.Should().Be(targetY);
        result.BoundingBox.Width.Should().Be(tplW);
        result.BoundingBox.Height.Should().Be(tplH);
        result.Center.Should().Be(new System.Drawing.Point(targetX + (tplW / 2), targetY + (tplH / 2)));
    }

    [Fact]
    public async Task OpenCvMatcher_MissingTemplate_ReturnsUnsuccessfulMatch()
    {
        int sceneW = 80;
        int sceneH = 80;

        using var sceneMat = new Mat(sceneH, sceneW, MatType.CV_8UC4, new Scalar(50, 50, 50, 255));
        using var tplMat = new Mat(15, 15, MatType.CV_8UC4, new Scalar(0, 0, 255, 255));
        Cv2.Circle(tplMat, new OpenCvSharp.Point(7, 7), 4, new Scalar(0, 255, 0, 255), -1);
        Cv2.ImEncode(".png", tplMat, out byte[] tplBytes);

        int stride = sceneW * 4;
        var owner = new ArrayMemoryOwner(stride * sceneH);
        CopyMatToOwner(sceneMat, owner);

        using var frame = new CaptureFrame(sceneW, sceneH, stride, FramePixelFormat.Bgra32, CaptureSource.Dxgi, owner);
        using var matcher = new OpenCvMatcher();

        MatchResult result = await matcher.MatchAsync(frame, tplBytes, minConfidence: 0.8);

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task OpenCvMatcher_TemplateLargerThanScene_ReportsSearchAndTemplateSizes()
    {
        int sceneW = 20;
        int sceneH = 20;

        using var sceneMat = new Mat(sceneH, sceneW, MatType.CV_8UC4, new Scalar(0, 0, 0, 255));
        using var tplMat = new Mat(50, 50, MatType.CV_8UC4, new Scalar(255, 255, 255, 255));
        Cv2.ImEncode(".png", tplMat, out byte[] tplBytes);

        int stride = sceneW * 4;
        var owner = new ArrayMemoryOwner(stride * sceneH);
        CopyMatToOwner(sceneMat, owner);

        using var frame = new CaptureFrame(sceneW, sceneH, stride, FramePixelFormat.Bgra32, CaptureSource.Dxgi, owner);
        using var matcher = new OpenCvMatcher();

        MatchResult result = await matcher.MatchAsync(frame, tplBytes, minConfidence: 0.8);

        result.Status.Should().Be(MatchOutcome.Failed);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("ImageSearchRegionTooSmall");
        result.ErrorMessage.Should().Contain("20 x 20 pixels").And.Contain("50 x 50 pixels")
            .And.Contain("Choose a larger search area or capture a smaller template");
    }

    [Theory]
    [InlineData(5, 7, 19, 25, 19, 25)]
    [InlineData(5, 7, 25, 19, 25, 19)]
    [InlineData(90, 7, 25, 25, 10, 25)]
    [InlineData(5, 90, 25, 25, 25, 10)]
    public async Task OpenCvMatcher_UndersizedRoi_ReportsActualClippedSize(
        int x, int y, int width, int height, int clippedWidth, int clippedHeight)
    {
        using var scene = new Mat(100, 100, MatType.CV_8UC4, new Scalar(20, 20, 20, 255));
        using var template = new Mat(20, 20, MatType.CV_8UC4, new Scalar(0, 0, 255, 255));
        Cv2.Circle(template, new OpenCvSharp.Point(10, 10), 5, new Scalar(0, 255, 0, 255), -1);
        Cv2.ImEncode(".png", template, out byte[] templateBytes);
        var owner = new ArrayMemoryOwner(100 * 100 * 4);
        CopyMatToOwner(scene, owner);
        using var frame = new CaptureFrame(100, 100, 400, FramePixelFormat.Bgra32, CaptureSource.Gdi, owner);
        using var matcher = new OpenCvMatcher();

        MatchResult result = await matcher.MatchAsync(frame, templateBytes, searchRegion: new Rectangle(x, y, width, height));

        result.Status.Should().Be(MatchOutcome.Failed, "a template that cannot fit is a configuration error, not an absent image");
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("ImageSearchRegionTooSmall");
        result.ErrorMessage.Should().Contain($"{clippedWidth} x {clippedHeight} pixels")
            .And.Contain("20 x 20 pixels")
            .And.Contain("Choose a larger search area or capture a smaller template");
    }
}
