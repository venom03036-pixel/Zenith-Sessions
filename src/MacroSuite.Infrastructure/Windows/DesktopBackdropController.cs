using System.Runtime.InteropServices;

namespace MacroSuite.Infrastructure.Windows;

/// <summary>Windows 11 system acrylic; unsupported systems retain the caller's solid fallback.</summary>
public static class DesktopBackdropController
{
    public static bool TryApply(nint window, bool enabled, bool dark, out string? reason)
    {
        reason = null;
        if (window == 0) return false;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            if (enabled) reason = "Desktop acrylic needs Windows 11 22H2 or later. Solid panels are active.";
            return false;
        }
        int backdrop = enabled ? 3 : 1; // DWMSBT_TRANSIENTWINDOW / DWMSBT_NONE
        int darkMode = dark ? 1 : 0;
        int result = DwmSetWindowAttribute(window, 38, ref backdrop, sizeof(int));
        if (result < 0) { if (enabled) reason = "Windows could not enable desktop acrylic. Solid panels are active."; return false; }
        _ = DwmSetWindowAttribute(window, 20, ref darkMode, sizeof(int));
        var margins = new Margins { Left = enabled ? -1 : 0 };
        if (DwmExtendFrameIntoClientArea(window, ref margins) < 0)
        {
            backdrop = 1; _ = DwmSetWindowAttribute(window, 38, ref backdrop, sizeof(int));
            if (enabled) reason = "Windows could not extend the acrylic surface. Solid panels are active.";
            return false;
        }
        return enabled;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
}
