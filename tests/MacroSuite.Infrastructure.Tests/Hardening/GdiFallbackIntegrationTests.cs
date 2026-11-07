using System.Buffers;
using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Infrastructure.Capture;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Hardening;

public class GdiFallbackIntegrationTests
{
    private sealed class ArrayMemoryOwner : IMemoryOwner<byte>
    {
        public Memory<byte> Memory { get; }
        public ArrayMemoryOwner(int size) => Memory = new byte[size];
        public void Dispose() { }
    }

    private sealed class MockCaptureEngine : IScreenCaptureService
    {
        public CaptureSource CurrentSource { get; }
        public bool IsSupported => true;
        public int CallCount { get; private set; }
        public bool ShouldFail { get; set; }

        public MockCaptureEngine(CaptureSource source) => CurrentSource = source;

        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
        {
            CallCount++;
            if (ShouldFail)
            {
                throw new InvalidOperationException($"Capture failure simulated on {CurrentSource}");
            }

            int stride = region.Width * 4;
            var owner = new ArrayMemoryOwner(stride * region.Height);
            // Paint a pixel green (R=0, G=255, B=0, A=255)
            owner.Memory.Span[0] = 0;   // B
            owner.Memory.Span[1] = 255; // G
            owner.Memory.Span[2] = 0;   // R
            owner.Memory.Span[3] = 255; // A

            var frame = new CaptureFrame(region.Width, region.Height, stride, FramePixelFormat.Bgra32, CurrentSource, owner);
            return Task.FromResult(frame);
        }

        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default) =>
            CaptureRegionAsync(new Rectangle(0, 0, 1920, 1080), ct);

        public void Dispose() { }
    }

    [Fact]
    public async Task MacroExecution_WhenDxgiFails_SeamlesslyFallsBackToGdi_AndSucceeds()
    {
        using var dxgi = new MockCaptureEngine(CaptureSource.Dxgi) { ShouldFail = true };
        using var gdi = new MockCaptureEngine(CaptureSource.Gdi) { ShouldFail = false };
        using var coordinator = new FallbackCaptureCoordinator(dxgi, gdi)
        {
            AllowGdiFallback = true,
            PrimaryRetryBackoff = TimeSpan.FromSeconds(30)
        };

        var controller = new MacroController();
        var context = new MacroContext(coordinator, controller);

        // Step requires capture (Color condition wait)
        var condition = new ColorCondition(new Point(0, 0), 0, 255, 0);
        var step = new WaitUntilStep(condition)
        {
            Timeout = TimeSpan.FromSeconds(1)
        };

        var macro = new MacroModel
        {
            Name = "GDI Fallback Macro",
            Steps = new List<MacroStep> { step }
        };

        var executor = new MacroExecutor();
        await executor.ExecuteAsync(macro, context);

        executor.State.Should().Be(ExecutionState.Completed);
        dxgi.CallCount.Should().Be(1);
        gdi.CallCount.Should().BeGreaterThanOrEqualTo(1);
        coordinator.CurrentSource.Should().Be(CaptureSource.Gdi);
    }

    [Fact]
    public async Task MacroExecution_WhenDxgiFails_AndGdiDisabled_HaltsWithError()
    {
        using var dxgi = new MockCaptureEngine(CaptureSource.Dxgi) { ShouldFail = true };
        using var gdi = new MockCaptureEngine(CaptureSource.Gdi) { ShouldFail = false };
        using var coordinator = new FallbackCaptureCoordinator(dxgi, gdi)
        {
            AllowGdiFallback = false // Disallow fallback per user configuration
        };

        var controller = new MacroController();
        var context = new MacroContext(coordinator, controller);

        var condition = new ColorCondition(new Point(0, 0), 0, 255, 0);
        var step = new WaitUntilStep(condition)
        {
            Timeout = TimeSpan.FromSeconds(1)
        };

        var macro = new MacroModel
        {
            Name = "Disabled Fallback Macro",
            Steps = new List<MacroStep> { step }
        };

        var executor = new MacroExecutor();
        var act = () => executor.ExecuteAsync(macro, context);

        await act.Should().ThrowAsync<InvalidOperationException>();
        executor.State.Should().Be(ExecutionState.Faulted);
        gdi.CallCount.Should().Be(0);
    }
}
