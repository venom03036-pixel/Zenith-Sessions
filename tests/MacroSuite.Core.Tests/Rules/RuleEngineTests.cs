using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Tests.TestHelpers;
using Xunit;

namespace MacroSuite.Core.Tests.Rules;

public sealed class RuleEngineTests
{
    private sealed class DummyCaptureService : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Dxgi;
        public bool IsSupported => true;
        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
            => Task.FromResult(TestFrameFactory.CreateSolidColor(region.Width, region.Height, 0, 0, 0));
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
            => Task.FromResult(TestFrameFactory.CreateSolidColor(10, 10, 0, 0, 0));
        public void Dispose() { }
    }

    private sealed class FlipsToTrueCondition : ICondition
    {
        private int _evalCount;
        private readonly int _targetCount;

        public string Description => "Flips to true";

        public FlipsToTrueCondition(int targetCount)
        {
            _targetCount = targetCount;
        }

        public Task<ConditionEvaluation> EvaluateDetailedAsync(MacroContext context, CancellationToken ct = default)
        {
            _evalCount++;
            return Task.FromResult(_evalCount >= _targetCount
                ? ConditionEvaluation.Satisfied
                : ConditionEvaluation.Unsatisfied);
        }
    }

    private sealed class NeverTrueCondition : ICondition
    {
        public string Description => "Never true";

        public Task<ConditionEvaluation> EvaluateDetailedAsync(MacroContext context, CancellationToken ct = default)
            => Task.FromResult(ConditionEvaluation.Unsatisfied);
    }

    [Fact]
    public async Task WaitUntilAsync_ImmediatelyTrue_ReturnsTrueWithoutDelay()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);
        var engine = new RuleEngine();
        var condition = new FlipsToTrueCondition(1); // true on first try

        bool result = await engine.WaitUntilAsync(condition, context, TimeSpan.FromSeconds(5));

        result.Should().BeTrue();
    }

    [Fact]
    public async Task WaitUntilAsync_EventuallyTrue_ReturnsTrueBeforeTimeout()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);
        var engine = new RuleEngine();
        var condition = new FlipsToTrueCondition(3); // true on 3rd evaluation

        bool result = await engine.WaitUntilAsync(
            condition,
            context,
            TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(15));

        result.Should().BeTrue();
    }

    [Fact]
    public async Task WaitUntilAsync_NeverTrue_TimesOutAndReturnsFalse()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);
        var engine = new RuleEngine();
        var condition = new NeverTrueCondition();

        bool result = await engine.WaitUntilAsync(
            condition,
            context,
            TimeSpan.FromMilliseconds(60),
            pollInterval: TimeSpan.FromMilliseconds(15));

        result.Should().BeFalse();
    }

    [Fact]
    public async Task WaitUntilAsync_CancellationSignaled_ThrowsOperationCanceled()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);
        var engine = new RuleEngine();
        var condition = new NeverTrueCondition();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));

        var act = async () => await engine.WaitUntilAsync(
            condition,
            context,
            TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(15),
            ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WaitUntilAsync_RequiresConsecutiveTrueConfirmations()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);
        var engine = new RuleEngine();
        var condition = new AlwaysTrueCondition();

        bool result = await engine.WaitUntilAsync(
            condition,
            context,
            TimeSpan.FromSeconds(2),
            pollInterval: TimeSpan.FromMilliseconds(10),
            requiredConfirmations: 3);

        result.Should().BeTrue();
        condition.Evaluations.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task WaitUntilAsync_InvertUsesFalseConfirmations()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);
        var engine = new RuleEngine();
        var condition = new NeverTrueCondition();

        bool result = await engine.WaitUntilAsync(
            condition,
            context,
            TimeSpan.FromSeconds(2),
            pollInterval: TimeSpan.FromMilliseconds(10),
            invert: true,
            requiredFalseConfirmations: 3);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task WaitUntilAsync_StabilityDurationRequiresHold()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);
        var engine = new RuleEngine();
        var condition = new AlwaysTrueCondition();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        bool result = await engine.WaitUntilAsync(
            condition,
            context,
            TimeSpan.FromSeconds(2),
            pollInterval: TimeSpan.FromMilliseconds(15),
            requiredConfirmations: 1,
            stabilityDurationMs: 80);

        sw.Stop();
        result.Should().BeTrue();
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(70);
        condition.Evaluations.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task WaitUntilAsync_MissResetsStabilityWindow()
    {
        using var capture = new DummyCaptureService();
        var context = new MacroContext(capture);
        var engine = new RuleEngine();
        var condition = new TrueThenFalseThenTrueCondition();

        bool result = await engine.WaitUntilAsync(
            condition,
            context,
            TimeSpan.FromSeconds(2),
            pollInterval: TimeSpan.FromMilliseconds(10),
            requiredConfirmations: 2,
            stabilityDurationMs: 0);

        result.Should().BeTrue();
        condition.Evaluations.Should().BeGreaterThanOrEqualTo(4);
    }

    private sealed class AlwaysTrueCondition : ICondition
    {
        public int Evaluations { get; private set; }
        public string Description => "Always true";
        public Task<ConditionEvaluation> EvaluateDetailedAsync(MacroContext context, CancellationToken ct = default)
        {
            Evaluations++;
            return Task.FromResult(ConditionEvaluation.Satisfied);
        }
    }

    private sealed class TrueThenFalseThenTrueCondition : ICondition
    {
        public int Evaluations { get; private set; }
        public string Description => "True, false, then true";
        public Task<ConditionEvaluation> EvaluateDetailedAsync(MacroContext context, CancellationToken ct = default)
        {
            Evaluations++;
            return Task.FromResult(Evaluations != 2
                ? ConditionEvaluation.Satisfied
                : ConditionEvaluation.Unsatisfied);
        }
    }
}
