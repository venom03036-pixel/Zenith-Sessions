using MacroSuite.Core.Abstractions;

namespace MacroSuite.Infrastructure.Windows;

/// <summary>
/// Implements <see cref="IWindowCoordinateService"/> using Win32 window APIs (Rule 3).
/// </summary>
public sealed class WindowCoordinateService : IWindowCoordinateService
{
    public static readonly WindowCoordinateService Default = new();

    /// <inheritdoc/>
    public IntPtr GetWindowAt(int screenX, int screenY)
    {
        var pt = new NativeMethods.POINT { x = screenX, y = screenY };
        return NativeMethods.WindowFromPoint(pt);
    }

    /// <inheritdoc/>
    public IntPtr GetForegroundWindow()
    {
        return NativeMethods.GetForegroundWindow();
    }

    /// <inheritdoc/>
    public (int clientX, int clientY) ScreenToClient(IntPtr hWnd, int screenX, int screenY)
    {
        if (hWnd == IntPtr.Zero)
            return (screenX, screenY);

        var pt = new NativeMethods.POINT { x = screenX, y = screenY };
        if (NativeMethods.ScreenToClient(hWnd, ref pt))
        {
            return (pt.x, pt.y);
        }

        return (screenX, screenY);
    }

    /// <inheritdoc/>
    public (int screenX, int screenY) ClientToScreen(IntPtr hWnd, int clientX, int clientY)
    {
        if (hWnd == IntPtr.Zero)
            return (clientX, clientY);

        var pt = new NativeMethods.POINT { x = clientX, y = clientY };
        if (NativeMethods.ClientToScreen(hWnd, ref pt))
        {
            return (pt.x, pt.y);
        }

        return (clientX, clientY);
    }

    /// <inheritdoc/>
    public (int width, int height) GetClientSize(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
            return (0, 0);

        if (NativeMethods.GetClientRect(hWnd, out var rect))
        {
            return (rect.Width, rect.Height);
        }

        return (0, 0);
    }

    /// <inheritdoc/>
    public bool IsWindowValid(IntPtr hWnd)
    {
        return hWnd != IntPtr.Zero && NativeMethods.GetClientRect(hWnd, out var rect) && rect.Width > 0 && rect.Height > 0;
    }

    /// <inheritdoc/>
    public IntPtr FindWindowByTitle(string? titleOrProcessName)
    {
        if (string.IsNullOrWhiteSpace(titleOrProcessName))
            return IntPtr.Zero;

        string needle = titleOrProcessName.Trim();
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
                return true;

            int length = NativeMethods.GetWindowTextLengthW(hWnd);
            if (length > 0)
            {
                var sb = new System.Text.StringBuilder(length + 1);
                NativeMethods.GetWindowTextW(hWnd, sb, sb.Capacity);
                if (sb.ToString().Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    found = hWnd;
                    return false;
                }
            }

            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != 0)
            {
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById((int)pid);
                    string? name = process.ProcessName;
                    if (!string.IsNullOrEmpty(name) && name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    {
                        found = hWnd;
                        return false;
                    }
                }
                catch (ArgumentException)
                {
                    // Process exited.
                }
                catch (InvalidOperationException)
                {
                    // Process handle unavailable.
                }
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }
}
