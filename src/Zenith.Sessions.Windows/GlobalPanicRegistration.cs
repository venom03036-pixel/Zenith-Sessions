using System.Runtime.InteropServices;

namespace Zenith.Sessions.Windows;

public sealed class GlobalPanicRegistration : IDisposable
{
    public const int Message=0x312,Identifier=0x5A51;
    private nint _window;
    public bool RegisterF12(nint window)
    {
        if(_window!=0) throw new InvalidOperationException("The global stop key is already registered.");
        if(!RegisterHotKey(window,Identifier,0x4000,0x7B)) return false;
        _window=window; return true;
    }
    public void Dispose() { if(_window!=0) { UnregisterHotKey(_window,Identifier); _window=0; } }
    [DllImport("user32.dll",SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(nint window,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(nint window,int id);
}
