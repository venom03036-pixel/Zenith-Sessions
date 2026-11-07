using System.Drawing;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using Xunit;

namespace Zenith.Sessions.Tests;
public sealed class ClientRelativePlaybackTargetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitRecipientCapabilitySuppliesTheHandleWithoutAnyCoordinateOrForegroundService(bool move)
    {
        using var player = new RecipientPlayer();
        using var controller = new MacroController();
        using var capture = new Zenith.MacroHost.Execution.NoCapture();
        var context = new MacroContext(capture, controller)
        {
            InputPlayer = player
        };
        await Step(move).ExecuteAsync(context);
        Assert.Equal(1, player.Calls);
        Assert.Equal((nint)123, player.Options!.TargetWindowHandle);
        Assert.Equal(100, player.Options.RecordedClientWidth);
        Assert.Equal(80, player.Options.RecordedClientHeight);
        Assert.All(player.Events, value => Assert.Equal((12, 23), (value.X, value.Y)));
        Assert.Null(context.WindowCoordinates);
        Assert.Null(context.PlaybackSession);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingPlayerWithoutCapabilityStillFailsInsteadOfGuessingAForegroundRecipient(bool move)
    {
        using var player = new ExistingPlayer();
        using var controller = new MacroController();
        using var capture = new Zenith.MacroHost.Execution.NoCapture();
        var context = new MacroContext(capture, controller)
        {
            InputPlayer = player
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Step(move).ExecuteAsync(context));
        Assert.Equal(0, player.Calls);
        Assert.Empty(player.Events);
        Assert.Null(context.WindowCoordinates);
        Assert.Null(context.PlaybackSession);
    }

    private static MacroStep Step(bool move) => move ? new MouseMoveStep
    {
        IsClientRelative = true,
        Position = new Point(12, 23),
        RecordedClientWidth = 100,
        RecordedClientHeight = 80
    }

    : new ClickStep
    {
        IsClientRelative = true,
        Position = new Point(12, 23),
        RecordedClientWidth = 100,
        RecordedClientHeight = 80,
        DelayAfterMs = 0
    };
    private sealed class RecipientPlayer : ExistingPlayer, IClientRelativePlaybackTarget
    {
        public IntPtr TargetWindowHandle => (nint)123;
    }

    private class ExistingPlayer : IInputPlayer
    {
        public int Calls;
        public InputEvent[] Events = [];
        public PlaybackOptions? Options;
        public bool IsPlaying => false;

        public Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default)
        {
            Calls++;
            Events = events.ToArray();
            Options = options;
            return Task.CompletedTask;
        }

        public void ReleaseAllHeldKeys()
        {
        }

        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false) => throw new InvalidOperationException("No direct input expected.");
        public void SendRelativeMove(int dx, int dy) => throw new InvalidOperationException("No direct input expected.");
        public void Dispose()
        {
        }
    }
}
