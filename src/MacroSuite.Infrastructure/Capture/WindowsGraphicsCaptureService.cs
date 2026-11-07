using System.Drawing;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.Infrastructure.Capture;

/// <summary>
/// Hardware capture via DXGI Desktop Duplication. Reports unsupported when a D3D11
/// device cannot be created so <see cref="FallbackCaptureCoordinator"/> uses GDI.
/// </summary>
public sealed class WindowsGraphicsCaptureService : IScreenCaptureService, IImageScanCaptureService
{
    private readonly object _gate = new();
    private DxgiDesktopDuplicationCapture? _duplication;
    private bool _deviceAvailable;
    private bool _deviceProbed;
    private bool _disposed;

    public CaptureSource CurrentSource => CaptureSource.Dxgi;

    public bool IsSupported
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_deviceProbed)
                    return _deviceAvailable;
                _deviceAvailable = DxgiDesktopDuplicationCapture.TryCreateDevice(out IntPtr device, out IntPtr context);
                NativeMethods.ReleaseCom(context);
                NativeMethods.ReleaseCom(device);
                _deviceProbed = true;
                return _deviceAvailable;
            }
        }
    }

    public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        if (!IsSupported)
            throw new PlatformNotSupportedException("DXGI Desktop Duplication is not available on this device.");
        if (region.Width <= 0 || region.Height <= 0)
            throw new ArgumentException("Capture region must have positive width and height.", nameof(region));

        var mapper = Win32VirtualDesktopGeometry.Query().CreateMapper();
        Rectangle clamped = mapper.ClampCaptureRegion(region);
        if (clamped.Width <= 0 || clamped.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(region),
                $"Capture region {region} does not intersect virtual desktop {mapper.VirtualDesktopBounds}.");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            _duplication ??= new DxgiDesktopDuplicationCapture();
            CaptureFrame frame = _duplication.CaptureRegion(clamped, ct);
            try
            {
                CaptureFrameIntegrity.ThrowIfUnpopulated(frame);
            }
            catch
            {
                frame.Dispose();
                throw;
            }

            return Task.FromResult(frame);
        }
    }

    public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
    {
        Rectangle bounds = Win32VirtualDesktopGeometry.Query().VirtualDesktopBounds;
        return CaptureRegionAsync(bounds, ct);
    }

    public Task<ImageScanFrame> CaptureImageAsync(Rectangle? region, ImageScanResolution resolution, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        if (!IsSupported)
            throw new PlatformNotSupportedException("DXGI Desktop Duplication is not available on this device.");
        if (!Enum.IsDefined(resolution)) throw new ArgumentOutOfRangeException(nameof(resolution));
        var mapper = Win32VirtualDesktopGeometry.Query().CreateMapper();
        Rectangle requested = region ?? mapper.VirtualDesktopBounds;
        if (requested.Width <= 0 || requested.Height <= 0)
            throw new ArgumentException("Capture region must have positive width and height.", nameof(region));
        Rectangle clamped = mapper.ClampCaptureRegion(requested);
        if (clamped.Width <= 0 || clamped.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(region), "The image search area does not intersect the virtual desktop.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            _duplication ??= new DxgiDesktopDuplicationCapture();
            return Task.FromResult(_duplication.CaptureSampledRegion(clamped, resolution, ct));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _duplication?.Dispose();
            _duplication = null;
        }
    }
}
