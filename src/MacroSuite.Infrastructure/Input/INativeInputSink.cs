using System.Runtime.InteropServices;
using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.Infrastructure.Input;

// Native emission and read-only desktop state share a seam so tests never touch real input.
internal interface INativeInputSink
{
    uint Send(NativeMethods.INPUT[] inputs, int count);
    bool IsPressed(int virtualKey);
    bool SuppressAbsoluteMoves { get; }
    (int X, int Y) CursorPosition { get; }
    (int X, int Y, int Width, int Height) VirtualScreen { get; }
}

internal sealed class Win32NativeInputSink : INativeInputSink
{
    private static readonly int InputSize = Marshal.SizeOf<NativeMethods.INPUT>();
    public uint Send(NativeMethods.INPUT[] inputs, int count)
        => NativeMethods.SendInput((uint)count, inputs, InputSize);
    public bool IsPressed(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    public bool SuppressAbsoluteMoves => RecordingWindowFilter.IsPointerConstrained()
        || RecordingWindowFilter.IsAnyMouseButtonDown();
    public (int X, int Y) CursorPosition
        => NativeMethods.GetCursorPos(out var point) ? (point.x, point.y) : (0, 0);
    public (int X, int Y, int Width, int Height) VirtualScreen => (
        NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));
}
