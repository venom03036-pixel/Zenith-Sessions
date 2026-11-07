using System.Drawing;
using MacroSuite.Core.AI;
using MacroSuite.Core.Audit;
using MacroSuite.Core.Capture;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Models;
using MacroSuite.Core.Usage;
using MacroSuite.SDK.AI;

namespace MacroSuite.Core.Steps;

/// <summary>
/// Executes an AI-driven vision action through the full safety sandbox pipeline (Rule 8, Rule 15).
/// Pipeline: Licensing -> Spend Cap -> Screen Capture -> Vision Router -> Action Validation -> Approval Gate -> Input Simulation -> Audit Log.
/// </summary>
public sealed class AIStep : MacroStep
{
    /// <summary>Instruction prompt passed to the vision model.</summary>
    public string Prompt { get; init; } = string.Empty;

    /// <summary>Optional region constraint on the screen.</summary>
    public Rectangle? TargetRegion { get; init; }

    /// <summary>Specific AI provider identifier (e.g., 'openai', 'anthropic', 'gemini'), or null for default.</summary>
    public string? ProviderId { get; init; }

    /// <summary>Model identifier (or null for provider default).</summary>
    public string? ModelId { get; init; }

    /// <summary>Expected active window title pattern for safety matching.</summary>
    public string? ExpectedWindow { get; init; }

    /// <summary>Timeout for vision analysis and user approval.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Optional minimum confidence threshold override (0.0 to 1.0).</summary>
    public double? MinConfidence { get; init; }

    public AIStep()
    {
        Label = "AI Vision Step";
    }

    public AIStep(string prompt, Rectangle? targetRegion = null, string? modelId = null, string? providerId = null)
    {
        Prompt = prompt ?? string.Empty;
        TargetRegion = targetRegion;
        ModelId = modelId;
        ProviderId = providerId;
        Label = $"AI: {prompt}";
    }

    /// <inheritdoc/>
    public override async Task ExecuteAsync(MacroContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!IsEnabled || context.IsAborted || ct.IsCancellationRequested)
        {
            return;
        }

        // 1. Licensing Gate (Rule 18)
        context.FeatureGate?.Require(FeatureId.AIVision);

        bool autoIntent = context.AutonomousMode
            && context.ApprovalGate?.Policy == ApprovalPolicy.AutoApprove;
        bool autonomousLicensed = context.FeatureGate?.IsAvailable(FeatureId.AutonomousMode) == true;
        bool forceInteractive = context.ApprovalGate?.Policy == ApprovalPolicy.AutoApprove
            && !(autoIntent && autonomousLicensed);

        if (context.AuditLogger?.Health == AuditLoggerHealth.Faulted && autoIntent && autonomousLicensed)
        {
            forceInteractive = true;
            context.WriteTrace(
                "AIVision",
                "Audit logger is faulted; autonomous auto-approve is disabled (fail closed).",
                ExecutionTraceLevel.Warning,
                StepId);
        }

        // 3. Router Availability Check (Rule 15)
        var router = context.VisionRouter;
        if (router is null)
        {
            throw new InvalidOperationException("No AIVisionRouter is configured in MacroContext.");
        }

        UsageReservation? reservation = null;
        try
        {
            // 4. Usage / Spend Cap Pre-flight Check (Rule 18)
            if (context.UsageTracker != null)
            {
                reservation = await context.UsageTracker
                    .ReserveAsync(UsageTracker.DefaultVisionEstimateUsd, ct)
                    .ConfigureAwait(false);
            }

            await ExecuteVisionPipelineAsync(
                    context,
                    router,
                    autoIntent,
                    autonomousLicensed,
                    forceInteractive,
                    reservation,
                    ct)
                .ConfigureAwait(false);
        }
        finally
        {
            if (reservation is { IsCommitted: false } && context.UsageTracker is not null)
            {
                try
                {
                    await context.UsageTracker.ReleaseAsync(reservation, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception releaseEx)
                {
                    context.WriteTrace(
                        "AIVision",
                        "Failed to release unused spend reservation.",
                        ExecutionTraceLevel.Warning,
                        StepId,
                        exception: releaseEx);
                }
            }
        }
    }

    private async Task ExecuteVisionPipelineAsync(
        MacroContext context,
        AIVisionRouter router,
        bool autoIntent,
        bool autonomousLicensed,
        bool forceInteractive,
        UsageReservation? reservation,
        CancellationToken ct)
    {
        // 5. Screen Capture (Rule 5, Rule 6). Crop at native res, then pre-scale to the
        // provider's effective size so the model cannot silently resize the image.
        byte[] imageBytes;
        CaptureTransform transform;
        Rectangle capturedRegion;
        VisionEncodePlan encodePlan;
        {
            using var frame = TargetRegion.HasValue
                ? await context.CaptureService.CaptureRegionAsync(TargetRegion.Value, ct).ConfigureAwait(false)
                : await context.CaptureService.CaptureScreenAsync(ct).ConfigureAwait(false);
            capturedRegion = new Rectangle(frame.OriginX, frame.OriginY, frame.Width, frame.Height);
            encodePlan = router.PlanEncode(frame.Width, frame.Height, ProviderId, ModelId);
            var fit = ImageDownscale.Fit(frame.Width, frame.Height, encodePlan.SentWidth, encodePlan.SentHeight);
            transform = CaptureTransform.FromCrop(
                frame.OriginX,
                frame.OriginY,
                frame.Width,
                frame.Height,
                fit.DestWidth,
                fit.DestHeight,
                encodePlan);

            if (context.ImageEncoder != null)
            {
                imageBytes = context.ImageEncoder.EncodeJpeg(
                    frame,
                    ImageDownscale.VisionJpegQuality,
                    fit.DestWidth,
                    fit.DestHeight);
            }
            else if (fit.WasDownscaled)
            {
                using var preview = ImageDownscale.Downscale(frame, fit);
                imageBytes = CreateSimpleBmp(preview);
            }
            else
            {
                imageBytes = CreateSimpleBmp(frame);
            }
        }

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["coordinate_space"] = transform.Space.ToString()
        };
        if (!string.IsNullOrWhiteSpace(encodePlan.DetailParameter))
            parameters["detail"] = encodePlan.DetailParameter;

        // 7. Execute Vision Analysis (Rule 9, Rule 16)
        var request = new AIRequest
        {
            Prompt = Prompt,
            ImageBytes = imageBytes,
            ImageWidth = transform.SentWidth,
            ImageHeight = transform.SentHeight,
            ModelId = ModelId,
            Parameters = parameters
        };

        AIResponse response;
        using var stepCts = new CancellationTokenSource(Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, context.PanicToken, stepCts.Token);

        try
        {
            response = await router.AnalyzeAsync(request, ProviderId, ModelId, linkedCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.AuditLogger?.Enqueue(new AuditEntry
            {
                RunId = context.RunId,
                StepId = StepId,
                ProviderId = ProviderId ?? router.DefaultProviderId,
                ModelId = ModelId ?? router.DefaultModelId ?? "default",
                Prompt = Prompt,
                Error = ex.Message
            });
            throw;
        }

        string billedModel = !string.IsNullOrWhiteSpace(response.ModelId)
            ? response.ModelId
            : (ModelId ?? router.DefaultModelId ?? "default");
        await PersistUsageAsync(context, billedModel, router.DefaultProviderId, response, reservation).ConfigureAwait(false);
        if (context.UsageTracker != null)
            await context.UsageTracker.ThrowIfCapsExceededAsync(CancellationToken.None).ConfigureAwait(false);

        // 8. Action Validation (Rule 8)
        var validator = context.ActionValidator ?? new AIActionValidator();
        if (MinConfidence.HasValue)
        {
            validator = new AIActionValidator
            {
                MinConfidenceThreshold = MinConfidence.Value,
                MaxTextLength = validator.MaxTextLength
            };
        }

        AIAction proposed = MapActionCoordinates(response.Action, transform);
        proposed = await RefineClickAsync(context, proposed, capturedRegion, ct).ConfigureAwait(false);

        context.WriteTrace(
            "AIVision",
            transform.FormatLog(response.Action.X, response.Action.Y, proposed.X, proposed.Y),
            actionId: StepId);

        string? currentWindow = context.ForegroundWindow?.GetForegroundWindowTitle();
        var validationResult = validator.Validate(proposed, capturedRegion, ExpectedWindow, currentWindow);
        if (!validationResult.IsValid)
        {
            context.AuditLogger?.Enqueue(new AuditEntry
            {
                RunId = context.RunId,
                StepId = StepId,
                ProviderId = ProviderId ?? router.DefaultProviderId,
                ModelId = billedModel,
                Prompt = Prompt,
                ProposedAction = response.Action.ActionType,
                ValidationResult = validationResult.ErrorMessage,
                Error = $"Validation failed: {validationResult.ErrorMessage}",
                InputTokens = response.InputTokens,
                OutputTokens = response.OutputTokens,
                EstimatedCostUsd = response.EstimatedCostUsd
            });

            throw new InvalidOperationException($"AI action rejected by safety validator: {validationResult.ErrorMessage}");
        }

        if (proposed.ActionType.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            context.AuditLogger?.Enqueue(new AuditEntry
            {
                RunId = context.RunId,
                StepId = StepId,
                ProviderId = ProviderId ?? router.DefaultProviderId,
                ModelId = billedModel,
                Prompt = Prompt,
                ProposedAction = proposed.ActionType,
                ValidationResult = "Passed",
                ApprovalResult = "NotRequired",
                ExecutedAction = "None",
                InputTokens = response.InputTokens,
                OutputTokens = response.OutputTokens,
                EstimatedCostUsd = response.EstimatedCostUsd
            });
            context.WriteTrace("AIVision", "AI returned no action.", actionId: StepId);
            return;
        }

        // 9. Safety Approval Gate (Rule 8)
        if (context.ApprovalGate is null)
        {
            throw new InvalidOperationException(
                "AI action execution requires an approval gate; no action was executed.");
        }
        else
        {
            var approvalContext = new ApprovalContext
            {
                TargetRegion = TargetRegion,
                ActionSummary = $"{proposed.ActionType} (X={proposed.X}, Y={proposed.Y})",
                Reasoning = proposed.Reasoning,
                ScreenshotThumbnail = imageBytes,
                Timeout = TimeoutForApproval(context.ApprovalTimeout)
            };

            string approvalMode = forceInteractive
                ? "interactive"
                : (autoIntent && autonomousLicensed ? "automatic" : "interactive");
            context.WriteTrace(
                "AIVision",
                $"Approval gate: licensedAutonomous={autonomousLicensed}, intent={context.AutonomousMode}, mode={approvalMode}.",
                actionId: StepId);

            var decision = await context.ApprovalGate.RequestApprovalAsync(
                proposed,
                approvalContext,
                linkedCts.Token,
                forceInteractive).ConfigureAwait(false);
            if (decision != ApprovalDecision.Approved)
            {
                context.AuditLogger?.Enqueue(new AuditEntry
                {
                    RunId = context.RunId,
                    StepId = StepId,
                    ProviderId = ProviderId ?? router.DefaultProviderId,
                    ModelId = billedModel,
                    Prompt = Prompt,
                    ProposedAction = response.Action.ActionType,
                    ValidationResult = "Passed",
                    ApprovalResult = $"{decision}; licensedAutonomous={autonomousLicensed}; intent={context.AutonomousMode}; mode={(forceInteractive ? "interactive" : "automatic")}",
                    Error = $"Action not approved: {decision}",
                    InputTokens = response.InputTokens,
                    OutputTokens = response.OutputTokens,
                    EstimatedCostUsd = response.EstimatedCostUsd
                });
                throw new InvalidOperationException(
                    decision == ApprovalDecision.TimedOut
                        ? $"AI Vision approval timed out after {approvalContext.Timeout.TotalSeconds:N0}s. Open Settings & AI and raise Approval Toast Timeout, or approve the toast faster."
                        : $"AI Vision action was not approved ({decision}).");
            }
        }

        // 10. Execute Action via Input Simulation (Rule 4)
        if (context.InputPlayer is null)
            throw new InvalidOperationException("AI action execution requires an input player.");
        if (context.IsAborted)
            return;

        await AIVisionActionDispatcher.DispatchAsync(context.InputPlayer, proposed, linkedCts.Token).ConfigureAwait(false);

        // 11. Audit Logging (Rule 10, Rule 18)
        context.AuditLogger?.Enqueue(new AuditEntry
        {
            RunId = context.RunId,
            StepId = StepId,
            ProviderId = ProviderId ?? router.DefaultProviderId,
            ModelId = billedModel,
            Prompt = Prompt,
            ProposedAction = proposed.ActionType,
            ValidationResult = "Passed",
            ApprovalResult = $"Approved; licensedAutonomous={autonomousLicensed}; intent={context.AutonomousMode}; mode={(forceInteractive ? "interactive" : "automatic")}",
            ExecutedAction = $"{proposed.ActionType} (X={proposed.X}, Y={proposed.Y}); {transform.FormatLog(response.Action.X, response.Action.Y, proposed.X, proposed.Y)}",
            InputTokens = response.InputTokens,
            OutputTokens = response.OutputTokens,
            EstimatedCostUsd = response.EstimatedCostUsd
        });

        // 12. Usage is persisted immediately after a billed provider response (see PersistUsageAsync).
    }

    private async Task PersistUsageAsync(
        MacroContext context,
        string billedModel,
        string defaultProviderId,
        AIResponse response,
        UsageReservation? reservation)
    {
        if (context.UsageTracker is null)
            return;

        var record = new UsageRecord
        {
            ProviderId = ProviderId ?? defaultProviderId,
            ModelId = billedModel,
            ProviderRequestId = response.ProviderRequestId,
            InputTokens = response.InputTokens,
            OutputTokens = response.OutputTokens,
            EstimatedCostUsd = response.EstimatedCostUsd,
            Feature = FeatureId.AIVision
        };

        if (reservation is not null)
        {
            await context.UsageTracker.CommitAsync(reservation, record, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await context.UsageTracker.RecordAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static AIAction MapActionCoordinates(AIAction action, CaptureTransform transform)
    {
        int? x = null;
        int? y = null;
        if (action.X.HasValue && action.Y.HasValue)
        {
            (int nx, int ny) = transform.ToNative(action.X.Value, action.Y.Value);
            x = nx;
            y = ny;
        }
        else if (action.X.HasValue)
        {
            (int nx, _) = transform.ToNative(action.X.Value, 0);
            x = nx;
        }
        else if (action.Y.HasValue)
        {
            (_, int ny) = transform.ToNative(0, action.Y.Value);
            y = ny;
        }

        return new AIAction
        {
            ActionType = action.ActionType,
            X = x,
            Y = y,
            KeyCode = action.KeyCode,
            Text = action.Text,
            Confidence = action.Confidence,
            Reasoning = action.Reasoning
        };
    }

    private static async Task<AIAction> RefineClickAsync(
        MacroContext context,
        AIAction proposed,
        Rectangle capturedRegion,
        CancellationToken ct)
    {
        if (context.ClickRefiner is null)
            return proposed;
        if (proposed.X is not int x || proposed.Y is not int y)
            return proposed;
        if (!proposed.ActionType.Equals("click", StringComparison.OrdinalIgnoreCase)
            && !proposed.ActionType.Equals("double_click", StringComparison.OrdinalIgnoreCase))
        {
            return proposed;
        }

        int refinedX = x;
        int refinedY = y;
        try
        {
            (refinedX, refinedY) = await context.ClickRefiner
                .RefineAsync(context.CaptureService, x, y, capturedRegion, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.WriteTrace(
                "AIVision",
                "Click refinement failed; using mapped model coordinates.",
                ExecutionTraceLevel.Warning,
                exception: ex);
            return proposed;
        }

        refinedX = Math.Clamp(refinedX, capturedRegion.Left, Math.Max(capturedRegion.Left, capturedRegion.Right - 1));
        refinedY = Math.Clamp(refinedY, capturedRegion.Top, Math.Max(capturedRegion.Top, capturedRegion.Bottom - 1));
        if (refinedX == x && refinedY == y)
            return proposed;

        context.WriteTrace(
            "AIVision",
            $"Click refined from ({x},{y}) to ({refinedX},{refinedY}).");

        return new AIAction
        {
            ActionType = proposed.ActionType,
            X = refinedX,
            Y = refinedY,
            KeyCode = proposed.KeyCode,
            Text = proposed.Text,
            Confidence = proposed.Confidence,
            Reasoning = proposed.Reasoning
        };
    }

    private static TimeSpan TimeoutForApproval(TimeSpan configured)
    {
        // Manual approval toasts shorter than 12s expire while the studio is hiding
        // into compact overlay, so the click never happens.
        TimeSpan floor = TimeSpan.FromSeconds(12);
        return configured < floor ? floor : configured;
    }

    private static byte[] CreateSimpleBmp(CaptureFrame frame)
    {
        int width = frame.Width;
        int height = frame.Height;
        int rowSize = (width * 3 + 3) & ~3;
        int imageSize = rowSize * height;
        int fileSize = 54 + imageSize;

        byte[] bmp = new byte[fileSize];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(fileSize).CopyTo(bmp, 2);
        BitConverter.GetBytes(54).CopyTo(bmp, 10);
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(width).CopyTo(bmp, 18);
        BitConverter.GetBytes(height).CopyTo(bmp, 22);
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
        BitConverter.GetBytes((short)24).CopyTo(bmp, 28);
        BitConverter.GetBytes(imageSize).CopyTo(bmp, 34);

        var span = frame.Span;
        int bytesPerPixel = CaptureFrame.GetBytesPerPixel(frame.Format);

        for (int y = 0; y < height; y++)
        {
            int srcY = height - 1 - y;
            int srcRowOffset = srcY * frame.Stride;
            int dstRowOffset = 54 + (y * rowSize);

            for (int x = 0; x < width; x++)
            {
                int srcPixel = srcRowOffset + (x * bytesPerPixel);
                int dstPixel = dstRowOffset + (x * 3);
                bmp[dstPixel] = span[srcPixel];
                bmp[dstPixel + 1] = span[srcPixel + 1];
                bmp[dstPixel + 2] = span[srcPixel + 2];
            }
        }

        return bmp;
    }
}
