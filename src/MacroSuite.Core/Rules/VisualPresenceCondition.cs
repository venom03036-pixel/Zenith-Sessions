using System.Drawing;
using System.Text.Json.Serialization;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Vision;

namespace MacroSuite.Core.Rules;

public enum VisualPresenceKind
{
    ColorPresent = 0,
    ColorAbsent = 1,
    ImagePresent = 2,
    ImageAbsent = 3
}

/// <summary>
/// Shared visual presence/absence condition. Matching goes through <see cref="VisualMatcher"/>
/// (color) or <see cref="ImageCondition"/> (template) — not per-action capture loops.
/// </summary>
public sealed class VisualPresenceCondition : ICondition
{
    public VisualPresenceKind Kind { get; init; } = VisualPresenceKind.ColorPresent;
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; } = 80;
    public int Height { get; init; } = 40;
    public byte R { get; init; }
    public byte G { get; init; }
    public byte B { get; init; }
    public int SimilarityPercent { get; init; } = 90;
    public double RequiredPixelFraction { get; init; } = 0.08;
    public IReadOnlyList<PixelColor>? AdditionalTargets { get; init; }
    public string SearchMode { get; init; } = "Fixed";
    public string? TemplatePath { get; init; }
    public byte[]? TemplateBytes { get; init; }
    public double MinConfidence { get; init; } = 0.8;
    /// <summary>Defaults to CIE76 so existing perceptual presence checks keep their algorithm.</summary>
    public ColorComparisonMode ComparisonMode { get; init; } = ColorComparisonMode.Cie76;
    public int SampleStride { get; init; } = 1;
    public int PollIntervalMs { get; init; } = ConditionReliability.DefaultVisualPollIntervalMs;
    public int RequiredConfirmations { get; init; } = ConditionReliability.DefaultConfirmations;
    public int RequiredFalseConfirmations { get; init; } = ConditionReliability.DefaultConfirmations;
    public int StabilityDurationMs { get; init; }
    public int TimeoutMs { get; init; } = ConditionReliability.DefaultTimeoutMs;
    public ConditionFailurePolicy FailurePolicy { get; init; } = ConditionFailurePolicy.Default;

    [JsonIgnore]
    public string Description => Kind switch
    {
        VisualPresenceKind.ColorPresent => $"Color #{R:X2}{G:X2}{B:X2} present in ({X},{Y} {Width}×{Height})",
        VisualPresenceKind.ColorAbsent => $"Color #{R:X2}{G:X2}{B:X2} absent in ({X},{Y} {Width}×{Height})",
        VisualPresenceKind.ImagePresent => $"Image present ({TemplatePath ?? "template"})",
        VisualPresenceKind.ImageAbsent => $"Image absent ({TemplatePath ?? "template"})",
        _ => "Visual condition"
    };

    public async Task<ConditionEvaluation> EvaluateDetailedAsync(MacroContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        if (context.IsAborted)
            throw new OperationCanceledException("Macro execution was aborted.", ct);

        ConditionEvaluation present;
        try
        {
            if (Kind is VisualPresenceKind.ImagePresent or VisualPresenceKind.ImageAbsent)
            {
                if (context.ImageMatcher is null)
                    return ConditionEvaluation.Fail("ImageMatcherUnavailable");

                // Use the same scopes as color presence. In particular, Screen must
                // capture the full desktop rather than ImageCondition's foreground fallback.
                const int nearbyRadius = 50; // VisualMatchOptions.SearchRadius default.
                Rectangle region = string.Equals(SearchMode, "Nearby", StringComparison.OrdinalIgnoreCase)
                    ? new Rectangle(X - nearbyRadius, Y - nearbyRadius,
                        Math.Max(1, Width + nearbyRadius * 2), Math.Max(1, Height + nearbyRadius * 2))
                    : new Rectangle(X, Y, Math.Max(1, Width), Math.Max(1, Height));
                using var frame = string.Equals(SearchMode, "Screen", StringComparison.OrdinalIgnoreCase)
                    ? await context.CaptureService.CaptureScreenAsync(ct).ConfigureAwait(false)
                    : await context.CaptureService.CaptureRegionAsync(region, ct).ConfigureAwait(false);
                var image = new ImageCondition(TemplatePath, TemplateBytes, MinConfidence);
                present = frame is null
                    ? ConditionEvaluation.Fail("CaptureFrameUnavailable")
                    : await image.EvaluateFrameDetailedAsync(context, frame, null, ct).ConfigureAwait(false);
            }
            else
            {
                var matcher = new VisualMatcher();
                bool matched = await matcher.EvaluateOnceAsync(context, ToMatchOptions(), ct).ConfigureAwait(false);
                present = matched ? ConditionEvaluation.Satisfied : ConditionEvaluation.Unsatisfied;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.WriteTrace(
                "VisualDetection",
                $"Visual condition failed: {Description}.",
                ExecutionTraceLevel.Error,
                exception: ex);
            return ConditionEvaluation.Fail("VisualConditionFailed", ex);
        }

        if (present.IsFailed)
            return present;

        bool isAbsentKind = Kind is VisualPresenceKind.ColorAbsent or VisualPresenceKind.ImageAbsent;
        if (isAbsentKind)
        {
            // Failed already returned — never treat matcher/capture failure as gone/absent.
            return present.IsSatisfied ? ConditionEvaluation.Unsatisfied : ConditionEvaluation.Satisfied;
        }

        return present;
    }

    public VisualMatchOptions ToMatchOptions()
    {
        var scope = SearchMode.ToLowerInvariant() switch
        {
            "nearby" => VisualSearchScope.Nearby,
            "screen" => VisualSearchScope.Screen,
            _ => VisualSearchScope.Fixed
        };
        return new VisualMatchOptions
        {
            Kind = VisualMatchKind.ColorPresent,
            SearchScope = scope,
            Algorithm = VisualColorAlgorithm.Perceptual,
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
            RequiredPixelFraction = RequiredPixelFraction,
            AdditionalTargets = AdditionalTargets,
            PollIntervalMs = ConditionReliability.ClampPollIntervalMs(PollIntervalMs, ConditionReliability.DefaultVisualPollIntervalMs),
            RequiredConfirmations = ConditionReliability.ClampConfirmations(RequiredConfirmations),
            RequiredFalseConfirmations = ConditionReliability.ClampConfirmations(RequiredFalseConfirmations),
            StabilityDurationMs = ConditionReliability.ClampStabilityMs(StabilityDurationMs),
            TimeoutMs = ConditionReliability.ClampTimeoutMs(TimeoutMs),
            FailurePolicy = FailurePolicy,
            OnTimeout = ConditionReliability.ToVisualTimeoutBehavior(FailurePolicy)
        };
    }
}
