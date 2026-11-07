using System.Diagnostics;
using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace MacroSuite.Core.Tests.Execution;

public sealed class CameraTraceAuditTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FixedSeedPacketTraces_PreserveEveryIntermediateMove_AtMultipleSpeeds()
    {
        var random = new Random(24023);
        var patterns = new Dictionary<string, (int X, int Y)[]>
        {
            ["horizontal"] = Enumerable.Range(0, 64).Select(i => (i < 32 ? 4 : -4, 0)).ToArray(),
            ["vertical"] = Enumerable.Range(0, 64).Select(i => (0, i < 32 ? -5 : 5)).ToArray(),
            ["diagonal"] = Enumerable.Range(0, 64).Select(i => (i % 2 == 0 ? 2 : -2, i % 2 == 0 ? 2 : -2)).ToArray(),
            ["unequal-axis-reversals"] = Enumerable.Range(0, 64).Select(i => (i % 3 == 0 ? -9 : 9, i < 32 ? 2 : -2)).ToArray(),
            ["tiny-corrections"] = Enumerable.Range(0, 64).Select(i => i < 8 ? (32, -21) : (i % 2 == 0 ? 1 : -1, i % 3 == 0 ? 1 : -1)).ToArray(),
            ["irregular-burst"] = Enumerable.Range(0, 64).Select(_ => (random.Next(-15, 16), random.Next(-15, 16))).ToArray(),
            ["circle"] = Curve(false),
            ["figure-eight"] = Curve(true)
        };
        long allocatedBefore = GC.GetTotalAllocatedBytes();
        var clock = Stopwatch.StartNew();
        double cpuBefore = Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
        int checkedEvents = 0;
        var durations = new List<double>();
        foreach ((string name, var points) in patterns)
        foreach (double speed in new[] { 0.5, 1.0, 2.0, 10.0 })
        {
            long offset = 0;
            var packets = points.Select((p, i) =>
            {
                // Equal-time bursts, sub-ms times, an intentional hold, and restart.
                offset += i == 31 ? 40_000 : i % 5 == 0 ? 0 : 1_250;
                return new MouseTrajectoryPacket((int)(offset / 10_000), p.X, p.Y, i) { OffsetTicks = offset };
            }).ToArray();
            var player = new TracePlayer();
            using var control = new MacroController();
            var context = new MacroContext(new NoCapture(), control) { InputPlayer = player, PlaybackSpeed = speed };
            var step = new MouseTrajectoryStep([new(0, 0, 0), new((int)(offset / 10_000), 1, 1)])
            { Mode = TrajectoryPlaybackMode.PacketReference, Packets = packets };
            var watch = Stopwatch.StartNew();
            await step.ExecuteAsync(context);
            durations.Add(watch.Elapsed.TotalMilliseconds);
            player.Moves.Should().Equal(points.Where(p => p.X != 0 || p.Y != 0), $"{name} at {speed}x must preserve intermediate motion");
            checkedEvents += player.Moves.Count;
            // Packet storage must retain sub-ms offsets, order, and precise raw deltas.
            var serializer = new MacroJsonSerializer();
            var loaded = (MouseTrajectoryStep)serializer.Deserialize(serializer.Serialize(new MacroModel { Steps = [step] })).Steps[0];
            loaded.Packets.Should().Equal(packets);
        }
        double cpuMs = Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds - cpuBefore;
        output.WriteLine($"seed=24023; patterns={patterns.Count}; speeds=0.5,1,2,10; emitted events verified={checkedEvents}; wallMs={clock.Elapsed.TotalMilliseconds:F1}; cpuMs={cpuMs:F1}; allocatedBytes={GC.GetTotalAllocatedBytes() - allocatedBefore}; medianTraceMs={durations.Order().ElementAt(durations.Count / 2):F2}; maxTraceMs={durations.Max():F2}; receiver=fake relative-input sink; real-target response=unverified");
    }

    [Fact]
    public async Task PacketCancellation_EmitsOnlyPrefix_AndNoFinalResidual()
    {
        using var cancel = new CancellationTokenSource();
        var player = new TracePlayer { OnMove = count => { if (count == 17) cancel.Cancel(); } };
        var packets = Enumerable.Range(0, 200).Select(i => new MouseTrajectoryPacket(0, i % 2 == 0 ? 1 : -1, 1, i) { OffsetTicks = i * 100 }).ToArray();
        var step = new MouseTrajectoryStep([new(0, 0, 0), new(2, 0, 200)]) { Mode = TrajectoryPlaybackMode.PacketReference, Packets = packets };
        var watch = Stopwatch.StartNew();
        await step.ExecuteAsync(new MacroContext(new NoCapture()) { InputPlayer = player }, cancel.Token);
        player.Moves.Should().Equal(packets.Take(17).Select(p => (p.Dx, p.Dy)));
        output.WriteLine($"cancel after packet17; emitted17/200; wallMs={watch.Elapsed.TotalMilliseconds:F3}; residualPackets=0");
    }

    [Fact]
    public async Task InvalidPacketTimeline_IsRejectedBeforeInput()
    {
        var player = new TracePlayer();
        var step = new MouseTrajectoryStep([new(0, 0, 0), new(2, 4, 4)])
        {
            Mode = TrajectoryPlaybackMode.PacketReference,
            Packets = [new(1, 2, 2, 0) { OffsetTicks = 15000 }, new(2, 2, 2, 1) { OffsetTicks = 1000 }]
        };
        Func<Task> play = () => step.ExecuteAsync(new MacroContext(new NoCapture()) { InputPlayer = player });
        await play.Should().ThrowAsync<InvalidOperationException>();
        player.Moves.Should().BeEmpty();
    }

    private static (int, int)[] Curve(bool figureEight)
    {
        int x = 0, y = 0;
        return Enumerable.Range(1, 64).Select(i =>
        {
            double angle = i * Math.PI * 2 / 64;
            int nextX = (int)Math.Round(40 * Math.Sin(angle));
            int nextY = (int)Math.Round(30 * (figureEight ? Math.Sin(2 * angle) : 1 - Math.Cos(angle)));
            var delta = (nextX - x, nextY - y); x = nextX; y = nextY; return delta;
        }).ToArray();
    }
    private sealed class TracePlayer : IInputPlayer
    {
        public List<(int X, int Y)> Moves { get; } = [];
        public Action<int>? OnMove;
        public bool IsPlaying => false;
        public void SendRelativeMove(int dx, int dy) { Moves.Add((dx, dy)); OnMove?.Invoke(Moves.Count); }
        public Task PlayAsync(IReadOnlyList<InputEvent> events, PlaybackOptions? options = null, CancellationToken ct = default) => throw new NotSupportedException();
        public void SendClick(MouseButton button = MouseButton.Left, int x = -1, int y = -1, bool doubleClick = false) => throw new NotSupportedException();
        public void ReleaseAllHeldKeys() { }
        public void Dispose() { }
    }
    private sealed class NoCapture : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => false;
        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
