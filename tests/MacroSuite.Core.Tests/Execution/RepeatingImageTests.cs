using System.Diagnostics;
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

public sealed class RepeatingImageTests
{
    [Fact]
    public async Task ReenteringMonitor_DebitsWholeBlocksFromTheRunBudget()
    {
        using var run = new Harness((_, _) => Hit(10));
        var log = new List<string>();
        var setup = Record(log, "setup");
        var jump = new IfThenElseStep(new ConstantCondition(false), [])
        { Routing = new() { OnNotSatisfied = FlowRoute.Jump, TargetStepId = setup.StepId } };
        await FluentActions.Awaiting(() => run.RunAsync([setup, Trigger("A"), Record(log, "body"), jump], maxIterations: 8))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*maximum allowable iterations*");
        log.Should().Equal("setup", "body", "setup", "body");
    }

    [Fact]
    public async Task AppearanceDeclaration_DoesNotSplitRepeatBlockOrConsumeItsBudget()
    {
        using var run = new Harness((_, _) => Hit(10));
        var log = new List<string>();
        var after = Record(log, "after");
        var declaration = new WaitUntilStep(new ImageCondition("watch"))
        { AppearanceRedirect = new() { TargetStepId = after.StepId } };
        IReadOnlyList<MacroStep> steps = [Trigger("A"), Record(log, "before"), declaration, after, Stop()];
        int counted = 0;
        RepeatingImageDispatcher.Validate(steps);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await RepeatingImageDispatcher.ExecuteAsync(steps, 0, run.Context, timeout.Token,
            maxExecutions: 4, executionCount: count => counted += count);
        log.Should().Equal("before", "after");
        counted.Should().Be(4);
        run.Matcher.Checks.Keys.Should().Equal("A");
    }

    [Fact]
    public async Task AbsentFirstImage_DoesNotBlockLaterBlock_AndSetupRunsOnce()
    {
        using var run = new Harness((name, _) => name == "A" ? MatchResult.NotFound : Hit(20));
        var log = new List<string>();
        await run.RunAsync([
            Record(log, "setup"), Trigger("A"), Record(log, "never"),
            Trigger("B"), Record(log, "B1"), Record(log, "B2"), Stop()
        ]);
        log.Should().Equal("setup", "B1", "B2");
        run.Capture.Frames.Should().HaveCount(1, "one scan shares its capture across both templates");
        run.Matcher.Frames[0].Should().BeSameAs(run.Matcher.Frames[1]);
        run.Capture.Frames.Should().OnlyContain(frame => frame.IsDisposed);
        run.Input.Releases.Should().Be(1);
    }

    [Fact]
    public async Task ReadySlowPollingPeers_GetFreshTurnsBeforeFastTriggerRepeats()
    {
        using var run = new Harness((_, _) => Hit(10), maxRuntime: TimeSpan.FromSeconds(2));
        var log = new List<string>();
        WaitUntilStep slow(string name) => new(new ImageCondition(name))
        {
            RepeatWhileVisible = true, RepeatDelayMs = 50,
            PollInterval = TimeSpan.FromSeconds(10)
        };
        await run.RunAsync([
            Trigger("M"), Record(log, "M"),
            slow("OK"), Record(log, "OK"),
            slow("X"), Record(log, "X"), Stop()
        ]);
        log.Should().Equal("M", "OK", "X");
        run.Matcher.Checks["OK"].Should().BeGreaterThan(1, "the ready peer is revalidated after M runs");
        run.Matcher.Checks["X"].Should().BeGreaterThan(1, "a stale scan cannot authorize the later action");
    }

    [Theory]
    [InlineData("OK", 41)]
    [InlineData("X", 71)]
    public async Task ThreeImageGroups_OnlyVisibleLaterGroupRepeats_WhenMIsAbsent(string visible, int expectedX)
    {
        using var run = new Harness((name, _) => name == visible ? Hit(name == "OK" ? 40 : 70) : MatchResult.NotFound);
        var log = new List<string>();
        int runs = 0;
        CallbackStep completed(string name) => new(async (context, _) =>
        {
            log.Add(name);
            if (++runs == 2) await context.Control.StopAsync();
        });
        WaitUntilStep trigger(string name) => new(new ImageCondition(name))
        {
            RepeatWhileVisible = true, RepeatDelayMs = 500,
            PollInterval = TimeSpan.FromMilliseconds(90)
        };
        ClickStep detectedClick() => new(new Point(-999, -999), delayAfterMs: 0)
        {
            XVariable = MacroVariableNames.LastDetectedXAlias,
            YVariable = MacroVariableNames.LastDetectedYAlias
        };
        await run.RunAsync([
            trigger("M"), new KeyPressStep(77, 0), new DelayStep(TimeSpan.FromMilliseconds(250)), completed("M"),
            trigger("OK"), detectedClick(), new DelayStep(TimeSpan.FromMilliseconds(250)), completed("OK"),
            trigger("X"), detectedClick(), completed("X")
        ]);
        log.Should().Equal(visible, visible);
        run.Input.Moves.Should().NotContain(input => input.Type == InputEventType.KeyDown || input.Type == InputEventType.KeyUp);
        run.Input.Moves.Where(input => input.Type == InputEventType.MouseDown)
            .Select(input => (input.X, input.Y)).Should().Equal((expectedX, 11), (expectedX, 11));
        run.Matcher.Checks.Keys.Should().BeEquivalentTo("M", "OK", "X");
    }

    [Fact]
    public async Task ThreeVisibleGroups_RestoreEachDetectionBeforeItsVariableClick()
    {
        using var run = new Harness((name, _) => Hit(name == "M" ? 10 : name == "OK" ? 40 : 70));
        var log = new List<string>();
        ClickStep detectedClick() => new(new Point(-999, -999), delayAfterMs: 0)
        {
            XVariable = MacroVariableNames.LastDetectedXAlias,
            YVariable = MacroVariableNames.LastDetectedYAlias
        };
        await run.RunAsync([
            Trigger("M"), new KeyPressStep(77, 0), new DelayStep(TimeSpan.FromMilliseconds(250)), Record(log, "M"),
            Trigger("OK"), detectedClick(), new DelayStep(TimeSpan.FromMilliseconds(250)), Record(log, "OK"),
            Trigger("X"), detectedClick(), Record(log, "X"), Stop()
        ]);
        log.Should().Equal("M", "OK", "X");
        run.Input.Moves.Where(input => input.Type == InputEventType.MouseDown)
            .Select(input => (input.X, input.Y)).Should().Equal((41, 11), (71, 11));
    }

    [Fact]
    public async Task DefaultRepeatImageScope_SharesFullScreenDespiteSmallForegroundOverlay()
    {
        using var run = new Harness((name, _) => name == "X" ? Hit(70) : MatchResult.NotFound,
            windowCoordinates: new SmallForegroundOverlay());
        run.Matcher.OnMatch = () =>
        {
            run.Matcher.Frames[^1].Width.Should().Be(100, "an unrelated 40px foreground overlay is not the watch scope");
            run.Matcher.Frames[^1].Height.Should().Be(100);
        };
        await run.RunAsync([Trigger("M"), Trigger("OK"), Trigger("X"), Stop()]);
        run.Capture.Regions.Should().Equal(new Rectangle(0, 0, 100, 100));
        run.Matcher.Checks.Keys.Should().BeEquivalentTo("M", "OK", "X");
        run.Matcher.Frames.Should().OnlyContain(frame => ReferenceEquals(frame, run.Capture.Frames[0]));
        run.Capture.Frames.Should().OnlyContain(frame => frame.IsDisposed);
    }

    [Fact]
    public async Task VisibleImages_RepeatWholeBlocksFairly_WithSeparateMatchCoordinates()
    {
        using var run = new Harness((name, _) => Hit(name == "A" ? 10 : 60));
        var log = new List<string>();
        var observed = new List<int>();
        int activeBodies = 0;
        int maximumActiveBodies = 0;
        int rounds = 0;
        CallbackStep body(string name) => new(async (context, ct) =>
        {
            activeBodies++;
            maximumActiveBodies = Math.Max(maximumActiveBodies, activeBodies);
            // The scan frame must have been released before any body or input runs.
            run.Capture.Frames.Should().OnlyContain(frame => frame.IsDisposed);
            observed.Add(context.Variables.Get<int>(ImageCondition.LastMatchXVariable));
            await Task.Delay(5, ct);
            log.Add(name);
            activeBodies--;
        });
        await run.RunAsync([
            Trigger("A"), body("A1"), Record(log, "A2"),
            Trigger("B"), body("B1"), Record(log, "B2"),
            new CallbackStep(context => { if (++rounds == 2) context.Control.Panic(); })
        ]);
        log.Should().Equal("A1", "A2", "B1", "B2", "A1", "A2", "B1", "B2");
        observed.Should().Equal(11, 61, 11, 61);
        maximumActiveBodies.Should().Be(1);
        run.Input.Moves.Select(move => move.X).Should().Equal(11, 61, 11, 61);
        run.Executor.State.Should().Be(ExecutionState.Aborted);
    }

    [Fact]
    public async Task EarlierBlockHidesLaterImage_LaterBlockMustPassAFreshScan()
    {
        bool secondVisible = true;
        using var run = new Harness((name, _) => name == "B" && !secondVisible ? MatchResult.NotFound : Hit(10));
        var log = new List<string>();
        int firstCount = 0;
        await run.RunAsync([
            Trigger("A"), new CallbackStep(context =>
            {
                log.Add("A");
                secondVisible = false;
                if (++firstCount == 2) context.Control.Panic();
            }),
            Trigger("B"), Record(log, "stale B action")
        ]);
        log.Should().Equal("A", "A");
        run.Matcher.Checks["B"].Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task MissResetsConfirmations_AndReappearingImageCanTriggerAgain()
    {
        using var run = new Harness((_, check) => check is >= 3 and <= 6 ? MatchResult.NotFound : Hit(10));
        var executionChecks = new List<int>();
        var wait = new WaitUntilStep(new ImageCondition("A"))
        {
            RepeatWhileVisible = true, RepeatDelayMs = 50,
            PollInterval = TimeSpan.FromMilliseconds(10), RequiredConfirmations = 2
        };
        await run.RunAsync([wait, new CallbackStep(context =>
        {
            executionChecks.Add(run.Matcher.Checks["A"]);
            if (executionChecks.Count == 2) context.Control.Panic();
        })]);
        executionChecks[0].Should().Be(2);
        executionChecks[1].Should().BeGreaterThanOrEqualTo(8);
    }

    [Theory]
    [InlineData(-10, 50)]
    [InlineData(1, 50)]
    [InlineData(100, 100)]
    public async Task RepeatDelay_IsEnforcedAfterBody_AndClamped(int requestedDelay, int expectedDelay)
    {
        using var run = new Harness((_, _) => Hit(10));
        long firstBodyFinished = 0;
        long secondBodyStarted = 0;
        int count = 0;
        await run.RunAsync([Trigger("A", requestedDelay), new CallbackStep(async (context, ct) =>
        {
            if (++count == 1)
            {
                await Task.Delay(20, ct);
                firstBodyFinished = Stopwatch.GetTimestamp();
            }
            else
            {
                secondBodyStarted = Stopwatch.GetTimestamp();
                await context.Control.StopAsync();
            }
        })]);
        // TickCount64 has a coarse Windows clock quantum that can undercount a
        // correct 100 ms delay as 93 ms. Measure elapsed time at stopwatch precision.
        Stopwatch.GetElapsedTime(firstBodyFinished, secondBodyStarted).TotalMilliseconds
            .Should().BeGreaterThanOrEqualTo(expectedDelay - 2);
        count.Should().Be(2);
    }

    [Fact]
    public async Task StabilityWindow_PreventsEarlyExecution()
    {
        using var run = new Harness((_, _) => Hit(10));
        var watch = Stopwatch.StartNew();
        long bodyStarted = 0;
        var trigger = new WaitUntilStep(new ImageCondition("A"))
        {
            RepeatWhileVisible = true, PollInterval = TimeSpan.FromMilliseconds(10), StabilityDurationMs = 60
        };
        await run.RunAsync([trigger, new CallbackStep(context =>
        {
            bodyStarted = watch.ElapsedMilliseconds;
            context.Control.Panic();
        })]);
        bodyStarted.Should().BeGreaterThanOrEqualTo(58);
        run.Matcher.Checks["A"].Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task DisabledTrigger_DelimitsAndSuppressesItsEntireBlock()
    {
        using var run = new Harness((_, _) => Hit(10));
        var log = new List<string>();
        await run.RunAsync([
            Trigger("disabled", enabled: false), Record(log, "suppressed"),
            Trigger("A"), Record(log, "A"),
            new WaitUntilStep(new ImageCondition("disabled-normal")) { IsEnabled = false }, Record(log, "also suppressed"),
            Trigger("B"), Record(log, "B"), Stop()
        ]);
        log.Should().Equal("A", "B");
        run.Matcher.Checks.Keys.Should().BeEquivalentTo("A", "B");
    }

    [Fact]
    public async Task MixedOneShotAfterRepeating_ReportsConfigurationErrorBeforeSetupRuns()
    {
        using var run = new Harness((_, _) => Hit(10));
        var log = new List<string>();
        var act = () => run.RunAsync([
            Record(log, "setup"), Trigger("A"), Record(log, "A"),
            new WaitUntilStep(new ImageCondition("B")), Record(log, "B")
        ]);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Every enabled Wait Image*");
        log.Should().BeEmpty();
        run.Capture.Frames.Should().BeEmpty();
    }

    [Fact]
    public async Task MatcherFailure_FaultsWithoutExecutingAnyScannedBlock()
    {
        using var run = new Harness((name, _) => name == "B" ? MatchResult.Failed : Hit(10));
        var log = new List<string>();
        var act = () => run.RunAsync([Trigger("A"), Record(log, "A"), Trigger("B"), Record(log, "B")]);
        await act.Should().ThrowAsync<ConditionEvaluationException>();
        log.Should().BeEmpty();
        run.Executor.State.Should().Be(ExecutionState.Faulted);
        run.Capture.Frames.Should().OnlyContain(frame => frame.IsDisposed);
        run.Input.Releases.Should().Be(1);
    }

    [Theory]
    [InlineData("M.png")]
    [InlineData("OK.png")]
    [InlineData("X.png")]
    public async Task UndersizedSearchRegion_ReportsTheAffectedImageAndCorrection_WithoutAnyInput(string failingTemplate)
    {
        const string reason = "The checked search area is 934 x 269 pixels, but the template is 947 x 257 pixels. "
            + "Choose a larger search area or capture a smaller template.";
        using var run = new Harness((name, _) => name == failingTemplate
            ? new MatchResult(false, 0, Rectangle.Empty, MatchOutcome.Failed, "ImageSearchRegionTooSmall") { ErrorMessage = reason }
            : Hit(10));
        var log = new List<string>();

        var failure = await FluentActions.Awaiting(() => run.RunAsync([
            Trigger("M.png"), Record(log, "M"),
            Trigger("OK.png"), Record(log, "OK"),
            Trigger("X.png"), Record(log, "X")
        ])).Should().ThrowAsync<ConditionEvaluationException>();

        failure.Which.ErrorCode.Should().Be("ImageSearchRegionTooSmall");
        failure.Which.Message.Should().Contain("Repeating Wait Image").And.Contain(failingTemplate)
            .And.Contain("934 x 269 pixels").And.Contain("947 x 257 pixels")
            .And.Contain("Choose a larger search area or capture a smaller template");
        BlockRouting.IsRecoverable(ConditionEvaluation.Fail(failure.Which.ErrorCode!)).Should().BeFalse();
        log.Should().BeEmpty();
        run.Input.Moves.Should().BeEmpty("no cursor move, key or click may execute from a scan with invalid configuration");
        run.Executor.State.Should().Be(ExecutionState.Faulted);
        run.Capture.Frames.Should().OnlyContain(frame => frame.IsDisposed);
        run.Input.Releases.Should().Be(1);
    }

    [Fact]
    public async Task Pause_PreventsCapturesAndBodies_ResumeChecksFreshImage()
    {
        using var run = new Harness((_, _) => Hit(10));
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int executions = 0;
        Task running = run.RunAsync([Trigger("A"), new CallbackStep(async (context, ct) =>
        {
            if (++executions == 1)
            {
                await context.Control.PauseAsync(ct);
                paused.TrySetResult();
            }
            else await context.Control.StopAsync();
        })]);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(2));
        int captures = run.Capture.Frames.Count;
        await Task.Delay(100);
        executions.Should().Be(1);
        run.Capture.Frames.Should().HaveCount(captures);
        await run.Controller.ResumeAsync();
        await running;
        executions.Should().Be(2);
        run.Capture.Frames.Count.Should().BeGreaterThan(captures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrPanic_EndsAnIdleMonitorPromptly(bool panic)
    {
        using var run = new Harness((_, _) => MatchResult.NotFound);
        var scanned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Matcher.OnMatch = () => scanned.TrySetResult();
        Task running = run.RunAsync([new WaitUntilStep(new ImageCondition("A"))
        {
            RepeatWhileVisible = true, PollInterval = TimeSpan.FromSeconds(10)
        }]);
        await scanned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (panic) run.Controller.Panic(); else await run.Controller.StopAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(1));
        run.Executor.State.Should().Be(panic ? ExecutionState.Aborted : ExecutionState.Stopped);
        run.Input.Releases.Should().Be(1);
    }

    [Fact]
    public async Task RuntimeCap_StopsMonitorEvenWhenAllImagesAreAbsent()
    {
        using var run = new Harness((_, _) => MatchResult.NotFound, maxRuntime: TimeSpan.FromMilliseconds(80));
        await run.RunAsync([Trigger("A")]);
        run.Executor.State.Should().Be(ExecutionState.Stopped);
    }

    [Fact]
    public async Task RuntimeCap_StopsPausedMonitorWithoutRequiringResume()
    {
        using var run = new Harness((_, _) => Hit(10), maxRuntime: TimeSpan.FromMilliseconds(100));
        Task running = run.RunAsync([
            Trigger("A"), new CallbackStep((context, ct) => context.Control.PauseAsync(ct))
        ]);
        await running.WaitAsync(TimeSpan.FromSeconds(1));
        run.Controller.IsPaused.Should().BeTrue();
        run.Executor.State.Should().Be(ExecutionState.Stopped);
        run.Capture.Frames.Should().HaveCount(1);
        run.Input.Releases.Should().Be(1);
    }

    [Fact]
    public async Task RepeatUntilStopped_ImageGroupsContinuePastRootActionBudgetUntilManualStop()
    {
        using var run = new Harness((_, _) => Hit(10), maxIterations: 3, enforceRuntimeCap: false);
        var log = new List<string>();
        CallbackStep body(string name) => new(async (context, _) =>
        {
            log.Add(name);
            if (log.Count == 6)
                await context.Control.StopAsync();
        });
        await run.RunAsync([
            Trigger("A", delay: 50), body("A"), Trigger("B", delay: 50), body("B")
        ], maxIterations: 3, repeatUntilStopped: true);

        log.Should().Equal("A", "B", "A", "B", "A", "B");
        run.Executor.State.Should().Be(ExecutionState.Stopped);
        run.Capture.Frames.Should().OnlyContain(frame => frame.IsDisposed);
        run.Input.Releases.Should().Be(1);
    }

    [Fact]
    public async Task BoundedPlayback_ImageGroupsStillRespectRootActionBudget()
    {
        using var run = new Harness((_, _) => Hit(10), maxIterations: 3);
        var log = new List<string>();
        var act = () => run.RunAsync([
            Trigger("A", delay: 50), Record(log, "A"), Trigger("B", delay: 50), Record(log, "B")
        ], maxIterations: 3);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*maximum allowable iterations (3)*");
        log.Should().Equal("A");
        run.Executor.State.Should().Be(ExecutionState.Faulted);
        run.Capture.Frames.Should().OnlyContain(frame => frame.IsDisposed);
    }

    [Fact]
    public async Task RepeatUntilStopped_NestedImageMonitorKeepsItsLocalActionBudget()
    {
        using var run = new Harness((_, _) => Hit(10), maxIterations: 3, enforceRuntimeCap: false);
        var log = new List<string>();
        var act = () => run.RunAsync([
            new LoopStep(1, [Trigger("A", delay: 50), Record(log, "A")])
        ], maxIterations: 3, repeatUntilStopped: true);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*maximum allowable iterations (3)*");
        log.Should().Equal("A");
        run.Executor.State.Should().Be(ExecutionState.Faulted);
    }

    [Fact]
    public async Task TriggerIterations_RespectMacroSettingsCap()
    {
        using var run = new Harness((_, _) => Hit(10));
        int count = 0;
        var act = () => run.RunAsync([Trigger("A"), new CallbackStep(_ => count++)], maxIterations: 2);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*maximum allowable iterations (2)*");
        count.Should().Be(1, "both the trigger and its body action consume the configured action budget");
    }

    [Fact]
    public async Task TriggerBudget_IncludesActionsAlreadyExecutedInSetup()
    {
        using var run = new Harness((_, _) => Hit(10));
        var log = new List<string>();
        var act = () => run.RunAsync([Record(log, "setup"), Trigger("A"), Record(log, "never")], maxIterations: 2);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*maximum allowable iterations*");
        log.Should().Equal("setup");
    }

    [Fact]
    public async Task IfBranch_UsesItsOwnTriggerList_AndPreservesLoopBreak()
    {
        using var run = new Harness((_, _) => Hit(10));
        var log = new List<string>();
        var branch = new IfThenElseStep(new ConstantCondition(true), [
            Trigger("A"), Record(log, "then"), new BreakStep(), Record(log, "never")
        ], [Record(log, "else")]);
        await run.RunAsync([new LoopStep(5, [branch, Record(log, "after branch")]), Record(log, "after loop")]);
        log.Should().Equal("then", "after loop");
        run.Executor.State.Should().Be(ExecutionState.Completed);
    }

    [Fact]
    public async Task LoopTriggerBlock_ContinueStartsNextIterationWithoutLeakingSignal()
    {
        using var run = new Harness((_, _) => Hit(10));
        var log = new List<string>();
        var loop = new LoopStep(2, [Trigger("A"), Record(log, "body"), new ContinueStep(), Record(log, "never")]);
        await run.RunAsync([loop, Record(log, "after loop")]);
        log.Should().Equal("body", "body", "after loop");
        run.Context.PendingControlFlow.Should().Be(ControlFlowSignal.None);
    }

    [Fact]
    public async Task LoopDurationCap_EndsIdleMonitoringAndContinuesAfterLoop()
    {
        using var run = new Harness((_, _) => MatchResult.NotFound);
        var log = new List<string>();
        await run.RunAsync([
            new LoopStep(10, [Trigger("A")], new ConstantCondition(true)) { MaxDurationMs = 60 },
            Record(log, "after")
        ]);
        log.Should().Equal("after");
        run.Executor.State.Should().Be(ExecutionState.Completed);
    }

    [Fact]
    public async Task LoopDurationCap_InterruptsLongRepeatingBodyWithoutStoppingFollowingActions()
    {
        using var run = new Harness((_, _) => Hit(10));
        var log = new List<string>();
        var watch = Stopwatch.StartNew();
        await run.RunAsync([
            new LoopStep(10, [Trigger("A"), new DelayStep(TimeSpan.FromSeconds(10)), Record(log, "never")]) { MaxDurationMs = 70 },
            Record(log, "after")
        ]);
        log.Should().Equal("after");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        run.Executor.State.Should().Be(ExecutionState.Completed);
    }

    [Fact]
    public async Task LoopDurationCap_ReachesMonitorNestedInsideIf()
    {
        using var run = new Harness((_, _) => MatchResult.NotFound);
        var log = new List<string>();
        var watch = Stopwatch.StartNew();
        await run.RunAsync([
            new LoopStep(10, [new IfThenElseStep(new ConstantCondition(true), [Trigger("missing")])]) { MaxDurationMs = 70 },
            Record(log, "after loop")
        ]);
        log.Should().Equal("after loop");
        run.Executor.State.Should().Be(ExecutionState.Completed);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RecordedBlock_UsesLocalTimelineWithoutWaitingForRecordingPrefix()
    {
        using var run = new Harness((_, _) => Hit(10));
        var watch = Stopwatch.StartNew();
        await run.RunAsync([
            Trigger("A"),
            new MouseMoveStep(new Point(1, 1), relativeTimestamp: TimeSpan.FromSeconds(10)),
            new MouseMoveStep(new Point(2, 2), relativeTimestamp: TimeSpan.FromSeconds(10.08)),
            Stop()
        ]);
        run.Input.Moves.Should().HaveCount(3, "the trigger cursor move plus both body moves execute");
        watch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(70));
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task WhileLoop_WithImmediateBody_RespectsConditionCheckInterval()
    {
        using var run = new Harness((_, _) => Hit(10));
        var checks = new List<long>();
        int bodies = 0;
        var condition = new CallbackCondition(() =>
        {
            checks.Add(Environment.TickCount64);
            return bodies < 3;
        });
        await run.RunAsync([new LoopStep(10, [new CallbackStep(_ => bodies++)], condition) { CheckIntervalMs = 50 }]);
        bodies.Should().Be(3);
        checks.Should().HaveCount(4);
        for (int i = 1; i < checks.Count; i++)
            (checks[i] - checks[i - 1]).Should().BeGreaterThanOrEqualTo(48);
    }

    [Fact]
    public async Task WhileLoop_StopInterruptsLongConditionInterval()
    {
        using var run = new Harness((_, _) => Hit(10));
        var firstBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task running = run.RunAsync([new LoopStep(10,
            [new CallbackStep(_ => firstBody.TrySetResult())], new ConstantCondition(true)) { CheckIntervalMs = 10_000 }]);
        await firstBody.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await run.Controller.StopAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(1));
        run.Executor.State.Should().Be(ExecutionState.Stopped);
    }

    [Fact]
    public async Task NestedRepeatingMonitor_IsRejectedRatherThanStarvingSiblingImages()
    {
        using var run = new Harness((_, _) => Hit(10));
        var act = () => run.RunAsync([
            Trigger("A"), new IfThenElseStep(new ConstantCondition(true), [Trigger("nested")]), Trigger("B")
        ]);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cannot contain another repeating monitor*");
        run.Capture.Frames.Should().BeEmpty();
    }

    [Fact]
    public async Task OneShotImageWaits_KeepSequentialBehaviorAndComplete()
    {
        using var run = new Harness((_, _) => Hit(10));
        var log = new List<string>();
        await run.RunAsync([
            new WaitUntilStep(new ImageCondition("A")), Record(log, "A"),
            new WaitUntilStep(new ImageCondition("B")), Record(log, "B")
        ]);
        log.Should().Equal("A", "B");
        run.Executor.State.Should().Be(ExecutionState.Completed);
    }

    [Fact]
    public void Serialization_RetainsRepeatSettings_AndLegacyWaitDefaultsToOneShot()
    {
        var serializer = new MacroJsonSerializer();
        var restored = serializer.Deserialize(serializer.Serialize(new MacroModel
        {
            Steps = [Trigger("A", 750), new DelayStep(TimeSpan.FromMilliseconds(25))]
        }));
        var wait = restored.Steps[0].Should().BeOfType<WaitUntilStep>().Subject;
        wait.RepeatWhileVisible.Should().BeTrue();
        wait.RepeatDelayMs.Should().Be(750);
        var legacy = serializer.Deserialize("""
            {"schemaVersion":2,"steps":[{"$type":"waituntil","condition":{"$type":"image","templatePath":"old.png"}}]}
            """);
        legacy.Steps[0].Should().BeOfType<WaitUntilStep>().Which.RepeatWhileVisible.Should().BeFalse();
    }

    [Fact]
    public async Task SearchRegions_AreCombinedAndTranslatedRelativeToSharedFrame()
    {
        using var run = new Harness((_, _) => Hit(10));
        var first = new WaitUntilStep(new ImageCondition("A", searchRegion: new Rectangle(-100, 20, 50, 50))) { RepeatWhileVisible = true };
        var second = new WaitUntilStep(new ImageCondition("B", searchRegion: new Rectangle(0, 30, 50, 50))) { RepeatWhileVisible = true };
        await run.RunAsync([first, second, Stop()]);
        run.Capture.Regions.Count.Should().BeGreaterThanOrEqualTo(2);
        run.Capture.Regions[0].Should().Be(new Rectangle(-100, 20, 150, 60));
        run.Matcher.Regions.Take(2).Should().Equal(new Rectangle(0, 0, 50, 50), new Rectangle(100, 10, 50, 50));
    }

    private static WaitUntilStep Trigger(string name, int delay = 50, bool enabled = true)
        => new(new ImageCondition(name))
        {
            RepeatWhileVisible = true, RepeatDelayMs = delay, IsEnabled = enabled,
            PollInterval = TimeSpan.FromMilliseconds(10)
        };
    private static MatchResult Hit(int x) => MatchResult.Matched(0.95, new Rectangle(x, 10, 2, 2));
    private static CallbackStep Record(List<string> log, string text) => new(_ => log.Add(text));
    private static CallbackStep Stop() => new((context, _) => context.Control.StopAsync());

    private sealed class CallbackStep : MacroStep
    {
        private readonly Func<MacroContext, CancellationToken, Task> _callback;
        public CallbackStep(Action<MacroContext> callback)
            : this((context, _) => { callback(context); return Task.CompletedTask; }) { }
        public CallbackStep(Func<MacroContext, CancellationToken, Task> callback) => _callback = callback;
        public override Task ExecuteAsync(MacroContext context, CancellationToken ct = default) => _callback(context, ct);
    }

    private sealed class ConstantCondition(bool value) : ICondition
    {
        public string Description => "constant";
        public Task<ConditionEvaluation> EvaluateDetailedAsync(MacroContext context, CancellationToken ct = default)
            => Task.FromResult(value ? ConditionEvaluation.Satisfied : ConditionEvaluation.Unsatisfied);
    }

    private sealed class CallbackCondition(Func<bool> predicate) : ICondition
    {
        public string Description => "callback";
        public Task<ConditionEvaluation> EvaluateDetailedAsync(MacroContext context, CancellationToken ct = default)
            => Task.FromResult(predicate() ? ConditionEvaluation.Satisfied : ConditionEvaluation.Unsatisfied);
    }

    private sealed class Harness : IDisposable
    {
        public FakeCapture Capture { get; } = new();
        public FakeMatcher Matcher { get; }
        public FakeInput Input { get; } = new();
        public MacroController Controller { get; } = new();
        public MacroContext Context { get; }
        public MacroExecutor Executor { get; } = new();

        public Harness(Func<string, int, MatchResult> match, TimeSpan? maxRuntime = null,
            IWindowCoordinateService? windowCoordinates = null, int maxIterations = 100_000,
            bool enforceRuntimeCap = true)
        {
            Matcher = new FakeMatcher(match);
            Context = new MacroContext(Capture, Controller)
            {
                ImageMatcher = Matcher, InputPlayer = Input, WindowCoordinates = windowCoordinates,
                MaxRuntime = maxRuntime ?? TimeSpan.FromSeconds(5),
                MaxIterations = maxIterations, EnforceRuntimeCap = enforceRuntimeCap
            };
        }

        public async Task RunAsync(IReadOnlyList<MacroStep> steps, int maxIterations = 100_000,
            bool repeatUntilStopped = false)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Executor.ExecuteAsync(new MacroModel
            {
                Steps = steps, Settings = new MacroSettings
                {
                    MaxIterations = maxIterations, RepeatUntilStopped = repeatUntilStopped
                }
            }, Context, timeout.Token);
        }

        public void Dispose() { Controller.Dispose(); Capture.Dispose(); Matcher.Dispose(); Input.Dispose(); }
    }

    private sealed class FakeCapture : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;
        public List<CaptureFrame> Frames { get; } = [];
        public List<Rectangle> Regions { get; } = [];
        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Regions.Add(region);
            var frame = TestFrameFactory.CreateSolidColor(region.Width, region.Height, 10, 20, 30, originX: region.X, originY: region.Y);
            Frames.Add(frame);
            return Task.FromResult(frame);
        }
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
            => CaptureRegionAsync(new Rectangle(0, 0, 100, 100), ct);
        public void Dispose() { foreach (var frame in Frames) frame.Dispose(); }
    }

    private sealed class SmallForegroundOverlay : IWindowCoordinateService
    {
        public IntPtr GetWindowAt(int screenX, int screenY) => (IntPtr)1;
        public IntPtr GetForegroundWindow() => (IntPtr)1;
        public (int clientX, int clientY) ScreenToClient(IntPtr hWnd, int screenX, int screenY) => (screenX - 10, screenY - 10);
        public (int screenX, int screenY) ClientToScreen(IntPtr hWnd, int clientX, int clientY) => (clientX + 10, clientY + 10);
        public (int width, int height) GetClientSize(IntPtr hWnd) => (40, 40);
        public bool IsWindowValid(IntPtr hWnd) => true;
        public IntPtr FindWindowByTitle(string? titleOrProcessName) => (IntPtr)1;
    }

    private sealed class FakeMatcher(Func<string, int, MatchResult> match) : IImageMatcher
    {
        public Dictionary<string, int> Checks { get; } = [];
        public List<CaptureFrame> Frames { get; } = [];
        public List<Rectangle?> Regions { get; } = [];
        public Action? OnMatch { get; set; }
        public Task<MatchResult> MatchAsync(CaptureFrame frame, string templatePath, double minConfidence = 0.8,
            Rectangle? searchRegion = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Frames.Add(frame);
            Regions.Add(searchRegion);
            Checks.TryGetValue(templatePath, out int count);
            Checks[templatePath] = ++count;
            MatchResult result = match(templatePath, count);
            OnMatch?.Invoke();
            return Task.FromResult(result);
        }
        public Task<MatchResult> MatchAsync(CaptureFrame frame, byte[] templateImageBytes, double minConfidence = 0.8,
            Rectangle? searchRegion = null, CancellationToken ct = default)
            => MatchAsync(frame, "bytes", minConfidence, searchRegion, ct);
        public void Dispose() { }
    }

    private sealed class FakeInput : IInputPlayer
    {
        public bool IsPlaying => false;
        public int Releases { get; private set; }
        public List<InputEvent> Moves { get; } = [];
        public Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Moves.AddRange(events);
            return Task.CompletedTask;
        }
        public void ReleaseAllHeldKeys() => Releases++;
        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false) { }
        public void SendRelativeMove(int dx, int dy) { }
        public void Dispose() { }
    }
}
