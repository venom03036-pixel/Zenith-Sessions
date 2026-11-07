using System.Buffers;
using System.Diagnostics;

namespace MacroSuite.Core.Models;

/// <summary>
/// A captured screen frame backed by pooled memory (<see cref="IMemoryOwner{T}"/>).
/// Reuses the potentially large pixel buffer to reduce hot-path allocation pressure.
/// Must be disposed by the owner to return the memory buffer to the pool (Rule 13).
/// </summary>
public sealed class CaptureFrame : IDisposable
{
    private IMemoryOwner<byte>? _memoryOwner;
    private readonly int _byteCount;

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Physical virtual-desktop X coordinate represented by local pixel zero.</summary>
    public int OriginX { get; }

    /// <summary>Physical virtual-desktop Y coordinate represented by local pixel zero.</summary>
    public int OriginY { get; }

    /// <summary>Row stride (bytes per line, including any padding).</summary>
    public int Stride { get; }

    /// <summary>Pixel format of the frame data.</summary>
    public FramePixelFormat Format { get; }

    /// <summary>Hardware/API source that produced this frame.</summary>
    public CaptureSource Source { get; }

    /// <summary>High-resolution timestamp from <see cref="Stopwatch.GetTimestamp"/>.</summary>
    public long TimestampTicks { get; }

    /// <summary>True if the frame buffer has been disposed/returned to the pool.</summary>
    public bool IsDisposed => _memoryOwner is null;

    /// <summary>
    /// Gets a read-only view of the active frame bytes.
    /// Returns empty span if disposed.
    /// </summary>
    public ReadOnlySpan<byte> Span
    {
        get
        {
            var owner = _memoryOwner;
            if (owner is null) return ReadOnlySpan<byte>.Empty;
            return owner.Memory.Span[.._byteCount];
        }
    }

    /// <summary>
    /// Gets a read-only memory view of the active frame bytes.
    /// Returns empty memory if disposed.
    /// </summary>
    public ReadOnlyMemory<byte> Memory
    {
        get
        {
            var owner = _memoryOwner;
            if (owner is null) return ReadOnlyMemory<byte>.Empty;
            return owner.Memory[.._byteCount];
        }
    }

    /// <summary>
    /// Constructs a new <see cref="CaptureFrame"/> wrapping a rented buffer.
    /// </summary>
    public CaptureFrame(
        int width,
        int height,
        int stride,
        FramePixelFormat format,
        CaptureSource source,
        IMemoryOwner<byte> memoryOwner,
        long? timestampTicks = null,
        int originX = 0,
        int originY = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stride);
        ArgumentNullException.ThrowIfNull(memoryOwner);

        int byteCount = checked(height * stride);
        int bufferLength = memoryOwner.Memory.Length;
        if (bufferLength < byteCount)
        {
            memoryOwner.Dispose();
            throw new ArgumentException(
                $"Buffer length ({bufferLength}) is smaller than required frame size ({byteCount}).",
                nameof(memoryOwner));
        }

        Width = width;
        Height = height;
        OriginX = originX;
        OriginY = originY;
        Stride = stride;
        Format = format;
        Source = source;
        _memoryOwner = memoryOwner;
        _byteCount = byteCount;
        TimestampTicks = timestampTicks ?? Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Computes the byte offset into <see cref="Span"/> for the specified pixel coordinates.
    /// </summary>
    public int GetPixelOffset(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException($"Coordinates ({x}, {y}) out of frame bounds ({Width}x{Height}).");
        }

        return (y * Stride) + (x * GetBytesPerPixel(Format));
    }

    /// <summary>
    /// Returns the number of bytes per pixel for the given format.
    /// </summary>
    public static int GetBytesPerPixel(FramePixelFormat format) => format switch
    {
        FramePixelFormat.Bgra32 => 4,
        FramePixelFormat.Rgba32 => 4,
        FramePixelFormat.Bgr24  => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported pixel format.")
    };

    /// <inheritdoc/>
    public void Dispose()
    {
        Interlocked.Exchange(ref _memoryOwner, null)?.Dispose();
    }
}
