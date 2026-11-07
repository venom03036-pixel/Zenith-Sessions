using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Tests.TestHelpers;
using Xunit;

namespace MacroSuite.Core.Tests.Steps;

public sealed class StepTests
{
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

    private sealed class CountingStep : MacroStep
    {
        public int ExecutionCount { get; private set; }

        public override Task ExecuteAsync(MacroContext context, CancellationToken ct = default)
        {
            ExecutionCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ConstantCondition : ICondition
    {
        private readonly bool _value;
        public string Description => $"Constant({_value})";

        public ConstantCondition(bool value)
        {
            _value = value;
        }

        public Task<ConditionEvaluation> EvaluateDetailedAsync(MacroContext context, CancellationToken ct = default)
            => Task.FromResult(_value ? ConditionEvaluation.Satisfied : ConditionEvaluation.Unsatisfied);
    }

    private sealed class FailedCondition : ICondition
    {
        public string Description => "failed";
        public Task<ConditionEvaluation> EvaluateDetailedAsync(MacroContext context, CancellationToken ct = default)
            => Task.FromResult(ConditionEvaluation.Fail("boom"));
    }

    [Fact]
    public async Task IfThenElseStep_ConditionTrue_ExecutesThenStepsOnly()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);

        var thenStep = new CountingStep();
        var elseStep = new CountingStep();

        var step = new IfThenElseStep(
            new ConstantCondition(true),
            new[] { thenStep },
            new[] { elseStep });

        await step.ExecuteAsync(context);

        thenStep.ExecutionCount.Should().Be(1);
        elseStep.ExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task IfThenElseStep_FailedCondition_DoesNotTakeElse()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);
        var thenStep = new CountingStep();
        var elseStep = new CountingStep();
        var step = new IfThenElseStep(
            new FailedCondition(),
            new[] { thenStep },
            new[] { elseStep });

        var act = () => step.ExecuteAsync(context);
        await act.Should().ThrowAsync<ConditionEvaluationException>();
        thenStep.ExecutionCount.Should().Be(0);
        elseStep.ExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task IfThenElseStep_ConditionFalse_ExecutesElseStepsOnly()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);

        var thenStep = new CountingStep();
        var elseStep = new CountingStep();

        var step = new IfThenElseStep(
            new ConstantCondition(false),
            new[] { thenStep },
            new[] { elseStep });

        await step.ExecuteAsync(context);

        thenStep.ExecutionCount.Should().Be(0);
        elseStep.ExecutionCount.Should().Be(1);
    }

    [Fact]
    public async Task LoopStep_ExecutesSpecifiedIterations()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);

        var body = new CountingStep();
        var loop = new LoopStep(5, new[] { body });

        await loop.ExecuteAsync(context);

        body.ExecutionCount.Should().Be(5);
    }

    [Fact]
    public async Task LoopStep_StopsEarlyWhenWhileConditionBecomesFalse()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);

        var body = new CountingStep();
        // Stop after 2 iterations
        var dynamicCondition = new DynamicCondition(() => body.ExecutionCount < 2);

        var loop = new LoopStep(10, new[] { body }, whileCondition: dynamicCondition);

        await loop.ExecuteAsync(context);

        body.ExecutionCount.Should().Be(2);
    }

    [Fact]
    public async Task LoopStep_StopsWhenMaxDurationElapses()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);
        var body = new CountingStep();
        var loop = new LoopStep(100_000, new MacroStep[] { body, new DelayStep(TimeSpan.FromMilliseconds(20)) }, new ConstantCondition(true))
        {
            MaxDurationMs = 50
        };

        await loop.ExecuteAsync(context);
        body.ExecutionCount.Should().BeGreaterThan(0);
        body.ExecutionCount.Should().BeLessThan(20);
    }

    [Fact]
    public async Task IfThenElse_NestedInWhile_UsesSameConditionPrimitives()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);
        var thenStep = new CountingStep();
        var innerIf = new IfThenElseStep(new ConstantCondition(true), new[] { thenStep }, []);
        var loop = new LoopStep(3, new MacroStep[] { innerIf });

        await loop.ExecuteAsync(context);
        thenStep.ExecutionCount.Should().Be(3);
    }

    [Fact]
    public async Task LoopStep_RepeatUntil_StopsWhenConditionBecomesTrue()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);
        var body = new CountingStep();
        int checks = 0;
        var until = new DynamicCondition(() => Interlocked.Increment(ref checks) >= 3);
        var loop = new LoopStep(100, new MacroStep[] { body }, until)
        {
            RepeatUntil = true,
            MaxDurationMs = 5_000
        };

        await loop.ExecuteAsync(context);
        body.ExecutionCount.Should().Be(2);
        checks.Should().Be(3);
    }

    private sealed class DynamicCondition : ICondition
    {
        private readonly Func<bool> _predicate;
        public string Description => "Dynamic";

        public DynamicCondition(Func<bool> predicate)
        {
            _predicate = predicate;
        }

        public Task<ConditionEvaluation> EvaluateDetailedAsync(MacroContext context, CancellationToken ct = default)
            => Task.FromResult(_predicate() ? ConditionEvaluation.Satisfied : ConditionEvaluation.Unsatisfied);
    }

    [Fact]
    public async Task WaitUntilStep_ThrowsTimeoutException_WhenConfigured()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);

        var step = new WaitUntilStep(new ConstantCondition(false))
        {
            Timeout = TimeSpan.FromMilliseconds(50),
            PollInterval = TimeSpan.FromMilliseconds(10),
            ThrowOnTimeout = true
        };

        var act = () => step.ExecuteAsync(context);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task DelayStep_ScalesDurationWithPlaybackSpeed()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture)
        {
            PlaybackSpeed = 2.0
        };

        var step = new DelayStep(TimeSpan.FromMilliseconds(100));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await step.ExecuteAsync(context);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(95);
    }

    [Fact]
    public async Task LoopStep_Break_ExitsCurrentLoop()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);
        var before = new CountingStep();
        var after = new CountingStep();
        var loop = new LoopStep(10, [before, new BreakStep(), after]);

        await loop.ExecuteAsync(context);

        before.ExecutionCount.Should().Be(1);
        after.ExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task LoopStep_Continue_SkipsRestOfBody()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);
        var before = new CountingStep();
        var after = new CountingStep();
        var loop = new LoopStep(3, [before, new ContinueStep(), after]);

        await loop.ExecuteAsync(context);

        before.ExecutionCount.Should().Be(3);
        after.ExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task NestedIf_Break_PropagatesToLoop()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);
        var afterIf = new CountingStep();
        var conditionalBreak = new IfThenElseStep(
            new ConstantCondition(true),
            [new BreakStep()]);
        var loop = new LoopStep(10, [conditionalBreak, afterIf]);

        await loop.ExecuteAsync(context);

        afterIf.ExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task LoopStep_EnforcesMaximumNestingDepth()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture) { MaxLoopDepth = 1 };
        var inner = new LoopStep(1, []);
        var outer = new LoopStep(1, [inner]);

        var act = () => outer.ExecuteAsync(context);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*maximum depth*");
    }

    [Fact]
    public async Task WaitUntilStep_WaitWhile_CompletesWhenConditionBecomesFalse()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);
        int checks = 0;
        var condition = new DynamicCondition(() => Interlocked.Increment(ref checks) < 3);
        var step = new WaitUntilStep(condition)
        {
            Timeout = TimeSpan.FromSeconds(2),
            PollInterval = TimeSpan.FromMilliseconds(5),
            WaitWhile = true,
            ThrowOnTimeout = true
        };

        await step.ExecuteAsync(context);
        checks.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task UnknownStep_ExecuteAsync_ThrowsConfigurationError()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);
        var step = new UnknownStep("future_quantum_click");

        var act = () => step.ExecuteAsync(context);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*future_quantum_click*");
    }

    [Fact]
    public async Task MacroExecutor_UnknownStep_FaultsInsteadOfSkipping()
    {
        using var capture = new DummyCaptureService();
        using var controller = new MacroController();
        var context = new MacroContext(capture, controller);
        var executor = new MacroExecutor();
        var macro = new MacroModel
        {
            Steps = [new UnknownStep("unregistered_action")]
        };

        var act = () => executor.ExecuteAsync(macro, context);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unregistered_action*");
        executor.State.Should().Be(ExecutionState.Faulted);
    }
}
