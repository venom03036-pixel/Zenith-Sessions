using System.IO;
using System.Net.Http;
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
    public Task SelectingTheLauncherOrABootstrapperManagedPlayerRowDoesNotPauseTheQueue() => OnUi(async () =>
    {
        var discovery = new InstallationProbe();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), installationDiscovery: discovery);
        await vm.InitializeAsync(); Assert.True(vm.QueueReady);
        vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;
        await ResumeQueueForInstallationTestAsync(vm);
        var native = vm.InstallationCandidates[0];
        // Discovery lists RobloxPlayerLauncher.exe in every native version folder, and a player under a
        // bootstrapper's folder carries that bootstrapper as evidence. Both are present Roblox rows the
        // provider never accepts as the trial installation.
        InstallationCandidateRow launcher = new(native.Candidate with { ExecutablePath = "C:\\Synthetic\\RobloxPlayerLauncher.exe" });
        InstallationCandidateRow managed = new(native.Candidate with
        {
            ExecutablePath = "C:\\Synthetic\\Bloxstrap\\Versions\\version-x\\RobloxPlayerBeta.exe",
            Evidence = [new(ClientInstallationHintSource.VersionDirectory, ClientInstallationProduct.Bloxstrap)]
        });
        vm.InstallationCandidates.Add(launcher); vm.InstallationCandidates.Add(managed);
        Assert.Equal(ClientInstallationProduct.Roblox, launcher.Candidate.Product); Assert.Equal(ClientInstallationFileState.PresentUnverified, launcher.Candidate.FileState);
        Assert.Equal(ClientInstallationProduct.Bloxstrap, managed.ManagedBy);

        // Nothing native is selected, so inspecting either row cannot change the trial installation.
        vm.SelectedInstallation = launcher;
        Assert.False(InstallationTestQueuePaused(vm), "Selecting the launcher row must not pause the launch queue.");
        Assert.Contains("not supported", vm.NativeTrialStatus);
        vm.SelectedInstallation = managed;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(InstallationTestQueuePaused(vm), "Selecting a bootstrapper-managed player row must not pause the launch queue.");
        Assert.Equal("Pause queue", vm.QueuePauseLabel); Assert.Contains("managed by Bloxstrap", vm.NativeTrialStatus);

        // Moving off a selected native row onto either one still revokes the trial installation.
        foreach (var unsupported in new[] { launcher, managed })
        {
            vm.SelectedInstallation = native;
            Assert.True(InstallationTestQueuePaused(vm)); Assert.Contains("Native file selected", vm.NativeTrialStatus);
            await WaitForQueuePauseLabelAsync(vm, "Resume queue");
            await ResumeQueueForInstallationTestAsync(vm);
            vm.SelectedInstallation = unsupported;
            Assert.True(InstallationTestQueuePaused(vm)); Assert.Contains("not supported", vm.NativeTrialStatus);
            await WaitForQueuePauseLabelAsync(vm, "Resume queue");
            await ResumeQueueForInstallationTestAsync(vm);
        }
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
        Assert.Equal("Too large to inspect (over 512 MB)", vm.InstallationCandidates[1].FileState);

        // An issue naming a product or source that does not exist is inconsistent metadata, not a line to show.
        discovery.Override = Snapshot(new ClientInstallationIssue(ClientInstallationIssueCode.MultipleCandidates, (ClientInstallationProduct)99));
        vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;
        Assert.Equal("Check failed", vm.InstallationObserved); Assert.Empty(vm.InstallationIssues); Assert.Empty(vm.InstallationCandidates);
        discovery.Override = Snapshot(new ClientInstallationIssue(ClientInstallationIssueCode.InvalidHint, Source: (ClientInstallationHintSource)99));
        vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;
        Assert.Equal("Check failed", vm.InstallationObserved); Assert.Empty(vm.InstallationIssues);
    });

    private static readonly PublicRobloxClientRelease CurrentRelease = new("0.744.0.7441080", "version-9ac8595712b447a7", DateTimeOffset.UtcNow);
    private static ClientInstallationSnapshot ReleaseSnapshot(string robloxFolder) => new(DateTimeOffset.UtcNow, ClientInstallationSnapshotStatus.Complete,
        [new(ClientInstallationProduct.Roblox, "C:\\Synthetic\\Roblox\\Versions\\" + robloxFolder + "\\RobloxPlayerBeta.exe", "Roblox", "0.744.0.35509", ClientInstallationFileState.PresentUnverified, [new(ClientInstallationHintSource.VersionDirectory, ClientInstallationProduct.Roblox)]),
         new(ClientInstallationProduct.Froststrap, "C:\\Synthetic\\Froststrap.exe", "Synthetic Froststrap", "1.0.0", ClientInstallationFileState.PresentUnverified, [new(ClientInstallationHintSource.UninstallRegistry, ClientInstallationProduct.Froststrap)])],
        []);

    [Fact]
    public Task RefreshComparesInstalledPlayersWithRobloxsCurrentReleaseAndSaysWhenAnUpdateIsPending() => OnUi(async () =>
    {
        var discovery = new InstallationProbe { Override = ReleaseSnapshot("version-117ad38284844374") };
        var releases = new ReleaseProbe { Result = new(PublicDirectoryState.Success, CurrentRelease, "Public release.") };
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), installationDiscovery: discovery, clientReleases: releases);
        await vm.InitializeAsync(); Assert.Equal(0, releases.Calls);
        vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;

        // One request per explicit refresh, under the refresh's own cancellation.
        Assert.Equal(1, releases.Calls); Assert.True(releases.Token.CanBeCanceled);
        Assert.Equal(new[] { "Not current", "Not compared" }, vm.InstallationCandidates.Select(row => row.ReleaseStatus));
        Assert.StartsWith("Update pending:", vm.InstallationReleaseStatus, StringComparison.Ordinal);
        Assert.Contains("0.744.0.7441080 (version-9ac8595712b447a7)", vm.InstallationReleaseStatus, StringComparison.Ordinal);
        Assert.Contains("Nothing is downloaded or installed unless you choose Download Roblox update", vm.InstallationReleaseStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("never downloads", vm.InstallationReleaseStatus, StringComparison.Ordinal);

        discovery.Override = ReleaseSnapshot("version-9ac8595712b447a7");
        vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;
        Assert.Equal(2, releases.Calls);
        Assert.Equal(new[] { "Current release", "Not compared" }, vm.InstallationCandidates.Select(row => row.ReleaseStatus));
        Assert.StartsWith("Up to date:", vm.InstallationReleaseStatus, StringComparison.Ordinal);

        // Only bootstrapper rows: the release is named but nothing could be compared with it.
        discovery.Override = ReleaseSnapshot("version-9ac8595712b447a7") with { Candidates = [ReleaseSnapshot("x").Candidates[1]] };
        vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;
        Assert.Equal("Roblox's current release is 0.744.0.7441080 (version-9ac8595712b447a7). No installed Roblox player could be compared with it.", vm.InstallationReleaseStatus);

        // Panic clears the comparison with the list.
        vm.Panic.Execute(null);
        Assert.Empty(vm.InstallationCandidates); Assert.Equal("", vm.InstallationReleaseStatus);
        Assert.Empty(vm.Launches);
    });

    private static ClientInstallationCandidate BloxstrapManagedPlayer(string folder) => new(ClientInstallationProduct.Roblox,
        "C:\\Synthetic\\Bloxstrap\\Versions\\" + folder + "\\RobloxPlayerBeta.exe", "Roblox", "0.744.0.35512", ClientInstallationFileState.PresentUnverified,
        [new(ClientInstallationHintSource.VersionDirectory, ClientInstallationProduct.Bloxstrap)]);

    [Fact]
    public Task ABootstrapperManagedPlayerIsSummarizedSeparatelyFromTheNativeRobloxPlayer() => OnUi(async () =>
    {
        const string current = "version-9ac8595712b447a7", stale = "version-117ad38284844374";
        var native = ReleaseSnapshot(stale).Candidates[0];
        var discovery = new InstallationProbe { Override = ReleaseSnapshot(stale) with { Candidates = [native, BloxstrapManagedPlayer(current)] } };
        var releases = new ReleaseProbe { Result = new(PublicDirectoryState.Success, CurrentRelease, "Public release.") };
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), installationDiscovery: discovery, clientReleases: releases);
        await vm.InitializeAsync();
        vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;

        // Each row still says what its own files show; a current Bloxstrap copy never makes a stale native player "Up to date".
        Assert.Equal(new[] { "Not current", "Current release" }, vm.InstallationCandidates.Select(row => row.ReleaseStatus));
        Assert.Equal("Update pending: no native Roblox player matches Roblox's current release 0.744.0.7441080 (version-9ac8595712b447a7). Roblox normally updates when it next starts."
            + " The Bloxstrap-managed player matches that release. Only Roblox's default live channel is checked. Nothing is downloaded or installed unless you choose Download Roblox update and then confirm Install Roblox update.", vm.InstallationReleaseStatus);

        // Only a bootstrapper-managed player: no native headline, and the bootstrapper, not Roblox, is named as the one that updates it.
        discovery.Override = ReleaseSnapshot(stale) with { Candidates = [BloxstrapManagedPlayer(current)] };
        vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;
        Assert.Equal("Roblox's current release is 0.744.0.7441080 (version-9ac8595712b447a7). No native Roblox player could be compared with it."
            + " The Bloxstrap-managed player matches that release.", vm.InstallationReleaseStatus);

        discovery.Override = ReleaseSnapshot(stale) with { Candidates = [BloxstrapManagedPlayer(stale)] };
        vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;
        Assert.Equal("Roblox's current release is 0.744.0.7441080 (version-9ac8595712b447a7). No native Roblox player could be compared with it."
            + " The Bloxstrap-managed player does not match that release; Bloxstrap, not Roblox, decides when that copy updates and may keep an older version."
            + " Only Roblox's default live channel is checked. Nothing is downloaded or installed unless you choose Download Roblox update and then confirm Install Roblox update.", vm.InstallationReleaseStatus);

        // A current native player with a stale managed copy is up to date, and the managed copy is named on its own.
        discovery.Override = ReleaseSnapshot(current) with { Candidates = [ReleaseSnapshot(current).Candidates[0], BloxstrapManagedPlayer(stale)] };
        vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;
        Assert.Equal("Up to date: a native Roblox player matches Roblox's current release 0.744.0.7441080 (version-9ac8595712b447a7)."
            + " The Bloxstrap-managed player does not match that release; Bloxstrap, not Roblox, decides when that copy updates and may keep an older version."
            + " Only Roblox's default live channel is checked. Nothing is downloaded or installed unless you choose Download Roblox update and then confirm Install Roblox update.", vm.InstallationReleaseStatus);
    });

    [Fact]
    public Task AnUnavailableOrInvalidReleaseStillListsTheInstallations() => OnUi(async () =>
    {
        var discovery = new InstallationProbe { Override = ReleaseSnapshot("version-9ac8595712b447a7") };
        var releases = new ReleaseProbe { Result = new(PublicDirectoryState.Failure, null, "The public Roblox service could not be reached.") };
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), installationDiscovery: discovery, clientReleases: releases);
        await vm.InitializeAsync();
        vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;
        Assert.Equal(2, vm.InstallationCandidates.Count); Assert.NotEqual("Check failed", vm.InstallationObserved);
        Assert.All(vm.InstallationCandidates, row => Assert.Equal("Not checked", row.ReleaseStatus));
        Assert.Equal("Could not check Roblox's current release. The public Roblox service could not be reached.", vm.InstallationReleaseStatus);

        // A success carrying a release that fails validation is not a release.
        releases.Result = new(PublicDirectoryState.Success, CurrentRelease with { ClientVersionUpload = "version-..\\x" }, "Public release.");
        vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;
        Assert.Equal(2, vm.InstallationCandidates.Count); Assert.All(vm.InstallationCandidates, row => Assert.Equal("Not checked", row.ReleaseStatus));
        Assert.StartsWith("Could not check Roblox's current release.", vm.InstallationReleaseStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("..", vm.InstallationReleaseStatus, StringComparison.Ordinal);

        // A source that throws is recorded by type and never fails the local list.
        releases.Result = null; releases.Throw = new HttpRequestException("synthetic secret text");
        vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;
        Assert.Equal(2, vm.InstallationCandidates.Count); Assert.NotEqual("Check failed", vm.InstallationObserved);
        Assert.StartsWith("Could not check Roblox's current release.", vm.InstallationReleaseStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic secret", vm.InstallationReleaseStatus, StringComparison.Ordinal);
        Assert.DoesNotContain(vm.Events, text => text.Contains("synthetic secret", StringComparison.Ordinal));

        // An inconsistent local inventory fails before any network request is made.
        int asked = releases.Calls; releases.Throw = null; releases.Result = new(PublicDirectoryState.Success, CurrentRelease, "Public release.");
        discovery.Override = null; discovery.Malformed = true;
        vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;
        Assert.Equal("Check failed", vm.InstallationObserved); Assert.Equal(asked, releases.Calls); Assert.Equal("", vm.InstallationReleaseStatus);
    });

    [Fact]
    public Task LockDuringTheReleaseRequestCancelsItAndPublishesNothing() => OnUi(async () =>
    {
        var discovery = new InstallationProbe { Override = ReleaseSnapshot("version-9ac8595712b447a7") };
        var releases = new ReleaseProbe { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), installationDiscovery: discovery, clientReleases: releases);
        await vm.InitializeAsync(); vm.RefreshInstallations.Execute(null);
        try
        {
            for (int wait = 0; wait < 100 && releases.Calls == 0; wait++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, releases.Calls); Assert.True(vm.IsInstallationDiscoveryBusy);
            vm.LockDashboard();
            Assert.True(releases.Token.IsCancellationRequested);
            vm.Unlock.Execute(null);
        }
        finally { releases.Gate.TrySetResult(new(PublicDirectoryState.Success, CurrentRelease, "Public release.")); }
        await vm.RefreshInstallations.Execution;
        Assert.Empty(vm.InstallationCandidates); Assert.Equal("", vm.InstallationReleaseStatus); Assert.Equal("Not checked", vm.InstallationObserved);
    });

    [Fact]
    public Task TheLocalInstallationListIsUsableWhileRobloxsReleaseIsStillAwaited() => OnUi(async () =>
    {
        // A blackholed or captive network can hold the anonymous release request for its whole timeout;
        // the rows already read from this PC must not wait for it.
        var discovery = new InstallationProbe { Override = ReleaseSnapshot("version-9ac8595712b447a7") };
        var releases = new ReleaseProbe { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), installationDiscovery: discovery, clientReleases: releases);
        await vm.InitializeAsync(); vm.RefreshInstallations.Execute(null);
        try
        {
            for (int wait = 0; wait < 100 && releases.Calls == 0; wait++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, releases.Calls);
            Assert.Equal(new[] { "Not checked", "Not checked" }, vm.InstallationCandidates.Select(row => row.ReleaseStatus));
            Assert.NotEqual("Checking…", vm.InstallationObserved); Assert.StartsWith("Complete", vm.InstallationStatus, StringComparison.Ordinal);
            Assert.StartsWith("Asking Roblox's public version service", vm.InstallationReleaseStatus, StringComparison.Ordinal);

            // The rows can be selected and verified now; only another refresh waits for the answer.
            var player = vm.InstallationCandidates[0]; vm.SelectedInstallation = player;
            Assert.True(vm.CanVerifyInstallationPublisher); Assert.False(vm.CanRefreshInstallations);
            var changed = new List<string?>();
            Assert.IsAssignableFrom<System.ComponentModel.INotifyPropertyChanged>(player).PropertyChanged += (_, args) => changed.Add(args.PropertyName);
            releases.Gate.TrySetResult(new(PublicDirectoryState.Success, CurrentRelease, "Public release."));
            await vm.RefreshInstallations.Execution;

            // The answer fills the Release column in place: the selected row keeps its identity, and so its selection and any verification.
            Assert.Same(player, vm.SelectedInstallation); Assert.Contains(nameof(InstallationCandidateRow.ReleaseStatus), changed);
            Assert.Equal(new[] { "Current release", "Not compared" }, vm.InstallationCandidates.Select(row => row.ReleaseStatus));
            Assert.StartsWith("Up to date:", vm.InstallationReleaseStatus, StringComparison.Ordinal);
            Assert.False(vm.IsInstallationDiscoveryBusy); Assert.True(vm.CanRefreshInstallations);
        }
        finally { releases.Gate.TrySetResult(new(PublicDirectoryState.Failure, null, "Test ended.")); }
    });

    [Fact]
    public Task CancelCheckWhileOnlyTheReleaseIsAwaitedStopsThatRequestAndKeepsTheLocalList() => OnUi(async () =>
    {
        var discovery = new InstallationProbe { Override = ReleaseSnapshot("version-117ad38284844374") };
        var releases = new ReleaseProbe { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), installationDiscovery: discovery, clientReleases: releases);
        await vm.InitializeAsync(); vm.RefreshInstallations.Execute(null);
        string observed;
        try
        {
            for (int wait = 0; wait < 100 && releases.Calls == 0; wait++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, releases.Calls); Assert.Equal(2, vm.InstallationCandidates.Count); Assert.True(vm.IsInstallationDiscoveryBusy);
            observed = vm.InstallationObserved;
            vm.CancelInstallationDiscovery.Execute(null);
            Assert.True(releases.Token.IsCancellationRequested);
        }
        // An answer that still arrives after Cancel check is not applied.
        finally { releases.Gate.TrySetResult(new(PublicDirectoryState.Success, CurrentRelease, "Public release.")); }
        await vm.RefreshInstallations.Execution;
        Assert.Equal(new[] { "Not checked", "Not checked" }, vm.InstallationCandidates.Select(row => row.ReleaseStatus));
        Assert.Equal(observed, vm.InstallationObserved); Assert.StartsWith("Complete", vm.InstallationStatus, StringComparison.Ordinal);
        Assert.StartsWith("Release check stopped.", vm.InstallationReleaseStatus, StringComparison.Ordinal);
        Assert.False(vm.IsInstallationDiscoveryBusy); Assert.True(vm.CanRefreshInstallations);
    });

    [Fact]
    public Task AnInjectedInstallationDiscoveryWithoutAReleaseSourceAsksNoNetworkService() => OnUi(async () =>
    {
        // Tests inject discovery; the app's own public directory must not answer for them, or a refresh would reach Roblox.
        var discovery = new InstallationProbe();
        await using var vm = new ShellViewModel(_root, false, Dispatcher.CurrentDispatcher, new Monitor(), installationDiscovery: discovery);
        await vm.InitializeAsync(); vm.RefreshInstallations.Execute(null); await vm.RefreshInstallations.Execution;
        Assert.Equal(2, vm.InstallationCandidates.Count);
        Assert.All(vm.InstallationCandidates, row => Assert.Equal("Not checked", row.ReleaseStatus));
        Assert.Equal("", vm.InstallationReleaseStatus);
    });

    [Fact]
    public void InstallationListShowsTheReleaseComparisonAndItsSummary()
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProductionMainWindow.xaml"); Assert.NotNull(source);
        var document = XDocument.Load(source!); XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var list = Assert.Single(document.Descendants(ui + "ListView").Where(item => (string?)item.Attribute("AutomationProperties.Name") == "Unverified installation candidates"));
        var bindings = list.Descendants(ui + "GridViewColumn").Select(column => (string?)column.Attribute("DisplayMemberBinding")).ToArray();
        int version = Array.IndexOf(bindings, "{Binding Version}");
        Assert.True(version >= 0 && version + 1 < bindings.Length && bindings[version + 1] == "{Binding ReleaseStatus}", "The installation list has no release column after Version.");
        Assert.Single(document.Descendants(ui + "TextBlock").Where(item => (string?)item.Attribute("Text") == "{Binding InstallationReleaseStatus}"));
        string xaml = document.ToString();
        Assert.Contains("public version service", xaml, StringComparison.Ordinal);
    }

    private sealed class ReleaseProbe : IPublicRobloxClientReleaseDirectory
    {
        public int Calls; public CancellationToken Token; public Exception? Throw;
        public PublicDirectoryResult<PublicRobloxClientRelease>? Result;
        public TaskCompletionSource<PublicDirectoryResult<PublicRobloxClientRelease>>? Gate;
        public Task<PublicDirectoryResult<PublicRobloxClientRelease>> GetCurrentWindowsPlayerReleaseAsync(CancellationToken ct)
        {
            Calls++; Token = ct;
            if (Throw is not null) throw Throw;
            return Gate is not null ? Gate.Task : Task.FromResult(Result!);
        }
    }

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
