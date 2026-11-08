using MacroSuite.App.Themes;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

// Light, Follow Windows and Windows High Contrast. The window passes what Windows reports when it opens and again whenever
// Windows announces a colour, theme or accessibility change, so nothing here polls (Rule 14) or writes a Windows setting.
// Only the chosen theme id is saved; the palette shown also depends on Windows and is worked out each time.
public sealed partial class ShellViewModel
{
    private SystemAppearance _systemAppearance = SystemAppearance.Unknown;
    // Set once the saved theme has been applied: a Windows report that arrives before then must not replace the startup palette.
    private bool _themeApplied;
    internal ThemePreset EffectiveTheme => SessionsThemes.Effective(_settings.Theme, _systemAppearance);
    /// <summary>One sentence under the theme tiles: the theme in use and, when Windows decides it, why.</summary>
    public string ThemeStatus
    {
        get
        {
            if (_systemAppearance.HighContrast is not null)
                return "Windows High Contrast is on, so the manager uses your Windows contrast colours. Your theme choice returns when High Contrast is turned off.";
            var choice = SessionsThemes.Choice(_settings.Theme);
            if (choice.Id != SessionsThemes.FollowWindowsId) return $"Current theme: {choice.Name}.";
            return _systemAppearance.AppsUseLightTheme switch
            {
                true => "Following Windows: Light, because Windows apps use light mode.",
                false => $"Following Windows: {EffectiveTheme.Name}, because Windows apps use dark mode.",
                null => $"Following Windows: {EffectiveTheme.Name}, because Windows did not report an app mode."
            };
        }
    }

    /// <summary>
    /// UI thread: what Windows reports now. A change re-applies the palette once settings have loaded. A failed read is kept in
    /// Diagnostics by type only (Rule 10) and leaves the default palette; it is not an error the user has to act on.
    /// </summary>
    internal void SetSystemAppearance(SystemAppearance system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (system == _systemAppearance) return;
        _systemAppearance = system;
        if (system.ReadFailure is { } failure) { _diagnostics.RecordFailure(ManagerSubsystem.Settings, failure); RefreshDiagnosticsView(); }
        Raise(nameof(ThemeStatus));
        if (_themeApplied) ThemeChanged?.Invoke(EffectiveTheme);
    }

    private void ApplyEffectiveTheme()
    {
        _themeApplied = true;
        ThemeChanged?.Invoke(EffectiveTheme);
        Raise(nameof(ThemeName)); Raise(nameof(ThemeStatus));
    }
}
