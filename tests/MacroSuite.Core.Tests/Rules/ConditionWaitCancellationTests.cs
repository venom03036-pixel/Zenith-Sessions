using FluentAssertions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Tests.TestHelpers;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Models;
using System.Drawing;
using Xunit;

namespace MacroSuite.Core.Tests.Rules;

public sealed class ConditionWaitCancellationTests
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

    private sealed class SlowFalseCondition : ICondition
    {
        public string Description => "slow false";
        public async Task<ConditionEvaluation> EvaluateDetailedAsync(MacroContext context, CancellationToken ct = default)
        {
            await Task.Delay(15, ct).ConfigureAwait(false);
            return ConditionEvaluation.Unsatisfied;
        }
    }

    [Fact]
    public async Task WaitAsync_CancelBeforeWait_ThrowsOperationCanceled()
    {
        using var capture = new DummyCapture();
        var controller = new MacroController();
        var context = new MacroContext(capture, controller);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await ConditionWaitEvaluator.WaitAsync(
            context,
            (_, _) => Task.FromResult(false),
            new ConditionWaitPolicy
            {
                Timeout = TimeSpan.FromSeconds(2),
                PollInterval = TimeSpan.FromMilliseconds(20),
                RequiredTrueConfirmations = 1,
                RequiredFalseConfirmations = 1,
                Phase = "WaitUntil"
            },
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WaitAsync_CancelDuringPollDelay_ThrowsOperationCanceled()
    {
        using var capture = new DummyCapture();
        var context = new MacroContext(capture);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));

        var act = async () => await ConditionWaitEvaluator.WaitDetailedAsync(
            context,
            new SlowFalseCondition(),
            new ConditionWaitPolicy
            {
                Timeout = TimeSpan.FromSeconds(5),
                PollInterval = TimeSpan.FromMilliseconds(200),
                RequiredTrueConfirmations = 1,
                RequiredFalseConfirmations = 1,
                Phase = "WaitUntil"
            },
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
