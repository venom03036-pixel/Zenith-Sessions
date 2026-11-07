using System.Runtime.InteropServices;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

internal readonly record struct PreviewSurfaceSize(int Width, int Height)
{
    public bool Valid => Width is > 0 and <= ClientPreviewLimits.MaximumSourceDimension
        && Height is > 0 and <= ClientPreviewLimits.MaximumSourceDimension && (long)Width * Height <= ClientPreviewLimits.MaximumSourcePixels;
    // Conservative row alignment allowance. Driver metadata/other application graphics allocations are not measured here.
    public long ReservedBytes => Valid ? ((Width * 4L + 255) / 256) * 256 * Height : long.MaxValue;
}

/// <summary>One availability bit/token. Only a successful wait resets the bit, so callbacks never over-release.</summary>
internal sealed class ClientPreviewFrameSignal : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private int _pending, _disposed;
    public void Signal()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _pending, 1) != 0) return;
        try { _signal.Release(); } catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0) { }
    }
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0) return false;
        if (!await _signal.WaitAsync(timeout, ct).ConfigureAwait(false)) return false;
        Interlocked.Exchange(ref _pending, 0); return true;
    }
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _signal.Dispose(); }
}

/// <summary>Pure limits/QPC and owned native-buffer sampling. Never opens a process/window or creates capture objects.</summary>
internal static class ClientPreviewBufferReader
{
    internal const long MaximumSurfaceBudget = 192L * 1024 * 1024;
    internal const uint MaximumReadbackBytes = 64 * 1024 * 1024;
    internal static bool FitsBudget(IReadOnlyList<PreviewSurfaceSize> sizes)
    {
        if (sizes.Count is < 1 or > ClientPreviewLimits.MaximumTargets) return false;
        long sum = 0, largest = 0;
        foreach (var size in sizes)
        {
            if (!size.Valid) return false;
            sum += size.ReservedBytes * 3; largest = Math.Max(largest, size.ReservedBytes);
        }
        // Two pooled surfaces and one held surface per source, one transient drain frame,
        // and at most one checked SoftwareBitmap readback. Managed outputs are separately <=230400 bytes each.
        return sum + largest + MaximumReadbackBytes <= MaximumSurfaceBudget;
    }
    internal static (int Width, int Height) OutputSize(PreviewSurfaceSize source)
    {
        if (!source.Valid) throw new ArgumentException("The preview surface dimensions are outside the capture limits.");
        if (source.Width <= ClientPreviewLimits.MaximumWidth && source.Height <= ClientPreviewLimits.MaximumHeight) return (source.Width, source.Height);
        return (long)source.Width * ClientPreviewLimits.MaximumHeight >= (long)source.Height * ClientPreviewLimits.MaximumWidth
            ? (ClientPreviewLimits.MaximumWidth, Math.Max(1, (int)((long)source.Height * ClientPreviewLimits.MaximumWidth / source.Width)))
            : (Math.Max(1, (int)((long)source.Width * ClientPreviewLimits.MaximumHeight / source.Height)), ClientPreviewLimits.MaximumHeight);
    }
    internal static bool TryTimestamp(long surfaceTicks, long previousTicks, long qpc, long frequency,
        DateTimeOffset now, out DateTimeOffset capturedUtc)
    {
        capturedUtc = default;
        if (surfaceTicks <= 0 || surfaceTicks <= previousTicks || qpc < 0 || frequency <= 0) return false;
        Int128 compositorNow = (Int128)qpc * TimeSpan.TicksPerSecond / frequency;
        Int128 age = compositorNow - surfaceTicks;
        if (age < -ClientPreviewLimits.MaximumFutureSkew.Ticks || age > ClientPreviewLimits.MaximumFrameAge.Ticks) return false;
        long utc = now.UtcTicks - (long)age;
        if (utc < DateTimeOffset.MinValue.UtcTicks || utc > DateTimeOffset.MaxValue.UtcTicks) return false;
        capturedUtc = new(utc, TimeSpan.Zero); return true;
    }
    internal static byte[] CopyBgra(nint pointer, uint capacity, int startIndex, int stride, PreviewSurfaceSize source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!source.Valid || pointer == 0 || capacity is 0 or > MaximumReadbackBytes || startIndex < 0
            || Math.Abs((long)stride) < source.Width * 4L) throw new ArgumentException("The preview bitmap plane is outside its owned buffer.");
        long last = startIndex + (source.Height - 1L) * stride;
        long low = Math.Min(startIndex, last), high = Math.Max(startIndex, last) + source.Width * 4L;
        if (low < 0 || high > capacity || high > int.MaxValue || (nuint)pointer > nuint.MaxValue - (nuint)high)
            throw new ArgumentException("The preview bitmap plane exceeds its checked capacity.");
        var output = OutputSize(source); var pixels = new byte[checked(output.Width * output.Height * 4)];
        for (int y = 0; y < output.Height; y++)
        {
            ct.ThrowIfCancellationRequested(); int sy = (int)((long)y * source.Height / output.Height);
            for (int x = 0; x < output.Width; x++)
            {
                int sx = (int)((long)x * source.Width / output.Width);
                int offset = checked((int)(startIndex + (long)sy * stride + sx * 4L));
                int value = Marshal.ReadInt32(pointer, offset), destination = (y * output.Width + x) * 4;
                pixels[destination] = unchecked((byte)value); pixels[destination + 1] = unchecked((byte)(value >> 8));
                pixels[destination + 2] = unchecked((byte)(value >> 16)); pixels[destination + 3] = 255;
            }
        }
        return pixels;
    }
}
