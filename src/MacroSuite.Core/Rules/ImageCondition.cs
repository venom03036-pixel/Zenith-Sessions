using System.Drawing;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Rules;

/// <summary>
/// Condition that checks whether an image template appears on screen within confidence threshold.
/// </summary>
public sealed partial class ImageCondition : ICondition
{
    /// <summary>Execution variable written with the last successful match center X (virtual desktop pixels).</summary>
    public const string LastMatchXVariable = "LastImageMatchX";

    /// <summary>Execution variable written with the last successful match center Y (virtual desktop pixels).</summary>
    public const string LastMatchYVariable = "LastImageMatchY";

    /// <summary>Relative or absolute path to template image.</summary>
    public string? TemplatePath { get; }

    /// <summary>Raw byte content of the template image.</summary>
    public byte[]? TemplateBytes { get; }

    /// <summary>Minimum match confidence (0.0 to 1.0). Defaults to 0.8.</summary>
    public double MinConfidence { get; }

    /// <summary>Optional restricted search bounding box on screen.</summary>
    public Rectangle? SearchRegion { get; }

    /// <summary>Null preserves legacy scope, resolution and single-match semantics.</summary>
    public ImageScanOptions? ScanOptions { get; init; }

    /// <inheritdoc/>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Description { get; }

    /// <summary>
    /// Deserialization constructor for ImageCondition.
    /// </summary>
    [System.Text.Json.Serialization.JsonConstructor]
    public ImageCondition(string? templatePath, byte[]? templateBytes, double minConfidence = 0.8, Rectangle? searchRegion = null)
    {
        TemplatePath = templatePath;
        TemplateBytes = templateBytes;
        MinConfidence = Math.Clamp(minConfidence, 0.0, 1.0);
        SearchRegion = searchRegion;
        Description = !string.IsNullOrEmpty(templatePath)
            ? $"Image '{Path.GetFileName(templatePath)}' found (confidence ≥ {minConfidence:P0})"
            : $"In-memory image found (confidence ≥ {minConfidence:P0})";
    }

    /// <summary>
    /// Creates an image condition referencing a template file path.
    /// </summary>
    public ImageCondition(string templatePath, double minConfidence = 0.8, Rectangle? searchRegion = null)
        : this(templatePath, null, minConfidence, searchRegion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templatePath);
    }

    /// <summary>
    /// Creates an image condition using raw image bytes.
    /// </summary>
    public ImageCondition(byte[] templateBytes, double minConfidence = 0.8, Rectangle? searchRegion = null, string? name = null)
        : this(null, templateBytes, minConfidence, searchRegion)
    {
        ArgumentNullException.ThrowIfNull(templateBytes);
        if (name != null)
        {
            Description = $"In-memory image '{name}' found (confidence ≥ {minConfidence:P0})";
        }
    }

    /// <inheritdoc/>
    public async Task<ConditionEvaluation> EvaluateDetailedAsync(MacroContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        if (context.IsAborted)
            throw new OperationCanceledException("Macro execution was aborted.", ct);

        if (context.ImageMatcher is null)
        {
            context.WriteTrace(
                "ImageDetection",
                "Image matching is unavailable; condition failed closed.",
                ExecutionTraceLevel.Warning);
            return ConditionEvaluation.Fail("ImageMatcherUnavailable");
        }

        if (ScanOptions is not null)
            return await EvaluateConfiguredAsync(context, ct).ConfigureAwait(false);

        CaptureFrame? frame = null;
        try
        {
            if (SearchRegion.HasValue && SearchRegion.Value.Width > 0 && SearchRegion.Value.Height > 0)
            {
                frame = await context.CaptureService.CaptureRegionAsync(SearchRegion.Value, ct).ConfigureAwait(false);
            }
            else if (TryGetForegroundClientRegion(context, out Rectangle foreground))
            {
                frame = await context.CaptureService.CaptureRegionAsync(foreground, ct).ConfigureAwait(false);
            }
            else
            {
                frame = await context.CaptureService.CaptureScreenAsync(ct).ConfigureAwait(false);
            }

            if (frame is null || frame.IsDisposed)
            {
                return ConditionEvaluation.Fail("CaptureFrameUnavailable");
            }

            return await EvaluateFrameDetailedAsync(context, frame, null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.WriteTrace("ImageDetection", $"Image detection failed for '{Description}'.",
                ExecutionTraceLevel.Error, exception: ex);
            return ConditionEvaluation.Fail("ImageDetectionException", ex);
        }
        finally
        {
            frame?.Dispose();
        }
    }

    // A monitoring scan shares one owned capture across its templates (Rules 5/6/13).
    // The caller owns the frame; this method never disposes or retains it.
    internal async Task<ConditionEvaluation> EvaluateFrameDetailedAsync(
        MacroContext context, CaptureFrame frame, Rectangle? relativeRegion, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (context.IsAborted)
            throw new OperationCanceledException("Macro execution was aborted.", ct);
        if (context.ImageMatcher is null)
            return ConditionEvaluation.Fail("ImageMatcherUnavailable");
        if (frame.IsDisposed)
            return ConditionEvaluation.Fail("CaptureFrameUnavailable");

        try
        {

            MatchResult result;
            if (TemplateBytes != null)
            {
                result = await context.ImageMatcher.MatchAsync(frame, TemplateBytes, MinConfidence, relativeRegion, ct).ConfigureAwait(false);
            }
            else if (!string.IsNullOrEmpty(TemplatePath))
            {
                result = await context.ImageMatcher.MatchAsync(frame, TemplatePath, MinConfidence, relativeRegion, ct).ConfigureAwait(false);
            }
            else
            {
                return ConditionEvaluation.Fail("ImageTemplateMissing");
            }

            if (result.Status == MatchOutcome.Failed)
            {
                context.WriteTrace(
                    "ImageDetection",
                    $"Image matcher failed for '{Description}'.",
                    ExecutionTraceLevel.Error,
                    data: result);
                return ConditionEvaluation.Fail(result.ErrorCode ?? "ImageMatchFailed") with
                {
                    ErrorMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? null : $"{Description}: {result.ErrorMessage}"
                };
            }

            bool matched = result.Status == MatchOutcome.Matched && result.Confidence >= MinConfidence;
            if (matched)
            {
                int screenX = frame.OriginX + result.BoundingBox.X + (result.BoundingBox.Width / 2);
                int screenY = frame.OriginY + result.BoundingBox.Y + (result.BoundingBox.Height / 2);
                StoreMatch(context, screenX, screenY, result.Confidence);
            }

            context.WriteTrace(
                "ImageDetection",
                $"Image condition={matched}, confidence={result.Confidence:P1}, required={MinConfidence:P1}.",
                data: result);
            return matched ? ConditionEvaluation.Satisfied : ConditionEvaluation.Unsatisfied;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.WriteTrace(
                "ImageDetection",
                $"Image detection failed for '{Description}'.",
                ExecutionTraceLevel.Error,
                exception: ex);
            return ConditionEvaluation.Fail("ImageDetectionException", ex);
        }
    }

    internal static void StoreMatch(MacroContext context, int screenX, int screenY, double confidence)
    {
        context.Variables.Set(LastMatchXVariable, screenX);
        context.Variables.Set(LastMatchYVariable, screenY);
        context.Variables.Set(MacroVariableNames.LastDetectedX, screenX);
        context.Variables.Set(MacroVariableNames.LastDetectedY, screenY);
        context.Variables.Set(MacroVariableNames.LastDetectedXAlias, screenX);
        context.Variables.Set(MacroVariableNames.LastDetectedYAlias, screenY);
        context.Variables.Set(MacroVariableNames.LastDetectedConfidence, confidence);
    }

    internal Rectangle? GetCaptureRegion(MacroContext context, bool repeating = false)
    {
        ImageSearchScope scope = ScanOptions?.SearchScope ?? ImageSearchScope.Legacy;
        if (scope == ImageSearchScope.Anywhere)
            return ScanOptions?.MonitorBounds;
        if (scope == ImageSearchScope.TargetWindow)
            return TryGetForegroundClientRegion(context, out Rectangle target) ? target
                : throw new InvalidOperationException("The target window is unavailable. Focus the target window or choose a placed search area.");
        if (SearchRegion is { Width: > 0, Height: > 0 } region) return region;
        if (scope == ImageSearchScope.PlacedArea)
            throw new InvalidOperationException("Place a valid image search area before running this macro.");
        if (scope != ImageSearchScope.Legacy)
            throw new InvalidOperationException("This macro uses an unsupported image search scope.");
        return !repeating && TryGetForegroundClientRegion(context, out Rectangle foreground) ? foreground : null;
    }

    internal ImageScanRequest CreateScanRequest(Guid id, MacroContext context, bool repeating, int interval,
        Rectangle? targetWindowRegion = null)
    {
        var resolution = ScanOptions?.Resolution ?? ImageScanResolution.Original;
        if (!Enum.IsDefined(resolution)) throw new InvalidOperationException("Unsupported image scan resolution.");
        return new ImageScanRequest(id, TemplatePath, TemplateBytes, MinConfidence,
            ScanOptions?.SearchScope == ImageSearchScope.TargetWindow && targetWindowRegion is { } target
                ? target : GetCaptureRegion(context, repeating), resolution,
            ScanOptions?.MatchHandling == ImageMatchHandling.All, Math.Clamp(interval, 10, 10_000));
    }

    private async Task<ConditionEvaluation> EvaluateConfiguredAsync(MacroContext context, CancellationToken ct)
    {
        if (context.ImageMatcher is not IImageScanProvider provider)
            return ConditionEvaluation.Fail("ImageScanProviderUnavailable");
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, context.StopToken, context.PanicToken);
            using var search = new ConfiguredImageSearch(this, context, provider, ConditionReliability.DefaultPollIntervalMs);
            return await search.EvaluateAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            context.WriteTrace("ImageDetection", $"Configured image scan failed for '{Description}'.", ExecutionTraceLevel.Error, exception: ex);
            return ConditionEvaluation.Fail("ImageDetectionException", ex);
        }
    }

    private static bool TryGetForegroundClientRegion(MacroContext context, out Rectangle region)
    {
        region = default;
        if (context.PlaybackSession?.Target?.Window != null)
        { region = context.PlaybackSession.GetTargetClientBounds(); return true; }
        if (context.PlaybackSession?.Target != null) return false;
        var svc = context.WindowCoordinates;
        if (svc is null)
            return false;

        IntPtr hwnd = svc.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !svc.IsWindowValid(hwnd))
            return false;

        (int width, int height) = svc.GetClientSize(hwnd);
        if (width <= 32 || height <= 32)
            return false;

        (int screenX, int screenY) = svc.ClientToScreen(hwnd, 0, 0);
        region = new Rectangle(screenX, screenY, width, height);
        return true;
    }
}
