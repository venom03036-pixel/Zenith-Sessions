using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.AI;
using MacroSuite.Core.Security;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Vision;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.ViewModels;

public sealed partial class MiniToolbarHotkeyPreferenceTests : IAsyncLifetime
{
    private readonly AppDataPathResolver _paths = new(Path.Combine(Path.GetTempPath(), "ZenithMiniHotkeyPreferences", Guid.NewGuid().ToString("N")));
    private readonly List<SettingsViewModel> _models = [];
    private string SettingsPath => Path.Combine(_paths.ConfigDirectory, "settings.json");
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        foreach (var model in _models)
        {
            model.DetachRuntimeSettings();
            await model.Appearance.ShutdownAsync();
        }
        string fixtureParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ZenithMiniHotkeyPreferences"));
        string fixture = Path.GetFullPath(_paths.RootDirectory);
        if (!fixture.StartsWith(fixtureParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The isolated preference fixture escaped its parent.");
        if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true);
    }
    private SettingsViewModel Create()
    {
        var vault = new Vault();
        var model = new SettingsViewModel(vault, new AIVisionRouter(vault), new Display(), _paths, new ImageScanBudget());
        _models.Add(model);
        return model;
    }

    [Fact]
    public void NewInstallation_ProvidesRecoveryShortcutWithoutEnablingClickThrough()
    {
        var settings = Create();
        settings.MiniToolbarClickThroughHotkey.Should().Be("Ctrl+Alt+F10");
        settings.MiniToolbarClickThroughHotkeyDisplay.Should().Be("Ctrl+Alt+F10");
        settings.MiniToolbarClickThrough.Should().BeFalse();
        settings.MiniToolbarHideFromCapture.Should().BeFalse();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"miniToolbarClickThroughHotkey\":null}")]
    [InlineData("{\"miniToolbarClickThroughHotkey\":true}")]
    [InlineData("{\"miniToolbarClickThroughHotkey\":42}")]
    [InlineData("{\"miniToolbarClickThroughHotkey\":\"\"}")]
    [InlineData("{\"miniToolbarClickThroughHotkey\":\"Ctrl+NotAKey\"}")]
    public void LegacyOrInvalidShortcut_UsesDefaultAndKeepsOtherPreferences(string json)
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        File.WriteAllText(SettingsPath, json[..^1] + (json.Length > 2 ? "," : "") + "\"libraryPaneWidth\":275}");
        var settings = Create();
        settings.MiniToolbarClickThroughHotkey.Should().Be("Ctrl+Alt+F10");
        settings.LibraryPaneWidth.Should().Be(275);
        settings.MiniToolbarClickThrough.Should().BeFalse();
    }

    [Theory]
    [InlineData("Shift+9")]
    [InlineData("Ctrl+Shift+L")]
    [InlineData("Ctrl+Alt+Shift+Win+F24")]
    [InlineData("F10")]
    [InlineData("ESC")]
    public void ValidShortcut_SavesReloadsAndNotifiesOnce(string hotkey)
    {
        var settings = Create();
        var changes = new List<string>();
        var properties = new List<string>();
        settings.MiniToolbarClickThroughHotkeyChanged += changes.Add;
        settings.PropertyChanged += (_, args) => properties.Add(args.PropertyName ?? string.Empty);
        settings.MiniToolbarClickThroughHotkey = " " + hotkey + " ";
        settings.MiniToolbarClickThroughHotkey = hotkey;
        changes.Should().Equal(hotkey);
        properties.Should().Contain(nameof(SettingsViewModel.MiniToolbarClickThroughHotkeyDisplay));
        settings.MiniToolbarClickThrough.Should().BeFalse();
        settings.MiniToolbarHideFromCapture.Should().BeFalse();
        Create().MiniToolbarClickThroughHotkey.Should().Be(hotkey);
        using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        document.RootElement.GetProperty("miniToolbarClickThroughHotkey").GetString().Should().Be(hotkey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+NotAKey")]
    [InlineData("Shift")]
    public void InvalidAssignment_KeepsValidShortcutAndSavedFile(string? value)
    {
        var settings = Create();
        settings.MiniToolbarClickThroughHotkey = "Ctrl+Shift+9";
        string before = File.ReadAllText(SettingsPath);
        settings.MiniToolbarClickThroughHotkey = value!;
        settings.MiniToolbarClickThroughHotkey.Should().Be("Ctrl+Shift+9");
        File.ReadAllText(SettingsPath).Should().Be(before);
        settings.PersistenceStatus.Should().Contain("valid key or combination");
    }

    [Fact]
    public void ExplicitSave_CommitsShortcutWhenAutomaticSavingIsDisabled()
    {
        var settings = Create();
        settings.SaveSettings().Should().BeTrue();
        string before = File.ReadAllText(SettingsPath);
        settings.AutomaticSettingsSaving = false;
        settings.MiniToolbarClickThroughHotkey = "Ctrl+Shift+9";
        File.ReadAllText(SettingsPath).Should().Be(before);
        settings.SaveSettings().Should().BeTrue();
        Create().MiniToolbarClickThroughHotkey.Should().Be("Ctrl+Shift+9");
    }

    [Fact]
    public void Capture_UsesItsOwnDisplayAndEscapeOrSecondClickCancels()
    {
        var settings = Create();
        settings.BeginHotkeyCapture("mini-click-through");
        settings.MiniToolbarClickThroughHotkeyDisplay.Should().Be("Press a key…");
        settings.CompleteHotkeyCapture("ESC").Should().BeTrue();
        settings.HotkeyCaptureTarget.Should().BeEmpty();
        settings.MiniToolbarClickThroughHotkey.Should().Be("Ctrl+Alt+F10");
        settings.BeginHotkeyCapture("mini-click-through");
        settings.BeginHotkeyCapture("mini-click-through");
        settings.HotkeyCaptureTarget.Should().BeEmpty();
        settings.MiniToolbarClickThroughHotkeyDisplay.Should().Be("Ctrl+Alt+F10");
        settings.BeginHotkeyCapture("mini-click-through");
        settings.CompleteHotkeyCapture("Shift+9").Should().BeTrue();
        settings.MiniToolbarClickThroughHotkeyDisplay.Should().Be("Shift+9");
        Create().MiniToolbarClickThroughHotkey.Should().Be("Shift+9");
    }

    [Theory]
    [InlineData("record")]
    [InlineData("stop")]
    [InlineData("play")]
    [InlineData("pause")]
    [InlineData("panic")]
    public void Capture_RejectsReuseOfMiniShortcutByAnotherTransport(string target)
    {
        var settings = Create();
        settings.BeginHotkeyCapture(target);
        settings.CompleteHotkeyCapture(settings.MiniToolbarClickThroughHotkey).Should().BeTrue();
        settings.RecordHotkey.Should().Be("F6");
        settings.StopHotkey.Should().Be("F8");
        settings.PlayHotkey.Should().Be("F4");
        settings.PauseHotkey.Should().Be("F5");
        settings.PanicHotkey.Should().Be("F12");
    }

    [Theory]
    [InlineData("F1")]
    [InlineData("F6")]
    [InlineData("F8")]
    [InlineData("F4")]
    [InlineData("F5")]
    [InlineData("F12")]
    public void Capture_RejectsTransportOrAutoClickerShortcutForMini(string hotkey)
    {
        var settings = Create();
        settings.BeginHotkeyCapture("mini-click-through");
        settings.CompleteHotkeyCapture(hotkey).Should().BeTrue();
        settings.MiniToolbarClickThroughHotkey.Should().Be("Ctrl+Alt+F10");
    }

    [Fact]
    public void GeneralAndShortcuts_OfferCaptureAndShippedDefaultIsSafe()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "MacroSuite.slnx"))) directory = directory.Parent;
        directory.Should().NotBeNull();
        string app = Path.Combine(directory!.FullName, "src", "MacroSuite.App");
        var settings = XDocument.Load(Path.Combine(app, "Views", "SettingsView.xaml"));
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        foreach (string title in new[] { "General", "Shortcuts" })
        {
            var tab = settings.Descendants(wpf + "TabItem").Single(item => (string?)item.Attribute("Header") == title);
            tab.Descendants(wpf + "Button").Should().Contain(item => (string?)item.Attribute("CommandParameter") == "mini-click-through"
                && (string?)item.Attribute("Content") == "{Binding MiniToolbarClickThroughHotkeyDisplay}");
        }
        using var defaults = JsonDocument.Parse(File.ReadAllText(Path.Combine(app, "Defaults", "settings.json")));
        defaults.RootElement.GetProperty("miniToolbarClickThroughHotkey").GetString().Should().Be("Ctrl+Alt+F10");
        defaults.RootElement.GetProperty("miniToolbarClickThrough").GetBoolean().Should().BeFalse();
    }

    private sealed class Display : IDisplayInfoService
    {
        public int GetMaxMonitorRefreshRate() => 165;
        public int GetPrimaryMonitorRefreshRate() => 165;
    }
    private sealed class Vault : ISecretVault
    {
        public void SetSecret(string keyName, string secretValue) => throw new NotSupportedException("No credentials are written by preference tests.");
        public string? GetSecret(string keyName) => null;
        public bool HasSecret(string keyName) => false;
        public bool RemoveSecret(string keyName) => false;
        public IReadOnlyList<string> ListSecretKeys() => [];
    }
}
