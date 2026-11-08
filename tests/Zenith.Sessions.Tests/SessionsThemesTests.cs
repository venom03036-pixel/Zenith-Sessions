using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MacroSuite.App.Themes;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
// The namespace Zenith.Sessions.App hides its App class from this namespace.
using SessionsApp = Zenith.Sessions.App.App;

namespace Zenith.Sessions.Tests;

/// <summary>
/// Light, Follow Windows and Windows High Contrast. Windows itself is never read or changed here: each test passes the app mode and
/// High Contrast colours that Windows would report, so the result does not depend on this computer's settings.
/// </summary>
public sealed class SessionsThemesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-sessions-themes-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] DarkIds = ["graphite-mint", "midnight", "amber-night", "ocean-blue", "violet-dusk"];
    // Colour sets of the kind Windows applies under High Contrast (Window, WindowText, Highlight, HighlightText, GrayText), plus
    // one a person could make where the highlight is too pale to read on the window colour.
    private static readonly Dictionary<string, HighContrastColors> HighContrastSets = new(StringComparer.Ordinal)
    {
        ["black"] = Hc("#000000", "#FFFFFF", "#1AEBFF", "#000000", "#3FF23F"),
        ["white"] = Hc("#FFFFFF", "#000000", "#37006E", "#FFFFFF", "#600000"),
        ["dark grey"] = Hc("#202020", "#FFFFFF", "#8EE3F0", "#263B50", "#A6A6A6"),
        ["cream"] = Hc("#FFFAEF", "#3D3D3D", "#903909", "#FFF5E3", "#676767"),
        ["pale highlight"] = Hc("#FFFFFF", "#000000", "#F0F0F0", "#E0E0E0", "#F8F8F8"),
    };
    public static IEnumerable<object[]> PaletteNames()
    {
        foreach (string id in DarkIds) yield return [id];
        yield return ["light"];
        yield return ["follow-windows with Windows apps light"];
        yield return ["follow-windows with Windows apps dark"];
        foreach (string set in HighContrastSets.Keys) yield return ["high contrast " + set];
    }

    [Fact]
    public void SettingsOfferTheDarkPresetsThenLightThenFollowWindows()
    {
        Assert.Equal([.. DarkIds, "light", "follow-windows"], SessionsThemes.Choices.Select(theme => theme.Id));
        Assert.Equal(["Light", "Follow Windows"], SessionsThemes.Choices.Skip(5).Select(theme => theme.Name));
        Assert.True(AppearancePalette.Luminance(SessionsThemes.Light.Colors["AppBackground"]) > .7, "The Light theme must use light surfaces.");
        Assert.True(AppearancePalette.Luminance(SessionsThemes.Light.Colors["TextPrimary"]) < .05, "The Light theme must use dark text.");
    }

    [Theory]
    [MemberData(nameof(PaletteNames))]
    public void EveryPaletteKeepsTextChipsAndControlsReadable(string name)
    {
        var palette = Palette(name); var colors = palette.Colors; var problems = new List<string>();
        void Check(string foreground, string background, double minimum)
        {
            double ratio = AppearancePalette.Contrast(colors[foreground], colors[background]);
            if (ratio < minimum) problems.Add($"{foreground} on {background} is {ratio:0.00}:1, below {minimum}:1");
        }
        foreach (string text in new[] { "TextPrimary", "TextSecondary", "TextMuted", "AccentPrimary", "AccentSuccess", "AccentWarning", "AccentDanger" })
            foreach (string background in new[] { "AppBackground", "CardBackground", "SurfaceBackground", "HoverBackground", "ActiveBackground" })
                Check(text, background, 4.5);
        Check("SelectionForeground", "SelectionBackground", 4.5);
        foreach (string button in new[] { "AccentPrimary", "PrimaryHoverBackground", "PrimaryPressedBackground" }) Check("PrimaryForeground", button, 4.5);
        foreach (string button in new[] { "DangerGradient", "DangerHoverBackground", "DangerPressedBackground" }) Check("DangerForeground", button, 4.5);
        // Chips and banners in SessionsControls.xaml: each status text on its own badge background.
        Check("AccentPrimary", "BadgeBgTeal", 4.5); Check("AccentSuccess", "BadgeBgSuccess", 4.5);
        Check("AccentWarning", "BadgeBgWarning", 4.5); Check("AccentDanger", "BadgeBgDanger", 4.5); Check("TextPrimary", "BadgeBgWarning", 4.5);
        // Non-text contrast for field borders and the keyboard focus ring.
        foreach (string background in new[] { "CardBackground", "SurfaceBackground" }) { Check("ControlBorder", background, 3); Check("BorderBrushFocus", background, 3); }
        Assert.True(problems.Count == 0, $"{name} ({palette.Name}): " + string.Join("; ", problems));
        // Every key the window reads is filled, so applying a palette never leaves a previous palette's colour behind.
        Assert.Equal(ThemeCatalog.Resolve(ThemeCatalog.DefaultId).Colors.Keys.Order(StringComparer.Ordinal), colors.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FollowWindowsUsesLightOnlyWhileWindowsAppsAreLight()
    {
        Assert.Same(SessionsThemes.Light, SessionsThemes.Effective("follow-windows", new(true, null)));
        Assert.Equal(ThemeCatalog.DefaultId, SessionsThemes.Effective("follow-windows", new(false, null)).Id);
        // No answer from Windows keeps the default dark palette, the manager's look before this choice existed.
        Assert.Equal(ThemeCatalog.DefaultId, SessionsThemes.Effective("follow-windows", SystemAppearance.Unknown).Id);
        // The app mode changes nothing for a theme chosen by name.
        Assert.Equal("amber-night", SessionsThemes.Effective("amber-night", new(true, null)).Id);
        Assert.Same(SessionsThemes.Light, SessionsThemes.Effective("light", new(false, null)));
    }

    [Fact]
    public void HighContrastReplacesEveryChoiceWithOnlyTheWindowsColours()
    {
        foreach (var (set, hc) in HighContrastSets)
        {
            var allowed = new HashSet<Color> { hc.Window, hc.WindowText, hc.Highlight, hc.HighlightText, hc.GrayText, Colors.Black, Colors.White };
            foreach (var choice in SessionsThemes.Choices)
            {
                var palette = SessionsThemes.Effective(choice.Id, new(choice.Id == "light", hc));
                Assert.Equal("high-contrast", palette.Id);
                Assert.Equal(hc.Window, palette.Colors["AppBackground"]); Assert.Equal(hc.Window, palette.Colors["CardBackground"]);
                Assert.Equal(hc.Window, palette.Colors["SurfaceBackground"]); Assert.Equal(hc.WindowText, palette.Colors["TextPrimary"]);
                Assert.Equal(hc.Highlight, palette.Colors["SelectionBackground"]);
                // No dark-only badge, status or danger constant survives from the presets.
                var foreign = palette.Colors.Where(pair => !allowed.Contains(pair.Value)).Select(pair => pair.Key).ToArray();
                Assert.True(foreign.Length == 0, $"High contrast {set} kept non-Windows colours for {string.Join(", ", foreign)}.");
            }
        }
        var black = HighContrastSets["black"];
        Assert.Equal(black.HighlightText, SessionsThemes.HighContrast(black).Colors["SelectionForeground"]);
        Assert.Equal(black.Highlight, SessionsThemes.HighContrast(black).Colors["AccentPrimary"]);
        // A highlight too pale to read on the window colour is not used as text: the window text colour is.
        var pale = HighContrastSets["pale highlight"];
        Assert.Equal(pale.WindowText, SessionsThemes.HighContrast(pale).Colors["AccentPrimary"]);
        // Hovering or pressing a primary or danger button changes its fill (its label stays readable: see the palette test above).
        // Black is Windows' High Contrast Black: the button turns from the highlight colour to the window text colour.
        Assert.Equal(black.WindowText, SessionsThemes.HighContrast(black).Colors["PrimaryHoverBackground"]);
        foreach (string set in new[] { "black", "white", "dark grey", "cream" })
        {
            var colors = SessionsThemes.HighContrast(HighContrastSets[set]).Colors;
            foreach (var (rest, state) in new[] { ("AccentPrimary", "PrimaryHoverBackground"), ("AccentPrimary", "PrimaryPressedBackground"),
                         ("DangerGradient", "DangerHoverBackground"), ("DangerGradient", "DangerPressedBackground") })
                Assert.True(colors[state] != colors[rest], $"High contrast {set}: {state} is the resting {rest} colour, so hovering or pressing shows nothing.");
        }
    }

    [Fact]
    public Task HighContrastMarksHoveredAndPressedRowsAndButtonsWithAWindowTextEdge() => OnSta(() =>
    {
        // No Windows colour but the window colour keeps the window text readable as a fill, so the window colour stays behind the
        // text and a bar of the window text colour along the leading edge shows the state: 3 pixels on hover, 6 while pressed.
        var black = HighContrastSets["black"]; var resources = new ResourceDictionary();
        SessionsApp.ApplyStartupTheme(resources, _root, new(null, black));
        string text = AppearancePalette.Hex(black.WindowText), window = AppearancePalette.Hex(black.Window);
        Assert.Equal(new[] { text, text, window, window, window }, Columns(resources["HoverBackground"], 0, 2, 4, 7, 39));
        Assert.Equal(new[] { text, text, text, window, window }, Columns(resources["ActiveBackground"], 0, 2, 5, 7, 39));
        // Leaving High Contrast restores the theme's own solid hover and press colours.
        SessionsApp.ApplyStartupTheme(resources, _root, new(false, null));
        var standard = ThemeCatalog.Resolve(ThemeCatalog.DefaultId).Colors;
        Assert.Equal(standard["HoverBackground"], Assert.IsType<SolidColorBrush>(resources["HoverBackground"]).Color);
        Assert.Equal(standard["ActiveBackground"], Assert.IsType<SolidColorBrush>(resources["ActiveBackground"]).Color);
        return Task.CompletedTask;
    });

    // The colour drawn in each listed pixel column of a 40x10 surface filled with brush.
    private static string[] Columns(object brush, params int[] columns)
    {
        var surface = new Border { Width = 40, Height = 10, Background = Assert.IsAssignableFrom<Brush>(brush) };
        surface.Measure(new Size(40, 10)); surface.Arrange(new Rect(0, 0, 40, 10));
        var image = new RenderTargetBitmap(40, 10, 96, 96, PixelFormats.Pbgra32); image.Render(surface);
        return columns.Select(x => { var pixel = new byte[4]; image.CopyPixels(new Int32Rect(x, 5, 1, 1), pixel, 4, 0); return AppearancePalette.Hex(Color.FromRgb(pixel[2], pixel[1], pixel[0])); }).ToArray();
    }

    [Theory]
    [InlineData("sunset-neon", "graphite-mint")]
    [InlineData(null, "graphite-mint")]
    [InlineData("", "graphite-mint")]
    [InlineData(" LIGHT ", "light")]
    [InlineData("Follow-Windows", "follow-windows")]
    [InlineData("high-contrast", "graphite-mint")]
    public void AnEditedOrUnknownThemeIdNormalizesToAChoice(string? stored, string expected) =>
        Assert.Equal(expected, SessionsThemes.Normalize(stored));

    [Theory]
    [InlineData("high contrast")]
    [InlineData("light mode")]
    [InlineData("follow windows")]
    public void SettingsSearchFindsTheThemeChoices(string query) => Assert.Equal("appearance", SettingsSearchCatalog.Search(query)[0].Id);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void TheWindowsAppModeIsReadFromItsDword(int value, bool light) => Assert.Equal(light, WindowsAppMode.Interpret(value));

    [Fact]
    public void AnythingButADwordIsNoAppModeAnswer()
    {
        Assert.Null(WindowsAppMode.Interpret(null)); Assert.Null(WindowsAppMode.Interpret("1")); Assert.Null(WindowsAppMode.Interpret(1L));
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", WindowsAppMode.KeyPath);
        Assert.Equal("AppsUseLightTheme", WindowsAppMode.ValueName);
    }

    [Fact]
    public Task TheShellAppliesLightFollowWindowsAndHighContrastAndSavesOnlyTheChoice() => OnSta(async () =>
    {
        await using var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor());
        var applied = new List<ThemePreset>(); shell.ThemeChanged += applied.Add;
        // Windows reports before settings load: the startup palette stays until the saved choice is known.
        shell.SetSystemAppearance(new(true, null)); Assert.Empty(applied);
        await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice);
        Assert.Equal(ThemeCatalog.DefaultId, applied[^1].Id);
        Assert.Equal([.. DarkIds, "light", "follow-windows"], shell.Themes.Select(theme => theme.Id));

        shell.ApplyTheme.Execute("light"); await shell.ApplyTheme.Execution;
        Assert.Same(SessionsThemes.Light, applied[^1]); Assert.Equal("light", SettingsStore.ReadTheme(_root)); Assert.Equal("Light", shell.ThemeName);

        shell.ApplyTheme.Execute("follow-windows"); await shell.ApplyTheme.Execution;
        Assert.Same(SessionsThemes.Light, applied[^1]); Assert.Equal("follow-windows", SettingsStore.ReadTheme(_root));
        Assert.Contains("light mode", shell.ThemeStatus, StringComparison.Ordinal);
        shell.SetSystemAppearance(new(false, null));
        Assert.Equal(ThemeCatalog.DefaultId, applied[^1].Id); Assert.Contains("dark mode", shell.ThemeStatus, StringComparison.Ordinal);
        int count = applied.Count; shell.SetSystemAppearance(new(false, null)); Assert.Equal(count, applied.Count);

        var white = HighContrastSets["white"];
        shell.SetSystemAppearance(new(false, white));
        Assert.Equal("high-contrast", applied[^1].Id); Assert.Equal(white.Window, applied[^1].Colors["AppBackground"]);
        Assert.Contains("High Contrast", shell.ThemeStatus, StringComparison.Ordinal);
        // A theme picked while High Contrast is on is saved, and shown once High Contrast is off.
        shell.ApplyTheme.Execute("amber-night"); await shell.ApplyTheme.Execution;
        Assert.Equal("high-contrast", applied[^1].Id); Assert.Equal("amber-night", SettingsStore.ReadTheme(_root));
        shell.SetSystemAppearance(new(false, null));
        Assert.Equal("amber-night", applied[^1].Id); Assert.Equal("Amber Night", shell.ThemeName);
        Assert.DoesNotContain("High Contrast", shell.ThemeStatus, StringComparison.Ordinal);
    });

    [Fact]
    public Task AnUnreadableWindowsAppModeIsRecordedByTypeAndKeepsTheDefaultPalette() => OnSta(async () =>
    {
        await using var shell = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor());
        var applied = new List<ThemePreset>(); shell.ThemeChanged += applied.Add;
        await shell.InitializeAsync(); Assert.True(shell.IsReady, shell.Notice);
        shell.ApplyTheme.Execute("follow-windows"); await shell.ApplyTheme.Execution;
        shell.Navigate.Execute("Diagnostics");
        shell.SetSystemAppearance(new(null, null, typeof(UnauthorizedAccessException)));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(ThemeCatalog.DefaultId, applied[^1].Id);
        Assert.Contains("did not report", shell.ThemeStatus, StringComparison.Ordinal);
        Assert.Contains(shell.DiagnosticRows, row => row.Event.Subsystem == ManagerSubsystem.Settings && row.ErrorType == nameof(UnauthorizedAccessException));
        Assert.Equal("", shell.ErrorNotice);
    });

    [Fact]
    public void StartupAppliesFollowWindowsAndHighContrastBeforeTheWindowExists()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{\"Version\":1,\"Theme\":\"follow-windows\"}");
        Assert.Equal(SessionsThemes.Light.Colors["AppBackground"], StartupBackground(new(true, null)));
        Assert.Equal(ThemeCatalog.Resolve(ThemeCatalog.DefaultId).Colors["AppBackground"], StartupBackground(new(false, null)));
        Assert.Equal(HighContrastSets["black"].Window, StartupBackground(new(true, HighContrastSets["black"])));
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{\"Version\":1,\"Theme\":\"light\"}");
        Assert.Equal(SessionsThemes.Light.Colors["AppBackground"], StartupBackground(new(false, null)));
    }

    [Theory]
    [MemberData(nameof(PaletteNames))]
    public void ThePackagedReadabilityCheckAcceptsEveryPalette(string name) => Assert.Null(SessionsThemes.ReadabilityProblem(Palette(name)));

    [Fact]
    public Task ThePackagedCheckLaysOutEveryPaletteAndRefusesAnUnreadableOne() => OnSta(() =>
    {
        Assert.Equal([.. DarkIds, "light", "high-contrast"], SessionsThemes.PackagedPalettes().Select(palette => palette.Id));
        var resources = new ResourceDictionary(); var content = new Border { Resources = resources, Child = new TextBlock { Text = "Settings" } };
        content.SetResourceReference(Border.BackgroundProperty, "CardBackground");
        PackagedLayoutCheck.RequireEveryThemeLaysOut(resources, content, SessionsThemes.PackagedPalettes());
        var standard = ThemeCatalog.Resolve(ThemeCatalog.DefaultId).Colors["CardBackground"];
        Assert.Equal(standard, ((SolidColorBrush)content.Background).Color);
        var unreadable = new ThemePreset("unreadable", "Unreadable", "Secondary text in the card colour.",
            new Dictionary<string, Color>(SessionsThemes.Light.Colors, StringComparer.Ordinal) { ["TextSecondary"] = SessionsThemes.Light.Colors["CardBackground"] });
        var error = Assert.Throws<InvalidOperationException>(() => PackagedLayoutCheck.RequireEveryThemeLaysOut(resources, content, [SessionsThemes.Light, unreadable]));
        Assert.Contains("Unreadable", error.Message, StringComparison.Ordinal); Assert.Contains("TextSecondary on AppBackground", error.Message, StringComparison.Ordinal);
        Assert.Equal(standard, ((SolidColorBrush)content.Background).Color);
        var missing = new ThemePreset("missing", "Missing key", "No focus colour.",
            SessionsThemes.Light.Colors.Where(pair => pair.Key != "BorderBrushFocus").ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        Assert.Contains("BorderBrushFocus", Assert.Throws<InvalidOperationException>(() => PackagedLayoutCheck.RequireEveryThemeLaysOut(resources, content, [missing])).Message, StringComparison.Ordinal);
        return Task.CompletedTask;
    });

    // Also used by the rendered selection tests (SidebarTemplateTests, AccountsWorkspaceTests), so they read the same colour sets.
    internal static ThemePreset Palette(string name) => name switch
    {
        "follow-windows with Windows apps light" => SessionsThemes.Effective("follow-windows", new(true, null)),
        "follow-windows with Windows apps dark" => SessionsThemes.Effective("follow-windows", new(false, null)),
        _ when name.StartsWith("high contrast ", StringComparison.Ordinal) => SessionsThemes.Effective(ThemeCatalog.DefaultId, new(null, HighContrastSets[name["high contrast ".Length..]])),
        _ => SessionsThemes.Effective(name, new(false, null))
    };
    private static HighContrastColors Hc(string window, string text, string highlight, string highlightText, string gray) =>
        new(AppearancePalette.Parse(window), AppearancePalette.Parse(text), AppearancePalette.Parse(highlight), AppearancePalette.Parse(highlightText), AppearancePalette.Parse(gray));
    private Color StartupBackground(SystemAppearance system)
    {
        Color applied = default; Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { var resources = new ResourceDictionary(); SessionsApp.ApplyStartupTheme(resources, _root, system); applied = Assert.IsType<SolidColorBrush>(resources["AppBackground"]).Color; }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Startup did not finish applying its theme.");
        if (failure is not null) throw new Xunit.Sdk.XunitException($"Startup theme failed ({failure.GetType().Name}): {failure.Message}");
        return applied;
    }
    private sealed class Monitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 10), [])); }
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => throw new InvalidOperationException("No native action expected.");
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => throw new InvalidOperationException("No native action expected.");
    }
    private static Task OnSta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); done.TrySetResult(); } catch (Exception error) { done.TrySetException(error); } finally { dispatcher.InvokeShutdown(); }
            }); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-sessions-themes-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
