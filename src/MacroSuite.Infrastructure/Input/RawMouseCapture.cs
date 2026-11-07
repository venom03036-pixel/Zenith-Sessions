using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.Infrastructure.Input;

internal enum RawMousePacketResult { Ignored, RelativeMovement, AbsoluteDevice, Invalid }

/// <summary>Decodes Windows RAWINPUT mouse packets without touching the desktop.</summary>
internal static class RawMousePacketParser
{
    internal static RawMousePacketResult Parse(ReadOnlySpan<byte> packet, int pointerSize, out int dx, out int dy)
    {
        dx = dy = 0;
        if (pointerSize is not (4 or 8)) return RawMousePacketResult.Invalid;
        int headerSize = 8 + 2 * pointerSize;
        if (packet.Length < headerSize) return RawMousePacketResult.Invalid;
        if (BinaryPrimitives.ReadUInt32LittleEndian(packet) != 0) return RawMousePacketResult.Ignored;
        if (packet.Length < headerSize + 24
            || BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]) != packet.Length)
            return RawMousePacketResult.Invalid;
        var mouse = packet[headerSize..];
        if (BinaryPrimitives.ReadUInt32LittleEndian(mouse[20..]) == LowLevelHookService.PlaybackSentinelTag)
            return RawMousePacketResult.Ignored;
        if ((BinaryPrimitives.ReadUInt16LittleEndian(mouse) & 1) != 0)
            return RawMousePacketResult.AbsoluteDevice;
        dx = BinaryPrimitives.ReadInt32LittleEndian(mouse[12..]);
        dy = BinaryPrimitives.ReadInt32LittleEndian(mouse[16..]);
        return dx == 0 && dy == 0 ? RawMousePacketResult.Ignored : RawMousePacketResult.RelativeMovement;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawInputDeviceRegistration
{
    internal ushort UsagePage;
    internal ushort Usage;
    internal uint Flags;
    internal IntPtr Target;
}

internal interface IRawMouseRegistrationApi
{
    RawInputDeviceRegistration[] GetRegistrations();
    void Register(RawInputDeviceRegistration device);
}

/// <summary>A process registration belongs to the window, not to this library globally.</summary>
internal sealed class RawMouseRegistrationLease : IDisposable
{
    private readonly IRawMouseRegistrationApi _api;
    private readonly IntPtr _window;
    private bool _disposed;

    internal RawMouseRegistrationLease(IRawMouseRegistrationApi api, IntPtr window)
    {
        if (window == IntPtr.Zero) throw new ArgumentException("Raw mouse recording requires a capture window.", nameof(window));
        _api = api;
        _window = window;
        if (api.GetRegistrations().Any(IsMouseRegistration))
            throw new InvalidOperationException("Raw mouse recording is unavailable because another component in this app already owns mouse Raw Input. Turn off raw recording or restart the app after closing that component.");
        api.Register(new RawInputDeviceRegistration { UsagePage = 1, Usage = 2, Flags = 0x100, Target = window }); // INPUTSINK, keep legacy messages
    }

    private static bool IsMouseRegistration(RawInputDeviceRegistration device) =>
        device.UsagePage == 1 && (device.Usage == 2 || device.Usage == 0); // Mouse or whole generic-desktop page

    public void Dispose()
    {
        if (_disposed) return;
        // Never unregister a replacement installed by another component after our startup.
        if (_api.GetRegistrations().Any(d => d.UsagePage == 1 && d.Usage == 2 && d.Target == _window))
            _api.Register(new RawInputDeviceRegistration { UsagePage = 1, Usage = 2, Flags = 1, Target = IntPtr.Zero }); // REMOVE requires null target
        _disposed = true;
    }
}

/// <summary>Message-only receiver on the existing recorder pump. It never synthesizes input.</summary>
internal sealed class RawMouseCapture : IDisposable
{
    private const uint WmInput = 0x00FF;
    private readonly Action<int, int, long> _onMovement;
    private readonly Action<Exception> _onFailure;
    private readonly WindowProc _windowProc; // Native callback GC root
    private readonly string _className = "Zenith.RawMouse." + Guid.NewGuid().ToString("N");
    private readonly IntPtr _module;
    private readonly Exception _readError = new InvalidOperationException("Raw mouse recording stopped because Windows could not deliver a complete mouse packet. The captured prefix was preserved; this recording is incomplete.");
    private readonly Exception _absoluteError = new InvalidOperationException("Raw mouse recording does not support absolute-position devices such as tablets. The captured prefix was preserved. Turn off raw recording to record this device.");
    private RawMouseRegistrationLease? _registration;
    private IntPtr _window;
    private ushort _classAtom;
    private bool _failed;

    internal RawMouseCapture(Action<int, int, long> onMovement, Action<Exception> onFailure)
    {
        _onMovement = onMovement;
        _onFailure = onFailure;
        _windowProc = ReceiveMessage;
        _module = NativeMethods.GetModuleHandleW(null);
        try
        {
            var windowClass = new WindowClass
            {
                Procedure = Marshal.GetFunctionPointerForDelegate(_windowProc),
                Instance = _module,
                ClassName = _className
            };
            _classAtom = RegisterClassW(ref windowClass);
            if (_classAtom == 0) throw NativeFailure("create its window class");
            _window = CreateWindowExW(0, _className, string.Empty, 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, _module, IntPtr.Zero);
            if (_window == IntPtr.Zero) throw NativeFailure("create its message receiver");
            _registration = new RawMouseRegistrationLease(new NativeRegistrationApi(), _window);
        }
        catch (Exception startupError)
        {
            try { Dispose(); }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Raw mouse recording could not start and its partial setup could not be fully cleaned up.", startupError, cleanupError);
            }
            throw;
        }
    }

    private unsafe IntPtr ReceiveMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (message == WmInput && !_failed)
        {
            try
            {
                long timestamp = Stopwatch.GetTimestamp();
                // RAWINPUTHEADER is 16/24 bytes; RAWMOUSE is 24 bytes. Stack storage is bounded.
                byte* buffer = stackalloc byte[64];
                uint size = 64;
                uint read = GetRawInputData(lParam, 0x10000003, buffer, ref size, (uint)(8 + 2 * IntPtr.Size));
                if (read == uint.MaxValue || read > 64)
                    Fail(_readError);
                else
                {
                    var result = RawMousePacketParser.Parse(new ReadOnlySpan<byte>(buffer, (int)read), IntPtr.Size, out int dx, out int dy);
                    if (result == RawMousePacketResult.RelativeMovement) _onMovement(dx, dy, timestamp);
                    else if (result == RawMousePacketResult.AbsoluteDevice) Fail(_absoluteError);
                    else if (result == RawMousePacketResult.Invalid) Fail(_readError);
                }
            }
            catch (Exception error)
            {
                // Exceptions must not cross the unmanaged window callback boundary.
                Fail(error);
            }
        }
        // DefWindowProc performs the required foreground RAWINPUT cleanup.
        return DefWindowProcW(window, message, wParam, lParam);
    }

    private void Fail(Exception error)
    {
        if (_failed) return;
        _failed = true;
        _onFailure(error);
    }

    public void Dispose()
    {
        List<Exception>? failures = null;
        try { _registration?.Dispose(); _registration = null; }
        catch (Exception error) { (failures ??= []).Add(error); }
        if (_window != IntPtr.Zero)
        {
            if (DestroyWindow(_window)) _window = IntPtr.Zero;
            else (failures ??= []).Add(NativeFailure("destroy its message receiver"));
        }
        if (_classAtom != 0)
        {
            if (UnregisterClassW(_className, _module)) _classAtom = 0;
            else (failures ??= []).Add(NativeFailure("release its window class"));
        }
        if (failures != null) throw new AggregateException("Raw mouse recording cleanup was incomplete.", failures);
    }

    private static Exception NativeFailure(string operation) =>
        new Win32Exception(Marshal.GetLastWin32Error(), $"Raw mouse recording could not {operation}. Turn off raw recording to use desktop cursor capture.");

    private sealed class NativeRegistrationApi : IRawMouseRegistrationApi
    {
        public RawInputDeviceRegistration[] GetRegistrations()
        {
            uint count = 0;
            uint size = (uint)Marshal.SizeOf<RawInputDeviceRegistration>();
            if (GetRegisteredRawInputDevices(null, ref count, size) == uint.MaxValue) throw NativeFailure("inspect existing registrations");
            if (count == 0) return [];
            var devices = new RawInputDeviceRegistration[count];
            uint actual = GetRegisteredRawInputDevices(devices, ref count, size);
            if (actual == uint.MaxValue) throw NativeFailure("inspect existing registrations");
            return actual == devices.Length ? devices : devices[..(int)actual];
        }

        public void Register(RawInputDeviceRegistration device)
        {
            if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDeviceRegistration>()))
                throw NativeFailure("register the mouse receiver");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public IntPtr Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassW(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowExW(uint extendedStyle, string className, string name, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterClassW(string className, IntPtr instance);
    [DllImport("user32.dll", SetLastError = true)] private static extern unsafe uint GetRawInputData(IntPtr input, uint command, void* data, ref uint size, uint headerSize);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRegisteredRawInputDevices([Out] RawInputDeviceRegistration[]? devices, ref uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterRawInputDevices(RawInputDeviceRegistration[] devices, uint count, uint size);
}
