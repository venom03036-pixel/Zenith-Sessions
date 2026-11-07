using System.IO;
using System.Reflection;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    [Fact]
    public Task InstallationDiscoveryIsExplicitAndNeverSelectsAProviderOrWritesSettings() => OnUi(async () =>
    {
        var discovery = new InstallationProbe();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), installationDiscovery: discovery);
        Assert.False(vm.CanRefreshInstallations); await vm.InitializeAsync();
        Assert.Equal(0, discovery.Calls); Assert.True(vm.CanRefreshInstallations); string saved = vm.SaveStatus;
        vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;
        Assert.Equal(1, discovery.Calls); Assert.Equal(2, vm.InstallationCandidates.Count); Assert.Null(vm.SelectedInstallation);
        Assert.Single(vm.InstallationIssues); Assert.Contains("unverified", vm.InstallationStatus);
        Assert.All(vm.InstallationCandidates, row => Assert.Equal(CapabilityState.Unavailable, row.Candidate.NamedAccountLaunch));
        Assert.Empty(vm.Launches); Assert.Equal(saved, vm.SaveStatus);
        vm.SelectedInstallation = vm.InstallationCandidates[1];
        Assert.Equal("C:\\Synthetic\\Froststrap.exe", vm.SelectedInstallation.ExecutablePath);
    });

    [Fact]
    public Task SelectingBetweenUnsupportedInstallationRowsDoesNotPauseTheQueue() => OnUi(async () =>
    {
        var discovery = new InstallationProbe();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), installationDiscovery: discovery);
        await vm.InitializeAsync(); Assert.True(vm.QueueReady);
        vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;
        await ResumeQueueForInstallationTestAsync(vm);
        InstallationCandidateRow bloxstrap = new(new(ClientInstallationProduct.Bloxstrap, "C:\\Synthetic\\Bloxstrap.exe", "Synthetic Bloxstrap", "2.0.0",
            ClientInstallationFileState.PresentUnverified, [new(ClientInstallationHintSource.UninstallRegistry, ClientInstallationProduct.Bloxstrap)]));
        InstallationCandidateRow missingRoblox = new(vm.InstallationCandidates[0].Candidate with { ExecutablePath = "C:\\Synthetic\\Missing\\RobloxPlayerBeta.exe", FileState = ClientInstallationFileState.Missing });
        vm.InstallationCandidates.Add(bloxstrap); vm.InstallationCandidates.Add(missingRoblox);

        // Froststrap -> Bloxstrap -> a missing Roblox file: none can become the trial installation.
        vm.SelectedInstallation = vm.InstallationCandidates[1];
        vm.SelectedInstallation = bloxstrap;
        vm.SelectedInstallation = missingRoblox;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(InstallationTestQueuePaused(vm), "Selecting only unsupported rows must not pause the launch queue.");
        Assert.Equal("Pause queue", vm.QueuePauseLabel); Assert.Contains("not supported", vm.NativeTrialStatus);

        // Refresh and Cancel check clear an unsupported selection without touching the queue.
        vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution; Assert.Null(vm.SelectedInstallation);
        vm.SelectedInstallation = vm.InstallationCandidates[1]; vm.CancelInstallationDiscovery.Execute(null); Assert.Null(vm.SelectedInstallation);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(InstallationTestQueuePaused(vm)); Assert.Equal("Pause queue", vm.QueuePauseLabel);

        // A real trial-installation change still revokes: onto a present native row, and off it again.
        vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;
        vm.SelectedInstallation = vm.InstallationCandidates[0];
        Assert.True(InstallationTestQueuePaused(vm)); Assert.Contains("Native file selected", vm.NativeTrialStatus);
        await WaitForQueuePauseLabelAsync(vm, "Resume queue");
        await ResumeQueueForInstallationTestAsync(vm);
        vm.SelectedInstallation = vm.InstallationCandidates[1];
        Assert.True(InstallationTestQueuePaused(vm)); Assert.Contains("not supported", vm.NativeTrialStatus);
        await WaitForQueuePauseLabelAsync(vm, "Resume queue");
        Assert.Empty(vm.Launches);
    });

    [Fact]
    public void ProductionInstallationCopyStatesThatANativeRowBecomesTheTrialInstallation()
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProductionMainWindow.xaml"); Assert.NotNull(source);
        string xaml = new StreamReader(source!).ReadToEnd();
        Assert.DoesNotContain("No candidate is chosen as your launch provider", xaml, StringComparison.Ordinal);
        Assert.Contains("makes it the installation for an explicit trial in Join a game; changing that row stops any trial and pauses the queue", xaml, StringComparison.Ordinal);
    }

    private static async Task ResumeQueueForInstallationTestAsync(ShellViewModel vm)
    {
        vm.ToggleQueuePause.Execute(null); await vm.ToggleQueuePause.Execution;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(InstallationTestQueuePaused(vm)); Assert.Equal("Pause queue", vm.QueuePauseLabel);
    }

    private static async Task WaitForQueuePauseLabelAsync(ShellViewModel vm, string expected)
    {
        // The queue publishes its stopped state through a background dispatcher item.
        for (int wait = 0; wait < 100 && vm.QueuePauseLabel != expected; wait++) { await Task.Delay(50); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
        Assert.Equal(expected, vm.QueuePauseLabel);
    }

    private static bool InstallationTestQueuePaused(ShellViewModel vm)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var queue = typeof(ShellViewModel).GetField("_launchQueue", flags)!.GetValue(vm)!;
        return (bool)queue.GetType().GetField("_paused", flags)!.GetValue(queue)!;
    }

    [Fact]
    public Task CancelledInstallationReadCannotPublishAfterUnlockOrQueueAnotherRead() => OnUi(async () =>
    {
        var discovery = new InstallationProbe { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), installationDiscovery: discovery);
        await vm.InitializeAsync(); vm.RefreshInstallations.Execute(null);
        try
        {
            Assert.True(vm.IsInstallationDiscoveryBusy); vm.RefreshInstallations.Execute(null); Assert.Equal(1, discovery.Calls);
            vm.LockDashboard(); vm.Unlock.Execute(null);
            Assert.True(discovery.Token.IsCancellationRequested); Assert.Empty(vm.InstallationCandidates);
            Assert.False(vm.CanRefreshInstallations); vm.Panic.Execute(null);
        }
        finally { discovery.Gate.TrySetResult(InstallationProbe.Snapshot()); }
        await vm.RefreshInstallations.Execution;
        Assert.True(vm.CanRefreshInstallations); Assert.Empty(vm.InstallationCandidates); Assert.Empty(vm.InstallationIssues);
        Assert.Contains("Panic stop", vm.Notice); Assert.Contains("stopped", vm.InstallationStatus);
    });

    [Fact]
    public Task MalformedInventoryIsNotPresentedAsACompleteOrSuccessfulDiscovery() => OnUi(async () =>
    {
        var discovery = new InstallationProbe { Malformed = true };
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), installationDiscovery: discovery);
        await vm.InitializeAsync(); vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;
        Assert.Empty(vm.InstallationCandidates); Assert.Equal("Check failed", vm.InstallationObserved);
        Assert.Contains("could not be checked", vm.InstallationStatus);
        Assert.DoesNotContain(vm.Events, text => text.Contains("Synthetic", StringComparison.Ordinal));
        Assert.Empty(vm.Launches);
    });

    [Fact]
    public Task ShutdownWaitsForTheActualInstallationOwnerAndSuppressesLateResults() => OnUi(async () =>
    {
        var discovery = new InstallationProbe { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), installationDiscovery: discovery);
        await vm.InitializeAsync(); vm.RefreshInstallations.Execute(null);
        var disposing = vm.DisposeAsync().AsTask();
        try { Assert.True(discovery.Token.IsCancellationRequested); Assert.False(disposing.IsCompleted); }
        finally { discovery.Gate.TrySetResult(InstallationProbe.Snapshot()); }
        await disposing; Assert.Empty(vm.InstallationCandidates); Assert.False(vm.CanRefreshInstallations);
    });

    private sealed class InstallationProbe : IClientInstallationDiscovery
    {
        public int Calls; public bool Malformed; public CancellationToken Token;
        public TaskCompletionSource<ClientInstallationSnapshot>? Gate;
        public Task<ClientInstallationSnapshot> DiscoverAsync(CancellationToken ct)
        {
            Calls++; Token = ct;
            if (Gate is not null) return Gate.Task;
            var snapshot = Snapshot();
            return Task.FromResult(Malformed ? snapshot with { Candidates = [snapshot.Candidates[0], snapshot.Candidates[0]] } : snapshot);
        }
        public static ClientInstallationSnapshot Snapshot() => new(DateTimeOffset.UtcNow, ClientInstallationSnapshotStatus.Partial,
            [new(ClientInstallationProduct.Roblox, "C:\\Synthetic\\RobloxPlayerBeta.exe", "Synthetic Roblox", "0.745.0.7451137", ClientInstallationFileState.PresentUnverified, [new(ClientInstallationHintSource.RobloxPlayerProtocol, ClientInstallationProduct.Roblox)]),
             new(ClientInstallationProduct.Froststrap, "C:\\Synthetic\\Froststrap.exe", "Synthetic Froststrap", "1.0.0", ClientInstallationFileState.PresentUnverified, [new(ClientInstallationHintSource.UninstallRegistry, ClientInstallationProduct.Froststrap)])],
            [new(ClientInstallationIssueCode.ConflictingProtocols)]);
    }
}
