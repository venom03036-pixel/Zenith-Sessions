using System.Globalization;

namespace MacroSuite.Core.Vision;

/// <summary>
/// Extra palette colors for OR matching. Primary RGB stays on the condition; extras are optional.
/// </summary>
public static class ColorPalette
{
    public static PixelColor[] Combine(PixelColor primary, IReadOnlyList<PixelColor>? additional)
    {
        if (additional is null || additional.Count == 0)
            return [primary];

        var list = new List<PixelColor>(additional.Count + 1) { primary };
        for (int i = 0; i < additional.Count; i++)
        {
            PixelColor color = additional[i];
            bool duplicate = false;
            for (int j = 0; j < list.Count; j++)
            {
                if (list[j].R == color.R && list[j].G == color.G && list[j].B == color.B)
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
                list.Add(color);
        }

        return list.ToArray();
    }

    public static string FormatExtras(IReadOnlyList<PixelColor>? extras)
    {
        if (extras is null || extras.Count == 0)
            return string.Empty;

        var parts = new string[extras.Count];
        for (int i = 0; i < extras.Count; i++)
        {
            PixelColor c = extras[i];
            parts[i] = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        return string.Join(",", parts);
    }

    public static IReadOnlyList<PixelColor> ParseExtras(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<PixelColor>();

        string[] tokens = text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var colors = new List<PixelColor>(tokens.Length);
        foreach (string token in tokens)
        {
            if (!TryParseHex(token, out PixelColor color))
                throw new FormatException($"'{token}' is not a hex color. Use #RRGGBB.");
            colors.Add(color);
        }

        return colors;
    }

    public static bool TryParseHex(string token, out PixelColor color)
    {
        color = default;
        string hex = token.Trim();
        if (hex.StartsWith('#') || hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hex = hex[0] == '#' ? hex[1..] : hex[2..];
        if (hex.Length != 6)
            return false;
        if (!byte.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r))
            return false;
        if (!byte.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g))
            return false;
        if (!byte.TryParse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            return false;
        color = new PixelColor(r, g, b);
        return true;
    }
}
