using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using FluentAssertions;
using Xunit;

namespace MacroSuite.Core.Tests.Input;

public sealed class PanicSignalTests
{
    private sealed class BlockingRelease : IInputPlayer
    {
        public readonly ManualResetEventSlim Entered = new(), Finish = new();
        public bool IsPlaying => false;
        public void ReleaseAllHeldKeys() { Entered.Set(); if (!Finish.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException(); }
        public Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default) => Task.CompletedTask;
        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false) { }
        public void SendRelativeMove(int dx, int dy) { }
        public void Dispose() { Finish.Set(); Entered.Dispose(); Finish.Dispose(); }
    }
    [Fact]
    public async Task SignalPanicMarksCancellationImmediatelyWithoutWaitingForNativeRelease()
    {
        using var player = new BlockingRelease(); using var control = new MacroController(player);
        control.SignalPanic();
        try
        {
            control.IsPanicTriggered.Should().BeTrue(); control.StopToken.IsCancellationRequested.Should().BeTrue();
            control.PanicToken.IsCancellationRequested.Should().BeTrue();
            player.Entered.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();
            control.WaitForPanicReleaseAsync().IsCompleted.Should().BeFalse();
        }
        finally { player.Finish.Set(); }
        await control.WaitForPanicReleaseAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }
}
