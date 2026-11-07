using System.Diagnostics;
using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Tests.TestHelpers;
using Xunit;

namespace MacroSuite.Core.Tests.Execution;

public sealed class RecordedTimelineTests
{
    [Theory]
    [InlineData("root")]
    [InlineData("if")]
    [InlineData("loop")]
    public async Task RecordedSegments_DoNotAccumulateTwentyMillisecondActionOverhead(string scope)
    {
        using var run = new Harness();
        run.Input.KeyOverheadMs = 20;
        MacroStep[] recording = Enumerable.Range(0, 12).Select(index => (MacroStep)new KeyPressStep(65 + index, 0)
        { RelativeTimestamp = TimeSpan.FromMilliseconds(index * 40) }).ToArray();
        IReadOnlyList<MacroStep> steps = scope switch
        {
            "if" => [new IfThenElseStep(new TrueCondition(), recording)],
            "loop" => [new LoopStep(1, recording)],
            _ => recording
        };
        await run.ExecuteAsync(steps);
        run.Input.Keys.Should().Equal(Enumerable.Range(65, 12));
        TimeSpan elapsed = run.Input.KeyTimes[^1] - run.Input.KeyTimes[0];
        elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(420));
        // Eleven relative sleeps add at least 220 ms of action overhead. An anchored
        // timeline allows scheduling jitter without accumulating that per-action cost.
        elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(600));
    }

    [Fact]
    public async Task ManualDelay_IsABarrier_AndPreservesTheFollowingRecordedGap()
    {
        using var run = new Harness();
        await run.ExecuteAsync([
            new KeyPressStep(65, 0) { RelativeTimestamp = TimeSpan.FromMilliseconds(10) },
            new DelayStep(TimeSpan.FromMilliseconds(120)),
            new KeyPressStep(66, 0) { RelativeTimestamp = TimeSpan.FromMilliseconds(110) }
        ]);
        TimeSpan elapsed = run.Input.KeyTimes[1] - run.Input.KeyTimes[0];
        elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(205));
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task PauseInsideRelativeMoveBatch_DoesNotConsumeTheFollowingRecordedGap()
    {
        using var run = new Harness();
        TimeSpan batchFinished = TimeSpan.Zero;
        run.Input.OnBatch = async ct =>
        {
            await run.Controller.PauseAsync(ct);
            await Task.Delay(150, ct);
            await run.Controller.ResumeAsync();
            await Task.Delay(40, ct); // The batch's actual recorded duration, excluding pause.
            batchFinished = run.Input.Elapsed;
        };
        await run.ExecuteAsync([
            new MouseMoveStep(new Point(1, 0)) { IsRelative = true, RelativeTimestamp = TimeSpan.Zero },
            new MouseMoveStep(new Point(1, 0)) { IsRelative = true, RelativeTimestamp = TimeSpan.FromMilliseconds(40) },
            new KeyPressStep(65, 0) { RelativeTimestamp = TimeSpan.FromMilliseconds(100) }
        ]);
        (run.Input.KeyTimes.Single() - batchFinished).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(40));
        run.Controller.TotalPausedDuration.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(140));
        run.Executor.State.Should().Be(ExecutionState.Completed);
    }

    [Theory]
    [InlineData("resume")]
    [InlineData("stop")]
    [InlineData("panic")]
    public async Task PauseClock_IncludesCurrentPause_AndFreezesWhenPauseEnds(string finish)
    {
        using var controller = new MacroController();
        await controller.PauseAsync();
        await Task.Delay(30);
        controller.TotalPausedDuration.Should().BeGreaterThan(TimeSpan.FromMilliseconds(15));
        if (finish == "stop") await controller.StopAsync();
        else if (finish == "panic") controller.Panic();
        else await controller.ResumeAsync();
        TimeSpan completed = controller.TotalPausedDuration;
        await Task.Delay(20);
        controller.TotalPausedDuration.Should().Be(completed);
        controller.IsPaused.Should().BeFalse();
    }

    [Theory]
    [InlineData(10)]
    [InlineData(0)]
    public async Task ZeroMovementRows_PreserveBatchLeadInAndTailBeforeManualBarrier(int middleDelta)
    {
        using var run = new Harness();
        run.Input.OnBatch = async ct =>
        {
            var events = run.Input.LastBatch!;
            events.Select(e => e.X).Should().Equal(0, middleDelta, 0);
            events.Select(e => e.TimestampTicks).Should().Equal(
                0, Stopwatch.Frequency / 10, Stopwatch.Frequency / 5);
            await Task.Delay(TimeSpan.FromSeconds(
                (events[^1].TimestampTicks - events[0].TimestampTicks) / (double)Stopwatch.Frequency), ct);
        };
        await run.ExecuteAsync([
            new MouseMoveStep(new Point(0, 0)) { IsRelative = true },
            new MouseMoveStep(new Point(middleDelta, 0)) { IsRelative = true, RelativeTimestamp = TimeSpan.FromMilliseconds(100) },
            new MouseMoveStep(new Point(0, 0)) { IsRelative = true, RelativeTimestamp = TimeSpan.FromMilliseconds(200) },
            new DelayStep(TimeSpan.FromMilliseconds(30)),
            new KeyPressStep(65, 0)
        ]);
        run.Input.KeyTimes.Single().Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(215));
    }

    private sealed class TrueCondition : ICondition
    {
        public string Description => "true";
        public Task<ConditionEvaluation> EvaluateDetailedAsync(MacroContext context, CancellationToken ct = default)
            => Task.FromResult(ConditionEvaluation.Satisfied);
    }

    private sealed class Harness : IDisposable
    {
        private readonly FakeCapture _capture = new();
        public MacroController Controller { get; } = new();
        public FakeInput Input { get; } = new();
        public MacroExecutor Executor { get; } = new();
        public async Task ExecuteAsync(IReadOnlyList<MacroStep> steps)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Executor.ExecuteAsync(new MacroModel { Steps = steps },
                new MacroContext(_capture, Controller) { InputPlayer = Input }, timeout.Token);
        }
        public void Dispose() { Controller.Dispose(); Input.Dispose(); _capture.Dispose(); }
    }

    private sealed class FakeCapture : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;
        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
            => Task.FromResult(TestFrameFactory.CreateSolidColor(1, 1, 0, 0, 0));
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default) => CaptureRegionAsync(Rectangle.Empty, ct);
        public void Dispose() { }
    }

    private sealed class FakeInput : IInputPlayer
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        public int KeyOverheadMs { get; set; }
        public Func<CancellationToken, Task>? OnBatch { get; set; }
        public IReadOnlyList<InputEvent>? LastBatch { get; private set; }
        public List<int> Keys { get; } = [];
        public List<TimeSpan> KeyTimes { get; } = [];
        public TimeSpan Elapsed => _clock.Elapsed;
        public bool IsPlaying => false;
        public async Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (events.Count > 0 && events[0].Type == InputEventType.MouseMoveRelative && OnBatch is not null)
            {
                LastBatch = events;
                await OnBatch(ct);
                return;
            }
            foreach (InputEvent input in events)
                if (input.Type == InputEventType.KeyDown) { Keys.Add(input.Data); KeyTimes.Add(_clock.Elapsed); }
            if (KeyOverheadMs > 0) await Task.Delay(KeyOverheadMs, ct);
        }
        public void ReleaseAllHeldKeys() { }
        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false) { }
        public void SendRelativeMove(int dx, int dy) { }
        public void Dispose() { }
    }
}
