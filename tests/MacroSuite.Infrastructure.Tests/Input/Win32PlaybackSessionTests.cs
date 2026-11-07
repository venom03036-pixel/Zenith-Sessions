using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.Input;
using MacroSuite.Infrastructure.Windows;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Input;

public sealed class Win32PlaybackSessionTests
{
    private sealed class Native : INativeInputSink
    {
        public readonly ConcurrentQueue<NativeMethods.INPUT> Sent = new();
        public readonly HashSet<int> PhysicalKeys = new();
        public uint Send(NativeMethods.INPUT[] inputs, int count)
        {
            lock (PhysicalKeys)
                for (int i = 0; i < count; i++)
                {
                    var input = inputs[i]; Sent.Enqueue(input);
                    if (input.type == NativeMethods.INPUT_KEYBOARD && (input.ui.ki.dwFlags & NativeMethods.KEYEVENTF_UNICODE) == 0)
                    { if ((input.ui.ki.dwFlags & NativeMethods.KEYEVENTF_KEYUP) != 0) PhysicalKeys.Remove(input.ui.ki.wVk); else PhysicalKeys.Add(input.ui.ki.wVk); }
                }
            return (uint)count;
        }
        public bool IsPressed(int key) { lock (PhysicalKeys) return PhysicalKeys.Contains(key); }
        public bool SuppressAbsoluteMoves => false;
        public (int X, int Y) CursorPosition => (0, 0);
        public (int X, int Y, int Width, int Height) VirtualScreen => (-1920, 0, 3840, 1080);
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    [Fact]
    public async Task PausingTimedNativeBatchReleasesThenRestoresWithoutCatchUpOrReplay()
    {
        var native = new Native(); using var player = new Win32InputPlayer(native); using var control = new MacroController(player);
        await using var session = new PlaybackSessionContext(control); using var attachment = session.Attach(player);
        long origin = Stopwatch.GetTimestamp();
        var play = player.PlayAsync([new(InputEventType.KeyDown, origin, 0, 0, 65),
            new(InputEventType.MouseMoveRelative, origin + Stopwatch.Frequency / 5, 3, 4, 0),
            new(InputEventType.KeyUp, origin + Stopwatch.Frequency / 5, 0, 0, 65)], new() { Session = session });
        await Until(() => native.Sent.Count == 1); await control.PauseAsync(); await session.AwaitReleasedAsync();
        native.Sent.Count.Should().Be(2); await Task.Delay(220); play.IsCompleted.Should().BeFalse();
        (await session.TryResumeAsync(inputIsNeutral: false)).Should().BeFalse(); control.IsPaused.Should().BeTrue();
        (await session.TryResumeAsync(inputIsNeutral: true)).Should().BeTrue();
        play.IsCompleted.Should().BeFalse(); await play.WaitAsync(TimeSpan.FromSeconds(3));
        var keyboard = native.Sent.Where(e => e.type == NativeMethods.INPUT_KEYBOARD).ToArray();
        keyboard.Select(e => (e.ui.ki.dwFlags & NativeMethods.KEYEVENTF_KEYUP) != 0).Should().Equal(false, true, false, true);
        native.Sent.Count(e => e.type == NativeMethods.INPUT_MOUSE).Should().Be(1);
    }
    [Fact]
    public async Task BorrowedPhysicalKeyIsNeverReleasedBySessionPause()
    {
        var native = new Native(); native.PhysicalKeys.Add(65);
        using var player = new Win32InputPlayer(native); using var control = new MacroController(player);
        await using var session = new PlaybackSessionContext(control); using var attachment = session.Attach(player);
        await player.PlayAsync([new(InputEventType.KeyDown, 0, 0, 0, 65)]);
        await control.PauseAsync(); await session.AwaitReleasedAsync();
        native.Sent.Should().BeEmpty(); native.PhysicalKeys.Should().Contain(65);
    }
}
