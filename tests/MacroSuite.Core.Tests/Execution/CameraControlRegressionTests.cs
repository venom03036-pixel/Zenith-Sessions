using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using Xunit;

namespace MacroSuite.Core.Tests.Execution;

public sealed class CameraControlRegressionTests
{
    [Fact]
    public async Task SessionTimelineExcludesPauseThatOccursInsideInstantInputCall()
    {
        using var control = new MacroController();
        await using var session = new PlaybackSessionContext(control);
        var player = new AdditivePlayer();
        player.OnMove = () =>
        {
            if (player.Moves != 1) return;
            control.PauseAsync().GetAwaiter().GetResult(); Thread.Sleep(180); control.ResumeAsync().GetAwaiter().GetResult();
        };
        var context = new MacroContext(new UnusedCapture(), control) { InputPlayer = player, PlaybackSession = session };
        var step = new MouseTrajectoryStep([new(0, 0, 0), new(100, 2, 0), new(200, 3, 0)])
        { Mode = TrajectoryPlaybackMode.PacketReference, Packets = [new(0, 1, 0, 0), new(100, 1, 0, 1), new(200, 1, 0, 2)] };
        var elapsed = Stopwatch.StartNew();
        await Task.Run(() => step.ExecuteAsync(context));
        elapsed.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(330));
        player.X.Should().Be(3);
    }
    private sealed class UnusedCapture : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;
        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class AdditivePlayer : IInputPlayer
    {
        public bool IsPlaying => false;
        public int X, Y, Moves;
        public Action? OnMove;
        public Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default)
            => throw new InvalidOperationException("Camera paths must use relative input only.");
        public void SendRelativeMove(int dx, int dy) => throw new InvalidOperationException("Specify one input backend.");
        public void SendRelativeMove(int dx, int dy, bool dualBackend)
        {
            dualBackend.Should().BeFalse();
            X += dx;
            Y += dy;
            Moves++;
            OnMove?.Invoke();
        }
        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false)
            => throw new InvalidOperationException("Camera paths must not take ownership of the mouse button.");
        public void ReleaseAllHeldKeys() { }
        public void Dispose() { }
    }

    [Theory]
    [InlineData(TrajectoryPlaybackMode.Reconstructed)]
    [InlineData(TrajectoryPlaybackMode.PacketReference)]
    public async Task CameraPlayback_AddsToManualMovement_WithoutRecentering(TrajectoryPlaybackMode mode)
    {
        using var control = new MacroController();
        var player = new AdditivePlayer();
        player.OnMove = () =>
        {
            if (player.Moves == 1) { player.X += 17; player.Y -= 9; }
        };
        var context = new MacroContext(new UnusedCapture(), control) { InputPlayer = player };
        var step = new MouseTrajectoryStep([new(0, 0, 0), new(10, 20, -24), new(20, 40, -6)]) { Mode = mode };

        await step.ExecuteAsync(context);

        player.X.Should().Be(57);
        player.Y.Should().Be(-15);
    }

    [Fact]
    public void Preparation_PreservesStationarySegment_AndAvoidsEmptyTicks()
    {
        MouseTrajectorySample[] raw = [new(0, 0, 0), new(20, 40, -6), new(60020, 40, -6), new(60040, 60, 4)];
        var prepared = LookPathSmoother.Prepare(raw);
        MouseTrajectoryPlayback.Interpolate(prepared, 30000, out double x, out double y);
        x.Should().Be(40);
        y.Should().Be(-6);
        var ticks = RecordedTrajectory.BuildEmitTimesMs(prepared, 240);
        ticks.Should().Contain(20).And.Contain(60020).And.Contain(60040);
        ticks.Should().NotContain(t => t > 20 && t < 60020);
        ticks.Length.Should().BeLessThan(20);
    }

    [Fact]
    public void Preparation_ReusesPlan_ButDetectsEditedSamples()
    {
        var step = new MouseTrajectoryStep([new(0, 0, 0), new(20, 40, -6)]);
        var prepare = typeof(MouseTrajectoryStep).GetMethod("PreparePlayback", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = prepare.Invoke(step, null);
        prepare.Invoke(step, null).Should().BeSameAs(first);
        step.Samples[1] = new(20, -20, 10);
        prepare.Invoke(step, null).Should().NotBeSameAs(first);
    }

    [Fact]
    public async Task Playback_UsesEditedSamples_AfterCachedRun()
    {
        using var control = new MacroController();
        var player = new AdditivePlayer();
        var context = new MacroContext(new UnusedCapture(), control) { InputPlayer = player };
        var step = new MouseTrajectoryStep([new(0, 0, 0), new(20, 40, -6)]);
        await step.ExecuteAsync(context);
        player.X = player.Y = 0;
        step.Samples[1] = new(20, -20, 10);
        await step.ExecuteAsync(context);
        player.X.Should().Be(-20);
        player.Y.Should().Be(10);
    }

    [Fact]
    public async Task Timeline_PauseDuringWait_FreezesRemainingTime()
    {
        using var control = new MacroController();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var started = new ManualResetEventSlim();
        var context = new MacroContext(new UnusedCapture(), control);
        var watch = Stopwatch.StartNew();
        var wait = Task.Run(() =>
        {
            long origin = Stopwatch.GetTimestamp();
            started.Set();
            return HighResolutionScheduler.WaitForTimeline(ref origin, Stopwatch.Frequency / 5, false, context, timeout.Token);
        }, timeout.Token);
        try
        {
            started.Wait(timeout.Token);
            await control.PauseAsync();
            await Task.Delay(250, timeout.Token);
            wait.IsCompleted.Should().BeFalse();
            await control.ResumeAsync();
            (await wait.WaitAsync(timeout.Token)).Should().BeTrue();
            watch.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(400);
        }
        finally { await control.StopAsync(); await wait; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeline_StopOrPanic_UnblocksPausedWait(bool panic)
    {
        using var control = new MacroController();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var context = new MacroContext(new UnusedCapture(), control);
        await control.PauseAsync();
        var wait = Task.Run(() =>
        {
            long origin = Stopwatch.GetTimestamp();
            return HighResolutionScheduler.WaitForTimeline(ref origin, Stopwatch.Frequency * 60, false, context, timeout.Token);
        }, timeout.Token);
        if (panic) control.Panic(); else await control.StopAsync();
        (await wait.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeFalse();
    }

    [Fact]
    public async Task Timeline_RuntimeLimitStillAppliesWhilePaused()
    {
        using var control = new MacroController();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var context = new MacroContext(new UnusedCapture(), control) { MaxRuntime = TimeSpan.FromMilliseconds(100) };
        await control.PauseAsync();
        var wait = Task.Run(() =>
        {
            long origin = Stopwatch.GetTimestamp();
            return HighResolutionScheduler.WaitForTimeline(ref origin, Stopwatch.Frequency * 60, false, context, timeout.Token);
        }, timeout.Token);
        (await wait.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeFalse();
        context.HasExceededMaxRuntime.Should().BeTrue();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidSpeedAndSensitivity_DoNotReachInput(double value)
    {
        var options = new PlaybackOptions { SpeedMultiplier = value };
        Action validate = () => options.Validate();
        validate.Should().Throw<ArgumentOutOfRangeException>();
        MouseTrajectoryStep.ClampLookGain(value).Should().Be(1);
        StepTimeline.Scale(TimeSpan.FromSeconds(1), value).Should().Be(TimeSpan.FromSeconds(1));
    }
}
