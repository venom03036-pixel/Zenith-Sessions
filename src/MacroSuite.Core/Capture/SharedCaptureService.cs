using System.Buffers;
using System.Drawing;
using System.Threading.Channels;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Capture;

/// <summary>
/// Single request pipeline for all runtime capture consumers. Concurrent requests are coalesced
/// into one bounding capture and copied into independently owned pooled frames.
/// </summary>
public sealed class SharedCaptureService : IScreenCaptureService, IImageScanCaptureService, IAsyncDisposable
{
    private readonly IScreenCaptureService _source;
    private readonly Channel<CaptureRequest> _requests;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private volatile bool _disposed;

    public CaptureSource CurrentSource => _source.CurrentSource;
    public bool IsSupported => _source.IsSupported;
    public bool SupportsImageScanCapture => _source is IImageScanCaptureService { SupportsImageScanCapture: true };

    public SharedCaptureService(IScreenCaptureService source, int capacity = 256)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _requests = Channel.CreateBounded<CaptureRequest>(new BoundedChannelOptions(Math.Clamp(capacity, 8, 4096))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _worker = Task.Run(() => ProcessAsync(_shutdown.Token));
    }

    public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
    {
        if (region.Width <= 0 || region.Height <= 0)
            throw new ArgumentException("Capture region must have positive dimensions.", nameof(region));
        return EnqueueAsync(new CaptureRequest(region, fullScreen: false), ct);
    }

    public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
        => EnqueueAsync(new CaptureRequest(Rectangle.Empty, fullScreen: true), ct);

    public async Task<ImageScanFrame> CaptureImageAsync(Rectangle? region, ImageScanResolution resolution, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        var request = new CaptureRequest(region ?? Rectangle.Empty, !region.HasValue)
        { ImageCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously), Resolution = resolution };
        request.RegisterCancellation(ct);
        try
        {
            await _requests.Writer.WriteAsync(request, ct).ConfigureAwait(false);
            return await request.ImageCompletion.Task.ConfigureAwait(false);
        }
        finally { request.DisposeRegistration(); }
    }

    private async Task ProcessImageRequestsAsync(List<CaptureRequest> batch, CancellationToken ct)
    {
        // Sampled work stays on this worker. It cannot leak scaled pixels into legacy capture consumers.
        for (int i = 0; i < batch.Count; i++)
        {
            var request = batch[i];
            if (request.ImageCompletion == null || request.ImageCompletion.Task.IsCompleted) continue;
            try
            {
                if (_source is not IImageScanCaptureService sampled)
                    throw new NotSupportedException("The capture source does not support sampled image frames.");
                using var frame = await sampled.CaptureImageAsync(request.FullScreen ? null : request.Region, request.Resolution, ct).ConfigureAwait(false);
                for (int j = i; j < batch.Count; j++)
                {
                    var other = batch[j];
                    if (other.ImageCompletion == null || other.ImageCompletion.Task.IsCompleted || other.Region != request.Region ||
                        other.FullScreen != request.FullScreen || other.Resolution != request.Resolution) continue;
                    var owner = MemoryPool<byte>.Shared.Rent(frame.Memory.Length);
                    frame.Memory.CopyTo(owner.Memory);
                    var copy = new ImageScanFrame(frame.SourceBounds, frame.SampledSize, frame.Stride, frame.Format, owner, frame.TimestampTicks, frame.Generation);
                    if (!other.ImageCompletion.TrySetResult(copy)) copy.Dispose();
                }
            }
            catch (Exception ex) { request.ImageCompletion.TrySetException(ex); }
        }
        batch.RemoveAll(static request => request.ImageCompletion != null);
    }

    private async Task<CaptureFrame> EnqueueAsync(CaptureRequest request, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        request.RegisterCancellation(ct);
        try
        {
            await _requests.Writer.WriteAsync(request, ct).ConfigureAwait(false);
            return await request.Completion.Task.ConfigureAwait(false);
        }
        catch (ChannelClosedException) when (_disposed)
        {
            throw new ObjectDisposedException(nameof(SharedCaptureService));
        }
        finally
        {
            request.DisposeRegistration();
        }
    }

    private async Task ProcessAsync(CancellationToken ct)
    {
        var batch = new List<CaptureRequest>(16);
        try
        {
            while (await _requests.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                batch.Clear();
                if (_requests.Reader.TryRead(out var first))
                    batch.Add(first);
                // Give requests queued in the same scheduler turn one chance to join this frame.
                await Task.Yield();
                while (batch.Count < 256 && _requests.Reader.TryRead(out var pending))
                    batch.Add(pending);

                await ProcessImageRequestsAsync(batch, ct).ConfigureAwait(false);

                bool fullScreen = false;
                bool haveRegion = false;
                Rectangle union = Rectangle.Empty;
                for (int i = 0; i < batch.Count; i++)
                {
                    if (batch[i].Completion.Task.IsCompleted)
                        continue;
                    if (batch[i].FullScreen)
                    {
                        fullScreen = true;
                        continue;
                    }

                    union = haveRegion ? Rectangle.Union(union, batch[i].Region) : batch[i].Region;
                    haveRegion = true;
                }

                if (!fullScreen && !haveRegion)
                    continue;

                CaptureFrame? sourceFrame = null;
                try
                {
                    sourceFrame = fullScreen
                        ? await _source.CaptureScreenAsync(ct).ConfigureAwait(false)
                        : await _source.CaptureRegionAsync(union, ct).ConfigureAwait(false);
                    ArgumentNullException.ThrowIfNull(sourceFrame);
                    CaptureFrame captured = sourceFrame;
                    CaptureFrameIntegrity.ThrowIfUnpopulated(captured);
                    int originX = captured.OriginX;
                    int originY = captured.OriginY;
                    int frameWidth = captured.Width;
                    int frameHeight = captured.Height;
                    bool transferred = false;
                    for (int i = 0; i < batch.Count; i++)
                    {
                        var request = batch[i];
                        if (request.Completion.Task.IsCompleted)
                            continue;
                        Rectangle requested = request.FullScreen
                            ? new Rectangle(originX, originY, frameWidth, frameHeight)
                            : request.Region;
                        try
                        {
                            if (!transferred
                                && batch.Count == 1
                                && requested.X == originX
                                && requested.Y == originY
                                && requested.Width == frameWidth
                                && requested.Height == frameHeight)
                            {
                                if (request.Completion.TrySetResult(captured))
                                {
                                    transferred = true;
                                    sourceFrame = null;
                                }
                            }
                            else
                            {
                                var copy = CopyRegion(captured, requested);
                                if (!request.Completion.TrySetResult(copy)) copy.Dispose();
                            }
                        }
                        catch (Exception ex)
                        {
                            request.Completion.TrySetException(ex);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    for (int i = 0; i < batch.Count; i++)
                        batch[i].Completion.TrySetException(ex);
                }
                finally
                {
                    sourceFrame?.Dispose();
                }
            }
        }
        finally
        {
            for (int i = 0; i < batch.Count; i++)
            {
                batch[i].Completion.TrySetCanceled(ct);
                batch[i].ImageCompletion?.TrySetCanceled(ct);
            }
            while (_requests.Reader.TryRead(out var request))
            {
                request.Completion.TrySetCanceled(ct);
                request.ImageCompletion?.TrySetCanceled(ct);
            }
        }
    }

    private static CaptureFrame CopyRegion(CaptureFrame source, Rectangle requested)
    {
        var sourceBounds = new Rectangle(source.OriginX, source.OriginY, source.Width, source.Height);
        Rectangle clipped = Rectangle.Intersect(requested, sourceBounds);
        if (clipped.Width <= 0 || clipped.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(requested), "Requested region is outside the captured desktop.");

        int bytesPerPixel = CaptureFrame.GetBytesPerPixel(source.Format);
        int stride = checked(clipped.Width * bytesPerPixel);
        int byteCount = checked(stride * clipped.Height);
        IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(byteCount);
        try
        {
            Span<byte> destination = owner.Memory.Span[..byteCount];
            ReadOnlySpan<byte> sourcePixels = source.Span;
            int localX = clipped.X - source.OriginX;
            int localY = clipped.Y - source.OriginY;
            for (int y = 0; y < clipped.Height; y++)
            {
                int sourceOffset = ((localY + y) * source.Stride) + (localX * bytesPerPixel);
                sourcePixels.Slice(sourceOffset, stride).CopyTo(destination.Slice(y * stride, stride));
            }

            return new CaptureFrame(
                clipped.Width,
                clipped.Height,
                stride,
                source.Format,
                source.Source,
                owner,
                source.TimestampTicks,
                clipped.X,
                clipped.Y);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _requests.Writer.TryComplete();
        _shutdown.Cancel();
        try
        {
            _worker.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _shutdown.Dispose();
            _source.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _requests.Writer.TryComplete();
        _shutdown.Cancel();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _shutdown.Dispose();
            _source.Dispose();
        }
    }

    private sealed class CaptureRequest
    {
        private CancellationTokenRegistration _registration;
        private CancellationToken _cancellationToken;

        public Rectangle Region { get; }
        public bool FullScreen { get; }
        public TaskCompletionSource<CaptureFrame> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ImageScanFrame>? ImageCompletion { get; init; }
        public ImageScanResolution Resolution { get; init; }

        public CaptureRequest(Rectangle region, bool fullScreen)
        {
            Region = region;
            FullScreen = fullScreen;
        }

        public void RegisterCancellation(CancellationToken ct)
        {
            if (ct.CanBeCanceled)
            {
                _cancellationToken = ct;
                _registration = ct.UnsafeRegister(
                    static state => ((CaptureRequest)state!).Cancel(),
                    this);
            }
        }

        private void Cancel()
        {
            Completion.TrySetCanceled(_cancellationToken);
            ImageCompletion?.TrySetCanceled(_cancellationToken);
        }

        public void DisposeRegistration() => _registration.Dispose();
    }
}
