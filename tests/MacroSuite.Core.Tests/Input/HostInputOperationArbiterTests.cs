using MacroSuite.Core.Input;
using Xunit;

namespace MacroSuite.Core.Tests.Input;

public sealed class HostInputOperationArbiterTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    [ThreadStatic] private static bool _insideSignal;

    [Theory]
    [InlineData(HostInputOperationKind.Playback)] [InlineData(HostInputOperationKind.Recording)]
    [InlineData(HostInputOperationKind.AutoClicker)] [InlineData(HostInputOperationKind.Picker)]
    [InlineData(HostInputOperationKind.CaptureTest)] [InlineData(HostInputOperationKind.Companion)]
    public async Task EveryKindRetainsExclusiveReservationUntilActualCompletion(HostInputOperationKind kind)
    {
        var arbiter = new HostInputOperationArbiter();
        Assert.True(arbiter.TryAcquire(kind, out var lease, out var reason)); Assert.Empty(reason);
        Assert.True(lease!.CanContinue); Assert.True(lease.OwnsCleanup); Assert.Same(arbiter, lease.Arbiter);
        Assert.False(lease.Completion.IsCompleted); Assert.Equal(kind, arbiter.Snapshot.Kind);
        foreach (var contender in Enum.GetValues<HostInputOperationKind>())
        { Assert.False(arbiter.TryAcquire(contender, out var blocked, out var notice)); Assert.Null(blocked); Assert.NotEmpty(notice); }
        lease.SignalStop();
        Assert.False(lease.CanContinue); Assert.True(lease.IsCurrent); Assert.True(lease.OwnsCleanup);
        Assert.True(arbiter.Snapshot.IsBusy); Assert.False(lease.Completion.IsCompleted);
        await lease.CompleteAsync(true).WaitAsync(Deadline);
        Assert.True(lease.Completion.IsCompletedSuccessfully); Assert.True(lease.IsRetired);
        Assert.False(lease.IsCurrent); Assert.False(lease.OwnsCleanup); Assert.False(arbiter.Snapshot.IsBusy);
        Assert.True(arbiter.TryAcquire(kind, out var next, out _));
        Assert.NotEqual(lease.OperationId, next!.OperationId); await next.CompleteAsync(true);
    }

    [Fact]
    public async Task ConcurrentStartsHaveOneWinnerAndNeverQueueLosers()
    {
        var arbiter = new HostInputOperationArbiter();
        var attempts = Enumerable.Range(0, 32).Select(index => Task.Run(() =>
        { arbiter.TryAcquire(HostInputOperationKind.Playback, out var lease, out _); return lease; })).ToArray();
        var all = await Task.WhenAll(attempts).WaitAsync(Deadline);
        var winner = Assert.Single(all.Where(lease => lease is not null));
        await winner!.CompleteAsync(true); Assert.All(all.Where(lease => lease is null), Assert.Null);
        Assert.False(arbiter.Snapshot.IsBusy);
    }

    [Fact]
    public async Task StaleGenerationCannotAcquireAndCapturedStopCannotStopReplacement()
    {
        var arbiter = new HostInputOperationArbiter(); long before = arbiter.StopGeneration;
        arbiter.SignalStop();
        Assert.False(arbiter.TryAcquire(HostInputOperationKind.Playback, before, out var stale, out _)); Assert.Null(stale);
        Assert.True(arbiter.TryAcquire(HostInputOperationKind.Playback, out var first, out _));
        await first!.CompleteAsync(true);
        Assert.True(arbiter.TryAcquire(HostInputOperationKind.AutoClicker, out var replacement, out _));
        long generation = arbiter.StopGeneration;
        first.SignalStop(); await first.CompleteAsync(false);
        Assert.True(replacement!.CanContinue); Assert.Equal(generation, arbiter.StopGeneration);
        Assert.True(arbiter.Owns(replacement)); Assert.False(arbiter.Snapshot.IsQuarantined);
        await replacement.CompleteAsync(true);
    }

    [Fact]
    public async Task HookSignalReturnsBeforeBlockedCallbackAndCompletionJoinsIt()
    {
        var arbiter = new HostInputOperationArbiter(); arbiter.TryAcquire(HostInputOperationKind.Playback, out var lease, out _);
        var entered = NewSignal(); using var release = new ManualResetEventSlim(); bool inline = false;
        using var registration = lease!.StopToken.Register(() => { inline = _insideSignal; entered.TrySetResult(); release.Wait(); });
        Task? finish = null;
        try
        {
            await Task.Run(() => { _insideSignal = true; try { arbiter.SignalStop(); } finally { _insideSignal = false; } }).WaitAsync(Deadline);
            Assert.False(lease.CanContinue); await entered.Task.WaitAsync(Deadline); Assert.False(inline);
            finish = lease.CompleteAsync(true); Assert.False(finish.IsCompleted); Assert.False(lease.Completion.IsCompleted);
            Assert.False(arbiter.TryAcquire(HostInputOperationKind.AutoClicker, out _, out _));
        }
        finally { release.Set(); if (finish is not null) await finish.WaitAsync(Deadline); else await lease.CompleteAsync(true); }
    }

    [Fact]
    public async Task ReentrantSignalCallbackCanReadSnapshotAndSignalAgainWithoutDeadlock()
    {
        var arbiter = new HostInputOperationArbiter(); arbiter.TryAcquire(HostInputOperationKind.Recording, out var lease, out _);
        int observed = 0;
        using var registration = lease!.StopToken.Register(() =>
        { Assert.True(arbiter.Snapshot.IsBusy); lease.SignalStop(); Interlocked.Increment(ref observed); });
        arbiter.SignalStop(); await lease.CompleteAsync(true).WaitAsync(Deadline);
        Assert.Equal(1, observed);
    }

    [Fact]
    public async Task FailedStopCallbackQuarantinesAndRemainsAnObservableError()
    {
        var arbiter = new HostInputOperationArbiter(); arbiter.TryAcquire(HostInputOperationKind.Playback, out var lease, out _);
        using var registration = lease!.StopToken.Register(() => throw new InvalidDataException("controlled stop failure"));
        lease.SignalStop(); var error = await Assert.ThrowsAsync<InvalidOperationException>(() => lease.CompleteAsync(true).WaitAsync(Deadline));
        Assert.Contains("quarantined", error.Message); Assert.NotNull(error.InnerException);
        Assert.True(lease.Completion.IsCompletedSuccessfully); Assert.True(arbiter.Snapshot.IsQuarantined);
        Assert.False(lease.CanContinue); Assert.True(lease.OwnsCleanup);
        Assert.Same(lease.CompleteAsync(false), lease.CompleteAsync(true));
        Assert.True(await arbiter.TryRecoverAsync(_ => Task.CompletedTask)); Assert.False(lease.OwnsCleanup);
    }

    [Fact]
    public async Task FailedRetirementCannotBeErasedByAnotherCompleteOrStaleRecovery()
    {
        var arbiter = new HostInputOperationArbiter(); arbiter.TryAcquire(HostInputOperationKind.AutoClicker, out var lease, out _);
        var first = lease!.CompleteAsync(false); await first;
        Assert.Same(first, lease.CompleteAsync(true)); await lease.CompleteAsync(true);
        Assert.True(arbiter.Snapshot.IsQuarantined); Assert.True(lease.OwnsCleanup); Assert.True(lease.IsRetired);
        Assert.False(arbiter.TryAcquire(HostInputOperationKind.Recording, out _, out _));
        await Assert.ThrowsAsync<HostInputOperationCleanupException>(() => arbiter.DrainAsync().WaitAsync(Deadline));
        Assert.True(await arbiter.TryRecoverAsync(_ => Task.CompletedTask));
        Assert.False(arbiter.Snapshot.IsBusy); Assert.False(lease.OwnsCleanup);
        int calls = 0; Assert.False(await arbiter.TryRecoverAsync(_ => { calls++; return Task.CompletedTask; })); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ExactRecoveryCannotInvokeCleanupForADifferentQuarantinedOwner()
    {
        var arbiter = new HostInputOperationArbiter();
        Assert.True(arbiter.TryAcquire(HostInputOperationKind.Companion, out var old, out _));
        await old!.CompleteAsync(false);
        Assert.True(await arbiter.TryRecoverAsync(old, _ => Task.CompletedTask));
        Assert.True(arbiter.TryAcquire(HostInputOperationKind.Recording, out var current, out _));
        await current!.CompleteAsync(false);
        Assert.False(await arbiter.TryRecoverAsync(old, _ => throw new InvalidOperationException("The wrong owner was recovered.")));
        Assert.True(current.OwnsCleanup);
        Assert.True(arbiter.Snapshot.IsQuarantined);
        Assert.True(await arbiter.TryRecoverAsync(current, _ => Task.CompletedTask));
    }

    [Fact]
    public async Task RecoveryFailureIsObservableAndLeavesQuarantine()
    {
        var arbiter = new HostInputOperationArbiter(); arbiter.TryAcquire(HostInputOperationKind.Picker, out var lease, out _);
        await lease!.CompleteAsync(false);
        var fault = new IOException("controlled hook cleanup failure");
        var thrown = await Assert.ThrowsAsync<IOException>(() => arbiter.TryRecoverAsync(_ => Task.FromException(fault)));
        Assert.Same(fault, thrown); Assert.True(arbiter.Snapshot.IsQuarantined); Assert.True(lease.OwnsCleanup);
        await Assert.ThrowsAsync<HostInputOperationCleanupException>(() => arbiter.DrainAsync().WaitAsync(Deadline));
        Assert.True(await arbiter.TryRecoverAsync(_ => Task.CompletedTask));
    }

    [Fact]
    public async Task CancelledIgnoringRecoveryKeepsActualOwnershipUntilCallbackReturns()
    {
        var arbiter = new HostInputOperationArbiter(); arbiter.TryAcquire(HostInputOperationKind.CaptureTest, out var lease, out _);
        await lease!.CompleteAsync(false); using var cancel = new CancellationTokenSource();
        var entered = NewSignal(); var release = NewSignal(); int calls = 0;
        Task<bool> recovery = arbiter.TryRecoverAsync(async _ => { calls++; entered.TrySetResult(); await release.Task; }, cancel.Token);
        Task<bool>? coalesced = null;
        try
        {
            await entered.Task.WaitAsync(Deadline); cancel.Cancel();
            Assert.False(recovery.IsCompleted); Assert.True(arbiter.Snapshot.IsRecovering); Assert.True(lease.OwnsCleanup);
            Assert.False(arbiter.TryAcquire(HostInputOperationKind.Playback, out _, out _));
            coalesced = arbiter.TryRecoverAsync(_ => { calls++; return Task.CompletedTask; });
            Assert.False(coalesced.IsCompleted);
        }
        finally { release.TrySetResult(); await recovery.WaitAsync(Deadline); }
        Assert.False(await recovery.WaitAsync(Deadline)); Assert.False(await coalesced!.WaitAsync(Deadline));
        Assert.Equal(1, calls); Assert.True(arbiter.Snapshot.IsQuarantined);
        Assert.True(await arbiter.TryRecoverAsync(_ => Task.CompletedTask));
    }

    [Fact]
    public async Task NewStopFencesLateRecoverySuccessButDoesNotAbandonItsCallback()
    {
        var arbiter = new HostInputOperationArbiter(); arbiter.TryAcquire(HostInputOperationKind.Playback, out var lease, out _);
        await lease!.CompleteAsync(false); var entered = NewSignal(); var release = NewSignal();
        var recovery = arbiter.TryRecoverAsync(async _ => { entered.TrySetResult(); await release.Task; });
        try { await entered.Task.WaitAsync(Deadline); arbiter.SignalStop(); Assert.False(recovery.IsCompleted); }
        finally { release.TrySetResult(); await recovery.WaitAsync(Deadline); }
        Assert.False(await recovery.WaitAsync(Deadline)); Assert.True(arbiter.Snapshot.IsQuarantined);
        Assert.True(await arbiter.TryRecoverAsync(_ => Task.CompletedTask));
    }

    [Fact]
    public async Task CancellationAfterRecoveryCommitCannotContradictSuccess()
    {
        var arbiter = new HostInputOperationArbiter(); arbiter.TryAcquire(HostInputOperationKind.Playback, out var lease, out _);
        await lease!.CompleteAsync(false); using var cancel = new CancellationTokenSource();
        var recovery = arbiter.TryRecoverAsync(_ => Task.CompletedTask, cancel.Token);
        Assert.True(await recovery.WaitAsync(Deadline)); cancel.Cancel();
        Assert.True(await recovery); Assert.False(arbiter.Snapshot.IsBusy);
    }

    [Fact]
    public async Task ClosedAdmissionJoinsOwnerAndRecoveryWithoutBecomingAvailableAgain()
    {
        var arbiter = new HostInputOperationArbiter(); arbiter.TryAcquire(HostInputOperationKind.Recording, out var lease, out _);
        arbiter.Close(); var drain = arbiter.DrainAsync();
        Assert.False(drain.IsCompleted); Assert.False(lease!.CanContinue);
        Assert.False(arbiter.TryAcquire(HostInputOperationKind.Playback, out _, out _));
        await lease.CompleteAsync(false); await Assert.ThrowsAsync<HostInputOperationCleanupException>(() => drain.WaitAsync(Deadline));
        var entered = NewSignal(); var release = NewSignal();
        var recovery = arbiter.TryRecoverAsync(async _ => { entered.TrySetResult(); await release.Task; });
        Task? recoveringDrain = null;
        try { await entered.Task.WaitAsync(Deadline); recoveringDrain = arbiter.DrainAsync(); Assert.False(recoveringDrain.IsCompleted); }
        finally { release.TrySetResult(); await recovery.WaitAsync(Deadline); }
        Assert.True(await recovery.WaitAsync(Deadline)); await recoveringDrain!.WaitAsync(Deadline);
        Assert.True(arbiter.Snapshot.IsClosed); Assert.False(arbiter.Snapshot.IsBusy);
        Assert.False(arbiter.TryAcquire(HostInputOperationKind.Playback, out _, out _));
    }

    [Fact]
    public async Task CapturedRetirementWaitDoesNotJoinAReplacementOperation()
    {
        var arbiter = new HostInputOperationArbiter(); arbiter.TryAcquire(HostInputOperationKind.Playback, out var first, out _);
        var wait = arbiter.WaitForRetirementAsync(first!.OperationId); Assert.False(wait.IsCompleted);
        await first.CompleteAsync(true); arbiter.TryAcquire(HostInputOperationKind.AutoClicker, out var next, out _);
        await wait.WaitAsync(Deadline); await arbiter.WaitForRetirementAsync(first.OperationId).WaitAsync(Deadline);
        Assert.True(next!.CanContinue); await next.CompleteAsync(true);
    }

    [Fact]
    public async Task ForeignArbiterDoesNotRecognizeLiveLeaseAndInvalidKindCannotReserve()
    {
        var first = new HostInputOperationArbiter(); var second = new HostInputOperationArbiter();
        first.TryAcquire(HostInputOperationKind.Picker, out var lease, out _);
        Assert.False(second.Owns(lease!)); Assert.False(second.Snapshot.IsBusy);
        Assert.Throws<ArgumentOutOfRangeException>(() => second.TryAcquire((HostInputOperationKind)999, out _, out _));
        Assert.False(second.Snapshot.IsBusy); await lease!.CompleteAsync(true);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
