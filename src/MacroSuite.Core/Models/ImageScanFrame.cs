using System.Buffers;
using System.Drawing;

namespace MacroSuite.Core.Models;

/// <summary>Sampled image pixels with an explicit physical coordinate transform. Never a CaptureFrame.</summary>
public sealed class ImageScanFrame : IDisposable
{
    private IMemoryOwner<byte>? _owner;
    public Rectangle SourceBounds { get; }
    public Size SampledSize { get; }
    public int Width => SampledSize.Width;
    public int Height => SampledSize.Height;
    public int Stride { get; }
    public FramePixelFormat Format { get; }
    public long TimestampTicks { get; }
    public long Generation { get; }
    public ReadOnlyMemory<byte> Memory => _owner?.Memory[..checked(Stride * Height)] ?? ReadOnlyMemory<byte>.Empty;
    public double ScaleX => (double)Width / SourceBounds.Width;
    public double ScaleY => (double)Height / SourceBounds.Height;

    public ImageScanFrame(Rectangle sourceBounds, Size sampledSize, int stride, FramePixelFormat format,
        IMemoryOwner<byte> owner, long timestampTicks, long generation)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (sourceBounds.Width <= 0 || sourceBounds.Height <= 0 || sampledSize.Width <= 0 || sampledSize.Height <= 0 ||
            stride < checked(sampledSize.Width * CaptureFrame.GetBytesPerPixel(format)) ||
            owner.Memory.Length < checked(stride * sampledSize.Height))
        {
            owner.Dispose();
            throw new ArgumentException("Invalid scan frame dimensions or buffer.");
        }
        SourceBounds = sourceBounds; SampledSize = sampledSize; Stride = stride; Format = format;
        _owner = owner; TimestampTicks = timestampTicks; Generation = generation;
    }

    public Rectangle ToPhysical(Rectangle sampled)
    {
        int left = SourceBounds.Left + (int)Math.Floor(sampled.Left / ScaleX);
        int top = SourceBounds.Top + (int)Math.Floor(sampled.Top / ScaleY);
        int right = SourceBounds.Left + (int)Math.Ceiling(sampled.Right / ScaleX);
        int bottom = SourceBounds.Top + (int)Math.Ceiling(sampled.Bottom / ScaleY);
        return Rectangle.Intersect(SourceBounds, Rectangle.FromLTRB(left, top, right, bottom));
    }

    public static Size Fit(Size source, ImageScanResolution resolution)
    {
        if (source.Width <= 0 || source.Height <= 0) throw new ArgumentOutOfRangeException(nameof(source));
        int height = (int)resolution;
        if (height == 0) return source;
        int width = height == 480 ? 854 : height * 16 / 9;
        double scale = Math.Min(1, Math.Min((double)width / source.Width, (double)height / source.Height));
        return new Size(Math.Max(1, (int)Math.Floor(source.Width * scale)), Math.Max(1, (int)Math.Floor(source.Height * scale)));
    }

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Dispose();
}
