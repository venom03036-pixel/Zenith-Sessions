using System.IO;
using System.Reflection;
using System.Xml.Linq;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// R-api-7 (owner decision 2026-11-08): the native launch mode is a saved preference. Official page stays the default until
// the live trial verifies the direct mode; a schema-1 file from before the field loads unchanged.
public sealed partial class ClientWindowIntegrationTests
{
    [Fact]
    public Task ChoosingDirectTicketAndSavingSwitchesTheNativeCaptureAndReloadsThatChoice() => OnUi(async () =>
    {
        var monitor = new Monitor();
        await using (var shell = await Ready(monitor))
        {
            var capture = Assert.IsType<SelectableNativeHandoffCapture>(ReportedField(shell, "_nativeCapture").GetValue(shell));
            Assert.Equal(NativeLaunchMode.OfficialPage, capture.Mode); Assert.Equal(NativeLaunchMode.OfficialPage, shell.SelectedNativeLaunchMode.Mode);
            shell.SelectedNativeLaunchMode = ShellViewModel.NativeLaunchModeOptions.Single(option => option.Mode == NativeLaunchMode.DirectTicket);
            Assert.Equal("Unsaved settings", shell.SaveStatus);
            Assert.Equal(NativeLaunchMode.OfficialPage, capture.Mode); // A draft changes nothing until it is saved.
            shell.SaveSettings.Execute(null); await shell.SaveSettings.Execution;
            Assert.Equal("Settings saved", shell.SaveStatus); Assert.Equal(NativeLaunchMode.DirectTicket, capture.Mode);
            Assert.Contains("\"NativeLaunchMode\":\"direct-ticket\"", await File.ReadAllTextAsync(Path.Combine(_root, "settings.json")), StringComparison.Ordinal);
            Assert.Contains("Direct ticket", shell.NativeLaunchModeStatus, StringComparison.Ordinal);
            Assert.DoesNotContain(shell.Events, line => line.Contains("Operation failed", StringComparison.Ordinal));
        }
        await using (var reopened = await OpenedWithoutDisplayRefresh(monitor))
        {
            var capture = Assert.IsType<SelectableNativeHandoffCapture>(ReportedField(reopened, "_nativeCapture").GetValue(reopened));
            Assert.Equal(NativeLaunchMode.DirectTicket, capture.Mode); Assert.Equal(NativeLaunchMode.DirectTicket, reopened.SelectedNativeLaunchMode.Mode);
            Assert.Equal("Settings saved", reopened.SaveStatus);
        }
    });
}

public sealed class NativeLaunchModeSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-launch-mode-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_root, "settings.json");
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public async Task OfficialPageIsTheDefaultAndASchemaOneFileFromBeforeTheModeLoadsWithoutANotice()
    {
        Assert.Equal(NativeLaunchModes.OfficialPageValue, new SessionsSettings().Validated().NativeLaunchMode);
        Assert.Equal(NativeLaunchMode.OfficialPage, NativeLaunchModes.Default);
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(SettingsPath, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"DenseAccounts\":true}");
        using (var store = new SettingsStore(_root))
        {
            var loaded = await store.LoadAsync(default);
            Assert.Null(loaded.Notice); Assert.Equal(NativeLaunchModes.OfficialPageValue, loaded.Settings.NativeLaunchMode); Assert.Equal("ocean-blue", loaded.Settings.Theme);
            await store.SaveAsync(loaded.Settings with { NativeLaunchMode = "Direct-Ticket" }, default); // Saved in its canonical spelling.
        }
        Assert.Contains("\"NativeLaunchMode\":\"direct-ticket\"", await File.ReadAllTextAsync(SettingsPath), StringComparison.Ordinal);
        using (var store = new SettingsStore(_root))
        {
            var reloaded = await store.LoadAsync(default);
            Assert.Null(reloaded.Notice); Assert.Equal(NativeLaunchModes.DirectTicketValue, reloaded.Settings.NativeLaunchMode);
        }
        Assert.Throws<InvalidDataException>(() => new SessionsSettings(NativeLaunchMode: "browser").Validated());
        Assert.Throws<InvalidDataException>(() => new SessionsSettings(NativeLaunchMode: "").Validated());
    }

    [Theory]
    [InlineData("official-page", true, NativeLaunchMode.OfficialPage)]
    [InlineData(" Official-Page ", true, NativeLaunchMode.OfficialPage)]
    [InlineData("direct-ticket", true, NativeLaunchMode.DirectTicket)]
    [InlineData("DIRECT-TICKET", true, NativeLaunchMode.DirectTicket)]
    [InlineData("ticket", false, NativeLaunchMode.OfficialPage)]
    [InlineData("", false, NativeLaunchMode.OfficialPage)]
    [InlineData(null, false, NativeLaunchMode.OfficialPage)]
    public void OnlyTheTwoNamedModesParseAndEachHasOneSavedSpelling(string? value, bool parses, NativeLaunchMode expected)
    {
        Assert.Equal(parses, NativeLaunchModes.TryParse(value, out var mode)); Assert.Equal(expected, mode);
        Assert.Equal("official-page", NativeLaunchModes.ToSettingsValue(NativeLaunchMode.OfficialPage)); Assert.Equal("direct-ticket", NativeLaunchModes.ToSettingsValue(NativeLaunchMode.DirectTicket));
        Assert.Equal("Official page", NativeLaunchModes.Label(NativeLaunchMode.OfficialPage)); Assert.Equal("Direct ticket", NativeLaunchModes.Label(NativeLaunchMode.DirectTicket));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeLaunchModes.ToSettingsValue((NativeLaunchMode)7));
    }

    [Theory]
    [InlineData("direct ticket")]
    [InlineData("launch mode")]
    [InlineData("authentication ticket")]
    [InlineData("hands-free")]
    public void SettingsSearchFindsTheModeUnderRobloxInstallationAndTheControlIsAComboBoxBoundToTheDraft(string query)
    {
        var entry = Assert.Single(SettingsSearchCatalog.Search(query), entry => entry.Id == "native-launch-mode");
        Assert.Equal(SettingsSearchTarget.Installations, entry.Target); Assert.Equal("Settings", SettingsSearchCatalog.PageOf(entry.Target));
        Assert.Contains("Official page", entry.Description, StringComparison.Ordinal); Assert.Contains("the default", entry.Description, StringComparison.Ordinal);
        Assert.Contains("Direct ticket", entry.Description, StringComparison.Ordinal); Assert.Contains("saved sign-in", entry.Description, StringComparison.Ordinal);
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProductionMainWindow.xaml"); Assert.NotNull(source);
        var document = XDocument.Load(source!);
        var option = Assert.Single(document.Descendants().Where(e => (string?)e.Attribute(X + "Name") == "SettingsNativeLaunchMode"));
        Assert.Equal("ComboBox", option.Name.LocalName);
        Assert.Equal("{Binding SelectedNativeLaunchMode}", (string?)option.Attribute("SelectedItem"));
        Assert.Equal("{x:Static local:ShellViewModel.NativeLaunchModeOptions}", (string?)option.Attribute("ItemsSource"));
        // It sits in the Roblox installation panel, under the anchor its search result scrolls to.
        Assert.Contains(option.Ancestors(), ancestor => ancestor.Name.LocalName == "StackPanel"
            && ancestor.Descendants().Any(e => (string?)e.Attribute(X + "Name") == "SettingsInstallationsAnchor"));
        Assert.Equal(2, ShellViewModel.NativeLaunchModeOptions.Count);
        Assert.Equal(NativeLaunchMode.OfficialPage, ShellViewModel.NativeLaunchModeOptions[0].Mode); Assert.Equal("Official page (default)", ShellViewModel.NativeLaunchModeOptions[0].Name);
        Assert.Equal(NativeLaunchMode.DirectTicket, ShellViewModel.NativeLaunchModeOptions[1].Mode); Assert.Equal("Direct ticket", ShellViewModel.NativeLaunchModeOptions[1].Name);
        Assert.All(ShellViewModel.NativeLaunchModeOptions, option => Assert.Contains("verif", option.Description, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheGuideExplainsBothModesTheDefaultTheHonestHeadersAndThatEveryCheckStays()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProductionMainWindow.xaml")!;
        var document = XDocument.Load(stream);
        var guide = Assert.Single(document.Descendants().Where(e => e.Name.LocalName == "ScrollViewer"
            && ((string?)e.Attribute("Visibility"))?.Contains("IsGuide", StringComparison.Ordinal) == true));
        var texts = guide.Descendants().Where(e => e.Name.LocalName == "TextBlock").Select(e => ProductionWindowStrings.Resolve((string?)e.Attribute("Text"))).ToList();
        Assert.Contains(texts, text => text == "Native launch mode");
        string mode = Assert.Single(texts, text => text.Contains("Settings → Roblox installation → Native launch mode", StringComparison.Ordinal));
        Assert.Contains("Official page is the default", mode, StringComparison.Ordinal);
        Assert.Contains("Direct ticket asks Roblox for a launch ticket with the account's saved sign-in", mode, StringComparison.Ordinal);
        Assert.Contains("no page opens and no click is needed", mode, StringComparison.Ordinal);
        Assert.Contains("Zenith names itself in that request and never pretends to be the Roblox website or a browser", mode, StringComparison.Ordinal);
        Assert.Contains("The ticket is used once and never written anywhere", mode, StringComparison.Ordinal);
        Assert.Contains("Every other check stays", mode, StringComparison.Ordinal);
        Assert.Contains("proves neither the account nor arrival", mode, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchEvidenceNamesTheModeThatProducedTheRequestAndItsTypedRefusals()
    {
        var id = Guid.NewGuid();
        var direct = NativeLaunchDiagnosticPresentationTests.Snapshot(id) with { LaunchMode = NativeLaunchMode.DirectTicket, BrowserOutcome = RobloxHandoffProbeOutcome.TicketIssued };
        var view = NativeLaunchDiagnosticPresentation.Project(id, direct);
        Assert.True(view.Available); Assert.StartsWith("Direct ticket: ", view.Browser, StringComparison.Ordinal);
        Assert.Contains("issued a launch ticket", view.Browser, StringComparison.Ordinal); Assert.Contains("separate stage", view.Browser, StringComparison.Ordinal);
        Assert.Contains("Direct ticket:", view.StageTimes, StringComparison.Ordinal); Assert.DoesNotContain("Official page", view.StageTimes, StringComparison.Ordinal);
        Assert.Contains("Preparing the selected account's launch request (direct ticket).", NativeLaunchDiagnosticPresentation.Project(id, direct with { Stage = NativeLaunchDiagnosticStage.PreparingHandoff }).Summary, StringComparison.Ordinal);
        Assert.Contains("Roblox asked Zenith to slow down", NativeLaunchDiagnosticPresentation.Project(id, direct with { BrowserOutcome = RobloxHandoffProbeOutcome.RateLimited }).Browser, StringComparison.Ordinal);
        Assert.Contains("Roblox refused the ticket request", NativeLaunchDiagnosticPresentation.Project(id, direct with { BrowserOutcome = RobloxHandoffProbeOutcome.Refused }).Browser, StringComparison.Ordinal);
        Assert.Contains("the ticket request was cancelled", NativeLaunchDiagnosticPresentation.Project(id, direct with { BrowserOutcome = RobloxHandoffProbeOutcome.Cancelled }).Browser, StringComparison.Ordinal);
        // The official-page view is unchanged, and a snapshot without a recorded mode still reads as the official page.
        var page = NativeLaunchDiagnosticPresentation.Project(id, NativeLaunchDiagnosticPresentationTests.Snapshot(id));
        Assert.StartsWith("Official page: ", page.Browser, StringComparison.Ordinal); Assert.Contains("Official page:", page.StageTimes, StringComparison.Ordinal);
        Assert.Contains("the page request was cancelled", NativeLaunchDiagnosticPresentation.Project(id, NativeLaunchDiagnosticPresentationTests.Snapshot(id) with { BrowserOutcome = RobloxHandoffProbeOutcome.Cancelled }).Browser, StringComparison.Ordinal);
        Assert.Contains("the launch request was rejected", NativeLaunchDiagnosticPresentation.Project(id, direct with { PreparationStatus = NativeHandoffPreparationStatus.CaptureRejected }).Preparation, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => (direct with { LaunchMode = (NativeLaunchMode)9 }).Validated());
    }

    [Fact]
    public void TheTrialWordingNamesWhatEachModeDoesAndClaimsNoVerification()
    {
        string page = ShellViewModel.DescribeNativeLaunchMode(NativeLaunchMode.OfficialPage), direct = ShellViewModel.DescribeNativeLaunchMode(NativeLaunchMode.DirectTicket);
        Assert.Contains("private Roblox game page", page, StringComparison.Ordinal); Assert.Contains("Choose Play there", page, StringComparison.Ordinal);
        Assert.Contains("launch ticket", direct, StringComparison.Ordinal); Assert.Contains("no Roblox page opens", direct, StringComparison.Ordinal);
        Assert.All(new[] { page, direct }, text => Assert.Contains("does not verify its account or arrival", text, StringComparison.Ordinal));
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-launch-mode-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected synthetic fixture path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
