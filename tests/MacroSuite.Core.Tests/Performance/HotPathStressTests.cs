using System.Diagnostics;
using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Capture;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Tests.TestHelpers;
using MacroSuite.Core.Vision;
using Xunit;

namespace MacroSuite.Core.Tests.Performance;

public sealed class HotPathStressTests
{
    [Fact]
    public void ComputeSampleStride_FullHd_KeepsSamplesUnderCap()
    {
        int stride = ColorDetector.ComputeSampleStride(1920, 1080);
        stride.Should().BeGreaterThan(1);
        int samples = ((1920 + stride - 1) / stride) * ((1080 + stride - 1) / stride);
        samples.Should().BeLessThanOrEqualTo(ColorDetector.DefaultMaximumSamples);
    }

    [Fact]
    public void DetectRegion_FullHdStride1_IsAutoCapped()
    {
        using var frame = TestFrameFactory.CreateSolidColor(1920, 1080, 12, 34, 56);
        var result = ColorDetector.DetectRegion(
            frame,
            new Rectangle(0, 0, 1920, 1080),
            new PixelColor(12, 34, 56),
            new ColorMatchSettings
            {
                Mode = ColorComparisonMode.Rgb,
                Tolerance = 8,
                MinimumMatchPercentage = 0.05,
                SampleStride = 1,
                UseSimd = true
            });

        result.SampledPixels.Should().BeGreaterThan(10_000);
        result.SampledPixels.Should().BeLessThanOrEqualTo(ColorDetector.DefaultMaximumSamples);
        result.Matched.Should().BeTrue();
    }

    [Fact]
    public void DetectRegion_FullHd_ThirtyPassesStayInteractive()
    {
        using var frame = TestFrameFactory.CreateSolidColor(1920, 1080, 200, 10, 10);
        var settings = new ColorMatchSettings
        {
            Mode = ColorComparisonMode.Rgb,
            Tolerance = 20,
            MinimumMatchPercentage = 0.05,
            SampleStride = 1
        };
        var target = new PixelColor(200, 10, 10);

        var sw = Stopwatch.StartNew();
        ColorDetectionResult last = default!;
        for (int i = 0; i < 30; i++)
        {
            last = ColorDetector.DetectRegion(frame, new Rectangle(0, 0, 1920, 1080), target, settings);
        }

        sw.Stop();
        last.Matched.Should().BeTrue();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void MouseCompression_TenThousandEvents_CompletesQuickly()
    {
        var engine = new MouseCompressionEngine(new CompressionOptions
        {
            MinDistancePx = 5,
            MinTimeMs = 15,
            PreserveDirectionChanges = true
        });
        var output = new List<InputEvent>(10_000);
        long ticks = 1000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10_000; i++)
        {
            var ev = new InputEvent(InputEventType.MouseMove, ticks += 10, 100 + (i % 200), 200 + (i % 200), 0, 0);
            engine.Process(in ev, output);
        }
        engine.Flush(output);
        sw.Stop();

        output.Count.Should().BeGreaterThan(0).And.BeLessThan(10_000);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task SharedCapture_FiveHundredSequentialRegions_DisposeCleanly()
    {
        var source = new CountingCapture();
        await using var shared = new SharedCaptureService(source);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 500; i++)
        {
            using CaptureFrame frame = await shared.CaptureRegionAsync(new Rectangle(i % 40, i % 20, 32, 24));
            frame.Width.Should().Be(32);
            frame.IsDisposed.Should().BeFalse();
        }

        sw.Stop();
        source.RegionCaptures.Should().Be(500);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ColorCondition_LargeRegion_DoesNotScanEveryPixel()
    {
        using var capture = new SolidCapture(1920, 1080, 40, 80, 120);
        var context = new MacroContext(capture);
        var condition = new ColorCondition(new Rectangle(0, 0, 1920, 1080), 40, 80, 120, tolerance: 8, minMatchPercentage: 0.05);

        bool matched = await condition.EvaluateAsync(context);

        matched.Should().BeTrue();
        context.LastColorDetection.Should().NotBeNull();
        context.LastColorDetection!.SampledPixels.Should().BeLessThanOrEqualTo(ColorDetector.DefaultMaximumSamples);
    }

    [Fact]
    public async Task ImageCondition_UsesForegroundClientInsteadOfFullDesktop()
    {
        var capture = new CountingCapture();
        var windows = new StubWindows
        {
            Handle = (IntPtr)0x1234,
            ClientWidth = 800,
            ClientHeight = 600,
            ScreenX = 40,
            ScreenY = 80
        };
        var matcher = new StubMatcher();
        var context = new MacroContext(capture)
        {
            ImageMatcher = matcher,
            WindowCoordinates = windows
        };
        var condition = new ImageCondition("template.png", minConfidence: 0.5);

        await condition.EvaluateAsync(context);

        capture.ScreenCaptures.Should().Be(0);
        capture.RegionCaptures.Should().Be(1);
        capture.LastRegion.Should().Be(new Rectangle(40, 80, 800, 600));
    }

    [Fact]
    public void DetectPixel_TwentyThousand_StaysInteractive()
    {
        using var frame = TestFrameFactory.CreateSolidColor(64, 64, 8, 16, 24);
        var settings = new ColorMatchSettings { Mode = ColorComparisonMode.Rgb, Tolerance = 4 };
        var target = new PixelColor(8, 16, 24);
        var sw = Stopwatch.StartNew();
        ColorDetectionResult last = default!;
        for (int i = 0; i < 20_000; i++)
            last = ColorDetector.DetectPixel(frame, i % 64, (i / 64) % 64, target, settings);
        sw.Stop();
        last.Matched.Should().BeTrue();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ColorCondition_ThirtyLargeRegionEvals_StayCapped()
    {
        using var capture = new SolidCapture(1920, 1080, 40, 80, 120);
        var context = new MacroContext(capture);
        var condition = new ColorCondition(new Rectangle(0, 0, 1920, 1080), 40, 80, 120, tolerance: 8, minMatchPercentage: 0.05);
        var sw = Stopwatch.StartNew();
        bool last = false;
        for (int i = 0; i < 30; i++)
            last = await condition.EvaluateAsync(context);
        sw.Stop();
        last.Should().BeTrue();
        context.LastColorDetection!.SampledPixels.Should().BeLessThanOrEqualTo(ColorDetector.DefaultMaximumSamples);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task VisualMatcher_ColorPresentFullHd_IsAutoStrided()
    {
        using var capture = new SolidCapture(1920, 1080, 10, 20, 30);
        var context = new MacroContext(capture);
        var matcher = new VisualMatcher();
        var options = new VisualMatchOptions
        {
            Kind = VisualMatchKind.ColorPresent,
            SearchScope = VisualSearchScope.Fixed,
            X = 0,
            Y = 0,
            Width = 1920,
            Height = 1080,
            R = 10,
            G = 20,
            B = 30,
            SimilarityPercent = 50,
            RequiredPixelFraction = 0.05,
            SampleStride = 1,
            Algorithm = VisualColorAlgorithm.LegacyManhattan,
            LegacyTolerance = 8
        };

        bool matched = await matcher.EvaluateOnceAsync(context, options, CancellationToken.None);

        matched.Should().BeTrue();
        context.LastColorDetection.Should().NotBeNull();
        context.LastColorDetection!.SampledPixels.Should().BeLessThanOrEqualTo(ColorDetector.DefaultMaximumSamples);
    }

    private sealed class CountingCapture : IScreenCaptureService
    {
        public int RegionCaptures;
        public int ScreenCaptures;
        public Rectangle LastRegion;
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;

        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
        {
            Interlocked.Increment(ref RegionCaptures);
            LastRegion = region;
            return Task.FromResult(TestFrameFactory.CreateSolidColor(
                Math.Max(1, region.Width),
                Math.Max(1, region.Height),
                1, 2, 3,
                originX: region.X,
                originY: region.Y));
        }

        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref ScreenCaptures);
            return CaptureRegionAsync(new Rectangle(0, 0, 1920, 1080), ct);
        }

        public void Dispose() { }
    }

    private sealed class SolidCapture : IScreenCaptureService
    {
        private readonly int _w;
        private readonly int _h;
        private readonly byte _r, _g, _b;
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;

        public SolidCapture(int w, int h, byte r, byte g, byte b)
        {
            _w = w;
            _h = h;
            _r = r;
            _g = g;
            _b = b;
        }

        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
            => Task.FromResult(TestFrameFactory.CreateSolidColor(
                region.Width > 0 ? region.Width : _w,
                region.Height > 0 ? region.Height : _h,
                _r, _g, _b,
                originX: region.X,
                originY: region.Y));

        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
            => CaptureRegionAsync(new Rectangle(0, 0, _w, _h), ct);

        public void Dispose() { }
    }

    private sealed class StubWindows : IWindowCoordinateService
    {
        public IntPtr Handle;
        public int ClientWidth;
        public int ClientHeight;
        public int ScreenX;
        public int ScreenY;

        public IntPtr GetWindowAt(int screenX, int screenY) => Handle;
        public IntPtr GetForegroundWindow() => Handle;
        public (int clientX, int clientY) ScreenToClient(IntPtr hWnd, int screenX, int screenY) => (screenX - ScreenX, screenY - ScreenY);
        public (int screenX, int screenY) ClientToScreen(IntPtr hWnd, int clientX, int clientY) => (ScreenX + clientX, ScreenY + clientY);
        public (int width, int height) GetClientSize(IntPtr hWnd) => (ClientWidth, ClientHeight);
        public bool IsWindowValid(IntPtr hWnd) => hWnd == Handle;
        public IntPtr FindWindowByTitle(string? titleOrProcessName) => Handle;
    }

    private sealed class StubMatcher : IImageMatcher
    {
        public Task<MatchResult> MatchAsync(CaptureFrame frame, string templatePath, double minConfidence, Rectangle? searchRegion = null, CancellationToken ct = default)
            => Task.FromResult(MatchResult.NotFound);

        public Task<MatchResult> MatchAsync(CaptureFrame frame, byte[] templateImageBytes, double minConfidence = 0.8, Rectangle? searchRegion = null, CancellationToken ct = default)
            => Task.FromResult(MatchResult.NotFound);

        public void Dispose() { }
    }
}
