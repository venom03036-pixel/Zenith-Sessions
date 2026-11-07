using System.Diagnostics;
using System.Drawing;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using Xunit;

namespace MacroSuite.Core.Tests.Execution;

public sealed class ContinuousImageTests
{
    [Fact]
    public async Task ScannerContinuesDuringLongBody_AndSessionIsDrainedBeforeDisposal()
    {
        using var run = new Harness();
        var scannedDuringBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int inBody = 0, observations = 0;
        run.Matcher.Scan = (request, generation) =>
        {
            if (Volatile.Read(ref inBody) == 1 && Interlocked.Increment(ref observations) >= 2) scannedDuringBody.TrySetResult();
            return Result(request, generation, ImageScanStatus.Complete, Hit(10));
        };
        await run.Run([Trigger("A"), new Callback(async (context, ct) =>
        {
            Volatile.Write(ref inBody, 1);
            await scannedDuringBody.Task.WaitAsync(ct);
            context.SignalControlFlow(ControlFlowSignal.Break);
        })]);
        Assert.True(observations >= 2);
        Assert.True(run.Matcher.Disposed);
        Assert.Equal(0, run.Matcher.ConcurrentNativeCalls);
    }

    [Fact]
    public async Task EveryOccurrenceGetsATurn_BetweenGroups_AndCoordinatesBelongToItsOwnHit()
    {
        using var run = new Harness();
        var log = new List<(string, int)>();
        run.Matcher.Scan = (request, generation) => Result(request, generation, ImageScanStatus.Complete,
            request.TemplatePath == "A" ? [Hit(10), Hit(50)] : [Hit(110)]);
        Callback Body(string name) => new((context, _) =>
        {
            log.Add((name, context.Variables.Get<int>(ImageCondition.LastMatchXVariable)));
            if (log.Count == 3) context.SignalControlFlow(ControlFlowSignal.Break);
            return Task.CompletedTask;
        });
        await run.Run([Trigger("A", delay: 5000), Body("A"), Trigger("B", delay: 5000), Body("B")]);
        Assert.Equal(new[] { ("A", 20), ("B", 120), ("A", 60) }, log);
    }

    [Fact]
    public async Task PartialSweepAndDeferredScans_DoNotTurnSeenLocationsIntoAbsence()
    {
        using var run = new Harness();
        long firstObservation = Stopwatch.GetTimestamp();
        long secondObservation = firstObservation + Stopwatch.Frequency / 10;
        run.Matcher.Scan = (request, turn) => turn switch
        {
            1 => Result(request, 1, ImageScanStatus.Partial, Hit(10) with { TimestampTicks = firstObservation }),
            2 => Result(request, 1, ImageScanStatus.Partial),
            3 => Result(request, 1, ImageScanStatus.Complete),
            4 => Result(request, 2, ImageScanStatus.Deferred),
            _ => Result(request, 2, ImageScanStatus.Partial, Hit(10) with { TimestampTicks = secondObservation })
        };
        int firedAt = 0;
        await run.Run([Trigger("A", confirmations: 2), new Callback((context, _) =>
        { firedAt = run.Matcher.Scans; context.SignalControlFlow(ControlFlowSignal.Break); return Task.CompletedTask; })]);
        Assert.True(firedAt >= 5);
    }

    [Fact]
    public async Task FreshObservationsConfirmBeforeTheDetailSweepCompletes()
    {
        using var run = new Harness();
        run.Matcher.Scan = (request, turn) => Result(request, 1, ImageScanStatus.Partial,
            Hit(10) with { TimestampTicks = Stopwatch.GetTimestamp() });
        int firedAt = 0;
        await run.Run([Trigger("A", confirmations: 3), new Callback((context, _) =>
        { firedAt = run.Matcher.Scans; context.SignalControlFlow(ControlFlowSignal.Break); return Task.CompletedTask; })]);
        Assert.True(firedAt >= 3);
    }

    [Fact]
    public async Task ReusedHitTimestampDoesNotConfirmOrAdvanceStability_WhenOtherTilesAreFresh()
    {
        using var run = new Harness();
        long firstFrame = Stopwatch.GetTimestamp();
        long stableFrame = firstFrame + Stopwatch.Frequency / 20; // 50 ms of fresh image observations.
        run.Matcher.Scan = (request, turn) => Result(request, turn, ImageScanStatus.Partial,
            Hit(10) with { TimestampTicks = turn < 8 ? firstFrame : stableFrame });
        int firedAt = 0;
        var trigger = Trigger("A", confirmations: 2, stability: 40);
        await run.Run([trigger, new Callback((context, _) =>
        { firedAt = run.Matcher.Scans; context.SignalControlFlow(ControlFlowSignal.Break); return Task.CompletedTask; })]);
        Assert.True(firedAt >= 8);
    }

    [Fact]
    public async Task PixelJitterKeepsPerLocationCooldown()
    {
        using var run = new Harness();
        var times = new List<long>();
        var timer = Stopwatch.StartNew();
        run.Matcher.Scan = (request, generation) => Result(request, generation, ImageScanStatus.Complete, Hit(10 + generation % 2));
        await run.Run([Trigger("A", delay: 100), new Callback((context, _) =>
        {
            times.Add(timer.ElapsedMilliseconds);
            if (times.Count == 2) context.SignalControlFlow(ControlFlowSignal.Break);
            return Task.CompletedTask;
        })]);
        Assert.True(times[1] - times[0] >= 95);
    }

    [Fact]
    public async Task DisappearanceAndReappearanceKeepTheSameLocationCooldown()
    {
        using var run = new Harness();
        var times = new List<long>();
        var timer = Stopwatch.StartNew();
        int hideNextScan = 0, absentScans = 0;
        run.Matcher.Scan = (request, generation) =>
        {
            if (Interlocked.Exchange(ref hideNextScan, 0) == 1)
            {
                Interlocked.Increment(ref absentScans);
                return Result(request, generation, ImageScanStatus.Complete);
            }
            return Result(request, generation, ImageScanStatus.Complete, Hit(10));
        };
        await run.Run([Trigger("A", delay: 150), new Callback((context, _) =>
        {
            times.Add(timer.ElapsedMilliseconds);
            if (times.Count == 1) Volatile.Write(ref hideNextScan, 1);
            else context.SignalControlFlow(ControlFlowSignal.Break);
            return Task.CompletedTask;
        })]);
        Assert.Equal(1, absentScans);
        Assert.True(times[1] - times[0] >= 145);
    }

    [Fact]
    public async Task KnownLocationRepeatsOnItsCooldownDuringIncompleteCoverage_AndStopsWhenFreshCheckMisses()
    {
        using var run = new Harness();
        var freshMiss = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var times = new List<long>();
        var timer = Stopwatch.StartNew();
        int validations = 0;
        run.Matcher.Scan = (request, turn) => turn == 1
            ? Result(request, 1, ImageScanStatus.Partial, Hit(10))
            : Result(request, 1, turn % 2 == 0 ? ImageScanStatus.Deferred : ImageScanStatus.Partial);
        run.Matcher.Revalidate = (_, hit) =>
        {
            if (Interlocked.Increment(ref validations) < 3) return hit;
            freshMiss.TrySetResult();
            return null;
        };
        Task playback = run.Run([Trigger("A", delay: 50), new Callback((_, _) =>
        { times.Add(timer.ElapsedMilliseconds); return Task.CompletedTask; })]);
        await freshMiss.Task.WaitAsync(TimeSpan.FromSeconds(1));
        // Give further incomplete scans several turns: they must not resurrect a
        // location whose fresh native check has already established absence.
        await Task.Delay(100);
        await run.Control.StopAsync();
        Exception? error = await Record.ExceptionAsync(() => playback);
        Assert.True(error is null or OperationCanceledException, error?.ToString());
        Assert.Equal(2, times.Count);
        Assert.True(times[1] - times[0] >= 45);
        Assert.Equal(3, Volatile.Read(ref validations));
        Assert.True(run.Matcher.Scans > 3);
        Assert.True(run.Matcher.Disposed);
        Assert.Equal(0, run.Matcher.ConcurrentNativeCalls);
    }

    [Fact]
    public async Task CompletedAbsenceDuringBodyRequiresFreshConfirmationsOnReappearance()
    {
        using var run = new Harness();
        var absenceProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int phase = 0, emptyScans = 0, reappearances = 0, bodies = 0;
        long bodyFinished = 0, firstReappearance = 0, secondReappearance = 0;
        run.Matcher.Scan = (request, generation) =>
        {
            if (Volatile.Read(ref phase) == 0)
                return Result(request, generation, ImageScanStatus.Complete, Hit(10));
            if (Volatile.Read(ref phase) == 1)
            {
                // Reaching the next scan proves that the first completed absence
                // has been observed while the original body is still waiting.
                if (Interlocked.Increment(ref emptyScans) >= 2)
                {
                    absenceProcessed.TrySetResult();
                    return Result(request, generation, ImageScanStatus.Deferred);
                }
                return Result(request, generation, ImageScanStatus.Complete);
            }
            long now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(Volatile.Read(ref bodyFinished), now).TotalMilliseconds < 100)
                return Result(request, generation, ImageScanStatus.Deferred);
            if (firstReappearance == 0)
            {
                firstReappearance = now;
                Interlocked.Increment(ref reappearances);
            }
            if (secondReappearance == 0 && Stopwatch.GetElapsedTime(firstReappearance, now).TotalMilliseconds >= 150)
            {
                secondReappearance = now;
                Interlocked.Increment(ref reappearances);
            }
            return Result(request, generation, ImageScanStatus.Partial,
                Hit(10) with { TimestampTicks = secondReappearance == 0 ? firstReappearance : secondReappearance });
        };
        await run.Run([Trigger("A", confirmations: 2), new Callback(async (context, ct) =>
        {
            if (++bodies == 1)
            {
                Volatile.Write(ref phase, 1);
                await absenceProcessed.Task.WaitAsync(ct);
                Volatile.Write(ref bodyFinished, Stopwatch.GetTimestamp());
                Volatile.Write(ref phase, 2);
                return;
            }
            Assert.Equal(2, Volatile.Read(ref reappearances));
            context.SignalControlFlow(ControlFlowSignal.Break);
        })]);
        Assert.Equal(2, bodies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAndPanicInterruptLongBody_AndDrainScanner(bool panic)
    {
        using var run = new Harness();
        var bodyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int laterActions = 0;
        Task playback = run.Run([Trigger("A"), new Callback(async (_, ct) =>
        {
            bodyStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }), new Callback((_, _) => { laterActions++; return Task.CompletedTask; })]);
        await bodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await run.Control.PauseAsync();
        if (panic) run.Control.Panic();
        else await run.Control.StopAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => playback.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, laterActions);
        Assert.True(run.Matcher.Disposed);
        Assert.Equal(0, run.Matcher.ConcurrentNativeCalls);
        Assert.False(run.Control.IsPaused);
    }

    [Fact]
    public async Task PauseDiscardsConfirmationStreak_AndResumeRequiresNewObservations()
    {
        using var run = new Harness();
        var pauseRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int bodies = 0, firedAt = 0;
        run.Matcher.Scan = (request, generation) =>
        {
            if (generation == 2)
            {
                run.Control.PauseAsync().GetAwaiter().GetResult();
                pauseRequested.TrySetResult();
            }
            return Result(request, generation, ImageScanStatus.Complete, Hit(10));
        };
        Task playback = run.Run([Trigger("A", confirmations: 2), new Callback((context, _) =>
        {
            Interlocked.Increment(ref bodies);
            firedAt = run.Matcher.Scans;
            context.SignalControlFlow(ControlFlowSignal.Break);
            return Task.CompletedTask;
        })]);
        await pauseRequested.Task.WaitAsync(TimeSpan.FromSeconds(1));
        // Let the in-flight scan settle and both workers reach the pause gate.
        await Task.Delay(60);
        Assert.Equal(2, Volatile.Read(ref run.Matcher.Scans));
        Assert.Equal(0, Volatile.Read(ref bodies));
        await run.Control.ResumeAsync();
        await playback;
        Assert.True(firedAt >= 4);
        Assert.Equal(1, bodies);
    }

    [Fact]
    public async Task TargetWindowFollowsMovedAndResizedClient_WithoutSwitchingWithFocus()
    {
        using var run = new Harness();
        var windows = new FakeWindows();
        run.Windows = windows;
        Rectangle original = windows.TargetBounds;
        var moved = new Rectangle(-800, 150, 640, 480);
        var regions = new List<Rectangle?>();
        run.Matcher.Scan = (request, generation) =>
        {
            regions.Add(request.Region);
            if (generation == 1)
            {
                windows.TargetBounds = moved;
                windows.Foreground = FakeWindows.OtherHandle;
                return Result(request, generation, ImageScanStatus.Complete);
            }
            Assert.Equal(moved, request.Region);
            return Result(request, generation, ImageScanStatus.Complete,
                new ImageScanHit(new Rectangle(moved.X + 40, moved.Y + 30, 20, 20), .99));
        };
        await run.Run([Trigger("A", scope: ImageSearchScope.TargetWindow), new Callback((context, _) =>
        {
            Assert.Equal(moved.X + 50, context.Variables.Get<int>(ImageCondition.LastMatchXVariable));
            context.SignalControlFlow(ControlFlowSignal.Break);
            return Task.CompletedTask;
        })]);
        Assert.Equal(original, regions[0]);
        Assert.True(regions.Count >= 2);
        Assert.Equal(1, windows.ForegroundReads);
        Assert.All(windows.QueriedHandles, handle => Assert.Equal(FakeWindows.TargetHandle, handle));
    }

    [Fact]
    public async Task RepeatDiscardsWindowMovementDuringNativeVerification_BeforeAnyAction()
    {
        using var run = new Harness();
        var windows = new FakeWindows();
        run.Windows = windows;
        var moved = new Rectangle(-600, 100, 500, 400);
        int verifications = 0, bodies = 0;
        run.Matcher.Scan = (request, generation) => Result(request, generation, ImageScanStatus.Complete,
            new ImageScanHit(new Rectangle(request.Region!.Value.X + 20, request.Region.Value.Y + 20, 20, 20), .99));
        run.Matcher.Revalidate = (_, hit) =>
        {
            if (++verifications == 1) { windows.TargetBounds = moved; windows.Foreground = FakeWindows.OtherHandle; }
            return hit;
        };
        await run.Run([Trigger("A", scope: ImageSearchScope.TargetWindow), new Callback((context, _) =>
        {
            bodies++;
            Assert.True(verifications >= 2);
            Assert.Equal(moved.X + 30, context.Variables.Get<int>(ImageCondition.LastMatchXVariable));
            context.SignalControlFlow(ControlFlowSignal.Break);
            return Task.CompletedTask;
        })]);
        Assert.Equal(1, bodies);
        Assert.Equal(1, windows.ForegroundReads);
    }

    [Fact]
    public async Task RepeatDiscardsQuickPauseResumeDuringVerification_AndRequiresNewConfirmationStreak()
    {
        using var run = new Harness();
        int verifications = 0, scanAtInterruptedCheck = 0;
        run.Matcher.Revalidate = (_, hit) =>
        {
            if (++verifications == 1)
            {
                scanAtInterruptedCheck = run.Matcher.Scans;
                run.Control.PauseAsync().GetAwaiter().GetResult();
                run.Control.ResumeAsync().GetAwaiter().GetResult();
            }
            return hit;
        };
        await run.Run([Trigger("A", confirmations: 2), new Callback((context, _) =>
        {
            Assert.True(verifications >= 2);
            Assert.True(run.Matcher.Scans >= scanAtInterruptedCheck + 2);
            context.SignalControlFlow(ControlFlowSignal.Break);
            return Task.CompletedTask;
        })]);
    }

    [Fact]
    public async Task RepeatDiscardsQuickPauseResumeDuringScan_AndRequiresNewConfirmationStreak()
    {
        using var run = new Harness();
        run.Matcher.Scan = (request, generation) =>
        {
            if (generation == 2)
            {
                run.Control.PauseAsync().GetAwaiter().GetResult();
                run.Control.ResumeAsync().GetAwaiter().GetResult();
            }
            return Result(request, generation, ImageScanStatus.Complete, Hit(10));
        };
        await run.Run([Trigger("A", confirmations: 2), new Callback((context, _) =>
        {
            Assert.True(run.Matcher.Scans >= 4);
            context.SignalControlFlow(ControlFlowSignal.Break);
            return Task.CompletedTask;
        })]);
    }

    [Fact]
    public async Task TargetWindowRefreshesAfterPause_AndUsesTheOriginalHandle()
    {
        using var run = new Harness();
        var windows = new FakeWindows();
        run.Windows = windows;
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var moved = new Rectangle(200, -500, 800, 600);
        run.Matcher.Scan = (request, generation) =>
        {
            if (generation == 1)
            {
                run.Control.PauseAsync().GetAwaiter().GetResult();
                paused.TrySetResult();
                return Result(request, generation, ImageScanStatus.Complete);
            }
            Assert.Equal(moved, request.Region);
            return Result(request, generation, ImageScanStatus.Complete,
                new ImageScanHit(new Rectangle(moved.X + 40, moved.Y + 30, 20, 20), .99));
        };
        Task playback = run.Run([Trigger("A", scope: ImageSearchScope.TargetWindow), new Callback((context, _) =>
        { context.SignalControlFlow(ControlFlowSignal.Break); return Task.CompletedTask; })]);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(60);
        windows.TargetBounds = moved;
        windows.Foreground = FakeWindows.OtherHandle;
        await run.Control.ResumeAsync();
        await playback;
        Assert.Equal(1, windows.ForegroundReads);
        Assert.All(windows.QueriedHandles, handle => Assert.Equal(FakeWindows.TargetHandle, handle));
    }

    [Fact]
    public async Task ClosingTargetWindowFailsClearly_WithoutFallingBackToDesktopOrNewForeground()
    {
        using var run = new Harness();
        var windows = new FakeWindows();
        run.Windows = windows;
        int bodies = 0;
        run.Matcher.Scan = (request, generation) =>
        {
            Assert.NotNull(request.Region);
            windows.TargetValid = false;
            windows.Foreground = FakeWindows.OtherHandle;
            return Result(request, generation, ImageScanStatus.Complete);
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run.Run([
            Trigger("A", scope: ImageSearchScope.TargetWindow), new Callback((_, _) =>
            { bodies++; return Task.CompletedTask; })]));
        Assert.Contains("target window was closed", error.Message);
        Assert.Equal(1, run.Matcher.Scans);
        Assert.Equal(0, bodies);
        Assert.True(run.Matcher.Disposed);
        Assert.Equal(1, windows.ForegroundReads);
    }

    [Fact]
    public async Task ScanFailureInterruptsBody_AndSurfacesTheActualError()
    {
        using var run = new Harness();
        int body = 0;
        run.Matcher.Scan = (request, generation) => Volatile.Read(ref body) == 1
            ? Result(request, generation, ImageScanStatus.Failed) with { ErrorCode = "CaptureLost", ErrorMessage = "capture lost" }
            : Result(request, generation, ImageScanStatus.Complete, Hit(10));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run.Run([Trigger("A"), new Callback(async (_, ct) =>
        { Volatile.Write(ref body, 1); await Task.Delay(Timeout.Infinite, ct); })]));
        Assert.Contains("capture lost", error.Message);
        Assert.True(run.Matcher.Disposed);
    }

    [Fact]
    public async Task StaleHitMustPassFreshRevalidationBeforeSendingActions()
    {
        using var run = new Harness();
        int attempts = 0, executed = 0;
        run.Matcher.Revalidate = (_, hit) => ++attempts == 1 ? null : hit;
        await run.Run([Trigger("A"), new Callback((context, _) =>
        { executed++; context.SignalControlFlow(ControlFlowSignal.Break); return Task.CompletedTask; })]);
        Assert.Equal(2, attempts);
        Assert.Equal(1, executed);
    }

    [Fact]
    public async Task ConfiguredConditionRejectsCoarseCandidatesThatFailNativeRevalidation()
    {
        using var run = new Harness();
        run.Matcher.Scan = (request, generation) => Result(request, generation, ImageScanStatus.Complete, Hit(10), Hit(50));
        int attempts = 0;
        run.Matcher.Revalidate = (_, _) => { attempts++; return null; };
        var (evaluation, _) = await run.Evaluate(new ImageCondition("target", searchRegion: new Rectangle(0, 0, 200, 100)) { ScanOptions = ImageScanOptions.ForPlacedArea() });
        Assert.Equal(ConditionOutcome.Unsatisfied, evaluation.Outcome);
        Assert.Equal(2, attempts);
        Assert.True(run.Matcher.Disposed);
    }

    [Fact]
    public async Task ConfiguredConditionContinuesPartialCoverage_AndStoresOnlyVerifiedCoordinates()
    {
        using var run = new Harness();
        run.Matcher.Scan = (request, generation) => Result(request, generation,
            generation == 1 ? ImageScanStatus.Partial : ImageScanStatus.Complete, Hit(generation == 1 ? 10 : 50));
        run.Matcher.Revalidate = (_, hit) => hit.Bounds.X == 10 ? null : Hit(55);
        var (evaluation, context) = await run.Evaluate(new ImageCondition("target") { ScanOptions = new ImageScanOptions() });
        Assert.Equal(ConditionOutcome.Satisfied, evaluation.Outcome);
        Assert.Equal(65, context.Variables.Get<int>(ImageCondition.LastMatchXVariable));
        Assert.Equal(2, run.Matcher.Scans);
    }

    [Fact]
    public async Task ConfiguredConditionKeepsNativeVerificationFailureDistinctFromAbsence()
    {
        using var run = new Harness();
        run.Matcher.Revalidate = (_, _) => throw new InvalidOperationException("native capture lost");
        var (evaluation, _) = await run.Evaluate(new ImageCondition("target") { ScanOptions = new ImageScanOptions() });
        Assert.Equal(ConditionOutcome.Failed, evaluation.Outcome);
        Assert.Equal("native capture lost", evaluation.Exception?.Message);
        Assert.True(run.Matcher.Disposed);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(500)]
    public async Task FindOnceCarriesRequestedCadenceThroughPartialSweeps_AndReusesSessionForConfirmations(int interval)
    {
        using var run = new Harness();
        run.Matcher.Scan = (request, turn) =>
        {
            Assert.Equal(interval, request.PollIntervalMs);
            return turn == 1 ? Result(request, 1, ImageScanStatus.Partial)
                : Result(request, 1, ImageScanStatus.Partial, Hit(10));
        };
        var (evaluation, _) = await run.Wait(new ImageCondition("target") { ScanOptions = new ImageScanOptions() },
            FindPolicy(interval: interval, confirmations: 2));
        Assert.True(evaluation.IsSatisfied);
        Assert.Equal(2, run.Matcher.Scans);
        Assert.Equal(1, run.Matcher.CreatedSessions);
        Assert.True(run.Matcher.Disposed);
    }

    [Theory]
    [InlineData(ConditionWaitGoal.UntilSatisfied, ConditionOutcome.Unsatisfied)]
    [InlineData(ConditionWaitGoal.UntilEitherStable, ConditionOutcome.Failed)]
    public async Task FindOnceTimeoutInterruptsIncompleteSweep_WithoutClaimingAbsenceForBranches(
        ConditionWaitGoal goal, ConditionOutcome expected)
    {
        using var run = new Harness();
        run.Matcher.Scan = (request, turn) => Result(request, 1,
            turn % 2 == 0 ? ImageScanStatus.Deferred : ImageScanStatus.Partial);
        var timer = Stopwatch.StartNew();
        var (evaluation, _) = await run.Wait(new ImageCondition("target") { ScanOptions = new ImageScanOptions() },
            FindPolicy() with { Timeout = TimeSpan.FromMilliseconds(80), Goal = goal });
        Assert.Equal(expected, evaluation.Outcome);
        if (goal == ConditionWaitGoal.UntilEitherStable) Assert.Equal("ConditionIndeterminate", evaluation.ErrorCode);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1));
        Assert.True(run.Matcher.Scans > 0);
        Assert.True(run.Matcher.Disposed);
    }

    [Fact]
    public async Task FindOnceDiscardsPausedVerification_AndRestartsConfirmationStreakAfterResume()
    {
        using var run = new Harness();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0, resumed = 0;
        run.Matcher.Scan = (request, turn) => Result(request, turn, ImageScanStatus.Partial,
            Hit(Volatile.Read(ref resumed) == 0 ? 10 : 50));
        run.Matcher.Revalidate = (_, hit) =>
        {
            if (Interlocked.Increment(ref attempts) == 2)
            {
                run.Control.PauseAsync().GetAwaiter().GetResult();
                paused.TrySetResult();
            }
            return hit;
        };
        var pending = run.Wait(new ImageCondition("target") { ScanOptions = new ImageScanOptions() },
            FindPolicy(confirmations: 2));
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(60);
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, run.Matcher.Scans);
        Volatile.Write(ref resumed, 1);
        await run.Control.ResumeAsync();
        var (evaluation, context) = await pending;
        Assert.True(evaluation.IsSatisfied);
        Assert.Equal(4, attempts);
        Assert.Equal(60, context.Variables.Get<int>(ImageCondition.LastMatchXVariable));
        Assert.Equal(1, run.Matcher.CreatedSessions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FindOnceStopAndPanicCancelPausedPartialSweep(bool panic)
    {
        using var run = new Harness();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Matcher.Scan = (request, turn) =>
        {
            run.Control.PauseAsync().GetAwaiter().GetResult();
            paused.TrySetResult();
            return Result(request, turn, ImageScanStatus.Partial);
        };
        var pending = run.Wait(new ImageCondition("target") { ScanOptions = new ImageScanOptions() }, FindPolicy());
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(1));
        if (panic) run.Control.Panic();
        else await run.Control.StopAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(run.Matcher.Disposed);
        Assert.Equal(0, run.Matcher.ConcurrentNativeCalls);
    }

    [Fact]
    public async Task FindOnceFollowsOriginalWindowDuringSweepAndVerification_WithoutRetargetingFocus()
    {
        using var run = new Harness();
        var windows = new FakeWindows();
        run.Windows = windows;
        var moved = new Rectangle(-500, 150, 640, 480);
        var movedAgain = new Rectangle(100, -500, 900, 700);
        int attempts = 0;
        run.Matcher.Scan = (request, turn) =>
        {
            if (turn == 1)
            {
                windows.TargetBounds = moved;
                windows.Foreground = FakeWindows.OtherHandle;
                return Result(request, turn, ImageScanStatus.Partial);
            }
            Assert.Equal(attempts == 0 ? moved : movedAgain, request.Region);
            Rectangle region = request.Region!.Value;
            return Result(request, turn, ImageScanStatus.Partial,
                new ImageScanHit(new Rectangle(region.X + 20, region.Y + 20, 20, 20), .99));
        };
        run.Matcher.Revalidate = (_, hit) =>
        {
            if (++attempts == 1) windows.TargetBounds = movedAgain;
            return hit;
        };
        var (evaluation, context) = await run.Wait(new ImageCondition("target")
        { ScanOptions = new ImageScanOptions { SearchScope = ImageSearchScope.TargetWindow } }, FindPolicy(confirmations: 2));
        Assert.True(evaluation.IsSatisfied);
        Assert.Equal(movedAgain.X + 30, context.Variables.Get<int>(ImageCondition.LastMatchXVariable));
        Assert.Equal(3, attempts);
        Assert.Equal(1, windows.ForegroundReads);
        Assert.All(windows.QueriedHandles, handle => Assert.Equal(FakeWindows.TargetHandle, handle));
        Assert.True(run.Matcher.Disposed);
    }

    [Fact]
    public async Task FindOnceFailsIfAnchoredWindowClosesDuringSweep()
    {
        using var run = new Harness();
        var windows = new FakeWindows();
        run.Windows = windows;
        run.Matcher.Scan = (request, turn) =>
        {
            windows.TargetValid = false;
            windows.Foreground = FakeWindows.OtherHandle;
            return Result(request, turn, ImageScanStatus.Partial);
        };
        var (evaluation, _) = await run.Wait(new ImageCondition("target")
        { ScanOptions = new ImageScanOptions { SearchScope = ImageSearchScope.TargetWindow } }, FindPolicy());
        Assert.True(evaluation.IsFailed);
        Assert.Contains("target window was closed", evaluation.Exception?.Message);
        Assert.Equal(1, run.Matcher.Scans);
        Assert.Equal(1, windows.ForegroundReads);
        Assert.True(run.Matcher.Disposed);
    }

    [Fact]
    public async Task FindOnceConfirmsKnownLocationWithoutAnotherSweep_UsingFreshNativeCoordinatesEachTime()
    {
        using var run = new Harness();
        int validations = 0;
        run.Matcher.Scan = (request, turn) => turn == 1
            ? Result(request, 1, ImageScanStatus.Partial, Hit(10))
            : Result(request, 1, ImageScanStatus.Deferred);
        run.Matcher.Revalidate = (_, _) => Hit(10 + Interlocked.Increment(ref validations));
        var (evaluation, context) = await run.Wait(new ImageCondition("target") { ScanOptions = new ImageScanOptions() },
            FindPolicy(confirmations: 3));
        Assert.True(evaluation.IsSatisfied);
        Assert.Equal(1, run.Matcher.Scans);
        Assert.Equal(3, validations);
        Assert.Equal(23, context.Variables.Get<int>(ImageCondition.LastMatchXVariable));
        Assert.True(run.Matcher.Disposed);
    }

    [Fact]
    public async Task FindOnceDropsKnownLocationAfterFreshMiss_AndSearchesForAnotherVisibleTarget()
    {
        using var run = new Harness();
        var validatedLocations = new List<int>();
        run.Matcher.Scan = (request, turn) => turn switch
        {
            1 => Result(request, 1, ImageScanStatus.Partial, Hit(10)),
            2 => Result(request, 1, ImageScanStatus.Partial),
            _ => Result(request, 1, ImageScanStatus.Partial, Hit(50))
        };
        run.Matcher.Revalidate = (_, hit) =>
        {
            validatedLocations.Add(hit.Bounds.X);
            return validatedLocations.Count == 2 ? null : hit;
        };
        var (evaluation, context) = await run.Wait(new ImageCondition("target") { ScanOptions = new ImageScanOptions() },
            FindPolicy(confirmations: 2));
        Assert.True(evaluation.IsSatisfied);
        Assert.Equal(new[] { 10, 10, 50 }, validatedLocations);
        Assert.Equal(3, run.Matcher.Scans);
        Assert.Equal(60, context.Variables.Get<int>(ImageCondition.LastMatchXVariable));
    }

    [Fact]
    public async Task FindOnceStabilityUsesFreshChecksOfKnownLocation_WhileRestOfSweepIsDeferred()
    {
        using var run = new Harness();
        int validations = 0;
        run.Matcher.Scan = (request, turn) => turn == 1
            ? Result(request, 1, ImageScanStatus.Partial, Hit(10))
            : Result(request, 1, ImageScanStatus.Deferred);
        run.Matcher.Revalidate = (_, hit) => { Interlocked.Increment(ref validations); return hit; };
        var timer = Stopwatch.StartNew();
        var (evaluation, _) = await run.Wait(new ImageCondition("target") { ScanOptions = new ImageScanOptions() },
            FindPolicy() with { StabilityDurationMs = 50 });
        Assert.True(evaluation.IsSatisfied);
        Assert.Equal(1, run.Matcher.Scans);
        Assert.True(validations >= 2);
        Assert.True(timer.ElapsedMilliseconds >= 45);
    }

    [Fact]
    public void TenMillisecondCadenceAndExplicitOptionsSurviveSaveAndFactoryEdit_LegacyStaysLegacy()
    {
        var options = new ImageScanOptions { SearchScope = ImageSearchScope.Anywhere, Resolution = ImageScanResolution.P720,
            MatchHandling = ImageMatchHandling.All, MonitorBounds = new Rectangle(-1920, 0, 1920, 1080) };
        var wait = new WaitUntilStep(new ImageCondition("target") { ScanOptions = options })
        { PollInterval = TimeSpan.FromMilliseconds(10), RepeatWhileVisible = true, RepeatDelayMs = 750 };
        var updated = MacroActionFactory.UpdateImageWait(wait, 10000, 10, 1);
        var serializer = new MacroJsonSerializer();
        var saved = serializer.Deserialize(serializer.Serialize(new MacroModel { Steps = [updated, new WaitUntilStep(new ImageCondition("old"))] }));
        var restored = Assert.IsType<WaitUntilStep>(saved.Steps[0]);
        Assert.Equal(10, restored.PollInterval.TotalMilliseconds);
        Assert.Equal(750, restored.RepeatDelayMs);
        Assert.Equal(options, Assert.IsType<ImageCondition>(restored.Condition).ScanOptions);
        Assert.Null(Assert.IsType<ImageCondition>(Assert.IsType<WaitUntilStep>(saved.Steps[1]).Condition).ScanOptions);
    }

    private static WaitUntilStep Trigger(string path, int delay = 50, int confirmations = 1, int stability = 0,
        ImageSearchScope scope = ImageSearchScope.Anywhere) => new(new ImageCondition(path)
    { ScanOptions = new ImageScanOptions { SearchScope = scope, MatchHandling = ImageMatchHandling.All } })
    { RepeatWhileVisible = true, PollInterval = TimeSpan.FromMilliseconds(10), RepeatDelayMs = delay,
        RequiredConfirmations = confirmations, StabilityDurationMs = stability };
    private static ImageScanHit Hit(int x) => new(new Rectangle(x, 10, 20, 20), .99);
    private static ConditionWaitPolicy FindPolicy(int interval = 10, int confirmations = 1) => new()
    {
        Timeout = TimeSpan.FromSeconds(2), PollInterval = TimeSpan.FromMilliseconds(interval),
        RequiredTrueConfirmations = confirmations, RequiredFalseConfirmations = 1,
        Phase = "WaitUntil", Goal = ConditionWaitGoal.UntilSatisfied
    };
    private static ImageScanResult Result(ImageScanRequest request, long generation, ImageScanStatus status, params ImageScanHit[] hits)
        => new(request.Id, status, hits, generation, Stopwatch.GetTimestamp(), new Size(200, 100), new Rectangle(0, 0, 200, 100));
    private sealed class Callback(Func<MacroContext, CancellationToken, Task> action) : MacroStep
    { public override Task ExecuteAsync(MacroContext context, CancellationToken ct = default) => action(context, ct); }
    private sealed class Harness : IDisposable
    {
        public FakeMatcher Matcher { get; } = new();
        private readonly FakeCapture _capture = new();
        private readonly MacroController _control = new();
        public MacroController Control => _control;
        public IWindowCoordinateService? Windows { get; set; }
        public async Task Run(IReadOnlyList<MacroStep> steps)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var context = new MacroContext(_capture, _control) { ImageMatcher = Matcher, EnforceRuntimeCap = false, WindowCoordinates = Windows };
            await RepeatingImageDispatcher.ExecuteAsync(steps, 0, context, timeout.Token, enforceIterationLimit: false);
        }
        public async Task<(ConditionEvaluation, MacroContext)> Evaluate(ImageCondition condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var context = new MacroContext(_capture, _control) { ImageMatcher = Matcher, EnforceRuntimeCap = false, WindowCoordinates = Windows };
            return (await condition.EvaluateDetailedAsync(context, timeout.Token), context);
        }
        public async Task<(ConditionEvaluation, MacroContext)> Wait(ImageCondition condition, ConditionWaitPolicy policy)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var context = new MacroContext(_capture, _control) { ImageMatcher = Matcher, EnforceRuntimeCap = false, WindowCoordinates = Windows };
            return (await ConditionWaitEvaluator.WaitDetailedAsync(context, condition, policy, timeout.Token), context);
        }
        public void Dispose() { _capture.Dispose(); _control.Dispose(); }
    }
    private sealed class FakeCapture : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;
        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
    private sealed class FakeWindows : IWindowCoordinateService
    {
        public static readonly IntPtr TargetHandle = new(123);
        public static readonly IntPtr OtherHandle = new(456);
        public IntPtr Foreground = TargetHandle;
        public Rectangle TargetBounds = new(10, 20, 300, 200);
        public bool TargetValid = true;
        public int ForegroundReads;
        public List<IntPtr> QueriedHandles { get; } = new();
        public IntPtr GetForegroundWindow() { ForegroundReads++; return Foreground; }
        public bool IsWindowValid(IntPtr handle)
        { QueriedHandles.Add(handle); return handle == TargetHandle ? TargetValid : handle == OtherHandle; }
        public (int width, int height) GetClientSize(IntPtr handle)
        { QueriedHandles.Add(handle); return handle == TargetHandle ? (TargetBounds.Width, TargetBounds.Height) : (100, 100); }
        public (int screenX, int screenY) ClientToScreen(IntPtr handle, int x, int y)
        { QueriedHandles.Add(handle); return handle == TargetHandle ? (TargetBounds.X + x, TargetBounds.Y + y) : (1000 + x, 1000 + y); }
        public (int clientX, int clientY) ScreenToClient(IntPtr handle, int x, int y) => throw new NotSupportedException();
        public IntPtr GetWindowAt(int x, int y) => throw new NotSupportedException();
        public IntPtr FindWindowByTitle(string? titleOrProcessName) => throw new NotSupportedException();
    }
    private sealed class FakeMatcher : IImageMatcher, IImageScanProvider, IImageScanSession
    {
        public Func<ImageScanRequest, int, ImageScanResult> Scan { get; set; } = (request, generation) => Result(request, generation, ImageScanStatus.Complete, Hit(10));
        public Func<ImageScanRequest, ImageScanHit, ImageScanHit?> Revalidate { get; set; } = (_, hit) => hit;
        public int Scans;
        public int CreatedSessions;
        public int ConcurrentNativeCalls;
        public bool Disposed;
        public ImageScanPerformanceSnapshot Performance => new(10, 1, 1, false, 3) { RequestedIntervalMs = 10 };
        public IImageScanSession CreateScanSession(IScreenCaptureService capture) { CreatedSessions++; return this; }
        public async Task<IReadOnlyList<ImageScanResult>> ScanAsync(IReadOnlyList<ImageScanRequest> requests, CancellationToken ct)
        {
            Assert.Equal(1, Interlocked.Increment(ref ConcurrentNativeCalls));
            try
            {
                Assert.False(Disposed);
                await Task.Yield(); ct.ThrowIfCancellationRequested();
                int turn = Interlocked.Increment(ref Scans);
                return requests.Select(request => Scan(request, turn)).ToArray();
            }
            finally { Interlocked.Decrement(ref ConcurrentNativeCalls); }
        }
        public Task<ImageScanHit?> RevalidateAsync(ImageScanRequest request, ImageScanHit hit, CancellationToken ct)
        {
            Assert.Equal(1, Interlocked.Increment(ref ConcurrentNativeCalls));
            try { ct.ThrowIfCancellationRequested(); return Task.FromResult(Revalidate(request, hit)); }
            finally { Interlocked.Decrement(ref ConcurrentNativeCalls); }
        }
        public Task<MatchResult> MatchAsync(CaptureFrame frame, string path, double minConfidence = .8, Rectangle? searchRegion = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MatchResult> MatchAsync(CaptureFrame frame, byte[] bytes, double minConfidence = .8, Rectangle? searchRegion = null, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { Assert.Equal(0, ConcurrentNativeCalls); Disposed = true; }
    }
}
