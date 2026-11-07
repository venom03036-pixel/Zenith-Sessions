using System.Diagnostics;
using System.Runtime.InteropServices;
using FluentAssertions;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.Input;
using MacroSuite.Infrastructure.Windows;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Input;

public class Win32InputPlayerTests
{
    [Fact]
    public void MixedBatch_CameraWarpFilter_PreservesReleaseAndRelativeEvents()
    {
        // Build native packets without sending any input to the desktop.
        var player = new Win32InputPlayer();
        var method = typeof(Win32InputPlayer).GetMethod("BuildPlaybackBatch",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        InputEvent[] events =
        [
            new(InputEventType.MouseMove, 0, 100, 100, 0),
            new(InputEventType.KeyUp, 0, 0, 0, 0x57),
            new(InputEventType.MouseUp, 0, 0, 0, (int)MouseButton.Right),
            new(InputEventType.MouseMoveRelative, 0, 12, -3, 0),
            new(InputEventType.MouseMove, 0, 200, 200, 0)
        ];
        var packets = new NativeMethods.INPUT[events.Length];
        int count = (int)method.Invoke(player, [events, 0, events.Length, true, 0u, PlaybackOptions.Default, packets])!;
        count.Should().Be(3);
        packets[0].type.Should().Be(NativeMethods.INPUT_KEYBOARD);
        (packets[0].ui.ki.dwFlags & NativeMethods.KEYEVENTF_KEYUP).Should().NotBe(0);
        (packets[1].ui.mi.dwFlags & NativeMethods.MOUSEEVENTF_RIGHTUP).Should().NotBe(0);
        packets[2].ui.mi.dx.Should().Be(12);
        packets[2].ui.mi.dy.Should().Be(-3);
        (packets[2].ui.mi.dwFlags & NativeMethods.MOUSEEVENTF_ABSOLUTE).Should().Be(0);

        int unfiltered = (int)method.Invoke(player, [events, 0, events.Length, false, 0u, PlaybackOptions.Default, packets])!;
        unfiltered.Should().Be(events.Length);
    }

    [Fact]
    public async Task PrecisionWait_CancellationInterruptsLongWait()
    {
        var method = typeof(Win32InputPlayer).GetMethod("PrecisionWait",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var wait = (Action<long, CancellationToken>)method.CreateDelegate(typeof(Action<long, CancellationToken>));
        using var cts = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        var task = Task.Run(() =>
        {
            entered.Set();
            wait(Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60, cts.Token);
        });
        entered.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();
        await Task.Delay(30);
        cts.Cancel();
        Func<Task> finish = () => task.WaitAsync(TimeSpan.FromSeconds(1));
        await finish.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void InputStruct_Size_MatchesNativeExpectations()
    {
        int size = Marshal.SizeOf<NativeMethods.INPUT>();
        // On 64-bit Windows, sizeof(INPUT) is 40 bytes (4 bytes type, 4 bytes padding, 32 bytes union)
        // On 32-bit Windows, sizeof(INPUT) is 28 bytes
        if (Environment.Is64BitProcess)
        {
            size.Should().Be(40);
        }
        else
        {
            size.Should().Be(28);
        }
    }

    [Fact]
    public async Task Win32InputPlayer_CanPlayEmptySequence()
    {
        using var player = new Win32InputPlayer();
        var act = async () => await player.PlayAsync(Array.Empty<InputEvent>());
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Win32InputPlayer_ShortPlayback_RestoresCallingThreadPriority()
    {
        using var player = new Win32InputPlayer();
        ThreadPriority originalPriority = Thread.CurrentThread.Priority;

        await player.PlayAsync(Array.Empty<InputEvent>());

        Thread.CurrentThread.Priority.Should().Be(originalPriority);
    }

    [Fact]
    public void PlaybackOptions_Validation_EnforcesSpeedBounds()
    {
        var zeroSpeed = new PlaybackOptions { SpeedMultiplier = 0.0 };
        var negativeSpeed = new PlaybackOptions { SpeedMultiplier = -1.5 };

        var actZero = () => zeroSpeed.Validate();
        var actNegative = () => negativeSpeed.Validate();

        actZero.Should().Throw<ArgumentOutOfRangeException>();
        actNegative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void PlaybackOptions_Validation_EnforcesRepeatCountBounds()
    {
        var invalid = new PlaybackOptions { RepeatCount = -1 };
        var act = () => invalid.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void PlaybackOptions_Validation_EnforcesMouseInterpolationStepMs()
    {
        var invalid = new PlaybackOptions { MouseInterpolationStepMs = 0 };
        var act = () => invalid.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Win32InputPlayer_ReleaseAllHeldKeys_DoesNotThrow()
    {
        using var player = new Win32InputPlayer();
        var act = () => player.ReleaseAllHeldKeys();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Win32InputPlayer_PlaybackCancellation_AbortsImmediately()
    {
        using var player = new Win32InputPlayer();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled

        var events = new[]
        {
            new InputEvent(InputEventType.MouseMove, 0, 100, 100, 0),
            new InputEvent(InputEventType.MouseMove, Stopwatch.Frequency, 200, 200, 0)
        };

        var act = async () => await player.PlayAsync(events, ct: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class StubWindowCoordinateService : MacroSuite.Core.Abstractions.IWindowCoordinateService
    {
        public int ClientToScreenCalls { get; private set; }

        public IntPtr GetWindowAt(int screenX, int screenY) => (IntPtr)0x1234;
        public IntPtr GetForegroundWindow() => (IntPtr)0x1234;
        public (int clientX, int clientY) ScreenToClient(IntPtr hWnd, int screenX, int screenY) => (screenX - 50, screenY - 50);
        public (int screenX, int screenY) ClientToScreen(IntPtr hWnd, int clientX, int clientY)
        {
            ClientToScreenCalls++;
            return (clientX + 50, clientY + 50);
        }
        public (int width, int height) GetClientSize(IntPtr hWnd) => (800, 600);
        public bool IsWindowValid(IntPtr hWnd) => true;
        public IntPtr FindWindowByTitle(string? titleOrProcessName) => GetForegroundWindow();
    }

    [Fact]
    public void Win32InputPlayer_AcceptsCustomCoordinateService()
    {
        var stub = new StubWindowCoordinateService();
        using var player = new Win32InputPlayer(stub);
        player.Should().NotBeNull();
        player.IsPlaying.Should().BeFalse();
    }

    private sealed class RefusingForegroundGuard : MacroSuite.Core.Abstractions.IForegroundInputGuard
    {
        public int Calls { get; private set; }

        public bool TryAllowSynthesizedInput(out string? refusalReason)
        {
            Calls++;
            refusalReason = "test refusal";
            return false;
        }
    }

    [Fact]
    public async Task Win32InputPlayer_PlayAsync_RefusesUnsafeForeground()
    {
        var guard = new RefusingForegroundGuard();
        using var player = new Win32InputPlayer(foregroundGuard: guard);
        var events = new[] { new InputEvent(InputEventType.MouseMove, 0, 10, 10, 0) };

        var act = async () => await player.PlayAsync(events);
        await act.Should().ThrowAsync<UnsafeForegroundInputException>().WithMessage("*test refusal*");
        guard.Calls.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Win32InputPlayer_SendRelativeMove_ReportsUnsafeForeground()
    {
        var guard = new RefusingForegroundGuard();
        using var player = new Win32InputPlayer(foregroundGuard: guard);
        var act = () => player.SendRelativeMove(3, 3);
        act.Should().Throw<UnsafeForegroundInputException>();
        guard.Calls.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Win32InputPlayer_ReleaseAllHeldKeys_IgnoresForegroundGuard()
    {
        var guard = new RefusingForegroundGuard();
        using var player = new Win32InputPlayer(foregroundGuard: guard);
        var act = () => player.ReleaseAllHeldKeys();
        act.Should().NotThrow();
        guard.Calls.Should().Be(0);
    }
}
