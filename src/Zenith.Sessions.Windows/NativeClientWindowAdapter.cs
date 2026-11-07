using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

internal sealed class NativeClientWindowAdapter : IClientWindowAdapter
{
    // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwindowpos
    // https://learn.microsoft.com/windows/win32/api/shellscalingapi/nf-shellscalingapi-getscalefactorformonitor
    // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setthreaddpiawarenesscontext
    private static readonly string[] AllowedExecutables = ["RobloxPlayerBeta.exe", "RobloxPlayer.exe", "Roblox.exe"];
    // Pixel APIs execute in a synchronous PMv2 thread scope, restored before any await. No process-wide DPI change.
    public ClientDisplayList Displays(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var dpi = new PhysicalDpiScope();
            var displays = new List<ClientDisplay>(); bool incomplete = false;
            Native.MonitorCallback callback = (nint monitor, nint _, ref Native.Rect bounds, nint data) =>
            {
                if (ct.IsCancellationRequested || displays.Count == 32) { incomplete = true; return false; }
                var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>(), Device = "" };
                if (!Native.GetMonitorInfo(monitor, ref info) || Native.GetScaleFactorForMonitor(monitor, out int scale) != 0)
                { incomplete = true; return true; }
                var work = Rect(info.Work);
                var display = new ClientDisplay(info.Device, info.Device, work, scale, (info.Flags & 1) != 0);
                if (display.IsValid) displays.Add(display); else incomplete = true;
                return true;
            };
            bool enumerated = Native.EnumDisplayMonitors(0, 0, callback, 0);
            ct.ThrowIfCancellationRequested();
            if (!enumerated) return new(Array.Empty<ClientDisplay>(), "Windows could not confirm all display workareas and scale values.");
            return new(Array.AsReadOnly(displays.OrderByDescending(display => display.IsPrimary).ThenBy(display => display.WorkArea.Y)
                .ThenBy(display => display.WorkArea.X).ThenBy(display => display.Id, StringComparer.Ordinal).ToArray()),
                incomplete ? "Some displays could not be checked. Only confirmed physical workareas are available." : "Physical display workareas and Windows effective scale observed.");
        }
        catch (Win32Exception) { return new(Array.Empty<ClientDisplay>(), "Windows could not establish a physical-coordinate display context."); }
    }

    internal static bool SameClient(SessionObservation session)
    {
        if (session.ProcessId <= 0 || session.ProcessStartUtcTicks <= 0 || session.Window == 0 || !Native.IsWindow(session.Window)
            || Native.GetAncestor(session.Window, 2) != session.Window) return false;
        Native.GetWindowThreadProcessId(session.Window, out uint owner);
        if (owner != session.ProcessId) return false;
        using var process = Native.OpenProcess(0x1000 | 0x100000, false, session.ProcessId); // Limited query + synchronization.
        if (process.IsInvalid || Native.WaitForSingleObject(process, 0) != 258
            || !ProcessInstanceIdentity.MatchesStart(process, session.ProcessStartUtcTicks)) return false;
        var image = new StringBuilder(1024); uint length = (uint)image.Capacity;
        if (!Native.QueryFullProcessImageName(process, 0, image, ref length)) return false;
        string name = Path.GetFileName(image.ToString());
        if (!AllowedExecutables.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        Native.GetWindowThreadProcessId(session.Window, out owner);
        return owner == session.ProcessId && Native.IsWindow(session.Window) && Native.WaitForSingleObject(process, 0) == 258;
    }

    public ClientWindowCheck Check(SessionObservation session)
    {
        if (!SameClient(session)) return new(false, false, false, false, "The observed client exited or its window changed. Refresh and select it again.");
        bool minimized = Native.IsIconic(session.Window), visible = Native.IsWindowVisible(session.Window), maximized = Native.IsZoomed(session.Window);
        long style = Native.Style(session.Window);
        bool resizable = visible && !minimized && !maximized && (style & 0x40000) != 0;
        return new(true, minimized, visible, resizable, resizable ? "Current client window checked."
            : "Use a visible, resizable, normal window. Restore minimized/maximized clients or leave fullscreen first.", maximized);
    }

    public WindowPostStatus Move(SessionObservation session, PixelRectangle bounds, Func<bool> stopped)
    {
        if (stopped()) return WindowPostStatus.Cancelled;
        try
        {
            using var dpi = new PhysicalDpiScope();
            var state = Check(session);
            if (!state.Current) return WindowPostStatus.Invalid;
            if (!state.CanArrange || !bounds.IsValid) return WindowPostStatus.Unsupported;
            if (stopped()) return WindowPostStatus.Cancelled;
            // ASYNCWINDOWPOS | NOACTIVATE | NOZORDER | NOOWNERZORDER. No restore/minimize, focus or input call.
            return Native.SetWindowPos(session.Window, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x4000 | 0x10 | 0x4 | 0x200)
                ? WindowPostStatus.Accepted : WindowPostStatus.Denied;
        }
        catch (Win32Exception) { return WindowPostStatus.Denied; }
    }

    public WindowPostStatus Change(SessionObservation session, ClientWindowAction action, Func<bool> stopped)
    {
        if (stopped()) return WindowPostStatus.Cancelled;
        if (!SameClient(session)) return WindowPostStatus.Invalid;
        if (stopped()) return WindowPostStatus.Cancelled;
        // A queued call does not confirm the resulting state; the caller observes it. Neither code requests activation.
        Native.ShowWindowAsync(session.Window, action == ClientWindowAction.Minimize ? 7 : 4);
        return WindowPostStatus.Accepted;
    }

    public PixelRectangle? Bounds(SessionObservation session)
    {
        if (!SameClient(session)) return null;
        try
        {
            using var dpi = new PhysicalDpiScope();
            return Native.GetWindowRect(session.Window, out var rect) ? Rect(rect) : null;
        }
        catch (Win32Exception) { return null; }
    }

    // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getclientrect
    // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-clienttoscreen
    // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-monitorfromwindow
    public ClientWindowGeometry? Geometry(SessionObservation session)
    {
        try { using var dpi = new PhysicalDpiScope(); return GeometryCore(session); }
        catch (Win32Exception) { return null; }
    }
    private static ClientWindowGeometry? GeometryCore(SessionObservation session)
    {
        if (!SameClient(session) || !Native.GetWindowRect(session.Window, out var before)
            || !Native.GetClientRect(session.Window, out var client) || client.Left != 0 || client.Top != 0) return null;
        var topLeft = new Native.Point { X = client.Left, Y = client.Top };
        var bottomRight = new Native.Point { X = client.Right, Y = client.Bottom };
        if (!Native.ClientToScreen(session.Window, ref topLeft) || !Native.ClientToScreen(session.Window, ref bottomRight)) return null;
        // MONITOR_DEFAULTTONULL: an off-display window never silently falls back to a different monitor.
        nint monitor = Native.MonitorFromWindow(session.Window, 0);
        var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>(), Device = "" };
        if (monitor == 0 || !Native.GetMonitorInfo(monitor, ref info) || Native.GetScaleFactorForMonitor(monitor, out int scale) != 0
            || !Native.GetWindowRect(session.Window, out var after) || Rect(before) != Rect(after) || !SameClient(session)) return null;
        var content = Rect(new Native.Rect { Left = topLeft.X, Top = topLeft.Y, Right = bottomRight.X, Bottom = bottomRight.Y });
        // Reject mixed observations if the window moved during the two conversions.
        if (content.Width != (long)client.Right - client.Left || content.Height != (long)client.Bottom - client.Top) return null;
        var geometry = new ClientWindowGeometry(Rect(before), content, new(info.Device, info.Device, Rect(info.Work), scale, (info.Flags & 1) != 0));
        return geometry.IsValid ? geometry : null;
    }
    public WindowPostStatus Resize(SessionObservation session, ClientWindowGeometry expected, PixelRectangle bounds, Func<bool> stopped)
    {
        if (stopped()) return WindowPostStatus.Cancelled;
        try
        {
            using var dpi = new PhysicalDpiScope();
            var state = Check(session);
            if (!state.Current) return WindowPostStatus.Invalid;
            if (!state.CanArrange || !expected.IsValid || !expected.Display.WorkArea.Contains(bounds)) return WindowPostStatus.Unsupported;
            if (GeometryCore(session) != expected) return WindowPostStatus.Changed;
            state = Check(session);
            if (!state.Current) return WindowPostStatus.Invalid;
            if (!state.CanArrange) return WindowPostStatus.Unsupported;
            if (stopped()) return WindowPostStatus.Cancelled;
            // Same nonactivating asynchronous sizing lane/flags as tiling. No restore, focus or input.
            return Native.SetWindowPos(session.Window, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x4000 | 0x10 | 0x4 | 0x200)
                ? WindowPostStatus.Accepted : WindowPostStatus.Denied;
        }
        catch (Win32Exception) { return WindowPostStatus.Denied; }
    }
    private static PixelRectangle Rect(Native.Rect rect)
    {
        long width = (long)rect.Right - rect.Left, height = (long)rect.Bottom - rect.Top;
        return width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue ? new(rect.Left, rect.Top, (int)width, (int)height) : default;
    }
    private sealed class PhysicalDpiScope : IDisposable
    {
        private readonly nint _previous = Native.SetThreadDpiAwarenessContext((nint)(-4));
        public PhysicalDpiScope() { if (_previous == 0) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        public void Dispose() { if (Native.SetThreadDpiAwarenessContext(_previous) == 0) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct MonitorInfo
        { public uint Size; public Rect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] [return: MarshalAs(UnmanagedType.Bool)]
        internal delegate bool MonitorCallback(nint monitor, nint dc, ref Rect bounds, nint data);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
        [DllImport("shcore.dll")] internal static extern int GetScaleFactorForMonitor(nint monitor, out int scale);
        [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetThreadDpiAwarenessContext(nint context);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsZoomed(nint window);
        [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint pid);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindowAsync(nint window, int command);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint window, out Rect bounds);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClientRect(nint window, out Rect bounds);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ClientToScreen(nint window, ref Point point);
        [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint window, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
        internal static long Style(nint window) => nint.Size == 8 ? GetWindowLongPtr(window, -16).ToInt64() : GetWindowLong(window, -16);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint rights, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
        [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint length);
    }
}
