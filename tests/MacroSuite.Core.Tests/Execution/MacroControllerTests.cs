using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using Xunit;

namespace MacroSuite.Core.Tests.Execution;

public sealed class MacroControllerTests
{
    private sealed class MockInputPlayer : IInputPlayer
    {
        public bool IsPlaying => false;
        public bool ReleaseKeysCalled { get; private set; }

        public Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public void ReleaseAllHeldKeys()
        {
            ReleaseKeysCalled = true;
        }

        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false) { }
        public void SendRelativeMove(int dx, int dy) { }

        public void Dispose() { }
    }

    [Fact]
    public async Task PauseAndResume_TogglesStateAndUnblocksGate()
    {
        using var controller = new MacroController();

        controller.IsPaused.Should().BeFalse();

        await controller.PauseAsync();
        controller.IsPaused.Should().BeTrue();

        // Start waiting on pause gate in background
        var waitTask = Task.Run(async () =>
        {
            await controller.WaitIfPausedAsync();
            return true;
        });

        // Ensure waitTask has started and is waiting
        await Task.Delay(50);
        waitTask.IsCompleted.Should().BeFalse();

        // Resume should unblock
        await controller.ResumeAsync();
        controller.IsPaused.Should().BeFalse();

        bool finished = await waitTask.WaitAsync(TimeSpan.FromSeconds(2));
        finished.Should().BeTrue();
    }

    [Fact]
    public async Task Stop_SignalsStopToken()
    {
        using var controller = new MacroController();

        controller.StopToken.IsCancellationRequested.Should().BeFalse();

        await controller.StopAsync();

        controller.StopToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task Panic_ImmediatelyCancelsTokens_UnblocksGate_AndReleasesHeldKeys()
    {
        var mockPlayer = new MockInputPlayer();
        using var controller = new MacroController(mockPlayer);

        // Put into paused state
        await controller.PauseAsync();
        var waitTask = Task.Run(async () =>
        {
            await controller.WaitIfPausedAsync();
            return true;
        });

        await Task.Delay(30);
        waitTask.IsCompleted.Should().BeFalse();

        // Trigger Panic
        controller.Panic();

        controller.IsPanicTriggered.Should().BeTrue();
        controller.PanicToken.IsCancellationRequested.Should().BeTrue();
        controller.StopToken.IsCancellationRequested.Should().BeTrue();
        mockPlayer.ReleaseKeysCalled.Should().BeTrue(); // Panic releases keys! (Rule 11)

        // Pause gate must have unblocked immediately
        bool finished = await waitTask.WaitAsync(TimeSpan.FromSeconds(1));
        finished.Should().BeTrue();
    }
}
