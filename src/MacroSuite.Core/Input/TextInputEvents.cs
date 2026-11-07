using MacroSuite.Core.Models;

namespace MacroSuite.Core.Input;

/// <summary>
/// Converts a Unicode string into playback events. Newline and tab use virtual keys;
/// remaining UTF-16 code units use <see cref="InputEventType.Unicode"/>.
/// </summary>
public static class TextInputEvents
{
    public const int VkTab = 0x09;
    public const int VkReturn = 0x0D;

    public static InputEvent[] FromText(string text, long startTimestampTicks = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
            return [];

        var events = new List<InputEvent>(text.Length * 2);
        long t = startTimestampTicks;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    continue;
                AddVirtualKey(events, VkReturn, ref t);
                continue;
            }

            if (ch == '\n')
            {
                AddVirtualKey(events, VkReturn, ref t);
                continue;
            }

            if (ch == '\t')
            {
                AddVirtualKey(events, VkTab, ref t);
                continue;
            }

            events.Add(new InputEvent(InputEventType.Unicode, t++, 0, 0, ch));
            events.Add(new InputEvent(InputEventType.UnicodeUp, t++, 0, 0, ch));
        }

        return events.ToArray();
    }

    private static void AddVirtualKey(List<InputEvent> events, int vk, ref long t)
    {
        events.Add(new InputEvent(InputEventType.KeyDown, t++, 0, 0, vk));
        events.Add(new InputEvent(InputEventType.KeyUp, t++, 0, 0, vk));
    }
}
