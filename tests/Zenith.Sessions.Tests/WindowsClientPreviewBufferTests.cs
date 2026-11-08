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

    [Theory]
    [InlineData(1920, 1080, 2, 480, 270)]
    [InlineData(3840, 2160, 3, 480, 270)]
    [InlineData(1280, 720, 2, 320, 180)]
    [InlineData(641, 361, 1, 320, 180)]
    [InlineData(1366, 768, 2, 341, 192)]
    [InlineData(400, 800, 2, 100, 200)]
    [InlineData(1, 8192, 5, 1, 256)]
    [InlineData(639, 359, 0, 639, 359)]
    [InlineData(320, 180, 0, 320, 180)]
    [InlineData(200, 100, 0, 200, 100)]
    public void ReadbackUsesTheSmallestGpuLevelThatStillCoversThePreview(int width, int height, int level, int levelWidth, int levelHeight)
    {
        PreviewSurfaceSize source = new(width, height);
        Assert.Equal(level, ClientPreviewBufferReader.ReadbackLevel(source));
        var read = ClientPreviewBufferReader.LevelSize(source, level);
        Assert.Equal(new PreviewSurfaceSize(levelWidth, levelHeight), read);
        // The read level is never smaller than the output, so the CPU step only ever shrinks it.
        var output = ClientPreviewBufferReader.OutputSize(source);
        Assert.True(read.Width >= output.Width && read.Height >= output.Height);
        var next = ClientPreviewBufferReader.LevelSize(source, level + 1);
        Assert.True(next.Width < output.Width || next.Height < output.Height || next == read);
    }

    [Fact]
    public void ExplicitOutputSamplingNeverUpscalesTheReadLevel()
    {
        nint memory = Marshal.AllocHGlobal(32);
        try
        {
            for (int index = 0; index < 8; index++) Marshal.WriteInt32(memory, index * 4, index + 1);
            // A 4x2 level sampled into 2x1 keeps the top row's first and third pixels.
            byte[] result = ClientPreviewBufferReader.CopyBgra(memory, 32, 0, 16, new(4, 2), (2, 1), default);
            Assert.Equal(new byte[] { 1, 0, 0, 255, 3, 0, 0, 255 }, result);
            Assert.Throws<ArgumentException>(() => ClientPreviewBufferReader.CopyBgra(memory, 32, 0, 16, new(4, 2), (5, 1), default));
            Assert.Throws<ArgumentException>(() => ClientPreviewBufferReader.CopyBgra(memory, 32, 0, 16, new(4, 2), (0, 1), default));
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [Theory]
    [InlineData(3840, 2160)]
    [InlineData(8192, 1012)]
    [InlineData(1012, 8192)]
    [InlineData(2880, 2880)]
    public void OneGpuReadbackEntryForTheLargestAllowedSourceFitsTheBudgetsReadbackAllowance(int width, int height)
    {
        // The shared surface budget already reserves MaximumReadbackBytes for readback; the mip chain plus staging
        // copy (or the full-size staging copy when mips are unavailable) must fit inside it.
        PreviewSurfaceSize source = new(width, height);
        Assert.True(source.Valid);
        long mipped = ClientPreviewGpuReadback.EntryBytes(source, ClientPreviewBufferReader.ReadbackLevel(source));
        Assert.InRange(mipped, source.ReservedBytes + 1, ClientPreviewBufferReader.MaximumReadbackBytes);
        Assert.InRange(ClientPreviewGpuReadback.EntryBytes(source, 0), 1, ClientPreviewBufferReader.MaximumReadbackBytes);
    }

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
