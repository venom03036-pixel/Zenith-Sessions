using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Public-network guard (R-api-18, also R-shell-23): an opt-in, off by default, that watches which network profile Windows gives
// the active connection through the Network List Manager's own change events (no polling). While a connected network that
// reaches the internet is filed Public, new launches are held with that reason and the dashboard locks like Lock, with a
// clear notice; held requests start again on their own once the network is private. No network name is read or kept.
public sealed partial class PublicNetworkGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-public-network-" + Guid.NewGuid().ToString("N"));
    private string Primary => Path.Combine(_root, "settings.json");
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static ulong G(double value) => checked((ulong)Math.Ceiling(value * CapacityPolicy.GiB));
    private static LaunchOperation Candidate() => new(new(Guid.NewGuid(), Guid.NewGuid(), 1, 123, 42, 0, DateTimeOffset.UtcNow, 7),
        LaunchState.Queued, LaunchProblem.None, DateTimeOffset.UtcNow, ResourceBudget: new(G(.1), G(.1)));

    [Fact]
    public void OnlyAConnectedPublicNetworkThatReachesTheInternetCountsAsPublic()
    {
        Assert.Equal(NetworkExposure.NotPublic, PublicNetworkPolicy.Classify([]));
        Assert.Equal(NetworkExposure.NotPublic, PublicNetworkPolicy.Classify([new(NetworkProfile.Private, true), new(NetworkProfile.Domain, true)]));
        // Hyper-V, WSL and VPN adapters are often filed Public but reach nothing; they never hold.
        Assert.Equal(NetworkExposure.NotPublic, PublicNetworkPolicy.Classify([new(NetworkProfile.Public, false)]));
        Assert.True(PublicNetworkPolicy.Classify([new(NetworkProfile.Private, true), new(NetworkProfile.Public, true)]) == NetworkExposure.Public,
            "A connected Public network with internet access was not classified as public.");
        Assert.True(PublicNetworkPolicy.Holds(NetworkExposure.Public));
        Assert.False(PublicNetworkPolicy.Holds(NetworkExposure.NotPublic));
        // Windows could not say: nothing is held (Rule 16), the guard only keeps listening.
        Assert.False(PublicNetworkPolicy.Holds(NetworkExposure.Unknown));
    }

    [Fact]
    public void AWaitingRowHeldForAPublicNetworkExplainsItAndCountsApartFromCapacityWaits()
    {
        var held = Candidate() with { State = LaunchState.Blocked, Problem = LaunchProblem.InsufficientCapacity, HoldReason = LaunchHoldReason.PublicNetwork };
        Assert.True(LaunchDescriptions.AwaitsPublicNetwork(held), "A row held for a public network was not recognised as such.");
        Assert.True(LaunchDescriptions.AwaitsCapacity(held)); // The runner re-selects it and starts it on its own once the hold lifts.
        Assert.False(LaunchDescriptions.AwaitsClientUpdate(held));
        Assert.Equal(PublicNetworkPolicy.HoldExplanation, LaunchDescriptions.Explain(held));
        Assert.Contains("public network", PublicNetworkPolicy.HoldExplanation, StringComparison.OrdinalIgnoreCase);
        Assert.False(LaunchDescriptions.AwaitsPublicNetwork(held with { HoldReason = LaunchHoldReason.ResourceHeadroom }));
        Assert.False(LaunchDescriptions.AwaitsPublicNetwork(held with { State = LaunchState.Queued, Problem = LaunchProblem.None }));

        // Overview counts it apart from capacity waits and update holds, because it too starts by itself.
        var operations = new[] { Candidate() with { State = LaunchState.Blocked, Problem = LaunchProblem.InsufficientCapacity }, held };
        var view = OverviewWorkspaceProjection.Create(new OverviewWorkspaceInput(QueueReady: true, Queue: new(true, operations)), DateTimeOffset.UtcNow);
        Assert.Contains(" · 1 waiting for capacity (start automatically while the queue is active) · 1 held for a public network", view.QueueSummary, StringComparison.Ordinal);
        Assert.Contains(" · 0 blocked · ", view.QueueSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheGuardIsOffByDefaultOlderFilesStayOffAndASavedChoiceSurvivesLoadAndSave()
    {
        Assert.False(new SessionsSettings().PublicNetworkGuard);
        Directory.CreateDirectory(_root);
        // A schema-1 file from before this setting loads without a notice and keeps the guard off.
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"DenseAccounts\":true}");
        using (var store = new SettingsStore(_root))
        {
            var loaded = await store.LoadAsync(default);
            Assert.Null(loaded.Notice);
            Assert.False(loaded.Settings.PublicNetworkGuard);
            Assert.Contains("\"PublicNetworkGuard\":false", JsonSerializer.Serialize(loaded.Settings), StringComparison.Ordinal);
            await store.SaveAsync(loaded.Settings, default);
        }
        Assert.False(SavedGuard(), "Saving an older settings file turned the public-network guard on.");

        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"PublicNetworkGuard\":true}");
        using (var store = new SettingsStore(_root))
        {
            var loaded = await store.LoadAsync(default);
            Assert.Null(loaded.Notice);
            Assert.True(loaded.Settings.PublicNetworkGuard, "A saved public-network guard choice was dropped when settings loaded.");
            await store.SaveAsync(loaded.Settings, default);
        }
        Assert.True(SavedGuard(), "Saving settings dropped the chosen public-network guard.");
    }

    [Fact]
    public void SettingsOffersTheGuardInAppearanceTheFooterNamesTheHoldAndTheGuideNamesIt()
    {
        var document = ProductionWindow();
        XElement? Named(string name) => document.Descendants().SingleOrDefault(e => (string?)e.Attribute(X + "Name") == name);
        var option = Named("SettingsPublicNetworkGuard");
        Assert.True(option is not null, "Settings has no control for holding launches on a public network.");
        Assert.Equal("CheckBox", option!.Name.LocalName);
        Assert.Equal("{Binding PublicNetworkGuard}", (string?)option.Attribute("IsChecked"));
        Assert.False(string.IsNullOrWhiteSpace((string?)option.Attribute("AutomationProperties.Name")));
        Assert.Contains(document.Descendants(), e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == "{Binding PublicNetworkStatus}");

        // The choice sits with the other manager-behaviour choices under Appearance, before Performance, where a search result lands.
        var all = document.Descendants().ToList();
        int appearance = all.FindIndex(e => (string?)e.Attribute(X + "Name") == "SettingsAppearanceAnchor");
        int performance = all.FindIndex(e => (string?)e.Attribute(X + "Name") == "SettingsPerformanceAnchor");
        Assert.InRange(all.IndexOf(option), appearance + 1, performance - 1);

        // Every page says that launches are held, through the footer, only while they are.
        var footer = Named("FooterPublicNetwork");
        Assert.True(footer is not null, "The footer does not say when launches are held for a public network.");
        Assert.Contains(Named("FooterNotices")!, footer!.Ancestors());
        Assert.Equal("{Binding IsPublicNetworkHeld, Converter={StaticResource VisibleIf}}", (string?)footer.Attribute("Visibility"));
        Assert.Contains("Public network", (string?)footer.Attribute("Text") ?? "", StringComparison.Ordinal);

        var guide = Assert.Single(document.Descendants().Where(e => ((string?)e.Attribute("Text"))?.Contains("Lock hides account controls", StringComparison.Ordinal) == true));
        Assert.Contains("public network", (string)guide.Attribute("Text")!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public network")]
    [InlineData("public wifi")]
    [InlineData("hotspot")]
    [InlineData("hotel")]
    public void SettingsSearchFindsTheGuardInAppearance(string query)
    {
        var entry = SettingsSearchCatalog.Search(query).FirstOrDefault(e => e.Id == "public-network-guard");
        Assert.True(entry is not null, $"Searching settings for '{query}' does not find the public-network guard.");
        Assert.Equal(SettingsSearchTarget.Appearance, entry!.Target);
    }

    [Fact]
    public Task NothingIsWatchedByDefaultAndTheSavedGuardLocksAndHoldsLaunchesWhileTheNetworkIsPublic() => OnDispatcherAsync(async () =>
    {
        var network = new NetworkProbe();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), idleClock: new ManualClock(), publicNetworkMonitor: network);
        await vm.InitializeAsync(); Assert.True(vm.IsReady, vm.Notice);
        // Off by default: Windows is not asked anything, and no launch is held.
        Assert.False(vm.PublicNetworkGuard); Assert.Equal(0, network.StartCalls);
        Assert.False(vm.IsPublicNetworkHeld);
        Assert.NotEqual(LaunchHoldReason.PublicNetwork, Admission(vm).Hold);
        Assert.Contains("Off", vm.PublicNetworkStatus, StringComparison.Ordinal);

        // An unsaved draft changes nothing yet.
        vm.PublicNetworkGuard = true; Assert.Equal("Unsaved settings", vm.SaveStatus);
        Assert.Equal(0, network.StartCalls);

        // Saved on while the network is private: Windows is asked once, nothing is locked or held.
        vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        Assert.Equal("Settings saved", vm.SaveStatus);
        Assert.True(network.StartCalls == 1, "Saving the guard on did not start watching the network.");
        Assert.False(vm.IsLocked); Assert.False(vm.IsPublicNetworkHeld);
        Assert.Contains("\"PublicNetworkGuard\":true", File.ReadAllText(Primary), StringComparison.Ordinal);
        Assert.Contains("Private or Domain", vm.PublicNetworkStatus, StringComparison.Ordinal);

        // Windows reports the network as Public: launches are held with that reason and the dashboard locks, with a clear notice.
        network.Raise(NetworkExposure.Public); await Pump();
        Assert.True(vm.IsLocked, "A Public network did not lock the dashboard.");
        Assert.True(vm.IsPublicNetworkHeld, "A Public network did not hold launches.");
        Assert.Contains("Public network", vm.Notice, StringComparison.Ordinal);
        Assert.Contains(vm.Events, e => e.Contains("Public network", StringComparison.Ordinal));
        Assert.False(vm.HasErrorNotice);
        var held = Admission(vm);
        Assert.False(held.Allowed); Assert.Equal(LaunchHoldReason.PublicNetwork, held.Hold); Assert.Equal(PublicNetworkPolicy.HoldExplanation, held.Explanation);
        Assert.Contains(Diagnostics(vm).Events, e => e.Kind == ManagerEventKind.PublicNetworkDetected);
        Assert.Contains(Diagnostics(vm).Events, e => e.Kind == ManagerEventKind.ControlsLocked);

        // Unlock is the user's choice, as after any lock; launches stay held while the network is still Public.
        vm.Unlock.Execute(null); await vm.Unlock.Execution;
        Assert.False(vm.IsLocked); Assert.True(vm.IsPublicNetworkHeld);
        Assert.Equal(LaunchHoldReason.PublicNetwork, Admission(vm).Hold);

        // Back on a private network: the hold lifts on its own and the notice says so; nothing locks or unlocks.
        network.Raise(NetworkExposure.NotPublic); await Pump();
        Assert.False(vm.IsPublicNetworkHeld, "Leaving the Public network did not lift the hold.");
        Assert.NotEqual(LaunchHoldReason.PublicNetwork, Admission(vm).Hold);
        Assert.Contains("no longer Public", vm.Notice, StringComparison.Ordinal);
        Assert.False(vm.IsLocked);
        Assert.Contains(Diagnostics(vm).Events, e => e.Kind == ManagerEventKind.PublicNetworkCleared);

        // Each new Public network locks again.
        network.Raise(NetworkExposure.Public); await Pump();
        Assert.True(vm.IsLocked); Assert.True(vm.IsPublicNetworkHeld);
        // A repeated Public report while already held changes nothing and does not stack notices.
        int events = vm.Events.Count;
        network.Raise(NetworkExposure.Public); await Pump();
        Assert.Equal(events, vm.Events.Count);

        // Saved off: Windows is no longer watched and nothing is held, whatever the network; the lock stays until Unlock.
        vm.Unlock.Execute(null); await vm.Unlock.Execution;
        vm.PublicNetworkGuard = false; vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        Assert.True(network.StopCalls == 1, "Saving the guard off did not stop watching the network.");
        Assert.False(vm.IsPublicNetworkHeld); Assert.NotEqual(LaunchHoldReason.PublicNetwork, Admission(vm).Hold);
        Assert.Contains("Off", vm.PublicNetworkStatus, StringComparison.Ordinal);
        network.Raise(NetworkExposure.Public); await Pump();
        Assert.False(vm.IsPublicNetworkHeld); Assert.False(vm.IsLocked);
    });

    [Fact]
    public Task StartingOnAPublicNetworkLocksAtOnceAndAnUnknownProfileNeverHolds() => OnDispatcherAsync(async () =>
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"PublicNetworkGuard\":true}");
        var network = new NetworkProbe { Exposure = NetworkExposure.Public };
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), idleClock: new ManualClock(), publicNetworkMonitor: network);
        await vm.InitializeAsync(); Assert.True(vm.IsReady, vm.Notice);
        Assert.True(vm.PublicNetworkGuard); Assert.Equal(1, network.StartCalls);
        Assert.True(vm.IsLocked, "Starting on a Public network with the guard saved on did not lock the dashboard.");
        Assert.True(vm.IsPublicNetworkHeld);
        Assert.Equal(LaunchHoldReason.PublicNetwork, Admission(vm).Hold);
        Assert.Contains("Public network", vm.Notice, StringComparison.Ordinal);

        // Windows could not say: nothing is held (Rule 16); the status says so and the guard keeps listening. No error is raised for it.
        network.Raise(NetworkExposure.Unknown); await Pump();
        Assert.False(vm.IsPublicNetworkHeld, "An unknown network profile kept launches held.");
        Assert.NotEqual(LaunchHoldReason.PublicNetwork, Admission(vm).Hold);
        Assert.Contains("could not report", vm.PublicNetworkStatus, StringComparison.Ordinal);
        Assert.False(vm.HasErrorNotice);
        Assert.Equal(1, network.StartCalls); Assert.Equal(0, network.StopCalls);
    });

    [Fact]
    public Task ExitStopsAndDisposesTheMonitorAndAWindowsSessionEndStopsIt() => OnDispatcherAsync(async () =>
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Primary, "{\"Version\":1,\"PublicNetworkGuard\":true}");
        var network = new NetworkProbe();
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), idleClock: new ManualClock(), publicNetworkMonitor: network);
        try { await vm.InitializeAsync(); Assert.True(vm.IsReady, vm.Notice); Assert.Equal(1, network.StartCalls); }
        finally { await vm.DisposeAsync(); }
        Assert.True(network.StopCalls == 1, "Exit left the network watch running.");
        Assert.True(network.Disposed, "Exit did not dispose the network monitor.");
        // A report after exit is ignored.
        network.Raise(NetworkExposure.Public); await Pump();
        Assert.False(vm.IsPublicNetworkHeld);

        var ending = new NetworkProbe();
        var second = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), idleClock: new ManualClock(), publicNetworkMonitor: ending);
        try
        {
            await second.InitializeAsync(); Assert.True(second.IsReady, second.Notice);
            Assert.Equal(1, ending.StartCalls);
            second.PrepareForSessionEnd(TimeSpan.FromSeconds(2));
            Assert.Equal(1, ending.StopCalls);
        }
        finally { await second.DisposeAsync(); }
        Assert.True(ending.Disposed);
    });

    [Fact]
    public Task SampleModeNeverWatchesTheNetwork() => OnDispatcherAsync(async () =>
    {
        await using var vm = new ShellViewModel(_root, true, Dispatcher.CurrentDispatcher, new SyntheticMonitor(), idleClock: new ManualClock());
        await vm.InitializeAsync(); Assert.True(vm.IsReady, vm.Notice);
        Assert.Contains("sample", vm.PublicNetworkStatus, StringComparison.OrdinalIgnoreCase);
        vm.PublicNetworkGuard = true; vm.SaveSettings.Execute(null); await vm.SaveSettings.Execution;
        Assert.Equal("Settings saved", vm.SaveStatus);
        Assert.False(vm.IsPublicNetworkHeld); Assert.False(vm.IsLocked);
        Assert.Contains("sample", vm.PublicNetworkStatus, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public Task TheWindowsMonitorMapsWindowsProfilesAndReadsTheLocalNetworkListWithoutThrowing() => OnDispatcherAsync(() =>
    {
        // NLM_NETWORK_CATEGORY: 0 Public, 1 Private, 2 Domain-authenticated; anything else is read as Public (the cautious side).
        Assert.Equal(NetworkProfile.Public, WindowsPublicNetworkMonitor.Profile(0));
        Assert.Equal(NetworkProfile.Private, WindowsPublicNetworkMonitor.Profile(1));
        Assert.Equal(NetworkProfile.Domain, WindowsPublicNetworkMonitor.Profile(2));
        Assert.Equal(NetworkProfile.Public, WindowsPublicNetworkMonitor.Profile(7));
        // NLM_CONNECTIVITY: only the IPv4 or IPv6 internet bits count; local or subnet traffic does not.
        Assert.False(WindowsPublicNetworkMonitor.ReachesInternet(0));
        Assert.False(WindowsPublicNetworkMonitor.ReachesInternet(0x20 | 0x200));
        Assert.True(WindowsPublicNetworkMonitor.ReachesInternet(0x40));
        Assert.True(WindowsPublicNetworkMonitor.ReachesInternet(0x400));

        // Observation of this PC's own network list, through the Network List Manager, on an STA thread like the manager window's.
        // The answer depends on this PC and is not asserted; what is asserted is that reading, stopping and disposing never throw.
        using var monitor = new WindowsPublicNetworkMonitor();
        int changes = 0; monitor.Changed += _ => changes++;
        var exposure = monitor.Start();
        Assert.True(Enum.IsDefined(exposure), "Start returned an undefined network exposure.");
        Assert.Equal(exposure, monitor.Start()); // Idempotent: a second Start reads again and subscribes nothing twice.
        Assert.Equal(0, changes); // Changed is for later transitions, never raised by Start itself.
        monitor.Stop(); monitor.Stop();
        monitor.Dispose(); monitor.Dispose();
        return Task.CompletedTask;
    });

    private static LaunchAdmission Admission(ShellViewModel shell)
        => (LaunchAdmission)typeof(ShellViewModel).GetMethod("ReadCandidateLaunchAdmission", Private)!.Invoke(shell, [Candidate(), Array.Empty<LaunchOperation>()])!;
    private static ManagerDiagnosticSnapshot Diagnostics(ShellViewModel shell)
        => ((ManagerDiagnostics)typeof(ShellViewModel).GetField("_diagnostics", Private)!.GetValue(shell)!).GetSnapshot();
    /// <summary>Lets the shell's queued dispatcher work (a network report it marshals to the UI thread) run.</summary>
    private static async Task Pump()
    {
        for (int i = 0; i < 3; i++) await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
    }

    private bool SavedGuard()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Primary));
        return json.RootElement.TryGetProperty("PublicNetworkGuard", out var value) && value.GetBoolean();
    }

    private static XDocument ProductionWindow()
    {
        using var stream = typeof(PublicNetworkGuardTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        return XDocument.Load(stream);
    }

    private sealed class NetworkProbe : IPublicNetworkMonitor
    {
        internal int StartCalls, StopCalls;
        internal bool Disposed;
        internal NetworkExposure Exposure = NetworkExposure.NotPublic;
        public event Action<NetworkExposure>? Changed;
        public NetworkExposure Start() { StartCalls++; return Exposure; }
        public void Stop() => StopCalls++;
        internal Action<NetworkExposure>? CaptureCallback() => Changed;
        internal void Raise(NetworkExposure exposure) { Exposure = exposure; Changed?.Invoke(exposure); }
        public void Dispose() => Disposed = true;
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = 1;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
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
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-public-network-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
