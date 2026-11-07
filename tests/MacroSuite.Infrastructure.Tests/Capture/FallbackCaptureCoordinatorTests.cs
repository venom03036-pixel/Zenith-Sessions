using System.Buffers;
using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.Capture;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Capture;

public sealed class FallbackCaptureCoordinatorTests
{
    private sealed class ArrayMemoryOwner : IMemoryOwner<byte>
    {
        public Memory<byte> Memory { get; }

        public ArrayMemoryOwner(int size)
        {
            Memory = new byte[size];
        }

        public void Dispose() { }
    }

    private sealed class MockCaptureService : IScreenCaptureService
    {
        public CaptureSource CurrentSource { get; }
        public bool IsSupported { get; set; } = true;
        public int CaptureCallCount { get; private set; }
        public bool ThrowOnCapture { get; set; }
        public bool IsDisposed { get; private set; }

        public MockCaptureService(CaptureSource source)
        {
            CurrentSource = source;
        }

        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
        {
            CaptureCallCount++;
            if (ThrowOnCapture)
            {
                throw new InvalidOperationException("Simulated capture failure");
            }

            int stride = region.Width * 4;
            var owner = new ArrayMemoryOwner(stride * region.Height);
            var frame = new CaptureFrame(region.Width, region.Height, stride, FramePixelFormat.Bgra32, CurrentSource, owner);
            return Task.FromResult(frame);
        }

        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
        {
            return CaptureRegionAsync(new Rectangle(0, 0, 1920, 1080), ct);
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    [Fact]
    public async Task PrimarySucceeds_ReturnsPrimaryFrame_AndDoesNotCallFallback()
    {
        using var primary = new MockCaptureService(CaptureSource.Dxgi);
        using var fallback = new MockCaptureService(CaptureSource.Gdi);
        using var coordinator = new FallbackCaptureCoordinator(primary, fallback);

        using var frame = await coordinator.CaptureRegionAsync(new Rectangle(0, 0, 100, 100));

        frame.Source.Should().Be(CaptureSource.Dxgi);
        primary.CaptureCallCount.Should().Be(1);
        fallback.CaptureCallCount.Should().Be(0);
        coordinator.LastPrimaryFailureUtc.Should().BeNull();
        coordinator.CurrentSource.Should().Be(CaptureSource.Dxgi);
    }

    [Fact]
    public async Task PrimaryFails_WhenGdiFallbackEnabled_CallsFallbackAndSetsLastFailure()
    {
        using var primary = new MockCaptureService(CaptureSource.Dxgi) { ThrowOnCapture = true };
        using var fallback = new MockCaptureService(CaptureSource.Gdi);
        using var coordinator = new FallbackCaptureCoordinator(primary, fallback)
        {
            AllowGdiFallback = true
        };

        using var frame = await coordinator.CaptureRegionAsync(new Rectangle(0, 0, 100, 100));

        frame.Source.Should().Be(CaptureSource.Gdi);
        primary.CaptureCallCount.Should().Be(1);
        fallback.CaptureCallCount.Should().Be(1);
        coordinator.LastPrimaryFailureUtc.Should().NotBeNull();
        coordinator.CurrentSource.Should().Be(CaptureSource.Gdi);
    }

    [Fact]
    public async Task PrimaryFails_WhenGdiFallbackDisabled_ThrowsAndDoesNotCallFallback()
    {
        using var primary = new MockCaptureService(CaptureSource.Dxgi) { ThrowOnCapture = true };
        using var fallback = new MockCaptureService(CaptureSource.Gdi);
        using var coordinator = new FallbackCaptureCoordinator(primary, fallback)
        {
            AllowGdiFallback = false
        };

        var act = () => coordinator.CaptureRegionAsync(new Rectangle(0, 0, 100, 100));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*GDI fallback is disabled*");

        primary.CaptureCallCount.Should().Be(1);
        fallback.CaptureCallCount.Should().Be(0);
    }

    [Fact]
    public async Task BackoffPeriod_RoutesDirectlyToFallbackWithoutCallingPrimary()
    {
        using var primary = new MockCaptureService(CaptureSource.Dxgi) { ThrowOnCapture = true };
        using var fallback = new MockCaptureService(CaptureSource.Gdi);
        using var coordinator = new FallbackCaptureCoordinator(primary, fallback)
        {
            AllowGdiFallback = true,
            PrimaryRetryBackoff = TimeSpan.FromMinutes(1)
        };

        // First attempt: primary fails, falls back to GDI
        using (var f1 = await coordinator.CaptureRegionAsync(new Rectangle(0, 0, 50, 50)))
        {
            f1.Source.Should().Be(CaptureSource.Gdi);
        }

        primary.CaptureCallCount.Should().Be(1);
        fallback.CaptureCallCount.Should().Be(1);

        // Second attempt: during backoff, primary should NOT be called at all
        using (var f2 = await coordinator.CaptureRegionAsync(new Rectangle(0, 0, 50, 50)))
        {
            f2.Source.Should().Be(CaptureSource.Gdi);
        }

        primary.CaptureCallCount.Should().Be(1); // Still 1!
        fallback.CaptureCallCount.Should().Be(2);
    }

    [Fact]
    public async Task AfterBackoffExpires_RetriesPrimary_AndClearsFailureOnSuccess()
    {
        using var primary = new MockCaptureService(CaptureSource.Dxgi) { ThrowOnCapture = true };
        using var fallback = new MockCaptureService(CaptureSource.Gdi);
        using var coordinator = new FallbackCaptureCoordinator(primary, fallback)
        {
            AllowGdiFallback = true,
            PrimaryRetryBackoff = TimeSpan.FromMilliseconds(50)
        };

        // 1. Primary fails
        using (var f1 = await coordinator.CaptureRegionAsync(new Rectangle(0, 0, 50, 50)))
        {
            f1.Source.Should().Be(CaptureSource.Gdi);
        }

        coordinator.LastPrimaryFailureUtc.Should().NotBeNull();

        // Fix primary
        primary.ThrowOnCapture = false;

        // Wait for backoff to expire
        await Task.Delay(80);

        // 2. Primary retried and succeeds
        using (var f2 = await coordinator.CaptureRegionAsync(new Rectangle(0, 0, 50, 50)))
        {
            f2.Source.Should().Be(CaptureSource.Dxgi);
        }

        primary.CaptureCallCount.Should().Be(2);
        coordinator.LastPrimaryFailureUtc.Should().BeNull();
        coordinator.CurrentSource.Should().Be(CaptureSource.Dxgi);
    }

    [Fact]
    public void Dispose_DisposesBothPrimaryAndFallback()
    {
        var primary = new MockCaptureService(CaptureSource.Dxgi);
        var fallback = new MockCaptureService(CaptureSource.Gdi);
        var coordinator = new FallbackCaptureCoordinator(primary, fallback);

        coordinator.Dispose();

        primary.IsDisposed.Should().BeTrue();
        fallback.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task EmptyDxgiBuffer_FailsClosed_AndFallsBackToGdi()
    {
        using var primary = new EmptyDxgiCaptureService();
        using var fallback = new MockCaptureService(CaptureSource.Gdi);
        using var coordinator = new FallbackCaptureCoordinator(primary, fallback)
        {
            AllowGdiFallback = true
        };

        using CaptureFrame frame = await coordinator.CaptureRegionAsync(new Rectangle(0, 0, 8, 8));

        frame.Source.Should().Be(CaptureSource.Gdi);
        primary.CaptureCallCount.Should().Be(1);
        fallback.CaptureCallCount.Should().Be(1);
        coordinator.LastPrimaryFailureUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task EmptyDxgiBuffer_WhenFallbackDisabled_Throws()
    {
        using var primary = new EmptyDxgiCaptureService();
        using var fallback = new MockCaptureService(CaptureSource.Gdi);
        using var coordinator = new FallbackCaptureCoordinator(primary, fallback)
        {
            AllowGdiFallback = false
        };

        var act = () => coordinator.CaptureRegionAsync(new Rectangle(0, 0, 8, 8));
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*GDI fallback is disabled*");
        fallback.CaptureCallCount.Should().Be(0);
    }

    private sealed class EmptyDxgiCaptureService : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Dxgi;
        public bool IsSupported { get; set; } = true;
        public int CaptureCallCount { get; private set; }

        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
        {
            CaptureCallCount++;
            int stride = region.Width * 4;
            var owner = new ArrayMemoryOwner(stride * region.Height);
            var frame = new CaptureFrame(region.Width, region.Height, stride, FramePixelFormat.Bgra32, CaptureSource.Dxgi, owner);
            frame.Dispose();
            return Task.FromResult(frame);
        }

        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
            => CaptureRegionAsync(new Rectangle(0, 0, 8, 8), ct);

        public void Dispose() { }
    }
}
