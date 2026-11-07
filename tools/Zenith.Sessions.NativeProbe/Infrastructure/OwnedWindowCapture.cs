using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Zenith.Sessions.NativeProbe.Infrastructure;

/// <summary>One bounded WGC owner. Can capture only an HWND owned by this diagnostic process.</summary>
internal sealed class OwnedWindowCapture : IDisposable
{
    private readonly IDirect3DDevice _device;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly SemaphoreSlim _frames = new(0, 1);
    private volatile bool _disposed;
    private nint _devicePointer, _contextPointer;

    public OwnedWindowCapture(nint window)
    {
        Native.GetWindowThreadProcessId(window, out uint pid);
        if (window == 0 || pid != Environment.ProcessId) throw new InvalidOperationException("Only own windows can be captured.");
        if (!GraphicsCaptureSession.IsSupported()) throw new PlatformNotSupportedException("Windows application capture is unavailable.");
        nint dxgi = 0, inspectable = 0;
        IDirect3DDevice? device = null;
        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;
        try
        {
            Marshal.ThrowExceptionForHR(Native.D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7,
                out _devicePointer, out _, out _contextPointer));
            Guid dxgiId = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(_devicePointer, in dxgiId, out dxgi));
            Marshal.ThrowExceptionForHR(Native.CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            device = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            var item = CreateItem(window);
            if (item.Size.Width is <= 0 or > 1024 || item.Size.Height is <= 0 or > 1024)
                throw new InvalidOperationException("Probe capture dimensions exceed the diagnostic bound.");
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            session = pool.CreateCaptureSession(item);
            _device = device; _pool = pool; _session = session;
            pool.FrameArrived += OnFrame;
            session.StartCapture();
        }
        catch
        {
            session?.Dispose(); pool?.Dispose(); device?.Dispose();
            Release(ref _contextPointer); Release(ref _devicePointer);
            throw;
        }
        finally { Release(ref inspectable); Release(ref dxgi); }
    }

    private void OnFrame(Direct3D11CaptureFramePool sender, object args)
    {
        // A callback only signals bounded availability; pixel conversion stays in the owned collector task.
        try { _frames.Release(); }
        catch (SemaphoreFullException) { /* Coalesce already-signalled frames. */ }
        catch (ObjectDisposedException) when (_disposed) { /* Capture teardown raced an in-flight notification. */ }
    }

    public async Task<(byte R, byte G, byte B, long Timestamp)> ReadCenterAsync(CancellationToken ct)
    {
        for (int attempt = 0; attempt < 12; attempt++)
        {
            await _frames.WaitAsync(ct);
            using var frame = _pool.TryGetNextFrame();
            if (frame is null) continue;
            // Join the real readback before returning the borrowed surface/frame, even if the caller
            // cancels while Windows is copying. A cancelled observation must not outlive its owner.
            ct.ThrowIfCancellationRequested();
            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface).AsTask();
            ct.ThrowIfCancellationRequested();
            if (bitmap.PixelWidth > 1024 || bitmap.PixelHeight > 1024
                || bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                throw new InvalidOperationException("Unsupported probe frame format or bounds.");
            uint bytes = checked((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
            var buffer = new Windows.Storage.Streams.Buffer(bytes);
            bitmap.CopyToBuffer(buffer);
            using var reader = DataReader.FromBuffer(buffer);
            int center = checked(((bitmap.PixelHeight / 2) * bitmap.PixelWidth + bitmap.PixelWidth / 2) * 4);
            reader.ReadBytes(new byte[center]);
            byte blue = reader.ReadByte(), green = reader.ReadByte(), red = reader.ReadByte();
            return (red, green, blue, frame.SystemRelativeTime.Ticks);
        }
        throw new InvalidOperationException("No bounded capture frame was produced.");
    }

    private static GraphicsCaptureItem CreateItem(nint window)
    {
        nint name = 0, factory = 0, item = 0;
        try
        {
            const string runtime = "Windows.Graphics.Capture.GraphicsCaptureItem";
            Marshal.ThrowExceptionForHR(Native.WindowsCreateString(runtime, runtime.Length, out name));
            Guid interop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
            Marshal.ThrowExceptionForHR(Native.RoGetActivationFactory(name, ref interop, out factory));
            Guid itemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            nint vtable = Marshal.ReadIntPtr(factory);
            var create = Marshal.GetDelegateForFunctionPointer<CreateForWindow>(Marshal.ReadIntPtr(vtable, 3 * nint.Size));
            Marshal.ThrowExceptionForHR(create(factory, window, ref itemId, out item));
            return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(item);
        }
        finally { Release(ref item); Release(ref factory); if (name != 0) Native.WindowsDeleteString(name); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pool.FrameArrived -= OnFrame;
        _session.Dispose(); _pool.Dispose(); _device.Dispose(); _frames.Dispose();
        Release(ref _contextPointer); Release(ref _devicePointer);
    }
    private static void Release(ref nint pointer) { if (pointer != 0) Marshal.Release(pointer); pointer = 0; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForWindow(nint self, nint window, ref Guid id, out nint item);
    private static class Native
    {
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
        [DllImport("d3d11.dll")] internal static extern int D3D11CreateDevice(nint adapter, int driver, nint software,
            uint flags, nint levels, uint count, uint sdk, out nint device, out int level, out nint context);
        [DllImport("d3d11.dll")] internal static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgi, out nint device);
        [DllImport("combase.dll", CharSet = CharSet.Unicode)] internal static extern int WindowsCreateString(string value, int length, out nint name);
        [DllImport("combase.dll")] internal static extern int WindowsDeleteString(nint name);
        [DllImport("combase.dll")] internal static extern int RoGetActivationFactory(nint name, ref Guid id, out nint factory);
    }
}
