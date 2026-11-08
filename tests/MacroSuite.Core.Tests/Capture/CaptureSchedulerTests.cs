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

        /// <summary>
        /// Every frame handed to the scheduler, in order. Written on the capture loop's thread; read it only
        /// after the scheduler has been disposed (its loop task has then ended).
        /// </summary>
        public List<CaptureFrame> ReturnedFrames { get; } = new();

        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
        {
            CaptureCount++;
            var frame = TestFrameFactory.CreateSolidColor(region.Width, region.Height, 100, 100, 100);
            LastReturnedFrame = frame;
            ReturnedFrames.Add(frame);
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

    /// <summary>
    /// Holds the first frame it receives inside <see cref="OnFrameCaptured"/> until the test releases it,
    /// so the test can observe the scheduler while a fan-out is in progress.
    /// </summary>
    private sealed class GatedSubscriber : ICaptureSubscriber, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);

        public Guid Id { get; } = Guid.NewGuid();
        public Rectangle Region { get; } = new(0, 0, 10, 10);
        public TimeSpan PollingInterval { get; } = TimeSpan.FromMilliseconds(15);
        public TaskCompletionSource FrameHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnFrameCaptured(CaptureFrame frame, Rectangle subRegion)
        {
            FrameHeld.TrySetResult();
            _release.Wait(TimeSpan.FromSeconds(5));
        }

        public void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
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

        // The loop disposes a frame only after its fan-out (Rule 13), so reading the newest frame while the
        // loop still runs races that fan-out (the 100 ms delay above and the loop's 15 ms wait fire from the
        // same timer queue, so the two often wake together). DisposeAsync returns only after the loop task has
        // ended, and by then every frame the service handed out has been through the loop's finally.
        await scheduler.DisposeAsync();

        fakeService.LastReturnedFrame!.IsDisposed.Should().BeTrue();
        fakeService.ReturnedFrames.Should().OnlyContain(frame => frame.IsDisposed);
    }

    [Fact]
    public async Task DisposeAsync_WaitsForAFrameStillBeingFannedOut_ThenEveryReturnedFrameIsDisposed()
    {
        using var fakeService = new FakeCaptureService();
        // Declared before the scheduler so that scope exit disposes the scheduler (and ends its loop) before
        // the gate the loop may still be waiting on.
        using var sub = new GatedSubscriber();
        await using var scheduler = new CaptureScheduler(fakeService);

        scheduler.Subscribe(sub);
        await sub.FrameHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Rule 13: the frame stays the scheduler's, and valid, until the subscriber's handler returns.
        var heldFrame = fakeService.LastReturnedFrame!;
        try
        {
            heldFrame.IsDisposed.Should().BeFalse();
        }
        finally
        {
            sub.Release();
        }

        await scheduler.DisposeAsync();

        heldFrame.IsDisposed.Should().BeTrue();
        fakeService.ReturnedFrames.Should().NotBeEmpty().And.OnlyContain(frame => frame.IsDisposed);
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
