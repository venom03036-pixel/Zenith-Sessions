using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Keep clients awake (R-instances-17, owner decision 2026-11-08): the saved choice, its Settings control, the Guide and search.
// Off by default; the saved time is optional within settings schema 1.
public sealed class KeepAwakeSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-keep-awake-" + Guid.NewGuid().ToString("N"));
    private string Primary => Path.Combine(_root, "settings.json");
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public async Task TheSavedTimeSurvivesLoadAndSaveAndOlderFilesStayOff()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"KeepAwakeMinutes\":10}");
        using (var store = new SettingsStore(_root))
        {
            var loaded = await store.LoadAsync(default);
            Assert.Null(loaded.Notice);
            await store.SaveAsync(loaded.Settings, default);
        }
        Assert.True(SavedKeepAwakeMinutes() == 10, "Saving settings dropped the chosen keep-awake time.");

        // A schema-1 file from before this setting loads without a notice and keeps the feature off.
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"IdleLockMinutes\":15}");
        using (var store = new SettingsStore(_root))
        {
            var loaded = await store.LoadAsync(default);
            Assert.Null(loaded.Notice);
            await store.SaveAsync(loaded.Settings, default);
        }
        Assert.Equal(0, SavedKeepAwakeMinutes());
    }

    [Fact]
    public async Task ATimeOutsideTheBoundsIsRefusedAsInvalidSettings()
    {
        Directory.CreateDirectory(_root);
        // Roblox disconnects an idle client after 20 minutes, so a longer gap can never keep a client awake; a shorter one than
        // five minutes would change the foreground window far too often.
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"KeepAwakeMinutes\":25}");
        using var store = new SettingsStore(_root);
        var error = await Record.ExceptionAsync(() => store.LoadAsync(default));
        Assert.True(error is InvalidDataException, "A keep-awake time of 25 minutes was accepted; it must be refused as an invalid setting.");
    }

    [Fact]
    public void SettingsOffersTheChoiceBeforeThePerformanceSectionAndTheGuideNamesIt()
    {
        var document = ProductionWindow();
        var choice = document.Descendants().SingleOrDefault(e => (string?)e.Attribute(X + "Name") == "SettingsKeepAwake");
        Assert.True(choice is not null, "Settings has no control for keeping clients awake.");
        Assert.Equal("ComboBox", choice!.Name.LocalName);
        Assert.Equal("{Binding KeepAwakeChoices}", (string?)choice.Attribute("ItemsSource"));
        Assert.Equal("Minutes", (string?)choice.Attribute("SelectedValuePath"));
        Assert.Equal("{Binding KeepAwakeMinutes}", (string?)choice.Attribute("SelectedValue"));
        Assert.False(string.IsNullOrWhiteSpace((string?)choice.Attribute("AutomationProperties.Name")));
        var all = document.Descendants().ToList();
        int appearance = all.FindIndex(e => (string?)e.Attribute(X + "Name") == "SettingsAppearanceAnchor");
        int performance = all.FindIndex(e => (string?)e.Attribute(X + "Name") == "SettingsPerformanceAnchor");
        Assert.InRange(all.IndexOf(choice), appearance + 1, performance - 1);
        // The status line and the explicit restart after Panic or Lock sit with the choice.
        var status = document.Descendants().SingleOrDefault(e => (string?)e.Attribute(X + "Name") == "SettingsKeepAwakeStatus");
        Assert.True(status is not null, "Settings shows no status for keeping clients awake.");
        Assert.Equal("{Binding KeepAwakeStatus}", (string?)status!.Attribute("Text"));
        var resume = document.Descendants().SingleOrDefault(e => (string?)e.Attribute(X + "Name") == "ResumeKeepAwakeButton");
        Assert.True(resume is not null, "Settings has no Resume button for keeping clients awake after Panic or Lock.");
        Assert.Equal("{Binding ResumeKeepAwake}", (string?)resume!.Attribute("Command"));
        Assert.Equal("{Binding CanResumeKeepAwake}", (string?)resume.Attribute("IsEnabled"));

        var guide = document.Descendants().Where(e => ((string?)e.Attribute("Text"))?.Contains("Keep clients awake", StringComparison.Ordinal) == true
            && ((string?)e.Attribute("Text"))?.Contains("F13", StringComparison.Ordinal) == true).ToList();
        Assert.True(guide.Count >= 1, "The Guide does not explain Keep clients awake and its F13 press.");
        Assert.Contains(guide, e => ((string)e.Attribute("Text")!).Contains("input isolation trial", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("anti-afk")]
    [InlineData("idle kick")]
    [InlineData("keep awake")]
    [InlineData("afk")]
    public void SettingsSearchFindsKeepClientsAwake(string query)
    {
        var entry = SettingsSearchCatalog.Search(query).FirstOrDefault(e => e.Id == "keep-awake");
        Assert.True(entry is not null, $"Searching settings for '{query}' does not find Keep clients awake.");
        Assert.Equal(SettingsSearchTarget.Appearance, entry!.Target);
        Assert.Contains("Off by default", entry.Description, StringComparison.Ordinal);
    }

    private int SavedKeepAwakeMinutes()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Primary));
        return document.RootElement.TryGetProperty("KeepAwakeMinutes", out var value) ? value.GetInt32() : 0;
    }

    private static XDocument ProductionWindow()
    {
        using var stream = typeof(KeepAwakeSettingsTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        return XDocument.Load(stream);
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-keep-awake-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
