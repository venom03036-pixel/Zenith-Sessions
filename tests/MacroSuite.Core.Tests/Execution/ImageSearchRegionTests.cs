using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Tests.TestHelpers;
using Xunit;

namespace MacroSuite.Core.Tests.Execution;

public sealed class ImageSearchRegionTests
{
    [Fact]
    public async Task ThreeExplicitRepeatRegions_ShareUnionAndRestoreEachDesktopClick_WhenFirstImageIsAbsent()
    {
        using var run = new Harness(new Rectangle(-120, 0, 350, 120));
        var regions = new Rectangle?[]
        {
            new(-100, 20, 50, 50), new(0, 30, 50, 50), new(70, 40, 30, 30)
        };

        await run.RunAsync(ThreeGroups(regions, run.Executed));

        run.Capture.Requests[0].Should().Be(new CaptureRequest(false, new Rectangle(-100, 20, 200, 60)));
        run.Capture.Requests.Should().OnlyContain(request => !request.FullScreen);
        AssertInitialSharedScan(run, new Rectangle?[]
        {
            new(0, 0, 50, 50), new(100, 10, 50, 50), new(170, 20, 30, 30)
        });
        AssertCompletedGroups(run, new Point(9, 40), new Point(79, 50));
    }

    [Fact]
    public async Task DefaultAndExplicitRepeatRegions_ShareScreenAndClipEachRegionBeforeMatching()
    {
        using var run = new Harness(new Rectangle(-50, 0, 200, 100));
        var regions = new Rectangle?[]
        {
            null, new(-80, 20, 80, 40), new(100, 40, 80, 80)
        };

        await run.RunAsync(ThreeGroups(regions, run.Executed));

        run.Capture.Requests[0].Should().Be(new CaptureRequest(true, run.Capture.Desktop));
        AssertInitialSharedScan(run, new Rectangle?[]
        {
            null, new(0, 20, 50, 40), new(150, 40, 50, 60)
        });
        AssertCompletedGroups(run, new Point(-41, 30), new Point(109, 50));
    }

    [Fact]
    public async Task OneShotExplicitRegion_CapturesOnlyThatRegionAndRestoresClippedDesktopCoordinates()
    {
        using var run = new Harness(new Rectangle(-50, 0, 200, 100));
        var region = new Rectangle(-80, 20, 80, 40);

        await run.RunAsync([
            new WaitUntilStep(new ImageCondition("OK", searchRegion: region)), DetectedClick()
        ]);

        run.Capture.Requests.Should().Equal(new CaptureRequest(false, region));
        run.Matcher.Calls.Should().ContainSingle();
        run.Matcher.Calls[0].Region.Should().BeNull("the one-shot capture already contains only the selected region");
        run.Matcher.Calls[0].Frame.OriginX.Should().Be(-50);
        run.Matcher.Calls[0].Frame.Width.Should().Be(50);
        run.Input.Events.Where(input => input.Type == InputEventType.MouseDown)
            .Select(input => new Point(input.X, input.Y)).Should().Equal(new Point(-41, 30));
        run.Capture.Frames.Should().OnlyContain(frame => frame.IsDisposed);
        run.Executor.State.Should().Be(ExecutionState.Completed);
    }

    [Fact]
    public void RoundTrip_PreservesPerImageRegionsAlongsideTemplatesAndRepeatSettings()
    {
        var regions = new Rectangle?[]
        {
            new(-100, 20, 50, 50), null, new(100, 40, 80, 80)
        };
        var serializer = new MacroJsonSerializer();
        var original = new MacroModel
        {
            Steps = [
                Trigger("M", regions[0]), Trigger("OK", regions[1]),
                new WaitUntilStep(new ImageCondition("X", new byte[] { 1, 2, 3 }, 0.92, regions[2]))
                {
                    RepeatWhileVisible = true, RepeatDelayMs = 750,
                    PollInterval = TimeSpan.FromMilliseconds(90), RequiredConfirmations = 2
                }
            ]
        };

        MacroModel restored = serializer.Deserialize(serializer.Serialize(original));

        restored.Steps.OfType<WaitUntilStep>().Select(step => ((ImageCondition)step.Condition).SearchRegion)
            .Should().Equal(regions);
        restored.Steps.Select(step => step.StepId).Should().Equal(original.Steps.Select(step => step.StepId));
        var last = restored.Steps[2].Should().BeOfType<WaitUntilStep>().Subject;
        var image = last.Condition.Should().BeOfType<ImageCondition>().Subject;
        image.TemplatePath.Should().Be("X");
        image.TemplateBytes.Should().Equal(new byte[] { 1, 2, 3 });
        image.MinConfidence.Should().Be(0.92);
        last.RepeatWhileVisible.Should().BeTrue();
        last.RepeatDelayMs.Should().Be(750);
        last.PollInterval.Should().Be(TimeSpan.FromMilliseconds(90));
        last.RequiredConfirmations.Should().Be(2);
    }

    private static IReadOnlyList<MacroStep> ThreeGroups(Rectangle?[] regions, List<string> executed)
        => [
            Trigger("M", regions[0]), new CallbackStep(_ => executed.Add("M")),
            Trigger("OK", regions[1]), DetectedClick(), new CallbackStep(_ => executed.Add("OK")),
            Trigger("X", regions[2]), DetectedClick(), new CallbackStep(async context =>
            {
                executed.Add("X");
                await context.Control.StopAsync();
            })
        ];

    private static WaitUntilStep Trigger(string name, Rectangle? region)
        => new(new ImageCondition(name, searchRegion: region))
        {
            RepeatWhileVisible = true, RepeatDelayMs = 500,
            // A slow poll makes the required fresh turn for the second ready group explicit.
            PollInterval = TimeSpan.FromSeconds(10)
        };

    private static ClickStep DetectedClick() => new(new Point(-999, -999), delayAfterMs: 0)
    {
        XVariable = MacroVariableNames.LastDetectedXAlias,
        YVariable = MacroVariableNames.LastDetectedYAlias
    };

    private static void AssertInitialSharedScan(Harness run, Rectangle?[] relativeRegions)
    {
        run.Matcher.Calls.Take(3).Select(call => call.Template).Should().Equal("M", "OK", "X");
        run.Matcher.Calls.Take(3).Select(call => call.Region).Should().Equal(relativeRegions);
        run.Matcher.Calls.Take(3).Should().OnlyContain(call => ReferenceEquals(call.Frame, run.Capture.Frames[0]));
        run.Matcher.Calls.Where(call => call.Template == "X").Select(call => call.Frame).Distinct().Count()
            .Should().BeGreaterThanOrEqualTo(2, "X must be rechecked on a fresh frame after the OK block");
    }

    private static void AssertCompletedGroups(Harness run, Point expectedOk, Point expectedX)
    {
        run.Executed.Should().Equal(new[] { "OK", "X" }, "an absent first image must not block either later group");
        run.Input.Events.Where(input => input.Type == InputEventType.MouseDown)
            .Select(input => new Point(input.X, input.Y)).Should().Equal(expectedOk, expectedX);
        run.Context.Variables.Get<int>(ImageCondition.LastMatchXVariable).Should().Be(expectedX.X);
        run.Context.Variables.Get<int>(ImageCondition.LastMatchYVariable).Should().Be(expectedX.Y);
        run.Capture.Frames.Should().OnlyContain(frame => frame.IsDisposed);
        run.Executor.State.Should().Be(ExecutionState.Stopped);
    }

    private sealed class CallbackStep : MacroStep
    {
        private readonly Func<MacroContext, Task> _callback;
        public CallbackStep(Action<MacroContext> callback)
            : this(context => { callback(context); return Task.CompletedTask; }) { }
        public CallbackStep(Func<MacroContext, Task> callback) => _callback = callback;
        public override Task ExecuteAsync(MacroContext context, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return _callback(context);
        }
    }

    private sealed class Harness : IDisposable
    {
        public FakeCapture Capture { get; }
        public FakeMatcher Matcher { get; } = new();
        public FakeInput Input { get; } = new();
        public MacroController Controller { get; } = new();
        public MacroExecutor Executor { get; } = new();
        public MacroContext Context { get; }
        public List<string> Executed { get; } = [];

        public Harness(Rectangle desktop)
        {
            Capture = new FakeCapture(desktop);
            Context = new MacroContext(Capture, Controller) { ImageMatcher = Matcher, InputPlayer = Input };
        }

        public async Task RunAsync(IReadOnlyList<MacroStep> steps)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Executor.ExecuteAsync(new MacroModel { Steps = steps }, Context, timeout.Token);
        }

        public void Dispose() { Controller.Dispose(); Capture.Dispose(); Matcher.Dispose(); Input.Dispose(); }
    }

    private sealed record CaptureRequest(bool FullScreen, Rectangle Region);
    private sealed record MatchCall(string Template, CaptureFrame Frame, Rectangle? Region);

    private sealed class FakeCapture(Rectangle desktop) : IScreenCaptureService
    {
        public Rectangle Desktop { get; } = desktop;
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;
        public List<CaptureRequest> Requests { get; } = [];
        public List<CaptureFrame> Frames { get; } = [];

        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
            => CaptureAsync(region, false, ct);

        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
            => CaptureAsync(Desktop, true, ct);

        private Task<CaptureFrame> CaptureAsync(Rectangle requested, bool fullScreen, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(new CaptureRequest(fullScreen, requested));
            Rectangle actual = Rectangle.Intersect(requested, Desktop);
            if (actual.Width <= 0 || actual.Height <= 0)
                throw new InvalidOperationException("Test capture lies outside the desktop.");
            var frame = TestFrameFactory.CreateSolidColor(actual.Width, actual.Height, 10, 20, 30,
                originX: actual.X, originY: actual.Y);
            Frames.Add(frame);
            return Task.FromResult(frame);
        }

        public void Dispose() { foreach (CaptureFrame frame in Frames) frame.Dispose(); }
    }

    private sealed class FakeMatcher : IImageMatcher
    {
        public List<MatchCall> Calls { get; } = [];
        public Task<MatchResult> MatchAsync(CaptureFrame frame, string templatePath, double minConfidence = 0.8,
            Rectangle? searchRegion = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(new MatchCall(templatePath, frame, searchRegion));
            Rectangle area = searchRegion ?? new Rectangle(0, 0, frame.Width, frame.Height);
            // Matcher boxes are relative to the frame, including any matcher ROI offset.
            return Task.FromResult(templatePath == "M" ? MatchResult.NotFound
                : MatchResult.Matched(0.95, new Rectangle(area.X + 4, area.Y + 6, 10, 8)));
        }
        public Task<MatchResult> MatchAsync(CaptureFrame frame, byte[] templateImageBytes, double minConfidence = 0.8,
            Rectangle? searchRegion = null, CancellationToken ct = default)
            => MatchAsync(frame, "bytes", minConfidence, searchRegion, ct);
        public void Dispose() { }
    }

    private sealed class FakeInput : IInputPlayer
    {
        public List<InputEvent> Events { get; } = [];
        public bool IsPlaying => false;
        public Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Events.AddRange(events);
            return Task.CompletedTask;
        }
        public void ReleaseAllHeldKeys() { }
        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false) { }
        public void SendRelativeMove(int dx, int dy) { }
        public void Dispose() { }
    }
}
