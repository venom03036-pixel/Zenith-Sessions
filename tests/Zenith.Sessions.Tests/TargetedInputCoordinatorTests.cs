using System.Collections.Concurrent;
using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// Controlled transport only: no HWND lookup, Windows messages, physical input, capture or account data.
public sealed class TargetedInputCoordinatorTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TargetedInputTarget Target(int id = 1) => new(Guid.NewGuid(), 500 + id, 10000 + id,
        (nint)(100 + id), new PixelRectangle(-200, 40, 640, 480), 1);
    private static TargetedInputSignal Down(int key) => new(TargetedInputKind.KeyDown, key);
    private static TargetedInputSignal Up(int key) => new(TargetedInputKind.KeyUp, key);
    private static TargetedInputRun Open(Fixture fixture, TargetedInputTarget? target = null)
    {
        var result = fixture.Owner.TryOpen(target ?? Target(), TargetedInputPurpose.OwnedWindowDiagnostic);
        Assert.NotNull(result.Run);
        return result.Run!;
    }

    [Fact]
    public async Task QueuedMixedBatchKeepsOrderSequenceAndOnlyUnfinishedRunOwnedHolds()
    {
        await using var fixture = new Fixture(); var run = Open(fixture);
        var signals = new[] { Down(16), Down(65), Down(65), Up(65),
            new TargetedInputSignal(TargetedInputKind.Move, X: 30, Y: 40),
            new TargetedInputSignal(TargetedInputKind.LeftDown, X: 30, Y: 40),
            new TargetedInputSignal(TargetedInputKind.LeftUp, X: 30, Y: 40), Up(16) };
        var result = await run.SubmitAsync(signals).WaitAsync(Deadline);
        Assert.Equal(TargetedInputDispatchStatus.Submitted, result.Status);
        Assert.Equal(signals.Length, result.SubmittedCount); Assert.Equal(0, result.UncertainCount);
        Assert.Equal(0, result.Run.LogicalHolds); Assert.Equal(0, result.Run.SubmittedHolds);
        var commands = fixture.Transport.Accepted.ToArray();
        Assert.Equal(signals, commands.Select(command => command.Signal));
        Assert.Equal(Enumerable.Range(1, signals.Length).Select(value => (long)value), commands.Select(command => command.Sequence));
        Assert.All(commands, command => Assert.Equal(result.Run.RunId, command.RunId));
        Assert.False(commands[1].RepeatedDown); Assert.True(commands[2].RepeatedDown);
        Assert.Equal(TargetedInputModifiers.Shift, commands[1].Modifiers);
        Assert.Equal(TargetedInputModifiers.Shift | TargetedInputModifiers.LeftButton, commands[5].Modifiers);
        Assert.Equal(TargetedInputModifiers.Shift, commands[6].Modifiers);
        await run.StopAsync().WaitAsync(Deadline); Assert.Equal(commands.Length, fixture.Transport.Attempts.Count);
    }

    [Fact]
    public async Task PauseReleasesInReverseOrderAndExplicitResumeRestoresOnlyUnfinishedHoldsInOriginalOrder()
    {
        await using var fixture = new Fixture(); var run = Open(fixture);
        await run.SubmitAsync(new[] { Down(65), Up(65), Down(16), Down(66) }).WaitAsync(Deadline);
        var paused = await run.PauseAsync().WaitAsync(Deadline);
        Assert.Equal(TargetedInputRunState.Paused, paused.State);
        Assert.Equal(2, paused.LogicalHolds); Assert.Equal(0, paused.SubmittedHolds);
        Assert.Equal(new[] { Up(66), Up(16) }, fixture.Transport.Attempts.Where(command => command.Cleanup).Select(command => command.Signal));
        int calls = fixture.Transport.Attempts.Count;
        Assert.Equal(TargetedInputDispatchStatus.Inactive, (await run.SubmitAsync(new[] { Down(67) })).Status);
        Assert.Equal(calls, fixture.Transport.Attempts.Count);
        var resumed = await run.ResumeAsync().WaitAsync(Deadline);
        Assert.Equal(TargetedInputRunState.Running, resumed.State); Assert.Equal(2, resumed.SubmittedHolds);
        Assert.Equal(new[] { Down(16), Down(66) }, fixture.Transport.Attempts.Skip(calls).Select(command => command.Signal));
        Assert.Equal(1, fixture.Transport.Attempts.Count(command => command.Signal == Down(65)));
        await run.SubmitAsync(new[] { Up(66), Up(16) }).WaitAsync(Deadline);
        await run.StopAsync().WaitAsync(Deadline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PausedAndFaultedTargetsCannotBeStolenEvenWithANewRegistration(bool fault)
    {
        await using var fixture = new Fixture(); var target = Target(); var run = Open(fixture, target);
        if (fault)
        {
            fixture.Transport.Validation = (_, _, _) => ValueTask.FromResult(TargetedInputValidation.Unavailable);
            Assert.Equal(TargetedInputDispatchStatus.Faulted, (await run.SubmitAsync(new[] { Down(65) }).WaitAsync(Deadline)).Status);
            Assert.Equal(TargetedInputRunState.Faulted, run.Snapshot.State);
        }
        else await run.PauseAsync().WaitAsync(Deadline);
        Assert.Null(fixture.Owner.TryOpen(target, TargetedInputPurpose.MacroPlayback).Run);
        Assert.Null(fixture.Owner.TryOpen(target with { RegistrationId = Guid.NewGuid(), GeometryGeneration = 2 }, TargetedInputPurpose.BroadcastFollower).Run);
        fixture.Transport.Validation = null;
        Assert.Equal(TargetedInputRunState.Stopped, (await run.StopAsync().WaitAsync(Deadline)).State);
        Assert.NotNull(fixture.Owner.TryOpen(target with { RegistrationId = Guid.NewGuid() }, TargetedInputPurpose.OwnedWindowDiagnostic).Run);
    }

    [Fact]
    public async Task ActiveRunBudgetIsBoundedAndStoppingOneRunFreesExactlyItsSlot()
    {
        await using var fixture = new Fixture(new TargetedInputLimits(MaximumRuns: 2));
        var first = Open(fixture, Target(1)); var second = Open(fixture, Target(2));
        Assert.Null(fixture.Owner.TryOpen(Target(3), TargetedInputPurpose.OwnedWindowDiagnostic).Run);
        await first.StopAsync().WaitAsync(Deadline);
        Assert.NotNull(fixture.Owner.TryOpen(Target(3), TargetedInputPurpose.OwnedWindowDiagnostic).Run);
        Assert.Equal(TargetedInputRunState.Running, second.Snapshot.State);
        Assert.Empty(fixture.Transport.Attempts);
    }

    [Fact]
    public async Task InvalidTargetsPurposesAndOperationalLimitsNeverReachTransport()
    {
        await using var fixture = new Fixture(); var good = Target();
        var invalid = new[] { good with { RegistrationId = Guid.Empty }, good with { ProcessId = 0 },
            good with { ProcessStartUtcTicks = 0 }, good with { ProcessStartUtcTicks = long.MaxValue },
            good with { Window = 0 }, good with { Window = (nint)0xffff }, good with { GeometryGeneration = 0 },
            good with { ContentBounds = new(0, 0, 0, 480) }, good with { ContentBounds = new(int.MaxValue, 0, 2, 1) } };
        foreach (var target in invalid) Assert.Null(fixture.Owner.TryOpen(target, TargetedInputPurpose.OwnedWindowDiagnostic).Run);
        Assert.Null(fixture.Owner.TryOpen(good, (TargetedInputPurpose)99).Run);
        Assert.Empty(fixture.Transport.Validations); Assert.Empty(fixture.Transport.Attempts);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TargetedInputLimits(MaximumRuns: 0).Validated());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TargetedInputLimits(MaximumBatch: 1025).Validated());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TargetedInputLimits(MaximumHolds: 259).Validated());
    }

    [Fact]
    public async Task InvalidSignalAnywhereRejectsWholeBatchWithoutSendingAValidPrefix()
    {
        await using var fixture = new Fixture(); var run = Open(fixture);
        var invalid = new[] { new TargetedInputSignal((TargetedInputKind)99), Down(0), Down(255),
            new TargetedInputSignal(TargetedInputKind.KeyDown, 65, X: 1),
            new TargetedInputSignal(TargetedInputKind.Move, X: -1), new TargetedInputSignal(TargetedInputKind.Move, X: 640),
            new TargetedInputSignal(TargetedInputKind.Move, Y: 480), new TargetedInputSignal(TargetedInputKind.LeftDown, Key: 65),
            new TargetedInputSignal(TargetedInputKind.Wheel, WheelDelta: 0), new TargetedInputSignal(TargetedInputKind.Wheel, WheelDelta: 12001),
            new TargetedInputSignal(TargetedInputKind.Move, WheelDelta: 120) };
        foreach (var signal in invalid)
            Assert.Equal(TargetedInputDispatchStatus.Invalid, (await run.SubmitAsync(new[] { Down(65), signal })).Status);
        Assert.Equal(TargetedInputDispatchStatus.Invalid, (await run.SubmitAsync(Array.Empty<TargetedInputSignal>())).Status);
        Assert.Equal(TargetedInputDispatchStatus.Invalid, (await run.SubmitAsync(Enumerable.Repeat(Down(65), 257).ToArray())).Status);
        Assert.Empty(fixture.Transport.Validations); Assert.Empty(fixture.Transport.Attempts);
        Assert.Equal(0, run.Snapshot.LogicalHolds); Assert.Equal(0, run.Snapshot.SubmittedHolds);
    }

    [Fact]
    public async Task ClientCoordinatesUseContentDimensionsRatherThanScreenOrigin()
    {
        await using var fixture = new Fixture(); var run = Open(fixture);
        var edge = new TargetedInputSignal(TargetedInputKind.Move, X: 639, Y: 479);
        Assert.Equal(TargetedInputDispatchStatus.Submitted, (await run.SubmitAsync(new[] { edge })).Status);
        Assert.Equal(edge, Assert.Single(fixture.Transport.Accepted).Signal);
    }

    [Fact]
    public async Task OutstandingBatchIsBusyAndAllRunsShareOneActualTransportLane()
    {
        await using var fixture = new Fixture(); var gate = fixture.NewGate(); var first = Open(fixture, Target(1)); var second = Open(fixture, Target(2));
        fixture.Transport.Submission = async (command, admission, ct) =>
        { if (command.RunId == first.Snapshot.RunId && !command.Cleanup) await gate.HoldAsync(); return fixture.Transport.Accept(command, admission, ct); };
        var active = first.SubmitAsync(new[] { Down(65) }); await gate.EnteredAsync();
        Assert.Equal(TargetedInputDispatchStatus.Busy, (await first.SubmitAsync(new[] { Down(66) })).Status);
        var queued = second.SubmitAsync(new[] { Down(67) });
        Assert.False(active.IsCompleted); Assert.False(queued.IsCompleted);
        Assert.Single(fixture.Transport.Attempts); Assert.Single(fixture.Transport.Validations);
        gate.Open(); await Task.WhenAll(active, queued).WaitAsync(Deadline);
        Assert.Equal(1, fixture.Transport.MaximumConcurrentCalls);
        Assert.Equal(new[] { Down(65), Down(67) }, fixture.Transport.Accepted.Select(command => command.Signal));
    }

    [Fact]
    public async Task CallerBatchIsFrozenBeforeWaitingForTheGlobalLane()
    {
        await using var fixture = new Fixture(); var gate = fixture.NewGate(); var first = Open(fixture, Target(1)); var second = Open(fixture, Target(2));
        fixture.Transport.Submission = async (command, admission, ct) =>
        { if (command.RunId == first.Snapshot.RunId && !command.Cleanup) await gate.HoldAsync(); return fixture.Transport.Accept(command, admission, ct); };
        var occupying = first.SubmitAsync(new[] { Down(65) }); await gate.EnteredAsync();
        var callerOwned = new List<TargetedInputSignal> { Down(66), Up(66) }; var queued = second.SubmitAsync(callerOwned);
        callerOwned[0] = Down(90); callerOwned.Clear(); gate.Open();
        await Task.WhenAll(occupying, queued).WaitAsync(Deadline);
        Assert.Equal(new[] { Down(66), Up(66) }, fixture.Transport.Accepted.Where(command => command.RunId == second.Snapshot.RunId).Select(command => command.Signal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrPanicFencesABatchWaitingBehindAnotherRun(bool panic)
    {
        await using var fixture = new Fixture(); var gate = fixture.NewGate(); var first = Open(fixture, Target(1)); var queuedRun = Open(fixture, Target(2));
        fixture.Transport.Submission = async (command, admission, ct) =>
        { if (command.RunId == first.Snapshot.RunId && !command.Cleanup) await gate.HoldAsync(); return fixture.Transport.Accept(command, admission, ct); };
        var occupying = first.SubmitAsync(new[] { Down(65) }); await gate.EnteredAsync();
        long generation = queuedRun.Snapshot.Generation; var queued = queuedRun.SubmitAsync(new[] { Down(66) });
        if (panic) fixture.Owner.SignalStopAll();
        var stop = queuedRun.StopAsync(); Assert.True(queuedRun.Snapshot.Generation > generation);
        gate.Open(); await Task.WhenAll(occupying, queued).WaitAsync(Deadline); await stop.WaitAsync(Deadline);
        Assert.DoesNotContain(fixture.Transport.Attempts, command => command.RunId == queuedRun.Snapshot.RunId);
        Assert.Equal(TargetedInputRunState.Stopped, queuedRun.Snapshot.State);
        Assert.Equal(0, (await queued).SubmittedCount);
    }

    [Fact]
    public async Task CallerCancellationWhileQueuedStopsWithoutAnyTransportCallForThatRun()
    {
        await using var fixture = new Fixture(); var gate = fixture.NewGate(); using var stop = new CancellationTokenSource();
        var first = Open(fixture, Target(1)); var second = Open(fixture, Target(2));
        fixture.Transport.Submission = async (command, admission, ct) =>
        { if (command.RunId == first.Snapshot.RunId && !command.Cleanup) await gate.HoldAsync(); return fixture.Transport.Accept(command, admission, ct); };
        var occupying = first.SubmitAsync(new[] { Down(65) }); await gate.EnteredAsync();
        var queued = second.SubmitAsync(new[] { Down(66) }, stop.Token); stop.Cancel();
        Assert.False(queued.IsCompleted); gate.Open();
        await Task.WhenAll(occupying, queued).WaitAsync(Deadline);
        Assert.Equal(TargetedInputDispatchStatus.Cancelled, (await queued).Status);
        Assert.Equal(TargetedInputRunState.Stopped, second.Snapshot.State);
        Assert.DoesNotContain(fixture.Transport.Attempts, command => command.RunId == second.Snapshot.RunId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrPanicDuringValidationPreventsTheNextSubmission(bool panic)
    {
        await using var fixture = new Fixture(); var gate = fixture.NewGate(); var run = Open(fixture);
        fixture.Transport.Validation = async (_, cleanup, _) => { if (!cleanup) await gate.HoldAsync(); return TargetedInputValidation.Current; };
        var pending = run.SubmitAsync(new[] { Down(65), Down(66) }); await gate.EnteredAsync();
        if (panic) fixture.Owner.SignalStopAll(); var stopped = run.StopAsync();
        Assert.False(stopped.IsCompleted); gate.Open();
        var result = await pending.WaitAsync(Deadline); await stopped.WaitAsync(Deadline);
        Assert.Equal(0, result.SubmittedCount); Assert.Empty(fixture.Transport.Attempts);
        Assert.Equal(TargetedInputRunState.Stopped, run.Snapshot.State);
    }

    [Fact]
    public async Task CancellationDuringValidationPreventsSubmissionAndJoinsTheActualValidation()
    {
        await using var fixture = new Fixture(); var gate = fixture.NewGate(); using var stop = new CancellationTokenSource(); var run = Open(fixture);
        fixture.Transport.Validation = async (_, _, _) => { await gate.HoldAsync(); return TargetedInputValidation.Current; };
        var pending = run.SubmitAsync(new[] { Down(65) }, stop.Token); await gate.EnteredAsync(); stop.Cancel();
        Assert.False(pending.IsCompleted); gate.Open(); var result = await pending.WaitAsync(Deadline);
        Assert.Equal(TargetedInputDispatchStatus.Cancelled, result.Status); Assert.Empty(fixture.Transport.Attempts);
        Assert.Equal(TargetedInputRunState.Stopped, run.Snapshot.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrPanicInsideSubmissionClosesItsFinalAdmissionCallback(bool panic)
    {
        await using var fixture = new Fixture(); var gate = fixture.NewGate(); var run = Open(fixture);
        bool? admissionAfterStop = null;
        fixture.Transport.Submission = async (command, admission, ct) =>
        {
            if (!command.Cleanup) { await gate.HoldAsync(); admissionAfterStop = admission(); }
            return fixture.Transport.Accept(command, admission, ct);
        };
        var pending = run.SubmitAsync(new[] { Down(65), Down(66) }); await gate.EnteredAsync();
        if (panic) fixture.Owner.SignalStopAll(); var stopped = run.StopAsync(); gate.Open();
        await pending.WaitAsync(Deadline); await stopped.WaitAsync(Deadline);
        Assert.Equal(false, admissionAfterStop); Assert.Empty(fixture.Transport.Accepted);
        Assert.Single(fixture.Transport.Attempts); Assert.Equal(TargetedInputRunState.Stopped, run.Snapshot.State);
        Assert.Equal(0, run.Snapshot.SubmittedHolds);
    }

    [Fact]
    public async Task AlreadyQueuedPrefixCompletesAfterStopButNoLaterOrdinaryEventIsSent()
    {
        await using var fixture = new Fixture(); var gate = fixture.NewGate(); var run = Open(fixture);
        fixture.Transport.Submission = async (command, admission, ct) =>
        {
            var result = fixture.Transport.Accept(command, admission, ct);
            if (!command.Cleanup) await gate.HoldAsync();
            return result;
        };
        var pending = run.SubmitAsync(new[] { Down(65), Down(66) }); await gate.EnteredAsync();
        var stopped = run.StopAsync(); Assert.False(stopped.IsCompleted); gate.Open();
        var result = await pending.WaitAsync(Deadline); await stopped.WaitAsync(Deadline);
        Assert.Equal(1, result.SubmittedCount); Assert.Equal(0, result.UncertainCount);
        Assert.Equal(new[] { Down(65), Up(65) }, fixture.Transport.Accepted.Select(command => command.Signal));
        Assert.True(fixture.Transport.Accepted.Last().Cleanup); Assert.Equal(TargetedInputRunState.Stopped, result.Run.State);
    }

    [Fact]
    public async Task CancellationAfterPossibleSideEffectRetainsUncertaintyAndCleansOnlyThatHold()
    {
        await using var fixture = new Fixture(); using var stop = new CancellationTokenSource(); var run = Open(fixture);
        fixture.Transport.Submission = (command, admission, ct) =>
        {
            if (command.Cleanup) return ValueTask.FromResult(fixture.Transport.Accept(command, admission, ct));
            fixture.Transport.Accept(command, admission, ct); stop.Cancel();
            throw new OperationCanceledException(stop.Token);
        };
        var result = await run.SubmitAsync(new[] { Down(65), Down(66) }, stop.Token).WaitAsync(Deadline);
        Assert.Equal(TargetedInputDispatchStatus.Cancelled, result.Status); Assert.Equal(0, result.SubmittedCount); Assert.Equal(1, result.UncertainCount);
        Assert.Equal(new[] { Down(65), Up(65) }, fixture.Transport.Accepted.Select(command => command.Signal));
        Assert.Equal(TargetedInputRunState.Stopped, result.Run.State); Assert.Equal(0, result.Run.SubmittedHolds);
        Assert.Equal(0, result.Run.LogicalHolds);
        Assert.NotNull(fixture.Owner.TryOpen(run.Target, TargetedInputPurpose.OwnedWindowDiagnostic).Run);
    }

    [Fact]
    public async Task CancellationBeforeFinalQueuedPressReturnsStillStopsAndReleasesThatPress()
    {
        await using var fixture = new Fixture(); var gate = fixture.NewGate(); using var stop = new CancellationTokenSource(); var run = Open(fixture);
        fixture.Transport.Submission = async (command, admission, ct) =>
        {
            var result = fixture.Transport.Accept(command, admission, ct);
            if (!command.Cleanup) await gate.HoldAsync();
            return result;
        };
        var pending = run.SubmitAsync(new[] { Down(65) }, stop.Token); await gate.EnteredAsync(); stop.Cancel();
        Assert.False(pending.IsCompleted); Assert.Equal(Down(65), Assert.Single(fixture.Transport.Accepted).Signal);
        gate.Open(); var result = await pending.WaitAsync(Deadline);
        Assert.Equal(TargetedInputDispatchStatus.Cancelled, result.Status); Assert.Equal(1, result.SubmittedCount); Assert.Equal(0, result.UncertainCount);
        Assert.Equal(TargetedInputRunState.Stopped, result.Run.State); Assert.Equal(0, result.Run.SubmittedHolds); Assert.Equal(0, result.Run.LogicalHolds);
        Assert.Equal(new[] { Down(65), Up(65) }, fixture.Transport.Accepted.Select(command => command.Signal));
        Assert.True(fixture.Transport.Accepted.Last().Cleanup);
    }

    [Fact]
    public async Task UncertainPointerPressIsReleasedAtItsPossiblySubmittedContentPosition()
    {
        await using var fixture = new Fixture(); var run = Open(fixture);
        var press = new TargetedInputSignal(TargetedInputKind.LeftDown, X: 120, Y: 80);
        fixture.Transport.Submission = (command, admission, ct) =>
        {
            var accepted = fixture.Transport.Accept(command, admission, ct);
            return ValueTask.FromResult(command.Cleanup ? accepted : TargetedInputSubmission.Uncertain);
        };
        var result = await run.SubmitAsync(new[] { press }).WaitAsync(Deadline);
        Assert.Equal(TargetedInputDispatchStatus.Faulted, result.Status); Assert.Equal(1, result.UncertainCount);
        Assert.Equal(TargetedInputRunState.Faulted, result.Run.State); Assert.Equal(0, result.Run.SubmittedHolds);
        var cleanup = Assert.Single(fixture.Transport.Attempts.Where(command => command.Cleanup));
        Assert.Equal(new TargetedInputSignal(TargetedInputKind.LeftUp, X: 120, Y: 80), cleanup.Signal);
    }

    [Fact]
    public async Task RejectedSuffixReportsQueuedPrefixAndReleasesOnlyItsUnfinishedPress()
    {
        await using var fixture = new Fixture(); var run = Open(fixture);
        fixture.Transport.Submission = (command, admission, ct) => ValueTask.FromResult(
            !command.Cleanup && command.Signal == Down(66) ? TargetedInputSubmission.Rejected : fixture.Transport.Accept(command, admission, ct));
        var result = await run.SubmitAsync(new[] { Down(65), Down(66), Down(67) }).WaitAsync(Deadline);
        Assert.Equal(TargetedInputDispatchStatus.Faulted, result.Status); Assert.Equal(1, result.SubmittedCount); Assert.Equal(0, result.UncertainCount);
        Assert.Equal(new[] { Down(65), Down(66), Up(65) }, fixture.Transport.Attempts.Select(command => command.Signal));
        Assert.Equal(new[] { Down(65), Up(65) }, fixture.Transport.Accepted.Select(command => command.Signal));
        Assert.Equal(1, result.Run.LogicalHolds); Assert.Equal(0, result.Run.SubmittedHolds);
    }

    [Theory]
    [InlineData(TargetedInputSubmission.Rejected)]
    [InlineData(TargetedInputSubmission.Uncertain)]
    public async Task FailedReleaseQuarantinesTargetAndExplicitRetryRetainsTheSameOwnedRelease(TargetedInputSubmission failure)
    {
        await using var fixture = new Fixture(); var run = Open(fixture); await run.SubmitAsync(new[] { Down(65) }).WaitAsync(Deadline);
        bool fail = true;
        fixture.Transport.Submission = (command, admission, ct) => ValueTask.FromResult(command.Cleanup && fail
            ? failure : fixture.Transport.Accept(command, admission, ct));
        var failed = await run.StopAsync().WaitAsync(Deadline);
        Assert.Equal(TargetedInputRunState.Faulted, failed.State); Assert.Equal(1, failed.SubmittedHolds);
        Assert.Null(fixture.Owner.TryOpen(run.Target with { RegistrationId = Guid.NewGuid() }, TargetedInputPurpose.BroadcastFollower).Run);
        Assert.Equal(TargetedInputDispatchStatus.Inactive, (await run.SubmitAsync(new[] { Down(66) })).Status);
        Assert.Equal(TargetedInputRunState.Faulted, (await run.ResumeAsync()).State);
        fail = false; var stopped = await run.RetryCleanupAsync().WaitAsync(Deadline);
        Assert.Equal(TargetedInputRunState.Stopped, stopped.State); Assert.Equal(0, stopped.LogicalHolds); Assert.Equal(0, stopped.SubmittedHolds);
        Assert.Equal(new[] { Up(65), Up(65) }, fixture.Transport.Attempts.Where(command => command.Cleanup).Select(command => command.Signal));
        Assert.NotNull(fixture.Owner.TryOpen(run.Target, TargetedInputPurpose.OwnedWindowDiagnostic).Run);
    }

    [Fact]
    public async Task PauseDuringBlockedUncertainPressCleanupCannotDowngradeTheFaultOrPermitResume()
    {
        await using var fixture = new Fixture(); var cleanupGate = fixture.NewGate(); var run = Open(fixture);
        fixture.Transport.Submission = async (command, admission, ct) =>
        {
            if (command.Cleanup) await cleanupGate.HoldAsync();
            var accepted = fixture.Transport.Accept(command, admission, ct);
            return command.Cleanup ? accepted : TargetedInputSubmission.Uncertain;
        };
        var pending = run.SubmitAsync(new[] { Down(65) }); await cleanupGate.EnteredAsync();
        long faultGeneration = run.Snapshot.Generation;
        var pause = run.PauseAsync();
        Assert.False(pause.IsCompleted); Assert.False(pending.IsCompleted);
        Assert.Equal(faultGeneration, run.Snapshot.Generation);
        Assert.Null(fixture.Owner.TryOpen(run.Target, TargetedInputPurpose.BroadcastFollower).Run);
        cleanupGate.Open(); var result = await pending.WaitAsync(Deadline); var paused = await pause.WaitAsync(Deadline);
        Assert.Equal(TargetedInputDispatchStatus.Faulted, result.Status); Assert.Equal(1, result.UncertainCount);
        Assert.Equal(TargetedInputRunState.Faulted, result.Run.State); Assert.Equal(TargetedInputRunState.Faulted, paused.State);
        int attempts = fixture.Transport.Attempts.Count;
        Assert.Equal(TargetedInputRunState.Faulted, (await run.ResumeAsync().WaitAsync(Deadline)).State);
        Assert.Equal(attempts, fixture.Transport.Attempts.Count);
        Assert.Equal(new[] { Down(65), Up(65) }, fixture.Transport.Accepted.Select(command => command.Signal));
        Assert.Equal(0, run.Snapshot.SubmittedHolds);
        Assert.Equal(TargetedInputRunState.Stopped, (await run.RetryCleanupAsync().WaitAsync(Deadline)).State);
    }

    [Fact]
    public async Task FailedDisposalRemainsClosedButExplicitRepairedCleanupAllowsActualDisposalToFinish()
    {
        await using var fixture = new Fixture(); var run = Open(fixture);
        await run.SubmitAsync(new[] { Down(65) }).WaitAsync(Deadline);
        fixture.Transport.Submission = (command, admission, ct) => ValueTask.FromResult(command.Cleanup
            ? TargetedInputSubmission.Rejected : fixture.Transport.Accept(command, admission, ct));
        var failedDisposal = fixture.Owner.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<InvalidOperationException>(() => failedDisposal.WaitAsync(Deadline));
        Assert.Equal(TargetedInputRunState.Faulted, run.Snapshot.State); Assert.Equal(1, run.Snapshot.SubmittedHolds);
        Assert.Null(fixture.Owner.TryOpen(Target(2), TargetedInputPurpose.OwnedWindowDiagnostic).Run);
        fixture.Transport.Submission = null;
        var cleaned = await run.RetryCleanupAsync().WaitAsync(Deadline);
        Assert.Equal(TargetedInputRunState.Stopped, cleaned.State); Assert.Equal(0, cleaned.SubmittedHolds); Assert.Equal(0, cleaned.LogicalHolds);
        await fixture.Owner.DisposeAsync().AsTask().WaitAsync(Deadline);
        await fixture.Owner.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.Equal(new[] { Up(65), Up(65) }, fixture.Transport.Attempts.Where(command => command.Cleanup).Select(command => command.Signal));
        Assert.Equal(new[] { Down(65), Up(65) }, fixture.Transport.Accepted.Select(command => command.Signal));
        Assert.Null(fixture.Owner.TryOpen(run.Target, TargetedInputPurpose.OwnedWindowDiagnostic).Run);
        Assert.Equal(0, fixture.Transport.ActiveCalls);
    }

    [Fact]
    public async Task RunsWithTheSameKeyHaveSeparateHeldOwnershipAndStoppingOneCannotReleaseTheOther()
    {
        await using var fixture = new Fixture(); var first = Open(fixture, Target(1)); var second = Open(fixture, Target(2));
        await first.SubmitAsync(new[] { Down(65) }).WaitAsync(Deadline); await second.SubmitAsync(new[] { Down(65) }).WaitAsync(Deadline);
        await first.StopAsync().WaitAsync(Deadline);
        Assert.Equal(1, second.Snapshot.SubmittedHolds); Assert.Equal(1, second.Snapshot.LogicalHolds);
        var cleanup = Assert.Single(fixture.Transport.Attempts.Where(command => command.Cleanup));
        Assert.Equal(first.Snapshot.RunId, cleanup.RunId); Assert.Equal(first.Target, cleanup.Target);
        await second.SubmitAsync(new[] { Up(65) }).WaitAsync(Deadline);
        Assert.Single(fixture.Transport.Attempts.Where(command => command.Cleanup));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialRestorationFailureReleasesTheRestoredPrefixAndNeverReportsRunning(bool throwsAfterPossibleSubmission)
    {
        await using var fixture = new Fixture(); var run = Open(fixture);
        await run.SubmitAsync(new[] { Down(16), Down(65) }).WaitAsync(Deadline); await run.PauseAsync().WaitAsync(Deadline);
        int before = fixture.Transport.Attempts.Count;
        fixture.Transport.Submission = (command, admission, ct) =>
        {
            if (!command.Cleanup && command.Signal == Down(65))
            {
                if (throwsAfterPossibleSubmission) throw new InvalidOperationException("Synthetic restoration transport failure.");
                return ValueTask.FromResult(TargetedInputSubmission.Rejected);
            }
            return ValueTask.FromResult(fixture.Transport.Accept(command, admission, ct));
        };
        var result = await run.ResumeAsync().WaitAsync(Deadline);
        Assert.Equal(TargetedInputRunState.Faulted, result.State); Assert.Equal(2, result.LogicalHolds); Assert.Equal(0, result.SubmittedHolds);
        var expected = throwsAfterPossibleSubmission ? new[] { Down(16), Down(65), Up(65), Up(16) } : new[] { Down(16), Down(65), Up(16) };
        Assert.Equal(expected, fixture.Transport.Attempts.Skip(before).Select(command => command.Signal));
        Assert.Null(fixture.Owner.TryOpen(run.Target, TargetedInputPurpose.BroadcastFollower).Run);
    }

    [Fact]
    public async Task PausedMouseHoldRestoresAtTheLastMovedPointWithoutReplayingMovementOrOriginalPress()
    {
        await using var fixture = new Fixture(); var run = Open(fixture);
        var press = new TargetedInputSignal(TargetedInputKind.LeftDown, X: 10, Y: 20);
        var move = new TargetedInputSignal(TargetedInputKind.Move, X: 70, Y: 80);
        await run.SubmitAsync(new[] { press, move }).WaitAsync(Deadline);
        await run.PauseAsync().WaitAsync(Deadline);
        Assert.Equal(new TargetedInputSignal(TargetedInputKind.LeftUp, X: 70, Y: 80), fixture.Transport.Accepted.Last().Signal);
        int before = fixture.Transport.Accepted.Count;
        var resumed = await run.ResumeAsync().WaitAsync(Deadline);
        Assert.Equal(TargetedInputRunState.Running, resumed.State); Assert.Equal(1, resumed.SubmittedHolds);
        var restored = Assert.Single(fixture.Transport.Accepted.Skip(before));
        Assert.Equal(new TargetedInputSignal(TargetedInputKind.LeftDown, X: 70, Y: 80), restored.Signal); Assert.False(restored.Cleanup);
        Assert.Single(fixture.Transport.Accepted.Where(command => command.Signal == press));
        Assert.Single(fixture.Transport.Accepted.Where(command => command.Signal == move));
        await run.SubmitAsync(new[] { new TargetedInputSignal(TargetedInputKind.LeftUp, X: 70, Y: 80) }).WaitAsync(Deadline);
        await run.StopAsync().WaitAsync(Deadline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PanicDuringRestoreValidationOrSubmissionPreventsAnyRestoredPress(bool insideSubmission)
    {
        await using var fixture = new Fixture(); var gate = fixture.NewGate(); var run = Open(fixture);
        await run.SubmitAsync(new[] { Down(16), Down(65) }).WaitAsync(Deadline); await run.PauseAsync().WaitAsync(Deadline);
        int before = fixture.Transport.Accepted.Count;
        if (insideSubmission)
            fixture.Transport.Submission = async (command, admission, ct) =>
            { if (!command.Cleanup) await gate.HoldAsync(); return fixture.Transport.Accept(command, admission, ct); };
        else fixture.Transport.Validation = async (_, cleanup, _) => { if (!cleanup) await gate.HoldAsync(); return TargetedInputValidation.Current; };
        var restoring = run.ResumeAsync(); await gate.EnteredAsync(); fixture.Owner.SignalStopAll();
        var stopped = run.StopAsync(); Assert.False(stopped.IsCompleted); gate.Open();
        await restoring.WaitAsync(Deadline); await stopped.WaitAsync(Deadline);
        Assert.Equal(before, fixture.Transport.Accepted.Count); Assert.Equal(TargetedInputRunState.Stopped, run.Snapshot.State);
        Assert.Equal(0, run.Snapshot.SubmittedHolds); Assert.Equal(0, run.Snapshot.LogicalHolds);
    }

    [Fact]
    public async Task GeometryChangeFaultsOrdinarySubmissionButIdentityOnlyCleanupStillReleasesOwnedInput()
    {
        await using var fixture = new Fixture(); var run = Open(fixture); int ordinary = 0;
        fixture.Transport.Validation = (_, cleanup, _) => ValueTask.FromResult(cleanup || Interlocked.Increment(ref ordinary) == 1
            ? TargetedInputValidation.Current : TargetedInputValidation.GeometryChanged);
        var result = await run.SubmitAsync(new[] { Down(65), Down(66) }).WaitAsync(Deadline);
        Assert.Equal(TargetedInputDispatchStatus.Faulted, result.Status); Assert.Equal(1, result.SubmittedCount);
        Assert.Equal(new[] { Down(65), Up(65) }, fixture.Transport.Accepted.Select(command => command.Signal));
        Assert.Contains(fixture.Transport.Validations, value => value.Cleanup); Assert.Equal(TargetedInputRunState.Faulted, result.Run.State);
    }

    [Fact]
    public async Task DefinitivelyGoneRegistrationNeedsNoReleaseToAReusedWindow()
    {
        await using var fixture = new Fixture(); var run = Open(fixture); await run.SubmitAsync(new[] { Down(65) }).WaitAsync(Deadline);
        fixture.Transport.Validation = (_, _, _) => ValueTask.FromResult(TargetedInputValidation.Gone);
        var stopped = await run.StopAsync().WaitAsync(Deadline);
        Assert.Equal(TargetedInputRunState.Stopped, stopped.State); Assert.Equal(0, stopped.SubmittedHolds);
        Assert.Empty(fixture.Transport.Attempts.Where(command => command.Cleanup));
        Assert.NotNull(fixture.Owner.TryOpen(run.Target with { RegistrationId = Guid.NewGuid() }, TargetedInputPurpose.OwnedWindowDiagnostic).Run);
    }

    [Fact]
    public async Task UnmatchedReleaseAndHoldBudgetFaultBeforeTheDisallowedNativeCall()
    {
        await using var fixture = new Fixture(new TargetedInputLimits(MaximumHolds: 1));
        var first = Open(fixture, Target(1)); var second = Open(fixture, Target(2));
        var unmatched = await first.SubmitAsync(new[] { Up(65) }).WaitAsync(Deadline);
        Assert.Equal(TargetedInputDispatchStatus.Faulted, unmatched.Status); Assert.Empty(fixture.Transport.Attempts);
        var limited = await second.SubmitAsync(new[] { Down(65), Down(66) }).WaitAsync(Deadline);
        Assert.Equal(TargetedInputDispatchStatus.Faulted, limited.Status); Assert.Equal(1, limited.SubmittedCount);
        Assert.Equal(new[] { Down(65), Up(65) }, fixture.Transport.Attempts.Select(command => command.Signal));
        Assert.Equal(0, limited.Run.SubmittedHolds); Assert.Equal(1, limited.Run.LogicalHolds);
    }

    [Fact]
    public async Task TransportExceptionsAfterPossibleSubmissionRemainUncertainWithoutExceptionTextInSnapshot()
    {
        await using var fixture = new Fixture(); var run = Open(fixture); const string raw = "synthetic-private-transport-detail";
        fixture.Transport.Submission = (command, admission, ct) => command.Cleanup
            ? ValueTask.FromResult(fixture.Transport.Accept(command, admission, ct)) : throw new InvalidOperationException(raw);
        var result = await run.SubmitAsync(new[] { Down(65) }).WaitAsync(Deadline);
        Assert.Equal(TargetedInputDispatchStatus.Faulted, result.Status); Assert.Equal(1, result.UncertainCount);
        Assert.Equal(Up(65), Assert.Single(fixture.Transport.Accepted).Signal);
        Assert.DoesNotContain(raw, result.Run.Explanation); Assert.DoesNotContain(raw, result.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalJoinsActualSubmissionOrCleanupAndBlocksReplacementOwnership(bool blockCleanup)
    {
        await using var fixture = new Fixture(); var gate = fixture.NewGate(); var run = Open(fixture);
        fixture.Transport.Submission = async (command, admission, ct) =>
        {
            var result = fixture.Transport.Accept(command, admission, ct);
            if (command.Cleanup == blockCleanup) await gate.HoldAsync();
            return result;
        };
        var submitting = run.SubmitAsync(new[] { Down(65) });
        Task<TargetedInputRunSnapshot>? stopping = null;
        if (blockCleanup) { await submitting.WaitAsync(Deadline); stopping = run.StopAsync(); }
        await gate.EnteredAsync();
        var disposal = fixture.Owner.DisposeAsync().AsTask(); var repeated = fixture.Owner.DisposeAsync().AsTask();
        Assert.Same(disposal, repeated); Assert.False(disposal.IsCompleted);
        Assert.Null(fixture.Owner.TryOpen(Target(2), TargetedInputPurpose.OwnedWindowDiagnostic).Run);
        gate.Open(); await submitting.WaitAsync(Deadline); if (stopping is not null) await stopping.WaitAsync(Deadline);
        await disposal.WaitAsync(Deadline); Assert.Equal(TargetedInputRunState.Stopped, run.Snapshot.State);
        Assert.Equal(new[] { Down(65), Up(65) }, fixture.Transport.Accepted.Select(command => command.Signal));
        Assert.Equal(0, fixture.Transport.ActiveCalls);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly List<Gate> _gates = [];
        public FakeTransport Transport { get; } = new();
        public TargetedInputCoordinator Owner { get; }
        public Fixture(TargetedInputLimits? limits = null) => Owner = new(Transport, limits);
        public Gate NewGate() { var gate = new Gate(); _gates.Add(gate); return gate; }
        public async ValueTask DisposeAsync()
        {
            // Even an assertion failure releases and joins every controlled owner, without detaching work.
            Transport.Validation = null; Transport.Submission = null;
            foreach (var gate in _gates) gate.Open();
            await Owner.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    private sealed class Gate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task EnteredAsync() => _entered.Task.WaitAsync(Deadline);
        public async Task HoldAsync() { _entered.TrySetResult(); await _release.Task; }
        public void Open() => _release.TrySetResult();
    }

    private sealed record ValidationCall(TargetedInputTarget Target, bool Cleanup);
    private sealed class FakeTransport : ITargetedInputTransport
    {
        public readonly ConcurrentQueue<ValidationCall> Validations = new();
        public readonly ConcurrentQueue<TargetedInputCommand> Attempts = new();
        public readonly ConcurrentQueue<TargetedInputCommand> Accepted = new();
        public Func<TargetedInputTarget, bool, CancellationToken, ValueTask<TargetedInputValidation>>? Validation;
        public Func<TargetedInputCommand, Func<bool>, CancellationToken, ValueTask<TargetedInputSubmission>>? Submission;
        private int _active, _maximum;
        public int ActiveCalls => Volatile.Read(ref _active);
        public int MaximumConcurrentCalls => Volatile.Read(ref _maximum);
        private void Enter()
        {
            int active = Interlocked.Increment(ref _active), previous;
            do { previous = Volatile.Read(ref _maximum); if (active <= previous) break; }
            while (Interlocked.CompareExchange(ref _maximum, active, previous) != previous);
        }
        public async ValueTask<TargetedInputValidation> ValidateAsync(TargetedInputTarget target, bool cleanup, CancellationToken ct)
        {
            Enter();
            try
            {
                Validations.Enqueue(new(target, cleanup)); ct.ThrowIfCancellationRequested();
                return Validation is { } behavior ? await behavior(target, cleanup, ct) : TargetedInputValidation.Current;
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public async ValueTask<TargetedInputSubmission> SubmitAsync(TargetedInputCommand command, Func<bool> admissionOpen, CancellationToken ct)
        {
            Enter();
            try
            {
                Attempts.Enqueue(command);
                return Submission is { } behavior ? await behavior(command, admissionOpen, ct) : Accept(command, admissionOpen, ct);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public TargetedInputSubmission Accept(TargetedInputCommand command, Func<bool> admissionOpen, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!admissionOpen()) return TargetedInputSubmission.Rejected;
            Accepted.Enqueue(command); return TargetedInputSubmission.Queued;
        }
    }
}
