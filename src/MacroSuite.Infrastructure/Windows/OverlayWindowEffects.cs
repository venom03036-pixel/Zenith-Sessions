using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MacroSuite.Infrastructure.Windows;

/// <summary>Owns reversible native options for one top-level layered overlay, on its UI thread.</summary>
public sealed class OverlayWindowEffects : IDisposable
{
    private const int ExtendedStyleIndex = -20;
    private const long TransparentStyle = 0x20;
    private const long NoActivateStyle = 0x08000000;
    private const long OwnedStyles = TransparentStyle | NoActivateStyle;
    private const uint ExcludeFromCapture = 0x11;
    private readonly nint _window;
    private readonly long _originalStyles;
    private readonly uint _originalAffinity;
    private bool _disposed;

    public OverlayWindowEffects(nint window)
    {
        if (window == 0) throw new ArgumentException("A live overlay window is required.", nameof(window));
        _window = window;
        _originalStyles = ReadStyles();
        if (!GetWindowDisplayAffinity(window, out _originalAffinity))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The mini toolbar's screenshot option could not be read.");
    }

    public bool ClickThroughApplied { get; private set; }
    public bool CaptureExclusionApplied { get; private set; }

    public void SetClickThrough(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long styles = ReadStyles();
        // Change only our two bits; retain WPF's layered/tool-window and other runtime flags.
        long desired = enabled ? styles | OwnedStyles : (styles & ~OwnedStyles) | (_originalStyles & OwnedStyles);
        WriteStyles(desired);
        ClickThroughApplied = enabled;
    }

    public bool TrySetCaptureExcluded(bool enabled, out string? error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (enabled && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            error = "Hiding the mini toolbar from screenshots requires Windows 10 version 2004 or later.";
            return false;
        }
        if (!SetWindowDisplayAffinity(_window, enabled ? ExcludeFromCapture : _originalAffinity))
        {
            error = "Windows could not change mini toolbar screenshot visibility: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }
        CaptureExclusionApplied = enabled;
        error = null;
        return true;
    }

    private long ReadStyles()
    {
        Marshal.SetLastPInvokeError(0);
        nint result = nint.Size == 8 ? GetWindowLongPtrW(_window, ExtendedStyleIndex) : GetWindowLongW(_window, ExtendedStyleIndex);
        int error = Marshal.GetLastWin32Error();
        if (result == 0 && error != 0) throw new Win32Exception(error, "The mini toolbar's mouse options could not be read.");
        return result.ToInt64();
    }

    private void WriteStyles(long styles)
    {
        Marshal.SetLastPInvokeError(0);
        nint previous = nint.Size == 8 ? SetWindowLongPtrW(_window, ExtendedStyleIndex, (nint)styles) : SetWindowLongW(_window, ExtendedStyleIndex, (int)styles);
        int error = Marshal.GetLastWin32Error();
        if (previous == 0 && error != 0) throw new Win32Exception(error, "The mini toolbar's mouse options could not be changed.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        // The HWND is owned by the view. Restore while it still exists, never close it here.
        try
        {
            SetClickThrough(false);
            if (!TrySetCaptureExcluded(false, out string? error)) System.Diagnostics.Trace.TraceWarning(error);
        }
        catch (Win32Exception ex) { System.Diagnostics.Trace.TraceWarning("Mini toolbar options could not be restored: " + ex.Message); }
        finally { _disposed = true; }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLongW(nint window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLongW(nint window, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
}
