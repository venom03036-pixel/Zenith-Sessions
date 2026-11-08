using System.Windows;
using System.Windows.Media;
using MacroSuite.App.Themes;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

/// <summary>The colours Windows uses while High Contrast is on (SystemColors). They are the only inputs of the High Contrast palette.</summary>
internal sealed record HighContrastColors(Color Window, Color WindowText, Color Highlight, Color HighlightText, Color GrayText)
{
    public static HighContrastColors FromSystem() => new(SystemColors.WindowColor, SystemColors.WindowTextColor,
        SystemColors.HighlightColor, SystemColors.HighlightTextColor, SystemColors.GrayTextColor);
}

/// <summary>
/// What Windows reports about colours right now. <see cref="AppsUseLightTheme"/> is null when Windows gave no usable answer, and
/// <see cref="ReadFailure"/> then names the error type when reading it failed. <see cref="HighContrast"/> is null while High Contrast is off.
/// </summary>
internal sealed record SystemAppearance(bool? AppsUseLightTheme, HighContrastColors? HighContrast, Type? ReadFailure = null)
{
    public static SystemAppearance Unknown { get; } = new(null, null);
    /// <summary>UI thread: one registry value read (never written) and the current SystemParameters and SystemColors.</summary>
    public static SystemAppearance Read()
    {
        var mode = WindowsAppMode.Read();
        return new(mode.AppsUseLightTheme, SystemParameters.HighContrast ? HighContrastColors.FromSystem() : null, mode.Failure);
    }
}

/// <summary>
/// The Settings theme choices: the shared dark presets, a Light theme generated with enforced contrast, and Follow Windows, which
/// shows Light while Windows apps use light mode and the default dark theme otherwise. While Windows High Contrast is on, the
/// High Contrast palette built only from the Windows colours replaces every choice; the saved choice is kept for when it is off.
/// </summary>
internal static class SessionsThemes
{
    public const string LightId = "light", FollowWindowsId = "follow-windows", HighContrastId = "high-contrast";
    private const double TextContrast = 4.5;
    private static readonly ThemePreset Standard = ThemeCatalog.Resolve(ThemeCatalog.DefaultId);
    // Static fields initialise in this order, so the Light seeds come before Light itself.
    private static readonly (string Key, string Hex)[] LightStatusColors =
    [
        ("AccentSuccess", "#1E7B4B"), ("ActionFlow", "#1E7B4B"), ("AccentWarning", "#8A5300"), ("AccentGold", "#8A5300"), ("ActionKeyboard", "#8A5300"),
        ("AccentDanger", "#B3261E"), ("AccentSky", "#1F63A8"), ("ActionMouse", "#1F63A8"), ("AccentMauve", "#6E4BB5"), ("ActionImage", "#6E4BB5"),
        ("ActionAI", "#6E4BB5"), ("AccentCyan", "#0F766E"), ("AccentTeal", "#0F766E"), ("ActionColor", "#0F766E"), ("ActionWait", "#5B6470"),
    ];

    public static ThemePreset Light { get; } = CreateLight();
    // Only the tile in Settings uses these colours; choosing it shows Light or the default theme (see Effective).
    public static ThemePreset FollowWindows { get; } = new(FollowWindowsId, "Follow Windows",
        "Light while Windows apps use light mode, Graphite Mint while they use dark mode.", new(Standard.Colors, StringComparer.Ordinal));
    public static IReadOnlyList<ThemePreset> Choices { get; } = Array.AsReadOnly([.. ThemeCatalog.Presets, Light, FollowWindows]);

    /// <summary>The id to save for <paramref name="id"/>: a known choice, or the default for anything else (an edited file, an older or newer id).</summary>
    public static string Normalize(string? id) => Choice(id).Id;
    public static ThemePreset Choice(string? id) =>
        Choices.FirstOrDefault(theme => string.Equals(theme.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Standard;
    /// <summary>The palette to show for the saved choice <paramref name="id"/> under what Windows reports.</summary>
    public static ThemePreset Effective(string? id, SystemAppearance system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (system.HighContrast is { } colors) return HighContrast(colors);
        var choice = Choice(id);
        return choice.Id != FollowWindowsId ? choice : system.AppsUseLightTheme == true ? Light : Standard;
    }

    /// <summary>
    /// Windows' own High Contrast colours, unchanged wherever they are readable. A colour that would not reach 4.5:1 on what it is
    /// drawn over is replaced by the window text colour, or by black or white, so a hand-made contrast theme cannot hide text.
    /// Status colours become plain text: the words on chips and banners carry the meaning, as High Contrast expects.
    /// Hovered and pressed primary and danger buttons take the window text colour when their label reads on it; rows and other
    /// buttons keep the window colour and <see cref="Apply"/> marks those states with an edge.
    /// </summary>
    public static ThemePreset HighContrast(HighContrastColors system)
    {
        ArgumentNullException.ThrowIfNull(system);
        Color window = system.Window, text = Readable(system.WindowText, window);
        Color accent = AppearancePalette.Contrast(system.Highlight, window) >= TextContrast ? system.Highlight : text;
        Color onAccent = Readable(accent == system.Highlight ? system.HighlightText : window, accent);
        Color selection = system.Highlight, onSelection = Readable(system.HighlightText, selection);
        Color disabled = AppearancePalette.Contrast(system.GrayText, window) >= 3 ? system.GrayText : text;
        // A hovered or pressed accent button needs a fill other than the accent that keeps its label readable. Only the window text
        // colour, or black or white, can be; when none is, the fill stays the accent.
        Color buttonState = new[] { text, Colors.Black, Colors.White }
            .FirstOrDefault(fill => fill != accent && AppearancePalette.Contrast(onAccent, fill) >= TextContrast, accent);
        // Every key starts as the window colour (all backgrounds, badges and materials), then foregrounds are assigned. That includes
        // HoverBackground and ActiveBackground: no other Windows colour keeps the window text readable as a fill.
        var colors = Standard.Colors.Keys.ToDictionary(key => key, _ => window, StringComparer.Ordinal);
        foreach (string key in new[] { "TextPrimary", "TextSecondary", "TextMuted", "TextHighlight", "BorderBrushLight", "BorderBrushDark", "ControlBorder",
                     "AccentCyan", "AccentTeal", "AccentSuccess", "AccentWarning", "AccentDanger", "AccentMauve", "AccentSky", "AccentGold",
                     "ActionMouse", "ActionKeyboard", "ActionWait", "ActionFlow", "ActionImage", "ActionAI", "ActionColor" })
            colors[key] = text;
        foreach (string key in new[] { "AccentPrimary", "AccentMint", "BrandGradient", "SuccessGradient", "BorderBrushFocus", "BorderBrushGlow", "SelectionBorder",
                     "DangerGradient" })
            colors[key] = accent;
        foreach (string key in new[] { "PrimaryHoverBackground", "PrimaryPressedBackground", "DangerHoverBackground", "DangerPressedBackground" })
            colors[key] = buttonState;
        colors["PrimaryForeground"] = colors["DangerForeground"] = onAccent;
        colors["SelectionBackground"] = selection; colors["SelectionForeground"] = onSelection;
        colors["DisabledForeground"] = disabled;
        return new(HighContrastId, "High Contrast", "The Windows High Contrast colours.", colors);
    }

    /// <summary>
    /// UI thread: shows <paramref name="palette"/> through <paramref name="resources"/>. Under High Contrast, hovered and pressed rows
    /// and buttons that take the theme's hover and press fills (in SessionsControls.xaml all but the primary, danger and row action
    /// buttons; a selected segment keeps its selection fill) keep the window colour behind their text and show the state as a bar of
    /// the window text colour along their leading edge, 3 pixels wide on hover and 6 while pressed. The bar stops short of what the
    /// control shows: the narrowest controls that take these fills, the 28 px icon buttons, keep 7.5 px before their centred glyph,
    /// and the secondary button 12 px of padding before its label (HighContrastHoverTests renders both pressed). Every other
    /// palette's hover and press colours are applied as they are. The window applies every live change (a theme chosen in Settings,
    /// High Contrast switched on or off) through this method; nothing else in the Sessions app calls ThemeManager.Apply except the
    /// packaged check's default-theme reset.
    /// </summary>
    public static void Apply(ResourceDictionary resources, ThemePreset palette)
    {
        ArgumentNullException.ThrowIfNull(resources); ArgumentNullException.ThrowIfNull(palette);
        ThemeManager.Apply(resources, palette);
        if (palette.Id != HighContrastId) return;
        resources["HoverBackground"] = Edge(palette.Colors["HoverBackground"], palette.Colors["TextPrimary"], 3);
        resources["ActiveBackground"] = Edge(palette.Colors["ActiveBackground"], palette.Colors["TextPrimary"], 6);
    }

    // Absolute mapping keeps the bar the same width on every control: the bar colour up to width, then the surface, which Pad
    // (the default spread) carries on to the far edge.
    private static LinearGradientBrush Edge(Color surface, Color bar, double width)
    {
        var stops = new GradientStopCollection { new(bar, 0), new(bar, .5), new(surface, .5), new(surface, 1) };
        var brush = new LinearGradientBrush(stops, new Point(0, 0), new Point(width * 2, 0)) { MappingMode = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }

    /// <summary>The palettes the packaged self-check applies: every choice as it can appear, and High Contrast from this computer's SystemColors.</summary>
    public static IReadOnlyList<ThemePreset> PackagedPalettes() =>
        [.. Choices.Where(theme => theme.Id != FollowWindowsId), HighContrast(HighContrastColors.FromSystem())];

    /// <summary>
    /// Null when <paramref name="palette"/> fills every key the window reads and keeps body, secondary and accent text at 4.5:1 on the
    /// page, card and field backgrounds, and button and selection text at 4.5:1 on their fills; otherwise the first problem found.
    /// </summary>
    public static string? ReadabilityProblem(ThemePreset palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (Standard.Colors.Keys.FirstOrDefault(key => !palette.Colors.ContainsKey(key)) is { } missing) return $"{missing} has no colour";
        foreach (string text in new[] { "TextPrimary", "TextSecondary", "AccentPrimary" })
            foreach (string background in new[] { "AppBackground", "CardBackground", "SurfaceBackground" })
                if (Unreadable(palette, text, background) is { } problem) return problem;
        return Unreadable(palette, "SelectionForeground", "SelectionBackground") ?? Unreadable(palette, "PrimaryForeground", "AccentPrimary")
            ?? Unreadable(palette, "DangerForeground", "DangerGradient");
    }

    private static string? Unreadable(ThemePreset palette, string foreground, string background)
    {
        double ratio = AppearancePalette.Contrast(palette.Colors[foreground], palette.Colors[background]);
        return ratio >= TextContrast ? null : $"{foreground} on {background} is {ratio:0.00}:1, below 4.5:1";
    }

    private static Color Readable(Color preferred, Color background) =>
        AppearancePalette.Contrast(preferred, background) >= TextContrast ? preferred
        // One of black and white always reaches at least 4.58:1 on any colour.
        : AppearancePalette.Contrast(Colors.Black, background) >= AppearancePalette.Contrast(Colors.White, background) ? Colors.Black : Colors.White;

    private static ThemePreset CreateLight()
    {
        // AppearancePalette darkens text, accents and every status colour until they reach their contrast on all light surfaces.
        var seed = new AppearanceProfile
        {
            BaseThemeId = ThemeCatalog.DefaultId, Canvas = "#EEF1F4", Panel = "#FBFCFD", Text = "#1A1D21", AutomaticText = false,
            Accent = "#0E8A70", Button = "#0E8A70", BackgroundKind = AppearanceBackgroundKind.Solid, PanelOpacity = 1
        };
        // The button colour is used as given, but AccentPrimary is also link text, chip text and borders here: give it the enforced accent.
        string accent = AppearancePalette.Hex(AppearancePalette.Resolve(seed).Palette.Colors["AccentMint"]);
        var colors = new Dictionary<string, Color>(AppearancePalette.Resolve(seed with { Button = accent }).Palette.Colors, StringComparer.Ordinal);
        // Darkening the dark presets' pastel status colours greys them out, so start from hues made for light surfaces.
        Color[] surfaces = [colors["AppBackground"], colors["CardBackground"], colors["SurfaceBackground"], colors["HoverBackground"],
            colors["ActiveBackground"], colors["SelectionBackground"]];
        foreach (var (key, hex) in LightStatusColors)
            colors[key] = AppearancePalette.Ensure(AppearancePalette.Parse(hex), surfaces, TextContrast, Colors.Black);
        // The presets' badge backgrounds are dark-only constants. Tint the light panel with each chip's own text colour instead.
        foreach (var (badge, text) in new[] { ("BadgeBgTeal", "AccentPrimary"), ("BadgeBgSuccess", "AccentSuccess"), ("BadgeBgWarning", "AccentWarning"),
                     ("BadgeBgDanger", "AccentDanger"), ("BadgeBgIndigo", "AccentMauve") })
            colors[badge] = Tint(colors["CardBackground"], colors[text], colors["TextPrimary"]);
        return new(LightId, "Light", "Light grey panels, dark text and a deep mint accent.", colors);
    }

    // The strongest tint up to 14% that keeps the chip text and body text (banners) at 4.5:1; the panel itself always does.
    private static Color Tint(Color panel, Color text, Color body)
    {
        for (int percent = 14; percent > 0; percent--)
        {
            Color candidate = AppearancePalette.Mix(panel, text, percent / 100d);
            if (AppearancePalette.Contrast(text, candidate) >= TextContrast && AppearancePalette.Contrast(body, candidate) >= TextContrast) return candidate;
        }
        return panel;
    }
}
