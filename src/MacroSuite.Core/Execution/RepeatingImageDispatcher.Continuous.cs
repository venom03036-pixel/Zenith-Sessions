using System.Drawing;
using System.Runtime.ExceptionServices;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;

namespace MacroSuite.Core.Execution;

public static partial class RepeatingImageDispatcher
{
    // A single scanner and a single executor share a session. Results are coalesced by
    // location rather than queued once per frame; native calls never overlap.
    private static async Task ExecuteContinuousAsync(IReadOnlyList<MacroStep> steps, List<TriggerGroup> groups,
        MacroContext context, CancellationToken token, Action<int> countActions)
    {
        if (groups.Count > 128) throw new InvalidOperationException("A repeating section supports at most 128 image groups.");
        if (context.ImageMatcher is not IImageScanProvider provider)
            throw new InvalidOperationException("This image matcher does not support configured image scanning.");
        using IImageScanSession session = provider.CreateScanSession(context.CaptureService);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var nativeGate = new SemaphoreSlim(1, 1);
        using var available = new SemaphoreSlim(0, 1);
        CancellationToken ct = lifetime.Token;
        var sync = new object();
        var states = new MonitorGroup[groups.Count];
        var requests = new ImageScanRequest[groups.Count];
        var byId = new Dictionary<Guid, MonitorGroup>(groups.Count);
        IWindowCoordinateService? windowCoordinates = context.WindowCoordinates;
        IntPtr targetWindow = IntPtr.Zero;
        int targetUnavailable = 0;
        Rectangle? initialTargetRegion = null;
        if (groups.Any(group => group.Image.ScanOptions?.SearchScope == ImageSearchScope.TargetWindow))
        {
            targetWindow = context.PlaybackSession?.Target?.Window?.Handle ?? windowCoordinates?.GetForegroundWindow() ?? IntPtr.Zero;
            initialTargetRegion = GetTargetWindowRegion();
        }
        for (int i = 0; i < groups.Count; i++)
        {
            requests[i] = groups[i].Image.CreateScanRequest(groups[i].Wait.StepId, context, repeating: true,
                groups[i].PollMs, initialTargetRegion);
            states[i] = new MonitorGroup(groups[i], requests[i]);
            if (!byId.TryAdd(requests[i].Id, states[i])) throw new InvalidOperationException("Image groups must have distinct action IDs.");
        }
        ExceptionDispatchInfo? failure = null;
        int nextGroup = 0;
        TimeSpan executorPausedDuration = context.Control.TotalPausedDuration;
        Task scanner = Task.Run(ScanLoopAsync, ct);
        try
        {
            while (!context.IsAborted)
            {
                ct.ThrowIfCancellationRequested();
                if (context.Control.IsPaused)
                {
                    lock (sync) foreach (var state in states) state.ResetVisibility();
                    await context.Control.WaitIfPausedAsync(ct).ConfigureAwait(false);
                }
                TimeSpan selectedPausedDuration = context.Control.TotalPausedDuration;
                if (selectedPausedDuration != executorPausedDuration)
                {
                    lock (sync) foreach (var state in states) state.ResetVisibility();
                    executorPausedDuration = selectedPausedDuration;
                }
                MonitorGroup? selectedGroup = null;
                Occurrence? selected = null;
                ImageScanRequest? selectedRequest = null;
                lock (sync)
                {
                    long now = MonotonicMilliseconds;
                    for (int offset = 0; offset < states.Length; offset++)
                    {
                        int index = (nextGroup + offset) % states.Length;
                        var state = states[index];
                        selected = state.TakeReady(now);
                        if (selected is null) continue;
                        selectedGroup = state;
                        selectedRequest = state.Request;
                        nextGroup = (index + 1) % states.Length;
                        break;
                    }
                }
                if (selectedGroup is null || selected is null)
                {
                    await available.WaitAsync(50, ct).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    ImageScanHit? fresh;
                    await nativeGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        // Bounds may move after selection. Discard that nomination and
                        // let the scanner reconfirm in the updated anchored window.
                        bool moved = selectedGroup.Group.Image.ScanOptions?.SearchScope == ImageSearchScope.TargetWindow
                            && (GetTargetWindowRegion() != selectedRequest!.Region
                                || !ReferenceEquals(selectedRequest, selectedGroup.Request));
                        fresh = moved || Volatile.Read(ref targetUnavailable) != 0 ? null : await session.RevalidateAsync(selectedRequest!, selected.Hit, ct).ConfigureAwait(false);
                        // A pause/resume or window move can happen while native verification
                        // is in flight. A check made against the previous surface is stale.
                        bool interrupted = context.Control.IsPaused || context.Control.TotalPausedDuration != selectedPausedDuration;
                        moved |= selectedGroup.Group.Image.ScanOptions?.SearchScope == ImageSearchScope.TargetWindow
                            && GetTargetWindowRegion() != selectedRequest!.Region;
                        lock (sync)
                        {
                            if (interrupted || moved || !ReferenceEquals(selectedRequest, selectedGroup.Request)
                                || !selectedGroup.HasConfirmedPresence(selected))
                            {
                                selected.ResetVisibility();
                                fresh = null;
                            }
                        }
                    }
                    finally { nativeGate.Release(); }
                    if (fresh is null)
                    {
                        lock (sync) selected.ResetVisibility();
                        continue;
                    }
                    ct.ThrowIfCancellationRequested();
                    if (context.Control.IsPaused || context.IsAborted) continue;
                    ImageCondition.StoreMatch(context, fresh.CenterX, fresh.CenterY, fresh.Confidence);
                    countActions(1);
                    context.PublishStatus(new ExecutionStatusSnapshot
                    {
                        ActionId = selectedGroup.Group.Wait.StepId, Phase = "ImageTrigger",
                        Message = "Image found; running its actions while other images continue scanning."
                    });
                    await DetectionCursorMover.MoveToLastDetectionAsync(context, ct, requireCoordinates: true).ConfigureAwait(false);
                    await MacroExecutor.ExecuteActionBlockAsync(steps, selectedGroup.Group.Start, selectedGroup.Group.End,
                        context, ct, countActions).ConfigureAwait(false);
                    lock (sync)
                    {
                        selected.Hit = fresh;
                        selected.NextEligible = MonotonicMilliseconds + selectedGroup.Group.RepeatDelayMs;
                        selected.LastExecuted = MonotonicMilliseconds;
                    }
                    if (context.PendingControlFlow != ControlFlowSignal.None || context.PendingNavigation is not null) return;
                }
                finally { lock (sync) selected.Executing = false; }
            }
        }
        catch (OperationCanceledException) when (failure is not null)
        {
            failure.Throw();
            throw;
        }
        finally
        {
            lifetime.Cancel();
            try { await scanner.ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
        failure?.Throw();

        Rectangle GetTargetWindowRegion()
        {
            if (context.PlaybackSession?.Target?.Window is { } pinned)
            {
                bool valid = context.PlaybackSession.TryGetTargetClientBounds(out Rectangle bounds);
                Volatile.Write(ref targetUnavailable, valid ? 0 : 1);
                return valid ? bounds : pinned.ClientBounds;
            }
            if (context.PlaybackSession?.Target != null) throw new InvalidOperationException("Choose an application/window target to use Target window image search, or choose a screen/placed search area explicitly.");
            if (windowCoordinates is null || targetWindow == IntPtr.Zero || !windowCoordinates.IsWindowValid(targetWindow))
                throw new InvalidOperationException("The image-search target window was closed or is unavailable. Focus the intended window and run the macro again.");
            (int width, int height) = windowCoordinates.GetClientSize(targetWindow);
            if (width <= 32 || height <= 32)
                throw new InvalidOperationException("The image-search target window has no usable client area. Restore the window and run the macro again.");
            (int x, int y) = windowCoordinates.ClientToScreen(targetWindow, 0, 0);
            return new Rectangle(x, y, width, height);
        }

        void RefreshTargetWindowRequests()
        {
            if (targetWindow == IntPtr.Zero) return;
            Rectangle region = GetTargetWindowRegion();
            lock (sync)
            {
                for (int i = 0; i < states.Length; i++)
                {
                    if (states[i].Group.Image.ScanOptions?.SearchScope != ImageSearchScope.TargetWindow
                        || states[i].Request.Region == region) continue;
                    requests[i] = states[i].Request with { Region = region };
                    states[i].UpdateRequest(requests[i]);
                }
            }
        }

        async Task ScanLoopAsync()
        {
            long lastStatus = 0;
            try
            {
                while (!context.IsAborted)
                {
                    ct.ThrowIfCancellationRequested();
                    if (context.Control.IsPaused)
                    {
                        lock (sync) foreach (var state in states) state.ResetVisibility();
                        await context.Control.WaitIfPausedAsync(ct).ConfigureAwait(false);
                    }
                    IReadOnlyList<ImageScanResult> results;
                    TimeSpan scanPausedDuration = context.Control.TotalPausedDuration;
                    await nativeGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        // Follow the window chosen at startup, including after pause;
                        // changing foreground focus must not silently change the target.
                        RefreshTargetWindowRequests();
                        results = Volatile.Read(ref targetUnavailable) != 0 ? Array.Empty<ImageScanResult>() : await session.ScanAsync(requests, ct).ConfigureAwait(false);
                        // Updating the request after capture invalidates that result's
                        // old physical coordinates before it reaches the ready set.
                        var scannedRequests = (ImageScanRequest[])requests.Clone();
                        RefreshTargetWindowRequests();
                        for (int i = 0; i < scannedRequests.Length; i++)
                            if (!ReferenceEquals(scannedRequests[i], requests[i]))
                                results = results.Select(result => result.Id == requests[i].Id
                                    ? result with { Status = ImageScanStatus.Superseded, Hits = Array.Empty<ImageScanHit>() } : result).ToArray();
                    }
                    finally { nativeGate.Release(); }
                    if (Volatile.Read(ref targetUnavailable) != 0)
                    { await context.PlaybackSession!.WaitUntilReadyAsync(ct).ConfigureAwait(false); continue; }
                    long now = MonotonicMilliseconds;
                    bool pressure = false;
                    lock (sync)
                    {
                        foreach (var result in results)
                        {
                            if (!byId.TryGetValue(result.Id, out var state)) continue;
                            if (result.Status == ImageScanStatus.Failed)
                                throw new InvalidOperationException($"Repeating image '{state.Group.Wait.Label}' failed: {result.ErrorMessage ?? result.ErrorCode ?? "Image scan failed"}");
                            if (context.Control.IsPaused || context.Control.TotalPausedDuration != scanPausedDuration)
                            { state.ResetVisibility(); continue; }
                            state.Observe(result, now);
                            pressure |= state.CapacityDeferred;
                        }
                    }
                    if (available.CurrentCount == 0) available.Release();
                    if (now - lastStatus >= 1000)
                    {
                        lastStatus = now;
                        var performance = session.Performance;
                        context.PublishStatus(new ExecutionStatusSnapshot
                        {
                            Phase = "ImageScanning",
                            Message = pressure ? "Scanning continues; visible locations are being handled in turns."
                                : $"Image scanning · requested {performance.RequestedIntervalMs} ms · minimum {performance.EffectiveIntervalMs} ms"
                                    + (performance.MeasuredIntervalMs is { } measured ? $" · last measured {measured:0} ms" : " · measuring actual interval")
                                    + $"{(performance.IsThrottled ? " · adapting to resource target" : "")}."
                        });
                    }
                    // The provider owns requested cadence and resource backpressure. This
                    // cancellable yield prevents a tight loop when every request is deferred.
                    await Task.Delay(10, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
                lifetime.Cancel();
            }
        }
    }

    private sealed class MonitorGroup(TriggerGroup group, ImageScanRequest request)
    {
        private const int MaximumOccurrences = 512;
        public TriggerGroup Group { get; } = group;
        public ImageScanRequest Request { get; private set; } = request;
        private readonly List<Occurrence> _locations = new();
        private int _nextLocation;
        public bool CapacityDeferred { get; private set; }

        public void UpdateRequest(ImageScanRequest updated)
        {
            Request = updated;
            ResetVisibility();
        }

        public void Observe(ImageScanResult result, long now)
        {
            if (result.Status is not (ImageScanStatus.Partial or ImageScanStatus.Complete)) return;
            CapacityDeferred = false;
            _locations.RemoveAll(location => !location.Executing && !HasConfirmedPresence(location) && now >= location.NextEligible
                && now - location.LastSeen > Math.Max(5000, Group.RepeatDelayMs));
            foreach (var hit in result.Hits)
            {
                if (hit.Bounds.Width <= 0 || hit.Bounds.Height <= 0) continue;
                Occurrence? location = !Request.AllMatches && _locations.Count > 0 ? _locations[0] : FindLocation(hit);
                if (location is null)
                {
                    if (_locations.Count >= MaximumOccurrences)
                    {
                        // A completed cooldown can be retired to give new locations a turn.
                        int victim = -1;
                        long oldest = long.MaxValue;
                        for (int i = 0; i < _locations.Count; i++)
                        {
                            var candidate = _locations[i];
                            if (!candidate.Executing && candidate.LastExecuted > 0 && candidate.NextEligible <= now && candidate.LastExecuted < oldest)
                            { oldest = candidate.LastExecuted; victim = i; }
                        }
                        if (victim < 0) { CapacityDeferred = true; continue; }
                        _locations.RemoveAt(victim);
                    }
                    location = new Occurrence(hit);
                    _locations.Add(location);
                }
                location.Hit = hit;
                location.LastSeen = now;
                location.SeenGeneration = result.Generation;
                // Coverage generations can span many detail tiles. Confirm fresh
                // observations, not completed sweeps; a reused capture must not
                // manufacture another confirmation or advance stability time.
                long observedAt = hit.TimestampTicks > 0 ? hit.TimestampTicks : result.TimestampTicks;
                if (observedAt > location.LastConfirmationTimestamp)
                {
                    if (location.Confirmations == 0) location.FirstConfirmationTimestamp = observedAt;
                    location.Confirmations = Math.Min(Group.RequiredConfirmations, location.Confirmations + 1);
                    location.LastConfirmationTimestamp = observedAt;
                }
                if (!Request.AllMatches) break;
            }
            if (result.Status == ImageScanStatus.Complete)
                foreach (var location in _locations)
                    // Scanning continues during the action block. A completed absence
                    // breaks its streak even while that block is still executing;
                    // resetting visibility preserves the running block and cooldown.
                    if (location.SeenGeneration != result.Generation) location.ResetVisibility();
        }

        private Occurrence? FindLocation(ImageScanHit hit)
        {
            Occurrence? closest = null;
            double distance = double.MaxValue;
            foreach (var location in _locations)
            {
                var box = location.Hit.Bounds;
                if (Math.Abs(box.Width - hit.Bounds.Width) > Math.Max(2, box.Width / 5)
                    || Math.Abs(box.Height - hit.Bounds.Height) > Math.Max(2, box.Height / 5)) continue;
                int dx = Math.Abs(location.Hit.CenterX - hit.CenterX), dy = Math.Abs(location.Hit.CenterY - hit.CenterY);
                if (dx > Math.Max(3, box.Width / 8) || dy > Math.Max(3, box.Height / 8)) continue;
                double d = (double)dx * dx + (double)dy * dy;
                if (d < distance) { closest = location; distance = d; }
            }
            return closest;
        }

        public Occurrence? TakeReady(long now)
        {
            for (int offset = 0; offset < _locations.Count; offset++)
            {
                int index = (_nextLocation + offset) % _locations.Count;
                var location = _locations[index];
                // A native tile sweep may take much longer than the repeat delay.
                // Keep a confirmed location eligible on its own cooldown; fresh
                // native revalidation is still mandatory before each action block.
                if (!HasConfirmedPresence(location) || location.Executing || now < location.NextEligible) continue;
                location.Executing = true;
                _nextLocation = (index + 1) % _locations.Count;
                return location;
            }
            return null;
        }
        public bool HasConfirmedPresence(Occurrence location)
            => location.Confirmations >= Group.RequiredConfirmations
                && System.Diagnostics.Stopwatch.GetElapsedTime(location.FirstConfirmationTimestamp,
                    location.LastConfirmationTimestamp).TotalMilliseconds >= Group.StabilityMs;
        public void ResetVisibility() { foreach (var location in _locations) location.ResetVisibility(); }
    }

    private sealed class Occurrence(ImageScanHit hit)
    {
        public ImageScanHit Hit = hit;
        public long SeenGeneration = long.MinValue;
        public long LastConfirmationTimestamp = long.MinValue;
        public long FirstConfirmationTimestamp;
        public long NextEligible;
        public long LastExecuted;
        public long LastSeen;
        public int Confirmations;
        public bool Executing;
        public void ResetVisibility()
        {
            Confirmations = 0;
            FirstConfirmationTimestamp = 0;
            LastConfirmationTimestamp = long.MinValue;
        }
    }
}
