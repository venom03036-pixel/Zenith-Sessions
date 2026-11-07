namespace MacroSuite.Core.Abstractions;

/// <summary>
/// Service for translating between absolute virtual screen coordinates and
/// window client-area relative coordinates (Rule 3 — platform isolation).
/// </summary>
public interface IWindowCoordinateService
{
    /// <summary>
    /// Gets the window handle under the specified screen coordinates.
    /// </summary>
    IntPtr GetWindowAt(int screenX, int screenY);

    /// <summary>
    /// Gets the currently active foreground window handle.
    /// </summary>
    IntPtr GetForegroundWindow();

    /// <summary>
    /// Converts absolute screen coordinates to client-area coordinates relative to <paramref name="hWnd"/>.
    /// </summary>
    (int clientX, int clientY) ScreenToClient(IntPtr hWnd, int screenX, int screenY);

    /// <summary>
    /// Converts client-area coordinates relative to <paramref name="hWnd"/> back to absolute screen coordinates.
    /// </summary>
    (int screenX, int screenY) ClientToScreen(IntPtr hWnd, int clientX, int clientY);

    /// <summary>
    /// Gets the width and height of the specified window's client area.
    /// </summary>
    (int width, int height) GetClientSize(IntPtr hWnd);

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="hWnd"/> represents a valid, visible window.
    /// </summary>
    bool IsWindowValid(IntPtr hWnd);

    /// <summary>
    /// Finds a top-level window whose title or process name contains <paramref name="titleOrProcessName"/>.
    /// Returns <see cref="IntPtr.Zero"/> when no window matches.
    /// </summary>
    IntPtr FindWindowByTitle(string? titleOrProcessName);
}
