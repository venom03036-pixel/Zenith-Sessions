using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Capture;
using MacroSuite.Core.Models;
using MacroSuite.Core.Tests.TestHelpers;
using Xunit;

namespace MacroSuite.Core.Tests.Capture;

public sealed class CaptureSchedulerTests
{
    private sealed class FakeCaptureService : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Dxgi;
        public bool IsSupported => true;
        public int CaptureCount { get; private set; }
        public CaptureFrame? LastReturnedFrame { get; private set; }

        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
        {
            CaptureCount++;
            var frame = TestFrameFactory.CreateSolidColor(region.Width, region.Height, 100, 100, 100);
            LastReturnedFrame = frame;
            return Task.FromResult(frame);
        }

        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
        {
            return CaptureRegionAsync(new Rectangle(0, 0, 1920, 1080), ct);
        }

        public void Dispose() { }
    }

    private sealed class TestSubscriber : ICaptureSubscriber
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Rectangle Region { get; }
        public TimeSpan PollingInterval { get; }
        public int FramesReceived { get; private set; }
        public Rectangle LastSubRegion { get; private set; }
        public bool ThrowOnFrame { get; set; }

        public TestSubscriber(Rectangle region, TimeSpan interval)
        {
            Region = region;
            PollingInterval = interval;
        }

        public void OnFrameCaptured(CaptureFrame frame, Rectangle subRegion)
        {
            if (ThrowOnFrame)
            {
                throw new InvalidOperationException("Simulated subscriber error");
            }

            FramesReceived++;
            LastSubRegion = subRegion;
        }
    }

    private sealed class SlowCancellationIgnoringCaptureService : IScreenCaptureService
    {
        private int _active;
        private int _maxActive;

        public TaskCompletionSource FirstCaptureStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MaxConcurrentCaptures => Volatile.Read(ref _maxActive);
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;

        public async Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
        {
            int active = Interlocked.Increment(ref _active);
            int observed;
            while (active > (observed = Volatile.Read(ref _maxActive)))
                Interlocked.CompareExchange(ref _maxActive, active, observed);
            FirstCaptureStarted.TrySetResult();
            try
            {
                await Task.Delay(40);
                return TestFrameFactory.CreateSolidColor(region.Width, region.Height, 1, 2, 3);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
            => CaptureRegionAsync(new Rectangle(0, 0, 1, 1), ct);

        public void Dispose() { }
    }

    [Fact]
    public void InitialState_HasZeroSubscribersAndIsNotRunning()
    {
        using var fakeService = new FakeCaptureService();
        using var scheduler = new CaptureScheduler(fakeService);

        scheduler.SubscriberCount.Should().Be(0);
        scheduler.IsRunning.Should().BeFalse();
        fakeService.CaptureCount.Should().Be(0);
    }

    [Fact]
    public async Task Subscribe_StartsLoopAndDeliversFrames()
    {
        using var fakeService = new FakeCaptureService();
        await using var scheduler = new CaptureScheduler(fakeService);

        var sub = new TestSubscriber(new Rectangle(10, 10, 50, 50), TimeSpan.FromMilliseconds(15));
        scheduler.Subscribe(sub);

        scheduler.SubscriberCount.Should().Be(1);
        scheduler.IsRunning.Should().BeTrue();

        // Wait a few cycles
        await Task.Delay(100);

        sub.FramesReceived.Should().BeGreaterThan(0);
        sub.LastSubRegion.Should().Be(new Rectangle(0, 0, 50, 50));
        fakeService.CaptureCount.Should().BeGreaterThan(0);

        // Frame should have been disposed after fanout
        fakeService.LastReturnedFrame!.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task Unsubscribe_WhenLastSubscriber_StopsLoop()
    {
        using var fakeService = new FakeCaptureService();
        await using var scheduler = new CaptureScheduler(fakeService);

        var sub = new TestSubscriber(new Rectangle(0, 0, 100, 100), TimeSpan.FromMilliseconds(15));
        scheduler.Subscribe(sub);
        await Task.Delay(50);

        scheduler.Unsubscribe(sub.Id).Should().BeTrue();
        scheduler.SubscriberCount.Should().Be(0);

        await Task.Delay(50);
        scheduler.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task FaultingSubscriber_DoesNotPreventDeliveryToOtherSubscribers()
    {
        using var fakeService = new FakeCaptureService();
        await using var scheduler = new CaptureScheduler(fakeService);

        var faultingSub = new TestSubscriber(new Rectangle(0, 0, 10, 10), TimeSpan.FromMilliseconds(15))
        {
            ThrowOnFrame = true
        };
        var healthySub = new TestSubscriber(new Rectangle(20, 20, 10, 10), TimeSpan.FromMilliseconds(15));

        scheduler.Subscribe(faultingSub);
        scheduler.Subscribe(healthySub);

        await Task.Delay(100);

        healthySub.FramesReceived.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RapidUnsubscribeAndResubscribe_DoesNotCreateConcurrentCaptureLoops()
    {
        var capture = new SlowCancellationIgnoringCaptureService();
        await using var scheduler = new CaptureScheduler(capture);
        var first = new TestSubscriber(new Rectangle(0, 0, 10, 10), TimeSpan.FromMilliseconds(10));
        var second = new TestSubscriber(new Rectangle(20, 20, 10, 10), TimeSpan.FromMilliseconds(10));

        scheduler.Subscribe(first);
        await capture.FirstCaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        scheduler.Unsubscribe(first.Id);
        scheduler.Subscribe(second);
        await Task.Delay(120);

        capture.MaxConcurrentCaptures.Should().Be(1);
        second.FramesReceived.Should().BeGreaterThan(0);
    }
}
