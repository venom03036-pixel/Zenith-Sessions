using System.Diagnostics.CodeAnalysis;

namespace Zenith.Sessions.Core;

/// <summary>Modifier keys a panic shortcut may use. The values are RegisterHotKey's MOD_ALT, MOD_CONTROL and MOD_SHIFT.</summary>
[Flags]
public enum PanicShortcutModifiers { None = 0, Alt = 1, Control = 2, Shift = 4 }

/// <summary>
/// The global panic stop shortcut, F12 unless the user saves another. A global shortcut takes its keys from every program, so
/// combinations that type characters or run common commands elsewhere are refused. Allowed: F1 to F24 on their own or with Ctrl,
/// Alt or Shift, except F1 on its own (Help), F10 and Shift+F10 (menus) and Alt+F4 (close); or a letter or digit with Ctrl
/// (Shift may be added), except Ctrl or Ctrl+Shift with A, C, S, V, X, Y or Z (editing), and with Ctrl+Alt only when Shift is
/// added too, because Ctrl+Alt is AltGr on many keyboards. The Windows key is never allowed. A program may still use an allowed
/// combination for itself. Saved and shown as text such as "Ctrl+Alt+Shift+P".
/// Instances exist only through <see cref="TryParse"/> and <see cref="Default"/>, so every instance is allowed.
/// </summary>
public sealed record PanicShortcut
{
    private const int MaximumTextLength = 40, F1 = 0x70, F4 = 0x73, F10 = 0x79;
    private const string Example = "such as F12 or Ctrl+Alt+Shift+P";

    private PanicShortcut(PanicShortcutModifiers modifiers, int virtualKey) { Modifiers = modifiers; VirtualKey = virtualKey; }

    public PanicShortcutModifiers Modifiers { get; }
    /// <summary>The Windows virtual-key code: 0x70-0x87 for F1-F24, 0x41-0x5A for A-Z, 0x30-0x39 for 0-9.</summary>
    public int VirtualKey { get; }

    public static PanicShortcut Default { get; } = new(PanicShortcutModifiers.None, F1 + 11);

    /// <summary>
    /// Reads text such as "F12", "ctrl+alt+shift+p" or "Shift + F9". Ctrl (or Control), Alt and Shift may come in any order
    /// before exactly one key. On failure <paramref name="problem"/> says plainly what to change; it never repeats the text.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out PanicShortcut? shortcut, out string problem)
    {
        shortcut = null;
        if (!TryRead(text, out var modifiers, out int key, out problem)) return false;
        if (Reserved(modifiers, key) is { } reserved) { problem = reserved; return false; }
        shortcut = new(modifiers, key); return true;
    }

    /// <summary>
    /// True when <paramref name="text"/> is a combination earlier versions accepted and saved but that <see cref="TryParse"/> now
    /// refuses, such as Ctrl+Alt+Q (AltGr+Q types @ on German keyboards) or Ctrl+C. Saved settings holding one load F12 instead
    /// of being treated as damaged. False for allowed text and for text that was never allowed.
    /// </summary>
    public static bool IsNoLongerAllowed(string? text) => TryRead(text, out var modifiers, out int key, out _) && Reserved(modifiers, key) is not null;

    /// <summary>The canonical text, with modifiers in the order Ctrl, Alt, Shift: "Ctrl+Alt+Shift+F9".</summary>
    public override string ToString()
    {
        string key = VirtualKey >= F1 ? "F" + (VirtualKey - F1 + 1) : ((char)VirtualKey).ToString();
        return (Modifiers.HasFlag(PanicShortcutModifiers.Control) ? "Ctrl+" : "")
            + (Modifiers.HasFlag(PanicShortcutModifiers.Alt) ? "Alt+" : "")
            + (Modifiers.HasFlag(PanicShortcutModifiers.Shift) ? "Shift+" : "") + key;
    }

    private const string OneKey = "The panic shortcut needs exactly one key after Ctrl, Alt or Shift, " + Example + ".";

    // The syntax and the rules every version has applied. A combination that passes here but is Reserved is one an earlier
    // version saved.
    private static bool TryRead(string? text, out PanicShortcutModifiers modifiers, out int key, out string problem)
    {
        modifiers = PanicShortcutModifiers.None; key = 0;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength) { problem = $"Enter a panic shortcut, {Example}."; return false; }
        foreach (string raw in text.Split('+'))
        {
            string part = raw.Trim();
            if (part.Length == 0) { problem = $"Write the panic shortcut as keys joined by +, {Example}."; return false; }
            if (Modifier(part) is { } modifier)
            {
                if (key != 0) { problem = OneKey; return false; }
                modifiers |= modifier; continue;
            }
            if (IsWindowsKey(part)) { problem = "The panic shortcut cannot use the Windows key. Use Ctrl, Alt or Shift instead."; return false; }
            if (key != 0) { problem = OneKey; return false; }
            key = Key(part);
            if (key == 0) { problem = "The panic shortcut key must be F1 to F24, a letter or a digit."; return false; }
        }
        if (key == 0) { problem = OneKey; return false; }
        if (key < F1 && (modifiers & (PanicShortcutModifiers.Control | PanicShortcutModifiers.Alt)) == 0)
        { problem = "A letter or digit in the panic shortcut needs Ctrl, so ordinary typing cannot stop the manager."; return false; }
        if (key == F4 && modifiers == PanicShortcutModifiers.Alt)
        { problem = "Alt+F4 closes windows in every program, so it cannot be the panic shortcut."; return false; }
        problem = ""; return true;
    }

    // Combinations that type characters or run common commands in other programs, or null when the combination is allowed.
    private static string? Reserved(PanicShortcutModifiers modifiers, int key)
    {
        bool ctrl = modifiers.HasFlag(PanicShortcutModifiers.Control), alt = modifiers.HasFlag(PanicShortcutModifiers.Alt), shift = modifiers.HasFlag(PanicShortcutModifiers.Shift);
        if (key >= F1)
        {
            if (key == F1 && modifiers == PanicShortcutModifiers.None)
                return "F1 on its own opens Help in most programs, so it cannot be the panic shortcut. Add Ctrl, Alt or Shift, or choose another F-key.";
            if (key == F10 && !ctrl && !alt)
                return "F10 and Shift+F10 open the menu bar and the shortcut menu in most programs, so they cannot be the panic shortcut. Add Ctrl or Alt, or choose another F-key.";
            return null;
        }
        if (!ctrl)
            return "Alt with a letter or digit opens menus in most programs, so a letter or digit in the panic shortcut needs Ctrl, such as Ctrl+Alt+Shift+P.";
        if (alt && !shift)
            return "Ctrl+Alt with a letter or digit is the AltGr key on many keyboards, where it types characters such as @ or {. Add Shift, such as Ctrl+Alt+Shift+P.";
        if (!alt && key is 'A' or 'C' or 'S' or 'V' or 'X' or 'Y' or 'Z')
            return "Ctrl or Ctrl+Shift with A, C, S, V, X, Y or Z copies, pastes, cuts, undoes, redoes, saves or selects in most programs, so it cannot be the panic shortcut. Choose another letter or add Alt.";
        return null;
    }

    private static PanicShortcutModifiers? Modifier(string part) =>
        Is(part, "Ctrl") || Is(part, "Control") ? PanicShortcutModifiers.Control
        : Is(part, "Alt") ? PanicShortcutModifiers.Alt
        : Is(part, "Shift") ? PanicShortcutModifiers.Shift
        : null;

    private static bool IsWindowsKey(string part) => Is(part, "Win") || Is(part, "Windows") || Is(part, "Meta") || Is(part, "Super");

    // 0 when the part is not an allowed key.
    private static int Key(string part)
    {
        if (part.Length == 1)
            return char.IsAsciiLetter(part[0]) ? char.ToUpperInvariant(part[0]) : char.IsAsciiDigit(part[0]) ? part[0] : 0;
        if (part.Length <= 3 && (part[0] is 'F' or 'f') && part[1] is >= '1' and <= '9'
            && int.TryParse(part.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number)
            && number is >= 1 and <= 24) return F1 + number - 1;
        return 0;
    }

    private static bool Is(string part, string name) => string.Equals(part, name, StringComparison.OrdinalIgnoreCase);
}
