using System.Runtime.InteropServices;
using Xunit;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class WindowsClientPreviewBufferTests
{
    [Theory]
    [InlineData(1920, 1080, 320, 180)]
    [InlineData(1280, 1024, 225, 180)]
    [InlineData(200, 100, 200, 100)]
    [InlineData(800, 400, 320, 160)]
    [InlineData(400, 800, 90, 180)]
    [InlineData(1, 8192, 1, 180)]
    public void OutputFitsThePreviewAndNeverUpscales(int width, int height, int outputWidth, int outputHeight)
        => Assert.Equal((outputWidth, outputHeight), ClientPreviewBufferReader.OutputSize(new(width, height)));

    [Fact]
    public void AggregateBudgetIncludesPoolsHeldDrainAndOneNativeCpuReadback()
    {
        Assert.True(ClientPreviewBufferReader.FitsBudget([new(3840, 2160)]));
        Assert.True(ClientPreviewBufferReader.FitsBudget(Enumerable.Repeat(new PreviewSurfaceSize(1920, 1080), 4).ToArray()));
        Assert.False(ClientPreviewBufferReader.FitsBudget([new(3840, 2160), new(3840, 2160)]));
        Assert.False(ClientPreviewBufferReader.FitsBudget(Enumerable.Repeat(new PreviewSurfaceSize(320, 180), 5).ToArray()));
        Assert.False(ClientPreviewBufferReader.FitsBudget([new(8192, 8192)]));
        Assert.False(ClientPreviewBufferReader.FitsBudget([new(0, 100)]));
    }

    [Fact]
    public void QpcDatesTheSurfaceRatherThanRefreshingItAtReadTime()
    {
        var now = new DateTimeOffset(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
        const long frequency = 10_000_000, surface = 99 * frequency;
        Assert.True(ClientPreviewBufferReader.TryTimestamp(surface, 0, 100 * frequency, frequency, now, out var first));
        Assert.Equal(now.AddSeconds(-1), first);
        Assert.True(ClientPreviewBufferReader.TryTimestamp(surface, 0, 101 * frequency, frequency, now.AddSeconds(1), out var repeated));
        Assert.Equal(first, repeated);
        Assert.False(ClientPreviewBufferReader.TryTimestamp(surface, surface, 100 * frequency, frequency, now, out _));
        Assert.False(ClientPreviewBufferReader.TryTimestamp(surface, 0, 103 * frequency, frequency, now, out _));
        Assert.False(ClientPreviewBufferReader.TryTimestamp(101 * frequency, 0, 100 * frequency, frequency, now, out _));
        Assert.False(ClientPreviewBufferReader.TryTimestamp(0, 0, 100 * frequency, frequency, now, out _));
        Assert.False(ClientPreviewBufferReader.TryTimestamp(surface, 0, long.MaxValue, 1, now, out _));
        Assert.True(ClientPreviewBufferReader.TryTimestamp(surface, 0, 100 * frequency, frequency, now.AddHours(-1), out var shifted));
        Assert.Equal(now.AddHours(-1).AddSeconds(-1), shifted);
    }

    [Theory]
    [InlineData(8, 12, false)]
    [InlineData(20, -12, true)]
    public void OwnedPaddedAndNegativeStridePlanesCopyOnlyPixelsWithOpaqueAlpha(int start, int stride, bool reversed)
    {
        nint memory = Marshal.AllocHGlobal(40);
        try
        {
            byte[] source = Enumerable.Repeat((byte)0xee, 40).ToArray();
            source[8] = 1; source[9] = 2; source[10] = 3; source[11] = 4;
            source[12] = 5; source[13] = 6; source[14] = 7; source[15] = 8;
            source[20] = 9; source[21] = 10; source[22] = 11; source[23] = 12;
            source[24] = 13; source[25] = 14; source[26] = 15; source[27] = 16;
            Marshal.Copy(source, 0, memory, source.Length);
            byte[] result = ClientPreviewBufferReader.CopyBgra(memory, 40, start, stride, new(2, 2), default);
            byte[] top = [1, 2, 3, 255, 5, 6, 7, 255], bottom = [9, 10, 11, 255, 13, 14, 15, 255];
            Assert.Equal(reversed ? bottom.Concat(top).ToArray() : top.Concat(bottom).ToArray(), result);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [Theory]
    [InlineData(0, 8, 15)]
    [InlineData(0, -8, 16)]
    [InlineData(0, 7, 16)]
    [InlineData(-1, 8, 16)]
    [InlineData(int.MaxValue, int.MaxValue, 16)]
    public void InvalidLayoutIsRejectedBeforeAnyNativePointerRead(int start, int stride, uint capacity)
    {
        Assert.Throws<ArgumentException>(() => ClientPreviewBufferReader.CopyBgra((nint)1, capacity, start, stride, new(2, 2), default));
        Assert.Throws<ArgumentException>(() => ClientPreviewBufferReader.CopyBgra(0, capacity, start, stride, new(2, 2), default));
    }

    [Fact]
    public void DownsamplingSamplesOwnedPixelsDirectlyIntoOnlyTheSmallOutput()
    {
        const int width = 640, height = 360, stride = width * 4;
        nint memory = Marshal.AllocHGlobal(stride * height);
        try
        {
            // Only the sampled positions are initialized; there is no managed source-size bitmap array.
            for (int y = 0; y < 180; y++)
                for (int x = 0; x < 320; x++)
                    Marshal.WriteInt32(memory, (y * 2) * stride + (x * 2) * 4, ((y * 2) & 255) << 8 | ((x * 2) & 255) | 77 << 16);
            byte[] result = ClientPreviewBufferReader.CopyBgra(memory, stride * height, 0, stride, new(width, height), default);
            Assert.Equal(320 * 180 * 4, result.Length);
            Assert.Equal(new byte[] { 0, 0, 77, 255 }, result[..4]);
            Assert.Equal(new byte[] { 126, 102, 77, 255 }, result[^4..]);
            using var stop = new CancellationTokenSource(); stop.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => ClientPreviewBufferReader.CopyBgra(memory, stride * height, 0, stride, new(width, height), stop.Token));
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [Fact]
    public async Task FrameNotificationsCoalesceWithoutExceptionsOrUnboundedTokens()
    {
        using var signal = new ClientPreviewFrameSignal();
        for (int index = 0; index < 10_000; index++) signal.Signal();
        Assert.True(await signal.WaitAsync(TimeSpan.Zero, default));
        Assert.False(await signal.WaitAsync(TimeSpan.Zero, default));
        signal.Signal(); Assert.True(await signal.WaitAsync(TimeSpan.Zero, default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signal.WaitAsync(TimeSpan.Zero, cancelled.Token));
        signal.Dispose(); signal.Signal(); Assert.False(await signal.WaitAsync(TimeSpan.Zero, default));
    }
}
