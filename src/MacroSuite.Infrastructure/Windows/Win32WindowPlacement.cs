using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace MacroSuite.Infrastructure.Windows;

/// <summary>Places borderless overlays using the physical pixels used by capture.</summary>
public static class Win32WindowPlacement
{
    public static void CancelMove(IntPtr window) { if (window != IntPtr.Zero) PostMessage(window, 0x001F, IntPtr.Zero, IntPtr.Zero); }
    public static void CenterInPhysicalWorkArea(IntPtr window, Rectangle workArea)
    {
        if (window == IntPtr.Zero || !GetWindowRect(window, out var rect)) return;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        int x = workArea.Left + Math.Max(0, (workArea.Width - width) / 2);
        int y = workArea.Top + Math.Max(0, (workArea.Height - height) / 2);
        SetWindowPos(window, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010 | 0x0200);
    }

    /// <summary>Clamp a dragged overlay in physical pixels; never reinterpret a negative monitor origin as DIP.</summary>
    public static void ClampToNearestWorkArea(IntPtr window)
    {
        if (window == IntPtr.Zero || !GetWindowRect(window, out var rect)) return;
        var monitor = new MonitorInformation { Size = Marshal.SizeOf<MonitorInformation>() };
        if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref monitor)) return;
        int x = Math.Clamp(rect.Left, monitor.Work.Left, Math.Max(monitor.Work.Left, monitor.Work.Right - (rect.Right - rect.Left)));
        int y = Math.Clamp(rect.Top, monitor.Work.Top, Math.Max(monitor.Work.Top, monitor.Work.Bottom - (rect.Bottom - rect.Top)));
        SetWindowPos(window, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010 | 0x0200);
    }

    public static void SetPhysicalBounds(IntPtr window, Rectangle bounds)
    {
        if (window == IntPtr.Zero)
            throw new ArgumentException("The preview window does not have a native handle.", nameof(window));
        ValidateBounds(bounds);
        const uint noActivate = 0x0010;
        const uint noOwnerZOrder = 0x0200;
        if (!SetWindowPos(window, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height,
                noActivate | noOwnerZOrder))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not position the screen area overlay.");
    }

    public static void ValidateBounds(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0
            || (long)bounds.X + bounds.Width > int.MaxValue
            || (long)bounds.Y + bounds.Height > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(bounds), "The screen area must have positive, valid pixel dimensions.");
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInformation { public int Size; public NativeRect Bounds, Work; public uint Flags; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInformation information);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
