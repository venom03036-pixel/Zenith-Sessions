using System.Drawing;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Models;
using MacroSuite.Core.Usage;
using MacroSuite.SDK.AI;

namespace MacroSuite.Core.AI;

public interface IAIMacroAuthoringService
{
    string ProviderSummary { get; }
    Task<AIMacroAuthoringSnapshot> CaptureAsync(Rectangle area, CancellationToken ct);
    Task<AIMacroDraft> GenerateAsync(string prompt, AIMacroAuthoringSnapshot? snapshot, CancellationToken ct);
}

/// <summary>Dialog-owned snapshot. Never recaptures during generation, so preview and embedded templates match.</summary>
public sealed class AIMacroAuthoringSnapshot : IDisposable
{
    public CaptureFrame Frame { get; }
    public byte[] PreviewJpeg { get; }
    public int SentWidth { get; }
    public int SentHeight { get; }
    public Rectangle Area => new(Frame.OriginX, Frame.OriginY, Frame.Width, Frame.Height);
    public AIMacroAuthoringSnapshot(CaptureFrame frame, byte[] previewJpeg, int sentWidth, int sentHeight)
    { Frame = frame; PreviewJpeg = previewJpeg; SentWidth = sentWidth; SentHeight = sentHeight; }
    public void Dispose() => Frame.Dispose();
}

/// <summary>
/// Optional authoring controller: licensing → cap → configured provider → strict compile → reviewable draft.
/// Owns no input executor, repository, timer or capture loop. Prompts and responses are never logged.
/// </summary>
public sealed class AIMacroAuthoringService : IAIMacroAuthoringService
{
    private readonly AIVisionRouter _router;
    private readonly IScreenCaptureService _capture;
    private readonly IImageEncoder _encoder;
    private readonly IUsageTracker _usage;
    private readonly IFeatureGate _features;
    private readonly string _providerId;
    private readonly string? _modelId;
    public string ProviderSummary => $"{_providerId} · {_modelId ?? "default model"}";

    public AIMacroAuthoringService(AIVisionRouter router, IScreenCaptureService sharedCapture, IImageEncoder encoder, IUsageTracker usage, IFeatureGate featureGate)
    {
        _router = router; _capture = sharedCapture; _encoder = encoder; _usage = usage; _features = featureGate;
        _providerId = router.DefaultProviderId;
        _modelId = !string.IsNullOrWhiteSpace(router.DefaultModelId)
            ? router.DefaultModelId : router.TryGetProvider(_providerId)?.DefaultModel;
    }

    public async Task<AIMacroAuthoringSnapshot> CaptureAsync(Rectangle area, CancellationToken ct)
    {
        if (area.Width <= 0 || area.Height <= 0 || (long)area.Width * area.Height > 16_000_000
            || (long)area.X + area.Width > int.MaxValue || (long)area.Y + area.Height > int.MaxValue)
            throw new InvalidOperationException("Choose a screen area smaller than 16 million pixels.");
        CaptureFrame frame = await _capture.CaptureRegionAsync(area, ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (frame.IsDisposed || (long)frame.Width * frame.Height > 16_000_000)
                throw new InvalidOperationException("The captured screen area was unavailable or too large.");
            // Authoring has one consistent pixel coordinate contract for every provider.
            var fit = MacroSuite.Core.Capture.ImageDownscale.Fit(frame.Width, frame.Height, 1600, 1200);
            byte[] preview = await Task.Run(() => _encoder.EncodeJpeg(frame, 85, fit.DestWidth, fit.DestHeight), ct).ConfigureAwait(false);
            if (preview.Length > 8_000_000) throw new InvalidOperationException("The screen preview is too large. Choose a smaller area.");
            return new AIMacroAuthoringSnapshot(frame, preview, fit.DestWidth, fit.DestHeight);
        }
        catch { frame.Dispose(); throw; }
    }

    public async Task<AIMacroDraft> GenerateAsync(string prompt, AIMacroAuthoringSnapshot? snapshot, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 6000)
            throw new InvalidOperationException("Describe the macro in 1–6,000 characters.");
        _features.Require(FeatureId.AIVision);
        if (snapshot?.Frame.IsDisposed == true) throw new InvalidOperationException("The chosen screen snapshot is no longer available. Capture it again.");
        ct.ThrowIfCancellationRequested();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        // Authoring allows a larger output than live vision. Reserve a conservative larger amount.
        UsageReservation reservation = await _usage.ReserveAsync(0.25m, linked.Token).ConfigureAwait(false);
        try
        {
            AIResponse response = await _router.AnalyzeAsync(new AIRequest
            {
                IsMacroDraft = true, Prompt = prompt.Trim(),
                ImageBytes = snapshot?.PreviewJpeg,
                ImageWidth = snapshot?.SentWidth ?? 0, ImageHeight = snapshot?.SentHeight ?? 0
            }, _providerId, _modelId, linked.Token).ConfigureAwait(false);
            // Record incurred usage even if cancellation arrives or the returned draft is invalid.
            await _usage.CommitAsync(reservation, new UsageRecord
            {
                ProviderId = response.ProviderId ?? _providerId,
                ModelId = response.ModelId ?? _modelId ?? "default",
                ProviderRequestId = response.ProviderRequestId,
                InputTokens = response.InputTokens, OutputTokens = response.OutputTokens,
                EstimatedCostUsd = response.EstimatedCostUsd, Feature = FeatureId.AIVision
            }, CancellationToken.None).ConfigureAwait(false);
            await _usage.ThrowIfCapsExceededAsync(CancellationToken.None).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            var compiled = await Task.Run(() => new AIMacroDraftCompiler(snapshot?.Frame, _encoder,
                snapshot?.SentWidth ?? 0, snapshot?.SentHeight ?? 0, linked.Token).Compile(response.MacroDraftJson), linked.Token).ConfigureAwait(false);
            return new AIMacroDraft(compiled.Macro, compiled.Rows, response.ProviderId ?? _providerId,
                response.ModelId ?? "default", response.EstimatedCostUsd);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new TimeoutException("AI macro generation timed out after two minutes. Retry or use a shorter task."); }
        finally
        {
            if (!reservation.IsCommitted)
                await _usage.ReleaseAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
