using System.Runtime.InteropServices;

namespace MacroSuite.Infrastructure.Windows;

/// <summary>Returns an existing application window to the foreground without joining another app's input queue.</summary>
public static class Win32WindowActivation
{
    public static bool IsForeground(IntPtr window) => window != IntPtr.Zero && GetForegroundWindow() == window;
    public static bool IsReady(IntPtr window) => window != IntPtr.Zero && IsWindow(window) && IsWindowVisible(window) && !IsIconic(window)
        && GetWindowRect(window, out var rectangle) && rectangle.Right > rectangle.Left && rectangle.Bottom > rectangle.Top;
    /// <summary>Synchronizes a hidden/minimized owned HWND with the editor's pinned restore state.</summary>
    public static bool RestoreToState(IntPtr window, bool maximized)
    {
        if (window == IntPtr.Zero || !NativeActivationApi.Instance.IsOwnedByCurrentThread(window)) return false;
        // WPF can report Normal after changing WindowState while hidden although the
        // HWND is still iconic. Restore that HWND before asking it to take focus.
        ShowWindow(window, maximized ? 3 : 9); // SW_SHOWMAXIMIZED / SW_RESTORE
        if (!maximized && IsZoomed(window)) ShowWindow(window, 1); // explicit pinned Normal intent
        return IsReady(window) && IsZoomed(window) == maximized;
    }
    public static bool TryActivate(IntPtr window) => TryActivate(window, NativeActivationApi.Instance);
    public static bool TryRequestForeground(IntPtr window)
    {
        var api = NativeActivationApi.Instance;
        if (!IsReady(window) || !api.IsOwnedByCurrentThread(window)) return false;
        if (api.ForegroundWindow != window) api.RequestForeground(window);
        if (api.ForegroundWindow != window) return false;
        api.SetActiveAndFocus(window);
        return api.ForegroundWindow == window;
    }

    internal static bool TryActivate(IntPtr window, IWindowActivationApi api)
    {
        // Focus is a thread-owned operation. Never send synchronous focus work to a foreign thread.
        if (window == IntPtr.Zero || !api.IsOwnedByCurrentThread(window)) return false;
        if (api.ForegroundWindow != window) api.RequestForeground(window);
        if (api.ForegroundWindow != window)
        {
            // Showing a previously hidden window can leave it below the foreground game.
            // Raise only this window, without replacing the user's WPF Topmost binding.
            bool wasTopmost = api.IsTopmost(window);
            try
            {
                api.SetTopmost(window, true);
                api.RequestForeground(window);
            }
            finally
            {
                if (!wasTopmost) api.SetTopmost(window, false);
            }
        }
        if (api.ForegroundWindow != window) return false;
        api.SetActiveAndFocus(window);
        return api.ForegroundWindow == window;
    }

    internal interface IWindowActivationApi
    {
        IntPtr ForegroundWindow { get; }
        bool IsOwnedByCurrentThread(IntPtr window);
        bool IsTopmost(IntPtr window);
        void RequestForeground(IntPtr window);
        void SetTopmost(IntPtr window, bool topmost);
        void SetActiveAndFocus(IntPtr window);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    private sealed class NativeActivationApi : IWindowActivationApi
    {
        internal static readonly NativeActivationApi Instance = new();
        public IntPtr ForegroundWindow => GetForegroundWindow();
        public bool IsOwnedByCurrentThread(IntPtr window) => GetWindowThreadProcessId(window, out _) == GetCurrentThreadId();
        public bool IsTopmost(IntPtr window) => (GetWindowLong(window, -20) & 0x00000008) != 0;
        public void RequestForeground(IntPtr window) => SetForegroundWindow(window);
        public void SetTopmost(IntPtr window, bool topmost) => SetWindowPos(window, new IntPtr(topmost ? -1 : -2), 0, 0, 0, 0,
            0x0001 | 0x0002 | 0x0010 | 0x0200); // NOSIZE | NOMOVE | NOACTIVATE | NOOWNERZORDER
        public void SetActiveAndFocus(IntPtr window) { SetActiveWindow(window); SetFocus(window); }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    }
}
