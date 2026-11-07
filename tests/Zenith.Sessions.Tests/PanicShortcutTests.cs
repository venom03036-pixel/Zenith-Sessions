using System.IO;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// The global panic stop shortcut is F12 by default. Settings can change it to another safe combination; the focused F12
// binding and the Panic button stay as fallbacks.
public sealed class PanicShortcutTests : IDisposable
{
    private const PanicShortcutModifiers Ctrl = PanicShortcutModifiers.Control, Alt = PanicShortcutModifiers.Alt, Shift = PanicShortcutModifiers.Shift;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-panic-shortcut-" + Guid.NewGuid().ToString("N"));
    private string Primary => Path.Combine(_root, "settings.json");
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("F12", "F12", PanicShortcutModifiers.None, 0x7B)]
    [InlineData("f1", "F1", PanicShortcutModifiers.None, 0x70)]
    [InlineData("F24", "F24", PanicShortcutModifiers.None, 0x87)]
    [InlineData("ctrl+alt+p", "Ctrl+Alt+P", Ctrl | Alt, 0x50)]
    [InlineData(" Shift + F9 ", "Shift+F9", Shift, 0x78)]
    [InlineData("Control+Shift+5", "Ctrl+Shift+5", Ctrl | Shift, 0x35)]
    [InlineData("Shift+Alt+Ctrl+z", "Ctrl+Alt+Shift+Z", Ctrl | Alt | Shift, 0x5A)]
    [InlineData("Alt+0", "Alt+0", Alt, 0x30)]
    [InlineData("Alt+Shift+F4", "Alt+Shift+F4", Alt | Shift, 0x73)]
    public void AcceptedShortcutsReadAsOneCanonicalForm(string text, string canonical, PanicShortcutModifiers modifiers, int key)
    {
        Assert.True(PanicShortcut.TryParse(text, out var shortcut, out string problem), problem);
        Assert.Equal(canonical, shortcut!.ToString());
        Assert.Equal(modifiers, shortcut.Modifiers);
        Assert.Equal(key, shortcut.VirtualKey);
        Assert.True(PanicShortcut.TryParse(canonical, out var again, out _));
        Assert.Equal(shortcut, again);
    }

    [Theory]
    [InlineData(null, "Enter a panic shortcut")]
    [InlineData("  ", "Enter a panic shortcut")]
    [InlineData("P", "needs Ctrl or Alt")]
    [InlineData("7", "needs Ctrl or Alt")]
    [InlineData("Shift+P", "needs Ctrl or Alt")]
    [InlineData("Win+F12", "Windows key")]
    [InlineData("Ctrl+Windows+P", "Windows key")]
    [InlineData("Alt+F4", "Alt+F4 closes windows")]
    [InlineData("Ctrl+Alt", "exactly one key")]
    [InlineData("Ctrl+P+Q", "exactly one key")]
    [InlineData("Ctrl++P", "joined by +")]
    [InlineData("Ctrl+Space", "F1 to F24, a letter or a digit")]
    [InlineData("F25", "F1 to F24, a letter or a digit")]
    [InlineData("F0", "F1 to F24, a letter or a digit")]
    [InlineData("Ctrl+Alt+Delete", "F1 to F24, a letter or a digit")]
    public void UnsafeOrUnknownShortcutsAreRefusedWithAPlainReason(string? text, string reason)
    {
        Assert.False(PanicShortcut.TryParse(text, out var shortcut, out string problem), $"'{text}' was accepted as a panic shortcut.");
        Assert.Null(shortcut);
        Assert.Contains(reason, problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSavedShortcutSurvivesLoadAndSaveAndOlderFilesKeepF12()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"PanicShortcut\":\"Ctrl+Alt+P\"}");
        using (var store = new SettingsStore(_root))
        {
            var loaded = await store.LoadAsync(default);
            Assert.Null(loaded.Notice);
            Assert.Equal("Ctrl+Alt+P", loaded.Settings.PanicShortcut);
            await store.SaveAsync(loaded.Settings, default);
        }
        Assert.Equal("Ctrl+Alt+P", SavedShortcut());

        // A schema-1 file from before this setting loads without a notice and keeps F12.
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"Theme\":\"ocean-blue\"}");
        using (var store = new SettingsStore(_root))
        {
            var loaded = await store.LoadAsync(default);
            Assert.Null(loaded.Notice);
            Assert.Equal("F12", loaded.Settings.PanicShortcut);
        }
        Assert.Throws<InvalidDataException>(() => new SessionsSettings(PanicShortcut: "P").Validated());
        Assert.Throws<InvalidDataException>(() => new SessionsSettings(PanicShortcut: "Win+F12").Validated());
        Assert.Throws<InvalidDataException>(() => new SessionsSettings(PanicShortcut: null!).Validated());
    }

    [Fact]
    public Task WindowsGivesAnUnusualCombinationToOneWindowAndAReplacedOneIsReleased() => OnStaAsync(() =>
    {
        var rare = Shortcut("Ctrl+Alt+Shift+F21");
        var other = Shortcut("Ctrl+Alt+Shift+F22");
        using var firstWindow = MessageOnlyWindow();
        using var secondWindow = MessageOnlyWindow();
        using (var first = new GlobalPanicRegistration())
        using (var second = new GlobalPanicRegistration())
        {
            Assert.True(first.Register(firstWindow.Handle, rare), "Windows refused an unusual combination for the first window.");
            Assert.Equal(rare, first.Active);
            Assert.True(GlobalPanicRegistration.IsPanic(first.Identifier));
            Assert.True(first.Register(firstWindow.Handle, rare), "Asking again for the shortcut a window already holds must keep it.");
            Assert.False(second.Register(secondWindow.Handle, rare), "Windows let a second window register the same global shortcut.");
            Assert.Null(second.Active);
            Assert.Throws<InvalidOperationException>(() => first.Register(secondWindow.Handle, other));

            // A change registers the new combination first, then releases the old one for others.
            int before = first.Identifier;
            Assert.True(first.Register(firstWindow.Handle, other));
            Assert.Equal(other, first.Active);
            Assert.NotEqual(before, first.Identifier);
            Assert.True(GlobalPanicRegistration.IsPanic(first.Identifier) && GlobalPanicRegistration.IsPanic(before));
            Assert.True(second.Register(secondWindow.Handle, rare), "The replaced shortcut was not released.");
        }
        // Disposing released both combinations.
        using var third = new GlobalPanicRegistration();
        Assert.True(third.Register(firstWindow.Handle, other), "Disposing did not release the shortcut.");
        third.Dispose();
        Assert.Null(third.Active);
        Assert.Throws<ObjectDisposedException>(() => third.Register(firstWindow.Handle, rare));
    });

    [Fact]
    public Task TheSavedShortcutIsRegisteredAfterSaveAndARefusedChoiceKeepsTheWorkingOne() => OnDispatcherAsync(async () =>
    {
        var registrar = new FakeRegistrar();
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor());
        try
        {
            // Nothing is registered before the saved choice is known, so a different saved choice never takes F12 first.
            vm.AttachGlobalPanicShortcut(registrar.Register);
            Assert.Empty(registrar.Attempts);
            await vm.InitializeAsync();
            Assert.Equal(["F12"], registrar.Attempts);
            Assert.Equal("F12", vm.PanicShortcutText);
            Assert.Equal("Settings saved", vm.SaveStatus);
            Assert.Equal("Panic stop · F12", vm.PanicLabel);
            Assert.Equal("F12", vm.PanicKeyCap);
            Assert.Equal("F12 is registered globally for this manager.", vm.PanicShortcutStatus);

            // An unsaved draft registers nothing.
            vm.PanicShortcutText = "ctrl+alt+p";
            Assert.Equal("Unsaved settings", vm.SaveStatus);
            Assert.Single(registrar.Attempts);

            vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
            Assert.Equal("Settings saved", vm.SaveStatus);
            Assert.Equal(["F12", "Ctrl+Alt+P"], registrar.Attempts);
            Assert.Equal("Ctrl+Alt+P", SavedShortcut());
            Assert.Equal("Ctrl+Alt+P", vm.PanicShortcutText);
            Assert.Equal("Panic stop · Ctrl+Alt+P", vm.PanicLabel);
            Assert.Equal("Ctrl+Alt+P", vm.PanicKeyCap);
            Assert.Equal("Ctrl+Alt+P is registered globally for this manager. F12 also works while this window is focused.", vm.PanicShortcutStatus);

            // An unsafe draft is refused with its reason and changes nothing.
            vm.PanicShortcutText = "P";
            vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
            Assert.Contains("needs Ctrl or Alt", vm.ErrorNotice, StringComparison.Ordinal);
            Assert.Equal("Ctrl+Alt+P", SavedShortcut());
            Assert.Equal(2, registrar.Attempts.Count);
            Assert.Equal("Unsaved settings", vm.SaveStatus);

            // Windows refuses a combination another program holds: the choice is saved and the working shortcut stays.
            registrar.Refused.Add("Ctrl+Alt+Q");
            vm.PanicShortcutText = "Ctrl+Alt+Q";
            vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
            Assert.Equal("Ctrl+Alt+Q", SavedShortcut());
            Assert.Equal(["F12", "Ctrl+Alt+P", "Ctrl+Alt+Q"], registrar.Attempts);
            Assert.Equal("Panic stop · Ctrl+Alt+P", vm.PanicLabel);
            Assert.Contains("Ctrl+Alt+Q is unavailable", vm.PanicShortcutStatus, StringComparison.Ordinal);
            Assert.Contains("Ctrl+Alt+P stays registered globally", vm.PanicShortcutStatus, StringComparison.Ordinal);
            Assert.Contains("Ctrl+Alt+Q is unavailable", vm.Notice, StringComparison.Ordinal);

            // A theme autosave does not retry the refused shortcut; Save settings does.
            vm.ApplyTheme.Execute("ocean-blue"); await vm.ApplyTheme.Execution;
            Assert.Equal(3, registrar.Attempts.Count);
            registrar.Refused.Clear();
            vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
            Assert.Equal(["F12", "Ctrl+Alt+P", "Ctrl+Alt+Q", "Ctrl+Alt+Q"], registrar.Attempts);
            Assert.Equal("Panic stop · Ctrl+Alt+Q", vm.PanicLabel);
        }
        finally { await vm.DisposeAsync(); }

        // The next start registers the saved choice directly, without taking F12 first.
        var next = new FakeRegistrar();
        await using var reopened = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor());
        reopened.AttachGlobalPanicShortcut(next.Register);
        await reopened.InitializeAsync();
        Assert.Equal(["Ctrl+Alt+Q"], next.Attempts);
        Assert.Equal("Ctrl+Alt+Q", reopened.PanicShortcutText);
        Assert.Equal("Panic stop · Ctrl+Alt+Q", reopened.PanicLabel);
    });

    [Fact]
    public void SettingsOffersTheShortcutAndTheHeaderShowsTheRegisteredOne()
    {
        var document = ProductionWindow();
        var field = document.Descendants().SingleOrDefault(e => (string?)e.Attribute(X + "Name") == "SettingsPanicShortcut");
        Assert.True(field is not null, "Settings has no field for the panic shortcut.");
        Assert.Equal("TextBox", field!.Name.LocalName);
        Assert.Equal("{Binding PanicShortcutText, UpdateSourceTrigger=PropertyChanged}", (string?)field.Attribute("Text"));
        Assert.False(string.IsNullOrWhiteSpace((string?)field.Attribute("AutomationProperties.Name")));
        var all = document.Descendants().ToList();
        int appearance = all.FindIndex(e => (string?)e.Attribute(X + "Name") == "SettingsAppearanceAnchor");
        int performance = all.FindIndex(e => (string?)e.Attribute(X + "Name") == "SettingsPerformanceAnchor");
        Assert.InRange(all.IndexOf(field), appearance + 1, performance - 1);
        // Settings shows whether Windows accepted the saved choice.
        Assert.Contains(all.Skip(appearance).Take(performance - appearance), e => (string?)e.Attribute("Text") == "{Binding PanicShortcutStatus}");

        // The header key cap shows the registered shortcut; the focused F12 binding stays as a fallback.
        var keyCap = Assert.Single(document.Descendants().Where(e => e.Name.LocalName == "Border" && (string?)e.Attribute("ToolTip") == "Panic stop shortcut"));
        Assert.Equal("{Binding PanicKeyCap}", (string?)keyCap.Descendants().Single(e => e.Name.LocalName == "TextBlock").Attribute("Text"));
        Assert.Single(document.Descendants().Where(e => e.Name.LocalName == "KeyBinding" && (string?)e.Attribute("Key") == "F12" && (string?)e.Attribute("Command") == "{Binding Panic}"));

        // The Guide says where to change it.
        Assert.Single(document.Descendants().Where(e => ((string?)e.Attribute("Text"))?.Contains("Settings → Panic shortcut", StringComparison.Ordinal) == true));
    }

    [Theory]
    [InlineData("panic")]
    [InlineData("hotkey")]
    [InlineData("emergency stop")]
    [InlineData("F12")]
    public void SettingsSearchFindsThePanicShortcutInAppearance(string query)
    {
        var entry = SettingsSearchCatalog.Search(query).FirstOrDefault(e => e.Id == "panic-shortcut");
        Assert.True(entry is not null, $"Searching settings for '{query}' does not find the panic shortcut.");
        Assert.Equal(SettingsSearchTarget.Appearance, entry!.Target);
    }

    private static PanicShortcut Shortcut(string text)
    { Assert.True(PanicShortcut.TryParse(text, out var shortcut, out string problem), problem); return shortcut!; }

    private string? SavedShortcut()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Primary));
        return json.RootElement.TryGetProperty("PanicShortcut", out var value) ? value.GetString() : null;
    }

    private static XDocument ProductionWindow()
    {
        using var stream = typeof(PanicShortcutTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        return XDocument.Load(stream);
    }

    // A message-only window (parent HWND_MESSAGE) is never shown and receives no desktop input.
    private static HwndSource MessageOnlyWindow() => new(new HwndSourceParameters("Zenith panic shortcut test") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });

    private sealed class FakeRegistrar
    {
        public List<string> Attempts { get; } = [];
        public HashSet<string> Refused { get; } = [];
        public bool Register(PanicShortcut shortcut) { Attempts.Add(shortcut.ToString()); return !Refused.Contains(shortcut.ToString()); }
    }

    private sealed class SyntheticMonitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 20), [])); }
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
    }

    private static Task OnStaAsync(Action work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { work(); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
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
            || !Path.GetFileName(full).StartsWith("zenith-panic-shortcut-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
