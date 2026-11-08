using System.IO;
using System.Reflection;
using System.Xml.Linq;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// R-launch-2 (owner decision 2026-11-08): the saved "Shorten the wait between starts when the previous client reports it
// joined" preference, off by default. Through the real sample and presentation path with a synthetic log snapshot, a
// started client's Connected report ends the startup stabilization wait down to the launch spacing only once the
// preference is saved on. The queue's gate is seeded by the same private call the queue uses after a start, with the
// receipt the Shell would otherwise record; no provider, account, browser or client is involved.
public sealed partial class ClientWindowIntegrationTests
{
    [Fact]
    public Task TheWaitAfterAStartEndsEarlyOnAJoinReportOnlyWhenTheSettingIsSavedOn() => OnUi(async () =>
    {
        var monitor = new Monitor();
        await using var shell = await Ready(monitor);
        Assert.False(shell.ShortenWaitAfterJoin);
        var log = new ArrivalLog(shell, monitor);
        await log.StartedWithNoClientsAsync();
        var client = log.Client(600, minutesAgo: 1);
        var queue = Assert.IsType<LaunchCoordinator>(ReportedField(shell, "_launchQueue").GetValue(shell));
        var operation = Guid.NewGuid();
        typeof(LaunchCoordinator).GetMethod("BeginStartupPacing", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(queue, [operation, TimeSpan.FromSeconds(60), LaunchPacingReason.StartupStabilization, TimeSpan.FromSeconds(15), 600, client.ProcessStartUtcTicks]);
        // Off (the default): the client's own Connected report leaves the full stabilization wait in place.
        await log.PresentAsync([client], (client, [ClientLogFact.Joining, ClientLogFact.Connected]));
        var pacing = queue.ReadPacing();
        Assert.Equal(LaunchPacingReason.StartupStabilization, pacing.Reason); Assert.InRange(pacing.Remaining, TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60));
        Assert.Equal(operation, pacing.OperationId);
        Assert.Contains("for startup stabilization", shell.LaunchPacingStatus);
        Assert.DoesNotContain(shell.Events, line => line.Contains("reported it joined", StringComparison.Ordinal));
        // Saved on: the same report, still current, ends the wait down to the spacing floor and says so once.
        shell.ShortenWaitAfterJoin = true; Assert.Equal("Unsaved settings", shell.SaveStatus);
        shell.SaveSettings.Execute(null); await shell.SaveSettings.Execution;
        Assert.Equal("Settings saved", shell.SaveStatus);
        await log.PresentAsync([client]);
        pacing = queue.ReadPacing();
        Assert.Equal(LaunchPacingReason.BetweenStarts, pacing.Reason); Assert.InRange(pacing.Remaining, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
        Assert.Contains("for launch spacing", shell.LaunchPacingStatus);
        Assert.Single(shell.Events, line => line.Contains("reported it joined", StringComparison.Ordinal));
        await log.PresentAsync([client]);
        Assert.Single(shell.Events, line => line.Contains("reported it joined", StringComparison.Ordinal));
        Assert.Contains("\"ShortenWaitAfterJoin\":true", await File.ReadAllTextAsync(Path.Combine(_root, "settings.json")), StringComparison.Ordinal);
        Assert.DoesNotContain(shell.Events, line => line.Contains("Operation failed", StringComparison.Ordinal));
    });
}

public sealed class LaunchJoinPacingSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-join-pacing-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_root, "settings.json");
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public async Task TheSettingIsOffByDefaultAndASchemaOneFileFromBeforeItLoadsWithoutANotice()
    {
        Assert.False(new SessionsSettings().Validated().ShortenWaitAfterJoin);
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(SettingsPath, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"DenseAccounts\":true}");
        using (var store = new SettingsStore(_root))
        {
            var loaded = await store.LoadAsync(default);
            Assert.Null(loaded.Notice); Assert.False(loaded.Settings.ShortenWaitAfterJoin); Assert.Equal("ocean-blue", loaded.Settings.Theme);
            await store.SaveAsync(loaded.Settings with { ShortenWaitAfterJoin = true }, default);
        }
        Assert.Contains("\"ShortenWaitAfterJoin\":true", await File.ReadAllTextAsync(SettingsPath), StringComparison.Ordinal);
        using (var store = new SettingsStore(_root))
        {
            var reloaded = await store.LoadAsync(default);
            Assert.Null(reloaded.Notice); Assert.True(reloaded.Settings.ShortenWaitAfterJoin);
        }
    }

    [Theory]
    [InlineData("joined")]
    [InlineData("shorten wait")]
    [InlineData("next start")]
    public void SettingsSearchFindsTheOptionUnderThePerformanceSectionAndTheOptionIsACheckBoxBoundToTheDraft(string query)
    {
        var entry = Assert.Single(SettingsSearchCatalog.Search(query), entry => entry.Id == "shorten-wait-after-join");
        Assert.Equal(SettingsSearchTarget.Performance, entry.Target);
        Assert.Equal("Settings", SettingsSearchCatalog.PageOf(entry.Target));
        Assert.Contains("off by default", entry.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never before the launch spacing", entry.Description, StringComparison.OrdinalIgnoreCase);
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProductionMainWindow.xaml"); Assert.NotNull(source);
        var document = XDocument.Load(source!);
        var option = Assert.Single(document.Descendants().Where(e => (string?)e.Attribute(X + "Name") == "ShortenWaitAfterJoinOption"));
        Assert.Equal("CheckBox", option.Name.LocalName);
        Assert.Equal("{Binding ShortenWaitAfterJoin}", (string?)option.Attribute("IsChecked"));
        // It sits in the Performance profile panel, under the same anchor its search result scrolls to.
        Assert.Contains(option.Ancestors(), ancestor => ancestor.Descendants().Any(e => (string?)e.Attribute(X + "Name") == "SettingsPerformanceAnchor")
            && ancestor.Name.LocalName == "StackPanel");
    }

    [Fact]
    public void TheGuideSaysTheWaitEndsEarlyOnlyWhenChosenNeverBelowTheSpacingAndGrantsNoPermission()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProductionMainWindow.xaml")!;
        var document = XDocument.Load(stream);
        var guide = Assert.Single(document.Descendants().Where(e => e.Name.LocalName == "ScrollViewer"
            && ((string?)e.Attribute("Visibility"))?.Contains("IsGuide", StringComparison.Ordinal) == true));
        var texts = guide.Descendants().Where(e => e.Name.LocalName == "TextBlock").Select(e => (string?)e.Attribute("Text") ?? "").ToList();
        string pacing = Assert.Single(texts, text => text.Contains("reports it joined", StringComparison.Ordinal));
        // The same paragraph that explains the refusal backoff, so the two pacing rules are read together.
        Assert.Contains("twice the Launch spacing", pacing, StringComparison.Ordinal);
        Assert.Contains("it is off by default", pacing, StringComparison.Ordinal);
        Assert.Contains("never before the Launch spacing has passed since its start and never later than the full stabilization", pacing, StringComparison.Ordinal);
        Assert.Contains("That report is diagnostic: it grants no permission", pacing, StringComparison.Ordinal);
        Assert.Contains("without a report the full wait stays", pacing, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-join-pacing-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected synthetic fixture path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
