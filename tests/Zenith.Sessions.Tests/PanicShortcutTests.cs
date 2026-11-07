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
    [InlineData("f2", "F2", PanicShortcutModifiers.None, 0x71)]
    [InlineData("ctrl+f1", "Ctrl+F1", Ctrl, 0x70)]
    [InlineData("F24", "F24", PanicShortcutModifiers.None, 0x87)]
    [InlineData("ctrl+alt+shift+p", "Ctrl+Alt+Shift+P", Ctrl | Alt | Shift, 0x50)]
    [InlineData(" Shift + F9 ", "Shift+F9", Shift, 0x78)]
    [InlineData("Control+Shift+5", "Ctrl+Shift+5", Ctrl | Shift, 0x35)]
    [InlineData("Shift+Alt+Ctrl+z", "Ctrl+Alt+Shift+Z", Ctrl | Alt | Shift, 0x5A)]
    [InlineData("Ctrl+P", "Ctrl+P", Ctrl, 0x50)]
    [InlineData("Ctrl+0", "Ctrl+0", Ctrl, 0x30)]
    [InlineData("Ctrl+F10", "Ctrl+F10", Ctrl, 0x79)]
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
    [InlineData("P", "needs Ctrl, so ordinary typing")]
    [InlineData("7", "needs Ctrl, so ordinary typing")]
    [InlineData("Shift+P", "needs Ctrl, so ordinary typing")]
    // Editing commands in most programs, with or without Shift (Ctrl+Shift+C and V copy and paste in terminals).
    [InlineData("Ctrl+C", "copies, pastes")]
    [InlineData("ctrl+v", "copies, pastes")]
    [InlineData("Ctrl+X", "copies, pastes")]
    [InlineData("Ctrl+Z", "copies, pastes")]
    [InlineData("Ctrl+Y", "copies, pastes")]
    [InlineData("Ctrl+S", "copies, pastes")]
    [InlineData("Ctrl+A", "copies, pastes")]
    [InlineData("Ctrl+Shift+V", "copies, pastes")]
    [InlineData("Ctrl+Shift+Z", "copies, pastes")]
    // Menu access keys (Alt+Shift+letter is also a page shortcut in browsers).
    [InlineData("Alt+P", "Alt with a letter or digit opens menus")]
    [InlineData("Alt+0", "Alt with a letter or digit opens menus")]
    [InlineData("Alt+Shift+P", "Alt with a letter or digit opens menus")]
    // Ctrl+Alt is AltGr on many layouts: German AltGr+Q is @ and AltGr+7 is {; Polish AltGr+A is a letter.
    [InlineData("Ctrl+Alt+Q", "AltGr")]
    [InlineData("Ctrl+Alt+7", "AltGr")]
    [InlineData("Ctrl+Alt+A", "AltGr")]
    [InlineData("ctrl+alt+p", "AltGr")]
    [InlineData("F1", "opens Help")]
    [InlineData("F10", "open the menu bar")]
    [InlineData("Shift+F10", "open the menu bar")]
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
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"PanicShortcut\":\"Ctrl+Alt+Shift+P\"}");
        using (var store = new SettingsStore(_root))
        {
            var loaded = await store.LoadAsync(default);
            Assert.Null(loaded.Notice);
            Assert.Equal("Ctrl+Alt+Shift+P", loaded.Settings.PanicShortcut);
            await store.SaveAsync(loaded.Settings, default);
        }
        Assert.Equal("Ctrl+Alt+Shift+P", SavedShortcut());

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

    [Theory]
    [InlineData("Ctrl+Alt+Q", "AltGr")]
    [InlineData("Ctrl+C", "copies, pastes")]
    [InlineData("Alt+0", "opens menus")]
    [InlineData("F1", "opens Help")]
    public async Task ASavedShortcutEarlierVersionsAllowedLoadsAsF12WithANoticeAndTheOtherSettingsStay(string retired, string reason)
    {
        // Earlier versions accepted these. A file holding one keeps its other settings instead of being treated as damaged.
        Assert.True(PanicShortcut.IsNoLongerAllowed(retired), $"'{retired}' is not recognized as a shortcut earlier versions allowed.");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"IdleLockMinutes\":15,\"PanicShortcut\":\"" + retired + "\"}");
        using (var store = new SettingsStore(_root))
        {
            var loaded = await store.LoadAsync(default);
            Assert.Equal("F12", loaded.Settings.PanicShortcut);
            Assert.Equal("ocean-blue", loaded.Settings.Theme);
            Assert.Equal(15, loaded.Settings.IdleLockMinutes);
            Assert.NotNull(loaded.Notice);
            Assert.Contains("The saved panic shortcut is no longer allowed, so F12 is used", loaded.Notice, StringComparison.Ordinal);
            Assert.Contains(reason, loaded.Notice, StringComparison.Ordinal);
            await store.SaveAsync(loaded.Settings, default);
        }
        // Saving repairs the file normally: the old copy becomes the backup, not a damaged original.
        Assert.Equal("F12", SavedShortcut());
        Assert.False(File.Exists(Primary + ".damaged"), "A file holding an earlier allowed shortcut was preserved as damaged.");
        using (var store = new SettingsStore(_root)) Assert.Null((await store.LoadAsync(default)).Notice);
    }

    [Fact]
    public void OnlyShortcutsEarlierVersionsAllowedCountAsNoLongerAllowed()
    {
        Assert.False(PanicShortcut.IsNoLongerAllowed("Ctrl+Alt+Shift+Q"));
        Assert.False(PanicShortcut.IsNoLongerAllowed("F12"));
        // Never allowed: a saved file holding one is still invalid.
        Assert.False(PanicShortcut.IsNoLongerAllowed("P"));
        Assert.False(PanicShortcut.IsNoLongerAllowed("Win+F12"));
        Assert.False(PanicShortcut.IsNoLongerAllowed("Alt+F4"));
        Assert.False(PanicShortcut.IsNoLongerAllowed(null));
        Assert.Throws<InvalidDataException>(() => new SessionsSettings(PanicShortcut: "Ctrl+Alt+Q").Validated());
    }

    [Fact]
    public Task TheManagerSaysWhyAnEarlierAllowedShortcutWasReplacedAndRegistersF12() => OnDispatcherAsync(async () =>
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"PanicShortcut\":\"Ctrl+Alt+Q\"}");
        var registrar = new FakeRegistrar();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor());
        vm.AttachGlobalPanicShortcut(registrar.Register);
        await vm.InitializeAsync();
        Assert.Equal(["F12"], registrar.Attempts);
        Assert.Equal("F12", vm.PanicShortcutText);
        Assert.Contains("The saved panic shortcut is no longer allowed", vm.Notice, StringComparison.Ordinal);
        Assert.Equal("Recovered settings — save to repair", vm.SaveStatus);
    });

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
            vm.PanicShortcutText = "ctrl+alt+shift+p";
            Assert.Equal("Unsaved settings", vm.SaveStatus);
            Assert.Single(registrar.Attempts);

            vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
            Assert.Equal("Settings saved", vm.SaveStatus);
            Assert.Equal(["F12", "Ctrl+Alt+Shift+P"], registrar.Attempts);
            Assert.Equal("Ctrl+Alt+Shift+P", SavedShortcut());
            Assert.Equal("Ctrl+Alt+Shift+P", vm.PanicShortcutText);
            Assert.Equal("Panic stop · Ctrl+Alt+Shift+P", vm.PanicLabel);
            Assert.Equal("Ctrl+Alt+Shift+P", vm.PanicKeyCap);
            Assert.Equal("Ctrl+Alt+Shift+P is registered globally for this manager. F12 also works while this window is focused.", vm.PanicShortcutStatus);

            // An unsafe draft is refused with its reason and changes nothing.
            vm.PanicShortcutText = "P";
            vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
            Assert.Contains("needs Ctrl", vm.ErrorNotice, StringComparison.Ordinal);
            Assert.Equal("Ctrl+Alt+Shift+P", SavedShortcut());
            Assert.Equal(2, registrar.Attempts.Count);
            Assert.Equal("Unsaved settings", vm.SaveStatus);

            // Windows refuses a combination another program holds: the choice is saved and the working shortcut stays.
            registrar.Refused.Add("Ctrl+Alt+Shift+Q");
            vm.PanicShortcutText = "Ctrl+Alt+Shift+Q";
            vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
            Assert.Equal("Ctrl+Alt+Shift+Q", SavedShortcut());
            Assert.Equal(["F12", "Ctrl+Alt+Shift+P", "Ctrl+Alt+Shift+Q"], registrar.Attempts);
            Assert.Equal("Panic stop · Ctrl+Alt+Shift+P", vm.PanicLabel);
            Assert.Contains("Ctrl+Alt+Shift+Q is unavailable", vm.PanicShortcutStatus, StringComparison.Ordinal);
            Assert.Contains("Ctrl+Alt+Shift+P stays registered globally", vm.PanicShortcutStatus, StringComparison.Ordinal);
            Assert.Contains("Ctrl+Alt+Shift+Q is unavailable", vm.Notice, StringComparison.Ordinal);

            // A theme autosave does not retry the refused shortcut; Save settings does.
            vm.ApplyTheme.Execute("ocean-blue"); await vm.ApplyTheme.Execution;
            Assert.Equal(3, registrar.Attempts.Count);
            registrar.Refused.Clear();
            vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
            Assert.Equal(["F12", "Ctrl+Alt+Shift+P", "Ctrl+Alt+Shift+Q", "Ctrl+Alt+Shift+Q"], registrar.Attempts);
            Assert.Equal("Panic stop · Ctrl+Alt+Shift+Q", vm.PanicLabel);
        }
        finally { await vm.DisposeAsync(); }

        // The next start registers the saved choice directly, without taking F12 first.
        var next = new FakeRegistrar();
        await using var reopened = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor());
        reopened.AttachGlobalPanicShortcut(next.Register);
        await reopened.InitializeAsync();
        Assert.Equal(["Ctrl+Alt+Shift+Q"], next.Attempts);
        Assert.Equal("Ctrl+Alt+Shift+Q", reopened.PanicShortcutText);
        Assert.Equal("Panic stop · Ctrl+Alt+Shift+Q", reopened.PanicLabel);
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
        // The help names the combinations that are refused because they type or edit in other programs.
        string? help = all.Skip(appearance).Take(performance - appearance).Select(e => (string?)e.Attribute("Text"))
            .FirstOrDefault(text => text?.StartsWith("F12 by default.", StringComparison.Ordinal) == true);
        Assert.True(help is not null, "Settings has no help text for the panic shortcut.");
        Assert.Contains("AltGr", help, StringComparison.Ordinal);
        Assert.Contains("Ctrl+C", help, StringComparison.Ordinal);
        Assert.Contains("every program", help, StringComparison.Ordinal);

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
