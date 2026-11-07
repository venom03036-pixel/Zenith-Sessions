using System.IO;
using System.Text.Json;
using MacroSuite.App.Common;

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

/// <summary>Cosmetic data is isolated from transport, credentials, and safety configuration.</summary>
public sealed class AppearanceStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public AppearanceStore(string appDataRoot) => _path = Path.Combine(appDataRoot, "Config", "appearance.json");

    public AppearanceDocument Load(out string? message)
    {
        message = null;
        try
        {
            var loaded = SettingsJsonStore.Load(_path, ValidateDocument);
            message = loaded.Kind == SettingsLoadKind.Missing ? null : loaded.Message;
            using var json = loaded.Document;
            if (json is null) return new();
            var document = JsonSerializer.Deserialize<AppearanceDocument>(json.RootElement.GetRawText(), Options)
                ?? throw new InvalidDataException("The appearance file is empty.");
            if (document.Version != 1) throw new InvalidDataException("This appearance format is not supported.");
            var themes = (document.CustomThemes ?? []).Where(IsValid).Take(64).DistinctBy(p => p.Id).ToList();
            return document with { CustomThemes = themes, ActiveCustomId = themes.Any(p => p.Id == document.ActiveCustomId) ? document.ActiveCustomId : null };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            message = $"Custom appearance could not be loaded. The base theme is still available. {ex.Message}";
            return new();
        }
    }

    public void Save(AppearanceDocument document)
    {
        if (document.CustomThemes.Count > 64 || document.CustomThemes.Any(p => !IsValid(p)))
            throw new InvalidDataException("A custom theme contains invalid values.");
        SettingsJsonStore.SaveAtomic(_path, JsonSerializer.Serialize(document, Options), ValidateDocument);
    }

    private static bool ValidateDocument(JsonElement root)
    {
        try { var document = JsonSerializer.Deserialize<AppearanceDocument>(root.GetRawText(), Options); return document is { Version: 1 } && document.CustomThemes is { Count: <= 64 } && document.CustomThemes.All(IsValid); }
        catch (JsonException) { return false; }
    }
    private static bool IsValid(AppearanceProfile p) => p is not null
        && Guid.TryParseExact(p.Id, "N", out _) && !string.IsNullOrWhiteSpace(p.Name) && p.Name.Length <= 80
        && new[] { p.Canvas, p.Panel, p.Accent, p.Button, p.Text }.All(AppearancePalette.TryParse)
        && double.IsFinite(p.PanelOpacity) && p.PanelOpacity is >= 0 and <= 1
        && double.IsFinite(p.Dim) && p.Dim is >= 0 and <= 1
        && double.IsFinite(p.BlurRadius) && p.BlurRadius is >= 0 and <= 24
        && Enum.IsDefined(p.BackgroundKind)
        && (p.AssetId is null || AppearanceAssetService.IsValidAssetId(p.AssetId));
}
