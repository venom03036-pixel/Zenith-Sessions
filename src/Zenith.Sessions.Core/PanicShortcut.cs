using System.Diagnostics.CodeAnalysis;

namespace Zenith.Sessions.Core;

/// <summary>Modifier keys a panic shortcut may use. The values are RegisterHotKey's MOD_ALT, MOD_CONTROL and MOD_SHIFT.</summary>
[Flags]
public enum PanicShortcutModifiers { None = 0, Alt = 1, Control = 2, Shift = 4 }

/// <summary>
/// The global panic stop shortcut, F12 unless the user saves another. Only combinations that cannot get in the way of ordinary
/// typing or of Windows itself are allowed: F1 to F24 on their own or with Ctrl, Alt or Shift, or a letter or digit with Ctrl
/// or Alt (Shift may be added). The Windows key and Alt+F4 are never allowed. Saved and shown as text such as "Ctrl+Alt+P".
/// Instances exist only through <see cref="TryParse"/> and <see cref="Default"/>, so every instance is allowed.
/// </summary>
public sealed record PanicShortcut
{
    private const int MaximumTextLength = 40, F1 = 0x70, F4 = 0x73;
    private const string Example = "such as F12 or Ctrl+Alt+P";

    private PanicShortcut(PanicShortcutModifiers modifiers, int virtualKey) { Modifiers = modifiers; VirtualKey = virtualKey; }

    public PanicShortcutModifiers Modifiers { get; }
    /// <summary>The Windows virtual-key code: 0x70-0x87 for F1-F24, 0x41-0x5A for A-Z, 0x30-0x39 for 0-9.</summary>
    public int VirtualKey { get; }

    public static PanicShortcut Default { get; } = new(PanicShortcutModifiers.None, F1 + 11);

    /// <summary>
    /// Reads text such as "F12", "ctrl+alt+p" or "Shift + F9". Ctrl (or Control), Alt and Shift may come in any order before
    /// exactly one key. On failure <paramref name="problem"/> says plainly what to change; it never repeats the text.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out PanicShortcut? shortcut, out string problem)
    {
        shortcut = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength) { problem = $"Enter a panic shortcut, {Example}."; return false; }
        var modifiers = PanicShortcutModifiers.None;
        int key = 0;
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
        bool functionKey = key >= F1;
        if (!functionKey && (modifiers & (PanicShortcutModifiers.Control | PanicShortcutModifiers.Alt)) == 0)
        { problem = "A letter or digit in the panic shortcut needs Ctrl or Alt, so ordinary typing cannot stop the manager."; return false; }
        if (key == F4 && modifiers == PanicShortcutModifiers.Alt)
        { problem = "Alt+F4 closes windows in every program, so it cannot be the panic shortcut."; return false; }
        shortcut = new(modifiers, key); problem = ""; return true;
    }

    /// <summary>The canonical text, with modifiers in the order Ctrl, Alt, Shift: "Ctrl+Alt+Shift+F9".</summary>
    public override string ToString()
    {
        string key = VirtualKey >= F1 ? "F" + (VirtualKey - F1 + 1) : ((char)VirtualKey).ToString();
        return (Modifiers.HasFlag(PanicShortcutModifiers.Control) ? "Ctrl+" : "")
            + (Modifiers.HasFlag(PanicShortcutModifiers.Alt) ? "Alt+" : "")
            + (Modifiers.HasFlag(PanicShortcutModifiers.Shift) ? "Shift+" : "") + key;
    }

    private const string OneKey = "The panic shortcut needs exactly one key after Ctrl, Alt or Shift, " + Example + ".";

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
