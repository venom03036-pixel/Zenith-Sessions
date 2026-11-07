using System.Collections.ObjectModel;
using System.Windows.Media;

namespace MacroSuite.App.Themes;

/// <summary>A complete, opaque desktop palette. Semantic action and status colors stay independent of the accent.</summary>
public sealed class ThemePreset
{
    internal ThemePreset(string id, string name, string description, Dictionary<string, Color> colors)
    {
        Id = id;
        Name = name;
        Description = description;
        Colors = new ReadOnlyDictionary<string, Color>(colors);
        PreviewBackground = Brush(colors["CardBackground"]);
        PreviewAccent = Brush(colors["AccentPrimary"]);
        PreviewForeground = Brush(colors["TextPrimary"]);
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public IReadOnlyDictionary<string, Color> Colors { get; }
    public SolidColorBrush PreviewBackground { get; }
    public SolidColorBrush PreviewAccent { get; }
    public SolidColorBrush PreviewForeground { get; }

    internal static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

public static class ThemeCatalog
{
    public const string DefaultId = "graphite-mint";

    public static IReadOnlyList<ThemePreset> Presets { get; } = Array.AsReadOnly(new[]
    {
        Create(DefaultId, "Graphite Mint", "Soft graphite panels with a restrained mint accent.",
            "#151719", "#1C1F23", "#23272D", "#EDF1F5", "#B8C1CC", "#A3ADB8", "#83D8C1", "#0E2421", "#7B8793"),
        Create("midnight", "Midnight", "The original near-black appearance with vivid green accents.",
            "#09090B", "#101013", "#18181C", "#F1F1F5", "#BFC0CC", "#A6A7B4", "#4ADEA3", "#0C231B", "#777783"),
        Create("amber-night", "Amber Night", "Yellow-orange controls on warm black surfaces.",
            "#100D08", "#1B1711", "#282119", "#FFF3DF", "#D8C6A7", "#C3B291", "#FFB547", "#2A1700", "#93836A"),
        Create("ocean-blue", "Ocean Blue", "Deep navy panels and clear blue controls.",
            "#101823", "#182330", "#202E3E", "#EBF4FF", "#B4C6DB", "#A3BAD2", "#7EBBFF", "#0D213C", "#8398AF"),
        Create("violet-dusk", "Violet Dusk", "Muted plum surfaces with soft violet highlights.",
            "#19151F", "#221D2B", "#2D2638", "#F6EEFF", "#CCBEDC", "#BCAACC", "#C4A1FF", "#21122E", "#9585A9")
    });

    public static ThemePreset Resolve(string? id) =>
        Presets.FirstOrDefault(preset => string.Equals(preset.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? Presets[0];

    private static ThemePreset Create(string id, string name, string description,
        string canvas, string panel, string field, string text, string secondary, string muted,
        string accent, string onAccent, string controlBorder)
    {
        Color app = Parse(canvas), card = Parse(panel), surface = Parse(field), brand = Parse(accent);
        var colors = new Dictionary<string, Color>(StringComparer.Ordinal)
        {
            ["AppBackground"] = app,
            ["CardBackground"] = card,
            ["SurfaceBackground"] = surface,
            ["HeaderBackground"] = card,
            ["WorkspaceMaterial"] = app,
            ["PanelMaterial"] = card,
            ["HeaderMaterial"] = card,
            ["HoverBackground"] = Mix(surface, Colors.White, .055),
            ["ActiveBackground"] = Mix(surface, Colors.White, .08),
            ["RowAlternateBackground"] = Mix(app, card, .55),
            ["BorderBrushLight"] = Mix(card, Colors.White, .13),
            ["BorderBrushDark"] = Parse(controlBorder),
            ["ControlBorder"] = Parse(controlBorder),
            ["BorderBrushFocus"] = brand,
            ["BorderBrushGlow"] = brand,
            ["TextPrimary"] = Parse(text),
            ["TextSecondary"] = Parse(secondary),
            ["TextMuted"] = Parse(muted),
            ["TextHighlight"] = Parse(text),
            ["DisabledForeground"] = Parse(muted),
            ["DisabledBackground"] = surface,
            ["AccentMint"] = brand,
            ["AccentPrimary"] = brand,
            ["AccentCyan"] = Parse("#73DCCB"),
            ["AccentTeal"] = Parse("#73DCCB"),
            ["AccentSuccess"] = Parse("#6BDD9C"),
            ["AccentWarning"] = Parse("#FBC65D"),
            ["AccentDanger"] = Parse("#FF929C"),
            ["AccentMauve"] = Parse("#CCA8FA"),
            ["AccentSky"] = Parse("#80C7FF"),
            ["AccentGold"] = Parse("#FBC65D"),
            ["BadgeBgTeal"] = Parse("#193D36"),
            ["BadgeBgIndigo"] = Parse("#28243C"),
            ["BadgeBgSuccess"] = Parse("#153C29"),
            ["BadgeBgWarning"] = Parse("#3A2B12"),
            ["BadgeBgDanger"] = Parse("#421D27"),
            ["KeyPillBackground"] = surface,
            ["BrandGradient"] = brand,
            ["SuccessGradient"] = Parse("#6BDD9C"),
            ["DangerGradient"] = Parse("#B4233B"),
            ["PrimaryForeground"] = Parse(onAccent),
            ["PrimaryHoverBackground"] = Mix(brand, Colors.White, .12),
            ["PrimaryPressedBackground"] = Mix(brand, Colors.Black, .12),
            ["DangerForeground"] = Colors.White,
            ["DangerHoverBackground"] = Parse("#C82C46"),
            ["DangerPressedBackground"] = Parse("#981D33"),
            ["SelectionBackground"] = Mix(app, brand, .18),
            ["SelectionForeground"] = Parse(text),
            ["SelectionBorder"] = brand,
            ["ActionMouse"] = Parse("#80C7FF"),
            ["ActionKeyboard"] = Parse("#FBC65D"),
            ["ActionWait"] = Parse("#BAC5D4"),
            ["ActionFlow"] = Parse("#6BDD9C"),
            ["ActionImage"] = Parse("#CCA8FA"),
            ["ActionAI"] = Parse("#CCA8FA"),
            ["ActionColor"] = Parse("#73DCCB")
        };
        return new ThemePreset(id, name, description, colors);
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));
}
