using System.Windows.Media;

namespace MacroSuite.App.Themes;

public sealed record ResolvedAppearance(ThemePreset Palette, double EffectivePanelOpacity, string? Notice);

public static class AppearancePalette
{
    public static bool TryParse(string? hex) => hex is { Length: 7 } && hex[0] == '#'
        && uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out _);
    public static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    public static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    public static double Luminance(Color c)
    {
        static double Channel(byte b) { double v = b / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        return .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
    }
    public static double Contrast(Color a, Color b) => (Math.Max(Luminance(a), Luminance(b)) + .05) / (Math.Min(Luminance(a), Luminance(b)) + .05);
    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    public static ResolvedAppearance Resolve(AppearanceProfile profile)
    {
        Color canvas = Parse(profile.Canvas), panel = Parse(profile.Panel);
        bool light = Luminance(panel) > .35;
        Color contrastAnchor = light ? Colors.Black : Colors.White;
        Color surfaceAnchor = light ? Colors.White : Colors.Black;
        // Middle-luminance surfaces cannot provide enough room for readable text and controls.
        while (light ? Luminance(panel) < .72 : Luminance(panel) > .10) panel = Mix(panel, surfaceAnchor, .05);
        // Canvas and panels share a luminance family so every page can use one readable text hierarchy.
        if ((Luminance(canvas) > .35) != light) canvas = Mix(panel, canvas, .12);
        while (light ? Luminance(canvas) < .72 : Luminance(canvas) > .10) canvas = Mix(canvas, surfaceAnchor, .05);
        Color surface = Mix(panel, contrastAnchor, light ? .04 : .05);
        Color hover = Mix(surface, contrastAnchor, .035), active = Mix(surface, contrastAnchor, .06);
        Color[] surfaces = [canvas, panel, surface, hover, active];
        Color requestedText = Parse(profile.Text);
        Color text = profile.AutomaticText ? contrastAnchor : Ensure(requestedText, surfaces, 7, contrastAnchor);
        Color secondary = Ensure(Mix(text, panel, .2), surfaces, 5.2, contrastAnchor);
        Color muted = Ensure(Mix(text, panel, .28), surfaces, 4.8, contrastAnchor);
        Color accent = Ensure(Parse(profile.Accent), surfaces, 4.5, contrastAnchor);
        Color button = Parse(profile.Button);
        Color onButton = Contrast(Colors.Black, button) >= Contrast(Colors.White, button) ? Colors.Black : Colors.White;
        Color hoverButton = Mix(button, onButton == Colors.Black ? Colors.White : Colors.Black, .1);
        Color pressedButton = Mix(button, onButton == Colors.Black ? Colors.White : Colors.Black, .18);
        Color selection = Mix(panel, accent, .13);
        if (Contrast(text, selection) < 4.5) selection = panel;
        var colors = new Dictionary<string, Color>(ThemeCatalog.Resolve(profile.BaseThemeId).Colors, StringComparer.Ordinal)
        {
            ["AppBackground"] = canvas, ["CardBackground"] = panel, ["HeaderBackground"] = panel,
            ["SurfaceBackground"] = surface, ["HoverBackground"] = hover, ["ActiveBackground"] = active,
            ["RowAlternateBackground"] = Mix(canvas, panel, .55), ["KeyPillBackground"] = surface,
            ["TextPrimary"] = text, ["TextHighlight"] = text, ["TextSecondary"] = secondary, ["TextMuted"] = muted,
            ["DisabledForeground"] = muted, ["DisabledBackground"] = surface,
            ["AccentPrimary"] = button, ["AccentMint"] = accent, ["BrandGradient"] = button,
            ["PrimaryForeground"] = onButton, ["PrimaryHoverBackground"] = hoverButton, ["PrimaryPressedBackground"] = pressedButton,
            ["BorderBrushFocus"] = accent, ["BorderBrushGlow"] = accent, ["SelectionBorder"] = accent,
            ["SelectionBackground"] = selection, ["SelectionForeground"] = text,
            ["ControlBorder"] = Ensure(Mix(panel, contrastAnchor, .55), surfaces, 3, contrastAnchor),
            ["BorderBrushDark"] = Ensure(Mix(panel, contrastAnchor, .55), surfaces, 3, contrastAnchor),
            ["BorderBrushLight"] = Mix(panel, contrastAnchor, .18),
            ["WorkspaceMaterial"] = canvas, ["PanelMaterial"] = panel, ["HeaderMaterial"] = panel
        };
        // Preserve semantic hues, adjusting only luminance for light surfaces.
        foreach (string key in new[] { "AccentSuccess", "AccentWarning", "AccentDanger", "AccentCyan", "AccentTeal", "AccentMauve", "AccentSky", "AccentGold", "ActionMouse", "ActionKeyboard", "ActionWait", "ActionFlow", "ActionImage", "ActionAI", "ActionColor" })
            colors[key] = Ensure(colors[key], surfaces.Append(selection).ToArray(), 4.5, contrastAnchor);
        if (light)
        {
            foreach (string key in new[] { "BadgeBgTeal", "BadgeBgIndigo", "BadgeBgSuccess", "BadgeBgWarning", "BadgeBgDanger" })
                colors[key] = Mix(panel, Colors.White, .8);
        }
        double opacity = profile.BackgroundKind == AppearanceBackgroundKind.Solid ? 1 : Math.Clamp(profile.PanelOpacity, 0, 1);
        // Both black and white bound the possible wallpaper/desktop luminance beneath a panel.
        Color[] materialText = [text, secondary, muted, accent, colors["AccentSuccess"], colors["AccentWarning"], colors["AccentDanger"],
            colors["AccentCyan"], colors["AccentTeal"], colors["AccentMauve"], colors["AccentSky"], colors["AccentGold"],
            colors["ActionMouse"], colors["ActionKeyboard"], colors["ActionWait"], colors["ActionFlow"], colors["ActionImage"], colors["ActionAI"], colors["ActionColor"]];
        while (opacity < 1 && new[] { canvas, panel }.Any(p => materialText.Any(f =>
                   Contrast(f, Mix(Colors.White, p, opacity)) < 4.5 || Contrast(f, Mix(Colors.Black, p, opacity)) < 4.5)))
            opacity = Math.Min(1, opacity + .01);
        string? notice = opacity > profile.PanelOpacity + .011 && profile.BackgroundKind != AppearanceBackgroundKind.Solid
            ? $"Panel opacity is protected at {opacity:P0} to keep text readable." : null;
        return new(new ThemePreset(profile.Id, profile.Name, "Custom appearance", colors), opacity, notice);
    }

    /// <summary>Moves <paramref name="color"/> toward <paramref name="destination"/> until it reaches <paramref name="ratio"/> on every background.</summary>
    internal static Color Ensure(Color color, IReadOnlyList<Color> backgrounds, double ratio, Color destination)
    {
        if (backgrounds.All(b => Contrast(color, b) >= ratio)) return color;
        for (int n = 1; n <= 100; n++)
        {
            Color candidate = Mix(color, destination, n / 100d);
            if (backgrounds.All(b => Contrast(candidate, b) >= ratio)) return candidate;
        }
        return destination;
    }
}
