using System.Diagnostics;
using System.Drawing;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Models;
using MacroSuite.Core.Vision;
using MacroSuite.Infrastructure.Diagnostics;
using OpenCvSharp;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

namespace MacroSuite.Infrastructure.OpenCV;

/// <summary>
/// One bounded batch scanner. Shared scenes are converted once; native recovery advances in tiles.
/// The caller serializes input separately and revalidates every proposed physical location.
/// </summary>
internal sealed class OpenCvImageScanSession : IImageScanSession
{
    private const int MaxGroups = 128, MaxHits = 32, TileStep = 512;
    private readonly IScreenCaptureService _capture;
    private readonly ImageScanBudget _budget;
    private readonly Dictionary<Guid, State> _states = new();
    private readonly PreparationBudget _preparationBudget;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private int _rotation;
    private long _epoch;
    private Size _lastSampledSize;
    private bool _disposed;
    public ImageScanPerformanceSnapshot Performance => _budget.Snapshot;
    internal long PreparedImageBytes => _preparationBudget.Used;
    public OpenCvImageScanSession(IScreenCaptureService capture, ImageScanBudget? budget = null, long preparedImageBudgetBytes = 128L * 1024 * 1024)
    { _capture = capture; _budget = budget ?? ImageScanBudget.Shared; _preparationBudget = new(preparedImageBudgetBytes); }

    public async Task<IReadOnlyList<ImageScanResult>> ScanAsync(IReadOnlyList<ImageScanRequest> requests, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        ct = linked.Token;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var results = new ImageScanResult[requests.Count];
            if (requests.Count == 0) return results;
            for (int i = 0; i < requests.Count; i++) results[i] = Empty(requests[i], ImageScanStatus.Deferred);
            // Remove configurations no longer subscribed; preparation is owned by this session.
            var active = new HashSet<Guid>();
            for (int i = 0; i < Math.Min(MaxGroups, requests.Count); i++) active.Add(requests[i].Id);
            foreach (Guid id in _states.Keys.ToArray())
                if (!active.Contains(id)) { _states[id].Dispose(); _states.Remove(id); }
            for (int i = MaxGroups; i < requests.Count; i++)
                results[i] = Empty(requests[i], ImageScanStatus.Failed, "ImageGroupLimit", "At most 128 image groups can scan together.");

            int count = Math.Min(MaxGroups, requests.Count);
            // Resource measurements continue while cadence defers capture. A long
            // effective interval must not postpone recovery/pressure updates.
            if (ReferenceEquals(_budget, ImageScanBudget.Shared))
            {
                int requested = 10000;
                for (int i = 0; i < count; i++) requested = Math.Min(requested, requests[i].PollIntervalMs);
                ImageScanResourceSampler.Sample(requested, _lastSampledSize);
            }
            var watch = Stopwatch.StartNew();
            ImageScanFrame? frame = null;
            Mat? scene = null;
            try
            {
                int start = _rotation % count;
                // Rotate the first eligible group each pass, independently of surface
                // grouping. One retained frame/gray scene serves all matching requests,
                // even when other regions are interleaved in the action list.
                _rotation = (start + 1) % count;
                var visited = new bool[count];
                int processed = 0;
                for (int offset = 0; offset < count; offset++)
                {
                    int index = (start + offset) % count;
                    if (visited[index]) continue;
                    var surface = requests[index];
                    scene?.Dispose(); scene = null; frame?.Dispose(); frame = null;
                    for (int sameOffset = 0; sameOffset < count; sameOffset++)
                    {
                        int sameIndex = (start + sameOffset) % count;
                        var request = requests[sameIndex];
                        if (visited[sameIndex] || request.Region != surface.Region || request.Resolution != surface.Resolution) continue;
                        visited[sameIndex] = true;
                        ct.ThrowIfCancellationRequested();
                        if (watch.ElapsedMilliseconds > 30 && processed > 0) return results;
                        try
                        {
                            State state = GetState(request);
                            long now = Stopwatch.GetTimestamp();
                            int interval = _budget.EffectiveInterval(request.PollIntervalMs);
                            double sincePrevious = Stopwatch.GetElapsedTime(state.LastScan, now).TotalMilliseconds;
                            if (state.LastScan != 0 && sincePrevious < interval) continue;
                            if (state.LastScan != 0) _budget.RecordScanInterval(request.Id, request.PollIntervalMs, sincePrevious);
                            state.LastScan = now; // Never catch up overdue scans in a burst.
                            processed++;
                            if (frame is null)
                            {
                                frame = await CaptureAsync(request.Region, request.Resolution, ct).ConfigureAwait(false);
                                _lastSampledSize = frame.SampledSize;
                                scene = ImageScanFrameConverter.Gray(frame.Memory.Span, frame.Width, frame.Height, frame.Stride, frame.Format);
                            }
                            results[sameIndex] = await SearchAsync(request, state, frame, scene!, ct).ConfigureAwait(false);
                            if (ReferenceEquals(_budget, ImageScanBudget.Shared)) ImageScanResourceSampler.Sample(request.PollIntervalMs, frame.SampledSize);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            results[sameIndex] = Empty(request, ImageScanStatus.Failed, ex.GetType().Name, ex.Message);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // A started batch reports which requests did not finish. Cancellation
                // before acquiring this session still uses normal Task cancellation.
                for (int i = 0; i < results.Length; i++)
                    if (results[i].Status == ImageScanStatus.Deferred)
                        results[i] = Empty(requests[i], ImageScanStatus.Cancelled);
            }
            finally { scene?.Dispose(); frame?.Dispose(); }
            return results;
        }
        finally { _gate.Release(); }
    }

    private async Task<ImageScanFrame> CaptureAsync(Rectangle? bounds, ImageScanResolution resolution, CancellationToken ct)
    {
        if (_capture is IImageScanCaptureService { SupportsImageScanCapture: true } sampled)
            return await sampled.CaptureImageAsync(bounds, resolution, ct).ConfigureAwait(false);
        using var native = bounds.HasValue
            ? await _capture.CaptureRegionAsync(bounds.Value, ct).ConfigureAwait(false)
            : await _capture.CaptureScreenAsync(ct).ConfigureAwait(false);
        return ImageScanFrameConverter.Convert(native, resolution);
    }

    private async Task<ImageScanResult> SearchAsync(ImageScanRequest request, State state, ImageScanFrame frame, Mat scene, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        if (frame.SourceBounds.Width < state.Template.Width || frame.SourceBounds.Height < state.Template.Height)
            throw new InvalidOperationException("The search area must contain the entire target image.");
        if (state.Bounds != frame.SourceBounds || state.Generation == 0)
        {
            state.Bounds = frame.SourceBounds; state.Tile = 0; state.DetailCursor = state.OverviewCursor = 0;
            state.Generation = Interlocked.Increment(ref _epoch);
        }
        Mat variant = state.Variant(frame.ScaleX, frame.ScaleY);
        var hits = new List<ImageScanHit>(MaxHits);
        bool overviewDone = true;
        bool sampled = frame.Width != frame.SourceBounds.Width || frame.Height != frame.SourceBounds.Height;
        if (variant.Width >= 4 && variant.Height >= 4)
        {
            double threshold = sampled ? Math.Max(.35, request.MinConfidence * .70) : request.MinConfidence;
            var candidates = Match(scene, variant, threshold, request.AllMatches, state.OverviewCursor, out int next, ct, sampled ? 16 : MaxHits);
            state.OverviewCursor = next; overviewDone = next == 0;
            foreach (var candidate in candidates)
            {
                Rectangle mapped = frame.ToPhysical(candidate.Bounds);
                mapped.Size = new Size(state.Template.Width, state.Template.Height);
                AddDistinct(hits, new ImageScanHit(mapped, candidate.Confidence) { TimestampTicks = frame.TimestampTicks });
            }
        }
        bool complete = overviewDone;
        Rectangle detailBounds = Rectangle.Empty;
        bool detailComplete = false;
        long timestamp = frame.TimestampTicks;
        // Native tiles also cover detail lost to scaling. Each turn consumes at most one bounded tile.
        // A sweep is explicitly Partial until every top-left position has been covered.
        if (sampled)
        {
            int columns = (frame.SourceBounds.Width - state.Template.Width) / TileStep + 1;
            int rows = (frame.SourceBounds.Height - state.Template.Height) / TileStep + 1;
            int tile = state.Tile % checked(columns * rows);
            int x = tile % columns * TileStep, y = tile / columns * TileStep;
            detailBounds = new Rectangle(frame.SourceBounds.X + x, frame.SourceBounds.Y + y,
                Math.Min(TileStep + state.Template.Width - 1, frame.SourceBounds.Width - x),
                Math.Min(TileStep + state.Template.Height - 1, frame.SourceBounds.Height - y));
            using var detail = await _capture.CaptureRegionAsync(detailBounds, ct).ConfigureAwait(false);
            timestamp = detail.TimestampTicks;
            using var gray = ImageScanFrameConverter.Gray(detail.Span, detail.Width, detail.Height, detail.Stride, detail.Format);
            var recovered = Match(gray, state.Template, request.MinConfidence, request.AllMatches, state.DetailCursor, out int next, ct, 16);
            state.DetailCursor = next;
            detailComplete = next == 0;
            foreach (var found in recovered)
                AddDistinct(hits, new ImageScanHit(new Rectangle(detail.OriginX + found.Bounds.X, detail.OriginY + found.Bounds.Y,
                    found.Bounds.Width, found.Bounds.Height), found.Confidence) { TimestampTicks = detail.TimestampTicks });
            if (next == 0) state.Tile++;
            complete = overviewDone && state.Tile >= columns * rows;
        }
        long generation = state.Generation;
        if (complete) { state.Tile = 0; state.DetailCursor = 0; state.Generation = 0; }
        return new ImageScanResult(request.Id, complete ? ImageScanStatus.Complete : ImageScanStatus.Partial,
            hits, generation, timestamp, frame.SampledSize, frame.SourceBounds)
        { Elapsed = elapsed.Elapsed, DetailCoverage = detailComplete ? new[] { detailBounds } : Array.Empty<Rectangle>() };
    }

    public async Task<ImageScanHit?> RevalidateAsync(ImageScanRequest request, ImageScanHit hit, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        ct = linked.Token;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            State state = GetState(request);
            // Known-candidate confirmations may avoid discovery for an entire wait.
            // Keep whole-process telemetry current without changing action timing.
            if (ReferenceEquals(_budget, ImageScanBudget.Shared))
                ImageScanResourceSampler.Sample(request.PollIntervalMs, _lastSampledSize);
            // Allow for fractional scaling, rounded pixels and tiny UI motion without searching a desktop.
            int pad = request.Resolution == ImageScanResolution.Original ? 2 : 10;
            Rectangle crop = new(hit.Bounds.X - pad, hit.Bounds.Y - pad, state.Template.Width + pad * 2, state.Template.Height + pad * 2);
            if (request.Region is { } region) crop = Rectangle.Intersect(crop, region);
            if (crop.Width < state.Template.Width || crop.Height < state.Template.Height) return null;
            using var frame = await _capture.CaptureRegionAsync(crop, ct).ConfigureAwait(false);
            using var gray = ImageScanFrameConverter.Gray(frame.Span, frame.Width, frame.Height, frame.Stride, frame.Format);
            var found = Match(gray, state.Template, request.MinConfidence, false, 0, out _, ct);
            if (found.Count == 0) return null;
            var match = found[0];
            return match with { Bounds = new Rectangle(frame.OriginX + match.Bounds.X, frame.OriginY + match.Bounds.Y, state.Template.Width, state.Template.Height), TimestampTicks = frame.TimestampTicks };
        }
        finally { _gate.Release(); }
    }

    private static void AddDistinct(List<ImageScanHit> hits, ImageScanHit hit)
    {
        for (int i = 0; i < hits.Count; i++)
        {
            var overlap = Rectangle.Intersect(hits[i].Bounds, hit.Bounds);
            if ((long)overlap.Width * overlap.Height <= (long)hit.Bounds.Width * hit.Bounds.Height / 2) continue;
            if (hit.Confidence > hits[i].Confidence) hits[i] = hit;
            return;
        }
        if (hits.Count < MaxHits) hits.Add(hit);
    }

    private static unsafe List<ImageScanHit> Match(Mat scene, Mat template, double confidence, bool all, int cursor, out int next, CancellationToken ct, int limit = MaxHits)
    {
        next = 0;
        var hits = new List<ImageScanHit>();
        if (scene.Width < template.Width || scene.Height < template.Height) return hits;
        ct.ThrowIfCancellationRequested();
        using var scores = new Mat();
        Cv2.MatchTemplate(scene, template, scores, TemplateMatchModes.CCoeffNormed);
        ct.ThrowIfCancellationRequested();
        if (!all)
        {
            Cv2.MinMaxLoc(scores, out _, out double max, out _, out OpenCvSharp.Point position);
            if (double.IsFinite(max) && max >= confidence) hits.Add(new ImageScanHit(new Rectangle(position.X, position.Y, template.Width, template.Height), max));
            return hits;
        }
        // Raster pagination has bounded output and visits later matches even on a very crowded screen.
        // Local maxima plus overlap suppression keep a template from triggering repeatedly on its own edges.
        int total = checked(scores.Width * scores.Height);
        cursor = cursor >= total ? 0 : cursor;
        for (int p = cursor; p < total; p++)
        {
            int y = p / scores.Width, x = p % scores.Width;
            if (x == 0) ct.ThrowIfCancellationRequested();
            float value = ((float*)scores.Ptr(y))[x];
            if (!float.IsFinite(value) || value < confidence) continue;
            bool maximum = true;
            for (int yy = Math.Max(0, y - 1); yy <= Math.Min(scores.Height - 1, y + 1) && maximum; yy++)
                for (int xx = Math.Max(0, x - 1); xx <= Math.Min(scores.Width - 1, x + 1); xx++)
                    if (((float*)scores.Ptr(yy))[xx] > value) { maximum = false; break; }
            if (!maximum) continue;
            AddDistinct(hits, new ImageScanHit(new Rectangle(x, y, template.Width, template.Height), value));
            if (hits.Count == limit) { next = p + 1 < total ? p + 1 : 0; return hits; }
        }
        return hits;
    }

    private State GetState(ImageScanRequest request)
    {
        if (_states.TryGetValue(request.Id, out var state))
        {
            if (state.Path == request.TemplatePath && ReferenceEquals(state.Bytes, request.TemplateBytes)) return state;
            state.Dispose(); _states.Remove(request.Id);
        }
        if (_states.Count >= MaxGroups) throw new InvalidOperationException("Image scanner preparation limit reached.");
        // Clear optional scaled copies before preparing another native template.
        // The decoder checks its declared grayscale size against the remaining
        // session budget before OpenCV is allowed to allocate pixel storage.
        EnsurePreparationRoom((long)BoundedTemplateDecoder.MaxDimension * BoundedTemplateDecoder.MaxDimension);
        state = new State(request, _preparationBudget, EnsurePreparationRoom); _states.Add(request.Id, state); return state;
    }

    private void EnsurePreparationRoom(long bytes)
    {
        if (_preparationBudget.Remaining >= bytes) return;
        foreach (var state in _states.Values) state.ClearVariants();
    }

    private static ImageScanResult Empty(ImageScanRequest request, ImageScanStatus status, string? code = null, string? message = null)
        => new(request.Id, status, Array.Empty<ImageScanHit>(), 0, Stopwatch.GetTimestamp(), Size.Empty, request.Region ?? Rectangle.Empty, code, message);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _shutdown.Cancel();
        _gate.Wait();
        try { foreach (var state in _states.Values) state.Dispose(); _states.Clear(); }
        finally { _gate.Release(); _shutdown.Dispose(); }
    }

    private sealed class PreparationBudget
    {
        private readonly long _limit;
        private long _used;
        public PreparationBudget(long limit)
        {
            if (limit is < 1 or > 128L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(limit));
            _limit = limit;
        }
        public long Used => _used;
        public long Remaining => _limit - _used;
        public void Reserve(long bytes)
        {
            if (bytes < 0 || bytes > Remaining) throw new InvalidOperationException("The image groups exceed the 128 MB prepared-image budget. Use smaller templates or fewer groups.");
            _used += bytes;
        }
        public void Release(long bytes) => _used -= bytes;
    }

    private sealed class State : IDisposable
    {
        public string? Path { get; }
        public byte[]? Bytes { get; }
        public Mat Template { get; }
        public Rectangle Bounds;
        public long Generation, LastScan;
        public int Tile, DetailCursor, OverviewCursor;
        private readonly Dictionary<Size, Mat> _variants = new();
        private readonly PreparationBudget _budget;
        private readonly Action<long> _ensureRoom;
        private readonly long _templateBytes;
        private long _variantBytes;
        public State(ImageScanRequest request, PreparationBudget budget, Action<long> ensureRoom)
        {
            Path = request.TemplatePath; Bytes = request.TemplateBytes;
            _budget = budget; _ensureRoom = ensureRoom;
            Template = BoundedTemplateDecoder.Decode(Path, Bytes, budget.Remaining);
            try
            {
                Cv2.MeanStdDev(Template, out _, out Scalar deviation);
                if (deviation.Val0 <= 1e-6) throw new InvalidOperationException("The target image has no detail. Capture an icon or text with its edges.");
                _templateBytes = Template.Total() * Template.ElemSize();
                _budget.Reserve(_templateBytes);
            }
            catch { Template.Dispose(); throw; }
        }
        public Mat Variant(double scaleX, double scaleY)
        {
            if (!double.IsFinite(scaleX) || !double.IsFinite(scaleY) || scaleX <= 0 || scaleY <= 0 || scaleX > 1 || scaleY > 1)
                throw new InvalidOperationException("The sampled image scale is invalid.");
            var size = new Size(Math.Max(1, (int)Math.Round(Template.Width * scaleX)), Math.Max(1, (int)Math.Round(Template.Height * scaleY)));
            if (size.Width == Template.Width && size.Height == Template.Height) return Template;
            if (_variants.TryGetValue(size, out var existing)) return existing;
            if (_variants.Count >= 4) ClearVariants();
            long bytes = (long)size.Width * size.Height;
            _ensureRoom(bytes); _budget.Reserve(bytes);
            var variant = new Mat();
            try { Cv2.Resize(Template, variant, new OpenCvSharp.Size(size.Width, size.Height), 0, 0, InterpolationFlags.Linear); }
            catch { variant.Dispose(); _budget.Release(bytes); throw; }
            _variantBytes += bytes;
            _variants.Add(size, variant); return variant;
        }
        public void ClearVariants()
        {
            foreach (var mat in _variants.Values) mat.Dispose(); _variants.Clear();
            _budget.Release(_variantBytes); _variantBytes = 0;
        }
        public void Dispose() { ClearVariants(); Template.Dispose(); _budget.Release(_templateBytes); }
    }
}
