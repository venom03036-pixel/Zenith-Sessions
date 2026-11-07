using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.Input;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Input;

// Every session is synthetic. No test installs a hook, creates a native thread or reads the cursor.
public sealed class OneShotLocationPickerOwnershipTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AcceptedPointRetainsExclusiveOwnershipUntilActualSessionExit()
    {
        var owner = new HostInputOperationArbiter(); var session = new Session();
        var picker = new OneShotLocationPicker(owner, () => session);
        Task<ScreenPoint?> pick = picker.ArmAndWaitAsync();
        try
        {
            await session.Started.Task.WaitAsync(Deadline);
            session.ReadySignal.SetResult(); session.Selected.SetResult(new(11, 22));
            await session.StopRequested.Task.WaitAsync(Deadline);
            Assert.False(pick.IsCompleted); Assert.Same(pick, picker.Completion);
            Assert.Equal(HostInputOperationKind.Picker, owner.Snapshot.Kind);
            Assert.False(owner.TryAcquire(HostInputOperationKind.Playback, out _, out _));
            Assert.Throws<InvalidOperationException>(() => { _ = picker.ArmAndWaitAsync(); });
        }
        finally { await Finish(picker, session, pick); }
        Assert.Equal(new ScreenPoint(11, 22), await pick.WaitAsync(Deadline));
        Assert.False(owner.Snapshot.IsBusy); await picker.DisposeAsync();
    }

    [Fact]
    public async Task CancellationBeforeReadinessWaitsForExitAndNeverPublishesLateCoordinates()
    {
        var owner = new HostInputOperationArbiter(); var session = new Session();
        var picker = new OneShotLocationPicker(owner, () => session); using var stop = new CancellationTokenSource();
        var pick = picker.ArmAndWaitAsync(stop.Token);
        try
        {
            await session.Started.Task.WaitAsync(Deadline); stop.Cancel();
            await session.StopRequested.Task.WaitAsync(Deadline);
            Assert.False(pick.IsCompleted); Assert.True(owner.Snapshot.IsBusy);
            session.ReadySignal.TrySetResult(); session.Selected.TrySetResult(new(777, 888));
        }
        finally { await Finish(picker, session, pick); }
        Assert.Null(await pick.WaitAsync(Deadline)); Assert.False(owner.Snapshot.IsBusy);
        await picker.DisposeAsync();
    }

    [Fact]
    public async Task StopDuringTheActualExitJoinWinsOverAnAlreadySelectedPoint()
    {
        var owner = new HostInputOperationArbiter(); var session = new Session();
        var picker = new OneShotLocationPicker(owner, () => session);
        var pick = picker.ArmAndWaitAsync();
        try
        {
            await session.Started.Task.WaitAsync(Deadline);
            session.ReadySignal.SetResult(); session.Selected.SetResult(new(20, 30));
            await session.StopRequested.Task.WaitAsync(Deadline);
            Assert.False(pick.IsCompleted); picker.Cancel();
        }
        finally { await Finish(picker, session, pick); }
        Assert.Null(await pick); Assert.False(owner.Snapshot.IsBusy); await picker.DisposeAsync();
    }

    [Fact]
    public async Task CallerAlreadyCancelledDoesNotReserveOrCreateASession()
    {
        var owner = new HostInputOperationArbiter(); int factories = 0;
        var picker = new OneShotLocationPicker(owner, () => { factories++; return new Session(); });
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => { _ = picker.ArmAndWaitAsync(stop.Token); });
        Assert.Equal(0, factories); Assert.False(owner.Snapshot.IsBusy); await picker.DisposeAsync();
    }

    [Theory]
    [InlineData(HostInputOperationKind.Picker)]
    [InlineData(HostInputOperationKind.CaptureTest)]
    public async Task ExplicitInteractiveParentIsBorrowedAndOnlyParentCanRetireIt(HostInputOperationKind kind)
    {
        var owner = new HostInputOperationArbiter(); var parent = Acquire(owner, kind); var session = new Session();
        var picker = new OneShotLocationPicker(owner, () => session);
        var pick = picker.ArmAndWaitAsync(parent);
        try
        {
            await session.Started.Task.WaitAsync(Deadline);
            session.ReadySignal.SetResult(); session.Selected.SetResult(new(5, 9)); session.ExitedSignal.SetResult();
            Assert.Equal(new ScreenPoint(5, 9), await pick.WaitAsync(Deadline));
            Assert.True(parent.CanContinue); Assert.False(parent.Completion.IsCompleted);
            Assert.Equal(parent.OperationId, owner.Snapshot.OperationId);
        }
        finally { await Finish(picker, session, pick); await picker.DisposeAsync(); await parent.CompleteAsync(true); }
    }

    [Theory]
    [InlineData(HostInputOperationKind.Playback)]
    [InlineData(HostInputOperationKind.Recording)]
    [InlineData(HostInputOperationKind.AutoClicker)]
    [InlineData(HostInputOperationKind.Companion)]
    public async Task OtherProducerCannotLendItsLeaseToAnInteractivePicker(HostInputOperationKind kind)
    {
        var owner = new HostInputOperationArbiter(); var parent = Acquire(owner, kind); int factories = 0;
        var picker = new OneShotLocationPicker(owner, () => { factories++; return new Session(); });
        try
        {
            Assert.Throws<InvalidOperationException>(() => { _ = picker.ArmAndWaitAsync(parent); });
            Assert.Equal(0, factories); Assert.True(parent.CanContinue); Assert.False(parent.Completion.IsCompleted);
        }
        finally { await picker.DisposeAsync(); await parent.CompleteAsync(true); }
    }

    [Fact]
    public async Task ForeignOrRevokedParentIsRejectedWithoutTouchingTheCurrentOwner()
    {
        var owner = new HostInputOperationArbiter(); var other = new HostInputOperationArbiter();
        var current = Acquire(owner, HostInputOperationKind.Picker); var foreign = Acquire(other, HostInputOperationKind.Picker);
        var picker = new OneShotLocationPicker(owner, () => throw new InvalidOperationException("No session expected."));
        try
        {
            Assert.Throws<InvalidOperationException>(() => { _ = picker.ArmAndWaitAsync(foreign); });
            Assert.True(current.CanContinue); current.SignalStop();
            Assert.Throws<InvalidOperationException>(() => { _ = picker.ArmAndWaitAsync(current); });
        }
        finally { await current.CompleteAsync(true); await foreign.CompleteAsync(true); await picker.DisposeAsync(); }
    }

    [Fact]
    public async Task CapturedCancelCannotStopTheNextArm()
    {
        var owner = new HostInputOperationArbiter(); var first = new Session(); var second = new Session(); int calls = 0;
        var picker = new OneShotLocationPicker(owner, () => Interlocked.Increment(ref calls) == 1 ? first : second);
        var oldParent = Acquire(owner, HostInputOperationKind.Picker);
        first.ReadySignal.SetResult(); first.Selected.SetResult(null); first.ExitedSignal.SetResult();
        await picker.ArmAndWaitAsync(oldParent).WaitAsync(Deadline); await oldParent.CompleteAsync(true);
        var nextParent = Acquire(owner, HostInputOperationKind.Picker);
        var pick = picker.ArmAndWaitAsync(nextParent);
        try
        {
            await second.Started.Task.WaitAsync(Deadline); second.ReadySignal.SetResult();
            picker.Cancel(oldParent);
            Assert.False(second.StopRequested.Task.IsCompleted); Assert.True(nextParent.CanContinue);
            picker.Cancel(nextParent); await second.StopRequested.Task.WaitAsync(Deadline);
            Assert.False(pick.IsCompleted);
        }
        finally { second.ExitedSignal.TrySetResult(); await pick.WaitAsync(Deadline); await nextParent.CompleteAsync(true); await picker.DisposeAsync(); }
    }

    [Fact]
    public async Task ImmediateDisposeFencesButAsyncDisposeJoinsTheActualSession()
    {
        var owner = new HostInputOperationArbiter(); var session = new Session();
        var picker = new OneShotLocationPicker(owner, () => session); var pick = picker.ArmAndWaitAsync();
        await session.Started.Task.WaitAsync(Deadline); picker.Dispose();
        Task dispose = picker.DisposeAsync().AsTask();
        try
        {
            await session.StopRequested.Task.WaitAsync(Deadline);
            Assert.False(dispose.IsCompleted); Assert.False(pick.IsCompleted); Assert.True(owner.Snapshot.IsBusy);
            Assert.Throws<ObjectDisposedException>(() => { _ = picker.ArmAndWaitAsync(); });
            Assert.Same(dispose, picker.DisposeAsync().AsTask());
        }
        finally { session.ExitedSignal.TrySetResult(); await dispose.WaitAsync(Deadline); }
        Assert.Null(await pick); Assert.False(owner.Snapshot.IsBusy);
        Assert.False(owner.Snapshot.IsClosed); // The injected arbiter remains caller-owned.
    }

    [Fact]
    public async Task StartupFailureDoesNotReleaseOwnershipBeforeVerifiedExit()
    {
        var owner = new HostInputOperationArbiter(); var session = new Session();
        var picker = new OneShotLocationPicker(owner, () => session); var pick = picker.ArmAndWaitAsync();
        try
        {
            await session.Started.Task.WaitAsync(Deadline);
            session.ReadySignal.SetException(new InvalidOperationException("Synthetic startup failed."));
            await session.StopRequested.Task.WaitAsync(Deadline);
            Assert.False(pick.IsCompleted); Assert.True(owner.Snapshot.IsBusy);
        }
        finally { await Finish(picker, session, pick); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => pick.WaitAsync(Deadline));
        Assert.False(owner.Snapshot.IsBusy); Assert.False(owner.Snapshot.IsQuarantined);
        await Assert.ThrowsAsync<InvalidOperationException>(() => picker.DisposeAsync().AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainStopOrExitQuarantinesOnlyAfterActualSessionRetires(bool stopThrows)
    {
        var owner = new HostInputOperationArbiter(); var session = new Session { ThrowStop = stopThrows };
        var picker = new OneShotLocationPicker(owner, () => session); var pick = picker.ArmAndWaitAsync();
        try
        {
            await session.Started.Task.WaitAsync(Deadline); session.ReadySignal.SetResult(); session.Selected.SetResult(null);
            await session.StopRequested.Task.WaitAsync(Deadline);
            Assert.False(pick.IsCompleted); Assert.False(owner.Snapshot.IsQuarantined);
            if (stopThrows) session.ExitedSignal.SetResult();
            else session.ExitedSignal.SetException(new InvalidOperationException("Synthetic exit uncertain."));
        }
        finally { await Finish(picker, session, pick); }
        await Assert.ThrowsAsync<HostInputOperationCleanupException>(() => pick.WaitAsync(Deadline));
        Assert.True(owner.Snapshot.IsQuarantined); Assert.True(owner.Snapshot.IsBusy);
        Assert.False(owner.TryAcquire(HostInputOperationKind.Recording, out _, out _));
        await Assert.ThrowsAsync<HostInputOperationCleanupException>(() => picker.DisposeAsync().AsTask());
    }

    private static HostInputOperationLease Acquire(HostInputOperationArbiter owner, HostInputOperationKind kind)
    { Assert.True(owner.TryAcquire(kind, out var lease, out _)); return lease!; }

    private static async Task Finish(OneShotLocationPicker picker, Session session, Task pick)
    {
        if (!session.Selected.Task.IsCompleted && !session.ReadySignal.Task.IsFaulted) picker.Cancel();
        session.ExitedSignal.TrySetResult();
        try { await pick.WaitAsync(Deadline); }
        catch (Exception) when (pick.IsCompleted) { /* The test below asserts the exact operation outcome. */ }
    }

    private sealed class Session : IOneShotLocationPickerSession
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadySignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ScreenPoint?> Selected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ExitedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ThrowStop;
        public bool IsArmed => Started.Task.IsCompleted && !StopRequested.Task.IsCompleted;
        public Task Ready => ReadySignal.Task;
        public Task<ScreenPoint?> Selection => Selected.Task;
        public Task Exited => ExitedSignal.Task;
        public void Start() => Started.TrySetResult();
        public void RequestStop()
        { StopRequested.TrySetResult(); if (ThrowStop) throw new InvalidOperationException("Synthetic stop rejected."); }
    }
}
