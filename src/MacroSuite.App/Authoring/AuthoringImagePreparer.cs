using System.Drawing;
using System.IO;
using MacroSuite.App.Overlays;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.OpenCV;

namespace MacroSuite.App.Authoring;

/// <summary>Prepares detached authoring data using the caller's interactive operation and shared services.</summary>
internal sealed class AuthoringImagePreparer(IScreenCaptureService capture, IImageEncoder encoder)
{
    private const int MaximumVisualEdge = 512;
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    private readonly IScreenCaptureService _capture = capture ?? throw new ArgumentNullException(nameof(capture));
    private readonly IImageEncoder _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
    private int _active;

    public Task<ImageTemplateSelection> CaptureTemplateAsync(HostInputOperationLease parent, Rectangle region,
        double confidence, bool autoClick, CancellationToken ct = default)
    {
        ValidateRegion(region, 4, BoundedTemplateDecoder.MaxDimension);
        ValidateConfidence(confidence);
        return CaptureAsync(parent, region, ct, frame =>
        {
            byte[] encoded = _encoder.EncodePng(frame);
            RequireCurrent(parent, ct);
            ValidateEncodedLength(encoded?.Length ?? 0);
            // The selection and subsequent image condition own these bytes independently of the encoder.
            byte[] bytes = (byte[])encoded!.Clone();
            if (!bytes.AsSpan().StartsWith(PngSignature))
                throw new InvalidDataException("The captured template was not encoded as PNG.");

            RequireCurrent(parent, ct);
            var size = DecodeSize(bytes);
            if (size != region.Size)
                throw new InvalidDataException("The encoded template does not match the captured region.");

            var center = new Point(frame.OriginX + frame.Width / 2, frame.OriginY + frame.Height / 2);
            return new ImageTemplateSelection("Captured image", confidence, autoClick, center, region, size, bytes);
        });
    }

    public Task<ImageTemplateSelection> ImportTemplateAsync(HostInputOperationLease parent, string path,
        double confidence, bool autoClick, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateConfidence(confidence);
        return RunAsync(parent, ct, async () =>
        {
            byte[] bytes;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 8192, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                long length = input.Length;
                ValidateEncodedLength(length);
                RequireCurrent(parent, ct);
                bytes = new byte[(int)length];
                await input.ReadExactlyAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                if (input.Length != length)
                    throw new InvalidDataException("The selected template changed while it was being read.");
            }

            RequireCurrent(parent, ct);
            var size = DecodeSize(bytes);
            return new ImageTemplateSelection(path, confidence, autoClick, null, TemplateSize: size, TemplateBytes: bytes);
        });
    }

    public Task<AuthoringPixelSample> SamplePixelAsync(HostInputOperationLease parent, ScreenPoint point,
        CancellationToken ct = default)
    {
        var region = new Rectangle(point.X, point.Y, 1, 1);
        ValidateRegion(region, 1, 1);
        return CaptureAsync(parent, region, ct, static frame =>
        {
            var pixel = frame.Span;
            return frame.Format == FramePixelFormat.Rgba32
                ? new AuthoringPixelSample(frame.OriginX, frame.OriginY, pixel[0], pixel[1], pixel[2])
                : new AuthoringPixelSample(frame.OriginX, frame.OriginY, pixel[2], pixel[1], pixel[0]);
        });
    }

    public Task<AuthoringVisualSample> CaptureVisualAsync(HostInputOperationLease parent, Rectangle region,
        CancellationToken ct = default)
    {
        ValidateRegion(region, 1, MaximumVisualEdge);
        return CaptureAsync(parent, region, ct, CopyVisual);
    }

    private Task<T> CaptureAsync<T>(HostInputOperationLease parent, Rectangle region, CancellationToken ct,
        Func<CaptureFrame, T> prepare)
        => RunAsync(parent, ct, async () =>
        {
            // SharedCaptureService can cancel a caller before the accepted source request actually ends.
            // Fence publication here and retain ownership until the actual frame or failure arrives.
            RequireCurrent(parent, ct);
            var frame = await _capture.CaptureRegionAsync(region, CancellationToken.None).ConfigureAwait(false);
            try
            {
                RequireCurrent(parent, ct);
                ValidateFrame(frame, region);
                RequireCurrent(parent, ct);
                return prepare(frame);
            }
            finally
            {
                try { frame?.Dispose(); }
                catch (Exception) { throw new HostInputOperationCleanupException(); }
            }
        });

    private Task<T> RunAsync<T>(HostInputOperationLease parent, CancellationToken ct, Func<Task<T>> prepare)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (parent.Kind is not (HostInputOperationKind.Picker or HostInputOperationKind.CaptureTest))
            throw new ArgumentException("Authoring capture requires an interactive operation.", nameof(parent));
        RequireCurrent(parent, ct);
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            throw new InvalidOperationException("Another image preparation is still running.");

        try
        {
            return Task.Run(async () =>
            {
                try
                {
                    RequireCurrent(parent, ct);
                    T result = await prepare().ConfigureAwait(false);
                    // Preparation returns only after its frame, file and decoder have been disposed.
                    RequireCurrent(parent, ct);
                    return result;
                }
                catch (Exception error) when (error is not HostInputOperationCleanupException)
                {
                    RequireCurrent(parent, ct);
                    throw;
                }
                finally { Volatile.Write(ref _active, 0); }
            }, CancellationToken.None);
        }
        catch
        {
            Volatile.Write(ref _active, 0);
            throw;
        }
    }

    private static AuthoringVisualSample CopyVisual(CaptureFrame frame)
    {
        int outputStride = frame.Width * 4;
        byte[] bytes = new byte[outputStride * frame.Height];
        int pixelBytes = CaptureFrame.GetBytesPerPixel(frame.Format);
        var source = frame.Span;
        for (int y = 0; y < frame.Height; y++)
        {
            var row = source.Slice(y * frame.Stride, frame.Width * pixelBytes);
            var output = bytes.AsSpan(y * outputStride, outputStride);
            if (frame.Format == FramePixelFormat.Bgra32)
            {
                row.CopyTo(output);
                continue;
            }

            for (int x = 0; x < frame.Width; x++)
            {
                int src = x * pixelBytes;
                int dst = x * 4;
                if (frame.Format == FramePixelFormat.Rgba32)
                {
                    output[dst] = row[src + 2];
                    output[dst + 2] = row[src];
                    output[dst + 3] = row[src + 3];
                }
                else
                {
                    output[dst] = row[src];
                    output[dst + 2] = row[src + 2];
                    output[dst + 3] = 255;
                }
                output[dst + 1] = row[src + 1];
            }
        }

        int center = (frame.Height / 2) * outputStride + (frame.Width / 2) * 4;
        var region = new Rectangle(frame.OriginX, frame.OriginY, frame.Width, frame.Height);
        return new AuthoringVisualSample(region, frame.Width, frame.Height, bytes,
            bytes[center + 2], bytes[center + 1], bytes[center]);
    }

    private static void RequireCurrent(HostInputOperationLease parent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!parent.CanContinue)
            throw new OperationCanceledException("The authoring operation was stopped.", parent.StopToken);
    }

    private static void ValidateRegion(Rectangle region, int minimumEdge, int maximumEdge)
    {
        if (region.Width < minimumEdge || region.Height < minimumEdge
            || region.Width > maximumEdge || region.Height > maximumEdge
            || (long)region.X + region.Width > int.MaxValue || (long)region.Y + region.Height > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(region), "The physical capture region is outside the supported bounds.");
    }

    private static void ValidateFrame(CaptureFrame frame, Rectangle region)
    {
        if (frame is null || frame.OriginX != region.X || frame.OriginY != region.Y
            || frame.Width != region.Width || frame.Height != region.Height
            || frame.Format is not (FramePixelFormat.Bgra32 or FramePixelFormat.Bgr24 or FramePixelFormat.Rgba32)
            || CaptureFrameIntegrity.IsUnpopulated(frame))
            throw new InvalidDataException("Capture did not return the exact requested physical pixels.");
    }

    private static void ValidateConfidence(double confidence)
    {
        if (!double.IsFinite(confidence) || confidence < 0 || confidence > 1)
            throw new ArgumentOutOfRangeException(nameof(confidence));
    }

    private static void ValidateEncodedLength(long length)
    {
        if (length is < 1 or > BoundedTemplateDecoder.MaxEncodedBytes)
            throw new InvalidDataException("Target images must be between 1 byte and 32 MB.");
    }

    private static Size DecodeSize(byte[] bytes)
    {
        using var image = BoundedTemplateDecoder.Decode(null, bytes);
        return new Size(image.Width, image.Height);
    }
}
