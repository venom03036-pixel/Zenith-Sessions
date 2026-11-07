using System.Buffers;
using System.Diagnostics;
using System.Drawing;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.OpenCV;
using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.Infrastructure.Capture;

/// <summary>
/// DXGI Desktop Duplication capture with GPU crop into a pooled BGRA frame.
/// Owned by <see cref="WindowsGraphicsCaptureService"/>. Falls back to GDI when
/// duplication is unavailable (secure desktop, RDP, lost access).
/// </summary>
internal sealed class DxgiDesktopDuplicationCapture : IDisposable
{
    private const int D3D11SdkVersion = 7;
    private const int DriverTypeHardware = 1;
    private const int CreateDeviceBgraSupport = 0x20;
    private const int UsageStaging = 3;
    private const int CpuAccessRead = 0x20000;
    private const int MapRead = 1;
    private const int InitialAcquireTimeoutMs = 40;
    private static readonly Guid D3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid DxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid DxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid DxgiOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");

    private static readonly int WaitTimeout = unchecked((int)0x887A0027);
    private static readonly int AccessLost = unchecked((int)0x887A0026);

    private readonly object _gate = new();
    private IntPtr _device;
    private IntPtr _context;
    private NativeMethods.MapFn? _map;
    private NativeMethods.UnmapFn? _unmap;
    private NativeMethods.CopySubresourceRegionFn? _copy;
    private OutputSession? _session;
    private D3D11ImageScaler? _scaler;
    private bool _scalerUnavailable;
    private long _generation;
    private bool _disposed;

    public static bool TryCreateDevice(out IntPtr device, out IntPtr context)
    {
        device = IntPtr.Zero;
        context = IntPtr.Zero;
        int hr = NativeMethods.D3D11CreateDevice(
            IntPtr.Zero,
            DriverTypeHardware,
            IntPtr.Zero,
            CreateDeviceBgraSupport,
            IntPtr.Zero,
            0,
            D3D11SdkVersion,
            out device,
            out _,
            out context);
        if (hr < 0 || device == IntPtr.Zero || context == IntPtr.Zero)
        {
            NativeMethods.ReleaseCom(device);
            NativeMethods.ReleaseCom(context);
            device = IntPtr.Zero;
            context = IntPtr.Zero;
            return false;
        }

        return true;
    }

    public CaptureFrame CaptureRegion(Rectangle region, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (region.Width <= 0 || region.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(region));

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            EnsureDevice();
            EnsureSession(region);
            OutputSession session = _session
                ?? throw new InvalidOperationException("DXGI output session was not created.");
            if (!session.DesktopBounds.Contains(region))
            {
                throw new InvalidOperationException(
                    $"DXGI output {session.DesktopBounds} cannot cover the requested region {region}; GDI fallback should be used.");
            }

            IntPtr desktop = AcquireDesktopTexture(ct);
            session = _session
                ?? throw new InvalidOperationException("DXGI output session was lost during acquire.");
            if (!session.DesktopBounds.Contains(region))
                throw new InvalidOperationException("The display geometry changed while acquiring the region; GDI fallback should be used.");
            ct.ThrowIfCancellationRequested();
            CopyGpuCrop(session, desktop, region);
            return ReadStaging(session, region);
        }
    }

    public ImageScanFrame CaptureSampledRegion(Rectangle region, ImageScanResolution resolution, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (region.Width <= 0 || region.Height <= 0) throw new ArgumentOutOfRangeException(nameof(region));
        if (!Enum.IsDefined(resolution)) throw new ArgumentOutOfRangeException(nameof(resolution));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            EnsureDevice();
            EnsureSession(region);
            if (_session is null || !_session.DesktopBounds.Contains(region))
                throw new InvalidOperationException("The requested sampled region spans DXGI outputs; shared GDI fallback should be used.");
            IntPtr desktop = AcquireDesktopTexture(ct);
            OutputSession session = _session ?? throw new InvalidOperationException("DXGI output session was lost during acquire.");
            if (!session.DesktopBounds.Contains(region))
                throw new InvalidOperationException("The display geometry changed while acquiring the image area; GDI fallback should be used.");
            Size sampledSize = ImageScanFrame.Fit(region.Size, resolution);
            if (sampledSize != region.Size && !_scalerUnavailable)
            {
                try
                {
                    _scaler ??= new D3D11ImageScaler(_device, _context);
                    var crop = new Rectangle(region.X - session.DesktopBounds.X, region.Y - session.DesktopBounds.Y, region.Width, region.Height);
                    return _scaler.Scale(desktop, crop, region, sampledSize, session.TimestampTicks, session.Generation, ct);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException
                    or DllNotFoundException or EntryPointNotFoundException)
                {
                    // A driver without this shader path still has native DXGI capture.
                    // Disable retries for this device and retain the explicit CPU resize fallback.
                    Trace.WriteLine($"DXGI image downscaling unavailable; using native capture and CPU resize: {ex.Message}");
                    _scaler?.Dispose();
                    _scaler = null;
                    _scalerUnavailable = true;
                }
            }
            ct.ThrowIfCancellationRequested();
            CopyGpuCrop(session, desktop, region);
            using CaptureFrame native = ReadStaging(session, region);
            CaptureFrameIntegrity.ThrowIfUnpopulated(native);
            return ImageScanFrameConverter.Convert(native, resolution);
        }
    }

    private void EnsureDevice()
    {
        if (_device != IntPtr.Zero && _context != IntPtr.Zero)
            return;
        if (!TryCreateDevice(out _device, out _context))
            throw new InvalidOperationException("D3D11 hardware device is unavailable for DXGI capture.");

        _map = NativeMethods.BindMap(_context);
        _unmap = NativeMethods.BindUnmap(_context);
        _copy = NativeMethods.BindCopySubresourceRegion(_context);
    }

    private void EnsureSession(Rectangle region)
    {
        if (_session != null && _session.DesktopBounds.Contains(region))
            return;

        _session?.Dispose();
        _session = null;
        _session = OutputSession.Create(_device, region);
    }

    // Returns a borrowed pointer to an owned GPU snapshot. The duplication resource
    // itself becomes invalid at ReleaseFrame and must never be used as the cache.
    private IntPtr AcquireDesktopTexture(CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            OutputSession session = _session
                ?? throw new InvalidOperationException("DXGI output session was not created.");
            int hr = session.AcquireNextFrame(out NativeMethods.DXGI_OUTDUPL_FRAME_INFO info, out IntPtr resource);
            if (hr == WaitTimeout)
            {
                NativeMethods.ReleaseCom(resource);
                if (session.LatestTexture != IntPtr.Zero)
                {
                    // A successful no-change query freshly confirms these pixels.
                    // Keep content generation, but advance observation time so a
                    // static visible image can satisfy consecutive checks.
                    session.TimestampTicks = Stopwatch.GetTimestamp();
                    return session.LatestTexture;
                }
                throw new InvalidOperationException("DXGI desktop duplication has not produced its first frame; GDI fallback should be used.");
            }
            if (hr == AccessLost)
            {
                NativeMethods.ReleaseCom(resource);
                Rectangle bounds = session.DesktopBounds;
                _session.Dispose();
                _session = null;
                _session = OutputSession.Create(_device, bounds);
                continue;
            }

            IntPtr texture = IntPtr.Zero;
            try
            {
                if (hr < 0 || resource == IntPtr.Zero)
                    throw new InvalidOperationException($"DXGI AcquireNextFrame failed (HRESULT 0x{hr:X8}).");
                // Pointer-only notifications freshly confirm unchanged image content.
                if (info.LastPresentTime == 0 && session.LatestTexture != IntPtr.Zero)
                {
                    session.TimestampTicks = Stopwatch.GetTimestamp();
                    return session.LatestTexture;
                }
                ct.ThrowIfCancellationRequested();
                texture = NativeMethods.QueryInterface(resource, D3D11Texture2D);
                if (texture == IntPtr.Zero)
                    throw new InvalidOperationException("DXGI desktop resource is not an ID3D11Texture2D.");
                var desc = NativeMethods.GetTextureDescription(texture);
                if (!NativeMethods.IsBgraDesktopFormat(desc.Format))
                    throw new InvalidOperationException($"DXGI desktop format {desc.Format} is not BGRA8; GDI fallback should be used.");
                if (desc.Width != session.DesktopBounds.Width || desc.Height != session.DesktopBounds.Height)
                    throw new InvalidOperationException("DXGI desktop texture dimensions do not match its physical output; GDI fallback should be used.");
                session.EnsureLatest(_device);
                var box = new NativeMethods.D3D11_BOX { right = desc.Width, bottom = desc.Height, back = 1 };
                _copy!(_context, session.LatestTexture, 0, 0, 0, 0, texture, 0, in box);
                session.TimestampTicks = Stopwatch.GetTimestamp();
                session.Generation = ++_generation;
                return session.LatestTexture;
            }
            finally
            {
                NativeMethods.ReleaseCom(texture);
                NativeMethods.ReleaseCom(resource);
                session.ReleaseFrame();
            }
        }

        throw new InvalidOperationException("DXGI desktop duplication timed out waiting for a frame.");
    }

    private void CopyGpuCrop(OutputSession session, IntPtr desktop, Rectangle local)
    {
        NativeMethods.CopySubresourceRegionFn copy = _copy
            ?? throw new InvalidOperationException("D3D11 copy entry point is not bound.");
        int dstW = local.Width;
        int dstH = local.Height;
        session.EnsureStaging(dstW, dstH, _device);
        var box = new NativeMethods.D3D11_BOX
        {
            left = (uint)(local.X - session.DesktopBounds.X),
            top = (uint)(local.Y - session.DesktopBounds.Y),
            front = 0,
            right = (uint)(local.Right - session.DesktopBounds.X),
            bottom = (uint)(local.Bottom - session.DesktopBounds.Y),
            back = 1
        };

        copy(_context, session.StagingTexture, 0, 0, 0, 0, desktop, 0, in box);
    }

    private CaptureFrame ReadStaging(OutputSession session, Rectangle local)
    {
        NativeMethods.MapFn map = _map
            ?? throw new InvalidOperationException("D3D11 Map entry point is not bound.");
        NativeMethods.UnmapFn unmap = _unmap
            ?? throw new InvalidOperationException("D3D11 Unmap entry point is not bound.");

        int hr = map(_context, session.StagingTexture, 0, MapRead, 0, out NativeMethods.D3D11_MAPPED_SUBRESOURCE mapped);
        if (hr < 0)
            throw new InvalidOperationException($"ID3D11DeviceContext.Map failed (HRESULT 0x{hr:X8}).");
        try
        {
            if (mapped.pData == IntPtr.Zero || mapped.RowPitch < checked(local.Width * 4))
                throw new InvalidOperationException("DXGI staging map returned an empty or unpopulated buffer.");
            int width = local.Width;
            int height = local.Height;
            int dstStride = checked(width * 4);
            IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(checked(dstStride * height));
            try
            {
                unsafe
                {
                    byte* src = (byte*)mapped.pData;
                    Span<byte> dst = owner.Memory.Span;
                    for (int y = 0; y < height; y++)
                    {
                        new ReadOnlySpan<byte>(src + (y * mapped.RowPitch), dstStride)
                            .CopyTo(dst.Slice(y * dstStride, dstStride));
                    }
                }

                return new CaptureFrame(
                    width,
                    height,
                    dstStride,
                    FramePixelFormat.Bgra32,
                    CaptureSource.Dxgi,
                    owner,
                    timestampTicks: session.TimestampTicks,
                    originX: local.X,
                    originY: local.Y);
            }
            catch
            {
                owner.Dispose();
                throw;
            }
        }
        finally
        {
            unmap(_context, session.StagingTexture, 0);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _session?.Dispose();
            _session = null;
            _scaler?.Dispose();
            _scaler = null;
            _map = null;
            _unmap = null;
            _copy = null;
            NativeMethods.ReleaseCom(_context);
            NativeMethods.ReleaseCom(_device);
            _context = IntPtr.Zero;
            _device = IntPtr.Zero;
        }
    }

    private sealed class OutputSession : IDisposable
    {
        public Rectangle DesktopBounds { get; }
        public IntPtr StagingTexture { get; private set; }
        public IntPtr LatestTexture { get; private set; }
        public long TimestampTicks { get; set; }
        public long Generation { get; set; }
        private readonly IntPtr _duplication;
        private readonly NativeMethods.AcquireNextFrameFn _acquire;
        private readonly NativeMethods.ReleaseFrameFn _release;
        private int _stagingWidth;
        private int _stagingHeight;
        private bool _frameHeld;
        private bool _disposed;

        private OutputSession(IntPtr duplication, Rectangle desktopBounds)
        {
            _duplication = duplication;
            DesktopBounds = desktopBounds;
            _acquire = NativeMethods.BindAcquireNextFrame(duplication);
            _release = NativeMethods.BindReleaseFrame(duplication);
        }

        public static OutputSession Create(IntPtr device, Rectangle region)
        {
            IntPtr dxgiDevice = NativeMethods.QueryInterface(device, DxgiDevice);
            if (dxgiDevice == IntPtr.Zero)
                throw new InvalidOperationException("ID3D11Device does not expose IDXGIDevice.");

            IntPtr adapter = IntPtr.Zero;
            IntPtr factory = IntPtr.Zero;
            try
            {
                adapter = NativeMethods.DxgiGetAdapter(dxgiDevice);
                if (adapter == IntPtr.Zero)
                    throw new InvalidOperationException("IDXGIDevice.GetAdapter failed.");
                factory = NativeMethods.DxgiGetParent(adapter, DxgiFactory1);
                for (uint i = 0; i < 16; i++)
                {
                    IntPtr output = NativeMethods.DxgiEnumOutputs(adapter, i);
                    if (output == IntPtr.Zero)
                        break;

                    try
                    {
                        NativeMethods.DXGI_OUTPUT_DESC desc = NativeMethods.DxgiGetOutputDesc(output);
                        var bounds = Rectangle.FromLTRB(
                            desc.DesktopCoordinates.Left,
                            desc.DesktopCoordinates.Top,
                            desc.DesktopCoordinates.Right,
                            desc.DesktopCoordinates.Bottom);
                        if (desc.AttachedToDesktop == 0 || !bounds.IntersectsWith(region))
                            continue;
                        if (desc.Rotation is not (0 or 1))
                            continue; // A rotated duplication texture requires a transform; GDI provides physical pixels.

                        IntPtr output1 = NativeMethods.QueryInterface(output, DxgiOutput1);
                        if (output1 == IntPtr.Zero)
                            continue;
                        try
                        {
                            IntPtr duplication = NativeMethods.DxgiDuplicateOutput(output1, device);
                            if (duplication == IntPtr.Zero)
                                continue;
                            return new OutputSession(duplication, bounds);
                        }
                        finally
                        {
                            NativeMethods.ReleaseCom(output1);
                        }
                    }
                    finally
                    {
                        NativeMethods.ReleaseCom(output);
                    }
                }
            }
            finally
            {
                NativeMethods.ReleaseCom(factory);
                NativeMethods.ReleaseCom(adapter);
                NativeMethods.ReleaseCom(dxgiDevice);
            }

            throw new InvalidOperationException("No DXGI output could duplicate the requested region.");
        }

        public void EnsureStaging(int width, int height, IntPtr device)
        {
            if (StagingTexture != IntPtr.Zero && _stagingWidth == width && _stagingHeight == height)
                return;

            NativeMethods.ReleaseCom(StagingTexture);
            StagingTexture = IntPtr.Zero;
            StagingTexture = NativeMethods.CreateStagingTexture(
                device,
                width,
                height,
                NativeMethods.DxgiFormatB8G8R8A8Unorm,
                UsageStaging,
                CpuAccessRead);
            _stagingWidth = width;
            _stagingHeight = height;
        }

        public void EnsureLatest(IntPtr device)
        {
            if (LatestTexture != IntPtr.Zero) return;
            LatestTexture = NativeMethods.CreateStagingTexture(device, DesktopBounds.Width, DesktopBounds.Height,
                NativeMethods.DxgiFormatB8G8R8A8Unorm, usage: 0, cpuAccess: 0, bindFlags: 0x8);
        }

        public int AcquireNextFrame(out NativeMethods.DXGI_OUTDUPL_FRAME_INFO info, out IntPtr resource)
        {
            if (_frameHeld)
                ReleaseFrame();
            int hr = _acquire(_duplication, LatestTexture == IntPtr.Zero ? (uint)InitialAcquireTimeoutMs : 0u, out info, out resource);
            _frameHeld = hr >= 0;
            return hr;
        }

        public void ReleaseFrame()
        {
            if (!_frameHeld)
                return;
            _release(_duplication);
            _frameHeld = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseFrame();
            NativeMethods.ReleaseCom(StagingTexture);
            StagingTexture = IntPtr.Zero;
            NativeMethods.ReleaseCom(LatestTexture);
            LatestTexture = IntPtr.Zero;
            NativeMethods.ReleaseCom(_duplication);
        }
    }
}
