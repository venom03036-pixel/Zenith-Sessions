using System.Buffers;
using System.Drawing;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.Infrastructure.Capture;

/// <summary>
/// GDI BitBlt-based screen capture service.
/// Serves as the reliable fallback capture engine when DXGI/WGC is unavailable (Rule 5, Rule 15).
/// Allocates frame buffers from <see cref="MemoryPool{T}.Shared"/> to eliminate GC churn (Rule 7).
/// </summary>
public sealed class GdiBitBltCaptureService : IScreenCaptureService
{
    private bool _disposed;

    /// <inheritdoc/>
    public CaptureSource CurrentSource => CaptureSource.Gdi;

    /// <inheritdoc/>
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <inheritdoc/>
    public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        if (region.Width <= 0 || region.Height <= 0)
        {
            throw new ArgumentException("Capture region must have positive width and height.", nameof(region));
        }

        var mapper = Win32VirtualDesktopGeometry.Query().CreateMapper();
        Rectangle clamped = mapper.ClampCaptureRegion(region);
        if (clamped.Width <= 0 || clamped.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(region),
                $"Capture region {region} does not intersect virtual desktop {mapper.VirtualDesktopBounds}.");
        }

        CaptureFrame frame = CaptureRegionCore(clamped);
        return Task.FromResult(frame);
    }

    /// <inheritdoc/>
    public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        return CaptureRegionAsync(Win32VirtualDesktopGeometry.Query().VirtualDesktopBounds, ct);
    }

    private static unsafe CaptureFrame CaptureRegionCore(Rectangle region)
    {
        int width = region.Width;
        int height = region.Height;
        int bytesPerPixel = 4; // Bgra32
        int stride = width * bytesPerPixel;
        int totalBytes = stride * height;

        IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(totalBytes);

        IntPtr hdcScreen = IntPtr.Zero;
        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr hOldBitmap = IntPtr.Zero;

        try
        {
            hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to acquire screen device context (GetDC returned null).");
            }

            hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
            if (hdcMem == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create memory device context for capture.");
            }

            hBitmap = NativeMethods.CreateCompatibleBitmap(hdcScreen, width, height);
            if (hBitmap == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create compatible bitmap for capture.");
            }

            hOldBitmap = NativeMethods.SelectObject(hdcMem, hBitmap);
            if (hOldBitmap == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to select bitmap into memory DC.");
            }

            // BitBlt with SRCCOPY | CAPTUREBLT to capture layered/translucent windows properly
            bool bltResult = NativeMethods.BitBlt(
                hdcMem,
                0,
                0,
                width,
                height,
                hdcScreen,
                region.X,
                region.Y,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

            if (!bltResult)
            {
                throw new InvalidOperationException("GDI BitBlt failed to copy screen bits.");
            }

            NativeMethods.BITMAPINFO bmi = default;
            bmi.bmiHeader.biSize = (uint)sizeof(NativeMethods.BITMAPINFOHEADER);
            bmi.bmiHeader.biWidth = width;
            bmi.bmiHeader.biHeight = -height; // Top-down DIB so rows start at top
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = NativeMethods.BI_RGB;

            fixed (byte* pBits = owner.Memory.Span)
            {
                int linesCopied = NativeMethods.GetDIBits(
                    hdcMem,
                    hBitmap,
                    0,
                    (uint)height,
                    (IntPtr)pBits,
                    ref bmi,
                    NativeMethods.DIB_RGB_COLORS);

                if (linesCopied == 0)
                {
                    throw new InvalidOperationException("GetDIBits failed to extract pixel data from bitmap.");
                }
            }

            return new CaptureFrame(
                width,
                height,
                stride,
                FramePixelFormat.Bgra32,
                CaptureSource.Gdi,
                owner,
                originX: region.X,
                originY: region.Y);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
        finally
        {
            // Rule 13: Clean up all native GDI handles
            if (hOldBitmap != IntPtr.Zero && hdcMem != IntPtr.Zero)
            {
                NativeMethods.SelectObject(hdcMem, hOldBitmap);
            }

            if (hBitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(hBitmap);
            }

            if (hdcMem != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(hdcMem);
            }

            if (hdcScreen != IntPtr.Zero)
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, hdcScreen);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _disposed = true;
    }
}
