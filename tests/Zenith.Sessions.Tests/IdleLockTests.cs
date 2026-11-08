using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Optional idle lock: off by default; when chosen in Settings, the manager locks like Lock after that long without
// keyboard or mouse input in its windows.
public sealed class IdleLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-idle-lock-" + Guid.NewGuid().ToString("N"));
    private string Primary => Path.Combine(_root, "settings.json");
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public async Task TheIdleChoiceSurvivesLoadAndSaveAndOlderFilesStayOff()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"IdleLockMinutes\":15}");
        using (var store = new SettingsStore(_root))
        {
            var loaded = await store.LoadAsync(default);
            Assert.Null(loaded.Notice);
            await store.SaveAsync(loaded.Settings, default);
        }
        Assert.True(SavedIdleLockMinutes() == 15, "Saving settings dropped the chosen idle lock time.");

        // A schema-1 file from before this setting loads without a notice and keeps the lock off.
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"DenseAccounts\":true}");
        using (var store = new SettingsStore(_root))
        {
            var loaded = await store.LoadAsync(default);
            Assert.Null(loaded.Notice);
            await store.SaveAsync(loaded.Settings, default);
        }
        Assert.Equal(0, SavedIdleLockMinutes());
    }

    [Fact]
    public void SettingsOffersTheIdleLockChoiceInAppearanceAndTheGuideNamesIt()
    {
        var document = ProductionWindow();
        var choice = document.Descendants().SingleOrDefault(e => (string?)e.Attribute(X + "Name") == "SettingsIdleLock");
        Assert.True(choice is not null, "Settings has no control for locking the manager after idle time.");
        Assert.Equal("ComboBox", choice!.Name.LocalName);
        Assert.Equal("{Binding IdleLockChoices}", (string?)choice.Attribute("ItemsSource"));
        Assert.Equal("Minutes", (string?)choice.Attribute("SelectedValuePath"));
        Assert.Equal("{Binding IdleLockMinutes}", (string?)choice.Attribute("SelectedValue"));
        Assert.False(string.IsNullOrWhiteSpace((string?)choice.Attribute("AutomationProperties.Name")));
        // The choice sits in Appearance, before the Performance section, where a search result for it lands.
        var all = document.Descendants().ToList();
        int appearance = all.FindIndex(e => (string?)e.Attribute(X + "Name") == "SettingsAppearanceAnchor");
        int performance = all.FindIndex(e => (string?)e.Attribute(X + "Name") == "SettingsPerformanceAnchor");
        Assert.InRange(all.IndexOf(choice), appearance + 1, performance - 1);

        var guide = Assert.Single(document.Descendants().Where(e => ((string?)e.Attribute("Text"))?.Contains("Lock hides account controls", StringComparison.Ordinal) == true));
        Assert.Contains("Lock when idle", (string)guide.Attribute("Text")!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("auto lock")]
    [InlineData("inactivity")]
    public void SettingsSearchFindsTheIdleLockInAppearance(string query)
    {
        var entry = SettingsSearchCatalog.Search(query).FirstOrDefault(e => e.Id == "idle-lock");
        Assert.True(entry is not null, $"Searching settings for '{query}' does not find the idle lock.");
        Assert.Equal(SettingsSearchTarget.Appearance, entry!.Target);
    }

    [Fact]
    public void ThePolicyIsOffByDefaultAndLocksOnlyAfterTheWholeIdleTime()
    {
        var clock = new ManualClock();
        var policy = new IdleLockPolicy(clock);
        clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(IdleLockStep.Off, policy.Check(busy: false, out _));

        policy.Configure(15);
        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(IdleLockStep.Wait, policy.Check(busy: false, out var wait));
        Assert.Equal(TimeSpan.FromMinutes(1), wait);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(IdleLockStep.Lock, policy.Check(busy: false, out _));

        // Turning it off again never locks.
        policy.Configure(0);
        clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal(IdleLockStep.Off, policy.Check(busy: false, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Configure(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Configure(IdleLockPolicy.MaximumMinutes + 1));
    }

    [Fact]
    public void InputRestartsTheCountButAPointerRestingInPlaceDoesNot()
    {
        var clock = new ManualClock();
        var policy = new IdleLockPolicy(clock);
        policy.Configure(10);

        clock.Advance(TimeSpan.FromMinutes(8)); policy.RecordActivity();
        clock.Advance(TimeSpan.FromMinutes(8));
        Assert.Equal(IdleLockStep.Wait, policy.Check(busy: false, out var wait));
        Assert.Equal(TimeSpan.FromMinutes(2), wait);

        // The first pointer report and a moved pointer are input; WPF repeating a still pointer's position is not.
        policy.RecordPointer(40, 60);
        clock.Advance(TimeSpan.FromMinutes(9)); policy.RecordPointer(40, 60);
        Assert.Equal(IdleLockStep.Wait, policy.Check(busy: false, out wait));
        Assert.Equal(TimeSpan.FromMinutes(1), wait);
        policy.RecordPointer(41, 60);
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(IdleLockStep.Wait, policy.Check(busy: false, out wait));
        Assert.Equal(TimeSpan.FromMinutes(1), wait);
    }

    [Fact]
    public void WorkOutsideTheManagerWindowsCountsAsActivityAndTheTimerNeverSpins()
    {
        var clock = new ManualClock();
        var policy = new IdleLockPolicy(clock);
        policy.Configure(5);

        // A sign-in or native trial is under way in a page Zenith opened: no lock, and the count restarts from now.
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(IdleLockStep.Wait, policy.Check(busy: true, out var wait));
        Assert.Equal(TimeSpan.FromMinutes(5), wait);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(IdleLockStep.Wait, policy.Check(busy: false, out wait));
        Assert.Equal(TimeSpan.FromMinutes(1), wait);

        // A timer that wakes a moment early is asked to wait at least the minimum, never a tight loop.
        clock.Advance(TimeSpan.FromMinutes(1) - TimeSpan.FromMilliseconds(20));
        Assert.Equal(IdleLockStep.Wait, policy.Check(busy: false, out wait));
        Assert.Equal(IdleLockPolicy.MinimumWait, wait);
    }

    [Fact]
    public void SettingsOffersPlainChoicesAndKeepsAnUnlistedSavedTime()
    {
        var standard = IdleLockPolicy.ChoicesFor(0);
        Assert.Equal([0, 5, 10, 15, 30, 60], standard.Select(c => c.Minutes));
        Assert.Equal("Off", standard[0].Label);
        Assert.Equal("After 5 minutes", standard[1].Label);
        Assert.Same(standard, IdleLockPolicy.ChoicesFor(30));

        var kept = IdleLockPolicy.ChoicesFor(45);
        Assert.Equal([0, 5, 10, 15, 30, 45, 60], kept.Select(c => c.Minutes));
        Assert.Equal("After 1 minute", IdleLockPolicy.ChoicesFor(1)[1].Label);

        Assert.Throws<InvalidDataException>(() => new SessionsSettings(IdleLockMinutes: -1).Validated());
        Assert.Throws<InvalidDataException>(() => new SessionsSettings(IdleLockMinutes: IdleLockPolicy.MaximumMinutes + 1).Validated());
        Assert.Equal(IdleLockPolicy.MaximumMinutes, new SessionsSettings(IdleLockMinutes: IdleLockPolicy.MaximumMinutes).Validated().IdleLockMinutes);
    }

    [Fact]
    public Task TheSavedIdleChoiceLocksTheManagerAndUnlockStartsTheCountAgain() => OnDispatcherAsync(async () =>
    {
        var clock = new ManualClock();
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), clock: clock);
        try
        {
            await vm.InitializeAsync(); Assert.Equal("Settings saved", vm.SaveStatus);
            // Off by default: hours without input never lock and no timer is armed.
            Assert.Equal(0, vm.IdleLockMinutes); Assert.Null(vm.IdleLockDue);
            clock.Advance(TimeSpan.FromHours(5)); vm.CheckIdleLock(); Assert.False(vm.IsLocked);

            // An unsaved draft changes nothing yet.
            vm.IdleLockMinutes = 5; Assert.Equal("Unsaved settings", vm.SaveStatus);
            clock.Advance(TimeSpan.FromMinutes(10)); vm.CheckIdleLock();
            Assert.False(vm.IsLocked); Assert.Null(vm.IdleLockDue);

            // After Save settings one timer waits for the whole idle time.
            vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
            Assert.Equal("Settings saved", vm.SaveStatus); Assert.Equal(TimeSpan.FromMinutes(5), vm.IdleLockDue);
            using (var store = new SettingsStore(_root)) Assert.Equal(5, (await store.LoadAsync(default)).Settings.IdleLockMinutes);

            // Input restarts the count; when the timer wakes, it re-arms for the time still left.
            clock.Advance(TimeSpan.FromMinutes(4)); vm.NotePointer(120, 80);
            clock.Advance(TimeSpan.FromMinutes(4)); vm.NotePointer(120, 80); vm.CheckIdleLock();
            Assert.False(vm.IsLocked); Assert.Equal(TimeSpan.FromMinutes(1), vm.IdleLockDue);

            clock.Advance(TimeSpan.FromMinutes(1)); vm.CheckIdleLock();
            Assert.True(vm.IsLocked, "Five minutes without input did not lock the manager.");
            Assert.Contains("5 minutes without keyboard or mouse input", vm.Notice, StringComparison.Ordinal);
            Assert.Null(vm.IdleLockDue);
            Assert.Contains(vm.Events, e => e.Contains("5 minutes without keyboard or mouse input", StringComparison.Ordinal));

            // Unlocking starts a fresh count.
            clock.Advance(TimeSpan.FromMinutes(20));
            vm.Unlock.Execute(null);
            Assert.False(vm.IsLocked); Assert.Equal(TimeSpan.FromMinutes(5), vm.IdleLockDue);

            // Exit stops the timer.
            vm.PrepareForSessionEnd(TimeSpan.FromSeconds(2));
            Assert.Null(vm.IdleLockDue);
            clock.Advance(TimeSpan.FromMinutes(20)); vm.CheckIdleLock(); Assert.False(vm.IsLocked);
        }
        finally { await vm.DisposeAsync(); }

        // The next start reads the saved choice and arms the timer from startup.
        var reopenedClock = new ManualClock();
        await using var reopened = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), clock: reopenedClock);
        await reopened.InitializeAsync();
        Assert.Equal(5, reopened.IdleLockMinutes); Assert.Equal(TimeSpan.FromMinutes(5), reopened.IdleLockDue);
        Assert.Contains(reopened.IdleLockChoices, c => c.Minutes == 5);
        Assert.Equal("Settings saved", reopened.SaveStatus);
    });

    private int SavedIdleLockMinutes()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Primary));
        return json.RootElement.TryGetProperty("IdleLockMinutes", out var value) ? value.GetInt32() : 0;
    }

    private static XDocument ProductionWindow()
    {
        using var stream = typeof(IdleLockTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        return XDocument.Load(stream);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = 1;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
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
            || !Path.GetFileName(full).StartsWith("zenith-idle-lock-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
