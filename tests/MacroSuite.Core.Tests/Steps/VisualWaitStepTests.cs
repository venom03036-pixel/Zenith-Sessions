using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Tests.TestHelpers;
using Xunit;

namespace MacroSuite.Core.Tests.Steps;

public sealed class VisualWaitStepTests
{
    private sealed class DummyCapture : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;
        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
            => Task.FromResult(TestFrameFactory.CreateSolidColor(region.Width, region.Height, 0, 0, 0));
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
            => Task.FromResult(TestFrameFactory.CreateSolidColor(8, 8, 0, 0, 0));
        public void Dispose() { }
    }

    [Fact]
    public async Task CancelBeforeWait_ThrowsOperationCanceled_NotTimeout()
    {
        using var capture = new DummyCapture();
        var context = new MacroContext(capture);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var step = new VisualWaitStep
        {
            R = 255,
            TimeoutMs = 5_000,
            PollIntervalMs = 20,
            OnTimeout = "Stop"
        };

        var act = async () => await step.ExecuteAsync(context, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PanicDuringWait_ThrowsOperationCanceled_NotTimeout()
    {
        using var capture = new DummyCapture();
        var controller = new MacroController();
        var context = new MacroContext(capture, controller);
        var step = new VisualWaitStep
        {
            R = 255,
            TimeoutMs = 5_000,
            PollIntervalMs = 20,
            OnTimeout = "Skip"
        };

        var execute = step.ExecuteAsync(context);
        await Task.Delay(30);
        controller.Panic();

        var act = async () => await execute;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SkipOnTimeout_DoesNotThrowWhenElapsed()
    {
        using var capture = new DummyCapture();
        var context = new MacroContext(capture);
        var step = new VisualWaitStep
        {
            R = 255,
            TimeoutMs = 40,
            PollIntervalMs = 10,
            OnTimeout = "Skip"
        };

        await step.ExecuteAsync(context);
    }
}
