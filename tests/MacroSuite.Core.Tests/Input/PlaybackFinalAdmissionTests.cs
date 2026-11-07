using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Reflection;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using Xunit;

namespace MacroSuite.Core.Tests.Input;

// Domain targets and sinks only; these checks perform no native input or desktop queries.
public sealed class PlaybackFinalAdmissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static InputEvent Key(bool down, int code = 65) => new(down ? InputEventType.KeyDown : InputEventType.KeyUp, 0, 0, 0, code);

    [Theory]
    [InlineData("pause", PlaybackInputAdmission.Paused)]
    [InlineData("stop", PlaybackInputAdmission.Stopped)]
    [InlineData("panic", PlaybackInputAdmission.Stopped)]
    [InlineData("cancel", PlaybackInputAdmission.Stopped)]
    public async Task FinalAdmissionRechecksControllerAndCallerAfterTheFreshQuery(string mutation, PlaybackInputAdmission expected)
    {
        using var control = new MacroController();
        using var caller = new CancellationTokenSource();
        using var targets = new Targets();
        await using var session = new PlaybackSessionContext(control, targets, targets.Target);
        targets.OnInspect = () =>
        {
            if (mutation == "pause") control.PauseAsync().GetAwaiter().GetResult();
            if (mutation == "stop") control.StopAsync().GetAwaiter().GetResult();
            if (mutation == "panic") control.Panic();
            if (mutation == "cancel") caller.Cancel();
        };
        Assert.Equal(expected, session.InspectFinalInputAdmission(caller.Token));
        Assert.Equal(1, targets.Inspections);
    }

    [Fact]
    public async Task InspectionFailureIsFixedUnavailableAndHasNoPauseOrNotificationSideEffects()
    {
        using var control = new MacroController();
        using var targets = new Targets { OnInspect = () => throw new IOException("synthetic-private-path-and-ticket") };
        await using var session = new PlaybackSessionContext(control, targets, targets.Target);
        int notified = 0;
        session.TargetPaused += (_, _) => notified++;
        Assert.Equal(PlaybackInputAdmission.TargetUnavailable, session.InspectFinalInputAdmission());
        Assert.False(control.IsPaused);
        Assert.Equal(0, notified);
        var error = new PlaybackInputDeferredException(PlaybackInputAdmission.TargetUnavailable);
        Assert.DoesNotContain("synthetic-private", error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StrictMissingVerificationFailsClosedWhileLegacyAndScreenRemainUsable(bool missingWindow)
    {
        using var control = new MacroController();
        using var targets = new Targets();
        var target = missingWindow ? targets.Target with { Window = null } : targets.Target;
        await using var strict = new PlaybackSessionContext(control, missingWindow ? targets : null, target);
        Assert.Equal(PlaybackInputAdmission.TargetUnavailable, strict.InspectFinalInputAdmission());
        await using var legacy = new PlaybackSessionContext(control);
        Assert.Equal(PlaybackInputAdmission.Allowed, legacy.InspectFinalInputAdmission());
        await using var screen = new PlaybackSessionContext(control, target: new(new() { Mode = MacroTargetMode.Screen }, null));
        Assert.Equal(PlaybackInputAdmission.Allowed, screen.InspectFinalInputAdmission());
    }

    [Fact]
    public async Task ActualOwnerAndItsCopiedBatchOptionsReachOrdinaryRestoreAndCleanup()
    {
        using var control = new MacroController();
        using var targets = new Targets();
        await using var session = new PlaybackSessionContext(control, targets, targets.Target);
        var sink = new Sink();
        var options = new PlaybackOptions
        {
            Session = session, SpeedMultiplier = 1.5, RepeatCount = 1, TagSynthesizedEvents = false,
            SynthesizedEventTag = 123, InterpolateMouseMoves = false, MouseInterpolationStepMs = 4,
            UseWindowRelativeCoordinates = true, TargetWindowHandle = targets.Target.Window!.Handle,
            RecordedClientWidth = 80, RecordedClientHeight = 60
        };
        await PlaybackSessionRunner.RunAsync([Key(true)], options, session, sink, default);
        Assert.NotSame(options, session.InputOptions);
        Assert.Null(session.InputOptions.Session);
        Assert.Equal(1.5, session.InputOptions.SpeedMultiplier);
        Assert.False(session.InputOptions.TagSynthesizedEvents);
        Assert.Equal((uint)123, session.InputOptions.SynthesizedEventTag);
        Assert.False(session.InputOptions.InterpolateMouseMoves);
        Assert.Equal(4, session.InputOptions.MouseInterpolationStepMs);
        Assert.Equal(80, session.InputOptions.RecordedClientWidth);
        Assert.Equal(60, session.InputOptions.RecordedClientHeight);
        await control.PauseAsync();
        await session.AwaitReleasedAsync().WaitAsync(Deadline);
        Assert.True(await session.TryResumeAsync(true).WaitAsync(Deadline));
        await session.EmitAsync(Key(false));
        Assert.All(sink.Events, item =>
        {
            Assert.Same(session, item.Owner);
            Assert.Same(session.InputOptions, item.Options);
        });
        Assert.Equal(0, sink.LegacyCalls);
        Assert.Equal(new[] { false, true, false, false }, sink.Events.Select(item => item.Cleanup));
    }

    [Fact]
    public async Task MixedBatchesCanReplaceOptionsAndAnotherDirectContextKeepsDefaults()
    {
        using var control = new MacroController();
        await using var session = new PlaybackSessionContext(control);
        var sink = new Sink();
        await PlaybackSessionRunner.RunAsync([], new() { TargetWindowHandle = (nint)10, RecordedClientWidth = 100,
            RecordedClientHeight = 200, SynthesizedEventTag = 17 }, session, sink, default);
        var first = session.InputOptions;
        await PlaybackSessionRunner.RunAsync([], new() { TargetWindowHandle = 0, UseWindowRelativeCoordinates = false,
            SynthesizedEventTag = 18 }, session, sink, default);
        Assert.NotSame(first, session.InputOptions);
        Assert.Equal((nint)10, first.TargetWindowHandle);
        Assert.Equal((nint)0, session.InputOptions.TargetWindowHandle);
        Assert.Equal(0, session.InputOptions.RecordedClientWidth);
        Assert.Equal((uint)18, session.InputOptions.SynthesizedEventTag);
        using var otherControl = new MacroController();
        await using var other = new PlaybackSessionContext(otherControl);
        other.BindSink(sink);
        await other.EmitAsync(new(InputEventType.MouseMove, 0, 12, 34, 0));
        var observed = Assert.Single(sink.Events);
        Assert.Same(other, observed.Owner);
        Assert.Equal((nint)0, observed.Options.TargetWindowHandle);
        Assert.Equal(0xBEEFCAFEu, observed.Options.SynthesizedEventTag);
        Assert.Null(observed.Options.Session);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignSessionOrWindowOptionsFailBeforeAnyEvent(bool foreignSession)
    {
        using var control = new MacroController();
        using var targets = new Targets();
        await using var session = new PlaybackSessionContext(control, targets, targets.Target);
        using var otherControl = new MacroController();
        await using var other = new PlaybackSessionContext(otherControl);
        var sink = new Sink();
        var options = new PlaybackOptions { Session = foreignSession ? other : session,
            TargetWindowHandle = foreignSession ? targets.Target.Window!.Handle : (nint)20 };
        var error = await Assert.ThrowsAsync<ArgumentException>(() => PlaybackSessionRunner.RunAsync([Key(true)], options, session, sink, default));
        Assert.DoesNotContain("0x", error.Message);
        Assert.Empty(sink.Events);
        Assert.Same(PlaybackOptions.Default, session.InputOptions);
    }

    [Fact]
    public async Task LatePauseLeavesTheUnsentEventPendingUntilExplicitResume()
    {
        using var control = new MacroController();
        await using var session = new PlaybackSessionContext(control);
        var refused = Gate();
        int once = 0;
        var sink = new Sink { Before = async (_, _, cleanup) =>
        {
            if (!cleanup && Interlocked.Exchange(ref once, 1) == 0)
            {
                await control.PauseAsync();
                refused.TrySetResult();
                throw new PlaybackInputDeferredException(PlaybackInputAdmission.Paused);
            }
        }};
        session.BindSink(sink);
        var pending = session.EmitAsync(Key(true)).AsTask();
        try
        {
            await refused.Task.WaitAsync(Deadline);
            await session.AwaitReleasedAsync().WaitAsync(Deadline);
            Assert.False(pending.IsCompleted);
            Assert.Empty(sink.Events);
            Assert.True(await session.TryResumeAsync(true).WaitAsync(Deadline));
            await pending.WaitAsync(Deadline);
            await session.EmitAsync(Key(false));
            Assert.Equal(new[] { InputEventType.KeyDown, InputEventType.KeyUp }, sink.Events.Select(item => item.Input.Type));
        }
        finally
        {
            await control.StopAsync();
            try { await pending.WaitAsync(Deadline); } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task PartialRestorationDeferralCompletesThenReleasesOnlyItsAcceptedPrefix()
    {
        using var control = new MacroController();
        await using var session = new PlaybackSessionContext(control);
        var sink = new Sink(); session.BindSink(sink);
        await session.EmitAsync(Key(true, 65)); await session.EmitAsync(Key(true, 66));
        await control.PauseAsync(); await session.AwaitReleasedAsync().WaitAsync(Deadline);
        int refused = 0;
        sink.Before = (_, input, cleanup) =>
        {
            if (!cleanup && input.Type == InputEventType.KeyDown && input.Data == 66 && Interlocked.Exchange(ref refused, 1) == 0)
                throw new PlaybackInputDeferredException(PlaybackInputAdmission.TargetUnavailable);
            return ValueTask.CompletedTask;
        };
        await control.ResumeAsync();
        await session.AwaitReleasedAsync().WaitAsync(Deadline);
        Assert.True(control.IsPaused);
        Assert.Equal(new[] { 65, 66, 65, 66, 65, 65 }, sink.Events.Select(item => item.Input.Data));
        Assert.Equal(new[] { 65, 66, 65 }, sink.Events.Where(item => item.Cleanup).Select(item => item.Input.Data));
        await JoinPauseWorkAsync(session);
        Assert.True(await session.TryResumeAsync(true).WaitAsync(Deadline));
        await session.EmitAsync(Key(false, 65)); await session.EmitAsync(Key(false, 66));
        Assert.Equal(new[] { 65, 66, 65, 66, 65, 65, 65, 66, 65, 66 }, sink.Events.Select(item => item.Input.Data));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopDuringRestorationIsANormalTransitionOutcomeAndDoesNotResubmit(bool panic)
    {
        using var control = new MacroController();
        await using var session = new PlaybackSessionContext(control);
        var sink = new Sink(); session.BindSink(sink);
        await session.EmitAsync(Key(true)); await control.PauseAsync(); await session.AwaitReleasedAsync();
        sink.Before = (_, _, cleanup) =>
        {
            if (!cleanup) { if (panic) control.Panic(); else control.StopAsync().GetAwaiter().GetResult(); }
            return ValueTask.CompletedTask;
        };
        await control.ResumeAsync();
        await session.AwaitReleasedAsync().WaitAsync(Deadline);
        Assert.Equal(new[] { InputEventType.KeyDown, InputEventType.KeyUp }, sink.Events.Select(item => item.Input.Type));
        Assert.False(await session.TryResumeAsync(true));
    }

    [Fact]
    public async Task GenuineRestoreFailureDoesNotSkipKnownHoldCleanupOrDisappearOnRepeatedDispose()
    {
        using var control = new MacroController();
        var session = new PlaybackSessionContext(control);
        var sink = new Sink(); session.BindSink(sink);
        var failure = new InvalidOperationException("Synthetic genuine restore failure");
        try
        {
            await session.EmitAsync(Key(true, 65)); await session.EmitAsync(Key(true, 66));
            await control.PauseAsync(); await session.AwaitReleasedAsync();
            sink.Before = (_, input, cleanup) =>
            {
                if (!cleanup && input.Data == 66) throw failure;
                return ValueTask.CompletedTask;
            };
            await control.ResumeAsync();
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => session.AwaitReleasedAsync()));
            await control.StopAsync();
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => session.AwaitReleasedAsync()));
            Assert.Equal(new[] { 65, 66, 65 }, sink.Events.Where(item => item.Cleanup).Select(item => item.Input.Data));
        }
        finally
        {
            Task first = session.DisposeAsync().AsTask();
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => first));
            Task second = session.DisposeAsync().AsTask();
            Assert.Same(first, second);
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => second));
        }
    }

    [Fact]
    public async Task CleanupFailureAttemptsEveryHoldAndRemainsObservableOnRepeatedDispose()
    {
        using var control = new MacroController();
        var session = new PlaybackSessionContext(control);
        var sink = new Sink(); session.BindSink(sink);
        await session.EmitAsync(Key(true, 65)); await session.EmitAsync(Key(true, 66));
        var attempts = new ConcurrentQueue<int>();
        var failure = new IOException("Synthetic cleanup failure");
        sink.Before = (_, input, cleanup) =>
        {
            if (cleanup) { attempts.Enqueue(input.Data); if (input.Data == 65) throw failure; }
            return ValueTask.CompletedTask;
        };
        Task first = session.DisposeAsync().AsTask();
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => first));
        Assert.Equal(new[] { 65, 66 }, attempts);
        Assert.Equal(66, Assert.Single(sink.Events.Where(item => item.Cleanup)).Input.Data);
        Assert.Same(first, session.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<IOException>(() => session.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task PendingTargetPauseBlocksAdmissionEvenAfterTargetRefocusesBeforeItsWorkerRuns()
    {
        using var control = new MacroController(); using var targets = new Targets();
        await using var session = new PlaybackSessionContext(control, targets, targets.Target);
        var queued = new QueuedContext();
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(queued);
            targets.PublishOtherForeground();
            targets.PublishPinnedForeground();
            Assert.False(control.IsPaused);
            Assert.Equal(PlaybackInputAdmission.TargetUnavailable, session.InspectFinalInputAdmission());
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); queued.Drain(); }
        await session.AwaitReleasedAsync().WaitAsync(Deadline);
        Assert.True(control.IsPaused);
        await JoinPauseWorkAsync(session);
        Assert.True(await session.TryResumeAsync(true).WaitAsync(Deadline));
    }

    [Fact]
    public async Task TargetPauseObserverCanAwaitReleaseButReentrantResumeIsRefusedWithoutSelfWaiting()
    {
        using var control = new MacroController(); using var targets = new Targets();
        await using var session = new PlaybackSessionContext(control, targets, targets.Target);
        var sink = new Sink(); session.BindSink(sink);
        await session.EmitAsync(Key(true));
        var observer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TargetPaused += async (_, _) =>
        {
            try
            {
                await session.AwaitReleasedAsync().WaitAsync(Deadline);
                observer.TrySetResult(await session.TryResumeAsync(true).WaitAsync(Deadline));
            }
            catch (Exception error) { observer.TrySetException(error); }
        };
        targets.PublishOtherForeground(); targets.PublishPinnedForeground();
        Assert.False(await observer.Task.WaitAsync(Deadline));
        await JoinPauseWorkAsync(session);
        Assert.True(control.IsPaused);
        Assert.True(await session.TryResumeAsync(true).WaitAsync(Deadline));
        await session.EmitAsync(Key(false));
        Assert.Equal(new[] { InputEventType.KeyDown, InputEventType.KeyUp, InputEventType.KeyDown, InputEventType.KeyUp },
            sink.Events.Select(item => item.Input.Type));
        Assert.False(control.IsPaused);
    }

    [Fact]
    public async Task RawResumeInsideNotificationCannotRestoreOrCreateAWorkerChainSelfWait()
    {
        using var control = new MacroController(); using var targets = new Targets();
        await using var session = new PlaybackSessionContext(control, targets, targets.Target);
        var sink = new Sink(); session.BindSink(sink);
        await session.EmitAsync(Key(true));
        var observer = Gate();
        session.TargetPaused += (_, _) =>
        {
            try
            {
                control.ResumeAsync().GetAwaiter().GetResult();
                // Reproduce the known notification -> resume -> restore -> second pause cycle.
                session.AwaitReleasedAsync().WaitAsync(Deadline).GetAwaiter().GetResult();
                Assert.True(control.IsPaused);
                observer.TrySetResult();
            }
            catch (Exception error) { observer.TrySetException(error); }
        };
        targets.PublishOtherForeground(); targets.PublishPinnedForeground();
        await observer.Task.WaitAsync(Deadline);
        await JoinPauseWorkAsync(session);
        Assert.Equal(new[] { InputEventType.KeyDown, InputEventType.KeyUp }, sink.Events.Select(item => item.Input.Type));
        Assert.True(await session.TryResumeAsync(true).WaitAsync(Deadline));
        await session.EmitAsync(Key(false));
    }

    [Theory]
    [InlineData(PlaybackInputAdmission.Allowed)]
    [InlineData(PlaybackInputAdmission.Stopped)]
    [InlineData((PlaybackInputAdmission)99)]
    public void DeferredExceptionCannotPretendAllowedOrStoppedInputWasAManualPause(PlaybackInputAdmission admission)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new PlaybackInputDeferredException(admission));

    private static Task JoinPauseWorkAsync(PlaybackSessionContext session)
    {
        // Join the actual known notification tail before simulating a later, separate manual Resume.
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var notification = (Task)typeof(PlaybackSessionContext).GetField("_targetPauseWorker", flags)!.GetValue(session)!;
        var pause = (Task)typeof(PlaybackSessionContext).GetField("_targetPauseControl", flags)!.GetValue(session)!;
        return Task.WhenAll(notification, pause).WaitAsync(Deadline);
    }

    private sealed record Observed(PlaybackSessionContext Owner, InputEvent Input, bool Cleanup, PlaybackOptions Options);
    private sealed class Sink : IPlaybackSessionBoundSink
    {
        public ConcurrentQueue<Observed> Events { get; } = new();
        public Func<PlaybackSessionContext, InputEvent, bool, ValueTask>? Before;
        public int LegacyCalls;
        public ValueTask EmitSessionInputAsync(InputEvent input, bool cleanup, CancellationToken ct)
        { Interlocked.Increment(ref LegacyCalls); throw new InvalidOperationException("Actual owner must be supplied."); }
        public async ValueTask EmitSessionInputAsync(PlaybackSessionContext owner, InputEvent input, bool cleanup, CancellationToken ct)
        {
            if (Before is { } before) await before(owner, input, cleanup);
            if (!cleanup)
            {
                var admission = owner.InspectFinalInputAdmission(ct);
                if (admission == PlaybackInputAdmission.Stopped) throw new OperationCanceledException(ct);
                if (admission != PlaybackInputAdmission.Allowed) throw new PlaybackInputDeferredException(admission);
            }
            Events.Enqueue(new(owner, input, cleanup, owner.InputOptions));
        }
    }
    private sealed class Targets : ITargetWindowService
    {
        public bool SupportsWindowTargeting => true;
        private static readonly TargetWindowInfo Window = new((nint)10, 1, 100, "application", "synthetic", new(0, 0, 100, 100), 1);
        public ResolvedMacroTarget Target { get; } = new(new() { Mode = MacroTargetMode.SpecificWindow }, Window);
        public TargetWindowSnapshot Snapshot { get; private set; } = new(Window, Window, 1, 1, true);
        public event EventHandler? Changed;
        public Action? OnInspect;
        public int Inspections;
        public Task<IReadOnlyList<TargetWindowInfo>> EnumerateAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TargetWindowInfo>>([Window]);
        public ResolvedMacroTarget Resolve(MacroTargetRule rule, TargetWindowInfo? preferredExternal = null) => Target;
        public TargetValidation Inspect(ResolvedMacroTarget target)
        {
            Interlocked.Increment(ref Inspections); OnInspect?.Invoke();
            return new(true, Snapshot.Foreground?.Handle == Window.Handle, false, Window.ClientBounds, 1);
        }
        public void PublishOtherForeground()
        { Snapshot = Snapshot with { Foreground = Window with { Handle = (nint)20, ProcessId = 2 } }; Changed?.Invoke(this, EventArgs.Empty); }
        public void PublishPinnedForeground()
        { Snapshot = Snapshot with { Foreground = Window }; Changed?.Invoke(this, EventArgs.Empty); }
        public void RegisterOwnWindow(IntPtr window) { }
        public bool TryActivate(ResolvedMacroTarget target) => false;
        public void Dispose() { }
    }
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Enqueue((callback, state));
        public void Drain() { while (_queue.TryDequeue(out var work)) work.Callback(work.State); }
    }
}
