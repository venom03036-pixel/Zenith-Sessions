using System.Runtime.InteropServices;
using System.Windows.Input;

namespace MacroSuite.App.Common;

/// <summary>
/// Single source of truth for hotkey text ↔ virtual key translation.
/// Any key on the keyboard is bindable: function keys, letters, digits, punctuation,
/// navigation keys, numpad keys, and any of the above with Ctrl / Alt / Shift / Win.
/// </summary>
public static class HotkeyCodec
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char ch);

    /// <summary>Human-readable hotkey text for a key event, or null for a bare modifier key.</summary>
    public static string? FromKeyEvent(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return null;

        var parts = new List<string>(4);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    public static string KeyName(Key key) => key switch
    {
        >= Key.F1 and <= Key.F24 => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (int)(key - Key.NumPad0),
        Key.Escape => "ESC",
        Key.Return => "Enter",
        Key.Space => "Space",
        Key.Decimal => "NumDecimal",
        Key.Add => "NumAdd",
        Key.Subtract => "NumSubtract",
        Key.Divide => "NumDivide",
        Key.Multiply => "NumMultiply",
        Key.Separator => "NumSeparator",
        Key.OemPeriod => ".",
        Key.OemComma => ",",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemQuestion => "/",
        Key.Oem1 => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe => "\\",
        Key.OemTilde => "`",
        _ => key.ToString()
    };

    /// <summary>Virtual key for the key portion of a hotkey string, or 0 when unparseable.</summary>
    public static uint VirtualKeyOf(string? hotkey)
        => TryParse(hotkey, out _, out uint vk) ? vk : 0;

    /// <summary>Rebuilds a hotkey string from the same modifiers / VK used by RegisterHotKey.</summary>
    public static string Format(uint modifiers, uint vk)
    {
        var parts = new List<string>(4);
        if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModWin) != 0) parts.Add("Win");

        var key = KeyInterop.KeyFromVirtualKey((int)vk);
        parts.Add(key != Key.None ? KeyName(key) : $"0x{vk:X2}");
        return string.Join("+", parts);
    }

    /// <summary>Case-insensitive, order-insensitive comparison of two hotkey strings.</summary>
    public static bool AreEqual(string? a, string? b)
    {
        if (!TryParse(a, out uint modA, out uint vkA)) return false;
        if (!TryParse(b, out uint modB, out uint vkB)) return false;
        return modA == modB && vkA == vkB;
    }

    public static bool TryParse(string? hotkeyStr, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(hotkeyStr)) return false;

        var parts = hotkeyStr.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            var mod = parts[i].ToLowerInvariant();
            if (mod is "ctrl" or "control") modifiers |= ModControl;
            else if (mod == "alt") modifiers |= ModAlt;
            else if (mod == "shift") modifiers |= ModShift;
            else if (mod == "win") modifiers |= ModWin;
            else return false;
        }

        var keyPart = parts[^1].ToUpperInvariant();
        if (keyPart.StartsWith('F') && int.TryParse(keyPart[1..], out int fNum) && fNum is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + (fNum - 1));
            return true;
        }

        if (keyPart.StartsWith("NUM") && keyPart.Length == 4 && char.IsAsciiDigit(keyPart[3]))
        {
            vk = (uint)(0x60 + (keyPart[3] - '0'));
            return true;
        }

        if (keyPart.Length == 1)
        {
            char c = keyPart[0];
            if (c is >= 'A' and <= 'Z' || c is >= '0' and <= '9')
            {
                vk = c;
                return true;
            }

            switch (c)
            {
                case '.': vk = 0xBE; return true; // VK_OEM_PERIOD
                case ',': vk = 0xBC; return true; // VK_OEM_COMMA
                case '-': vk = 0xBD; return true; // VK_OEM_MINUS
                case '=': vk = 0xBB; return true; // VK_OEM_PLUS
                case '/': vk = 0xBF; return true; // VK_OEM_2
                case ';': vk = 0xBA; return true; // VK_OEM_1
                case '\'': vk = 0xDE; return true; // VK_OEM_7
                case '[': vk = 0xDB; return true; // VK_OEM_4
                case ']': vk = 0xDD; return true; // VK_OEM_6
                case '\\': vk = 0xDC; return true; // VK_OEM_5
                case '`': vk = 0xC0; return true; // VK_OEM_3
                case '*': vk = 0x6A; return true; // VK_MULTIPLY
                case '+': vk = 0x6B; return true; // VK_ADD
            }

            short scan = VkKeyScanW(c);
            if (scan != -1)
            {
                vk = (uint)(scan & 0xFF);
                return true;
            }
        }

        switch (keyPart)
        {
            case "ESC" or "ESCAPE": vk = 0x1B; return true;
            case "SPACE": vk = 0x20; return true;
            case "ENTER" or "RETURN": vk = 0x0D; return true;
            case "TAB": vk = 0x09; return true;
            case "BACKSPACE" or "BACK": vk = 0x08; return true;
            case "INSERT" or "INS": vk = 0x2D; return true;
            case "DELETE" or "DEL": vk = 0x2E; return true;
            case "HOME": vk = 0x24; return true;
            case "END": vk = 0x23; return true;
            case "PAGEUP" or "PGUP": vk = 0x21; return true;
            case "PAGEDOWN" or "PGDN": vk = 0x22; return true;
            case "UP": vk = 0x26; return true;
            case "DOWN": vk = 0x28; return true;
            case "LEFT": vk = 0x25; return true;
            case "RIGHT": vk = 0x27; return true;
            case "CAPS" or "CAPSLOCK": vk = 0x14; return true;
            case "PERIOD" or "OEMPERIOD" or "DOT": vk = 0xBE; return true;
            case "COMMA" or "OEMCOMMA": vk = 0xBC; return true;
            case "NUMMULTIPLY": vk = 0x6A; return true;
            case "NUMADD": vk = 0x6B; return true;
            case "NUMSEPARATOR": vk = 0x6C; return true;
            case "NUMSUBTRACT": vk = 0x6D; return true;
            case "NUMDECIMAL": vk = 0x6E; return true;
            case "NUMDIVIDE": vk = 0x6F; return true;
        }

        if (Enum.TryParse<Key>(keyPart, true, out var parsedWpfKey))
        {
            int mappedVk = KeyInterop.VirtualKeyFromKey(parsedWpfKey);
            if (mappedVk > 0)
            {
                vk = (uint)mappedVk;
                return true;
            }
        }

        return false;
    }
}
