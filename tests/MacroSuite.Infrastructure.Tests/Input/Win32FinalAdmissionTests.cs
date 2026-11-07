using System.Collections.Concurrent;
using System.Drawing;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.Input;
using MacroSuite.Infrastructure.Windows;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Input;

// All desktop state and native submissions are memory-only. No hooks, input or windows are opened.
public sealed class Win32FinalAdmissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static InputEvent LeftDown => new(InputEventType.MouseDown, 0, 10, 10, (int)MouseButton.Left);
    private static InputEvent LeftUp => new(InputEventType.MouseUp, 0, 10, 10, (int)MouseButton.Left);

    [Fact]
    public async Task FocusChangingDuringPhysicalProbeMustNotSubmitToTheWrongForeground()
    {
        using var targets = new Targets();
        var native = new Native();
        using var player = new Win32InputPlayer(native, foregroundGuard: new GenericGuard());
        using var control = new MacroController(player);
        await using var session = new PlaybackSessionContext(control, targets, targets.Pinned);
        using var attachment = session.Attach(player);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        control.StateChanged += (_, _) => { if (control.IsPaused) paused.TrySetResult(); };
        // No Changed event is raised: final fresh inspection must catch a late change even
        // when the cached generic foreground observation still says an external window is safe.
        native.OnPhysicalProbe = () => targets.IsForeground = false;
        Task emission = Task.Run(async () => await session.EmitAsync(LeftDown));
        try
        {
            await Task.WhenAny(emission, paused.Task).WaitAsync(Deadline);
            Assert.Empty(native.Sent);
            Assert.True(control.IsPaused);
            Assert.False(emission.IsCompleted);
        }
        finally { await StopAndJoinAsync(control, session, emission); }
    }

    [Fact]
    public async Task StopDuringFullyBorrowedPhysicalProbeMustNotReportTheUnsentEventCompleted()
    {
        var native = new Native { PhysicallyHeld = true };
        using var player = new Win32InputPlayer(native, foregroundGuard: new GenericGuard());
        using var control = new MacroController(player);
        await using var session = new PlaybackSessionContext(control);
        using var attachment = session.Attach(player);
        Task stopped = Task.CompletedTask;
        native.OnPhysicalProbe = () => stopped = control.StopAsync();
        Task emission = session.EmitAsync(LeftDown).AsTask();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => emission.WaitAsync(Deadline));
            Assert.Empty(native.Sent);
        }
        finally
        {
            await stopped.WaitAsync(Deadline);
            await StopAndJoinAsync(control, session, emission);
        }
    }

    [Fact]
    public async Task PauseDuringFullyBorrowedProbeRetainsTheUnsentDownForOneExplicitResume()
    {
        var native = new Native { PhysicallyHeld = true };
        using var player = new Win32InputPlayer(native, foregroundGuard: new GenericGuard());
        using var control = new MacroController(player);
        await using var session = new PlaybackSessionContext(control);
        using var attachment = session.Attach(player);
        Task pause = Task.CompletedTask;
        native.OnPhysicalProbe = () => pause = control.PauseAsync();
        Task emission = session.EmitAsync(LeftDown).AsTask();
        try
        {
            await pause.WaitAsync(Deadline);
            await session.AwaitReleasedAsync().WaitAsync(Deadline);
            Assert.True(control.IsPaused);
            Assert.Empty(native.Sent);
            Assert.False(emission.IsCompleted);
            native.PhysicallyHeld = false;
            Assert.True(await session.TryResumeAsync(inputIsNeutral: true).WaitAsync(Deadline));
            await emission.WaitAsync(Deadline);
            Assert.Single(native.Sent);
            await session.EmitAsync(LeftUp);
            Assert.Equal(new[] { NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_LEFTUP },
                native.Sent.Select(input => input.ui.mi.dwFlags));
        }
        finally
        {
            await pause.WaitAsync(Deadline);
            await StopAndJoinAsync(control, session, emission);
        }
    }

    [Theory]
    [InlineData("key")]
    [InlineData("unicode")]
    [InlineData("absolute")]
    [InlineData("relative")]
    [InlineData("button")]
    [InlineData("wheel")]
    [InlineData("horizontal-wheel")]
    public async Task EveryOrdinaryEventRechecksPinnedFocusAfterGenericGuard(string kind)
    {
        using var targets = new Targets();
        var native = new Native(); var guard = new GenericGuard();
        using var player = new Win32InputPlayer(native, foregroundGuard: guard);
        using var control = new MacroController(player);
        await using var session = new PlaybackSessionContext(control, targets, targets.Pinned);
        using var attachment = session.Attach(player);
        var paused = WatchPause(control);
        guard.OnCheck = () => targets.IsForeground = false;
        Task emission = Task.Run(async () => await session.EmitAsync(Event(kind)));
        try { await AssertDeferredAsync(control, native, emission, paused.Task); }
        finally { await StopAndJoinAsync(control, session, emission); }
    }

    [Theory]
    [InlineData("probe", "pause")]
    [InlineData("probe", "stop")]
    [InlineData("probe", "panic")]
    [InlineData("probe", "cancel")]
    [InlineData("inspect", "pause")]
    [InlineData("inspect", "stop")]
    [InlineData("inspect", "panic")]
    [InlineData("inspect", "cancel")]
    public async Task StateChangingInsideAProbeOrFinalInspectorCannotAuthorizeThePendingEvent(string boundary, string change)
    {
        using var targets = new Targets();
        var native = new Native(); var guard = new GenericGuard();
        using var player = new Win32InputPlayer(native, foregroundGuard: guard);
        using var control = new MacroController(player);
        using var caller = new CancellationTokenSource();
        await using var session = new PlaybackSessionContext(control, targets, targets.Pinned);
        using var attachment = session.Attach(player);
        var paused = WatchPause(control);
        Task changeTask = Task.CompletedTask;
        bool reached = false;
        void Change()
        {
            reached = true;
            if (change == "pause") changeTask = control.PauseAsync();
            else if (change == "stop") changeTask = control.StopAsync();
            else if (change == "panic") control.Panic();
            else caller.Cancel();
        }
        if (boundary == "probe") native.OnPhysicalProbe = Change;
        else guard.OnCheck = () => targets.OnInspect = Change;
        Task emission = Task.Run(async () => await session.EmitAsync(LeftDown, caller.Token));
        try
        {
            if (change == "pause") await AssertDeferredAsync(control, native, emission, paused.Task);
            else
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => emission.WaitAsync(Deadline));
                Assert.Empty(native.Sent);
            }
            Assert.True(reached);
            await changeTask.WaitAsync(Deadline);
        }
        finally { await StopAndJoinAsync(control, session, emission); }
    }

    [Theory]
    [InlineData("cursor")]
    [InlineData("cursor-outside")]
    [InlineData("coordinates")]
    [InlineData("coordinates-throw")]
    public async Task PointerPreparationCannotHideALaterPinnedFocusChange(string boundary)
    {
        using var targets = new Targets();
        var native = new Native(); var coordinates = new Coordinates();
        using var player = new Win32InputPlayer(native, coordinates, new GenericGuard());
        using var control = new MacroController(player);
        await using var session = new PlaybackSessionContext(control, targets, targets.Pinned);
        using var attachment = session.Attach(player);
        var paused = WatchPause(control);
        Task emission;
        if (boundary.StartsWith("cursor", StringComparison.Ordinal))
        {
            native.OnCursorRead = () =>
            {
                targets.IsForeground = false;
                if (boundary == "cursor-outside") native.Cursor = (200, 200);
            };
            emission = Task.Run(async () => await session.EmitAsync(LeftDown));
        }
        else
        {
            coordinates.OnTranslation = () =>
            {
                targets.IsForeground = false;
                if (boundary == "coordinates-throw") throw new InvalidOperationException("Controlled mapping failure.");
            };
            emission = player.PlayAsync([Event("absolute")], new PlaybackOptions { Session = session,
                TargetWindowHandle = targets.Pinned.Window!.Handle, RecordedClientWidth = 100, RecordedClientHeight = 100 });
        }
        try { await AssertDeferredAsync(control, native, emission, paused.Task); }
        finally { await StopAndJoinAsync(control, session, emission); }
    }

    [Fact]
    public async Task AReusedProcessIncarnationIsRejectedEvenWhenTheWindowHandleAndCachedForegroundMatch()
    {
        using var targets = new Targets(); var native = new Native(); var guard = new GenericGuard();
        using var player = new Win32InputPlayer(native, foregroundGuard: guard);
        using var control = new MacroController(player);
        await using var session = new PlaybackSessionContext(control, targets, targets.Pinned);
        using var attachment = session.Attach(player);
        var paused = WatchPause(control);
        guard.OnCheck = () => targets.SameProcessInstance = false;
        var emission = session.EmitAsync(Event("relative")).AsTask();
        try { await AssertDeferredAsync(control, native, emission, paused.Task); }
        finally { await StopAndJoinAsync(control, session, emission); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuppressedAbsoluteMoveStillChecksItsOwnerBeforeReportingCompletion(bool stop)
    {
        using var targets = new Targets(); var native = new Native { SuppressMoves = true };
        using var player = new Win32InputPlayer(native, foregroundGuard: new GenericGuard());
        using var control = new MacroController(player);
        await using var session = new PlaybackSessionContext(control, targets, targets.Pinned);
        using var attachment = session.Attach(player);
        var paused = WatchPause(control);
        Task stopped = Task.CompletedTask;
        native.OnSuppressionRead = () => { if (stop) stopped = control.StopAsync(); else targets.IsForeground = false; };
        var emission = session.EmitAsync(Event("absolute")).AsTask();
        try
        {
            if (stop) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => emission.WaitAsync(Deadline));
            else await AssertDeferredAsync(control, native, emission, paused.Task);
            Assert.Empty(native.Sent);
        }
        finally { await stopped.WaitAsync(Deadline); await StopAndJoinAsync(control, session, emission); }
    }

    [Fact]
    public async Task ADeferredBorrowedUpDoesNotLoseItsMarkerAndSynthesizeAnUnownedReleaseOnRetry()
    {
        var native = new Native { PhysicallyHeld = true }; var guard = new GenericGuard();
        using var player = new Win32InputPlayer(native, foregroundGuard: guard);
        using var control = new MacroController(player);
        await using var session = new PlaybackSessionContext(control);
        using var attachment = session.Attach(player);
        await session.EmitAsync(LeftDown);
        Assert.Empty(native.Sent);
        Task pause = Task.CompletedTask;
        guard.OnCheck = () => pause = control.PauseAsync();
        var release = session.EmitAsync(LeftUp).AsTask();
        try
        {
            await pause.WaitAsync(Deadline); await session.AwaitReleasedAsync().WaitAsync(Deadline);
            Assert.False(release.IsCompleted);
            native.PhysicallyHeld = false; // The physical user released it during the pause.
            Assert.True(await session.TryResumeAsync(true).WaitAsync(Deadline));
            await release.WaitAsync(Deadline);
            Assert.Empty(native.Sent);
            await session.EmitAsync(LeftDown); await session.EmitAsync(LeftUp);
            Assert.Equal(new[] { NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_LEFTUP },
                native.Sent.Select(input => input.ui.mi.dwFlags));
        }
        finally { await StopAndJoinAsync(control, session, release); }
    }

    [Fact]
    public async Task CleanupIgnoresUnsafeFocusAndReleasesOnlyActuallySubmittedHolds()
    {
        using var targets = new Targets(); var native = new Native(); var guard = new GenericGuard();
        using var player = new Win32InputPlayer(native, foregroundGuard: guard);
        using var control = new MacroController(player);
        await using var session = new PlaybackSessionContext(control, targets, targets.Pinned);
        using var attachment = session.Attach(player);
        native.PhysicallyHeldKeys.Add(2);
        await session.EmitAsync(new(InputEventType.MouseDown, 0, 10, 10, (int)MouseButton.Right));
        await session.EmitAsync(LeftDown);
        targets.IsForeground = false; guard.Allowed = false;
        guard.OnCheck = () => throw new InvalidOperationException("Cleanup consulted the ordinary foreground guard.");
        targets.OnInspect = () => throw new InvalidOperationException("Cleanup inspected an ordinary target.");
        await control.StopAsync(); await session.AwaitReleasedAsync().WaitAsync(Deadline);
        Assert.Equal(new[] { NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_LEFTUP },
            native.Sent.Select(input => input.ui.mi.dwFlags));
        Assert.Empty(native.SubmittedHolds);
        Assert.Contains(2, native.PhysicallyHeldKeys);
    }

    [Fact]
    public async Task AValidOwnerStillObservesAGenuineCoordinateFailure()
    {
        using var targets = new Targets(); var native = new Native(); var coordinates = new Coordinates();
        using var player = new Win32InputPlayer(native, coordinates, new GenericGuard());
        using var control = new MacroController(player);
        await using var session = new PlaybackSessionContext(control, targets, targets.Pinned);
        using var attachment = session.Attach(player);
        coordinates.OnTranslation = () => throw new InvalidOperationException("Controlled mapping failure.");
        var play = player.PlayAsync([Event("absolute")],
            new PlaybackOptions { Session = session, TargetWindowHandle = targets.Pinned.Window!.Handle });
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => play.WaitAsync(Deadline));
            Assert.Equal("Controlled mapping failure.", error.Message);
            Assert.Empty(native.Sent);
        }
        finally { await StopAndJoinFaultedAsync(control, session, play); }
    }

    [Fact]
    public async Task OldExplicitOptionsCannotAuthorizeANewAttachedOwner()
    {
        using var oldTargets = new Targets(123); using var newTargets = new Targets(456);
        var native = new Native(); var guard = new GenericGuard();
        using var player = new Win32InputPlayer(native, new Coordinates(), guard);
        using var oldControl = new MacroController(player); using var newControl = new MacroController(player);
        await using var oldOwner = new PlaybackSessionContext(oldControl, oldTargets, oldTargets.Pinned);
        await using var newOwner = new PlaybackSessionContext(newControl, newTargets, newTargets.Pinned);
        await player.PlayAsync([Event("relative")], new PlaybackOptions { Session = oldOwner,
            TargetWindowHandle = oldTargets.Pinned.Window!.Handle });
        native.Sent.Clear();
        using var attachment = newOwner.Attach(player);
        var paused = WatchPause(newControl);
        guard.OnCheck = () => newTargets.IsForeground = false;
        var emission = newOwner.EmitAsync(Event("relative")).AsTask();
        try
        {
            await AssertDeferredAsync(newControl, native, emission, paused.Task);
            Assert.False(oldControl.IsPaused);
        }
        finally { await StopAndJoinAsync(newControl, newOwner, emission); }
    }

    [Fact]
    public async Task NewAttachedOwnerUsesFreshScreenMetricsAndDefaultMappingRatherThanOldOptions()
    {
        var native = new Native(); var coordinates = new Coordinates();
        using var player = new Win32InputPlayer(native, coordinates, new GenericGuard());
        using var oldControl = new MacroController(player); using var newControl = new MacroController(player);
        await using var oldOwner = new PlaybackSessionContext(oldControl);
        await using var newOwner = new PlaybackSessionContext(newControl);
        await player.PlayAsync([Event("absolute")], new PlaybackOptions { Session = oldOwner,
            TargetWindowHandle = (nint)123, SynthesizedEventTag = 0x11111111 });
        Assert.True(coordinates.Translations > 0);
        int priorTranslations = coordinates.Translations;
        native.Sent.Clear(); native.Screen = (-100, -50, 200, 100);
        using var attachment = newOwner.Attach(player);
        await newOwner.EmitAsync(new(InputEventType.MouseMove, 0, 20, 30, 0));
        var actual = Assert.Single(native.Sent).ui.mi;
        Assert.Equal(priorTranslations, coordinates.Translations);
        Assert.Equal(120 * 65535 / 199, actual.dx);
        Assert.Equal(80 * 65535 / 99, actual.dy);
        Assert.Equal((nuint)PlaybackOptions.Default.SynthesizedEventTag, actual.dwExtraInfo);
    }

    [Fact]
    public async Task DirectAbsoluteInputOnANewPlayerUsesRealFakeDesktopDimensions()
    {
        var native = new Native { Screen = (-100, -50, 200, 100) };
        using var player = new Win32InputPlayer(native, foregroundGuard: new GenericGuard());
        using var control = new MacroController(player);
        await using var session = new PlaybackSessionContext(control);
        using var attachment = session.Attach(player);
        await session.EmitAsync(new(InputEventType.MouseMove, 0, 20, 30, 0));
        var actual = Assert.Single(native.Sent).ui.mi;
        Assert.Equal(120 * 65535 / 199, actual.dx);
        Assert.Equal(80 * 65535 / 99, actual.dy);
    }

    [Fact]
    public async Task BoundAbsoluteMoveSubmitsTheSamePointThatPassedTargetBoundsValidation()
    {
        using var targets = new Targets(); var native = new Native();
        var coordinates = new Coordinates { SecondTranslation = (200, 200) };
        using var player = new Win32InputPlayer(native, coordinates, new GenericGuard());
        using var control = new MacroController(player);
        await using var session = new PlaybackSessionContext(control, targets, targets.Pinned);
        await player.PlayAsync([Event("absolute")], new PlaybackOptions
        {
            Session = session, TargetWindowHandle = targets.Pinned.Window!.Handle
        }).WaitAsync(Deadline);
        Assert.Equal(1, coordinates.Translations);
        var actual = Assert.Single(native.Sent).ui.mi;
        Assert.Equal(50 * 65535 / 99, actual.dx);
        Assert.Equal(40 * 65535 / 99, actual.dy);
    }

    [Fact]
    public async Task OneOwnerCanUseDifferentImmutableMappingAndTagOptionsOnSuccessiveBatches()
    {
        var native = new Native(); var coordinates = new Coordinates();
        using var player = new Win32InputPlayer(native, coordinates, new GenericGuard());
        using var control = new MacroController(player);
        await using var session = new PlaybackSessionContext(control);
        await player.PlayAsync([Event("absolute")], new PlaybackOptions { Session = session,
            TargetWindowHandle = (nint)123, SynthesizedEventTag = 0x11111111 });
        await player.PlayAsync([new(InputEventType.MouseMove, 0, 20, 30, 0)], new PlaybackOptions { Session = session,
            UseWindowRelativeCoordinates = false, SynthesizedEventTag = 0x22222222 });
        var actual = native.Sent.ToArray(); Assert.Equal(2, actual.Length);
        Assert.Equal(50 * 65535 / 99, actual[0].ui.mi.dx);
        Assert.Equal(40 * 65535 / 99, actual[0].ui.mi.dy);
        Assert.Equal((nuint)0x11111111u, actual[0].ui.mi.dwExtraInfo);
        Assert.Equal(20 * 65535 / 99, actual[1].ui.mi.dx);
        Assert.Equal(30 * 65535 / 99, actual[1].ui.mi.dy);
        Assert.Equal((nuint)0x22222222u, actual[1].ui.mi.dwExtraInfo);
    }

    [Fact]
    public async Task StrictTargetCannotBeTranslatedThroughADifferentWindowHandle()
    {
        using var targets = new Targets(); var native = new Native(); var coordinates = new Coordinates();
        using var player = new Win32InputPlayer(native, coordinates, new GenericGuard());
        using var control = new MacroController(player);
        await using var session = new PlaybackSessionContext(control, targets, targets.Pinned);
        var play = player.PlayAsync([Event("absolute")], new PlaybackOptions { Session = session, TargetWindowHandle = (nint)456 });
        try
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() => play.WaitAsync(Deadline));
            Assert.DoesNotContain("456", error.Message);
            Assert.Empty(native.Sent); Assert.Equal(0, coordinates.Translations);
            Assert.False(control.IsPaused);
        }
        finally { await StopAndJoinFaultedAsync(control, session, play); }
    }

    [Fact]
    public async Task PauseDuringRestorationReleasesTheAcceptedPrefixAndRestoresUnfinishedHoldsOnlyOnce()
    {
        var native = new Native(); using var player = new Win32InputPlayer(native, foregroundGuard: new GenericGuard());
        using var control = new MacroController(player);
        await using var session = new PlaybackSessionContext(control); using var attachment = session.Attach(player);
        var rightDown = new InputEvent(InputEventType.MouseDown, 0, 10, 10, (int)MouseButton.Right);
        await session.EmitAsync(LeftDown); await session.EmitAsync(rightDown);
        await control.PauseAsync(); await session.AwaitReleasedAsync().WaitAsync(Deadline);
        Task pauseAgain = Task.CompletedTask;
        native.OnSend = () => pauseAgain = control.PauseAsync();
        try
        {
            Assert.False(await session.TryResumeAsync(true).WaitAsync(Deadline));
            await pauseAgain.WaitAsync(Deadline); await session.AwaitReleasedAsync().WaitAsync(Deadline);
            Assert.Empty(native.SubmittedHolds);
            Assert.Equal(6, native.Sent.Count);
            Assert.True(await session.TryResumeAsync(true).WaitAsync(Deadline));
            Assert.Equal(8, native.Sent.Count);
        }
        finally { await StopAndJoinAsync(control, session, Task.CompletedTask); }
        Assert.Equal(10, native.Sent.Count);
        Assert.Empty(native.SubmittedHolds);
    }

    [Fact]
    public async Task ReentrantTargetPauseCannotPublishReleaseOrResumeBeforeActualCleanupCompletes()
    {
        using var targets = new Targets(); var native = new Native(); var guard = new GenericGuard();
        using var player = new Win32InputPlayer(native, foregroundGuard: guard);
        using var control = new MacroController(player);
        await using var session = new PlaybackSessionContext(control, targets, targets.Pinned);
        using var attachment = session.Attach(player);
        using var allowCleanup = new ManualResetEventSlim(false);
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? released = null; Task<bool>? resumed = null;
        await session.EmitAsync(LeftDown);
        native.BeforeSend = () =>
        {
            cleanupEntered.TrySetResult();
            if (!allowCleanup.Wait(Deadline)) throw new TimeoutException("Controlled cleanup gate was not released.");
        };
        session.TargetPaused += (_, _) =>
        {
            targets.IsForeground = true;
            released = session.AwaitReleasedAsync();
            resumed = session.TryResumeAsync(true);
            callbackEntered.TrySetResult();
        };
        guard.OnCheck = () => targets.IsForeground = false;
        var emission = Task.Run(async () => await session.EmitAsync(Event("relative")));
        try
        {
            await callbackEntered.Task.WaitAsync(Deadline);
            await cleanupEntered.Task.WaitAsync(Deadline);
            Assert.NotNull(released); Assert.False(released!.IsCompleted);
            Assert.NotNull(resumed); Assert.False(await resumed!.WaitAsync(Deadline));
            Assert.False(emission.IsCompleted);
            allowCleanup.Set();
            await released!.WaitAsync(Deadline);
            await session.AwaitReleasedAsync().WaitAsync(Deadline);
            Assert.True(await session.TryResumeAsync(true).WaitAsync(Deadline));
            await emission.WaitAsync(Deadline);
        }
        finally
        {
            allowCleanup.Set();
            try { await StopAndJoinAsync(control, session, emission); }
            finally
            {
                if (released is not null) await released.WaitAsync(Deadline);
                if (resumed is not null)
                    try { await resumed.WaitAsync(Deadline); }
                    catch (OperationCanceledException) { }
            }
        }
        var flags = native.Sent.Select(input => input.ui.mi.dwFlags).ToArray();
        Assert.Equal(2, flags.Count(value => value == NativeMethods.MOUSEEVENTF_LEFTDOWN));
        Assert.Equal(2, flags.Count(value => value == NativeMethods.MOUSEEVENTF_LEFTUP));
        Assert.Single(flags.Where(value => (value & NativeMethods.MOUSEEVENTF_MOVE) != 0));
        Assert.Empty(native.SubmittedHolds);
    }

    [Fact]
    public async Task ARealRestorationFailureStillAllowsStopToReleaseAnAlreadyAcceptedPrefix()
    {
        var native = new Native(); using var player = new Win32InputPlayer(native, foregroundGuard: new GenericGuard());
        using var control = new MacroController(player);
        var session = new PlaybackSessionContext(control); using var attachment = session.Attach(player);
        try
        {
            await session.EmitAsync(LeftDown);
            await session.EmitAsync(new(InputEventType.MouseDown, 0, 10, 10, (int)MouseButton.Right));
            await control.PauseAsync(); await session.AwaitReleasedAsync().WaitAsync(Deadline);
            native.OnSend = () => native.NextAccept = 0; // First restored down succeeds; the following one is rejected.
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.TryResumeAsync(true).WaitAsync(Deadline));
            Assert.Single(native.SubmittedHolds);
            uint restoredPrefix = native.Sent.Last().ui.mi.dwFlags;
            Assert.Contains(restoredPrefix, new[] { NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_RIGHTDOWN });
            await control.StopAsync();
            try { await session.AwaitReleasedAsync().WaitAsync(Deadline); }
            catch (InvalidOperationException) { } // The already-observed genuine restoration error may remain observable.
            Assert.Empty(native.SubmittedHolds);
            uint expectedRelease = restoredPrefix == NativeMethods.MOUSEEVENTF_LEFTDOWN
                ? NativeMethods.MOUSEEVENTF_LEFTUP : NativeMethods.MOUSEEVENTF_RIGHTUP;
            Assert.Equal(expectedRelease, native.Sent.Last().ui.mi.dwFlags);
        }
        finally
        {
            native.NextAccept = int.MaxValue;
            await control.StopAsync();
            try { await session.DisposeAsync().AsTask().WaitAsync(Deadline); }
            catch (InvalidOperationException) { }
            player.ReleaseAllHeldKeys();
        }
    }

    [Fact]
    public async Task DisposalCannotClaimSuccessWhenNativeCleanupWasRejected()
    {
        var native = new Native(); using var player = new Win32InputPlayer(native, foregroundGuard: new GenericGuard());
        using var control = new MacroController(player);
        var session = new PlaybackSessionContext(control); using var attachment = session.Attach(player);
        InvalidOperationException? releaseFailure = null;
        AggregateException? disposalFailure = null;
        try
        {
            await session.EmitAsync(LeftDown);
            native.NextAccept = 0;
            await control.StopAsync();
            releaseFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => session.AwaitReleasedAsync().WaitAsync(Deadline));
            Assert.Single(native.SubmittedHolds);
            Assert.Equal(2, native.SendCalls);
            native.NextAccept = 0;
            Task disposal = session.DisposeAsync().AsTask();
            disposalFailure = await Assert.ThrowsAsync<AggregateException>(() => disposal.WaitAsync(Deadline));
            Assert.Equal(2, disposalFailure.InnerExceptions.Count);
            Assert.Same(releaseFailure, disposalFailure.InnerExceptions[0]);
            var retryFailure = Assert.IsType<InvalidOperationException>(disposalFailure.InnerExceptions[1]);
            Assert.NotSame(releaseFailure, retryFailure);
            const string refused = "Input playback failed: Windows accepted 0 of 1 events. Playback was stopped.";
            Assert.Equal(refused, releaseFailure.Message); Assert.Equal(refused, retryFailure.Message);
            Assert.Equal(3, native.SendCalls);
            Assert.Single(native.SubmittedHolds);
            Task repeated = session.DisposeAsync().AsTask();
            Assert.Same(disposal, repeated);
            var repeatedFailure = await Assert.ThrowsAsync<AggregateException>(() => repeated.WaitAsync(Deadline));
            Assert.Same(disposalFailure, repeatedFailure);
            Assert.Equal(3, native.SendCalls);
            Assert.Single(native.SubmittedHolds);
        }
        finally
        {
            native.NextAccept = int.MaxValue;
            try
            {
                try { await session.DisposeAsync().AsTask().WaitAsync(Deadline); }
                catch (AggregateException error) when (ReferenceEquals(error, disposalFailure)) { }
                catch (InvalidOperationException error) when (ReferenceEquals(error, releaseFailure)) { }
            }
            finally { player.ReleaseAllHeldKeys(); }
        }
        Assert.Empty(native.SubmittedHolds);
        Assert.Equal(new[] { NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_LEFTUP },
            native.Sent.Select(input => input.ui.mi.dwFlags));
    }

    [Theory]
    [InlineData(false, "focus")]
    [InlineData(false, "stop")]
    [InlineData(false, "panic")]
    [InlineData(true, "focus")]
    [InlineData(true, "stop")]
    public async Task GenericGuardRefusalOrFailureCannotMaskANewerOwnerDenial(bool throws, string change)
    {
        using var targets = new Targets(); var native = new Native(); var guard = new GenericGuard { Allowed = false };
        using var player = new Win32InputPlayer(native, foregroundGuard: guard);
        using var control = new MacroController(player);
        await using var session = new PlaybackSessionContext(control, targets, targets.Pinned);
        using var attachment = session.Attach(player);
        var paused = WatchPause(control); Task stopped = Task.CompletedTask;
        guard.OnCheck = () =>
        {
            if (change == "focus") targets.IsForeground = false;
            else if (change == "stop") stopped = control.StopAsync();
            else control.Panic();
            if (throws) throw new InvalidOperationException("Controlled generic guard failure.");
        };
        var emission = Task.Run(async () => await session.EmitAsync(Event("relative")));
        try
        {
            if (change == "focus") await AssertDeferredAsync(control, native, emission, paused.Task);
            else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => emission.WaitAsync(Deadline));
            Assert.Empty(native.Sent);
        }
        finally { await stopped.WaitAsync(Deadline); await StopAndJoinAsync(control, session, emission); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AValidOwnerStillObservesAGenuineGenericGuardRefusalOrFailure(bool throws)
    {
        var native = new Native(); var guard = new GenericGuard { Allowed = false };
        using var player = new Win32InputPlayer(native, foregroundGuard: guard);
        using var control = new MacroController(player);
        await using var session = new PlaybackSessionContext(control); using var attachment = session.Attach(player);
        if (throws) guard.OnCheck = () => throw new InvalidOperationException("Controlled generic guard failure.");
        var emission = session.EmitAsync(Event("relative")).AsTask();
        try
        {
            if (throws)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => emission.WaitAsync(Deadline));
                Assert.Equal("Controlled generic guard failure.", error.Message);
            }
            else await Assert.ThrowsAsync<UnsafeForegroundInputException>(() => emission.WaitAsync(Deadline));
            Assert.Empty(native.Sent); Assert.False(control.IsPaused);
        }
        finally { await StopAndJoinFaultedAsync(control, session, emission); }
    }

    [Fact]
    public async Task ActualNativeSubmissionFailureCannotBeReinterpretedAsDeferredFocusLoss()
    {
        using var targets = new Targets(); var native = new Native { NextAccept = 0 };
        using var player = new Win32InputPlayer(native, foregroundGuard: new GenericGuard());
        using var control = new MacroController(player);
        await using var session = new PlaybackSessionContext(control, targets, targets.Pinned); using var attachment = session.Attach(player);
        native.OnSend = () => targets.IsForeground = false;
        var emission = session.EmitAsync(LeftDown).AsTask();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => emission.WaitAsync(Deadline));
            Assert.Empty(native.Sent); Assert.False(control.IsPaused);
        }
        finally { await StopAndJoinFaultedAsync(control, session, emission); }
    }

    private static InputEvent Event(string kind) => kind switch
    {
        "key" => new(InputEventType.KeyDown, 0, 0, 0, 0x87),
        "unicode" => new(InputEventType.Unicode, 0, 0, 0, 0x03A9),
        "absolute" => new(InputEventType.MouseMove, 0, 10, 10, 0),
        "relative" => new(InputEventType.MouseMoveRelative, 0, 3, 4, 0),
        "button" => LeftDown,
        "wheel" => new(InputEventType.MouseWheel, 0, 10, 10, 120),
        "horizontal-wheel" => new(InputEventType.MouseHWheel, 0, 10, 10, -120),
        _ => throw new ArgumentException("Unknown controlled event.")
    };

    private static TaskCompletionSource WatchPause(MacroController control)
    {
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        control.StateChanged += (_, _) => { if (control.IsPaused) paused.TrySetResult(); };
        return paused;
    }

    private static async Task AssertDeferredAsync(MacroController control, Native native, Task emission, Task paused)
    {
        await Task.WhenAny(emission, paused).WaitAsync(Deadline);
        Assert.Empty(native.Sent);
        Assert.True(control.IsPaused);
        Assert.False(emission.IsCompleted);
    }

    private static async Task StopAndJoinAsync(MacroController control, PlaybackSessionContext session, Task emission)
    {
        await control.StopAsync().WaitAsync(Deadline);
        try { await emission.WaitAsync(Deadline); }
        catch (OperationCanceledException) { }
        await session.AwaitReleasedAsync().WaitAsync(Deadline);
    }

    private static async Task StopAndJoinFaultedAsync(MacroController control, PlaybackSessionContext session, Task emission)
    {
        await control.StopAsync().WaitAsync(Deadline);
        try { await emission.WaitAsync(Deadline); }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or ArgumentException) { }
        await session.AwaitReleasedAsync().WaitAsync(Deadline);
    }

    private sealed class GenericGuard : IForegroundInputGuard
    {
        public Action? OnCheck;
        public bool Allowed = true;
        public bool TryAllowSynthesizedInput(out string? refusalReason)
        {
            Interlocked.Exchange(ref OnCheck, null)?.Invoke();
            refusalReason = Allowed ? null : "Synthetic generic foreground refusal";
            return Allowed;
        }
    }

    private sealed class Native : INativeInputSink
    {
        public ConcurrentQueue<NativeMethods.INPUT> Sent { get; } = new();
        public Action? OnPhysicalProbe;
        public Action? OnCursorRead;
        public Action? OnVirtualScreenRead;
        public Action? OnSuppressionRead;
        public Action? OnSend;
        public Action? BeforeSend;
        public bool PhysicallyHeld;
        public HashSet<int> PhysicallyHeldKeys { get; } = new();
        public HashSet<int> SubmittedHolds { get; } = new();
        public bool SuppressMoves;
        public int SendCalls;
        public int NextAccept = int.MaxValue;
        public (int X, int Y, int Width, int Height) Screen = (0, 0, 100, 100);
        public (int X, int Y) Cursor = (10, 10);
        public bool IsPressed(int virtualKey)
        {
            Interlocked.Exchange(ref OnPhysicalProbe, null)?.Invoke();
            return PhysicallyHeld || PhysicallyHeldKeys.Contains(virtualKey);
        }
        public uint Send(NativeMethods.INPUT[] inputs, int count)
        {
            Interlocked.Exchange(ref BeforeSend, null)?.Invoke();
            Interlocked.Increment(ref SendCalls);
            int accepted = Math.Min(count, Interlocked.Exchange(ref NextAccept, int.MaxValue));
            for (int index = 0; index < accepted; index++)
            {
                var input = inputs[index]; Sent.Enqueue(input);
                if (input.type == NativeMethods.INPUT_MOUSE)
                {
                    uint flags = input.ui.mi.dwFlags;
                    ChangeMouseHold(flags, NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_LEFTUP, 1);
                    ChangeMouseHold(flags, NativeMethods.MOUSEEVENTF_RIGHTDOWN, NativeMethods.MOUSEEVENTF_RIGHTUP, 2);
                    ChangeMouseHold(flags, NativeMethods.MOUSEEVENTF_MIDDLEDOWN, NativeMethods.MOUSEEVENTF_MIDDLEUP, 4);
                }
                else if (input.type == NativeMethods.INPUT_KEYBOARD)
                {
                    int key = (input.ui.ki.dwFlags & NativeMethods.KEYEVENTF_UNICODE) != 0 ? 100000 + input.ui.ki.wScan : input.ui.ki.wVk;
                    if ((input.ui.ki.dwFlags & NativeMethods.KEYEVENTF_KEYUP) != 0) SubmittedHolds.Remove(key);
                    else SubmittedHolds.Add(key);
                }
            }
            Interlocked.Exchange(ref OnSend, null)?.Invoke();
            return (uint)accepted;
        }
        private void ChangeMouseHold(uint flags, uint down, uint up, int key)
        {
            if ((flags & down) != 0) SubmittedHolds.Add(key);
            if ((flags & up) != 0) SubmittedHolds.Remove(key);
        }
        public bool SuppressAbsoluteMoves
        { get { Interlocked.Exchange(ref OnSuppressionRead, null)?.Invoke(); return SuppressMoves; } }
        public (int X, int Y) CursorPosition
        { get { Interlocked.Exchange(ref OnCursorRead, null)?.Invoke(); return Cursor; } }
        public (int X, int Y, int Width, int Height) VirtualScreen
        { get { Interlocked.Exchange(ref OnVirtualScreenRead, null)?.Invoke(); return Screen; } }
    }

    private sealed class Targets : ITargetWindowService
    {
        private readonly TargetWindowInfo _window;
        public Targets() : this((nint)123) { }
        public Targets(nint handle)
            => _window = new(handle, 71, 1, "owned-synthetic.exe", "Synthetic target", new Rectangle(0, 0, 100, 100), 1);
        public bool IsForeground { get; set; } = true;
        public bool SameProcessInstance { get; set; } = true;
        public Action? OnInspect;
        public bool SupportsWindowTargeting => true;
        public ResolvedMacroTarget Pinned => new(new MacroTargetRule { Mode = MacroTargetMode.SpecificWindow,
            ApplicationId = _window.ApplicationId, WindowTitle = _window.Title }, _window);
        public TargetWindowSnapshot Snapshot => new(_window, _window, 1, 1, true);
        public event EventHandler? Changed { add { } remove { } }
        public TargetValidation Inspect(ResolvedMacroTarget target)
        {
            Interlocked.Exchange(ref OnInspect, null)?.Invoke();
            return new(target.Window == _window && SameProcessInstance, IsForeground, false, _window.ClientBounds, 1);
        }
        public Task<IReadOnlyList<TargetWindowInfo>> EnumerateAsync(CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<TargetWindowInfo>>([_window]); }
        public ResolvedMacroTarget Resolve(MacroTargetRule rule, TargetWindowInfo? preferredExternal = null) => new(rule, _window);
        public void RegisterOwnWindow(nint window) { }
        public bool TryActivate(ResolvedMacroTarget target) => false;
        public void Dispose() { }
    }

    private sealed class Coordinates : IWindowCoordinateService
    {
        public Action? OnTranslation;
        public int Translations;
        public (int X, int Y)? SecondTranslation;
        public nint GetWindowAt(int screenX, int screenY) => (nint)123;
        public nint GetForegroundWindow() => (nint)123;
        public (int clientX, int clientY) ScreenToClient(nint window, int x, int y) => (x - 40, y - 30);
        public (int screenX, int screenY) ClientToScreen(nint window, int x, int y)
        {
            Translations++; Interlocked.Exchange(ref OnTranslation, null)?.Invoke();
            return Translations > 1 && SecondTranslation is { } changed ? changed : (x + 40, y + 30);
        }
        public (int width, int height) GetClientSize(nint window) => (100, 100);
        public bool IsWindowValid(nint window) => true;
        public nint FindWindowByTitle(string? titleOrProcessName) => (nint)123;
    }
}
