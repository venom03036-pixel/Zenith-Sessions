using System.Drawing;
using MacroSuite.Core.Abstractions;

namespace MacroSuite.Core.Execution;

/// <summary>
/// Converts stored client-relative points to screen coordinates immediately before input.
/// Missing windows fail closed — never fall back to stale screen coordinates.
/// </summary>
public static class ClientCoordinateResolver
{
    public static Point ResolveScreenPoint(
        IWindowCoordinateService windows,
        Point clientPoint,
        string? targetWindowName,
        int recordedClientWidth = 0,
        int recordedClientHeight = 0,
        MacroSuite.Core.Input.PlaybackSessionContext? playbackSession = null)
    {
        ArgumentNullException.ThrowIfNull(windows);

        IntPtr hwnd = IntPtr.Zero;
        bool strict = playbackSession?.Target?.Rule.Mode is Models.MacroTargetMode.ActiveExternalWindow or Models.MacroTargetMode.Application or Models.MacroTargetMode.SpecificWindow;
        if (strict)
        {
            _ = playbackSession!.GetTargetClientBounds(); // Validate the pinned process instance before translation.
            hwnd = playbackSession.Target!.Window!.Handle;
        }
        else if (!string.IsNullOrWhiteSpace(targetWindowName))
            hwnd = windows.FindWindowByTitle(targetWindowName);

        if (!strict && (hwnd == IntPtr.Zero || !windows.IsWindowValid(hwnd)))
            hwnd = windows.GetForegroundWindow();

        if (hwnd == IntPtr.Zero || !windows.IsWindowValid(hwnd))
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(targetWindowName)
                    ? "Client-relative input failed: the target window could not be resolved."
                    : $"Client-relative input failed: window '{targetWindowName}' could not be resolved.");
        }

        int x = clientPoint.X;
        int y = clientPoint.Y;
        if (recordedClientWidth > 0 && recordedClientHeight > 0)
        {
            var (width, height) = windows.GetClientSize(hwnd);
            if (width > 0 && height > 0)
            {
                x = (int)Math.Round(x * (width / (double)recordedClientWidth));
                y = (int)Math.Round(y * (height / (double)recordedClientHeight));
            }
        }

        var (screenX, screenY) = windows.ClientToScreen(hwnd, x, y);
        return new Point(screenX, screenY);
    }
}
