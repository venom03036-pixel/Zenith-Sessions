using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.Infrastructure.Input;

/// <summary>
/// Observes a physical emergency shortcut independently of macro playback and recording.
/// Create and dispose this listener on the existing UI message-pump thread. It owns no worker,
/// task, timer, or message pump. The callback must only enqueue the emergency-stop request.
/// </summary>
public sealed class EmergencyHotkeyListener : IDisposable
{
    private const uint ModifierMask = NativeMethods.MOD_ALT | NativeMethods.MOD_CONTROL
        | NativeMethods.MOD_SHIFT | NativeMethods.MOD_WIN;
    private const uint InjectedFlags = 0x10 | 0x02; // LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED
    private readonly NativeMethods.HookProc _hookProc;
    private readonly Action _onPressed;
    private readonly Func<bool>? _suppress;
    private readonly EmergencyHotkeyState _state;
    private readonly uint _virtualKey;
    private readonly int _ownerThreadId;
    private IntPtr _hook;
    private Exception? _callbackFailure;

    public EmergencyHotkeyListener(uint modifiers, uint virtualKey, Action onPressed,
        Func<bool>? suppress = null)
    {
        ArgumentNullException.ThrowIfNull(onPressed);
        if (virtualKey is 0 or > 0xFF)
            throw new ArgumentOutOfRangeException(nameof(virtualKey));
        if ((modifiers & ~(ModifierMask | NativeMethods.MOD_NOREPEAT)) != 0)
            throw new ArgumentOutOfRangeException(nameof(modifiers));

        _onPressed = onPressed;
        _suppress = suppress;
        _virtualKey = virtualKey;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _state = new EmergencyHotkeyState(modifiers, virtualKey, IsPressed((int)virtualKey));
        _hookProc = HookCallback;
        _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _hookProc,
            NativeMethods.GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Unable to install the emergency keyboard shortcut listener.");
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero)
            return;
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException(
                "Dispose the emergency keyboard shortcut listener on its owning message-pump thread.");

        if (!NativeMethods.UnhookWindowsHookEx(_hook))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Unable to remove the emergency keyboard shortcut listener.");

        _hook = IntPtr.Zero;
        GC.KeepAlive(_hookProc);
        if (Interlocked.Exchange(ref _callbackFailure, null) is { } failure) Trace.TraceError($"Emergency shortcut callback failed: {failure.GetType().Name}");
    }

    internal static bool MatchesShortcut(uint configuredModifiers, uint configuredVirtualKey,
        uint pressedModifiers, uint pressedVirtualKey) =>
        configuredVirtualKey == pressedVirtualKey
        && (configuredModifiers & ModifierMask) == (pressedModifiers & ModifierMask);

    internal static bool IsEligible(int hookCode, uint message, uint flags) =>
        hookCode >= 0 && (flags & InjectedFlags) == 0
        && message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;

    internal static bool IsPhysical(uint flags) => (flags & InjectedFlags) == 0;

    private unsafe IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && lParam != IntPtr.Zero && _hook != IntPtr.Zero)
        {
            try
            {
                ref readonly var data =
                    ref Unsafe.AsRef<NativeMethods.KBDLLHOOKSTRUCT>((void*)lParam);
                if (data.vkCode == _virtualKey && IsPhysical(data.flags))
                {
                    uint message = (uint)wParam;
                    bool down = IsEligible(nCode, message, data.flags);
                    uint modifiers = down ? ReadModifiers() : 0;
                    bool suppressed = down && (_suppress?.Invoke() ?? false);
                    bool consume = _state.TryHandle(message, data.flags, data.vkCode,
                        modifiers, suppressed, out bool invoke);
                    if (invoke)
                        _onPressed();
                    if (consume)
                        return (IntPtr)1;
                }
            }
            catch (Exception error)
            {
                Interlocked.Exchange(ref _callbackFailure, error); // report after hook removal, never log on the callback path
            }
        }

        return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static uint ReadModifiers()
    {
        uint modifiers = 0;
        if (IsPressed(0x10)) modifiers |= NativeMethods.MOD_SHIFT;   // VK_SHIFT
        if (IsPressed(0x11)) modifiers |= NativeMethods.MOD_CONTROL; // VK_CONTROL
        if (IsPressed(0x12)) modifiers |= NativeMethods.MOD_ALT;     // VK_MENU
        if (IsPressed(0x5B) || IsPressed(0x5C)) modifiers |= NativeMethods.MOD_WIN;
        return modifiers;
    }

    private static bool IsPressed(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}

/// <summary>Managed shortcut edge tracking, testable without installing hooks or generating input.</summary>
internal sealed class EmergencyHotkeyState(uint modifiers, uint virtualKey, bool initiallyDown = false)
{
    private bool _isDown = initiallyDown;
    private bool _consumeUntilUp;

    internal bool TryHandle(uint message, uint flags, uint pressedVirtualKey,
        uint pressedModifiers, bool suppressed, out bool invoke)
    {
        invoke = false;
        if (pressedVirtualKey != virtualKey || !EmergencyHotkeyListener.IsPhysical(flags))
            return false;

        if (message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
        {
            bool consumed = _consumeUntilUp;
            _isDown = false;
            _consumeUntilUp = false;
            return consumed;
        }

        if (!EmergencyHotkeyListener.IsEligible(0, message, flags))
            return false;
        if (_isDown)
            return _consumeUntilUp;

        _isDown = true;
        _consumeUntilUp = !suppressed && EmergencyHotkeyListener.MatchesShortcut(
            modifiers, virtualKey, pressedModifiers, pressedVirtualKey);
        invoke = _consumeUntilUp;
        return _consumeUntilUp;
    }
}
