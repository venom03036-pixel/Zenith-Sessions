using System.Runtime.InteropServices;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// Owns the manager window's global panic shortcut (RegisterHotKey). Use it on the thread that owns the window. A change
/// registers the new combination under the other of two identifiers before releasing the old one, so Panic never loses its
/// global key in between, and a combination Windows refuses leaves the current one registered (Rule 11).
/// </summary>
public sealed class GlobalPanicRegistration : IDisposable
{
    public const int Message = 0x312;
    private const int First = 0x5A51, Second = 0x5A52;
    private const uint NoRepeat = 0x4000;
    private nint _window;
    private bool _disposed;

    /// <summary>The shortcut Windows accepted for this window, or null when none is registered.</summary>
    public PanicShortcut? Active { get; private set; }
    /// <summary>The WM_HOTKEY identifier of <see cref="Active"/>; 0 when none is registered.</summary>
    public int Identifier { get; private set; }

    /// <summary>
    /// True for either identifier this class uses. A press of the old shortcut can already be queued when it is replaced,
    /// so both count as Panic.
    /// </summary>
    public static bool IsPanic(nint identifier) => identifier == First || identifier == Second;

    /// <summary>
    /// Registers <paramref name="shortcut"/> for <paramref name="window"/> on every desktop program. Returns false when Windows
    /// refuses it, usually because another program already holds it; the previous shortcut then stays registered.
    /// </summary>
    public bool Register(nint window, PanicShortcut shortcut)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(shortcut);
        if (window == 0) throw new ArgumentException("A window handle is required.", nameof(window));
        if (_window != 0 && window != _window) throw new InvalidOperationException("The panic shortcut is already registered for another window.");
        if (shortcut == Active) return true;
        int next = Identifier == First ? Second : First;
        if (!RegisterHotKey(window, next, (uint)shortcut.Modifiers | NoRepeat, (uint)shortcut.VirtualKey)) return false;
        if (Identifier != 0) UnregisterHotKey(window, Identifier);
        _window = window; Identifier = next; Active = shortcut;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Identifier != 0) UnregisterHotKey(_window, Identifier);
        _window = 0; Identifier = 0; Active = null;
    }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(nint window, int id);
}
