using System.Drawing;
using System.Diagnostics;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;

namespace MacroSuite.App.Services;

/// <summary>One explicitly requested scan through the shared capture/matcher services; sends no input.</summary>
public sealed class ImageDetectionProbe(IScreenCaptureService capture, IImageMatcher matcher)
{
    public async Task<MatchResult> TestAsync(ImageCondition image, Rectangle? region, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (image.ScanOptions is { } options && matcher is IImageScanProvider provider)
        {
            using var session = provider.CreateScanSession(capture);
            var request = new ImageScanRequest(Guid.NewGuid(), image.TemplatePath, image.TemplateBytes,
                image.MinConfidence, region, options.Resolution, true, 100);
            var elapsed = Stopwatch.StartNew();
            var hits = new List<ImageScanHit>();
            ImageScanResult? last = null;
            bool complete = false;
            do
            {
                var results = await session.ScanAsync(new[] { request }, ct).ConfigureAwait(false);
                var current = results[0];
                if (current.Status == ImageScanStatus.Failed)
                    return MatchResult.Failed with { ErrorCode = current.ErrorCode, ErrorMessage = current.ErrorMessage };
                if (current.Status is ImageScanStatus.Partial or ImageScanStatus.Complete)
                {
                    last = current;
                    bool verifiedAllCandidates = true;
                    foreach (var candidate in current.Hits)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (hits.Count >= 256 || elapsed.ElapsedMilliseconds >= 2000)
                        { verifiedAllCandidates = false; break; }
                        var fresh = await session.RevalidateAsync(request, candidate, ct).ConfigureAwait(false);
                        if (fresh is not null && !hits.Any(hit => Math.Abs(hit.CenterX - fresh.CenterX) <= 4 && Math.Abs(hit.CenterY - fresh.CenterY) <= 4))
                            hits.Add(fresh);
                    }
                    complete = current.Status == ImageScanStatus.Complete && verifiedAllCandidates;
                }
                if (complete || elapsed.ElapsedMilliseconds >= 2000 || hits.Count >= 256) break;
                await Task.Delay(Math.Max(10, session.Performance.EffectiveIntervalMs), ct).ConfigureAwait(false);
            } while (true);
            string locations = string.Join(", ", hits.Take(8).Select(hit => $"({hit.CenterX}, {hit.CenterY}) {hit.Confidence:P0}"));
            string coverage = complete ? "Coverage complete" : "Detail coverage still in progress; absence is not confirmed";
            string resolution = last is null ? "waiting for a frame" : $"{last.SampledSize.Width} × {last.SampledSize.Height}";
            string summary = $"{hits.Count} verified match{(hits.Count == 1 ? "" : "es")} · {resolution} scan · {elapsed.ElapsedMilliseconds} ms. {coverage}. {locations}";
            return (hits.Count > 0 ? MatchResult.Matched(hits[0].Confidence, hits[0].Bounds) : complete ? MatchResult.NotFound : MatchResult.Failed with
                { ErrorCode = "ImageScanIncomplete", ErrorMessage = "Detail coverage is unfinished. Test again or allow the macro to keep scanning." }) with { DiagnosticSummary = summary };
        }
        using CaptureFrame frame = region is { Width: > 0, Height: > 0 } area
            ? await capture.CaptureRegionAsync(area, ct).ConfigureAwait(false)
            : await capture.CaptureScreenAsync(ct).ConfigureAwait(false);
        if (frame is null || frame.IsDisposed) return MatchResult.Failed with { ErrorCode = "CaptureFrameUnavailable", ErrorMessage = "The screen could not be captured." };
        if (image.TemplateBytes is { Length: > 0 } bytes)
            return await matcher.MatchAsync(frame, bytes, image.MinConfidence, ct: ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(image.TemplatePath))
            return await matcher.MatchAsync(frame, image.TemplatePath, image.MinConfidence, ct: ct).ConfigureAwait(false);
        return MatchResult.Failed with { ErrorCode = "ImageTemplateMissing", ErrorMessage = "Choose a target image first." };
    }
}
