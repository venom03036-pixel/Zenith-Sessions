using System.Text;
using MacroSuite.Core.Abstractions;

namespace MacroSuite.Infrastructure.Windows;

/// <summary>
/// Win32 implementation of <see cref="IForegroundWindowInfo"/> (Rule 3).
/// </summary>
public sealed class Win32ForegroundWindowInfo : IForegroundWindowInfo
{
    public static readonly Win32ForegroundWindowInfo Default = new();

    public string? GetForegroundWindowTitle()
    {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return null;

        int length = NativeMethods.GetWindowTextLengthW(hwnd);
        if (length <= 0)
            return string.Empty;

        var buffer = new StringBuilder(length + 1);
        int written = NativeMethods.GetWindowTextW(hwnd, buffer, buffer.Capacity);
        return written <= 0 ? string.Empty : buffer.ToString();
    }
}
