using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.Input;
using MacroSuite.Infrastructure.Windows;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Input;

// Every physical observation and submission is a memory fixture; no desktop APIs are called.
public sealed class Win32HostInputOperationTests
{
    private static InputEvent Down => new(InputEventType.KeyDown, 0, 0, 0, 65);
    private static InputEvent Up => new(InputEventType.KeyUp, 0, 0, 0, 65);
    private static HostInputOperationLease Acquire(HostInputOperationArbiter arbiter)
    { Assert.True(arbiter.TryAcquire(HostInputOperationKind.Playback, out var lease, out _)); return lease!; }

    [Fact]
    public void NeverManagedDirectPlayerRetainsLegacyInput()
    {
        var native = new Native();
        using var player = new Win32InputPlayer(native);
        player.SendRelativeMove(2, 3);
        player.SendClick();
        Assert.Equal(3, native.Sent.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopDuringGenericGuardWinsOverRefusalOrGuardFailure(bool throws)
    {
        var arbiter = new HostInputOperationArbiter(); var lease = Acquire(arbiter);
        var native = new Native();
        var guard = new Guard { Callback = () => { lease.SignalStop(); if (throws) throw new InvalidOperationException("fixture guard"); }, Allowed = false };
        using var player = new Win32InputPlayer(native, foregroundGuard: guard);
        using var attached = player.AttachHostOperation(lease);
        try { Assert.ThrowsAny<OperationCanceledException>(() => player.SendRelativeMove(1, 1)); Assert.Empty(native.Sent); }
        finally { player.ReleaseHostOperationInputs(lease); await lease.CompleteAsync(true); }
    }

    [Fact]
    public async Task StopDuringBorrowedPhysicalProbeDoesNotCommitZeroInput()
    {
        var arbiter = new HostInputOperationArbiter(); var lease = Acquire(arbiter);
        var native = new Native { Held = true, Probe = lease.SignalStop };
        using var player = new Win32InputPlayer(native);
        using var attached = player.AttachHostOperation(lease);
        using var control = new MacroController();
        await using var session = new PlaybackSessionContext(control);
        session.BindHostOperation(lease);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await player.EmitSessionInputAsync(session, Down, false, default));
            Assert.Empty(native.Sent);
            player.ReleaseHostOperationInputs(lease);
            Assert.Empty(native.Sent); // No borrowed marker/owned Down was committed.
        }
        finally { await lease.CompleteAsync(true); }
    }

    [Fact]
    public async Task SuppressedAbsoluteMoveStillChecksCapturedStopAfterPreparation()
    {
        var arbiter = new HostInputOperationArbiter(); var lease = Acquire(arbiter);
        var native = new Native { Suppress = true, Suppression = lease.SignalStop };
        using var player = new Win32InputPlayer(native);
        using var attached = player.AttachHostOperation(lease);
        using var control = new MacroController();
        await using var session = new PlaybackSessionContext(control); session.BindHostOperation(lease);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await player.EmitSessionInputAsync(session, new(InputEventType.MouseMove, 0, 2, 2, 0), false, default));
            Assert.Empty(native.Sent);
        }
        finally { player.ReleaseHostOperationInputs(lease); await lease.CompleteAsync(true); }
    }

    [Fact]
    public async Task ManagedDetachGapCannotBecomeUnscopedInput()
    {
        var arbiter = new HostInputOperationArbiter(); var lease = Acquire(arbiter);
        var native = new Native(); using var player = new Win32InputPlayer(native);
        player.AttachHostOperation(lease).Dispose();
        Assert.ThrowsAny<OperationCanceledException>(() => player.SendRelativeMove(1, 1));
        Assert.ThrowsAny<OperationCanceledException>(() => player.SendRelativeMove(0, 0));
        Assert.ThrowsAny<OperationCanceledException>(() => player.SendClick());
        Assert.Empty(native.Sent);
        await lease.CompleteAsync(true);
    }

    [Fact]
    public async Task UnboundOldContextCannotInheritNewAttachment()
    {
        var arbiter = new HostInputOperationArbiter(); var lease = Acquire(arbiter);
        var native = new Native(); using var player = new Win32InputPlayer(native);
        using var attached = player.AttachHostOperation(lease);
        using var control = new MacroController();
        await using var old = new PlaybackSessionContext(control);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await player.EmitSessionInputAsync(old, Down, false, default));
        Assert.Empty(native.Sent);
        await lease.CompleteAsync(true);
    }

    [Fact]
    public async Task OldBoundContextCannotBorrowReplacementLeaseOrReleaseItsHeldInput()
    {
        var arbiter = new HostInputOperationArbiter(); var oldLease = Acquire(arbiter);
        var native = new Native(); using var player = new Win32InputPlayer(native);
        var oldAttachment = player.AttachHostOperation(oldLease);
        using var oldControl = new MacroController(); await using var old = new PlaybackSessionContext(oldControl);
        old.BindHostOperation(oldLease);
        oldAttachment.Dispose(); await oldLease.CompleteAsync(true);
        var current = Acquire(arbiter); using var attachment = player.AttachHostOperation(current);
        using var control = new MacroController(); await using var session = new PlaybackSessionContext(control);
        session.BindHostOperation(current);
        try
        {
            await player.EmitSessionInputAsync(session, Down, false, default);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await player.EmitSessionInputAsync(old, Up, true, default));
            Assert.ThrowsAny<OperationCanceledException>(() => player.ReleaseHostOperationInputs(oldLease));
            Assert.Throws<InvalidOperationException>(player.ReleaseAllHeldKeys);
            Assert.Single(native.Sent);
            player.ReleaseHostOperationInputs(current);
            Assert.Equal(2, native.Sent.Count);
        }
        finally { player.ReleaseHostOperationInputs(current); await current.CompleteAsync(true); }
    }

    [Fact]
    public async Task DisposingAnOlderAttachmentTokenDoesNotDetachTheNewTokenForSameLease()
    {
        var arbiter = new HostInputOperationArbiter(); var lease = Acquire(arbiter);
        var native = new Native(); using var player = new Win32InputPlayer(native);
        var first = player.AttachHostOperation(lease);
        using var second = player.AttachHostOperation(lease);
        first.Dispose(); first.Dispose();
        player.SendRelativeMove(1, 1);
        Assert.Single(native.Sent);
        player.ReleaseHostOperationInputs(lease); await lease.CompleteAsync(true);
    }

    [Fact]
    public async Task CapturedDirectCallCannotAdoptReplacementInstalledDuringPreparation()
    {
        var arbiter = new HostInputOperationArbiter(); var old = Acquire(arbiter);
        var native = new Native(); using var player = new Win32InputPlayer(native);
        var oldAttachment = player.AttachHostOperation(old);
        HostInputOperationLease? replacement = null; IDisposable? attachment = null;
        // Completing here is deliberately hostile caller behavior; it must still never grant
        // the already-entered old call the new operation's authority.
        native.Metrics = () =>
        {
            oldAttachment.Dispose(); old.CompleteAsync(true).GetAwaiter().GetResult();
            replacement = Acquire(arbiter); attachment = player.AttachHostOperation(replacement);
        };
        try
        {
            Assert.ThrowsAny<OperationCanceledException>(() => player.SendClick(x: 10, y: 10));
            Assert.Empty(native.Sent);
            player.SendRelativeMove(1, 1); Assert.Single(native.Sent);
        }
        finally
        {
            if (replacement is not null) { player.ReleaseHostOperationInputs(replacement); await replacement.CompleteAsync(true); }
            attachment?.Dispose(); oldAttachment.Dispose();
        }
    }

    [Fact]
    public async Task StoppedCurrentLeaseStillOwnsMatchingUpCleanup()
    {
        var arbiter = new HostInputOperationArbiter(); var lease = Acquire(arbiter);
        var native = new Native(); using var player = new Win32InputPlayer(native);
        using var attached = player.AttachHostOperation(lease);
        using var control = new MacroController(); await using var session = new PlaybackSessionContext(control); session.BindHostOperation(lease);
        await player.EmitSessionInputAsync(session, Down, false, default);
        lease.SignalStop(); player.ReleaseHostOperationInputs(lease);
        Assert.Equal(2, native.Sent.Count);
        Assert.Equal(NativeMethods.KEYEVENTF_KEYUP, native.Sent[1].ui.ki.dwFlags & NativeMethods.KEYEVENTF_KEYUP);
        await lease.CompleteAsync(true);
    }

    [Fact]
    public async Task QuarantinedCleanupCanReattachExactRetiredOwnerAndExplicitlyRecover()
    {
        var arbiter = new HostInputOperationArbiter(); var lease = Acquire(arbiter);
        var native = new Native(); using var player = new Win32InputPlayer(native);
        var attachment = player.AttachHostOperation(lease);
        using var control = new MacroController(); await using var session = new PlaybackSessionContext(control); session.BindHostOperation(lease);
        await player.EmitSessionInputAsync(session, Down, false, default);
        native.Accept = false;
        Assert.Throws<InvalidOperationException>(() => player.ReleaseHostOperationInputs(lease));
        attachment.Dispose(); await lease.CompleteAsync(false);
        Assert.True(arbiter.Snapshot.IsQuarantined);
        Assert.False(lease.CanContinue); Assert.True(lease.OwnsCleanup);
        native.Accept = true;
        Assert.True(await arbiter.TryRecoverAsync(_ =>
        {
            using var recovery = player.AttachHostOperation(lease);
            player.ReleaseHostOperationInputs(lease); return Task.CompletedTask;
        }));
        Assert.False(arbiter.Snapshot.IsBusy); Assert.Equal(2, native.Sent.Count);
    }

    [Fact]
    public async Task APlayerCannotBeOptedIntoAnUnrelatedArbiterAfterDetach()
    {
        var arbiter = new HostInputOperationArbiter(); var lease = Acquire(arbiter);
        var foreign = Acquire(new HostInputOperationArbiter());
        using var player = new Win32InputPlayer(new Native());
        player.AttachHostOperation(lease).Dispose(); await lease.CompleteAsync(true);
        Assert.Throws<InvalidOperationException>(() => player.AttachHostOperation(foreign));
        await foreign.CompleteAsync(true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationFailureCannotMaskTheCapturedOperationStop(bool physicalProbe)
    {
        var arbiter = new HostInputOperationArbiter(); var lease = Acquire(arbiter);
        Action failure = () => { lease.SignalStop(); throw new InvalidOperationException("fixture preparation failure"); };
        var native = new Native();
        if (physicalProbe) native.Probe = failure; else native.Metrics = failure;
        using var player = new Win32InputPlayer(native); using var attached = player.AttachHostOperation(lease);
        Assert.ThrowsAny<OperationCanceledException>(() => player.SendClick(x: physicalProbe ? -1 : 1, y: physicalProbe ? -1 : 2));
        Assert.Empty(native.Sent); player.ReleaseHostOperationInputs(lease); await lease.CompleteAsync(true);
    }

    [Fact]
    public async Task RealSendFailureRemainsFailureWhenTheNativeAttemptAlsoStopsTheOwner()
    {
        var arbiter = new HostInputOperationArbiter(); var lease = Acquire(arbiter);
        var native = new Native { Accept = false, OnSend = lease.SignalStop };
        using var player = new Win32InputPlayer(native); using var attached = player.AttachHostOperation(lease);
        Assert.Throws<InvalidOperationException>(() => player.SendRelativeMove(1, 1));
        Assert.Empty(native.Sent); native.Accept = true; player.ReleaseHostOperationInputs(lease); await lease.CompleteAsync(true);
    }

    [Fact]
    public async Task RejectedCompletedContextCannotConfigureANeverManagedPlayer()
    {
        var old = Acquire(new HostInputOperationArbiter());
        using var control = new MacroController();
        await using var session = new PlaybackSessionContext(control); session.BindHostOperation(old);
        await old.CompleteAsync(true);
        var native = new Native(); using var player = new Win32InputPlayer(native);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await player.EmitSessionInputAsync(session, Down, false, default));
        Assert.Empty(native.Sent);
        var current = Acquire(new HostInputOperationArbiter());
        using var attachment = player.AttachHostOperation(current);
        player.SendRelativeMove(1, 2); Assert.Single(native.Sent);
        player.ReleaseHostOperationInputs(current); await current.CompleteAsync(true);
    }

    private sealed class Guard : IForegroundInputGuard
    {
        public Action? Callback; public bool Allowed = true;
        public bool TryAllowSynthesizedInput(out string? refusalReason)
        { Interlocked.Exchange(ref Callback, null)?.Invoke(); refusalReason = "fixture refusal"; return Allowed; }
    }

    private sealed class Native : INativeInputSink
    {
        public List<NativeMethods.INPUT> Sent { get; } = [];
        public Action? Probe, Metrics, Suppression, OnSend;
        public bool Held, Suppress, Accept = true;
        public uint Send(NativeMethods.INPUT[] inputs, int count)
        { Interlocked.Exchange(ref OnSend, null)?.Invoke(); if (!Accept) return 0; for (int i = 0; i < count; i++) Sent.Add(inputs[i]); return (uint)count; }
        public bool IsPressed(int key) { Interlocked.Exchange(ref Probe, null)?.Invoke(); return Held; }
        public bool SuppressAbsoluteMoves { get { Interlocked.Exchange(ref Suppression, null)?.Invoke(); return Suppress; } }
        public (int X, int Y) CursorPosition => (10, 10);
        public (int X, int Y, int Width, int Height) VirtualScreen
        { get { Interlocked.Exchange(ref Metrics, null)?.Invoke(); return (0, 0, 100, 100); } }
    }
}
