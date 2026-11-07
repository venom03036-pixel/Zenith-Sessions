using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// The window's close button hides the manager to the tray by default; Settings can make it exit the manager instead.
public sealed class CloseButtonChoiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-close-button-" + Guid.NewGuid().ToString("N"));
    private string Primary => Path.Combine(_root, "settings.json");
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public async Task TheCloseButtonChoiceSurvivesLoadAndSaveAndOlderFilesKeepHideToTray()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"CloseButtonExits\":true}");
        using (var store = new SettingsStore(_root))
        {
            var loaded = await store.LoadAsync(default);
            Assert.Null(loaded.Notice);
            await store.SaveAsync(loaded.Settings, default);
        }
        Assert.True(SavedCloseButtonExits(), "Saving settings dropped the choice that the close button exits the manager.");

        // A schema-1 file from before this setting loads without a notice and keeps hide-to-tray.
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"DenseAccounts\":true}");
        using (var store = new SettingsStore(_root))
        {
            var loaded = await store.LoadAsync(default);
            Assert.Null(loaded.Notice);
            await store.SaveAsync(loaded.Settings, default);
        }
        Assert.False(SavedCloseButtonExits(), "A settings file without the choice must keep the default: the close button hides to the tray.");
    }

    [Fact]
    public void SettingsOffersTheChoiceAndTheCloseButtonNamesWhatItWillDo()
    {
        var document = ProductionWindow();
        var choice = document.Descendants().SingleOrDefault(e => (string?)e.Attribute(X + "Name") == "SettingsCloseButtonExits");
        Assert.True(choice is not null, "Settings has no control for what the close button does.");
        Assert.Equal("CheckBox", choice!.Name.LocalName);
        Assert.Equal("{Binding CloseButtonExits}", (string?)choice.Attribute("IsChecked"));
        Assert.False(string.IsNullOrWhiteSpace((string?)choice.Attribute("AutomationProperties.Name")));
        // The choice sits in Appearance, before the Performance section, where a search result for it lands.
        var all = document.Descendants().ToList();
        int appearance = all.FindIndex(e => (string?)e.Attribute(X + "Name") == "SettingsAppearanceAnchor");
        int performance = all.FindIndex(e => (string?)e.Attribute(X + "Name") == "SettingsPerformanceAnchor");
        Assert.InRange(all.IndexOf(choice), appearance + 1, performance - 1);

        var close = Assert.Single(document.Descendants().Where(e => e.Name.LocalName == "Button" && (string?)e.Attribute("Click") == "CloseWindow"));
        Assert.Equal("{Binding CloseButtonLabel}", (string?)close.Attribute("ToolTip"));
        Assert.Equal("{Binding CloseButtonLabel}", (string?)close.Attribute("AutomationProperties.Name"));

        // The Guide still documents hide-to-tray as the default, the single-click reopen, and where to change it.
        var guide = Assert.Single(document.Descendants().Where(e => ((string?)e.Attribute("Text"))?.Contains("keeps the manager in the tray", StringComparison.Ordinal) == true));
        string text = (string)guide.Attribute("Text")!;
        Assert.Contains("click the tray icon", text, StringComparison.Ordinal);
        Assert.Contains("Close button exits the manager", text, StringComparison.Ordinal);
    }

    [Fact]
    public Task TheCloseButtonFollowsTheSavedChoiceAndNeverStartsASecondExit() => OnDispatcherAsync(async () =>
    {
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor());
        try
        {
            await vm.InitializeAsync(); Assert.Equal("Settings saved", vm.SaveStatus);
            // Default: the close button hides the manager to the tray.
            Assert.False(vm.CloseButtonExits); Assert.False(vm.CloseButtonExitsNow); Assert.Equal("Close to tray", vm.CloseButtonLabel);

            // An unsaved draft changes nothing yet, and a theme autosave does not write it.
            vm.CloseButtonExits = true;
            Assert.Equal("Unsaved settings", vm.SaveStatus); Assert.False(vm.CloseButtonExitsNow); Assert.Equal("Close to tray", vm.CloseButtonLabel);
            vm.ApplyTheme.Execute("ocean-blue"); await vm.ApplyTheme.Execution;
            using (var store = new SettingsStore(_root)) Assert.False((await store.LoadAsync(default)).Settings.CloseButtonExits);
            Assert.Equal("Unsaved settings", vm.SaveStatus);

            // After Save settings the close button exits, and its name and tooltip say so.
            vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
            Assert.Equal("Settings saved", vm.SaveStatus); Assert.True(vm.CloseButtonExitsNow); Assert.Equal("Exit manager", vm.CloseButtonLabel);
            using (var store = new SettingsStore(_root))
            { var saved = (await store.LoadAsync(default)).Settings; Assert.True(saved.CloseButtonExits); Assert.Equal("ocean-blue", saved.Theme); }
            vm.CloseButtonExits = false; Assert.Equal("Unsaved settings", vm.SaveStatus); Assert.True(vm.CloseButtonExitsNow);
            vm.CloseButtonExits = true; Assert.Equal("Settings saved", vm.SaveStatus);

            // Once this manager is stopping (here Windows ending the session), a close never starts a second exit.
            vm.PrepareForSessionEnd(TimeSpan.FromSeconds(2));
            Assert.False(vm.CloseButtonExitsNow);
        }
        finally { await vm.DisposeAsync(); }

        // The next start reads the saved choice.
        await using var reopened = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor());
        await reopened.InitializeAsync();
        Assert.True(reopened.CloseButtonExits); Assert.True(reopened.CloseButtonExitsNow); Assert.Equal("Exit manager", reopened.CloseButtonLabel);
        Assert.Equal("Settings saved", reopened.SaveStatus);
    });

    [Theory]
    [InlineData("tray")]
    [InlineData("close button")]
    [InlineData("exit")]
    public void SettingsSearchFindsTheCloseButtonChoiceInAppearance(string query)
    {
        var entry = SettingsSearchCatalog.Search(query).FirstOrDefault(e => e.Id == "close-button");
        Assert.True(entry is not null, $"Searching settings for '{query}' does not find the close-button choice.");
        Assert.Equal(SettingsSearchTarget.Appearance, entry!.Target);
    }

    private bool SavedCloseButtonExits()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Primary));
        return json.RootElement.TryGetProperty("CloseButtonExits", out var value) && value.GetBoolean();
    }

    private static XDocument ProductionWindow()
    {
        using var stream = typeof(CloseButtonChoiceTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        return XDocument.Load(stream);
    }

    private sealed class SyntheticMonitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 20), [])); }
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
    }

    private static Task OnDispatcherAsync(Func<Task> work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            // A tracked delegate owns the async work; no window, tray icon or desktop input is created.
            Task operation = Task.CompletedTask;
            dispatcher.BeginInvoke(() => { operation = RunAsync(); });
            async Task RunAsync()
            {
                try { await work(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
            Dispatcher.Run(); operation.GetAwaiter().GetResult();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-close-button-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
