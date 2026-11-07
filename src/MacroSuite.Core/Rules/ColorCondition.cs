using System.Drawing;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Vision;

namespace MacroSuite.Core.Rules;

/// <summary>
/// Condition that checks whether a specified screen point or region matches a target RGB color.
/// </summary>
public sealed class ColorCondition : ICondition
{
    /// <summary>Screen region to check, or null if checking a single pixel.</summary>
    public Rectangle? Region { get; }

    /// <summary>Single pixel coordinate to check if <see cref="Region"/> is null.</summary>
    public Point? Location { get; }

    /// <summary>Target Red component (0-255).</summary>
    public byte R { get; }

    /// <summary>Target Green component (0-255).</summary>
    public byte G { get; }

    /// <summary>Target Blue component (0-255).</summary>
    public byte B { get; }

    /// <summary>Allowable Manhattan distance tolerance per color channel.</summary>
    public int Tolerance { get; }

    /// <summary>Minimum fraction of pixels (0.0 to 1.0) in region that must match.</summary>
    public double MinMatchPercentage { get; }

    /// <summary>Explicit color-distance algorithm used by this condition.</summary>
    public ColorComparisonMode ComparisonMode { get; }

    /// <summary>Pixel sampling stride for regions. One scans every pixel.</summary>
    public int SampleStride { get; }

    /// <summary>Optional extra target colors. A pixel matches if it is close to any palette entry.</summary>
    public IReadOnlyList<PixelColor> AdditionalTargets { get; }

    /// <summary>Interval between re-checks when this condition is used by a wait or loop.</summary>
    public int PollIntervalMs { get; }

    /// <summary>Consecutive true evaluations required before the condition is considered stable.</summary>
    public int RequiredConfirmations { get; }

    /// <summary>Consecutive false evaluations required before absence is considered stable.</summary>
    public int RequiredFalseConfirmations { get; }

    /// <summary>Minimum time the result must hold before succeeding. Zero disables the window.</summary>
    public int StabilityDurationMs { get; }

    /// <summary>Wall-clock cap when this condition is polled. Zero means no extra cap beyond the parent step.</summary>
    public int TimeoutMs { get; }

    /// <summary>What the parent wait/loop should do when this condition fails or times out.</summary>
    public ConditionFailurePolicy FailurePolicy { get; }

    /// <inheritdoc/>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Description { get; }

    /// <summary>
    /// Deserialization constructor for ColorCondition.
    /// </summary>
    [System.Text.Json.Serialization.JsonConstructor]
    public ColorCondition(
        Rectangle? region,
        Point? location,
        byte r,
        byte g,
        byte b,
        int tolerance = 20,
        double minMatchPercentage = 1.0,
        ColorComparisonMode comparisonMode = ColorComparisonMode.Rgb,
        int sampleStride = 1,
        IReadOnlyList<PixelColor>? additionalTargets = null,
        int pollIntervalMs = ConditionReliability.DefaultPollIntervalMs,
        int requiredConfirmations = ConditionReliability.DefaultConfirmations,
        int requiredFalseConfirmations = ConditionReliability.DefaultConfirmations,
        int stabilityDurationMs = 0,
        int timeoutMs = ConditionReliability.DefaultTimeoutMs,
        ConditionFailurePolicy failurePolicy = ConditionFailurePolicy.Default)
    {
        Region = region;
        Location = location;
        R = r;
        G = g;
        B = b;
        Tolerance = Math.Max(0, tolerance);
        MinMatchPercentage = Math.Clamp(minMatchPercentage, 0.0, 1.0);
        ComparisonMode = comparisonMode;
        SampleStride = ConditionReliability.ClampSampleStride(sampleStride);
        PixelColor[] palette = ColorPalette.Combine(new PixelColor(r, g, b), additionalTargets);
        AdditionalTargets = palette.Length > 1 ? palette[1..] : Array.Empty<PixelColor>();
        PollIntervalMs = ConditionReliability.ClampPollIntervalMs(pollIntervalMs);
        RequiredConfirmations = ConditionReliability.ClampConfirmations(requiredConfirmations);
        RequiredFalseConfirmations = ConditionReliability.ClampConfirmations(requiredFalseConfirmations);
        StabilityDurationMs = ConditionReliability.ClampStabilityMs(stabilityDurationMs);
        TimeoutMs = ConditionReliability.ClampTimeoutMs(timeoutMs);
        FailurePolicy = failurePolicy;

        if (region.HasValue)
        {
            Description = AdditionalTargets.Count == 0
                ? $"Region [{region.Value.X},{region.Value.Y},{region.Value.Width},{region.Value.Height}] matches RGB({r},{g},{b}) (≥{MinMatchPercentage:P0}) ±{tolerance}"
                : $"Region [{region.Value.X},{region.Value.Y},{region.Value.Width},{region.Value.Height}] matches RGB({r},{g},{b}) or {AdditionalTargets.Count} other color(s) (≥{MinMatchPercentage:P0}) ±{tolerance}";
        }
        else if (location.HasValue)
        {
            Description = AdditionalTargets.Count == 0
                ? $"Pixel at ({location.Value.X}, {location.Value.Y}) matches RGB({r},{g},{b}) ±{tolerance}"
                : $"Pixel at ({location.Value.X}, {location.Value.Y}) matches RGB({r},{g},{b}) or {AdditionalTargets.Count} other color(s) ±{tolerance}";
        }
        else
        {
            Description = AdditionalTargets.Count == 0
                ? $"Matches RGB({r},{g},{b}) ±{tolerance}"
                : $"Matches RGB({r},{g},{b}) or {AdditionalTargets.Count} other color(s) ±{tolerance}";
        }
    }

    /// <summary>
    /// Creates a pixel-based color condition.
    /// </summary>
    public ColorCondition(Point location, byte r, byte g, byte b, int tolerance = 20)
        : this(null, location, r, g, b, tolerance, 1.0)
    {
    }

    /// <summary>
    /// Creates a region-based color condition.
    /// </summary>
    public ColorCondition(Rectangle region, byte r, byte g, byte b, int tolerance = 20, double minMatchPercentage = 1.0)
        : this(region, null, r, g, b, tolerance, minMatchPercentage)
    {
    }

    /// <inheritdoc/>
    public async Task<ConditionEvaluation> EvaluateDetailedAsync(MacroContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        if (context.IsAborted)
            throw new OperationCanceledException("Macro execution was aborted.", ct);

        Rectangle captureArea = Region ?? (Location.HasValue
            ? new Rectangle(Location.Value.X, Location.Value.Y, 1, 1)
            : Rectangle.Empty);

        if (captureArea.Width <= 0 || captureArea.Height <= 0)
        {
            return ConditionEvaluation.Fail("ColorCaptureRegionInvalid");
        }

        var settings = new ColorMatchSettings
        {
            Mode = ComparisonMode,
            Tolerance = Tolerance,
            MinimumMatchPercentage = MinMatchPercentage,
            SampleStride = Region.HasValue
                ? Math.Max(SampleStride, ColorDetector.ComputeSampleStride(captureArea.Width, captureArea.Height))
                : SampleStride,
            AdditionalTargets = AdditionalTargets
        };
        var target = new PixelColor(R, G, B);
        try
        {
            using var frame = await context.CaptureService.CaptureRegionAsync(captureArea, ct).ConfigureAwait(false);
            var actualCaptureArea = new Rectangle(frame.OriginX, frame.OriginY, frame.Width, frame.Height);
            ColorDetectionResult result = Location.HasValue && Region == null
                ? ColorDetector.DetectPixel(frame, 0, 0, target, settings, actualCaptureArea)
                : ColorDetector.DetectRegion(
                    frame,
                    new Rectangle(0, 0, frame.Width, frame.Height),
                    target,
                    settings,
                    actualCaptureArea);

            PublishResult(context, result);
            return result.Matched ? ConditionEvaluation.Satisfied : ConditionEvaluation.Unsatisfied;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var result = new ColorDetectionResult
            {
                Matched = false,
                Target = target,
                Threshold = Tolerance,
                CaptureRegion = captureArea,
                DetectionMethod = ComparisonMode.ToString(),
                Error = ex.Message
            };
            PublishResult(context, result);
            context.WriteTrace(
                "ColorDetection",
                $"Capture or comparison failed for {captureArea}.",
                ExecutionTraceLevel.Error,
                data: result,
                exception: ex);
            return ConditionEvaluation.Fail("ColorDetectionFailed", ex);
        }
    }

    private static void PublishResult(MacroContext context, ColorDetectionResult result)
    {
        context.RecordDetection(result);
        context.WriteTrace(
            "ColorDetection",
            result.Matched
                ? $"Color matched ({result.MatchPercentage:P1}, distance {result.Distance:F2}/{result.Threshold:F2})."
                : $"Color not matched ({result.MatchPercentage:P1}, distance {result.Distance:F2}/{result.Threshold:F2}).",
            data: result);
    }
}
