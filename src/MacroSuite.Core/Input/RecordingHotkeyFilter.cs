using MacroSuite.Core.Models;

namespace MacroSuite.Core.Input;

/// <summary>
/// Removes one configured control shortcut from a recording while preserving the
/// same key used normally. One instance belongs to one serialized recording consumer.
/// This class observes captured values only; it installs no hooks and sends no input.
/// </summary>
public sealed class RecordingHotkeyFilter
{
    // These wire values match Windows MOD_ALT / CONTROL / SHIFT / WIN. Keeping
    // their numeric contract here avoids a platform or WPF dependency in the engine.
    private const uint Alt = 1;
    private const uint Control = 2;
    private const uint Shift = 4;
    private const uint Win = 8;
    private const uint SupportedModifiers = Alt | Control | Shift | Win;
    private const ushort ShiftKeys = 0b00000000111;
    private const ushort ControlKeys = 0b00000111000;
    private const ushort AltKeys = 0b00111000000;
    private const ushort WinKeys = 0b11000000000;

    private readonly uint _modifiers;
    private readonly uint _virtualKey;
    private readonly bool _enabled;
    private ushort _heldModifiers;
    private bool _controlKeyHeld;
    private bool _suppressControlPress;

    /// <param name="modifiers">Modifier bits: Alt=1, Ctrl=2, Shift=4, Win=8.</param>
    /// <param name="virtualKey">
    /// A nonmodifier virtual key, 1 through 255. Zero or an invalid configuration
    /// disables filtering, so an unavailable optional shortcut cannot discard input.
    /// </param>
    public RecordingHotkeyFilter(uint modifiers, uint virtualKey)
    {
        _modifiers = modifiers;
        _virtualKey = virtualKey;
        _enabled = virtualKey is > 0 and <= 255
            && (modifiers & ~SupportedModifiers) == 0
            && ModifierKeyBit((int)virtualKey) == 0;
    }

    /// <summary>
    /// Observe every raw event in order, before the ordinary recording policy.
    /// Modifier events and nonkeyboard events always pass. A shortcut's first
    /// key-down decides its whole physical press, including repeats and the paired
    /// key-up, even if modifiers change or are released before the control key.
    /// </summary>
    public bool ShouldRecord(in InputEvent input, bool shortcutActive = true)
    {
        if (!_enabled || input.Type is not (InputEventType.KeyDown or InputEventType.KeyUp))
            return true;

        ushort modifier = ModifierKeyBit(input.Data);
        if (modifier != 0)
        {
            if (input.Type == InputEventType.KeyDown) _heldModifiers |= modifier;
            else _heldModifiers &= (ushort)~modifier;
            return true;
        }

        if (input.Data < 0 || (uint)input.Data != _virtualKey)
            return true;

        if (input.Type == InputEventType.KeyDown)
        {
            if (!_controlKeyHeld)
            {
                _controlKeyHeld = true;
                _suppressControlPress = shortcutActive && CurrentModifiers() == _modifiers;
            }
            return !_suppressControlPress;
        }

        bool record = !_controlKeyHeld || !_suppressControlPress;
        _controlKeyHeld = false;
        _suppressControlPress = false;
        return record;
    }

    private uint CurrentModifiers()
    {
        uint modifiers = 0;
        if ((_heldModifiers & AltKeys) != 0) modifiers |= Alt;
        if ((_heldModifiers & ControlKeys) != 0) modifiers |= Control;
        if ((_heldModifiers & ShiftKeys) != 0) modifiers |= Shift;
        if ((_heldModifiers & WinKeys) != 0) modifiers |= Win;
        return modifiers;
    }

    private static ushort ModifierKeyBit(int virtualKey) => virtualKey switch
    {
        0x10 => 1 << 0, // generic Shift
        0xA0 => 1 << 1, // left Shift
        0xA1 => 1 << 2, // right Shift
        0x11 => 1 << 3, // generic Control
        0xA2 => 1 << 4, // left Control
        0xA3 => 1 << 5, // right Control
        0x12 => 1 << 6, // generic Alt
        0xA4 => 1 << 7, // left Alt
        0xA5 => 1 << 8, // right Alt
        0x5B => 1 << 9, // left Windows
        0x5C => 1 << 10, // right Windows
        _ => 0
    };
}
