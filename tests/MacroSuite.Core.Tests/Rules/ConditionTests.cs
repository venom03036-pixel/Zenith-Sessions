using System.Drawing;
using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Tests.TestHelpers;
using MacroSuite.Core.Vision;
using Xunit;

namespace MacroSuite.Core.Tests.Rules;

public sealed class ConditionTests
{
    private sealed class FakeCaptureService : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Dxgi;
        public bool IsSupported => true;
        public byte R { get; set; } = 255;
        public byte G { get; set; } = 0;
        public byte B { get; set; } = 0;

        public int OriginX { get; set; }
        public int OriginY { get; set; }

        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
        {
            var frame = TestFrameFactory.CreateSolidColor(region.Width, region.Height, R, G, B, originX: OriginX + region.X, originY: OriginY + region.Y);
            return Task.FromResult(frame);
        }

        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
        {
            return CaptureRegionAsync(new Rectangle(0, 0, 1920, 1080), ct);
        }

        public void Dispose() { }
    }

    private sealed class FakeImageMatcher : IImageMatcher
    {
        public MatchResult Result { get; set; } = MatchResult.Failed;

        public Task<MatchResult> MatchAsync(
            CaptureFrame frame,
            string templatePath,
            double minConfidence = 0.8,
            Rectangle? searchRegion = null,
            CancellationToken ct = default)
            => Task.FromResult(Result);

        public Task<MatchResult> MatchAsync(
            CaptureFrame frame,
            byte[] templateImageBytes,
            double minConfidence = 0.8,
            Rectangle? searchRegion = null,
            CancellationToken ct = default)
            => Task.FromResult(Result);

        public void Dispose() { }
    }

    private sealed class StubCondition : ICondition
    {
        private readonly bool _result;
        public int EvaluationCount { get; private set; }
        public string Description { get; }

        public StubCondition(bool result, string desc = "Stub")
        {
            _result = result;
            Description = desc;
        }

        public Task<ConditionEvaluation> EvaluateDetailedAsync(MacroContext context, CancellationToken ct = default)
        {
            EvaluationCount++;
            return Task.FromResult(_result ? ConditionEvaluation.Satisfied : ConditionEvaluation.Unsatisfied);
        }
    }

    private sealed class AlwaysFailedCondition : ICondition
    {
        public string Description => "always failed";
        public Task<ConditionEvaluation> EvaluateDetailedAsync(MacroContext context, CancellationToken ct = default)
            => Task.FromResult(ConditionEvaluation.Fail("boom"));
    }

    private static MacroContext CreateContext(IScreenCaptureService capture)
    {
        return new MacroContext(capture);
    }

    [Fact]
    public async Task ColorCondition_PixelMatch_ReturnsTrue()
    {
        using var capture = new FakeCaptureService { R = 100, G = 150, B = 200 };
        var context = CreateContext(capture);
        var condition = new ColorCondition(new Point(5, 5), 100, 150, 200);

        bool result = await condition.EvaluateAsync(context);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task ColorCondition_PixelMismatch_ReturnsFalse()
    {
        using var capture = new FakeCaptureService { R = 100, G = 150, B = 200 };
        var context = CreateContext(capture);
        var condition = new ColorCondition(new Point(5, 5), 0, 0, 0);

        bool result = await condition.EvaluateAsync(context);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task AndCondition_AllTrue_ReturnsTrue()
    {
        using var capture = new FakeCaptureService();
        var context = CreateContext(capture);
        var c1 = new StubCondition(true);
        var c2 = new StubCondition(true);
        var and = new AndCondition(c1, c2);

        bool result = await and.EvaluateAsync(context);

        result.Should().BeTrue();
        c1.EvaluationCount.Should().Be(1);
        c2.EvaluationCount.Should().Be(1);
    }

    [Fact]
    public async Task AndCondition_FirstFalse_ShortCircuitsSecond()
    {
        using var capture = new FakeCaptureService();
        var context = CreateContext(capture);
        var c1 = new StubCondition(false);
        var c2 = new StubCondition(true);
        var and = new AndCondition(c1, c2);

        bool result = await and.EvaluateAsync(context);

        result.Should().BeFalse();
        c1.EvaluationCount.Should().Be(1);
        c2.EvaluationCount.Should().Be(0); // Short circuit!
    }

    [Fact]
    public async Task OrCondition_FirstTrue_ShortCircuitsSecond()
    {
        using var capture = new FakeCaptureService();
        var context = CreateContext(capture);
        var c1 = new StubCondition(true);
        var c2 = new StubCondition(false);
        var or = new OrCondition(c1, c2);

        bool result = await or.EvaluateAsync(context);

        result.Should().BeTrue();
        c1.EvaluationCount.Should().Be(1);
        c2.EvaluationCount.Should().Be(0); // Short circuit!
    }

    [Fact]
    public async Task OrCondition_AllFalse_ReturnsFalse()
    {
        using var capture = new FakeCaptureService();
        var context = CreateContext(capture);
        var c1 = new StubCondition(false);
        var c2 = new StubCondition(false);
        var or = new OrCondition(c1, c2);

        bool result = await or.EvaluateAsync(context);

        result.Should().BeFalse();
        c1.EvaluationCount.Should().Be(1);
        c2.EvaluationCount.Should().Be(1);
    }

    [Fact]
    public async Task NotCondition_InvertsResult()
    {
        using var capture = new FakeCaptureService();
        var context = CreateContext(capture);
        var notTrue = new NotCondition(new StubCondition(true));
        var notFalse = new NotCondition(new StubCondition(false));

        (await notTrue.EvaluateAsync(context)).Should().BeFalse();
        (await notFalse.EvaluateAsync(context)).Should().BeTrue();
    }

    [Fact]
    public async Task VisualPresence_ColorPresent_UsesSharedMatcher()
    {
        using var capture = new FakeCaptureService { R = 220, G = 40, B = 40 };
        var context = CreateContext(capture);
        var present = new VisualPresenceCondition
        {
            Kind = VisualPresenceKind.ColorPresent,
            X = 0,
            Y = 0,
            Width = 40,
            Height = 20,
            R = 220,
            G = 40,
            B = 40,
            SimilarityPercent = 90,
            RequiredPixelFraction = 0.05
        };
        var absent = new VisualPresenceCondition
        {
            Kind = VisualPresenceKind.ColorAbsent,
            X = 0,
            Y = 0,
            Width = 40,
            Height = 20,
            R = 220,
            G = 40,
            B = 40,
            SimilarityPercent = 90,
            RequiredPixelFraction = 0.05
        };

        (await present.EvaluateAsync(context)).Should().BeTrue();
        (await absent.EvaluateAsync(context)).Should().BeFalse();
    }

    [Fact]
    public async Task ImageAbsent_MatcherFailed_IsNotTreatedAsGone()
    {
        using var capture = new FakeCaptureService();
        using var matcher = new FakeImageMatcher { Result = MatchResult.Failed };
        var context = new MacroContext(capture) { ImageMatcher = matcher };
        var absent = new VisualPresenceCondition
        {
            Kind = VisualPresenceKind.ImageAbsent,
            TemplatePath = "template.png",
            X = 0,
            Y = 0,
            Width = 40,
            Height = 20
        };

        var evaluation = await absent.EvaluateDetailedAsync(context);
        evaluation.IsFailed.Should().BeTrue();
        var act = () => absent.EvaluateAsync(context);
        await act.Should().ThrowAsync<ConditionEvaluationException>();
    }

    [Fact]
    public async Task ImageCondition_ValidNonMatch_IsUnsatisfiedNotFailed()
    {
        using var capture = new FakeCaptureService();
        using var matcher = new FakeImageMatcher { Result = MatchResult.NotFound };
        var context = new MacroContext(capture) { ImageMatcher = matcher };
        var condition = new ImageCondition("template.png", minConfidence: 0.8);

        var evaluation = await condition.EvaluateDetailedAsync(context);
        evaluation.Outcome.Should().Be(ConditionOutcome.Unsatisfied);
        (await condition.EvaluateAsync(context)).Should().BeFalse();
    }

    [Fact]
    public async Task NotCondition_DoesNotInvertFailure()
    {
        using var capture = new FakeCaptureService();
        var notFailed = new NotCondition(new AlwaysFailedCondition());
        var context = CreateContext(capture);

        var evaluation = await notFailed.EvaluateDetailedAsync(context);
        evaluation.IsFailed.Should().BeTrue();
        evaluation.ErrorCode.Should().Be("boom");
    }

    [Fact]
    public async Task ImageCondition_OnMatch_WritesVirtualDesktopCenter()
    {
        using var capture = new FakeCaptureService { OriginX = 100, OriginY = 200 };
        using var matcher = new FakeImageMatcher
        {
            Result = new MatchResult(true, 0.93, new Rectangle(10, 20, 40, 30))
        };
        var context = new MacroContext(capture) { ImageMatcher = matcher };
        var condition = new ImageCondition("template.png", minConfidence: 0.8);

        bool matched = await condition.EvaluateAsync(context);

        matched.Should().BeTrue();
        context.Variables.Get<int>(ImageCondition.LastMatchXVariable).Should().Be(100 + 10 + 20);
        context.Variables.Get<int>(ImageCondition.LastMatchYVariable).Should().Be(200 + 20 + 15);
    }

    [Fact]
    public async Task ColorCondition_HsvAndLabModes_MatchNearColors()
    {
        using var capture = new FakeCaptureService { R = 250, G = 8, B = 4 };
        var hsv = new ColorCondition(null, new Point(1, 1), 255, 0, 0, tolerance: 20, comparisonMode: ColorComparisonMode.Hsv);
        var lab = new ColorCondition(null, new Point(1, 1), 255, 0, 0, tolerance: 12, comparisonMode: ColorComparisonMode.Cie76);
        var context = CreateContext(capture);

        (await hsv.EvaluateAsync(context)).Should().BeTrue();
        (await lab.EvaluateAsync(context)).Should().BeTrue();
        context.LastColorDetection.Should().NotBeNull();
        context.LastColorDetection!.DetectionMethod.Should().Be(nameof(ColorComparisonMode.Cie76));
    }

    [Fact]
    public async Task ColorCondition_Palette_MatchesSecondaryColor()
    {
        using var capture = new FakeCaptureService { R = 0, G = 200, B = 0 };
        var context = CreateContext(capture);
        var condition = new ColorCondition(
            new Rectangle(0, 0, 8, 8),
            null,
            255, 0, 0,
            tolerance: 10,
            minMatchPercentage: 0.5,
            additionalTargets: [new PixelColor(0, 200, 0)]);

        (await condition.EvaluateAsync(context)).Should().BeTrue();
    }

    [Fact]
    public async Task ColorCondition_NegativeVirtualDesktopOrigin_WritesScreenPoint()
    {
        using var capture = new FakeCaptureService { R = 10, G = 20, B = 30, OriginX = 0, OriginY = 0 };
        var context = CreateContext(capture);
        var condition = new ColorCondition(new Point(-1900, 40), 10, 20, 30);

        (await condition.EvaluateAsync(context)).Should().BeTrue();
        context.LastColorDetection!.BestMatchPoint.Should().Be(new Point(-1900, 40));
        context.Variables.Get<int>(MacroSuite.Core.Execution.MacroVariableNames.LastDetectedX).Should().Be(-1900);
    }

    [Fact]
    public async Task ColorCondition_CaptureConfigurationFailure_DoesNotReturnSilentFalse()
    {
        using var capture = new FailingCaptureService();
        var context = CreateContext(capture);
        var condition = new ColorCondition(new Point(0, 0), 1, 2, 3);

        var act = () => condition.EvaluateAsync(context);
        var thrown = await act.Should().ThrowAsync<ConditionEvaluationException>();
        thrown.Which.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("configured");
    }

    private sealed class FailingCaptureService : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Dxgi;
        public bool IsSupported => true;
        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
            => throw new InvalidOperationException("Capture is not configured for the requested region.");
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
            => CaptureRegionAsync(new Rectangle(0, 0, 1, 1), ct);
        public void Dispose() { }
    }
}
