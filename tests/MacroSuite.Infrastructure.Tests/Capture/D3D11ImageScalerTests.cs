using System.Drawing;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.Capture;
using MacroSuite.Infrastructure.Windows;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.Capture;

// Uses a synthetic texture on a real D3D11 device; never reads the user's screen.
// Opt in with ZENITH_RUN_GPU_TESTS=1. Missing hardware then fails rather than hiding a regression.
[Trait("Category", "GpuHardware")]
public sealed class D3D11ImageScalerTests
{
    [GpuFact]
    public void Scale_CropsAndResizesOnHardware_PreservesBgraAndPhysicalCoordinates()
    {
        using var gpu = new SyntheticGpu();
        using var scaler = new D3D11ImageScaler(gpu.Device, gpu.Context);
        using var frame = scaler.Scale(gpu.Texture, new Rectangle(16, 8, 32, 24),
            new Rectangle(-100, 200, 32, 24), new Size(16, 12), 1234, 7);

        Assert.Equal(FramePixelFormat.Bgra32, frame.Format);
        Assert.Equal(16 * 4, frame.Stride);
        Assert.Equal(1234, frame.TimestampTicks);
        Assert.Equal(7, frame.Generation);
        Assert.Equal(new byte[] { 32, 64, 128, 255 }, Pixel(frame, 1, 1));
        Assert.Equal(new byte[] { 0, 180, 0, 255 }, Pixel(frame, 14, 1));
        Assert.Equal(new byte[] { 200, 0, 0, 255 }, Pixel(frame, 1, 10));
        Assert.Equal(new byte[] { 220, 220, 220, 255 }, Pixel(frame, 14, 10));
        Assert.Equal(new Rectangle(-96, 206, 8, 4), frame.ToPhysical(new Rectangle(2, 3, 4, 2)));
    }

    [GpuFact]
    public void Scale_ReusesResourcesButKeepsReturnedFramesIndependent_AndAcceptsBlack()
    {
        using var gpu = new SyntheticGpu();
        using var scaler = new D3D11ImageScaler(gpu.Device, gpu.Context);
        var bounds = new Rectangle(0, 0, 64, 48);
        using var first = scaler.Scale(gpu.Texture, bounds, bounds, new Size(32, 24), 100, 1);
        gpu.Upload(new byte[64 * 48 * 4]);
        // More than four output sizes exercises bounded cache eviction while a frame remains owned.
        foreach (int width in new[] { 32, 30, 28, 26, 24, 22, 32 })
        {
            using var black = scaler.Scale(gpu.Texture, bounds, bounds, new Size(width, 12), 200, 2);
            Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(black, 1, 1));
            Assert.Equal(200, black.TimestampTicks);
            Assert.Equal(2, black.Generation);
        }
        Assert.Equal(new byte[] { 32, 64, 128, 255 }, Pixel(first, 1, 1));
    }

    [GpuFact]
    public void Scale_RejectsInvalidCropAndCancellation_AndCanDisposeTwice()
    {
        using var gpu = new SyntheticGpu();
        var scaler = new D3D11ImageScaler(gpu.Device, gpu.Context);
        try
        {
            var bounds = new Rectangle(0, 0, 64, 48);
            Assert.Throws<ArgumentOutOfRangeException>(() => scaler.Scale(gpu.Texture,
                new Rectangle(40, 0, 64, 48), bounds, new Size(32, 24), 1, 1));
            Assert.Throws<OperationCanceledException>(() => scaler.Scale(gpu.Texture,
                bounds, bounds, new Size(32, 24), 1, 1, new CancellationToken(true)));
            // Rejected work must not poison the next capture.
            using var valid = scaler.Scale(gpu.Texture, bounds, bounds, new Size(32, 24), 2, 2);
            Assert.Equal(new byte[] { 32, 64, 128, 255 }, Pixel(valid, 1, 1));
        }
        finally { scaler.Dispose(); scaler.Dispose(); }
        Assert.Throws<ObjectDisposedException>(() => scaler.Scale(gpu.Texture,
            new Rectangle(0, 0, 64, 48), new Rectangle(0, 0, 64, 48), new Size(32, 24), 1, 1));
    }

    private static byte[] Pixel(ImageScanFrame frame, int x, int y)
        => frame.Memory.Span.Slice(y * frame.Stride + x * 4, 4).ToArray();

    private sealed class SyntheticGpu : IDisposable
    {
        public IntPtr Device, Context, Texture;

        public SyntheticGpu()
        {
            try
            {
                Assert.True(DxgiDesktopDuplicationCapture.TryCreateDevice(out Device, out Context),
                    "The requested GPU hardware check requires an available D3D11 hardware device.");
                var desc = new NativeMethods.D3D11_TEXTURE2D_DESC
                {
                    Width = 64, Height = 48, MipLevels = 1, ArraySize = 1,
                    Format = NativeMethods.DxgiFormatB8G8R8A8Unorm, SampleCount = 1, BindFlags = 8
                };
                int hr = NativeMethods.GetComDelegate<NativeMethods.CreateTexture2DFn>(Device, 5)(
                    Device, in desc, IntPtr.Zero, out Texture);
                Assert.True(hr >= 0, $"Synthetic texture creation failed: 0x{hr:X8}.");
                byte[] data = new byte[64 * 48 * 4];
                for (int y = 0; y < 48; y++)
                for (int x = 0; x < 64; x++)
                {
                    int i = (y * 64 + x) * 4;
                    if (y < 24 && x < 32) { data[i] = 32; data[i + 1] = 64; data[i + 2] = 128; }
                    else if (y < 24) data[i + 1] = 180;
                    else if (x < 32) data[i] = 200;
                    else data[i] = data[i + 1] = data[i + 2] = 220;
                    data[i + 3] = 255;
                }
                Upload(data);
            }
            catch { Dispose(); throw; }
        }

        public unsafe void Upload(byte[] data)
        {
            fixed (byte* pixels = data)
                NativeMethods.GetComDelegate<NativeMethods.UpdateSubresourceFn>(Context, 48)(
                    Context, Texture, 0, IntPtr.Zero, (IntPtr)pixels, 64 * 4, 0);
        }

        public void Dispose()
        {
            NativeMethods.ReleaseCom(Texture); NativeMethods.ReleaseCom(Context); NativeMethods.ReleaseCom(Device);
            Texture = Context = Device = IntPtr.Zero;
        }
    }
}

public sealed class GpuFactAttribute : FactAttribute
{
    public GpuFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("ZENITH_RUN_GPU_TESTS") != "1")
            Skip = "Opt-in D3D11 hardware validation: set ZENITH_RUN_GPU_TESTS=1 on Windows.";
    }
}
