using System.ComponentModel;
using System.Runtime.InteropServices;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>The manager's own window only: the current displays and its physical start position. Never another process's window.</summary>
public static class ManagerWindowNative
{
    // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwindowpos
    private const uint NoZOrder = 0x0004, NoActivate = 0x0010;

    /// <summary>Current display work areas and scales in physical pixels; empty when Windows cannot confirm them.</summary>
    public static IReadOnlyList<ClientDisplay> Displays() => new NativeClientWindowAdapter().Displays(CancellationToken.None).Displays;

    /// <summary>
    /// UI thread, after the window exists and before it is first shown: moves it to <paramref name="bounds"/> in physical pixels.
    /// Placed twice on purpose: when the first move lands on a display with another scale, Windows rescales the window, and the
    /// second placement, now on that display, restores the exact size. False when Windows refused either move.
    /// </summary>
    public static bool Place(nint window, PixelRectangle bounds)
    {
        if (window == 0 || !bounds.IsValid) return false;
        try
        {
            using var physical = new NativeClientWindowAdapter.PhysicalDpiScope();
            return SetWindowPos(window, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, NoZOrder | NoActivate)
                && SetWindowPos(window, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, NoZOrder | NoActivate);
        }
        catch (Win32Exception) { return false; }
    }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
