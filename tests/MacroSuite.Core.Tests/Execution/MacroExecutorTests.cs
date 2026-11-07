using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Tests.TestHelpers;
using Xunit;

namespace MacroSuite.Core.Tests.Execution;

public sealed class MacroExecutorTests
{
    [Fact]
    public async Task NestedExecution_PublishesChildIdentityWithoutCaptureOrInput()
    {
        using var capture = new DummyCaptureService();
        using var controller = new MacroController();
        var sink = new BoundedExecutionStatusSink();
        var context = new MacroContext(capture, controller) { StatusSink = sink };
        var first = new DelayStep(TimeSpan.Zero);
        var second = new DelayStep(TimeSpan.Zero);
        var loop = new LoopStep(1, [first, new LoopStep(1, [second]) { MinBodyDelayMs = 0 }]) { MinBodyDelayMs = 0 };
        await new MacroExecutor().ExecuteAsync(new MacroModel { Steps = [loop] }, context);
        sink.Complete();
        var ids = new List<Guid?>();
        await foreach (var status in sink.Reader.ReadAllAsync())
            if (status.Phase == "Executing") ids.Add(status.ActionId);
        ids.Should().ContainInOrder(first.StepId, second.StepId);
    }

    private sealed class DummyCaptureService : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Dxgi;
        public bool IsSupported => true;
        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
            => Task.FromResult(TestFrameFactory.CreateSolidColor(10, 10, 0, 0, 0));
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
            => Task.FromResult(TestFrameFactory.CreateSolidColor(10, 10, 0, 0, 0));
        public void Dispose() { }
    }

    private sealed class TrackingStep : MacroStep
    {
        private readonly List<string> _executionLog;
        private readonly string _name;
        private readonly int _delayMs;

        public TrackingStep(List<string> log, string name, int delayMs = 0)
        {
            _executionLog = log;
            _name = name;
            _delayMs = delayMs;
            Label = name;
        }

        public override async Task ExecuteAsync(MacroContext context, CancellationToken ct = default)
        {
            if (_delayMs > 0)
            {
                await Task.Delay(_delayMs, ct);
            }
            _executionLog.Add(_name);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RunsStepsInOrder_AndSetsCompletedState()
    {
        using var capture = new DummyCaptureService();
        using var controller = new MacroController();
        var context = new MacroContext(capture, controller);
        var executor = new MacroExecutor();

        var log = new List<string>();
        var macro = new MacroModel
        {
            Name = "Sequential Test",
            Steps = new MacroStep[]
            {
                new TrackingStep(log, "Step1"),
                new TrackingStep(log, "Step2"),
                new TrackingStep(log, "Step3")
            }
        };

        await executor.ExecuteAsync(macro, context);

        executor.State.Should().Be(ExecutionState.Completed);
        log.Should().Equal("Step1", "Step2", "Step3");
    }

    [Fact]
    public async Task ExecuteAsync_WithRepeatCount_RepeatsExecution()
    {
        using var capture = new DummyCaptureService();
        using var controller = new MacroController();
        var context = new MacroContext(capture, controller);
        var executor = new MacroExecutor();

        var log = new List<string>();
        var macro = new MacroModel
        {
            Name = "Repeat Test",
            Settings = new MacroSettings { RepeatCount = 3 },
            Steps = new MacroStep[]
            {
                new TrackingStep(log, "A")
            }
        };

        await executor.ExecuteAsync(macro, context);

        executor.State.Should().Be(ExecutionState.Completed);
        log.Should().Equal("A", "A", "A");
    }

    [Fact]
    public async Task ExecuteAsync_Panic_HaltsImmediatelyAndSetsAbortedState()
    {
        using var capture = new DummyCaptureService();
        using var controller = new MacroController();
        var context = new MacroContext(capture, controller);
        var executor = new MacroExecutor();

        var log = new List<string>();
        var macro = new MacroModel
        {
            Name = "Panic Test",
            Steps = new MacroStep[]
            {
                new TrackingStep(log, "BeforePanic", delayMs: 10),
                new PanicStep(controller),
                new TrackingStep(log, "AfterPanic")
            }
        };

        await executor.ExecuteAsync(macro, context);

        executor.State.Should().Be(ExecutionState.Aborted);
        log.Should().Equal("BeforePanic");
        log.Should().NotContain("AfterPanic");
    }

    private sealed class PanicStep : MacroStep
    {
        private readonly IMacroControl _control;

        public PanicStep(IMacroControl control)
        {
            _control = control;
        }

        public override Task ExecuteAsync(MacroContext context, CancellationToken ct = default)
        {
            _control.Panic();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ExecuteAsync_ExceedingMaxIterations_ThrowsInvalidOperationException()
    {
        using var capture = new DummyCaptureService();
        using var controller = new MacroController();
        var context = new MacroContext(capture, controller)
        {
            MaxIterations = 5 // Low limit for test
        };
        var executor = new MacroExecutor();

        var log = new List<string>();
        var macro = new MacroModel
        {
            Settings = new MacroSettings { RepeatCount = 10, MaxIterations = 5 },
            Steps = new MacroStep[] { new TrackingStep(log, "Step") }
        };

        var act = () => executor.ExecuteAsync(macro, context);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*maximum allowable iterations*");

        executor.State.Should().Be(ExecutionState.Faulted);
    }

    [Fact]
    public async Task ExecuteAsync_WithRepeatUntilStopped_RunsContinuouslyUntilCancelled()
    {
        using var capture = new DummyCaptureService();
        using var controller = new MacroController();
        var context = new MacroContext(capture, controller);
        var executor = new MacroExecutor();

        using var cts = new CancellationTokenSource();
        var iterations = 0;

        var macro = new MacroModel
        {
            Name = "Infinite Repeat Test",
            Settings = new MacroSettings { RepeatUntilStopped = true },
            Steps = new MacroStep[]
            {
                new InlineActionStep(() =>
                {
                    iterations++;
                    if (iterations >= 5)
                    {
                        cts.Cancel();
                    }
                })
            }
        };

        await executor.ExecuteAsync(macro, context, cts.Token);

        executor.State.Should().Be(ExecutionState.Stopped);
        iterations.Should().Be(5);
    }

    [Fact]
    public async Task ExecuteAsync_WithRepeatUntilStopped_IgnoresMaxRuntimeAndMaxIterations()
    {
        using var capture = new DummyCaptureService();
        using var controller = new MacroController();
        var context = new MacroContext(capture, controller)
        {
            MaxRuntime = TimeSpan.FromMilliseconds(1),
            MaxIterations = 3,
            StartedAtUtc = DateTime.UtcNow.AddSeconds(-5),
            EnforceRuntimeCap = false
        };
        var executor = new MacroExecutor();

        using var cts = new CancellationTokenSource();
        var iterations = 0;

        var macro = new MacroModel
        {
            Name = "Infinite Ignores Caps",
            Settings = new MacroSettings
            {
                RepeatUntilStopped = true,
                MaxIterations = 3,
                MaxRuntime = TimeSpan.FromMilliseconds(1)
            },
            Steps =
            [
                new InlineActionStep(() =>
                {
                    iterations++;
                    if (iterations >= 8)
                        cts.Cancel();
                })
            ]
        };

        await executor.ExecuteAsync(macro, context, cts.Token);

        executor.State.Should().Be(ExecutionState.Stopped);
        iterations.Should().Be(8);
    }

    [Fact]
    public async Task ExecuteAsync_MaxRuntimeElapsed_SetsStoppedNotCompleted()
    {
        using var capture = new DummyCaptureService();
        using var controller = new MacroController();
        var context = new MacroContext(capture, controller)
        {
            MaxRuntime = TimeSpan.FromMilliseconds(1),
            StartedAtUtc = DateTime.UtcNow.AddSeconds(-5)
        };
        var executor = new MacroExecutor();

        var iterations = 0;
        var macro = new MacroModel
        {
            Name = "Timed Out",
            Settings = new MacroSettings { RepeatCount = 50, MaxRuntime = TimeSpan.FromMilliseconds(1) },
            Steps = [new InlineActionStep(() => iterations++)]
        };

        await executor.ExecuteAsync(macro, context);

        executor.State.Should().Be(ExecutionState.Stopped);
        iterations.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_HonorsRecordedTimestampsScaledByPlaybackSpeed()
    {
        using var capture = new DummyCaptureService();
        using var controller = new MacroController();
        var context = new MacroContext(capture, controller) { PlaybackSpeed = 2.0 };
        var executor = new MacroExecutor();

        var macro = new MacroModel
        {
            Name = "Timed Playback",
            Steps = new MacroStep[]
            {
                new ClickStep(new Point(10, 10)) { RelativeTimestamp = TimeSpan.FromMilliseconds(10) },
                new ClickStep(new Point(20, 20)) { RelativeTimestamp = TimeSpan.FromMilliseconds(90) }
            }
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await executor.ExecuteAsync(macro, context);
        sw.Stop();

        executor.State.Should().Be(ExecutionState.Completed);
        // 80ms recorded gap at 2x => ~40ms. Allow scheduler jitter.
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(20));
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(250));
    }

    private sealed class InlineActionStep : MacroStep
    {
        private readonly Action _action;
        public InlineActionStep(Action action) => _action = action;
        public override Task ExecuteAsync(MacroContext context, CancellationToken ct = default)
        {
            _action();
            return Task.CompletedTask;
        }
    }
}
