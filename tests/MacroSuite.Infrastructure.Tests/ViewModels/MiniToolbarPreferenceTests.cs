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

public sealed class MiniToolbarPreferenceTests : IAsyncLifetime
{
    private readonly AppDataPathResolver _paths = new(Path.Combine(Path.GetTempPath(), "ZenithMiniPreferences", Guid.NewGuid().ToString("N")));
    private readonly List<SettingsViewModel> _models = [];
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        foreach (var model in _models)
        {
            model.DetachRuntimeSettings();
            await model.Appearance.ShutdownAsync();
        }
        if (Directory.Exists(_paths.RootDirectory)) Directory.Delete(_paths.RootDirectory, recursive: true);
    }
    private SettingsViewModel Create()
    {
        var vault = new Vault();
        var model = new SettingsViewModel(vault, new AIVisionRouter(vault), new Display(), _paths, new ImageScanBudget());
        _models.Add(model);
        return model;
    }
    private string SettingsPath => Path.Combine(_paths.ConfigDirectory, "settings.json");

    [Fact]
    public void FirstLaunch_LeavesBothOptionalModesOff()
    {
        var settings = Create();
        settings.MiniToolbarClickThrough.Should().BeFalse();
        settings.MiniToolbarHideFromCapture.Should().BeFalse();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"miniToolbarClickThrough\":null,\"miniToolbarHideFromCapture\":false}")]
    [InlineData("{\"miniToolbarClickThrough\":\"true\",\"miniToolbarHideFromCapture\":1}")]
    public void LegacyOrInvalidModes_StayOffWithoutDiscardingOtherPreferences(string fields)
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        File.WriteAllText(SettingsPath, fields[..^1] + (fields.Length > 2 ? "," : "") + "\"libraryPaneWidth\":275}");
        var settings = Create();
        settings.MiniToolbarClickThrough.Should().BeFalse();
        settings.MiniToolbarHideFromCapture.Should().BeFalse();
        settings.LibraryPaneWidth.Should().Be(275);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AutomaticSave_PersistsIndependentModesAndClearingThem(bool clickThrough, bool hideFromCapture)
    {
        var settings = Create();
        settings.MiniToolbarClickThrough = !clickThrough;
        settings.MiniToolbarHideFromCapture = !hideFromCapture;
        settings.MiniToolbarClickThrough = clickThrough;
        settings.MiniToolbarHideFromCapture = hideFromCapture;
        var reloaded = Create();
        reloaded.MiniToolbarClickThrough.Should().Be(clickThrough);
        reloaded.MiniToolbarHideFromCapture.Should().Be(hideFromCapture);
        using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        document.RootElement.GetProperty("miniToolbarClickThrough").GetBoolean().Should().Be(clickThrough);
        document.RootElement.GetProperty("miniToolbarHideFromCapture").GetBoolean().Should().Be(hideFromCapture);
    }

    [Fact]
    public void ExplicitSave_CommitsBothModesWhenAutomaticSavingIsDisabled()
    {
        var settings = Create();
        settings.SaveSettings().Should().BeTrue();
        string before = File.ReadAllText(SettingsPath);
        settings.AutomaticSettingsSaving = false;
        settings.MiniToolbarClickThrough = true;
        settings.MiniToolbarHideFromCapture = true;
        File.ReadAllText(SettingsPath).Should().Be(before);
        settings.SaveSettings().Should().BeTrue();
        settings.SettingsStatus.Should().Be("Settings saved.");
        var reloaded = Create();
        reloaded.MiniToolbarClickThrough.Should().BeTrue();
        reloaded.MiniToolbarHideFromCapture.Should().BeTrue();
    }

    [Fact]
    public void FailedSave_KeepsPreviousFileAndReportsFailureUntilRetry()
    {
        var settings = Create();
        settings.SaveSettings().Should().BeTrue();
        string before = File.ReadAllText(SettingsPath);
        using (var held = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            settings.MiniToolbarClickThrough = true;
            settings.MiniToolbarHideFromCapture = true;
            settings.SaveSettings().Should().BeFalse();
            settings.HasSettingsError.Should().BeTrue();
            settings.SettingsStatus.Should().Contain("Could not save settings");
        }
        File.ReadAllText(SettingsPath).Should().Be(before);
        settings.SaveSettings().Should().BeTrue();
        settings.HasSettingsError.Should().BeFalse();
        var reloaded = Create();
        reloaded.MiniToolbarClickThrough.Should().BeTrue();
        reloaded.MiniToolbarHideFromCapture.Should().BeTrue();
    }

    [Fact]
    public void GeneralSettings_ExposeBothModesWithRecoveryInstructionsAndOptInDefaults()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "MacroSuite.slnx"))) directory = directory.Parent;
        directory.Should().NotBeNull();
        string app = Path.Combine(directory!.FullName, "src", "MacroSuite.App");
        var settings = XDocument.Load(Path.Combine(app, "Views", "SettingsView.xaml"));
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var general = settings.Descendants(wpf + "TabItem").Single(x => (string?)x.Attribute("Header") == "General");
        general.Descendants(wpf + "CheckBox").Should().Contain(x => (string?)x.Attribute("IsChecked") == "{Binding MiniToolbarClickThrough}");
        general.Descendants(wpf + "CheckBox").Should().Contain(x => (string?)x.Attribute("IsChecked") == "{Binding MiniToolbarHideFromCapture}");
        general.ToString().Should().Contain("tray menu").And.Contain("Off by default");
        using var defaults = JsonDocument.Parse(File.ReadAllText(Path.Combine(app, "Defaults", "settings.json")));
        defaults.RootElement.GetProperty("miniToolbarClickThrough").GetBoolean().Should().BeFalse();
        defaults.RootElement.GetProperty("miniToolbarHideFromCapture").GetBoolean().Should().BeFalse();
    }

    private sealed class Display : IDisplayInfoService
    {
        public int GetMaxMonitorRefreshRate() => 165;
        public int GetPrimaryMonitorRefreshRate() => 165;
    }
    private sealed class Vault : ISecretVault
    {
        public void SetSecret(string keyName, string secretValue) { }
        public string? GetSecret(string keyName) => null;
        public bool HasSecret(string keyName) => false;
        public bool RemoveSecret(string keyName) => false;
        public IReadOnlyList<string> ListSecretKeys() => [];
    }
}
