using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.App.Services;

internal interface IHotkeyRegistrationApi
{
    bool Register(IntPtr window, int id, uint modifiers, uint key);
    bool Unregister(IntPtr window, int id);
}

internal sealed class WindowsHotkeyRegistrationApi : IHotkeyRegistrationApi
{
    public bool Register(IntPtr window, int id, uint modifiers, uint key) => NativeMethods.RegisterHotKey(window, id, modifiers, key);
    public bool Unregister(IntPtr window, int id) => NativeMethods.UnregisterHotKey(window, id);
}
