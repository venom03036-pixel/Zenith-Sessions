using System.Collections.Concurrent;
using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Tests.TestHelpers;
using Xunit;

namespace MacroSuite.Core.Tests.Execution;

public sealed class AppearanceRedirectTests
{
    [Fact]
    public async Task AppearanceDuringNestedFinalAction_JoinsOldPathBeforeLaterTarget()
    {
        using var run = new Harness();
        int visible = 0;
        run.Matcher.Match = _ => Volatile.Read(ref visible) != 0;
        var target = run.Record("target");
        var waiting = new Callback(async (_, ct) =>
        {
            run.Log.Enqueue("old-start");
            Volatile.Write(ref visible, 1);
            try { await Task.Delay(60_000, ct); run.Log.Enqueue("stale"); }
            finally { run.Log.Enqueue("old-joined"); }
        });
        await run.Run([Watch(target), new IfThenElseStep(new Constant(true), [waiting]), run.Record("skipped"), target]);
        run.Log.Should().Equal("old-start", "old-joined", "target");
        run.Context.CurrentLoopDepth.Should().Be(0);
        run.Executor.State.Should().Be(ExecutionState.Completed);
    }

    [Fact]
    public async Task EarlierTarget_RemainingVisible_DoesNotRestartRepeatedly()
    {
        using var run = new Harness();
        int visible = 0, visits = 0;
        run.Matcher.Match = _ => Volatile.Read(ref visible) == 1;
        var target = new Callback((_, _) => { run.Log.Enqueue($"target-{++visits}"); return Task.CompletedTask; });
        var final = new Callback(async (_, ct) =>
        {
            if (visits == 1) { Volatile.Write(ref visible, 1); await Task.Delay(60_000, ct); }
            else { await Task.Delay(100, ct); run.Log.Enqueue("complete"); }
        });
        await run.Run([target, Watch(target), final]);
        run.Log.Should().Equal("target-1", "target-2", "complete");
        visits.Should().Be(2);
    }

    [Fact]
    public async Task ConfirmedAbsence_RearmsAppearance()
    {
        using var run = new Harness();
        int state = 0, visits = 0, absentChecks = 0;
        var absence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Matcher.Match = _ =>
        {
            if (Volatile.Read(ref state) == 2 && Interlocked.Increment(ref absentChecks) >= 2) absence.TrySetResult();
            return Volatile.Read(ref state) == 1;
        };
        var target = new Callback((_, _) => { run.Log.Enqueue($"visit-{++visits}"); return Task.CompletedTask; });
        var body = new Callback(async (_, ct) =>
        {
            if (visits == 1) { Volatile.Write(ref state, 1); await Task.Delay(60_000, ct); }
            else if (visits == 2)
            {
                Volatile.Write(ref state, 2);
                await absence.Task.WaitAsync(ct);
                await Task.Delay(60, ct);
                Volatile.Write(ref state, 1);
                await Task.Delay(60_000, ct);
            }
            else run.Log.Enqueue("done");
        });
        await run.Run([target, Watch(target, falseConfirmations: 2), body]);
        run.Log.Should().Equal("visit-1", "visit-2", "visit-3", "done");
    }

    [Fact]
    public async Task StopDuringMatch_WinsOverDestination()
    {
        using var run = new Harness();
        run.Matcher.Match = _ => { run.Controller.Panic(); return true; };
        var target = run.Record("must-not-run");
        await run.Run([Watch(target), new DelayStep(TimeSpan.FromSeconds(60)), target]);
        run.Log.Should().BeEmpty();
        run.Executor.State.Should().Be(ExecutionState.Aborted);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StopRacingMatcherFailure_PreservesCancellationAndNeverRunsDestination(bool panic, bool duringAction)
    {
        using var run = new Harness();
        int active = duringAction ? 0 : 1;
        run.Matcher.Match = _ =>
        {
            if (Volatile.Read(ref active) == 0) return false;
            if (panic) run.Controller.Panic();
            else run.Controller.StopAsync().GetAwaiter().GetResult();
            throw new IOException("Simulated capture failure concurrent with Stop.");
        };
        var target = run.Record("must-not-run");
        var action = new Callback(async (_, ct) =>
        {
            run.Log.Enqueue("entered");
            Volatile.Write(ref active, 1);
            try { await Task.Delay(60_000, ct); run.Log.Enqueue("stale"); }
            finally { run.Log.Enqueue("joined"); }
        });
        await run.Run([Watch(target), action, target]);
        run.Executor.State.Should().Be(panic ? ExecutionState.Aborted : ExecutionState.Stopped);
        if (duringAction) run.Log.Should().Equal("entered", "joined");
        else run.Log.Should().BeEmpty();
    }

    [Fact]
    public async Task MatcherFailureWithoutStop_RemainsVisibleAsFailure()
    {
        using var run = new Harness();
        run.Matcher.Match = _ => throw new IOException("Simulated capture failure.");
        var target = run.Record("must-not-run");
        Func<Task> playback = () => run.Run([Watch(target), target]);
        await playback.Should().ThrowAsync<ConditionEvaluationException>();
        run.Executor.State.Should().Be(ExecutionState.Faulted);
        run.Log.Should().BeEmpty();
    }

    [Fact]
    public async Task PausedDetection_ResumesWithFreshConfirmation()
    {
        using var run = new Harness();
        int visible = 0;
        run.Matcher.Match = _ => Volatile.Read(ref visible) == 1;
        var target = run.Record("target");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = new Callback(async (context, ct) =>
        {
            await context.Control.PauseAsync(); entered.TrySetResult();
            await Task.Delay(60_000, ct);
        });
        Task playback = run.Run([Watch(target), blocker, target]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Volatile.Write(ref visible, 1);
        int checks = run.Matcher.Checks;
        await Task.Delay(60);
        run.Log.Should().BeEmpty();
        run.Matcher.Checks.Should().BeLessThanOrEqualTo(checks + 1);
        await run.Controller.ResumeAsync();
        await playback;
        run.Log.Should().Equal("target");
    }

    [Fact]
    public async Task SimultaneousImages_UseListOrder_AndShareFrames()
    {
        using var run = new Harness();
        int visible = 0;
        run.Matcher.Match = _ => Volatile.Read(ref visible) == 1;
        var first = new Callback(async (context, _) => { run.Log.Enqueue("first"); await context.Control.StopAsync(); });
        var second = run.Record("second");
        var blocker = new Callback(async (_, ct) => { Volatile.Write(ref visible, 1); await Task.Delay(60_000, ct); });
        await run.Run([Watch(first, "A"), Watch(second, "B"), blocker, first, second]);
        run.Log.Should().Equal("first");
        var frames = run.Matcher.Frames.ToArray();
        frames.Length.Should().BeGreaterThanOrEqualTo(2);
        frames[0].Should().BeSameAs(frames[1]);
        run.Capture.Frames.Should().OnlyContain(f => f.IsDisposed);
    }

    [Fact]
    public async Task DisabledWatcher_DoesNotEvaluateOrBlock()
    {
        using var run = new Harness();
        run.Matcher.Match = _ => true;
        var target = run.Record("target");
        var watch = new WaitUntilStep(new ImageCondition("A")) { IsEnabled = false, AppearanceRedirect = new() { TargetStepId = target.StepId } };
        await run.Run([watch, run.Record("ordinary"), target]);
        run.Log.Should().Equal("ordinary", "target");
        run.Matcher.Checks.Should().Be(0);
    }

    [Fact]
    public async Task AppearanceDuringRepeatBody_JoinsBeforeJump_AndSkipsRemainingBody()
    {
        using var run = new Harness();
        int visible = 0;
        run.Matcher.Match = name => name == "repeat" || Volatile.Read(ref visible) == 1;
        var target = new Callback(async (context, _) =>
        {
            run.Log.Enqueue("target");
            await context.Control.StopAsync();
        });
        var body = new Callback(async (_, ct) =>
        {
            run.Log.Enqueue("old-start");
            Volatile.Write(ref visible, 1);
            try { await Task.Delay(60_000, ct); }
            finally { run.Log.Enqueue("old-joined"); }
        });
        var repeat = new WaitUntilStep(new ImageCondition("repeat"))
        { RepeatWhileVisible = true, RepeatDelayMs = 50 };
        await run.Run([Watch(target), repeat, body, run.Record("stale-body"), target]);
        run.Log.Should().Equal("old-start", "old-joined", "target");
    }

    [Fact]
    public async Task MatcherFailureDuringAction_JoinsAbandonedPathBeforeReportingFailure()
    {
        using var run = new Harness();
        int active = 0;
        run.Matcher.Match = _ => Volatile.Read(ref active) == 0
            ? false : throw new IOException("Simulated matcher failure while executing.");
        var target = run.Record("must-not-run");
        var body = new Callback(async (_, ct) =>
        {
            run.Log.Enqueue("entered"); Volatile.Write(ref active, 1);
            try { await Task.Delay(60_000, ct); run.Log.Enqueue("stale"); }
            finally { run.Log.Enqueue("joined"); }
        });
        Func<Task> playback = () => run.Run([Watch(target), body, target]);
        await playback.Should().ThrowAsync<ConditionEvaluationException>();
        run.Log.Should().Equal("entered", "joined");
        run.Executor.State.Should().Be(ExecutionState.Faulted);
    }

    [Fact]
    public async Task AppearanceCoordinates_ArePublishedOnlyAfterOldPathJoins()
    {
        using var run = new Harness();
        int visible = 0;
        run.Context.Variables.Set(ImageCondition.LastMatchXVariable, -99);
        run.Matcher.Match = _ => Volatile.Read(ref visible) == 1;
        var target = new Callback((context, _) =>
        {
            context.Variables.Get<int>(ImageCondition.LastMatchXVariable).Should().Be(2);
            context.Variables.Get<int>(ImageCondition.LastMatchYVariable).Should().Be(2);
            run.Log.Enqueue("new-coordinates"); return Task.CompletedTask;
        });
        var body = new Callback(async (context, ct) =>
        {
            Volatile.Write(ref visible, 1);
            try { await Task.Delay(60_000, ct); }
            finally
            {
                context.Variables.Get<int>(ImageCondition.LastMatchXVariable).Should().Be(-99);
                run.Log.Enqueue("old-coordinates");
            }
        });
        await run.Run([Watch(target), body, target]);
        run.Log.Should().Equal("old-coordinates", "new-coordinates");
    }

    [Fact]
    public async Task AppearanceDeclarations_DoNotConsumeActionBudget()
    {
        using var run = new Harness();
        var target = run.Record("action");
        await run.Run([Watch(target, "A"), Watch(target, "B"), target], maxIterations: 1);
        run.Log.Should().Equal("action");
        run.Executor.State.Should().Be(ExecutionState.Completed);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("nested")]
    [InlineData("disabled")]
    [InlineData("duplicate")]
    public async Task InvalidDestination_FailsBeforeAnyAction(string kind)
    {
        using var run = new Harness();
        var target = new DelayStep(TimeSpan.Zero) { IsEnabled = kind != "disabled" };
        var steps = new List<MacroStep> { run.Record("must-not-run"), Watch(target) };
        if (kind == "nested") steps.Add(new IfThenElseStep(new Constant(true), [target]));
        else if (kind != "deleted") steps.Add(target);
        if (kind == "duplicate") steps.Add(target);
        Func<Task> play = () => run.Run(steps);
        await play.Should().ThrowAsync<InvalidOperationException>();
        run.Log.Should().BeEmpty();
    }

    [Fact]
    public void RoundtripAndReorder_PreserveStableTargetId()
    {
        var target = new DelayStep(TimeSpan.FromMilliseconds(5));
        var wait = Watch(target);
        var serializer = new MacroJsonSerializer();
        var saved = serializer.Deserialize(serializer.Serialize(new MacroModel { Steps = [target, wait] }));
        var restored = (WaitUntilStep)saved.Steps[1];
        restored.AppearanceRedirect!.TargetStepId.Should().Be(saved.Steps[0].StepId);
        restored.AppearanceRedirect.CooldownMs.Should().Be(50);
    }

    private static WaitUntilStep Watch(MacroStep target, string name = "A", int falseConfirmations = 1)
        => new(new ImageCondition(name))
        {
            AppearanceRedirect = new() { TargetStepId = target.StepId, CooldownMs = 50 },
            PollInterval = TimeSpan.FromMilliseconds(10), RequiredFalseConfirmations = falseConfirmations
        };
    private sealed class Callback(Func<MacroContext, CancellationToken, Task> callback) : MacroStep
    {
        public override Task ExecuteAsync(MacroContext context, CancellationToken ct = default) => callback(context, ct);
    }
    private sealed class Constant(bool value) : ICondition
    {
        public string Description => "constant";
        public Task<ConditionEvaluation> EvaluateDetailedAsync(MacroContext context, CancellationToken ct = default)
            => Task.FromResult(value ? ConditionEvaluation.Satisfied : ConditionEvaluation.Unsatisfied);
    }
    private sealed class Harness : IDisposable
    {
        public readonly ConcurrentQueue<string> Log = new();
        public readonly Capture Capture = new();
        public readonly Matcher Matcher = new();
        public readonly MacroController Controller = new();
        public readonly MacroExecutor Executor = new();
        public MacroContext Context { get; }
        public Harness() => Context = new(Capture, Controller) { ImageMatcher = Matcher, MaxRuntime = TimeSpan.FromSeconds(3) };
        public MacroStep Record(string name) => new Callback((_, _) => { Log.Enqueue(name); return Task.CompletedTask; });
        public async Task Run(IReadOnlyList<MacroStep> steps, int maxIterations = 100_000)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Executor.ExecuteAsync(new MacroModel { Steps = steps,
                Settings = new MacroSettings { MaxIterations = maxIterations } }, Context, timeout.Token);
        }
        public void Dispose() { Controller.Dispose(); Capture.Dispose(); }
    }
    private sealed class Capture : IScreenCaptureService
    {
        public readonly ConcurrentQueue<CaptureFrame> Frames = new();
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default) => CaptureRegionAsync(new(0, 0, 50, 50), ct);
        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var frame = TestFrameFactory.CreateSolidColor(region.Width, region.Height, 0, 0, 0, originX: region.X, originY: region.Y);
            Frames.Enqueue(frame); return Task.FromResult(frame);
        }
        public void Dispose() { foreach (var frame in Frames) frame.Dispose(); }
    }
    private sealed class Matcher : IImageMatcher
    {
        public Func<string, bool> Match = _ => false;
        public readonly ConcurrentQueue<CaptureFrame> Frames = new();
        public int Checks;
        public Task<MatchResult> MatchAsync(CaptureFrame frame, string templatePath, double minConfidence = .8, Rectangle? searchRegion = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Checks); Frames.Enqueue(frame);
            return Task.FromResult(Match(templatePath) ? MatchResult.Matched(.99, new(1, 1, 2, 2)) : MatchResult.NotFound);
        }
        public Task<MatchResult> MatchAsync(CaptureFrame frame, byte[] templateImageBytes, double minConfidence = .8, Rectangle? searchRegion = null, CancellationToken ct = default)
            => MatchAsync(frame, "bytes", minConfidence, searchRegion, ct);
        public void Dispose() { }
    }
}
