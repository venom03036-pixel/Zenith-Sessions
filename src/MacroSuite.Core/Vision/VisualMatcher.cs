using System.Drawing;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;

namespace MacroSuite.Core.Vision;

/// <summary>
/// Shared visual matcher for legacy Manhattan color waits and perceptual Visual Wait.
/// Cheap stages run first; CIEDE2000 and AI only after CIE76 (or Manhattan) passes.
/// </summary>
public sealed class VisualMatcher
{
    public VisualMatchDiagnostics? LastDiagnostics { get; private set; }

    public async Task<bool> WaitAsync(MacroContext context, VisualMatchOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        int attempts = 1;
        if (options.OnTimeout == VisualTimeoutBehavior.Retry)
            attempts += Math.Clamp(options.RetryCount, 0, 20);

        for (int i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (context.IsAborted)
                throw new OperationCanceledException("Macro execution was aborted.", ct);

            if (await WaitOnceAsync(context, options, ct).ConfigureAwait(false))
                return true;
        }

        return false;
    }

    public async Task<bool> EvaluateOnceAsync(MacroContext context, VisualMatchOptions options, CancellationToken ct)
        => await EvaluateCheapAsync(context, options, SrgbLabConvert.ToLab(options.R, options.G, options.B), ct)
            .ConfigureAwait(false);

    private async Task<bool> WaitOnceAsync(MacroContext context, VisualMatchOptions options, CancellationToken ct)
    {
        var referenceLab = SrgbLabConvert.ToLab(options.R, options.G, options.B);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, context.PanicToken, context.StopToken);

        if (options.AiEnabled)
        {
            context.WriteTrace(
                "VisualDetection",
                "AI fallback is disabled for deterministic color/template waits; use an AI action for semantic vision.",
                ExecutionTraceLevel.Warning);
        }

        var policy = new ConditionWaitPolicy
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Max(0, options.TimeoutMs)),
            PollInterval = TimeSpan.FromMilliseconds(
                ConditionReliability.ClampPollIntervalMs(
                    options.PollIntervalMs,
                    ConditionReliability.DefaultVisualPollIntervalMs)),
            RequiredTrueConfirmations = options.RequiredConfirmations,
            RequiredFalseConfirmations = options.RequiredFalseConfirmations,
            StabilityDurationMs = options.StabilityDurationMs,
            Invert = false,
            Phase = "VisualWait"
        };

        return await ConditionWaitEvaluator.WaitAsync(
            context,
            (ctx, token) => EvaluateCheapAsync(ctx, options, referenceLab, token),
            policy,
            linked.Token).ConfigureAwait(false);
    }

    private async Task<bool> EvaluateCheapAsync(
        MacroContext context,
        VisualMatchOptions options,
        LabColor referenceLab,
        CancellationToken ct)
    {
        bool isColorPresent = options.Kind == VisualMatchKind.ColorPresent;
        bool isRegion = options.Kind == VisualMatchKind.Region
                        && options.TemplateBgra != null
                        && options.TemplateWidth > 0
                        && options.TemplateHeight > 0;

        if (isColorPresent)
        {
            int radius = Math.Clamp(options.SearchRadius, 1, 400);
            var rect = options.SearchScope == VisualSearchScope.Nearby
                ? new Rectangle(
                    options.X - radius,
                    options.Y - radius,
                    Math.Max(1, options.Width + (radius * 2)),
                    Math.Max(1, options.Height + (radius * 2)))
                : new Rectangle(
                    options.X,
                    options.Y,
                    Math.Max(1, options.Width),
                    Math.Max(1, options.Height));
            using var scan = options.SearchScope == VisualSearchScope.Screen
                ? await context.CaptureService.CaptureScreenAsync(ct).ConfigureAwait(false)
                : await context.CaptureService.CaptureRegionAsync(rect, ct).ConfigureAwait(false);
            ColorComparisonMode mode = options.Algorithm == VisualColorAlgorithm.LegacyManhattan
                ? ColorComparisonMode.Rgb
                : options.ComparisonMode;
            if (options.MaximumAccuracy && mode == ColorComparisonMode.Cie76)
                mode = ColorComparisonMode.CieDe2000;
            double tolerance = options.Algorithm == VisualColorAlgorithm.LegacyManhattan
                ? options.LegacyTolerance
                : SrgbLabConvert.SimilarityPercentToDeltaE76(options.SimilarityPercent);
            int stride = options.SampleStride > 1
                ? Math.Clamp(options.SampleStride, 1, 64)
                : ColorDetector.ComputeSampleStride(scan.Width, scan.Height);
            var result = ColorDetector.DetectRegion(
                scan,
                new Rectangle(0, 0, scan.Width, scan.Height),
                new PixelColor(options.R, options.G, options.B),
                new ColorMatchSettings
                {
                    Mode = mode,
                    Tolerance = tolerance,
                    MinimumMatchPercentage = Math.Clamp(options.RequiredPixelFraction, 0.0001, 1),
                    SampleStride = stride,
                    AdditionalTargets = options.AdditionalTargets
                },
                new Rectangle(scan.OriginX, scan.OriginY, scan.Width, scan.Height));
            context.RecordDetection(result);
            var diagnostics = new VisualMatchDiagnostics
            {
                Mode = "ColorPresent",
                Algorithm = result.DetectionMethod,
                SimilarityPercent = options.SimilarityPercent,
                DeltaEThreshold = result.Threshold,
                SimilarityScore = result.MatchPercentage * 100,
                Matched = result.Matched,
                SampledPixels = result.SampledPixels,
                ComparedPixels = result.SampledPixels,
                RegionWidth = scan.Width,
                RegionHeight = scan.Height,
                ReferenceHex = $"#{options.R:X2}{options.G:X2}{options.B:X2}"
            };
            LastDiagnostics = diagnostics;
            context.RecordVisualDiagnostics(diagnostics);
            return result.Matched;
        }

        if (!isRegion)
        {
            int pad = Math.Max(0, options.NeighborhoodSize / 2);
            var captureRect = new Rectangle(
                options.X - pad,
                options.Y - pad,
                Math.Max(1, 1 + (pad * 2)),
                Math.Max(1, 1 + (pad * 2)));

            using var frame = await context.CaptureService.CaptureRegionAsync(captureRect, ct).ConfigureAwait(false);
            int lx = options.X - captureRect.X;
            int ly = options.Y - captureRect.Y;

            if (options.Algorithm == VisualColorAlgorithm.LegacyManhattan)
            {
                var result = ColorDetector.DetectPixel(
                    frame,
                    lx,
                    ly,
                    new PixelColor(options.R, options.G, options.B),
                    new ColorMatchSettings
                    {
                        Mode = ColorComparisonMode.Rgb,
                        Tolerance = options.LegacyTolerance,
                        MinimumMatchPercentage = 1
                    },
                    new Rectangle(frame.OriginX, frame.OriginY, frame.Width, frame.Height));
                context.RecordDetection(result);
                return result.Matched;
            }

            bool neighborhood = PerceptualColorMatcher.MatchNeighborhood(
                frame, lx, ly, options.NeighborhoodSize, in referenceLab,
                options.SimilarityPercent, options.MaximumAccuracy, 0.55,
                $"#{options.R:X2}{options.G:X2}{options.B:X2}",
                out VisualMatchDiagnostics diagnostics);
            LastDiagnostics = diagnostics;
            context.RecordVisualDiagnostics(diagnostics);
            var detection = ColorDetector.DetectPixel(
                frame,
                lx,
                ly,
                new PixelColor(options.R, options.G, options.B),
                new ColorMatchSettings
                {
                    Mode = options.MaximumAccuracy ? ColorComparisonMode.CieDe2000 : ColorComparisonMode.Cie76,
                    Tolerance = SrgbLabConvert.SimilarityPercentToDeltaE76(options.SimilarityPercent),
                    MinimumMatchPercentage = 1
                },
                new Rectangle(frame.OriginX, frame.OriginY, frame.Width, frame.Height));
            context.RecordDetection(detection with { Matched = neighborhood });
            return neighborhood;
        }

        if (VisualMatchOptions.IsTemplateOversized(options.TemplateWidth, options.TemplateHeight, options.MaxTemplateEdge))
            return false;

        var target = new Rectangle(options.X, options.Y, options.TemplateWidth, options.TemplateHeight);
        CaptureFrame haystack;
        int originX;
        int originY;

        if (options.SearchScope == VisualSearchScope.Screen)
        {
            haystack = await context.CaptureService.CaptureScreenAsync(ct).ConfigureAwait(false);
            originX = haystack.OriginX;
            originY = haystack.OriginY;
        }
        else
        {
            var search = options.SearchScope == VisualSearchScope.Nearby
                ? new Rectangle(
                    target.X - Math.Clamp(options.SearchRadius, 1, 400),
                    target.Y - Math.Clamp(options.SearchRadius, 1, 400),
                    target.Width + (Math.Clamp(options.SearchRadius, 1, 400) * 2),
                    target.Height + (Math.Clamp(options.SearchRadius, 1, 400) * 2))
                : target;
            haystack = await context.CaptureService.CaptureRegionAsync(search, ct).ConfigureAwait(false);
            originX = haystack.OriginX;
            originY = haystack.OriginY;
        }

        using (haystack)
        {
            if (options.SearchScope == VisualSearchScope.Fixed)
            {
                bool matched = PerceptualColorMatcher.MatchRegion(
                    haystack, options.X - originX, options.Y - originY,
                    options.TemplateBgra!, options.TemplateWidth, options.TemplateHeight,
                    options.SimilarityPercent, options.RequiredPixelFraction, options.MaximumAccuracy,
                    out VisualMatchDiagnostics diagnostics);
                LastDiagnostics = diagnostics;
                context.RecordVisualDiagnostics(diagnostics);
                return matched;
            }

            bool found = PerceptualColorMatcher.FindRegion(
                haystack,
                options.TemplateBgra!,
                options.TemplateWidth,
                options.TemplateHeight,
                options.SimilarityPercent,
                options.RequiredPixelFraction,
                options.MaximumAccuracy,
                ct,
                out VisualMatchDiagnostics searchDiagnostics);
            LastDiagnostics = searchDiagnostics;
            context.RecordVisualDiagnostics(searchDiagnostics);
            return found;
        }
    }

}
