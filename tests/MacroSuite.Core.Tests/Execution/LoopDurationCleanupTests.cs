using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using Xunit;

namespace MacroSuite.Core.Tests.Execution;

public sealed class LoopDurationCleanupTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task DurationCap_ReleasesHeldInputBeforeTheFollowingRootAction(int iterations)
    {
        using var capture = new UnusedCapture();
        using var controller = new MacroController();
        using var input = new OwnedInput();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var context = new MacroContext(capture, controller) { InputPlayer = input };
        var executor = new MacroExecutor();
        bool followingActionRan = false;
        var loop = new LoopStep(iterations,
        [
            new KeyPressStep(87, 0) { Stroke = "Down" },
            new ClickStep(Point.Empty, MouseButton.Right, delayAfterMs: 0) { Stroke = "Down" },
            new DelayStep(TimeSpan.FromSeconds(10)),
            new KeyPressStep(87, 0) { Stroke = "Up" },
            new ClickStep(Point.Empty, MouseButton.Right, delayAfterMs: 0) { Stroke = "Up" }
        ]) { MaxDurationMs = 150 };

        await executor.ExecuteAsync(new MacroModel
        {
            Steps = [loop, new CallbackStep(() =>
            {
                // Assert during continuation: executor shutdown also releases input,
                // which would hide this regression if checked only after playback.
                input.HeldKeys.Should().BeEmpty();
                input.HeldButtons.Should().BeEmpty();
                input.Releases.Should().Be(1);
                followingActionRan = true;
            })]
        }, context, timeout.Token);

        followingActionRan.Should().BeTrue();
        input.Events.Should().ContainSingle(e => e.Type == InputEventType.KeyDown);
        input.Events.Should().ContainSingle(e => e.Type == InputEventType.MouseDown);
        input.Events.Should().NotContain(e => e.Type == InputEventType.KeyUp || e.Type == InputEventType.MouseUp,
            "the time limit skips the scripted releases, so block cleanup must release the holds");
        executor.State.Should().Be(ExecutionState.Completed);
    }

    private sealed class CallbackStep(Action action) : MacroStep
    {
        public override Task ExecuteAsync(MacroContext context, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class OwnedInput : IInputPlayer
    {
        public List<InputEvent> Events { get; } = [];
        public HashSet<int> HeldKeys { get; } = [];
        public HashSet<MouseButton> HeldButtons { get; } = [];
        public int Releases { get; private set; }
        public bool IsPlaying => false;

        public Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            foreach (InputEvent item in events)
            {
                Events.Add(item);
                switch (item.Type)
                {
                    case InputEventType.KeyDown: HeldKeys.Add(item.Data); break;
                    case InputEventType.KeyUp: HeldKeys.Remove(item.Data); break;
                    case InputEventType.MouseDown: HeldButtons.Add((MouseButton)item.Data); break;
                    case InputEventType.MouseUp: HeldButtons.Remove((MouseButton)item.Data); break;
                }
            }
            return Task.CompletedTask;
        }

        public void ReleaseAllHeldKeys()
        {
            Releases++;
            HeldKeys.Clear();
            HeldButtons.Clear();
        }
        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false) { }
        public void SendRelativeMove(int dx, int dy) { }
        public void Dispose() { }
    }

    private sealed class UnusedCapture : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;
        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
            => throw new InvalidOperationException("This macro should not capture the screen.");
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("This macro should not capture the screen.");
        public void Dispose() { }
    }
}
