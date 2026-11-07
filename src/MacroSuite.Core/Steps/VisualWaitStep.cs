using System.Text.Json.Serialization;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Vision;

namespace MacroSuite.Core.Steps;

/// <summary>
/// Waits until a perceptual visual match is found. Matching is delegated to <see cref="VisualMatcher"/>.
/// </summary>
public sealed class VisualWaitStep : MacroStep
{
    public string Mode { get; init; } = "Pixel";
    public string SearchMode { get; init; } = "Fixed";
    public string MatchingMethod { get; init; } = "ColorImage";
    public string Quality { get; init; } = "Balanced";
    public string OnTimeout { get; init; } = "Stop";

    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; } = 1;
    public int Height { get; init; } = 1;
    public byte R { get; init; }
    public byte G { get; init; }
    public byte B { get; init; }
    public int SimilarityPercent { get; init; } = 90;
    public int NeighborhoodSize { get; init; } = 1;
    public double RequiredPixelFraction { get; init; } = 0.85;
    public int PollIntervalMs { get; init; } = 150;
    public int RequiredConfirmations { get; init; } = 1;
    public int RequiredFalseConfirmations { get; init; } = 1;
    public int StabilityDurationMs { get; init; }
    public int TimeoutMs { get; init; } = 10_000;
    public int RetryCount { get; init; } = 3;
    public int SearchRadius { get; init; } = 50;
    public int AiMinIntervalMs { get; init; } = 1500;
    public ColorComparisonMode ComparisonMode { get; init; } = ColorComparisonMode.Cie76;
    public int SampleStride { get; init; } = 1;
    public IReadOnlyList<PixelColor>? AdditionalTargets { get; init; }
    public ConditionFailurePolicy FailurePolicy { get; init; } = ConditionFailurePolicy.Stop;
    public byte[]? TemplateBgra { get; init; }
    public int TemplateWidth { get; init; }
    public int TemplateHeight { get; init; }

    [JsonIgnore]
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    public VisualWaitStep()
    {
        Label = "Wait for Visual Match";
    }

    public override async Task ExecuteAsync(MacroContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsEnabled || context.IsAborted)
            return;

        var matcher = new VisualMatcher();
        bool matched = await matcher.WaitAsync(context, ToOptions(), ct).ConfigureAwait(false);
        if (matched)
        {
            await DetectionCursorMover.MoveToLastDetectionAsync(context, ct, requireCoordinates: false)
                .ConfigureAwait(false);
            return;
        }

        if (ct.IsCancellationRequested || context.IsAborted)
            throw new OperationCanceledException("Visual wait was aborted.", ct);

        if (string.Equals(OnTimeout, "Skip", StringComparison.OrdinalIgnoreCase)
            || FailurePolicy == ConditionFailurePolicy.Skip)
            return;

        throw new TimeoutException(
            $"Visual wait timed out after {TimeoutMs}ms ({Mode} {Hex} at ({X},{Y})).");
    }

    public VisualMatchOptions ToOptions()
    {
        var scope = SearchMode.ToLowerInvariant() switch
        {
            "nearby" => VisualSearchScope.Nearby,
            "screen" => VisualSearchScope.Screen,
            _ => VisualSearchScope.Fixed
        };
        var timeout = ResolveTimeoutBehavior();

        return new VisualMatchOptions
        {
            Kind = Mode.Equals("Region", StringComparison.OrdinalIgnoreCase)
                ? VisualMatchKind.Region
                : VisualMatchKind.Pixel,
            SearchScope = scope,
            Algorithm = VisualColorAlgorithm.Perceptual,
            MaximumAccuracy = Quality.Equals("MaximumAccuracy", StringComparison.OrdinalIgnoreCase),
            AiEnabled = MatchingMethod.Contains("Ai", StringComparison.OrdinalIgnoreCase),
            ComparisonMode = ComparisonMode,
            SampleStride = ConditionReliability.ClampSampleStride(SampleStride),
            X = X,
            Y = Y,
            Width = Width,
            Height = Height,
            R = R,
            G = G,
            B = B,
            SimilarityPercent = SimilarityPercent,
            NeighborhoodSize = NeighborhoodSize,
            RequiredPixelFraction = RequiredPixelFraction,
            AdditionalTargets = AdditionalTargets,
            PollIntervalMs = PollIntervalMs,
            RequiredConfirmations = RequiredConfirmations,
            RequiredFalseConfirmations = RequiredFalseConfirmations,
            StabilityDurationMs = StabilityDurationMs,
            TimeoutMs = TimeoutMs,
            OnTimeout = timeout,
            FailurePolicy = FailurePolicy == ConditionFailurePolicy.Stop
                ? ConditionReliability.FromVisualTimeoutBehavior(timeout)
                : FailurePolicy,
            RetryCount = RetryCount,
            SearchRadius = SearchRadius,
            AiMinIntervalMs = AiMinIntervalMs,
            TemplateBgra = TemplateBgra,
            TemplateWidth = TemplateWidth,
            TemplateHeight = TemplateHeight
        };
    }

    private VisualTimeoutBehavior ResolveTimeoutBehavior()
    {
        if (FailurePolicy != ConditionFailurePolicy.Stop)
            return ConditionReliability.ToVisualTimeoutBehavior(FailurePolicy);

        return OnTimeout.ToLowerInvariant() switch
        {
            "skip" => VisualTimeoutBehavior.Skip,
            "retry" => VisualTimeoutBehavior.Retry,
            _ => VisualTimeoutBehavior.Stop
        };
    }
}
