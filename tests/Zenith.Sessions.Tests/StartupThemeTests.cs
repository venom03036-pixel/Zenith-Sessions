using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MacroSuite.App.Themes;
using Xunit;
// The namespace Zenith.Sessions.App hides its App class from this namespace.
using SessionsApp = Zenith.Sessions.App.App;

namespace Zenith.Sessions.Tests;

// App.OnStartup applies this theme before it creates and shows the manager window, so the first frame already uses the saved
// palette. App itself cannot be built here (one Application per process), so these drive the startup step it delegates to.
public sealed class StartupThemeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-startup-theme-" + Guid.NewGuid().ToString("N"));
    private string Primary => Path.Combine(_root, "settings.json");
    private const string AmberNight = "{\"Version\":1,\"Theme\":\"amber-night\"}";

    [Fact]
    public async Task TheSavedThemeIsInPlaceBeforeTheWindowIsCreated()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Primary, AmberNight);
        Assert.True(StartupBackground() == Background("amber-night"),
            $"Startup applied {Name(StartupBackground())} before the window instead of the saved Amber Night, so the first frames flash the wrong palette.");
        Assert.Equal(new[] { "settings.json" }, Files());
        Assert.Equal(AmberNight, await File.ReadAllTextAsync(Primary));
    }

    [Theory]
    [InlineData("no data folder yet")]
    [InlineData("a settings file over the 64 KiB limit")]
    [InlineData("a damaged settings file with a valid backup")]
    [InlineData("only a backup copy")]
    [InlineData("only an interrupted temporary copy")]
    [InlineData("a settings file from a newer version")]
    [InlineData("a settings file another program holds open")]
    [InlineData("a theme this version does not know")]
    public async Task StartupKeepsTheDefaultThemeAndLeavesSettingsFilesAsTheyWere(string situation)
    {
        FileStream? held = null;
        if (situation != "no data folder yet") Directory.CreateDirectory(_root);
        switch (situation)
        {
            case "a settings file over the 64 KiB limit":
                await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"Theme\":\"amber-night\",\"Padding\":\"" + new string('x', 70_000) + "\"}"); break;
            case "a damaged settings file with a valid backup":
                await File.WriteAllTextAsync(Primary, "damaged settings"); await File.WriteAllTextAsync(Primary + ".bak", AmberNight); break;
            case "only a backup copy": await File.WriteAllTextAsync(Primary + ".bak", AmberNight); break;
            case "only an interrupted temporary copy": await File.WriteAllTextAsync(Primary + ".tmp", AmberNight); break;
            case "a settings file from a newer version": await File.WriteAllTextAsync(Primary, "{\"Version\":2,\"Theme\":\"amber-night\"}"); break;
            case "a settings file another program holds open": await File.WriteAllTextAsync(Primary, AmberNight); break;
            case "a theme this version does not know": await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"Theme\":\"sunset-neon\"}"); break;
        }
        var before = Snapshot();
        if (situation == "a settings file another program holds open") held = new FileStream(Primary, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try { Assert.True(StartupBackground() == Background(ThemeCatalog.DefaultId), $"With {situation}, startup applied {Name(StartupBackground())} instead of the default."); }
        finally { held?.Dispose(); }
        Assert.Equal(before, Snapshot());
        Assert.Equal(situation != "no data folder yet", Directory.Exists(_root));
    }

    private static Color Background(string theme) => ThemeCatalog.Resolve(theme).Colors["AppBackground"];
    private static string Name(Color background) =>
        ThemeCatalog.Presets.FirstOrDefault(theme => theme.Colors["AppBackground"] == background)?.Name ?? background.ToString();
    // Runs the startup step on its own STA thread, as App.OnStartup does on the UI thread, and returns the applied canvas color.
    private Color StartupBackground()
    {
        Color applied = default; Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var resources = new ResourceDictionary();
                SessionsApp.ApplyStartupTheme(resources, _root);
                applied = Assert.IsType<SolidColorBrush>(resources["AppBackground"]).Color;
            }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Startup did not finish applying its theme.");
        if (failure is not null) throw new Xunit.Sdk.XunitException($"Startup theme failed ({failure.GetType().Name}): {failure.Message}");
        return applied;
    }
    private string[] Files() => Directory.Exists(_root) ? Directory.GetFiles(_root).Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal).ToArray() : [];
    private string Snapshot() => string.Join("|", Files().Select(name => name + "=" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(_root, name))))));
    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-startup-theme-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
