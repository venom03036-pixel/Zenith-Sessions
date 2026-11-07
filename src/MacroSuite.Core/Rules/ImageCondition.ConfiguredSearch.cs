using System.Drawing;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Rules;

public sealed partial class ImageCondition
{
    // One owned session spans every confirmation and partial sweep in a wait.
    // This retains decoded templates and pins the initial target window.
    internal async Task<ConditionEvaluation> EvaluateConfiguredWaitAsync(MacroContext context,
        ConditionWaitPolicy policy, CancellationToken ct)
    {
        ConditionWaitEvaluator.ThrowIfStopped(context, ct);
        if (context.ImageMatcher is not IImageScanProvider provider)
            return ConditionEvaluation.Fail("ImageScanProviderUnavailable");
        if (policy.Timeout <= TimeSpan.Zero) return TimedOut();
        using var deadline = new CancellationTokenSource(policy.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, context.StopToken, context.PanicToken, deadline.Token);
        try
        {
            int interval = (int)Math.Clamp(policy.PollInterval.TotalMilliseconds,
                ConditionReliability.MinPollIntervalMs, ConditionReliability.MaxPollIntervalMs);
            using var search = new ConfiguredImageSearch(this, context, provider, interval);
            return await ConditionWaitEvaluator.WaitDetailedAsync(context, (_, token) => search.EvaluateAsync(token),
                policy, linked.Token, resetConfirmationsOnPause: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested && !context.IsAborted)
        {
            context.WriteTrace("ConditionWait", "Image search reached its timeout before another complete result was available.", ExecutionTraceLevel.Warning);
            return TimedOut();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            context.WriteTrace("ImageDetection", $"Configured image wait failed for '{Description}'.", ExecutionTraceLevel.Error, exception: ex);
            return ConditionEvaluation.Fail("ImageDetectionException", ex);
        }

        ConditionEvaluation TimedOut() => policy.Goal == ConditionWaitGoal.UntilEitherStable
            ? ConditionEvaluation.Fail("ConditionIndeterminate") : ConditionEvaluation.Unsatisfied;
    }

    private sealed class ConfiguredImageSearch : IDisposable
    {
        private readonly MacroContext _context;
        private readonly IImageScanSession _session;
        private readonly IWindowCoordinateService? _windows;
        private readonly IntPtr _targetWindow;
        private readonly ImageScanRequest[] _requests;
        private ImageScanHit? _verifiedCandidate;
        private TimeSpan _verifiedPauseDuration;
        private bool _targetUnavailable;

        public ConfiguredImageSearch(ImageCondition image, MacroContext context, IImageScanProvider provider, int interval)
        {
            _context = context;
            Rectangle? targetRegion = null;
            if (image.ScanOptions?.SearchScope == ImageSearchScope.TargetWindow)
            {
                _windows = context.WindowCoordinates;
                _targetWindow = context.PlaybackSession?.Target?.Window?.Handle ?? _windows?.GetForegroundWindow() ?? IntPtr.Zero;
                targetRegion = GetTargetRegion();
            }
            _requests = [image.CreateScanRequest(Guid.Empty, context, repeating: false, interval, targetRegion)];
            _session = provider.CreateScanSession(context.CaptureService);
        }

        public async Task<ConditionEvaluation> EvaluateAsync(CancellationToken ct)
        {
            while (true)
            {
                ConditionWaitEvaluator.ThrowIfStopped(_context, ct);
                if (_context.Control.IsPaused)
                    await _context.Control.WaitIfPausedAsync(ct).ConfigureAwait(false);
                ConditionWaitEvaluator.ThrowIfStopped(_context, ct);
                RefreshTargetRegion();
                if (_targetUnavailable) { await _context.PlaybackSession!.WaitUntilReadyAsync(ct).ConfigureAwait(false); continue; }
                ImageScanRequest request = _requests[0];
                TimeSpan pausedBefore = _context.Control.TotalPausedDuration;
                if (pausedBefore != _verifiedPauseDuration) _verifiedCandidate = null;
                if (_verifiedCandidate is { } previous)
                {
                    // Confirmation/stability checks need a fresh observation of
                    // the known location, not another complete native-tile sweep.
                    ImageScanHit? confirmed = await _session.RevalidateAsync(request, previous, ct).ConfigureAwait(false);
                    ConditionWaitEvaluator.ThrowIfStopped(_context, ct);
                    if (ObservationInterrupted(pausedBefore)) continue;
                    if (confirmed is not null) return Accept(confirmed, pausedBefore);
                    _verifiedCandidate = null;
                }
                var results = await _session.ScanAsync(_requests, ct).ConfigureAwait(false);
                ConditionWaitEvaluator.ThrowIfStopped(_context, ct);
                if (ObservationInterrupted(pausedBefore)) continue;
                bool interrupted = false;
                foreach (var result in results)
                {
                    if (result.Status == ImageScanStatus.Failed)
                        return ConditionEvaluation.Fail(result.ErrorCode ?? "ImageScanFailed") with { ErrorMessage = result.ErrorMessage };
                    if (result.Status is not (ImageScanStatus.Partial or ImageScanStatus.Complete)) continue;
                    foreach (var candidate in result.Hits)
                    {
                        // Sampled candidates never satisfy a condition on their own.
                        ImageScanHit? hit = await _session.RevalidateAsync(request, candidate, ct).ConfigureAwait(false);
                        ConditionWaitEvaluator.ThrowIfStopped(_context, ct);
                        if (ObservationInterrupted(pausedBefore)) { interrupted = true; break; }
                        if (hit is null) continue;
                        return Accept(hit, pausedBefore);
                    }
                    if (interrupted) break;
                    if (result.Status == ImageScanStatus.Complete) return ConditionEvaluation.Unsatisfied;
                }
                if (!interrupted) await Task.Delay(10, ct).ConfigureAwait(false);
            }
        }

        private bool ObservationInterrupted(TimeSpan pausedBefore)
        {
            bool moved = RefreshTargetRegion();
            bool interrupted = _targetUnavailable || moved || _context.Control.IsPaused || _context.Control.TotalPausedDuration != pausedBefore;
            if (interrupted) _verifiedCandidate = null;
            return interrupted;
        }

        private ConditionEvaluation Accept(ImageScanHit hit, TimeSpan pausedDuration)
        {
            _verifiedCandidate = hit;
            _verifiedPauseDuration = pausedDuration;
            StoreMatch(_context, hit.CenterX, hit.CenterY, hit.Confidence);
            return ConditionEvaluation.Satisfied;
        }

        private bool RefreshTargetRegion()
        {
            if (_targetWindow == IntPtr.Zero) return false;
            Rectangle bounds = GetTargetRegion();
            if (_requests[0].Region == bounds) return false;
            _requests[0] = _requests[0] with { Region = bounds };
            _verifiedCandidate = null;
            return true;
        }

        private Rectangle GetTargetRegion()
        {
            if (_context.PlaybackSession?.Target?.Window is { } pinned)
            {
                _targetUnavailable = !_context.PlaybackSession.TryGetTargetClientBounds(out Rectangle bounds);
                return _targetUnavailable ? pinned.ClientBounds : bounds;
            }
            if (_context.PlaybackSession?.Target != null) throw new InvalidOperationException("Choose an application/window target to use Target window image search, or choose a screen/placed search area explicitly.");
            if (_windows is null || _targetWindow == IntPtr.Zero || !_windows.IsWindowValid(_targetWindow))
                throw new InvalidOperationException("The image-search target window was closed or is unavailable. Focus the intended window and run the macro again.");
            (int width, int height) = _windows.GetClientSize(_targetWindow);
            if (width <= 32 || height <= 32)
                throw new InvalidOperationException("The image-search target window has no usable client area. Restore the window and run the macro again.");
            (int x, int y) = _windows.ClientToScreen(_targetWindow, 0, 0);
            return new Rectangle(x, y, width, height);
        }

        public void Dispose() => _session.Dispose();
    }
}
