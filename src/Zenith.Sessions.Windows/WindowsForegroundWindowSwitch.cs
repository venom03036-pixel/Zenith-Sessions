using System.Runtime.InteropServices;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// The foreground window, read and requested through the documented user32 calls (Rule 3). SetForegroundWindow is a request:
/// Windows grants it only to a process that may take the foreground, so the answer is re-read instead of trusting the return value.
/// No window style, hook or client file is touched.
/// </summary>
public sealed class WindowsForegroundWindowSwitch : IForegroundWindowSwitch
{
    public nint Current => Native.GetForegroundWindow();

    public bool BringForward(nint window)
    {
        if (window == 0 || !Native.IsWindow(window)) return false;
        bool requested = Native.SetForegroundWindow(window);
        return requested && Native.GetForegroundWindow() == window;
    }

    private static class Native
    {
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint window);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    }
}

/// <summary>
/// How long ago the user last used the keyboard or mouse anywhere, from GetLastInputInfo: a read-only system fact, not a hook. Null
/// when Windows refuses the call. The tick count wraps every 49.7 days; the difference is taken in unsigned 32-bit arithmetic, which
/// is correct across one wrap.
/// </summary>
public sealed class WindowsUserInputIdleSource : IUserInputIdleSource
{
    public TimeSpan? IdleFor
    {
        get
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info)) return null;
            uint elapsed = unchecked((uint)Environment.TickCount - info.Time);
            return TimeSpan.FromMilliseconds(elapsed);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Time; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
