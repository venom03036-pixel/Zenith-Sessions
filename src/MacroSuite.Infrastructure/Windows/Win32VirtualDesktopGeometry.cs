using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Coordinates;

namespace MacroSuite.Infrastructure.Windows;

/// <summary>
/// Win32 virtual-desktop and per-monitor DPI geometry (Rule 3). Capture and input stay in
/// physical screen pixels; DIP conversion happens only at WPF boundaries.
/// </summary>
public sealed class Win32VirtualDesktopGeometry : IVirtualDesktopGeometry
{
    private static readonly object CacheLock = new();
    private static readonly long CacheTtlTicks = 2 * Stopwatch.Frequency;
    private static Win32VirtualDesktopGeometry? s_cached;
    private static long s_cachedTicks;

    public Rectangle VirtualDesktopBounds { get; }
    public IReadOnlyList<MonitorDescriptor> Monitors { get; }

    private Win32VirtualDesktopGeometry(Rectangle virtualDesktopBounds, IReadOnlyList<MonitorDescriptor> monitors)
    {
        VirtualDesktopBounds = virtualDesktopBounds;
        Monitors = monitors;
    }

    public static Win32VirtualDesktopGeometry Query()
    {
        long now = Stopwatch.GetTimestamp();
        lock (CacheLock)
        {
            if (s_cached is not null && now - s_cachedTicks < CacheTtlTicks)
                return s_cached;

            s_cached = QueryUncached();
            s_cachedTicks = now;
            return s_cached;
        }
    }

    public static void InvalidateCache()
    {
        lock (CacheLock)
        {
            s_cached = null;
            s_cachedTicks = 0;
        }
    }

    private static Win32VirtualDesktopGeometry QueryUncached()
    {
        var bounds = new Rectangle(
            NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException(
                $"Win32 virtual desktop metrics are invalid ({bounds.Width}x{bounds.Height}).");
        }

        var monitors = new List<MonitorDescriptor>(8);
        NativeMethods.MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
        {
            monitors.Add(ReadMonitor(hMonitor, rect));
            return true;
        };

        if (!NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero) || monitors.Count == 0)
        {
            monitors.Add(new MonitorDescriptor(bounds, bounds, 1.0, 1.0, IsPrimary: true));
        }

        GC.KeepAlive(callback);
        return new Win32VirtualDesktopGeometry(bounds, monitors);
    }

    public ScreenCoordinateMapper CreateMapper() => new(this);

    /// <summary>The display under the pointer is the current placement surface.</summary>
    public Rectangle GetCurrentDisplayBounds()
    {
        if (NativeMethods.GetCursorPos(out var pointer))
            foreach (var monitor in Monitors)
                if (monitor.Bounds.Contains(pointer.x, pointer.y)) return monitor.Bounds;
        foreach (var monitor in Monitors)
            if (monitor.IsPrimary) return monitor.Bounds;
        return VirtualDesktopBounds;
    }

    private static MonitorDescriptor ReadMonitor(IntPtr hMonitor, NativeMethods.RECT enumRect)
    {
        var fallback = Rectangle.FromLTRB(enumRect.Left, enumRect.Top, enumRect.Right, enumRect.Bottom);
        var info = new NativeMethods.MONITORINFOEXW
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEXW>(),
            szDevice = string.Empty
        };

        Rectangle bounds = fallback;
        Rectangle workArea = fallback;
        bool primary = false;
        if (NativeMethods.GetMonitorInfoW(hMonitor, ref info))
        {
            bounds = Rectangle.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);
            workArea = Rectangle.FromLTRB(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
            primary = (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;
        }

        double scaleX = 1.0;
        double scaleY = 1.0;
        if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY) >= 0
            && dpiX > 0
            && dpiY > 0)
        {
            scaleX = dpiX / 96.0;
            scaleY = dpiY / 96.0;
        }

        return new MonitorDescriptor(bounds, workArea, scaleX, scaleY, primary);
    }
}
