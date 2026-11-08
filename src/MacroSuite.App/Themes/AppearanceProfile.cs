namespace MacroSuite.App.Themes;

public enum AppearanceBackgroundKind { Solid, Image, DesktopAcrylic }

public sealed record AppearanceProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "My theme";
    public string BaseThemeId { get; init; } = ThemeCatalog.DefaultId;
    public string Canvas { get; init; } = "#151719";
    public string Panel { get; init; } = "#1C1F23";
    public string Accent { get; init; } = "#83D8C1";
    public string Button { get; init; } = "#83D8C1";
    public string Text { get; init; } = "#EDF1F5";
    public bool AutomaticText { get; init; } = true;
    public bool ColorsFromImage { get; init; } = true;
    public AppearanceBackgroundKind BackgroundKind { get; init; }
    public string? AssetId { get; init; }
    public double PanelOpacity { get; init; } = .9;
    public double BlurRadius { get; init; } = 8;
    public double Dim { get; init; } = .25;
    public bool Animate { get; init; } = true;

    public static AppearanceProfile FromPreset(ThemePreset preset) => new()
    {
        Name = preset.Name + " custom", BaseThemeId = preset.Id,
        Canvas = AppearancePalette.Hex(preset.Colors["AppBackground"]),
        Panel = AppearancePalette.Hex(preset.Colors["CardBackground"]),
        Accent = AppearancePalette.Hex(preset.Colors["AccentPrimary"]),
        Button = AppearancePalette.Hex(preset.Colors["AccentPrimary"]),
        Text = AppearancePalette.Hex(preset.Colors["TextPrimary"])
    };
}

public sealed record AppearanceDocument
{
    public int Version { get; init; } = 1;
    public string? ActiveCustomId { get; init; }
    public List<AppearanceProfile> CustomThemes { get; init; } = [];
}
