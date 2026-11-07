using System.Diagnostics;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.Input;
using MacroSuite.Infrastructure.Windows;
using Xunit;
using Xunit.Abstractions;

namespace MacroSuite.Infrastructure.Tests.Input;

public sealed class InputPipelineRegressionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task NativeEmission_OnePacketPerEvent_PreservesMixedOrderAndWheelSigns()
    {
        var sink = new RecordingNativeInputSink();
        using var player = new Win32InputPlayer(sink);
        InputEvent[] events = [
            new(InputEventType.KeyDown, 0, 0, 0, 87),
            new(InputEventType.MouseDown, 0, 0, 0, (int)MouseButton.Right),
            new(InputEventType.MouseMoveRelative, 0, 17, -3, 0),
            new(InputEventType.MouseWheel, 0, 0, 0, -120),
            new(InputEventType.MouseHWheel, 0, 0, 0, 120),
            new(InputEventType.MouseUp, 0, 0, 0, (int)MouseButton.Right),
            new(InputEventType.KeyUp, 0, 0, 0, 87)];
        await player.PlayAsync(events);
        sink.Inputs.Should().HaveCount(events.Length);
        sink.SendCalls.Should().Be(1);
        sink.Inputs[2].ui.mi.dx.Should().Be(17);
        sink.Inputs[2].ui.mi.dy.Should().Be(-3);
        unchecked((int)sink.Inputs[3].ui.mi.mouseData).Should().Be(-120);
        sink.Inputs[4].ui.mi.dwFlags.Should().Be(NativeMethods.MOUSEEVENTF_HWHEEL);
        player.ReleaseAllHeldKeys();
        sink.Inputs.Should().HaveCount(events.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RelativeMove_CompatibilityFlag_NeverDuplicatesTheDelta(bool dualBackend)
    {
        var sink = new RecordingNativeInputSink();
        using var player = new Win32InputPlayer(sink);
        player.SendRelativeMove(31, -7, dualBackend);
        sink.Inputs.Should().ContainSingle();
        sink.Inputs[0].ui.mi.dx.Should().Be(31);
        sink.Inputs[0].ui.mi.dy.Should().Be(-7);
    }

    [Theory]
    [InlineData(MouseButton.X1)]
    [InlineData(MouseButton.X2)]
    public void Click_UsesOnlyRequestedButton_AndSupportsNegativeScreenCoordinates(MouseButton button)
    {
        var sink = new RecordingNativeInputSink();
        using var player = new Win32InputPlayer(sink);
        player.SendClick(button, -700, 400);
        sink.Inputs.Should().HaveCount(3);
        sink.Inputs[0].ui.mi.dx.Should().BeInRange(1, 32767);
        sink.Inputs[1].ui.mi.dwFlags.Should().Be(NativeMethods.MOUSEEVENTF_XDOWN);
        sink.Inputs[2].ui.mi.dwFlags.Should().Be(NativeMethods.MOUSEEVENTF_XUP);
        sink.Inputs[1].ui.mi.mouseData.Should().Be(button == MouseButton.X1 ? 1u : 2u);
    }

    [Fact]
    public async Task Panic_ReleasesOnlySuccessfulOwnedDowns_AndLeavesPhysicalHoldsAlone()
    {
        var sink = new RecordingNativeInputSink();
        sink.PhysicallyHeld.UnionWith([65, 2]);
        using var player = new Win32InputPlayer(sink);
        await player.PlayAsync([
            new(InputEventType.KeyDown, 0, 0, 0, 65),
            new(InputEventType.MouseDown, 0, 0, 0, (int)MouseButton.Right),
            new(InputEventType.KeyDown, 0, 0, 0, 87),
            new(InputEventType.MouseDown, 0, 0, 0, (int)MouseButton.X1)]);
        sink.Inputs.Should().HaveCount(2);
        player.ReleaseAllHeldKeys();
        sink.Inputs.Should().HaveCount(4);
        sink.Inputs[2].ui.ki.wVk.Should().Be(87);
        sink.Inputs[2].ui.ki.dwFlags.Should().Be(NativeMethods.KEYEVENTF_KEYUP);
        sink.Inputs[3].ui.mi.dwFlags.Should().Be(NativeMethods.MOUSEEVENTF_XUP);
        sink.Inputs[3].ui.mi.dwExtraInfo.Should().Be(LowLevelHookService.PlaybackSentinelTag);
        player.ReleaseAllHeldKeys();
        sink.Inputs.Should().HaveCount(4);
    }

    [Fact]
    public async Task PartialNativeFailure_ReportsFailure_AndReleasesTheAcceptedPrefix()
    {
        var sink = new RecordingNativeInputSink { AcceptLimit = 1 };
        using var player = new Win32InputPlayer(sink);
        Func<Task> play = () => player.PlayAsync([
            new(InputEventType.KeyDown, 0, 0, 0, 87),
            new(InputEventType.KeyUp, 0, 0, 0, 87)]);
        await play.Should().ThrowAsync<InvalidOperationException>().WithMessage("*accepted 1 of 2*");
        sink.Inputs.Should().HaveCount(2);
        sink.Inputs[1].ui.ki.dwFlags.Should().Be(NativeMethods.KEYEVENTF_KEYUP);
        player.IsPlaying.Should().BeFalse();
    }

    [Fact]
    public async Task FocusChangeDuringWait_BlocksNextEmission_AndReleasesOwnedKey()
    {
        var sink = new RecordingNativeInputSink();
        var guard = new MutableGuard();
        var firstSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sink.OnSend = () => firstSent.TrySetResult();
        using var player = new Win32InputPlayer(sink, foregroundGuard: guard);
        var task = Task.Run(() => player.PlayAsync([
            new(InputEventType.KeyDown, 0, 0, 0, 87),
            new(InputEventType.MouseMoveRelative, Stopwatch.Frequency / 5, 20, 10, 0)]));
        await firstSent.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(30);
        guard.Allowed = false;
        Func<Task> play = () => task;
        await play.Should().ThrowAsync<UnsafeForegroundInputException>();
        sink.Inputs.Should().HaveCount(2);
        sink.Inputs[1].ui.ki.dwFlags.Should().Be(NativeMethods.KEYEVENTF_KEYUP);
    }

    [Fact]
    public async Task RecordingBuffer_OverflowPreservesAcceptedPrefix_AndReportsIncompleteRecording()
    {
        var buffer = new RecordingInputBuffer(3);
        for (int i = 0; i < 3; i++) buffer.TryWrite(new(InputEventType.KeyDown, i, 0, 0, 65 + i)).Should().BeTrue();
        buffer.TryWrite(new(InputEventType.KeyUp, 3, 0, 0, 65)).Should().BeFalse();
        var captured = new List<int>();
        while (buffer.Reader.TryRead(out var ev)) captured.Add(ev.Data);
        captured.Should().Equal(65, 66, 67);
        Func<Task> finish = () => buffer.Reader.Completion;
        await finish.Should().ThrowAsync<InvalidOperationException>().WithMessage("*recording is incomplete*");
    }

    [Fact]
    public async Task RecordingBuffer_CompletedNormally_DrainsEveryAcceptedEvent()
    {
        var buffer = new RecordingInputBuffer(1000);
        for (int i = 0; i < 1000; i++) buffer.TryWrite(new(InputEventType.MouseMoveRelative, i, i % 3 - 1, i % 5 - 2, 0)).Should().BeTrue();
        buffer.TryComplete().Should().BeTrue();
        int received = 0;
        await foreach (var ev in buffer.Reader.ReadAllAsync()) ev.TimestampTicks.Should().Be(received++);
        received.Should().Be(1000);
    }

    [Fact]
    public async Task FixedSeed_DenseMixedTrace_AndOneThousandSessions_PreserveEveryIntermediateCoordinate()
    {
        const int seed = 62026;
        var random = new Random(seed);
        var sink = new RecordingNativeInputSink();
        using var player = new Win32InputPlayer(sink);
        var watch = Stopwatch.StartNew();
        using var process = Process.GetCurrentProcess();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        long allocatedBefore = GC.GetTotalAllocatedBytes();
        int expectedX = 0, expectedY = 0, actualX = 0, actualY = 0;
        for (int session = 0; session < 1000; session++)
        {
            var events = new InputEvent[102];
            events[0] = new(InputEventType.KeyDown, 0, 0, 0, 87);
            for (int i = 1; i <= 100; i++) events[i] = new(InputEventType.MouseMoveRelative, 0, random.Next(-25, 26), random.Next(-9, 10), 0);
            events[^1] = new(InputEventType.KeyUp, 0, 0, 0, 87);
            int start = sink.Inputs.Count;
            await player.PlayAsync(events, new PlaybackOptions { InterpolateMouseMoves = false });
            sink.Inputs.Count.Should().Be(start + events.Length);
            for (int i = 1; i <= 100; i++)
            {
                expectedX += events[i].X; expectedY += events[i].Y;
                actualX += sink.Inputs[start + i].ui.mi.dx; actualY += sink.Inputs[start + i].ui.mi.dy;
                actualX.Should().Be(expectedX); actualY.Should().Be(expectedY);
            }
            player.ReleaseAllHeldKeys();
            sink.Inputs.Count.Should().Be(start + events.Length);
        }
        output.WriteLine($"seed={seed}; sessions=1000; events={sink.Inputs.Count}; elapsed_ms={watch.Elapsed.TotalMilliseconds:F2}; cpu_ms={(process.TotalProcessorTime - cpuBefore).TotalMilliseconds:F2}; allocated_bytes={GC.GetTotalAllocatedBytes() - allocatedBefore}");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    public async Task RealClock_FakeReceiver_MeasuresScheduleErrorAndDuration(double speed)
    {
        var sink = new RecordingNativeInputSink();
        using var player = new Win32InputPlayer(sink);
        var events = Enumerable.Range(0, 41).Select(i => new InputEvent(InputEventType.MouseMoveRelative,
            i * Stopwatch.Frequency / 100, i % 2 == 0 ? 3 : -2, i % 3 - 1, 0)).ToArray();
        await Task.Run(() => player.PlayAsync(events, new PlaybackOptions { SpeedMultiplier = speed, InterpolateMouseMoves = false }));
        sink.Inputs.Should().HaveCount(events.Length);
        var errors = sink.Timestamps.Select((tick, i) => (tick - sink.Timestamps[0]) * 1000d / Stopwatch.Frequency - i * 10d / speed).Order().ToArray();
        double duration = (sink.Timestamps[^1] - sink.Timestamps[0]) * 1000d / Stopwatch.Frequency;
        output.WriteLine($"speed={speed}; events=41; duration_ms={duration:F3}; expected_ms={400 / speed:F3}; error_ms_p50={errors[20]:F3}; p95={errors[38]:F3}; p99={errors[40]:F3}");
        duration.Should().BeInRange(400 / speed - 20, 400 / speed + 500);
    }

    [Fact]
    public async Task RealClock_UnderBoundedCpuLoad_PreservesTraceAndReportsTiming()
    {
        var sink = new RecordingNativeInputSink();
        using var player = new Win32InputPlayer(sink);
        using var loadCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        Task load = Task.Run(() =>
        {
            while (!loadCts.IsCancellationRequested)
            {
                Thread.SpinWait(20_000);
                Thread.Yield();
            }
        });
        try
        {
            var events = Enumerable.Range(0, 41).Select(i => new InputEvent(InputEventType.MouseMoveRelative,
                i * Stopwatch.Frequency / 100, i % 5 - 2, i % 7 - 3, 0)).ToArray();
            await Task.Run(() => player.PlayAsync(events, new PlaybackOptions { InterpolateMouseMoves = false }));
            sink.Inputs.Select(i => (i.ui.mi.dx, i.ui.mi.dy)).Should().Equal(events.Select(e => (e.X, e.Y)));
            var errors = sink.Timestamps.Select((tick, i) => (tick - sink.Timestamps[0]) * 1000d / Stopwatch.Frequency - i * 10d).Order().ToArray();
            double duration = (sink.Timestamps[^1] - sink.Timestamps[0]) * 1000d / Stopwatch.Frequency;
            output.WriteLine($"cpu_load_workers=1; events=41; duration_ms={duration:F3}; expected_ms=400; error_ms_p50={errors[20]:F3}; p95={errors[38]:F3}; p99={errors[40]:F3}");
            duration.Should().BeInRange(380, 1000);
        }
        finally { loadCts.Cancel(); await load; }
    }

    [Fact]
    public async Task StopDuringLongDelay_IsBoundedAndLeavesNoOwnedInput()
    {
        var sink = new RecordingNativeInputSink();
        using var entered = new ManualResetEventSlim();
        sink.OnSend = () => entered.Set();
        using var player = new Win32InputPlayer(sink);
        using var cts = new CancellationTokenSource();
        var task = Task.Run(() => player.PlayAsync([
            new(InputEventType.KeyDown, 0, 0, 0, 87),
            new(InputEventType.KeyUp, Stopwatch.Frequency * 60, 0, 0, 87)], ct: cts.Token));
        entered.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();
        var watch = Stopwatch.StartNew();
        cts.Cancel();
        Func<Task> finish = () => task.WaitAsync(TimeSpan.FromSeconds(1));
        await finish.Should().ThrowAsync<OperationCanceledException>();
        output.WriteLine($"stop_latency_ms={watch.Elapsed.TotalMilliseconds:F3}");
        sink.Inputs.Should().HaveCount(2);
        player.ReleaseAllHeldKeys();
        sink.Inputs.Should().HaveCount(2);
    }

    private sealed class MutableGuard : IForegroundInputGuard
    {
        public bool Allowed { get; set; } = true;
        public bool TryAllowSynthesizedInput(out string? refusalReason)
        {
            refusalReason = Allowed ? null : "Test receiver lost focus";
            return Allowed;
        }
    }
}
