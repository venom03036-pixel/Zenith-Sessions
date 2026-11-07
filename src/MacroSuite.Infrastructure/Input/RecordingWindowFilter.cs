using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.Infrastructure.Input;

/// <summary>
/// Identifies Macro Engine windows so recording ignores clicks/keys aimed at our own UI.
/// Call from the recording consumer, not from WH_*_LL callbacks.
/// </summary>
public static class RecordingWindowFilter
{
    private static uint _ownProcessId;

    public static void BindToCurrentProcess()
        => _ownProcessId = NativeMethods.GetCurrentProcessId();

    public static bool IsOwnWindowAt(int screenX, int screenY)
    {
        EnsurePid();
        var hwnd = NativeMethods.WindowFromPoint(new NativeMethods.POINT { x = screenX, y = screenY });
        return BelongsToOwnProcess(hwnd);
    }

    /// <summary>
    /// True when any mouse button is currently held. Left, right, middle, and X buttons
    /// all count — games often use right-hold for camera look.
    /// </summary>
    public static bool IsAnyMouseButtonDown()
        => IsMouseButtonDown(MouseButton.Left)
           || IsMouseButtonDown(MouseButton.Right)
           || IsMouseButtonDown(MouseButton.Middle)
           || IsMouseButtonDown(MouseButton.X1)
           || IsMouseButtonDown(MouseButton.X2);

    public static bool IsMouseButtonDown(MouseButton button)
    {
        int vk = button switch
        {
            MouseButton.Left => NativeMethods.VK_LBUTTON,
            MouseButton.Right => NativeMethods.VK_RBUTTON,
            MouseButton.Middle => NativeMethods.VK_MBUTTON,
            MouseButton.X1 => NativeMethods.VK_XBUTTON1,
            MouseButton.X2 => NativeMethods.VK_XBUTTON2,
            _ => 0
        };
        return vk != 0 && (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    /// <summary>
    /// True when Windows has confined the cursor (typical for raw / locked look).
    /// Used to decide whether recorded motion should be stored as deltas.
    /// </summary>
    public static bool IsPointerConstrained()
    {
        if (!NativeMethods.GetClipCursor(out var clip))
            return false;

        int clipW = clip.Right - clip.Left;
        int clipH = clip.Bottom - clip.Top;
        if (clipW <= 0 || clipH <= 0)
            return false;

        int virtualW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int virtualH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        return clipW < virtualW - 8 || clipH < virtualH - 8;
    }

    public static bool IsOwnForegroundWindow()
    {
        EnsurePid();
        return BelongsToOwnProcess(NativeMethods.GetForegroundWindow());
    }

    public static bool BelongsToOwnProcess(IntPtr hwnd)
    {
        EnsurePid();
        if (hwnd == IntPtr.Zero || _ownProcessId == 0)
            return false;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == _ownProcessId;
    }

    private static void EnsurePid()
    {
        if (_ownProcessId == 0)
            _ownProcessId = NativeMethods.GetCurrentProcessId();
    }
}
