using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using Xunit;

namespace MacroSuite.Core.Tests.Input;

public sealed class HostInputOperationContextTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RevokedHostOwnerUnblocksPausedSessionWithoutWaitingForControllerNotification()
    {
        var arbiter = new HostInputOperationArbiter(); arbiter.TryAcquire(HostInputOperationKind.Playback, out var lease, out _);
        using var control = new MacroController(); var session = new PlaybackSessionContext(control); var sink = new Sink();
        session.BindHostOperation(lease!); session.BindSink(sink); await control.PauseAsync();
        var pending = session.EmitAsync(new(InputEventType.KeyDown, 0, 0, 0, 0x87, 0)).AsTask();
        try
        {
            Assert.False(pending.IsCompleted); lease!.SignalStop();
            Assert.Equal(PlaybackInputAdmission.Stopped, session.InspectFinalInputAdmission());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Deadline));
            Assert.Equal(0, sink.Emissions); Assert.False(control.StopToken.IsCancellationRequested);
        }
        finally { await session.DisposeAsync(); await lease!.CompleteAsync(true); }
    }

    [Fact]
    public async Task ContextHasOneExplicitHostOwnerAndDoesNotAdoptAnotherLiveArbiter()
    {
        var first = new HostInputOperationArbiter(); var second = new HostInputOperationArbiter();
        first.TryAcquire(HostInputOperationKind.Playback, out var owner, out _);
        second.TryAcquire(HostInputOperationKind.Playback, out var foreign, out _);
        using var control = new MacroController(); var session = new PlaybackSessionContext(control);
        try
        {
            session.BindHostOperation(owner!); session.BindHostOperation(owner!);
            Assert.Same(owner, session.HostOperation);
            Assert.Throws<InvalidOperationException>(() => session.BindHostOperation(foreign!));
            Assert.Same(owner, session.HostOperation); Assert.True(foreign!.CanContinue);
        }
        finally { await session.DisposeAsync(); await owner!.CompleteAsync(true); await foreign!.CompleteAsync(true); }
    }

    [Fact]
    public async Task CapturedControllerPanicCannotReleaseReplacementOwnersInput()
    {
        var arbiter = new HostInputOperationArbiter(); arbiter.TryAcquire(HostInputOperationKind.Playback, out var old, out _);
        using var player = new Player(); using var controller = new MacroController(player, old);
        controller.Panic(); Assert.Same(old, player.LastReleased); Assert.Equal(1, player.OwnedReleases); Assert.Equal(0, player.GlobalReleases);
        await old!.CompleteAsync(true); arbiter.TryAcquire(HostInputOperationKind.AutoClicker, out var current, out _);
        controller.Panic(); controller.SignalPanic(); await controller.WaitForPanicReleaseAsync().WaitAsync(Deadline);
        Assert.Equal(1, player.OwnedReleases); Assert.Equal(0, player.GlobalReleases); Assert.True(current!.CanContinue);
        await current.CompleteAsync(true);
    }

    [Fact]
    public async Task LeaseStopSignalsNormalControllerStopWithoutConvertingItToPanic()
    {
        var arbiter = new HostInputOperationArbiter(); arbiter.TryAcquire(HostInputOperationKind.Playback, out var lease, out _);
        using var control = new MacroController(null, lease);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lease!.StopToken.Register(() => { control.SignalStop(); entered.TrySetResult(); });
        await control.PauseAsync(); lease.SignalStop();
        await entered.Task.WaitAsync(Deadline); await registration.DisposeAsync();
        await control.WaitForStopSignalAsync().WaitAsync(Deadline);
        Assert.True(control.StopToken.IsCancellationRequested); Assert.False(control.IsPanicTriggered);
        Assert.False(control.PanicToken.IsCancellationRequested); Assert.False(control.IsPaused);
        await lease.CompleteAsync(true).WaitAsync(Deadline);
    }

    [Fact]
    public void NeverManagedControllerRetainsLegacyReleaseContract()
    {
        using var player = new Player(); using var controller = new MacroController(player);
        controller.Panic(); Assert.Equal(1, player.GlobalReleases); Assert.Equal(0, player.OwnedReleases);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SynchronousDisposeFromSignalNotificationFailsClearlyInsteadOfWaitingOnItsOwnWorker(bool panic)
    {
        var controller = new MacroController();
        controller.StateChanged += (_, _) => controller.Dispose();
        if (panic) controller.SignalPanic(); else controller.SignalStop();
        var signal = panic ? controller.WaitForPanicReleaseAsync() : controller.WaitForStopSignalAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => signal.WaitAsync(Deadline));
        Assert.Equal("Complete controller disposal after the state-change handler returns.", error.Message);
        // A failed observer must not prevent the outside owner from disposing both token sources.
        var disposal = Assert.Throws<InvalidOperationException>(controller.Dispose);
        Assert.Same(error, disposal);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => controller.PauseAsync());
        controller.Dispose();
    }

    private sealed class Sink : IPlaybackSessionSink
    {
        public int Emissions;
        public ValueTask EmitSessionInputAsync(InputEvent input, bool cleanup, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Emissions++; return ValueTask.CompletedTask; }
    }

    private sealed class Player : IInputPlayer, IHostInputOperationAwarePlayer
    {
        public int GlobalReleases, OwnedReleases; public HostInputOperationLease? LastReleased;
        public bool IsPlaying => false;
        public void ReleaseAllHeldKeys() => GlobalReleases++;
        public void ReleaseHostOperationInputs(HostInputOperationLease lease)
        { Assert.True(lease.OwnsCleanup); OwnedReleases++; LastReleased = lease; }
        public IDisposable AttachHostOperation(HostInputOperationLease lease) => throw new InvalidOperationException("No attachment expected.");
        public Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default)
            => throw new InvalidOperationException("No input expected.");
        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false)
            => throw new InvalidOperationException("No input expected.");
        public void SendRelativeMove(int dx, int dy) => throw new InvalidOperationException("No input expected.");
        public void Dispose() { }
    }
}
