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

public sealed class BlockRoutingTests
{
    [Fact]
    public async Task LegacyReusedActionId_RemainsValidWithoutIdentityNavigation()
    {
        using var run = new Harness();
        MacroStep reused = run.Mark("same");
        await run.RunAsync([reused, reused]);
        run.Actions.Should().Equal("same", "same");
    }

    [Fact]
    public async Task DuplicateJumpTarget_IsRejectedBeforeAnyActionRuns()
    {
        using var run = new Harness();
        MacroStep reused = run.Mark("same");
        var jump = new IfThenElseStep(new PredicateCondition(() => false), [])
        { Routing = new() { OnNotSatisfied = FlowRoute.Jump, TargetStepId = reused.StepId } };
        await FluentActions.Awaiting(() => run.RunAsync([reused, reused, jump]))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*Duplicate*");
        run.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task FalseIf_SkipsInterveningActionsAndDisabledBlocks_ThenTestsEachSibling()
    {
        using var run = new Harness();
        await run.RunAsync([
            run.If(false, "wrong-a"), run.Mark("skip-a"),
            new IfThenElseStep(new PredicateCondition(() => true), [run.Mark("disabled")]) { IsEnabled = false },
            run.If(false, "wrong-b"), run.Mark("skip-b"), run.If(true, "selected"), run.Mark("after")]);
        run.Actions.Should().Equal("selected", "after");
        run.Executor.State.Should().Be(ExecutionState.Completed);
        run.Traces.Should().Contain(e => (e.Data as NavigationRequest) != null
            && ((NavigationRequest)e.Data!).Kind == NavigationKind.NextConditional);
    }

    [Fact]
    public async Task EligibleElse_WinsOverDefaultAndExplicitFalseRoute_ThenContinuesNormally()
    {
        using var run = new Harness();
        var block = new IfThenElseStep(new PredicateCondition(() => false), [run.Mark("then")], [run.Mark("else")])
        { Routing = new() { OnNotSatisfied = FlowRoute.Stop } };
        await run.RunAsync([block, run.Mark("after")]);
        run.Actions.Should().Equal("else", "after");
        run.Executor.State.Should().Be(ExecutionState.Completed);
    }

    [Fact]
    public async Task DisabledElse_DoesNotPreventDefaultContinuation()
    {
        using var run = new Harness();
        var disabled = new CallbackStep(() => run.Actions.Add("disabled")) { IsEnabled = false };
        await run.RunAsync([new IfThenElseStep(new PredicateCondition(() => false), [], [disabled]),
            run.Mark("skip"), run.If(true, "next")]);
        run.Actions.Should().Equal("next");
    }

    [Fact]
    public async Task ElseIfChain_ExecutesOnlyFirstSelectedBranch()
    {
        using var run = new Harness();
        var second = new IfThenElseStep(new PredicateCondition(() => true), [run.Mark("selected")], [run.Mark("wrong-else")]);
        await run.RunAsync([new IfThenElseStep(new PredicateCondition(() => false), [run.Mark("wrong-first")], [second]), run.Mark("after")]);
        run.Actions.Should().Equal("selected", "after");
    }

    [Fact]
    public async Task FinalFalseBlock_NoEligibleContinuation_CompletesWithoutRepeating()
    {
        using var run = new Harness();
        await run.RunAsync([run.Mark("setup"), run.If(false, "wrong")], repeatCount: 3);
        run.Actions.Should().Equal("setup");
        run.Executor.State.Should().Be(ExecutionState.Completed);
        run.Context.PendingNavigation.Should().BeNull();
    }

    [Fact]
    public async Task NestedFailure_UsesLaterSiblingConditionalBeforeParent()
    {
        using var run = new Harness();
        var inner = new IfThenElseStep(new PredicateCondition(() => true),
            [run.If(false, "wrong"), run.Mark("skip"), run.If(true, "inner-next"), run.Mark("inner-after")]);
        await run.RunAsync([inner, run.Mark("outer-after")]);
        run.Actions.Should().Equal("inner-next", "inner-after", "outer-after");
    }

    [Fact]
    public async Task NestedFailure_BubblesOutWithoutEnteringSkippedChildBranches()
    {
        using var run = new Harness();
        var outer = new IfThenElseStep(new PredicateCondition(() => true),
            [run.If(false, "wrong"), run.Mark("skip-inner")], [run.If(true, "wrong-else-child")]);
        await run.RunAsync([outer, run.Mark("skip-outer"), run.If(true, "next")]);
        run.Actions.Should().Equal("next");
        run.Context.CurrentBlockDepth.Should().Be(0);
    }

    [Fact]
    public async Task FalseIfInsideWhile_UnwindsLoopInsteadOfRepeatingFailedBranch()
    {
        using var run = new Harness();
        await run.RunAsync([new LoopStep(20, [run.If(false, "wrong"), run.Mark("skip-loop")], new PredicateCondition(() => true)),
            run.Mark("skip-root"), run.If(true, "next")]);
        run.Actions.Should().Equal("next");
        run.Context.CurrentLoopDepth.Should().Be(0);
    }

    [Fact]
    public async Task FalseWhileInsideIf_FindsNextConditionalAtParentScope()
    {
        using var run = new Harness();
        var whileBlock = new LoopStep(10, [run.Mark("wrong-body")], new PredicateCondition(() => false));
        await run.RunAsync([new IfThenElseStep(new PredicateCondition(() => true), [whileBlock, run.Mark("skip")]),
            run.If(true, "next")]);
        run.Actions.Should().Equal("next");
    }

    [Fact]
    public async Task WhileReevaluatesAfterFinalBodyAction_ThenRoutesWhenFalse()
    {
        using var run = new Harness();
        int iterations = 0;
        var condition = new PredicateCondition(() => iterations < 3);
        var loop = new LoopStep(20, [new CallbackStep(() => { iterations++; run.Actions.Add($"body-{iterations}"); })], condition)
        { CheckIntervalMs = 10 };
        await run.RunAsync([loop, run.Mark("skip"), run.If(true, "next")]);
        run.Actions.Should().Equal("body-1", "body-2", "body-3", "next");
        condition.Checks.Should().Be(4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitContinue_OverridesDefaultSkipForIfAndWhile(bool useLoop)
    {
        using var run = new Harness();
        MacroStep block = useLoop
            ? new LoopStep(10, [], new PredicateCondition(() => false)) { Routing = new() { OnNotSatisfied = FlowRoute.Continue } }
            : new IfThenElseStep(new PredicateCondition(() => false), []) { Routing = new() { OnNotSatisfied = FlowRoute.Continue } };
        await run.RunAsync([block, run.Mark("after")]);
        run.Actions.Should().Equal("after");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitRetry_RechecksConditionBeforeBody(bool useLoop)
    {
        using var run = new Harness();
        int checks = 0;
        var condition = new PredicateCondition(() => ++checks >= 3);
        var options = new BlockRoutingOptions { OnNotSatisfied = FlowRoute.Retry, RetryCount = 2, RetryDelayMs = 10 };
        MacroStep block = useLoop
            ? new LoopStep(1, [run.Mark("selected")], condition) { Routing = options, CheckIntervalMs = 10 }
            : new IfThenElseStep(condition, [run.Mark("selected")]) { Routing = options };
        await run.RunAsync([block, run.Mark("after")]);
        checks.Should().Be(3);
        run.Actions.Should().Equal("selected", "after");
    }

    [Fact]
    public async Task ExhaustedRetry_IsBoundedAndAdvancesToNextConditional()
    {
        using var run = new Harness();
        var condition = new PredicateCondition(() => false);
        await run.RunAsync([new IfThenElseStep(condition, [])
            { Routing = new() { OnNotSatisfied = FlowRoute.Retry, RetryCount = 2, RetryDelayMs = 10 } },
            run.Mark("skip"), run.If(true, "next")]);
        condition.Checks.Should().Be(3);
        run.Actions.Should().Equal("next");
    }

    [Fact]
    public async Task ExplicitStop_DoesNotReachAnyLaterAction()
    {
        using var run = new Harness();
        await run.RunAsync([new IfThenElseStep(new PredicateCondition(() => false), [])
            { Routing = new() { OnNotSatisfied = FlowRoute.Stop } }, run.If(true, "wrong")]);
        run.Actions.Should().BeEmpty();
        run.Executor.State.Should().Be(ExecutionState.Stopped);
    }

    [Fact]
    public async Task JumpFromNestedBlock_UnwindsAndEntersSelectedRootAction()
    {
        using var run = new Harness();
        var target = run.Mark("target");
        var inner = new IfThenElseStep(new PredicateCondition(() => false), [])
        { Routing = new() { OnNotSatisfied = FlowRoute.Jump, TargetStepId = target.StepId } };
        await run.RunAsync([new LoopStep(20, [inner, run.Mark("old-tail")]), run.Mark("skip"), target, run.Mark("after")]);
        run.Actions.Should().Equal("target", "after");
        run.Context.CurrentLoopDepth.Should().Be(0);
        run.Context.CurrentBlockDepth.Should().Be(0);
        run.Input.Releases.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task BackwardJump_PreservesRunVariablesAndExecutionBudget()
    {
        using var run = new Harness();
        int count = 0;
        var target = new CallbackStep(() => { count++; run.Actions.Add($"target-{count}"); run.Context.Variables.Set("counter", count); });
        var gate = new IfThenElseStep(new PredicateCondition(() => count >= 3), [run.Mark("done")])
        { Routing = new() { OnNotSatisfied = FlowRoute.Jump, TargetStepId = target.StepId } };
        await run.RunAsync([target, gate], maxIterations: 6);
        run.Actions.Should().Equal("target-1", "target-2", "target-3", "done");
        run.Context.Variables.Get<int>("counter").Should().Be(3);
    }

    [Fact]
    public async Task EndlessBackwardJump_CannotResetMacroBudget()
    {
        using var run = new Harness();
        var target = run.Mark("target");
        var gate = new IfThenElseStep(new PredicateCondition(() => false), [])
        { Routing = new() { OnNotSatisfied = FlowRoute.Jump, TargetStepId = target.StepId } };
        await FluentActions.Awaiting(() => run.RunAsync([target, gate], maxIterations: 4)).Should().ThrowAsync<InvalidOperationException>();
        run.Actions.Should().Equal("target", "target");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrNestedJumpTarget_FailsBeforeAnySetupInput(bool nested)
    {
        using var run = new Harness();
        var target = run.Mark("target");
        var gate = new IfThenElseStep(new PredicateCondition(() => false), [])
        { Routing = new() { OnNotSatisfied = FlowRoute.Jump, TargetStepId = target.StepId } };
        IReadOnlyList<MacroStep> steps = nested
            ? [run.Mark("setup"), gate, new IfThenElseStep(new PredicateCondition(() => true), [target])]
            : [run.Mark("setup"), gate];
        await FluentActions.Awaiting(() => run.RunAsync(steps)).Should().ThrowAsync<InvalidOperationException>();
        run.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task RecoverableConditionFailure_RoutesWithoutTakingElse()
    {
        using var run = new Harness();
        var block = new IfThenElseStep(new ResultCondition(_ => ConditionEvaluation.Fail("CaptureFrameUnavailable")),
            [run.Mark("then")], [run.Mark("else")]);
        await run.RunAsync([block, run.Mark("skip"), run.If(true, "next")]);
        run.Actions.Should().Equal("next");
    }

    [Theory]
    [InlineData("ImageTemplateMissing")]
    [InlineData("ImageMatcherUnavailable")]
    [InlineData("ColorCaptureRegionInvalid")]
    [InlineData("boom")]
    public async Task ConfigurationAndUnknownFailures_AreNeverConvertedToRoutingMisses(string code)
    {
        using var run = new Harness();
        var block = new IfThenElseStep(new ResultCondition(_ => ConditionEvaluation.Fail(code)), [run.Mark("wrong")])
        { Routing = new() { OnFailure = FlowRoute.Continue } };
        await FluentActions.Awaiting(() => run.RunAsync([block, run.Mark("after")])).Should().ThrowAsync<ConditionEvaluationException>();
        run.Actions.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ConditionFailurePolicy.Stop, false)]
    [InlineData(ConditionFailurePolicy.Skip, true)]
    public async Task LegacyExplicitConditionPolicy_OverridesDefaultFailureRoute(ConditionFailurePolicy policy, bool continues)
    {
        using var run = new Harness();
        run.Capture.FailuresRemaining = int.MaxValue;
        var condition = new VisualPresenceCondition { FailurePolicy = policy };
        await run.RunAsync([new IfThenElseStep(condition, [run.Mark("wrong")]), run.Mark("after")]);
        run.Actions.Should().Equal(continues ? ["after"] : Array.Empty<string>());
        run.Executor.State.Should().Be(continues ? ExecutionState.Completed : ExecutionState.Stopped);
    }

    [Fact]
    public async Task ExplicitBlockFailureRoute_OverridesLegacyConditionStop()
    {
        using var run = new Harness();
        run.Capture.FailuresRemaining = 1;
        var block = new IfThenElseStep(new VisualPresenceCondition { FailurePolicy = ConditionFailurePolicy.Stop }, [])
        { Routing = new() { OnFailure = FlowRoute.Continue } };
        await run.RunAsync([block, run.Mark("after")]);
        run.Actions.Should().Equal("after");
    }

    [Fact]
    public async Task LegacyRetry_RetriesRecoverableConditionFailure()
    {
        using var run = new Harness();
        run.Capture.FailuresRemaining = 2;
        var condition = new VisualPresenceCondition
        {
            FailurePolicy = ConditionFailurePolicy.Retry, Kind = VisualPresenceKind.ColorPresent,
            R = 220, G = 40, B = 40, X = 0, Y = 0, Width = 8, Height = 8
        };
        await run.RunAsync([new IfThenElseStep(condition, [run.Mark("selected")]), run.Mark("after")]);
        run.Capture.Calls.Should().Be(3);
        run.Actions.Should().Equal("selected", "after");
    }

    [Fact]
    public async Task DefaultWaitTimeout_LeavesOwningBlockAndSkipsItsRemainingActions()
    {
        using var run = new Harness();
        var wait = new WaitUntilStep(new PredicateCondition(() => false)) { Timeout = TimeSpan.Zero };
        await run.RunAsync([new IfThenElseStep(new PredicateCondition(() => true), [run.Mark("before"), wait, run.Mark("old-tail")]),
            run.Mark("skip"), run.If(true, "next")]);
        run.Actions.Should().Equal("before", "next");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulConditionLegacyStop_DoesNotOverrideDefaultBodyFailureRoute(bool useLoop)
    {
        using var run = new Harness();
        var condition = new VisualPresenceCondition
        {
            FailurePolicy = ConditionFailurePolicy.Stop, Kind = VisualPresenceKind.ColorPresent,
            R = 220, G = 40, B = 40, X = 0, Y = 0, Width = 8, Height = 8
        };
        IReadOnlyList<MacroStep> body = [new WaitUntilStep(new PredicateCondition(() => false)) { Timeout = TimeSpan.Zero }, run.Mark("abandoned")];
        MacroStep block = useLoop ? new LoopStep(10, body, condition) : new IfThenElseStep(condition, body);
        await run.RunAsync([block, run.Mark("skip"), run.If(true, "next")]);
        run.Actions.Should().Equal("next");
        run.Executor.State.Should().Be(ExecutionState.Completed);
    }

    [Fact]
    public async Task ExplicitWaitTimeoutStop_DoesNotUseOwningBlockContinue()
    {
        using var run = new Harness();
        var wait = new WaitUntilStep(new PredicateCondition(() => false)) { Timeout = TimeSpan.Zero, ThrowOnTimeout = true };
        var block = new IfThenElseStep(new PredicateCondition(() => true), [wait, run.Mark("old-tail")])
        { Routing = new() { OnFailure = FlowRoute.Continue } };
        await FluentActions.Awaiting(() => run.RunAsync([block, run.Mark("after")])).Should().ThrowAsync<TimeoutException>();
        run.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task VisualWaitSkip_RetainsExplicitFollowingActionBehavior()
    {
        using var run = new Harness();
        var wait = new VisualWaitStep { R = 0, G = 255, B = 0, TimeoutMs = 0, FailurePolicy = ConditionFailurePolicy.Skip };
        await run.RunAsync([new IfThenElseStep(new PredicateCondition(() => true), [wait, run.Mark("after-wait")]), run.Mark("after-if")]);
        run.Actions.Should().Equal("after-wait", "after-if");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordedChildren_PreserveLocalTimingAndReturnToContainingScope(bool useLoop)
    {
        using var run = new Harness();
        IReadOnlyList<MacroStep> body =
        [
            new KeyPressStep(65, 0) { RelativeTimestamp = TimeSpan.FromSeconds(10) },
            new KeyPressStep(66, 0) { RelativeTimestamp = TimeSpan.FromSeconds(10.08) }
        ];
        MacroStep block = useLoop ? new LoopStep(1, body) : new IfThenElseStep(new PredicateCondition(() => true), body);
        var elapsed = Stopwatch.StartNew();
        await run.RunAsync([block, run.Mark("after")]);
        run.Input.Keys.Should().Equal(65, 66);
        (run.Input.Times[1] - run.Input.Times[0]).TotalMilliseconds.Should().BeGreaterThanOrEqualTo(65);
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        run.Actions.Should().Equal("after");
    }

    [Fact]
    public async Task BodyFailureRetry_RestartsBlockAndDoesNotRunAbandonedTail()
    {
        using var run = new Harness();
        int checks = 0;
        var releasesAtStart = new List<int>();
        var wait = new WaitUntilStep(new PredicateCondition(() => ++checks > 1)) { Timeout = TimeSpan.Zero };
        var start = new CallbackStep(() => { releasesAtStart.Add(run.Input.Releases); run.Actions.Add("start"); });
        var block = new IfThenElseStep(new PredicateCondition(() => true), [start, wait, run.Mark("tail")])
        { Routing = new() { OnFailure = FlowRoute.Retry, RetryCount = 1, RetryDelayMs = 10 } };
        await run.RunAsync([block, run.Mark("after")]);
        run.Actions.Should().Equal("start", "start", "tail", "after");
        releasesAtStart.Should().Equal(new[] { 0, 1 }, "the failed attempt releases its owned input before retrying");
    }

    [Fact]
    public async Task BodyFailureContinue_ReleasesOwnedInputBeforeFollowingAction()
    {
        using var run = new Harness();
        int releasesBeforeFollowingAction = 0;
        var wait = new WaitUntilStep(new PredicateCondition(() => false)) { Timeout = TimeSpan.Zero };
        var block = new IfThenElseStep(new PredicateCondition(() => true), [wait, run.Mark("abandoned")])
        { Routing = new() { OnFailure = FlowRoute.Continue } };
        await run.RunAsync([block, new CallbackStep(() => releasesBeforeFollowingAction = run.Input.Releases)]);
        releasesBeforeFollowingAction.Should().Be(1);
        run.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task StopDuringRetryDelay_RemainsEffectiveAndPreventsContinuation()
    {
        using var run = new Harness();
        var checkedOnce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var condition = new PredicateCondition(() => { checkedOnce.TrySetResult(); return false; });
        var block = new IfThenElseStep(condition, [])
        { Routing = new() { OnNotSatisfied = FlowRoute.Retry, RetryDelayMs = 10_000 } };
        Task playback = run.RunAsync([block, run.If(true, "wrong")]);
        await checkedOnce.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await run.Controller.StopAsync();
        await playback.WaitAsync(TimeSpan.FromSeconds(1));
        run.Actions.Should().BeEmpty();
        run.Executor.State.Should().Be(ExecutionState.Stopped);
    }

    private sealed class PredicateCondition(Func<bool> predicate) : ICondition
    {
        public int Checks { get; private set; }
        public string Description => "test predicate";
        public Task<ConditionEvaluation> EvaluateDetailedAsync(MacroContext context, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Checks++; return Task.FromResult(predicate() ? ConditionEvaluation.Satisfied : ConditionEvaluation.Unsatisfied); }
    }

    private sealed class ResultCondition(Func<int, ConditionEvaluation> evaluate) : ICondition
    {
        private int _checks;
        public string Description => "test result";
        public Task<ConditionEvaluation> EvaluateDetailedAsync(MacroContext context, CancellationToken ct = default)
            => Task.FromResult(evaluate(++_checks));
    }

    private sealed class CallbackStep(Action action) : MacroStep
    {
        public override Task ExecuteAsync(MacroContext context, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
    }

    private sealed class Harness : IDisposable
    {
        public List<string> Actions { get; } = [];
        public List<ExecutionTraceEvent> Traces { get; } = [];
        public FakeCapture Capture { get; } = new();
        public FakeInput Input { get; } = new();
        public MacroController Controller { get; } = new();
        public MacroContext Context { get; }
        public MacroExecutor Executor { get; } = new();
        public Harness() => Context = new(Capture, Controller)
        { InputPlayer = Input, MaxRuntime = TimeSpan.FromSeconds(4), Trace = new DelegateExecutionTraceSink(Traces.Add) };
        public MacroStep Mark(string text) => new CallbackStep(() => Actions.Add(text)) { Label = text };
        public IfThenElseStep If(bool matched, string action) => new(new PredicateCondition(() => matched), [Mark(action)]);
        public async Task RunAsync(IReadOnlyList<MacroStep> steps, int repeatCount = 1, int maxIterations = 100_000)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await Executor.ExecuteAsync(new MacroModel
            { Steps = steps, Settings = new() { RepeatCount = repeatCount, MaxIterations = maxIterations } }, Context, timeout.Token);
        }
        public void Dispose() { Controller.Dispose(); Capture.Dispose(); Input.Dispose(); }
    }

    private sealed class FakeCapture : IScreenCaptureService
    {
        public int FailuresRemaining { get; set; }
        public int Calls { get; private set; }
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;
        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            if (FailuresRemaining-- > 0) throw new IOException("Transient capture failure.");
            return Task.FromResult(TestFrameFactory.CreateSolidColor(region.Width, region.Height, 220, 40, 40,
                originX: region.X, originY: region.Y));
        }
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default) => CaptureRegionAsync(new(0, 0, 8, 8), ct);
        public void Dispose() { }
    }

    private sealed class FakeInput : IInputPlayer
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        public List<int> Keys { get; } = [];
        public List<TimeSpan> Times { get; } = [];
        public int Releases { get; private set; }
        public bool IsPlaying => false;
        public Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            foreach (InputEvent item in events)
                if (item.Type == InputEventType.KeyDown) { Keys.Add(item.Data); Times.Add(_clock.Elapsed); }
            return Task.CompletedTask;
        }
        public void ReleaseAllHeldKeys() => Releases++;
        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false) { }
        public void SendRelativeMove(int dx, int dy) { }
        public void Dispose() { }
    }
}
