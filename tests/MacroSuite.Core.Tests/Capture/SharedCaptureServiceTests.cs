using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Capture;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Tests.TestHelpers;
using Xunit;

namespace MacroSuite.Core.Tests.Capture;

public sealed class SharedCaptureServiceTests
{
    private sealed class CountingCaptureService : IScreenCaptureService
    {
        public int RegionCaptures;
        public List<Rectangle> Regions { get; } = [];
        public TaskCompletionSource? FirstCaptureRelease { get; init; }
        public TaskCompletionSource FirstCaptureEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;

        public async Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
        {
            int count = Interlocked.Increment(ref RegionCaptures);
            Regions.Add(region);
            if (count == 1 && FirstCaptureRelease is { } release)
            {
                FirstCaptureEntered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            return Create(region);
        }

        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
            => Task.FromResult(Create(new Rectangle(-100, 0, 200, 100)));

        private static CaptureFrame Create(Rectangle region)
        {
            CaptureFrame source = TestFrameFactory.CreateSolidColor(
                region.Width,
                region.Height,
                12,
                34,
                56,
                source: CaptureSource.Gdi);
            return new CaptureFrameProxy(source, region.X, region.Y).Frame;
        }

        public void Dispose() { }
    }

    private sealed class CaptureFrameProxy
    {
        private sealed class Owner : System.Buffers.IMemoryOwner<byte>
        {
            private byte[]? _bytes;
            public Memory<byte> Memory => _bytes ?? Memory<byte>.Empty;
            public Owner(byte[] bytes) => _bytes = bytes;
            public void Dispose() => _bytes = null;
        }

        public CaptureFrame Frame { get; }

        public CaptureFrameProxy(CaptureFrame source, int x, int y)
        {
            byte[] bytes = source.Span.ToArray();
            Frame = new CaptureFrame(
                source.Width,
                source.Height,
                source.Stride,
                source.Format,
                source.Source,
                new Owner(bytes),
                originX: x,
                originY: y);
            source.Dispose();
        }
    }

    private sealed class BlockingCaptureService : IScreenCaptureService
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;

        public async Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable.");
        }

        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
            => CaptureRegionAsync(new Rectangle(0, 0, 1, 1), ct);

        public void Dispose() { }
    }

    [Fact]
    public async Task ConcurrentRegions_ShareOneBoundingCapture()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new CountingCaptureService { FirstCaptureRelease = release };
        await using var shared = new SharedCaptureService(source);
        var requests = new List<Task<CaptureFrame>>();
        try
        {
            // Keep the real worker inside a primer capture, then queue both consumers
            // before its next drain. Task.Yield alone cannot establish that boundary.
            requests.Add(shared.CaptureRegionAsync(new Rectangle(-10, 0, 1, 1)));
            await source.FirstCaptureEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            requests.Add(shared.CaptureRegionAsync(new Rectangle(10, 10, 10, 10)));
            requests.Add(shared.CaptureRegionAsync(new Rectangle(30, 10, 10, 10)));
            release.TrySetResult();
            CaptureFrame[] frames = await Task.WhenAll(requests);

            source.RegionCaptures.Should().Be(2, "one primer plus exactly one shared bounding capture");
            source.Regions.Should().Equal(new Rectangle(-10, 0, 1, 1), new Rectangle(10, 10, 30, 10));
            frames[1].OriginX.Should().Be(10);
            frames[2].OriginX.Should().Be(30);
            PixelAtZero(frames[1]).Should().Be((12, 34, 56));
            PixelAtZero(frames[2]).Should().Be((12, 34, 56));
        }
        finally
        {
            release.TrySetResult();
            foreach (Task<CaptureFrame> request in requests)
                (await request.WaitAsync(TimeSpan.FromSeconds(2))).Dispose();
        }
    }

    [Fact]
    public async Task DisposeAsync_CancelsRequestAlreadyBeingCaptured()
    {
        var source = new BlockingCaptureService();
        var shared = new SharedCaptureService(source);
        Task<CaptureFrame> pending = shared.CaptureRegionAsync(new Rectangle(0, 0, 5, 5));
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await shared.DisposeAsync();

        var act = async () => await pending;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ColorConditions_ShareOneSourceCapture_AndDoNotStartSchedulerLoop()
    {
        var source = new CountingCaptureService();
        await using var shared = new SharedCaptureService(source);
        using var scheduler = new CaptureScheduler(shared);
        var context = new MacroSuite.Core.Execution.MacroContext(shared);
        var left = new MacroSuite.Core.Rules.ColorCondition(new Point(10, 10), 12, 34, 56);
        var right = new MacroSuite.Core.Rules.ColorCondition(new Point(30, 10), 12, 34, 56);

        Task<bool> a = left.EvaluateAsync(context);
        Task<bool> b = right.EvaluateAsync(context);
        (await a).Should().BeTrue();
        (await b).Should().BeTrue();

        source.RegionCaptures.Should().BeGreaterThan(0);
        scheduler.IsRunning.Should().BeFalse();
        scheduler.SubscriberCount.Should().Be(0);
        context.LastColorDetection.Should().NotBeNull();
        context.LastColorDetection!.Matched.Should().BeTrue();
    }

    [Fact]
    public async Task SchedulerWrappingSharedCapture_DoesNotCreateASecondSourceLoop()
    {
        var source = new CountingCaptureService();
        await using var shared = new SharedCaptureService(source);
        await using var scheduler = new CaptureScheduler(shared);
        var sub = new SchedulerProbe(new Rectangle(0, 0, 8, 8), TimeSpan.FromMilliseconds(15));
        scheduler.Subscribe(sub);

        using CaptureFrame pulled = await shared.CaptureRegionAsync(new Rectangle(4, 0, 8, 8));
        await Task.Delay(80);

        source.RegionCaptures.Should().BeGreaterThan(0);
        sub.Frames.Should().BeGreaterThan(0);
        pulled.Width.Should().Be(8);
        scheduler.Unsubscribe(sub.Id);
    }

    private sealed class SchedulerProbe : ICaptureSubscriber
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Rectangle Region { get; }
        public TimeSpan PollingInterval { get; }
        public int Frames { get; private set; }

        public SchedulerProbe(Rectangle region, TimeSpan interval)
        {
            Region = region;
            PollingInterval = interval;
        }

        public void OnFrameCaptured(CaptureFrame frame, Rectangle subRegion) => Frames++;
    }

    private static (byte R, byte G, byte B) PixelAtZero(CaptureFrame frame)
    {
        ReadOnlySpan<byte> span = frame.Span;
        return (span[2], span[1], span[0]);
    }
}
