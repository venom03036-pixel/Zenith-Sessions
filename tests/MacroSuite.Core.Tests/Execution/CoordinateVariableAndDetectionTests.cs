using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Tests.TestHelpers;
using MacroSuite.Core.Vision;
using Xunit;

namespace MacroSuite.Core.Tests.Execution;

public sealed class CoordinateVariableAndDetectionTests
{
    private sealed class DummyCaptureService : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;
        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
            => Task.FromResult(TestFrameFactory.CreateSolidColor(10, 10, 0, 0, 0));
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
            => Task.FromResult(TestFrameFactory.CreateSolidColor(10, 10, 0, 0, 0));
        public void Dispose() { }
    }

    private sealed class TrackingInputPlayer : IInputPlayer
    {
        public List<InputEvent> Played { get; } = [];
        public bool IsPlaying => false;
        public Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default)
        {
            Played.AddRange(events);
            return Task.CompletedTask;
        }
        public void ReleaseAllHeldKeys() { }
        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false) { }
        public void SendRelativeMove(int dx, int dy) { }
        public void Dispose() { }
    }

    [Fact]
    public void RecordDetection_WritesLastDetectionVariables()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);
        var result = new ColorDetectionResult
        {
            Matched = true,
            Confidence = 0.8,
            MatchPercentage = 0.5,
            Distance = 4.2,
            BestMatchPoint = new Point(120, 80),
            MatchingBounds = new Rectangle(100, 60, 40, 30),
            CaptureRegion = new Rectangle(100, 60, 40, 30)
        };

        context.RecordDetection(result);

        context.Variables.Get<int>(MacroVariableNames.LastDetectedX).Should().Be(120);
        context.Variables.Get<int>(MacroVariableNames.LastDetectedY).Should().Be(80);
        context.Variables.Get<int>(MacroVariableNames.LastDetectedXAlias).Should().Be(120);
        context.Variables.Get<int>(MacroVariableNames.LastDetectedYAlias).Should().Be(80);
        context.Variables.Get<double>(MacroVariableNames.LastDetectedConfidence).Should().Be(0.8);
        context.Variables.Get<double>(MacroVariableNames.LastDetectedPercentage).Should().Be(0.5);
        context.Variables.Get<double>(MacroVariableNames.LastDetectedDistance).Should().Be(4.2);
        context.Variables.Get<int>(MacroVariableNames.LastDetectedRegionX).Should().Be(100);
        context.Variables.Get<int>(MacroVariableNames.LastDetectedRegionWidth).Should().Be(40);
        context.Variables.Get<int>(MacroVariableNames.LastDetectionX).Should().Be(120);
        context.Variables.Get<int>(MacroVariableNames.LastDetectionY).Should().Be(80);
        context.Variables.Get<int>(MacroVariableNames.LastDetectionRegionWidth).Should().Be(40);
        CoordinateVariableResolver.Resolve(context, "$LastDetection.X", 0, "X").Should().Be(120);
        CoordinateVariableResolver.Resolve(context, "$LastDetection.BestMatchPoint.Y", 0, "Y").Should().Be(80);
        CoordinateVariableResolver.Resolve(context, "$Vision.LastDetection.Region.X", 0, "X").Should().Be(100);
    }

    [Fact]
    public void Resolve_MissingVariable_ThrowsBeforeInput()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);

        var act = () => CoordinateVariableResolver.Resolve(context, "$LastDetectedX", 10, "X");

        act.Should().Throw<InvalidOperationException>().WithMessage("*LastDetectedX*");
    }

    [Fact]
    public async Task ClickStep_ResolvesAliasVariable()
    {
        using var capture = new DummyCaptureService();
        var player = new TrackingInputPlayer();
        var context = new MacroContext(capture) { InputPlayer = player };
        context.Variables.Set(MacroVariableNames.LastDetectedXAlias, 333);
        context.Variables.Set(MacroVariableNames.LastDetectedYAlias, 444);

        await new ClickStep(new Point(1, 2))
        {
            XVariable = "$LastDetectedX",
            YVariable = "$LastDetectedY"
        }.ExecuteAsync(context);

        player.Played.Should().Contain(e => e.Type == InputEventType.MouseDown && e.X == 333 && e.Y == 444);
    }

    [Fact]
    public async Task ClickStep_ResolvesDottedLastDetectionPath()
    {
        using var capture = new DummyCaptureService();
        var player = new TrackingInputPlayer();
        var context = new MacroContext(capture) { InputPlayer = player };
        context.RecordDetection(new ColorDetectionResult
        {
            Matched = true,
            BestMatchPoint = new Point(210, 77),
            CaptureRegion = new Rectangle(200, 60, 40, 30)
        });

        await new ClickStep(new Point(1, 2))
        {
            XVariable = "$LastDetection.BestMatchPoint.X",
            YVariable = "$LastDetection.Y"
        }.ExecuteAsync(context);

        player.Played.Should().Contain(e => e.Type == InputEventType.MouseDown && e.X == 210 && e.Y == 77);
    }

    [Fact]
    public async Task MouseMoveStep_ResolvesCanonicalVariable_ForAbsoluteMoves()
    {
        using var capture = new DummyCaptureService();
        var player = new TrackingInputPlayer();
        var context = new MacroContext(capture) { InputPlayer = player };
        context.Variables.Set(MacroVariableNames.LastDetectedX, 50);
        context.Variables.Set(MacroVariableNames.LastDetectedY, 75);

        await new MouseMoveStep(new Point(0, 0))
        {
            XVariable = "$Vision.LastDetectedX",
            YVariable = "$Vision.LastDetectedY"
        }.ExecuteAsync(context);

        player.Played.Should().ContainSingle();
        player.Played[0].X.Should().Be(50);
        player.Played[0].Y.Should().Be(75);
    }

    [Fact]
    public async Task MouseMoveStep_RelativeLook_IgnoresCoordinateVariables()
    {
        using var capture = new DummyCaptureService();
        var player = new TrackingInputPlayer();
        var context = new MacroContext(capture) { InputPlayer = player };

        await new MouseMoveStep(new Point(-12, 4))
        {
            IsRelative = true,
            XVariable = "$Missing",
            YVariable = "$Missing"
        }.ExecuteAsync(context);

        player.Played.Should().ContainSingle();
        player.Played[0].Type.Should().Be(InputEventType.MouseMoveRelative);
        player.Played[0].X.Should().Be(-12);
        player.Played[0].Y.Should().Be(4);
    }

    [Fact]
    public async Task WaitUntilColor_MovesCursorToMatch()
    {
        using var capture = new ColoredCaptureService(100, 150, 200);
        var player = new TrackingInputPlayer();
        var context = new MacroContext(capture) { InputPlayer = player };
        var step = new WaitUntilStep(new ColorCondition(new Point(5, 5), 100, 150, 200))
        {
            Timeout = TimeSpan.FromSeconds(1),
            PollInterval = TimeSpan.FromMilliseconds(5),
            ThrowOnTimeout = true
        };

        await step.ExecuteAsync(context);

        player.Played.Should().Contain(e => e.Type == InputEventType.MouseMove && e.X == 5 && e.Y == 5);
    }

    [Fact]
    public async Task WaitUntilImage_MovesCursorToMatchCenter()
    {
        using var capture = new ColoredCaptureService(0, 0, 0) { OriginX = 100, OriginY = 200 };
        using var matcher = new StubImageMatcher(new MatchResult(true, 0.93, new Rectangle(10, 20, 40, 30)));
        var player = new TrackingInputPlayer();
        var context = new MacroContext(capture)
        {
            InputPlayer = player,
            ImageMatcher = matcher
        };
        var step = new WaitUntilStep(new ImageCondition("template.png", minConfidence: 0.8))
        {
            Timeout = TimeSpan.FromSeconds(1),
            PollInterval = TimeSpan.FromMilliseconds(5),
            ThrowOnTimeout = true
        };

        await step.ExecuteAsync(context);

        player.Played.Should().Contain(e => e.Type == InputEventType.MouseMove && e.X == 130 && e.Y == 235);
    }

    [Fact]
    public async Task WaitWhileColor_DoesNotMoveCursor()
    {
        using var capture = new ColoredCaptureService(0, 0, 0);
        var player = new TrackingInputPlayer();
        var context = new MacroContext(capture) { InputPlayer = player };
        var step = new WaitUntilStep(new ColorCondition(new Point(1, 1), 255, 0, 0))
        {
            Timeout = TimeSpan.FromMilliseconds(40),
            PollInterval = TimeSpan.FromMilliseconds(5),
            WaitWhile = true
        };

        await step.ExecuteAsync(context);

        player.Played.Should().BeEmpty();
    }

    private sealed class ColoredCaptureService : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;
        public int OriginX { get; set; }
        public int OriginY { get; set; }
        private readonly byte _r;
        private readonly byte _g;
        private readonly byte _b;

        public ColoredCaptureService(byte r, byte g, byte b)
        {
            _r = r;
            _g = g;
            _b = b;
        }

        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
            => Task.FromResult(TestFrameFactory.CreateSolidColor(
                Math.Max(1, region.Width),
                Math.Max(1, region.Height),
                _r,
                _g,
                _b,
                originX: OriginX + region.X,
                originY: OriginY + region.Y));

        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
            => CaptureRegionAsync(new Rectangle(0, 0, 1920, 1080), ct);

        public void Dispose() { }
    }

    private sealed class StubImageMatcher : IImageMatcher
    {
        private readonly MatchResult _result;
        public StubImageMatcher(MatchResult result) => _result = result;

        public Task<MatchResult> MatchAsync(
            CaptureFrame frame,
            string templatePath,
            double minConfidence = 0.8,
            Rectangle? searchRegion = null,
            CancellationToken ct = default)
            => Task.FromResult(_result);

        public Task<MatchResult> MatchAsync(
            CaptureFrame frame,
            byte[] templateImageBytes,
            double minConfidence = 0.8,
            Rectangle? searchRegion = null,
            CancellationToken ct = default)
            => Task.FromResult(_result);

        public void Dispose() { }
    }
}
