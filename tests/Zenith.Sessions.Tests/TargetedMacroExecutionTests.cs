using System.Diagnostics;
using System.Reflection;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using Xunit;
using Zenith.MacroHost.Execution;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;
// Real interpreter and shared input owner; all recipients and transport effects are memory-only.
public sealed partial class TargetedMacroExecutionTests
{
    private static KeyPressStep Key(int code = 65, string stroke = "Press", int delay = 0) => new()
    {
        KeyCode = code,
        Stroke = stroke,
        DelayAfterMs = delay
    };
    private static ClickStep Click(string stroke = "Press") => new()
    {
        IsClientRelative = true,
        Position = new(10, 20),
        RecordedClientWidth = 100,
        RecordedClientHeight = 100,
        Stroke = stroke,
        DelayAfterMs = 0
    };
    private static MacroModel Model(params MacroStep[] steps) => new()
    {
        Steps = steps,
        Settings = new()
        {
            MaxRuntime = TimeSpan.FromSeconds(10)
        }
    };
    private static TargetedMacroPlan Plan(params MacroStep[] steps) => TargetedMacroPlan.Compile(Model(steps), TargetedMacroScope.OwnedWindowDiagnostic);
    private static TargetedInputTarget Target(int id = 1) => new(Guid.NewGuid(), id, new DateTime(2026, 1, 1).Ticks, (nint)(100 + id), new(0, 0, 200, 200), 1);
    private static TargetedMacroRun Start(Fixture fixture, TargetedMacroPlan plan, TargetedInputTarget? target = null)
    {
        var result = fixture.Runner.TryStart(plan, target ?? fixture.A);
        Assert.Equal(TargetedMacroStartStatus.Started, result.Status);
        return Assert.IsType<TargetedMacroRun>(result.Run);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("disabled-unknown")]
    [InlineData("unicode")]
    [InlineData("screen")]
    [InlineData("camera")]
    [InlineData("horizontal-wheel")]
    [InlineData("hint")]
    [InlineData("target")]
    [InlineData("unmatched-up")]
    [InlineData("held-press-up")]
    public async Task WholeDocumentRefusesUnsupportedSemanticsBeforeAnyLeaseOrInput(string invalid)
    {
        await using var f = new Fixture();
        MacroStep bad = invalid switch
        {
            "unknown" => new UnknownStep(),
            "disabled-unknown" => new UnknownStep
            {
                IsEnabled = false
            },
            "unicode" => new KeyPressStep
            {
                IsUnicode = true,
                KeyCode = 65
            },
            "screen" => new ClickStep(),
            "camera" => new MouseMoveStep
            {
                IsRelative = true
            },
            "horizontal-wheel" => new MouseWheelStep
            {
                DeltaX = 120
            },
            "hint" => new ClickStep
            {
                IsClientRelative = true,
                TargetWindowName = "synthetic-private-title"
            },
            _ => Key(66, "Up")};
        var model = invalid == "held-press-up" ? Model(Click("Down"), Click(), Click("Up")) : Model(Key(), bad);
        if (invalid == "target")
            model.Settings.Target = new()
            {
                Mode = MacroTargetMode.Screen
            };
        var error = Assert.Throws<TargetedMacroPlanException>(() => TargetedMacroPlan.Compile(model, TargetedMacroScope.OwnedWindowDiagnostic));
        Assert.DoesNotContain("synthetic-private", error.ToString());
        Assert.Empty(f.Transport.Commands);
        Assert.Equal(0, f.Transport.Validations);
        var lease = f.Input.TryOpen(f.A, TargetedInputPurpose.OwnedWindowDiagnostic);
        Assert.NotNull(lease.Run);
        await lease.Run!.StopAsync();
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("forever")]
    [InlineData("speed")]
    [InlineData("repeats")]
    [InlineData("runtime")]
    [InlineData("steps")]
    public void InvalidOrUnboundedSettingsCannotProduceAnExecutablePlan(string invalid)
    {
        var source = Model(Key());
        if (invalid == "forever")
            source.Settings.RepeatUntilStopped = true;
        if (invalid == "speed")
            source.Settings.PlaybackSpeed = double.NaN;
        if (invalid == "repeats")
            source.Settings.RepeatCount = 1001;
        if (invalid == "runtime")
            source.Settings.MaxRuntime = TimeSpan.Zero;
        if (invalid == "steps")
            source = new()
            {
                Steps = Enumerable.Range(0, 1025).Select(_ => (MacroStep)Key()).ToArray()
            };
        Assert.Throws<TargetedMacroPlanException>(() => TargetedMacroPlan.Compile(source, invalid == "scope" ? (TargetedMacroScope)0 : TargetedMacroScope.OwnedWindowDiagnostic));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledSavedMacroIsRefusedAsDisabledNotAsAResourceLimit(bool withSavedTarget)
    {
        var model = Model(Key());
        model.Settings.IsActive = false;
        if (withSavedTarget)
            model.Settings.Target = new() { Mode = MacroTargetMode.Screen };
        var error = Assert.Throws<TargetedMacroPlanException>(() => TargetedMacroPlan.Compile(model, TargetedMacroScope.OwnedWindowDiagnostic));
        Assert.NotEqual(TargetedMacroPlanProblem.ResourceLimit, error.Problem);
        Assert.NotEqual(TargetedMacroPlanProblem.UnsupportedTarget, error.Problem);
        Assert.DoesNotContain("limits", error.Message);
        Assert.Equal(TargetedMacroPlanProblem.Disabled, error.Problem);
        Assert.Equal("The saved macro is disabled. Enable it in the editor before running it.", error.Message);
    }

    [Fact]
    public async Task FrozenPlanAndPrivateRealInterpreterStepsIgnoreLaterSourceMutation()
    {
        await using var f = new Fixture();
        MacroStep[] steps = [Key(65)];
        var model = Model(steps);
        var plan = TargetedMacroPlan.Compile(model, TargetedMacroScope.OwnedWindowDiagnostic);
        steps[0] = Key(66);
        model.Settings.RepeatCount = 1000;
        model.Settings.PlaybackSpeed = 10;
        model.Settings.Target = new()
        {
            Mode = MacroTargetMode.Screen
        };
        var run = Start(f, plan);
        var result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TargetedMacroResultStatus.Completed, result.Status);
        Assert.Equal(new[] { TargetedInputKind.KeyDown, TargetedInputKind.KeyUp }, f.Transport.Commands.Select(item => item.Signal.Kind));
        Assert.All(f.Transport.Commands, item => Assert.Equal(65, item.Signal.Key));
        Assert.Equal(1, result.Snapshot.CompletedSteps);
        Assert.Equal(run.Id, result.Snapshot.Input.RunId);
        Assert.False(result.Snapshot.AccountVerified);
        Assert.False(result.Snapshot.RobloxQualified);
    }

    [Fact]
    public async Task DeclaredInputVocabularyAndActualOwnerHoldBudgetRejectTheWholePlanBeforeOpening()
    {
        await using var f = new Fixture(maximumHolds: 1);
        var plan = Plan(Key(65, "Down"), Key(66, "Down"));
        Assert.Equal(2, plan.PeakHolds);
        Assert.Equal(TargetedMacroStartStatus.UnsupportedInput, f.Runner.TryStart(plan, f.A).Status);
        await using var extra = new TargetedMacroRunner(f.Input, new([65], TargetedMacroSignals.None), (_, _) => true);
        Assert.Equal(TargetedMacroStartStatus.UnsupportedInput, extra.TryStart(Plan(Key(66)), f.B).Status);
        Assert.Empty(f.Transport.Commands);
        Assert.Equal(0, f.Transport.Validations);
        var lease = f.Input.TryOpen(f.A, TargetedInputPurpose.OwnedWindowDiagnostic);
        Assert.NotNull(lease.Run);
        await lease.Run!.StopAsync();
    }

    [Fact]
    public async Task UnsupportedLaterKeyOrImplicitMoveNeverEmitsAnEarlierSupportedKey()
    {
        var transport = new Transport();
        await using var input = new TargetedInputCoordinator(transport);
        var target = Target();
        transport.Register(target);
        await using var runner = new TargetedMacroRunner(input, new([65], TargetedMacroSignals.LeftButton), (_, _) => true);
        Assert.Equal(TargetedMacroStartStatus.UnsupportedInput, runner.TryStart(Plan(Key(65), Key(66)), target).Status);
        Assert.Equal(TargetedMacroStartStatus.UnsupportedInput, runner.TryStart(Plan(Key(65), Click()), target).Status);
        Assert.Empty(transport.Commands);
        Assert.Equal(0, transport.Validations);
        var lease = input.TryOpen(target, TargetedInputPurpose.OwnedWindowDiagnostic);
        Assert.NotNull(lease.Run);
        await lease.Run!.StopAsync();
    }

    [Fact]
    public async Task RealClientRelativeStepsUseExplicitRecipientSeamAndRecordedScalingWithoutForegroundQueries()
    {
        await using var f = new Fixture();
        var run = Start(f, Plan(Click(), new MouseMoveStep { IsClientRelative = true, Position = new(25, 30), RecordedClientWidth = 100, RecordedClientHeight = 100 }, new MouseWheelStep(120)));
        var result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TargetedMacroResultStatus.Completed, result.Status);
        Assert.All(f.Transport.Commands, item => Assert.Equal(f.A, item.Target));
        var commands = f.Transport.Commands;
        Assert.Equal(new[] { TargetedInputKind.Move, TargetedInputKind.LeftDown, TargetedInputKind.LeftUp, TargetedInputKind.Move, TargetedInputKind.Wheel }, commands.Select(item => item.Signal.Kind));
        Assert.Equal((20, 40), (commands[0].Signal.X, commands[0].Signal.Y));
        Assert.Equal((50, 60), (commands[^1].Signal.X, commands[^1].Signal.Y));
    }

    [Fact]
    public async Task TwoRealInterpretersKeepProgressAndActiveDelayIndependentAcrossPauseAndResume()
    {
        await using var f = new Fixture();
        var plan = Plan(Key(65, "Down"), new DelayStep(TimeSpan.FromSeconds(1)), Key(65, "Up"));
        var a = Start(f, plan);
        var b = Start(f, plan, f.B);
        await Until(() => f.Transport.Count(a.Id, TargetedInputKind.KeyDown) == 1 && f.Transport.Count(b.Id, TargetedInputKind.KeyDown) == 1);
        var paused = await a.PauseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TargetedMacroState.Paused, paused.State);
        Assert.Equal(0, paused.Input.SubmittedHolds);
        Assert.Equal(1, paused.Input.LogicalHolds);
        var bResult = await b.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TargetedMacroResultStatus.Completed, bResult.Status);
        Assert.False(a.Completion.IsCompleted);
        Assert.Equal(1, a.GetSnapshot().CompletedSteps);
        int before = a.GetSnapshot().CompletedSteps;
        var elapsed = Stopwatch.StartNew();
        await a.ResumeAsync();
        var aResult = await a.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(100));
        Assert.Equal(TargetedMacroResultStatus.Completed, aResult.Status);
        Assert.Equal(3, aResult.Snapshot.CompletedSteps);
        Assert.Equal(1, before);
        Assert.Equal(2, f.Transport.Count(a.Id, TargetedInputKind.KeyDown));
        Assert.Single(f.Transport.Commands.Where(item => item.RunId == a.Id && item.Cleanup && item.Signal.Kind == TargetedInputKind.KeyUp));
    }

    [Fact]
    public async Task PauseDuringBlockedRestorationOverridesResumeAndDoesNotLoseThePauseRequest()
    {
        await using var f = new Fixture();
        var run = Start(f, Plan(Key(65, "Down"), new DelayStep(TimeSpan.FromSeconds(2)), Key(65, "Up")));
        await Until(() => f.Transport.Count(run.Id, TargetedInputKind.KeyDown) == 1);
        await run.PauseAsync();
        var gate = Gate();
        var entered = Gate();
        f.Transport.Before = async command =>
        {
            if (command.RunId == run.Id && !command.Cleanup && command.Signal.Kind == TargetedInputKind.KeyDown)
            {
                entered.TrySetResult();
                await gate.Task;
            }
        };
        Task<TargetedMacroRunSnapshot> resume = run.ResumeAsync();
        Task<TargetedMacroRunSnapshot>? pause = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            pause = run.PauseAsync();
            Assert.False(pause.IsCompleted);
        }
        finally
        {
            gate.TrySetResult();
            await resume.WaitAsync(TimeSpan.FromSeconds(5));
            if (pause is not null)
                await pause.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(TargetedMacroState.Paused, run.GetSnapshot().State);
        Assert.Equal(TargetedInputRunState.Paused, run.GetSnapshot().Input.State);
        Assert.Equal(0, run.GetSnapshot().Input.SubmittedHolds);
        Assert.False(run.Completion.IsCompleted);
        Assert.Equal(1, f.Transport.Count(run.Id, TargetedInputKind.KeyDown));
        await run.StopAsync();
    }

    [Fact]
    public async Task StopAndDisposalJoinAnUncooperativeEmissionAndCannotAdmitLateInput()
    {
        var f = new Fixture();
        var gate = Gate();
        var entered = Gate();
        f.Transport.Before = async command =>
        {
            if (!command.Cleanup)
            {
                entered.TrySetResult();
                await gate.Task;
            }
        };
        var run = Start(f, Plan(Key()));
        Task? disposal = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            disposal = f.Runner.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.False(run.Completion.IsCompleted);
        }
        finally
        {
            gate.TrySetResult();
            if (disposal is not null)
                await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            await f.DisposeAsync();
        }

        Assert.Empty(f.Transport.Commands.Where(item => !item.Cleanup));
        Assert.NotEqual(TargetedMacroResultStatus.Completed, (await run.Completion).Status);
    }

    [Fact]
    public async Task FailedClearStillDisposesTheRealClockAndKeepsLeaseQuarantinedUntilExplicitRetry()
    {
        var f = new Fixture();
        var run = Start(f, Plan(Key(65, "Down"), new DelayStep(TimeSpan.FromSeconds(2))));
        try
        {
            await Until(() => f.Transport.Count(run.Id, TargetedInputKind.KeyDown) == 1);
            f.Transport.RejectCleanup = true;
            await run.PauseAsync();
            var result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TargetedMacroResultStatus.CleanupFailed, result.Status);
            Assert.Equal(TargetedInputRunState.Faulted, result.Snapshot.Input.State);
            var session = typeof(TargetedMacroRun).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(run)!;
            Assert.True((bool)typeof(PlaybackSessionContext).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!);
            Assert.Null(f.Input.TryOpen(f.A, TargetedInputPurpose.OwnedWindowDiagnostic).Run);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Runner.DisposeAsync().AsTask());
            f.Transport.RejectCleanup = false;
            var retried = await run.RetryCleanupAsync();
            Assert.Equal(TargetedInputRunState.Stopped, retried.Input.State);
            var next = f.Input.TryOpen(f.A, TargetedInputPurpose.OwnedWindowDiagnostic);
            Assert.NotNull(next.Run);
            await next.Run!.StopAsync();
        }
        finally
        {
            f.Transport.RejectCleanup = false;
            await run.RetryCleanupAsync();
            await f.DisposeAsync();
        }
    }

    // The first ordinary check passes; every later one sees the window moved or resized.
    // Cleanup checks ignore geometry, as the transport contract requires.
    private static Func<TargetedInputTarget, bool, TargetedInputValidation> GeometryChangesAfterFirstInput()
    {
        int ordinary = 0;
        return (_, cleanup) => cleanup || Interlocked.Increment(ref ordinary) == 1
            ? TargetedInputValidation.Current
            : TargetedInputValidation.GeometryChanged;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TargetChangeFaultQuarantinesTheLeaseOnlyWhenItsHoldReleaseFails(bool rejectCleanup)
    {
        var f = new Fixture();
        f.Transport.Validation = GeometryChangesAfterFirstInput();
        f.Transport.RejectCleanup = rejectCleanup;
        var run = Start(f, Plan(Key(65, "Down"), new DelayStep(TimeSpan.FromMilliseconds(20)), Key(65, "Up")));
        try
        {
            var result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            if (rejectCleanup)
            {
                // The press may still be held, so the lease stays quarantined until explicit retry.
                Assert.Equal(TargetedMacroResultStatus.CleanupFailed, result.Status);
                Assert.Equal(TargetedInputRunState.Faulted, result.Snapshot.Input.State);
                Assert.Equal(1, result.Snapshot.Input.SubmittedHolds);
                Assert.Null(f.Input.TryOpen(f.A, TargetedInputPurpose.OwnedWindowDiagnostic).Run);
                Assert.Equal(TargetedInputKind.KeyDown, Assert.Single(f.Transport.Commands).Signal.Kind);
                return;
            }

            Assert.Equal(TargetedMacroResultStatus.Faulted, result.Status);
            Assert.Equal(TargetedMacroProblem.InputRejected, result.Problem);
            Assert.Equal(TargetedMacroState.Faulted, result.Snapshot.State);
            Assert.Equal(TargetedInputRunState.Stopped, result.Snapshot.Input.State);
            Assert.Equal(0, result.Snapshot.Input.SubmittedHolds);
            Assert.DoesNotContain("Cleanup failed", result.Snapshot.Explanation);
            // The runner's stop only freed the faulted run's lease; the input snapshot keeps why it faulted.
            Assert.StartsWith("The pinned target identity or geometry changed", result.Snapshot.Input.Explanation);
            Assert.EndsWith("its holds were released and the window lease is free.", result.Snapshot.Input.Explanation);
            Assert.Collection(f.Transport.Commands,
                pressed => { Assert.False(pressed.Cleanup); Assert.Equal(TargetedInputKind.KeyDown, pressed.Signal.Kind); },
                released => { Assert.True(released.Cleanup); Assert.Equal(TargetedInputKind.KeyUp, released.Signal.Kind); });
            f.Transport.Validation = null;
            // Nothing was left to release, so the window lease is free and the runner admits the next run.
            Assert.Equal(TargetedMacroResultStatus.Completed, (await Start(f, Plan(Key())).Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        }
        finally
        {
            f.Transport.Validation = null;
            f.Transport.RejectCleanup = false;
            if (run.Completion.IsCompleted && run.GetSnapshot().Input.State != TargetedInputRunState.Stopped)
                Assert.Equal(TargetedInputRunState.Stopped, (await run.RetryCleanupAsync()).Input.State);
            await f.DisposeAsync();
        }
    }

    // The final input snapshot says why the runner released the lease; only a user's Stop says the user stopped it.
    [Theory]
    [InlineData("completed", TargetedMacroResultStatus.Completed, "Macro completed; input lease released.")]
    [InlineData("runtime-limit", TargetedMacroResultStatus.Stopped, "Macro reached its runtime limit; input lease released.")]
    [InlineData("interpreter-fault", TargetedMacroResultStatus.Faulted, "Macro stopped after a failure; input lease released.")]
    [InlineData("user", TargetedMacroResultStatus.Stopped, "Stopped by user.")]
    [InlineData("runner-panic", TargetedMacroResultStatus.Stopped, "Stopped by emergency stop.")]
    [InlineData("runner-closed", TargetedMacroResultStatus.Stopped, "Macro runner closed; input lease released.")]
    public async Task FinalInputSnapshotSaysWhyTheRunnerReleasedTheLease(string ending, TargetedMacroResultStatus status, string expected)
    {
        var f = new Fixture();
        try
        {
            var model = ending switch
            {
                "completed" => Model(Key()),
                "interpreter-fault" => Model(Key(), Key()),
                // No input, so the cap can only expire in the delay, never during a submission.
                "runtime-limit" => Model(new DelayStep(TimeSpan.FromSeconds(2))),
                _ => Model(Key(), new DelayStep(TimeSpan.FromSeconds(2)))
            };
            if (ending == "runtime-limit")
                model.Settings.MaxRuntime = TimeSpan.FromMilliseconds(100);
            if (ending == "interpreter-fault")
                model.Settings.MaxIterations = 1; // The second step exceeds it inside the interpreter.
            var run = Start(f, TargetedMacroPlan.Compile(model, TargetedMacroScope.OwnedWindowDiagnostic));
            if (ending is "user" or "runner-panic" or "runner-closed")
            {
                await Until(() => f.Transport.Count(run.Id, TargetedInputKind.KeyUp) == 1);
                if (ending == "user")
                    _ = run.StopAsync();
                else if (ending == "runner-panic")
                    f.Runner.SignalPanic(); // The runner's own panic, without the input owner's SignalStopAll.
                else
                    _ = f.Runner.DisposeAsync().AsTask();
            }

            var result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(status, result.Status);
            Assert.Equal(TargetedInputRunState.Stopped, result.Snapshot.Input.State);
            Assert.Equal(expected, result.Snapshot.Input.Explanation);
        }
        finally
        {
            await f.DisposeAsync();
        }
    }

    // The run's own stop can land while a key send is in flight. The input owner then stops on the cancelled batch, and
    // the run reports that stop (its runtime cap, the user's Stop, Panic), not an input rejection.
    [Theory]
    [InlineData("runtime-limit", TargetedMacroProblem.RuntimeLimit, "Input batch cancelled before any of it was sent; releasing the run's holds.")]
    [InlineData("user", TargetedMacroProblem.Stopped, "Stopped by user.")]
    [InlineData("runner-panic", TargetedMacroProblem.Stopped, "Stopped by emergency stop.")]
    public async Task AStopThatLandsDuringAKeySendEndsTheRunAsThatStopNotAsAnInputRejection(string ending, TargetedMacroProblem problem, string expected)
    {
        var f = new Fixture();
        try
        {
            var entered = Gate();
            // Like a slow native submission: the send stays inside the transport until the run's token is cancelled.
            f.Transport.BeforeWithToken = async (command, ct) =>
            {
                if (command.Cleanup)
                    return;
                entered.TrySetResult();
                await WhenCancelledAsync(ct);
            };
            var model = Model(Key());
            // Long enough for the send to reach the transport first under a loaded parallel run; the cap then expires there.
            if (ending == "runtime-limit")
                model.Settings.MaxRuntime = TimeSpan.FromSeconds(2);
            var run = Start(f, TargetedMacroPlan.Compile(model, TargetedMacroScope.OwnedWindowDiagnostic));
            var reached = await Task.WhenAny(entered.Task, run.Completion).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(ReferenceEquals(reached, entered.Task), "The run ended before its key send reached the transport, so this run did not exercise a stop during a send.");
            if (ending == "user")
                _ = run.StopAsync();
            else if (ending == "runner-panic")
                f.Runner.SignalPanic();
            var result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TargetedMacroResultStatus.Stopped, result.Status);
            Assert.Equal(problem, result.Problem);
            Assert.Equal(TargetedMacroState.Stopped, result.Snapshot.State);
            Assert.Equal(TargetedInputRunState.Stopped, result.Snapshot.Input.State);
            Assert.Equal(expected, result.Snapshot.Input.Explanation);
            Assert.Empty(f.Transport.Commands);
        }
        finally
        {
            await f.DisposeAsync();
        }
    }

    [Fact]
    public async Task CompanionStopReleasesItsRunWithoutSayingTheUserStoppedIt()
    {
        await using var f = new Fixture();
        var source = new HostDocument(Model(Key(), new DelayStep(TimeSpan.FromSeconds(2))));
        await using var preparer = new SavedMacroPlanPreparer(source, static _ => { });
        var host = new HostInputOperationArbiter();
        var operation = new CompanionMacroOperations(host, preparer, f.Runner);
        var running = operation.RunAsync(source.Reference, [f.A], host.StopGeneration);
        await Until(() => f.Transport.Commands.Count == 2);
        host.SignalStop();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
        var recipient = Assert.Single(result.Recipients).Run!;
        Assert.Equal(TargetedMacroResultStatus.Stopped, recipient.Status);
        Assert.Equal("Its companion operation stopped; input lease released.", recipient.Snapshot.Input.Explanation);
    }

    [Fact]
    public async Task RunThatCannotBeComposedReleasesItsInputLeaseAsNotStarted()
    {
        var f = new Fixture();
        var runner = new TargetedMacroRunner(f.Input, new([65, 66], (TargetedMacroSignals)31), (_, _) => true, 32,
            composing: static () => throw new InvalidOperationException("Synthetic composition failure."));
        var host = new HostInputOperationArbiter();
        Assert.True(host.TryAcquire(HostInputOperationKind.Companion, out var lease, out _));
        try
        {
            Assert.True(runner.TryBindOperation(lease!, runner.StopGeneration));
            var start = runner.TryPrepareBound(Plan(Key()), f.A, lease!, out var unstarted);
            Assert.Equal(TargetedMacroStartStatus.Rejected, start.Status);
            Assert.Null(start.Run);
            var input = await Assert.IsType<UnstartedTargetedMacro>(unstarted).Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TargetedInputRunState.Stopped, input.State);
            Assert.Equal("Run could not start; input lease released.", input.Explanation);
            Assert.Empty(f.Transport.Commands);
        }
        finally
        {
            await lease!.CompleteAsync(true);
            await runner.DisposeAsync();
            await f.DisposeAsync();
        }
    }

    [Fact]
    public async Task LegacySynchronousClearRetainsLeaseAndTheNextEventAwaitsItsActualRelease()
    {
        var transport = new Transport();
        await using var input = new TargetedInputCoordinator(transport);
        var target = Target();
        transport.Register(target);
        var lease = input.TryOpen(target, TargetedInputPurpose.OwnedWindowDiagnostic).Run!;
        using var player = new TargetedMacroPlayer(lease);
        using var control = new MacroController();
        await using var session = new PlaybackSessionContext(control);
        player.Session = session;
        await player.PlayAsync(Events(65, true));
        var gate = Gate();
        var entered = Gate();
        transport.Before = async command =>
        {
            if (command.Cleanup)
            {
                entered.TrySetResult();
                await gate.Task;
            }
        };
        player.ReleaseAllHeldKeys();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task next = player.PlayAsync(Events(66, false));
        try
        {
            Assert.False(next.IsCompleted);
            Assert.Null(input.TryOpen(target, TargetedInputPurpose.OwnedWindowDiagnostic).Run);
            Assert.Single(transport.Commands);
        }
        finally
        {
            gate.TrySetResult();
            await next.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(lease.Snapshot.RunId, transport.Commands[^1].RunId);
        Assert.Equal(new[] { 65, 65, 66, 66 }, transport.Commands.Select(command => command.Signal.Key));
        await lease.StopAsync();
    }

    [Fact]
    public async Task CompletedOldReadinessGateCannotTurnAnInProgressPauseIntoInputFailure()
    {
        var transport = new Transport();
        await using var input = new TargetedInputCoordinator(transport);
        var target = Target();
        transport.Register(target);
        var lease = input.TryOpen(target, TargetedInputPurpose.OwnedWindowDiagnostic).Run!;
        using var player = new TargetedMacroPlayer(lease);
        using var control = new BoundaryControl();
        await using var session = new PlaybackSessionContext(control);
        player.Session = session;
        var entered = Gate();
        control.OnPausedRead = () =>
        {
            player.CloseAdmission();
            lease.PauseAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            entered.TrySetResult();
        };
        Task emit = Task.Run(() => player.PlayAsync(Events(65, false)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Task.Delay(80);
            Assert.False(emit.IsCompleted);
            Assert.Empty(transport.Commands);
            Assert.False(player.Rejected);
        }
        finally
        {
            await lease.ResumeAsync();
            player.OpenAdmission();
            await emit.WaitAsync(TimeSpan.FromSeconds(5));
            await lease.StopAsync();
        }

        Assert.False(player.Rejected);
        Assert.Equal(2, transport.Commands.Count);
    }

    private static InputEvent[] Events(int key, bool downOnly)
    {
        long at = Stopwatch.GetTimestamp();
        return downOnly ? [new(InputEventType.KeyDown, at, 0, 0, key)] : [new(InputEventType.KeyDown, at, 0, 0, key), new(InputEventType.KeyUp, at, 0, 0, key)];
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task WhenCancelledAsync(CancellationToken ct)
    {
        var cancelled = Gate();
        using var registration = ct.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), cancelled);
        await cancelled.Task;
    }

    private static async Task Until(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("Controlled interpreter boundary was not reached.");
            await Task.Delay(1);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Transport Transport { get; } = new();
        public TargetedInputCoordinator Input { get; }
        public TargetedMacroRunner Runner { get; }
        public TargetedInputTarget A { get; } = Target();
        public TargetedInputTarget B { get; } = Target(2);

        public Fixture(int maximumHolds = 64, Func<TargetedInputTarget, TargetedMacroScope, bool>? eligible = null)
        {
            Transport.Register(A);
            Transport.Register(B);
            Input = new(Transport, new(MaximumHolds: maximumHolds));
            Runner = new(Input, new([65, 66], (TargetedMacroSignals)31), eligible ?? ((_, scope) => scope == TargetedMacroScope.OwnedWindowDiagnostic));
        }

        public async ValueTask DisposeAsync()
        {
            await Runner.DisposeAsync();
            await Input.DisposeAsync();
        }
    }

    private sealed class Transport : ITargetedInputTransport
    {
        private readonly object _sync = new();
        private readonly HashSet<TargetedInputTarget> _registered = new();
        private readonly List<TargetedInputCommand> _commands = [];
        public Func<TargetedInputCommand, Task>? Before;
        // Like Before, for a test that must see the batch's own token: (command, ct) => work before admission is checked.
        public Func<TargetedInputCommand, CancellationToken, Task>? BeforeWithToken;
        // Test-only answer for a registered target: (target, cleanup) => validation.
        public Func<TargetedInputTarget, bool, TargetedInputValidation>? Validation;
        public bool RejectCleanup;
        public int Validations;
        public IReadOnlyList<TargetedInputCommand> Commands
        {
            get
            {
                lock (_sync)
                    return _commands.ToArray();
            }
        }

        public void Register(TargetedInputTarget target)
        {
            lock (_sync)
                _registered.Add(target);
        }

        public int Count(Guid run, TargetedInputKind kind)
        {
            lock (_sync)
                return _commands.Count(command => command.RunId == run && command.Signal.Kind == kind);
        }

        public ValueTask<TargetedInputValidation> ValidateAsync(TargetedInputTarget target, bool cleanup, CancellationToken ct)
        {
            Interlocked.Increment(ref Validations);
            lock (_sync)
                if (!_registered.Contains(target))
                    return ValueTask.FromResult(TargetedInputValidation.Unavailable);
            return ValueTask.FromResult(Volatile.Read(ref Validation) is { } validation ? validation(target, cleanup) : TargetedInputValidation.Current);
        }

        public async ValueTask<TargetedInputSubmission> SubmitAsync(TargetedInputCommand command, Func<bool> admit, CancellationToken ct)
        {
            if (Before is { } before)
                await before(command);
            if (BeforeWithToken is { } beforeWithToken)
                await beforeWithToken(command, ct);
            if (!admit() || command.Cleanup && RejectCleanup)
                return TargetedInputSubmission.Rejected;
            lock (_sync)
            {
                if (_commands.Count >= 1024)
                    throw new InvalidOperationException("Controlled receiver budget exceeded.");
                _commands.Add(command);
            }

            return TargetedInputSubmission.Queued;
        }
    }

    private sealed class BoundaryControl : IMacroControl
    {
        private readonly CancellationTokenSource _stop = new();
        public Action? OnPausedRead;
        public bool IsPaused
        {
            get
            {
                var callback = OnPausedRead;
                OnPausedRead = null;
                callback?.Invoke();
                return false;
            }
        }

        public CancellationToken StopToken => _stop.Token;
        public CancellationToken PanicToken => _stop.Token;
        public bool IsPanicTriggered => false;

        public Task PauseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResumeAsync() => Task.CompletedTask;
        public Task WaitIfPausedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync()
        {
            _stop.Cancel();
            return Task.CompletedTask;
        }

        public void Panic() => _stop.Cancel();
        public void Dispose() => _stop.Dispose();
    }
}
