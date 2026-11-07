using System.Windows;

namespace MacroSuite.App.Themes;

/// <summary>Replaces a fixed set of brushes; DynamicResource consumers update without rebuilding views.</summary>
public static class ThemeManager
{
    public static void Apply(ResourceDictionary resources, string? id)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ThemePreset theme = ThemeCatalog.Resolve(id);
        Apply(resources, theme);
    }

    public static void Apply(ResourceDictionary resources, ThemePreset theme, double panelOpacity = 1)
    {
        ArgumentNullException.ThrowIfNull(resources);
        foreach (var pair in theme.Colors)
            resources[pair.Key] = ThemePreset.Brush(pair.Value);

        foreach (string key in new[] { "WorkspaceMaterial", "PanelMaterial", "HeaderMaterial" })
        {
            var color = theme.Colors[key];
            // Never round below the opacity that passed the contrast check.
            color.A = (byte)Math.Ceiling(Math.Clamp(panelOpacity, 0, 1) * 255);
            resources[key] = ThemePreset.Brush(color);
        }

        // Retain the legacy color keys for templates that consume Color rather than Brush.
        resources["ColorAppBackground"] = theme.Colors["AppBackground"];
        resources["ColorCardBackground"] = theme.Colors["CardBackground"];
        resources["ColorSurfaceBackground"] = theme.Colors["SurfaceBackground"];
        resources["ColorHeaderBackground"] = theme.Colors["HeaderBackground"];
    }
}
