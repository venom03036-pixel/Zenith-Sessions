using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Tests.TestHelpers;
using Xunit;

namespace MacroSuite.Core.Tests.Rules;

public sealed class VisualPresenceImageScopeTests
{
    [Theory]
    [InlineData("Screen")]
    [InlineData("screen")]
    public async Task EntireScreen_SearchesDesktopEvenWhenForegroundWindowIsAvailable(string scope)
    {
        using var capture = new RecordingCapture();
        using var matcher = new RecordingMatcher();
        var context = new MacroContext(capture)
        {
            ImageMatcher = matcher,
            WindowCoordinates = new ForegroundWindow()
        };

        var result = await CreateCondition(scope).EvaluateDetailedAsync(context);

        result.IsSatisfied.Should().BeTrue();
        capture.ScreenCalls.Should().Be(1);
        capture.LastRegion.Should().BeNull();
        matcher.LastSearchRegion.Should().BeNull();
        matcher.LastFrameSize.Should().Be(new Size(400, 240));
        context.Variables.Get<int>(ImageCondition.LastMatchXVariable).Should().Be(-188);
        context.Variables.Get<int>(ImageCondition.LastMatchYVariable).Should().Be(-77);
        capture.LastFrame!.IsDisposed.Should().BeTrue();
    }

    [Theory]
    [InlineData("Fixed", 20, 30, 40, 60)]
    [InlineData("Nearby", -30, -20, 140, 160)]
    [InlineData("nearby", -30, -20, 140, 160)]
    [InlineData("Unknown", 20, 30, 40, 60)]
    public async Task BoundedScope_UsesSameBoundsAsColorPresence(
        string scope, int x, int y, int width, int height)
    {
        using var capture = new RecordingCapture();
        using var matcher = new RecordingMatcher();
        var context = new MacroContext(capture) { ImageMatcher = matcher };

        var result = await CreateCondition(scope).EvaluateDetailedAsync(context);

        result.IsSatisfied.Should().BeTrue();
        capture.ScreenCalls.Should().Be(0);
        capture.LastRegion.Should().Be(new Rectangle(x, y, width, height));
        matcher.LastSearchRegion.Should().BeNull();
        matcher.LastFrameSize.Should().Be(new Size(width, height));
        context.Variables.Get<int>(ImageCondition.LastMatchXVariable).Should().Be(x + 12);
        context.Variables.Get<int>(ImageCondition.LastMatchYVariable).Should().Be(y + 23);
        capture.LastFrame!.IsDisposed.Should().BeTrue();
    }

    [Theory]
    [InlineData("Screen")]
    [InlineData("Nearby")]
    public async Task ImageAbsent_CaptureFailureRemainsFailure(string scope)
    {
        using var capture = new RecordingCapture { FailCapture = true };
        using var matcher = new RecordingMatcher();
        var context = new MacroContext(capture) { ImageMatcher = matcher };

        var result = await CreateCondition(scope, VisualPresenceKind.ImageAbsent)
            .EvaluateDetailedAsync(context);

        result.IsFailed.Should().BeTrue();
        result.IsSatisfied.Should().BeFalse();
        matcher.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ImageAbsent_MatcherFailureRemainsFailureAndDisposesCapture()
    {
        using var capture = new RecordingCapture();
        using var matcher = new RecordingMatcher { Result = MatchResult.Failed };
        var context = new MacroContext(capture) { ImageMatcher = matcher };

        var result = await CreateCondition("Screen", VisualPresenceKind.ImageAbsent)
            .EvaluateDetailedAsync(context);

        result.IsFailed.Should().BeTrue();
        result.IsSatisfied.Should().BeFalse();
        capture.LastFrame!.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task MissingMatcher_FailsWithoutCapturing()
    {
        using var capture = new RecordingCapture();
        var context = new MacroContext(capture);

        var result = await CreateCondition("Screen", VisualPresenceKind.ImageAbsent)
            .EvaluateDetailedAsync(context);

        result.IsFailed.Should().BeTrue();
        capture.ScreenCalls.Should().Be(0);
        capture.LastRegion.Should().BeNull();
    }

    private static VisualPresenceCondition CreateCondition(
        string scope, VisualPresenceKind kind = VisualPresenceKind.ImagePresent)
        => new()
        {
            Kind = kind,
            SearchMode = scope,
            X = 20,
            Y = 30,
            Width = 40,
            Height = 60,
            TemplateBytes = [1, 2, 3]
        };

    private sealed class RecordingCapture : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Dxgi;
        public bool IsSupported => true;
        public bool FailCapture { get; init; }
        public int ScreenCalls { get; private set; }
        public Rectangle? LastRegion { get; private set; }
        public CaptureFrame? LastFrame { get; private set; }

        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastRegion = region;
            return Capture(region);
        }

        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ScreenCalls++;
            return Capture(new Rectangle(-200, -100, 400, 240));
        }

        private Task<CaptureFrame> Capture(Rectangle bounds)
        {
            if (FailCapture)
                throw new InvalidOperationException("Capture unavailable.");
            var frame = TestFrameFactory.CreateSolidColor(bounds.Width, bounds.Height, 220, 40, 40,
                originX: bounds.X, originY: bounds.Y);
            LastFrame = frame;
            return Task.FromResult(frame);
        }

        public void Dispose() => LastFrame?.Dispose();
    }

    private sealed class RecordingMatcher : IImageMatcher
    {
        public MatchResult Result { get; init; } = MatchResult.Matched(0.95, new Rectangle(10, 20, 4, 6));
        public int Calls { get; private set; }
        public Size LastFrameSize { get; private set; }
        public Rectangle? LastSearchRegion { get; private set; }

        public Task<MatchResult> MatchAsync(CaptureFrame frame, string templatePath,
            double minConfidence = 0.8, Rectangle? searchRegion = null, CancellationToken ct = default)
            => Match(frame, searchRegion, ct);

        public Task<MatchResult> MatchAsync(CaptureFrame frame, byte[] templateImageBytes,
            double minConfidence = 0.8, Rectangle? searchRegion = null, CancellationToken ct = default)
            => Match(frame, searchRegion, ct);

        private Task<MatchResult> Match(CaptureFrame frame, Rectangle? searchRegion, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            LastFrameSize = new Size(frame.Width, frame.Height);
            LastSearchRegion = searchRegion;
            return Task.FromResult(Result);
        }

        public void Dispose() { }
    }

    private sealed class ForegroundWindow : IWindowCoordinateService
    {
        public IntPtr GetWindowAt(int screenX, int screenY) => new(1);
        public IntPtr GetForegroundWindow() => new(1);
        public (int clientX, int clientY) ScreenToClient(IntPtr hWnd, int screenX, int screenY)
            => (screenX - 100, screenY - 200);
        public (int screenX, int screenY) ClientToScreen(IntPtr hWnd, int clientX, int clientY)
            => (clientX + 100, clientY + 200);
        public (int width, int height) GetClientSize(IntPtr hWnd) => (100, 100);
        public bool IsWindowValid(IntPtr hWnd) => true;
        public IntPtr FindWindowByTitle(string? titleOrProcessName) => new(1);
    }
}
