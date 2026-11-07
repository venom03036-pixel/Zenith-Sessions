using System.Runtime.InteropServices;
using System.Text;

namespace MacroSuite.Infrastructure.Windows;

/// <summary>
/// P/Invoke declarations for Win32 APIs used by MacroSuite.
/// Only infrastructure code may reference this class (Rule 3).
/// All structs use Pack=1 or explicit layout to match the native ABI exactly.
/// </summary>
internal static partial class NativeMethods
{
    // -----------------------------------------------------------------------
    // Hook types & constants
    // -----------------------------------------------------------------------

    public const int WH_MOUSE_LL    = 14;
    public const int WH_KEYBOARD_LL = 13;

    public const uint WM_QUIT        = 0x0012;
    public const uint WM_KEYDOWN     = 0x0100;
    public const uint WM_KEYUP       = 0x0101;
    public const uint WM_SYSKEYDOWN  = 0x0104;
    public const uint WM_SYSKEYUP    = 0x0105;
    public const uint VK_ESCAPE      = 0x1B;
    public const uint VK_TAB         = 0x09;
    public const int  VK_LBUTTON     = 0x01;
    public const int  VK_RBUTTON     = 0x02;
    public const int  VK_MBUTTON     = 0x04;
    public const int  VK_XBUTTON1    = 0x05;
    public const int  VK_XBUTTON2    = 0x06;
    public const uint WM_MOUSEMOVE   = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP   = 0x0202;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP   = 0x0205;
    public const uint WM_MBUTTONDOWN = 0x0207;
    public const uint WM_MBUTTONUP   = 0x0208;
    public const uint WM_XBUTTONDOWN = 0x020B;
    public const uint WM_XBUTTONUP   = 0x020C;
    public const uint WM_MOUSEWHEEL  = 0x020A;
    public const uint WM_MOUSEHWHEEL = 0x020E;

    public const uint XBUTTON1 = 0x0001;
    public const uint XBUTTON2 = 0x0002;

    // -----------------------------------------------------------------------
    // SendInput constants
    // -----------------------------------------------------------------------

    public const uint INPUT_MOUSE      = 0;
    public const uint INPUT_KEYBOARD   = 1;

    public const uint MOUSEEVENTF_MOVE        = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN    = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP      = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN   = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP     = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN  = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP    = 0x0040;
    public const uint MOUSEEVENTF_XDOWN       = 0x0080;
    public const uint MOUSEEVENTF_XUP         = 0x0100;
    public const uint MOUSEEVENTF_WHEEL       = 0x0800;
    public const uint MOUSEEVENTF_HWHEEL      = 0x1000;
    public const uint MOUSEEVENTF_ABSOLUTE    = 0x8000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    public const uint MOUSEEVENTF_MOVE_NOCOALESCE = 0x2000;

    public const uint KEYEVENTF_KEYUP       = 0x0002;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_UNICODE     = 0x0004;
    public const uint KEYEVENTF_SCANCODE    = 0x0008;
    public const uint MAPVK_VK_TO_VSC       = 0;

    // System metrics for virtual screen (multi-monitor)
    public const int SM_XVIRTUALSCREEN  = 76;
    public const int SM_YVIRTUALSCREEN  = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    // -----------------------------------------------------------------------
    // Structs
    // -----------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    /// <summary>Mouse low-level hook data (lParam cast target for WH_MOUSE_LL).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint  mouseData;
        public uint  flags;
        public uint  time;
        public nuint dwExtraInfo;
    }

    /// <summary>Keyboard low-level hook data (lParam cast target for WH_KEYBOARD_LL).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint  vkCode;
        public uint  scanCode;
        public uint  flags;
        public uint  time;
        public nuint dwExtraInfo;
    }

    /// <summary>Mouse input for SendInput.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int   dx;
        public int   dy;
        public uint  mouseData;
        public uint  dwFlags;
        public uint  time;
        public nuint dwExtraInfo;
    }

    /// <summary>Keyboard input for SendInput.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint   dwFlags;
        public uint   time;
        public nuint  dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint  uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    /// <summary>
    /// Union for the three input types. Uses explicit layout offsets so the union
    /// overlaps MOUSEINPUT, KEYBDINPUT, and HARDWAREINPUT at offset 0, matching the
    /// native <c>INPUT.u</c> union exactly.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT_UNION
    {
        [FieldOffset(0)] public MOUSEINPUT    mi;
        [FieldOffset(0)] public KEYBDINPUT    ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    /// <summary>Top-level INPUT structure passed to SendInput.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint       type;
        public INPUT_UNION ui;
    }

    /// <summary>Message structure for the hook thread message pump.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr   hwnd;
        public uint     message;
        public nuint    wParam;
        public nint     lParam;
        public uint     time;
        public POINT    pt;
    }

    // -----------------------------------------------------------------------
    // Delegates
    // -----------------------------------------------------------------------

    /// <summary>
    /// Low-level hook callback delegate. Must be stored in a field to prevent GC collection
    /// while the hook is active.
    /// </summary>
    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    // -----------------------------------------------------------------------
    // Functions
    // -----------------------------------------------------------------------

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr SetWindowsHookExW(
        int       idHook,
        HookProc  lpfn,
        IntPtr    hMod,
        uint      dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(IntPtr hhk);

    [LibraryImport("user32.dll")]
    public static partial IntPtr CallNextHookEx(
        IntPtr hhk,
        int    nCode,
        IntPtr wParam,
        IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(
        uint    nInputs,
        INPUT[] pInputs,
        int     cbSize);

    [LibraryImport("user32.dll")]
    public static partial void mouse_event(
        uint  dwFlags,
        int   dx,
        int   dy,
        uint  dwData,
        nuint dwExtraInfo);

    [LibraryImport("user32.dll")]
    public static partial void keybd_event(
        byte  bVk,
        byte  bScan,
        uint  dwFlags,
        nuint dwExtraInfo);

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    public static partial uint MapVirtualKeyW(uint uCode, uint uMapType);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCursorPos(int x, int y);

    /// <summary>
    /// Unicode message pump. user32 exports GetMessageW/GetMessageA only — there is no
    /// undecorated GetMessage entry point, so LibraryImport must use the W suffix.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessageW(
        out MSG lpMsg,
        IntPtr  hWnd,
        uint    wMsgFilterMin,
        uint    wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    public static partial IntPtr DispatchMessageW(ref MSG lpmsg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessageW(
        uint    idThread,
        uint    Msg,
        nuint   wParam,
        nint    lParam);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr GetModuleHandleW(string? lpModuleName);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentProcessId();

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int nIndex);

    public const uint MONITORINFOF_PRIMARY = 1;
    public const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEXW
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClipCursor(out RECT lpRect);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    // -----------------------------------------------------------------------
    // GDI / Screen Capture Constants & Structs
    // -----------------------------------------------------------------------

    public const uint SRCCOPY     = 0x00CC0020;
    public const uint CAPTUREBLT  = 0x40000000;
    public const uint DIB_RGB_COLORS = 0;
    public const uint BI_RGB         = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint   biSize;
        public int    biWidth;
        public int    biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint   biCompression;
        public uint   biSizeImage;
        public int    biXPelsPerMeter;
        public int    biYPelsPerMeter;
        public uint   biClrUsed;
        public uint   biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint             bmiColors;
    }

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(
        IntPtr hdc,
        int x,
        int y,
        int cx,
        int cy,
        IntPtr hdcSrc,
        int x1,
        int y1,
        uint rop);

    [LibraryImport("gdi32.dll")]
    public static partial int GetDIBits(
        IntPtr hdc,
        IntPtr hbm,
        uint start,
        uint cLines,
        IntPtr lpvBits,
        ref BITMAPINFO lpbmi,
        uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr ho);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(IntPtr hdc);

    // -----------------------------------------------------------------------
    // Display Enumeration Constants & Structs
    // -----------------------------------------------------------------------

    public const int ENUM_CURRENT_SETTINGS = -1;
    public const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODEW
    {
        private const int CCHDEVICENAME = 32;
        private const int CCHFORMNAME = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;

        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;

        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICEW
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICEW lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "VkKeyScanW")]
    public static extern short VkKeyScanW(char ch);

    // -----------------------------------------------------------------------
    // Window Rect, Client Coordinates & Hotkeys
    // -----------------------------------------------------------------------

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;
    public const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll")]
    public static partial IntPtr WindowFromPoint(POINT point);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);

    // -----------------------------------------------------------------------
    // Multimedia timer resolution (camera sweep / high-rate input)
    // -----------------------------------------------------------------------

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint uPeriod);
}
