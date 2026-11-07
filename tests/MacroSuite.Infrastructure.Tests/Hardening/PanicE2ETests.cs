using System.Diagnostics;
using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Hardening;

public class PanicE2ETests
{
    private sealed class TrackingInputPlayer : IInputPlayer
    {
        public bool IsPlaying => false;
        public int ReleaseAllHeldKeysCallCount { get; private set; }
        public TaskCompletionSource FirstClick { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public void ReleaseAllHeldKeys()
        {
            ReleaseAllHeldKeysCallCount++;
        }

        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false)
            => FirstClick.TrySetResult();
        public void SendRelativeMove(int dx, int dy) { }

        public void Dispose() { }
    }

    private static readonly TimeSpan AutoClickerDeadline = TimeSpan.FromSeconds(2);

    private sealed class StubCaptureService : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;
        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public void Dispose() { }
    }

    [Fact]
    public async Task Panic_WhileExecutingLongDelay_AbortsWithin100ms_AndReleasesAllKeys()
    {
        var inputPlayer = new TrackingInputPlayer();
        var controller = new MacroController(inputPlayer);
        var capture = new StubCaptureService();
        var context = new MacroContext(capture, controller)
        {
            InputPlayer = inputPlayer
        };

        var macro = new MacroModel
        {
            Name = "Long Delay Macro",
            Steps = new List<MacroStep>
            {
                new ClickStep(new Point(100, 100)),
                new DelayStep(TimeSpan.FromSeconds(10)),
                new ClickStep(new Point(200, 200))
            }
        };

        var executor = new MacroExecutor();

        var execTask = Task.Run(() => executor.ExecuteAsync(macro, context));

        // Allow execution to enter the 10-second delay
        await Task.Delay(50);

        var sw = Stopwatch.StartNew();
        controller.Panic();

        await execTask;
        sw.Stop();

        // Must abort in < 100ms per Phase 9 specification
        sw.ElapsedMilliseconds.Should().BeLessThan(100);
        executor.State.Should().Be(ExecutionState.Aborted);
        controller.IsPanicTriggered.Should().BeTrue();
        context.IsAborted.Should().BeTrue();
        inputPlayer.ReleaseAllHeldKeysCallCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Panic_WhilePausedInGate_ImmediatelyUnblocksAndAborts()
    {
        var inputPlayer = new TrackingInputPlayer();
        var controller = new MacroController(inputPlayer);
        var capture = new StubCaptureService();
        var context = new MacroContext(capture, controller)
        {
            InputPlayer = inputPlayer
        };

        var macro = new MacroModel
        {
            Name = "Paused Macro",
            Steps = new List<MacroStep>
            {
                new DelayStep(TimeSpan.FromMilliseconds(50)),
                new DelayStep(TimeSpan.FromSeconds(5))
            }
        };

        var executor = new MacroExecutor();

        // Pause before starting
        await controller.PauseAsync();
        controller.IsPaused.Should().BeTrue();

        var execTask = Task.Run(() => executor.ExecuteAsync(macro, context));

        // Allow execution to reach pause gate
        await Task.Delay(50);
        executor.State.Should().Be(ExecutionState.Paused);

        // Emergency panic while still paused
        var sw = Stopwatch.StartNew();
        controller.Panic();

        await execTask;
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(100);
        executor.State.Should().Be(ExecutionState.Aborted);
        inputPlayer.ReleaseAllHeldKeysCallCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void Panic_WhenControllerAlreadyDisposed_DoesNotThrow()
    {
        var inputPlayer = new TrackingInputPlayer();
        var controller = new MacroController(inputPlayer);
        controller.Dispose();

        var act = () => controller.Panic();
        act.Should().NotThrow();
    }

    // The Auto Clicker releases only input it owns (Rule 13): the run that sent clicks releases
    // them in its finally. An idle clicker owns nothing, so it must not release keys another
    // owner (for example macro playback on the shared input player) is still holding.
    [Fact]
    public async Task AutoClicker_StopClicker_CallsReleaseAllHeldKeys()
    {
        var inputPlayer = new TrackingInputPlayer();
        using var autoClickerVM = new MacroSuite.App.ViewModels.AutoClickerViewModel(inputPlayer) { ClicksPerSecond = 0.1 };

        autoClickerVM.StopClicker();
        inputPlayer.ReleaseAllHeldKeysCallCount.Should().Be(0, "an idle Auto Clicker owns no input to release");

        autoClickerVM.ToggleRun();
        await inputPlayer.FirstClick.Task.WaitAsync(AutoClickerDeadline);
        autoClickerVM.StopClicker();
        await autoClickerVM.RunCompletion.WaitAsync(AutoClickerDeadline);

        inputPlayer.ReleaseAllHeldKeysCallCount.Should().Be(1, "stopping a clicker that sent input releases it exactly once");
        autoClickerVM.IsRunning.Should().BeFalse();
        await autoClickerVM.ShutdownAsync().WaitAsync(AutoClickerDeadline);
        inputPlayer.ReleaseAllHeldKeysCallCount.Should().Be(1, "shutdown after the run ended has nothing left to release");
    }

    [Fact]
    public async Task AutoClicker_Dispose_CallsReleaseAllHeldKeys()
    {
        var idlePlayer = new TrackingInputPlayer();
        var idleVM = new MacroSuite.App.ViewModels.AutoClickerViewModel(idlePlayer);
        idleVM.Dispose();
        await idleVM.ShutdownAsync().WaitAsync(AutoClickerDeadline);
        idlePlayer.ReleaseAllHeldKeysCallCount.Should().Be(0, "an idle Auto Clicker owns no input to release");

        var inputPlayer = new TrackingInputPlayer();
        var autoClickerVM = new MacroSuite.App.ViewModels.AutoClickerViewModel(inputPlayer) { ClicksPerSecond = 0.1 };
        autoClickerVM.ToggleRun();
        await inputPlayer.FirstClick.Task.WaitAsync(AutoClickerDeadline);

        autoClickerVM.Dispose();
        // Dispose starts the tracked shutdown; ShutdownAsync returns that same task.
        await autoClickerVM.ShutdownAsync().WaitAsync(AutoClickerDeadline);

        inputPlayer.ReleaseAllHeldKeysCallCount.Should().Be(1, "disposing a running clicker releases its input exactly once");
        autoClickerVM.RunCompletion.IsCompleted.Should().BeTrue();
        autoClickerVM.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Win32InputPlayer_ReleaseAllHeldKeys_ConcurrentWithPlayback_DoesNotDeadlockOrThrow()
    {
        using var player = new MacroSuite.Infrastructure.Input.Win32InputPlayer();

        // Concurrently invoke ReleaseAllHeldKeys and SendClick to verify thread safety of lock
        var barrier = new Barrier(4);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var tasks = Enumerable.Range(0, 4).Select(i => Task.Run(() =>
        {
            barrier.SignalAndWait();
            while (!cts.Token.IsCancellationRequested)
            {
                if (i % 2 == 0)
                {
                    player.ReleaseAllHeldKeys();
                }
                else
                {
                    player.SendClick(MouseButton.Left, -1, -1, false);
                }
            }
        })).ToArray();

        var act = () => Task.WaitAll(tasks);
        act.Should().NotThrow();
    }
}
