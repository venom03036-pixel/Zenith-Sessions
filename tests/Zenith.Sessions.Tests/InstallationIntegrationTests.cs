using System.IO;
using System.Reflection;
using System.Windows.Threading;
using System.Xml.Linq;
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

    [Fact]
    public void InstallationRowShowsTheProductNameTheFileReports()
    {
        static InstallationCandidateRow Row(string? productName) => new(new(ClientInstallationProduct.Roblox, "C:\\x\\RobloxPlayerBeta.exe", productName, "1.0.0.0",
            ClientInstallationFileState.PresentUnverified, [new(ClientInstallationHintSource.StandardInstallPath, ClientInstallationProduct.Roblox)]));
        // The path says Roblox; the file's own version resource says otherwise, and the row must show that.
        Assert.Equal("Roblox", Row("Bloxstrap").Product);
        Assert.Equal("Bloxstrap", Row("Bloxstrap").ReportedProductName);
        Assert.Equal("Not reported", Row(null).ReportedProductName);
        Assert.Equal("Not reported", Row("").ReportedProductName);
        Assert.Equal("Roblox Corporation " + (char)0x00A9, Row("Roblox Corporation " + (char)0x00A9).ReportedProductName);
        // File text is untrusted: hidden direction, line-break, control or oversized text is never rendered.
        Assert.Equal("Not shown: unusual text", Row("Roblox" + (char)0x202E + "exe.").ReportedProductName);
        Assert.Equal("Not shown: unusual text", Row("Roblox" + (char)0x2028 + "Player").ReportedProductName);
        Assert.Equal("Not shown: unusual text", Row("Roblox\tPlayer").ReportedProductName);
        Assert.Equal("Not shown: unusual text", Row(new string('R', 81)).ReportedProductName);
        Assert.Equal(new string('R', 80), Row(new string('R', 80)).ReportedProductName);
    }

    [Fact]
    public void InstallationListShowsTheFileReportedProductNameAsUnverified()
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProductionMainWindow.xaml"); Assert.NotNull(source);
        var document = XDocument.Load(source!); XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var list = Assert.Single(document.Descendants(ui + "ListView").Where(item => (string?)item.Attribute("AutomationProperties.Name") == "Unverified installation candidates"));
        var bindings = list.Descendants(ui + "GridViewColumn").Select(column => (string?)column.Attribute("DisplayMemberBinding")).ToArray();
        int reported = Array.IndexOf(bindings, "{Binding ReportedProductName}");
        Assert.True(reported > 0, "The installation list has no column for the product name the file reports.");
        Assert.Equal("{Binding Product}", bindings[reported - 1]); Assert.Equal("{Binding Version}", bindings[reported + 1]);
        string header = (string?)list.Descendants(ui + "GridViewColumn").ElementAt(reported).Attribute("Header") ?? "";
        Assert.Contains("unverified", header, StringComparison.OrdinalIgnoreCase);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task PanicOrLockAfterAFinishedCheckClearsTheListWithoutReportingAStoppedCheck(bool lockDashboard) => OnUi(async () =>
    {
        var discovery = new InstallationProbe();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), installationDiscovery: discovery);
        await vm.InitializeAsync(); string initial = vm.InstallationStatus;
        vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;
        Assert.False(vm.IsInstallationDiscoveryBusy); Assert.Equal(2, vm.InstallationCandidates.Count);
        vm.SelectedInstallation = vm.InstallationCandidates[0];
        if (lockDashboard) { vm.LockDashboard(); vm.Unlock.Execute(null); } else vm.Panic.Execute(null);
        Assert.Empty(vm.InstallationCandidates); Assert.Empty(vm.InstallationIssues); Assert.Null(vm.SelectedInstallation);
        // Nothing was running, so the panel returns to its unchecked wording instead of claiming a check was stopped.
        Assert.Equal("Not checked", vm.InstallationObserved); Assert.Equal(initial, vm.InstallationStatus);
        Assert.Equal(1, discovery.Calls); Assert.Empty(vm.Launches);
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
    public Task ALongButBoundedInstallationPathKeepsEveryRow() => OnUi(async () =>
    {
        // Discovery and executable trust accept paths up to 2048 characters, so one such row must not discard the scan.
        const string tail = "\\RobloxPlayerBeta.exe";
        string atLimit = "C:\\Synthetic" + string.Concat(Enumerable.Repeat("\\" + new string('a', 199), 10)) + "\\" + new string('b', 14) + tail;
        Assert.Equal(Zenith.Sessions.Windows.WindowsClientInstallationDiscovery.MaximumHintCharacters, atLimit.Length);
        var discovery = new InstallationProbe { ExtraPath = atLimit };
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), installationDiscovery: discovery);
        await vm.InitializeAsync(); vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;
        Assert.Equal(3, vm.InstallationCandidates.Count); Assert.Contains(vm.InstallationCandidates, row => row.ExecutablePath == atLimit);
        Assert.NotEqual("Check failed", vm.InstallationObserved); Assert.DoesNotContain("could not be checked", vm.InstallationStatus);

        // One character past the shared limit is still inconsistent metadata.
        discovery.ExtraPath = atLimit[..^tail.Length] + "b" + tail;
        vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;
        Assert.Empty(vm.InstallationCandidates); Assert.Equal("Check failed", vm.InstallationObserved);
        Assert.Contains("could not be checked", vm.InstallationStatus);
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

    [Fact]
    public Task InstallationIssuesNameTheirProductAndSourceAndATooLargeFileSaysSo() => OnUi(async () =>
    {
        ClientInstallationSnapshot Snapshot(params ClientInstallationIssue[] issues) => new(DateTimeOffset.UtcNow, ClientInstallationSnapshotStatus.Partial,
            [new(ClientInstallationProduct.Roblox, "C:\\Synthetic\\RobloxPlayerBeta.exe", "Synthetic Roblox", "0.745.0.7451137", ClientInstallationFileState.PresentUnverified, [new(ClientInstallationHintSource.StandardInstallPath, ClientInstallationProduct.Roblox)]),
             new(ClientInstallationProduct.Roblox, "C:\\Synthetic\\Versions\\version-1\\RobloxPlayerBeta.exe", null, null, ClientInstallationFileState.TooLarge, [new(ClientInstallationHintSource.VersionDirectory, ClientInstallationProduct.Roblox)])],
            issues);
        var discovery = new InstallationProbe
        {
            Override = Snapshot(new(ClientInstallationIssueCode.MultipleCandidates, ClientInstallationProduct.Roblox), new(ClientInstallationIssueCode.MultipleCandidates, ClientInstallationProduct.Bloxstrap),
                new(ClientInstallationIssueCode.LimitReached), new(ClientInstallationIssueCode.LimitReached, ClientInstallationProduct.Roblox),
                new(ClientInstallationIssueCode.MetadataUnavailable, ClientInstallationProduct.Roblox, ClientInstallationHintSource.VersionDirectory),
                new(ClientInstallationIssueCode.MetadataUnavailable, ClientInstallationProduct.Roblox, ClientInstallationHintSource.StandardInstallPath),
                new(ClientInstallationIssueCode.InvalidHint, Source: ClientInstallationHintSource.RobloxProtocol),
                new(ClientInstallationIssueCode.MultipleCandidates, ClientInstallationProduct.Roblox))
        };
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), installationDiscovery: discovery);
        await vm.InitializeAsync(); vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;

        // Each line says which product and where it came from; the same line is never shown twice.
        Assert.Equal(new[]
        {
            "Roblox: Multiple executable candidates remain for a product. No version or provider was selected automatically.",
            "Bloxstrap: Multiple executable candidates remain for a product. No version or provider was selected automatically.",
            "Discovery reached a bounded query or result limit. Additional candidates may exist.",
            "Roblox: Discovery reached a bounded query or result limit. Additional candidates may exist.",
            "Roblox · version folder: Some executable version metadata could not be read. The candidate remains unverified.",
            "Roblox · standard location: Some executable version metadata could not be read. The candidate remains unverified.",
            "Roblox registration: An installation hint was malformed, unsupported or oversized. Its raw value was discarded."
        }, vm.InstallationIssues);
        Assert.Equal("File found · unverified", vm.InstallationCandidates[0].FileState);
        Assert.Equal("Too large to inspect (over 256 MB)", vm.InstallationCandidates[1].FileState);

        // An issue naming a product or source that does not exist is inconsistent metadata, not a line to show.
        discovery.Override = Snapshot(new ClientInstallationIssue(ClientInstallationIssueCode.MultipleCandidates, (ClientInstallationProduct)99));
        vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;
        Assert.Equal("Check failed", vm.InstallationObserved); Assert.Empty(vm.InstallationIssues); Assert.Empty(vm.InstallationCandidates);
        discovery.Override = Snapshot(new ClientInstallationIssue(ClientInstallationIssueCode.InvalidHint, Source: (ClientInstallationHintSource)99));
        vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;
        Assert.Equal("Check failed", vm.InstallationObserved); Assert.Empty(vm.InstallationIssues);
    });

    private sealed class InstallationProbe : IClientInstallationDiscovery
    {
        public int Calls; public bool Malformed; public string? ExtraPath; public CancellationToken Token;
        public TaskCompletionSource<ClientInstallationSnapshot>? Gate;
        public ClientInstallationSnapshot? Override;
        public Task<ClientInstallationSnapshot> DiscoverAsync(CancellationToken ct)
        {
            Calls++; Token = ct;
            if (Gate is not null) return Gate.Task;
            if (Override is not null) return Task.FromResult(Override);
            var snapshot = Snapshot();
            if (ExtraPath is not null)
                snapshot = snapshot with { Candidates = [.. snapshot.Candidates, snapshot.Candidates[0] with { ExecutablePath = ExtraPath }] };
            return Task.FromResult(Malformed ? snapshot with { Candidates = [snapshot.Candidates[0], snapshot.Candidates[0]] } : snapshot);
        }
        public static ClientInstallationSnapshot Snapshot() => new(DateTimeOffset.UtcNow, ClientInstallationSnapshotStatus.Partial,
            [new(ClientInstallationProduct.Roblox, "C:\\Synthetic\\RobloxPlayerBeta.exe", "Synthetic Roblox", "0.745.0.7451137", ClientInstallationFileState.PresentUnverified, [new(ClientInstallationHintSource.RobloxPlayerProtocol, ClientInstallationProduct.Roblox)]),
             new(ClientInstallationProduct.Froststrap, "C:\\Synthetic\\Froststrap.exe", "Synthetic Froststrap", "1.0.0", ClientInstallationFileState.PresentUnverified, [new(ClientInstallationHintSource.UninstallRegistry, ClientInstallationProduct.Froststrap)])],
            [new(ClientInstallationIssueCode.ConflictingProtocols)]);
    }
}
