using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class ManagerWindowPlacementTests : IDisposable
{
    private const double MinimumWidth = 960, MinimumHeight = 640;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-manager-window-" + Guid.NewGuid().ToString("N"));
    private string Primary => Path.Combine(_root, "settings.json");
    // Synthetic displays in physical pixels: a 100% primary and a 150% display to its left (negative coordinates).
    private static readonly ClientDisplay Primary100 = new("\\\\.\\DISPLAY1", "Primary", new(0, 0, 1920, 1040), 100, true);
    private static readonly ClientDisplay Left150 = new("\\\\.\\DISPLAY2", "Left", new(-2560, 0, 2560, 1380), 150, false);

    [Fact]
    public async Task SavedManagerWindowPlacementSurvivesLoadAndAnUnrelatedSave()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Primary,
            "{\"Version\":1,\"Theme\":\"graphite-mint\",\"ManagerWindow\":{\"Left\":-1800,\"Top\":120,\"Width\":1440,\"Height\":960,\"Maximized\":true}}");
        using var store = new SettingsStore(_root);
        var loaded = (await store.LoadAsync(default)).Settings;
        await store.SaveAsync(loaded with { Theme = "ocean-blue" }, default);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Primary));
        Assert.True(json.RootElement.TryGetProperty("ManagerWindow", out var window) && window.ValueKind == JsonValueKind.Object,
            "The saved manager window placement was dropped by a settings save.");
        Assert.Equal(-1800, window.GetProperty("Left").GetInt32()); Assert.Equal(960, window.GetProperty("Height").GetInt32());
        Assert.True(window.GetProperty("Maximized").GetBoolean());
        Assert.Equal(new ManagerWindowPlacement(-1800, 120, 1440, 960, true), (await store.LoadAsync(default)).Settings.ManagerWindow);
    }

    [Fact]
    public async Task SchemaOneSettingsWithoutAPlacementStillLoadWithoutANotice()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"DenseAccounts\":true}");
        using var store = new SettingsStore(_root);
        var loaded = await store.LoadAsync(default);
        Assert.Null(loaded.Notice); Assert.Null(loaded.Settings.ManagerWindow); Assert.Equal("ocean-blue", loaded.Settings.Theme);
        Assert.Null(store.ReadManagerWindow());
        await store.SaveAsync(loaded.Settings, default);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Primary));
        Assert.Equal(1, json.RootElement.GetProperty("Version").GetInt32());
    }

    [Theory]
    [InlineData(0, 0, 0, 840)]
    [InlineData(0, 0, 1320, -1)]
    [InlineData(10_000_000, 0, 1320, 840)]
    [InlineData(0, -10_000_000, 1320, 840)]
    public void AnOutOfRangePlacementIsRejectedLikeAnyOtherInvalidSetting(int left, int top, int width, int height)
        => Assert.Throws<InvalidDataException>(() => new SessionsSettings(ManagerWindow: new(left, top, width, height)).Validated());

    [Fact]
    public async Task ThePreShowReadNeverCreatesOrRepairsSettings()
    {
        using (var empty = new SettingsStore(_root)) Assert.Null(empty.ReadManagerWindow());
        Assert.False(Directory.Exists(_root));
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Primary, "damaged settings");
        using (var damaged = new SettingsStore(_root)) Assert.Null(damaged.ReadManagerWindow());
        Assert.Equal("damaged settings", await File.ReadAllTextAsync(Primary));
        Assert.Equal(new[] { "settings.json" }, Directory.GetFiles(_root).Select(path => Path.GetFileName(path)));
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"ManagerWindow\":{\"Left\":40,\"Top\":30,\"Width\":1400,\"Height\":900,\"Maximized\":false}}");
        using var store = new SettingsStore(_root);
        Assert.Equal(new ManagerWindowPlacement(40, 30, 1400, 900), store.ReadManagerWindow());
    }

    public static TheoryData<string, ManagerWindowPlacement, bool> Placements => new()
    {
        { "on the primary display", new(100, 80, 1400, 900), true },
        { "maximized, kept with its normal bounds", new(100, 80, 1400, 900, true), true },
        { "on a display left of the primary, at negative coordinates", new(-2400, 60, 1600, 1000), true },
        { "at least the minimum size at 150%", new(-2400, 60, 1440, 960), true },
        { "the default size, below the minimum at 150%", new(-2400, 60, 1320, 840), false },
        { "below the minimum size at 100%", new(100, 80, 900, 600), false },
        { "on a display that is no longer connected", new(3000, 100, 1400, 900), false },
        { "with its header above the top of every display", new(100, -40, 1400, 900), false },
        { "with less than 120 DIPs of its header on a display", new(1820, 100, 1400, 900), false },
        { "below the bottom of every display", new(100, 1030, 1400, 900), false },
    };

    [Theory]
    [MemberData(nameof(Placements))]
    public void ASavedPlacementIsKeptOnlyWhenItIsReachableAndAtLeastTheMinimumSize(string description, ManagerWindowPlacement saved, bool kept)
    {
        var resolved = ManagerWindowPlacementPolicy.Resolve(saved, [Primary100, Left150], MinimumWidth, MinimumHeight);
        Assert.True(kept == (resolved is not null), $"A placement {description} was {(kept ? "dropped" : "kept")}.");
        if (kept) Assert.Equal(saved, resolved);
    }

    [Fact]
    public void WithoutASavedPlacementOrAnyConfirmedDisplayTheWindowOpensAtItsDefault()
    {
        Assert.Null(ManagerWindowPlacementPolicy.Resolve(null, [Primary100], MinimumWidth, MinimumHeight));
        Assert.Null(ManagerWindowPlacementPolicy.Resolve(new(100, 80, 1400, 900), [], MinimumWidth, MinimumHeight));
        Assert.Null(ManagerWindowPlacementPolicy.Resolve(new(100, 80, 1400, 900), [Primary100 with { ScalePercent = 0 }], MinimumWidth, MinimumHeight));
    }

    [Fact]
    public void LogicalBoundsAreStoredInPhysicalPixelsAtTheDisplayScale()
    {
        Assert.Equal(new ManagerWindowPlacement(-1500, 120, 1440, 960, true), ManagerWindowPlacement.FromLogical(-1000, 80, 960, 640, 1.5, 1.5, true));
        Assert.Equal(new ManagerWindowPlacement(100, 80, 1320, 840), ManagerWindowPlacement.FromLogical(100, 80, 1320, 840, 1, 1, false));
        // WPF reports Rect.Empty (infinite position, negative infinite size) before the window exists.
        Assert.Null(ManagerWindowPlacement.FromLogical(double.PositiveInfinity, double.PositiveInfinity, double.NegativeInfinity, double.NegativeInfinity, 1, 1, false));
        Assert.Null(ManagerWindowPlacement.FromLogical(100, 80, 1320, 840, 0, 1, false));
        Assert.Null(ManagerWindowPlacement.FromLogical(100, 80, 0.2, 840, 1, 1, false));
    }

    [Fact]
    public Task ExitSavesThePlacementWithTheLatestThemeButNeverUnsavedEdits() => OnDispatcherAsync(async () =>
    {
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor());
        await vm.InitializeAsync(); Assert.Equal("Settings saved", vm.SaveStatus);
        vm.SpareRam = "4"; vm.ApplyTheme.Execute("ocean-blue"); await vm.ApplyTheme.Execution;
        var placement = new ManagerWindowPlacement(-2400, 60, 1600, 1000, true);
        await vm.SaveManagerWindowAsync(placement);
        using var store = new SettingsStore(_root); var saved = (await store.LoadAsync(default)).Settings;
        Assert.Equal(placement, saved.ManagerWindow); Assert.Equal("ocean-blue", saved.Theme); Assert.Equal(3, saved.Capacity!.SparePhysicalGiB);
        Assert.Equal("Unsaved settings", vm.SaveStatus);
        // A later explicit save keeps the placement it did not edit.
        vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        saved = (await store.LoadAsync(default)).Settings;
        Assert.Equal(placement, saved.ManagerWindow); Assert.Equal(4, saved.Capacity!.SparePhysicalGiB);
        // An unchanged placement does not rewrite the file.
        byte[] before = await File.ReadAllBytesAsync(Primary);
        await vm.SaveManagerWindowAsync(placement with { });
        Assert.Equal(before, await File.ReadAllBytesAsync(Primary));
    });

    [Fact]
    public Task ExitNeverRepairsRecoveredSettingsWithoutReview() => OnDispatcherAsync(async () =>
    {
        using (var store = new SettingsStore(_root)) { await store.LoadAsync(default); await store.SaveAsync(new(DenseAccounts: true), default); await File.WriteAllTextAsync(store.Path, "damaged settings"); }
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor());
        await vm.InitializeAsync(); Assert.Equal("Recovered settings — save to repair", vm.SaveStatus);
        await vm.SaveManagerWindowAsync(new(100, 80, 1400, 900));
        Assert.Equal("damaged settings", await File.ReadAllTextAsync(Primary));
        Assert.False(File.Exists(Primary + ".damaged"));
    });

    [Fact]
    public Task ExitStopsWorkBeforeItWaitsToKeepThePlacement() => OnDispatcherAsync(async () =>
    {
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor());
        await vm.InitializeAsync();
        // A theme autosave still holds the settings writer when Exit starts.
        vm.ApplyTheme.Execute("ocean-blue");
        var placement = new ManagerWindowPlacement(-2400, 60, 1600, 1000, true);
        var exit = vm.ExitAsync(placement);
        Assert.True(vm.IsShuttingDown, "Exit waited to save the window placement before it stopped automation, the launch queue and monitoring.");
        await exit.WaitAsync(TimeSpan.FromSeconds(15));
        // The shutdown fence cancelled the shell's lifetime, yet the placement was still kept, with the latest theme.
        using var store = new SettingsStore(_root); var saved = (await store.LoadAsync(default)).Settings;
        Assert.Equal(placement, saved.ManagerWindow); Assert.Equal("ocean-blue", saved.Theme);
    });

    [Fact]
    public Task ExitKeepsThePlacementWithoutASavedNoticeOrSettingsSavedEvent() => OnDispatcherAsync(async () =>
    {
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor());
        await vm.InitializeAsync();
        vm.Inform("Ready.");
        var placement = new ManagerWindowPlacement(100, 80, 1400, 900);
        await vm.ExitAsync(placement).WaitAsync(TimeSpan.FromSeconds(15));
        using var store = new SettingsStore(_root);
        Assert.Equal(placement, (await store.LoadAsync(default)).Settings.ManagerWindow);
        Assert.Equal("Ready.", vm.Notice);
        string[] kinds = ReadKinds();
        Assert.DoesNotContain("SettingsSaved", kinds);
        Assert.Equal("Shutdown", kinds[^1]);
    });

    private string[] ReadKinds()
    {
        string path = Path.Combine(_root, "diagnostics", "manager.jsonl");
        if (!File.Exists(path)) return [];
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)
            .Select(l => { using var line = JsonDocument.Parse(l); return line.RootElement.GetProperty("Kind").GetString()!; }).ToArray();
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
            // A tracked delegate owns the async work; no window or desktop input is created.
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
            || !Path.GetFileName(full).StartsWith("zenith-manager-window-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
