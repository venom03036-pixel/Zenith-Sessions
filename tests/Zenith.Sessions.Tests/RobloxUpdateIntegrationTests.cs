using System.IO;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// The real Shell with isolated local data, a synthetic monitor, a fake installation inventory, a fake public release source and
// a fake installer. No process, network, Roblox client or account is touched; the "player" is an empty stand-in file in the
// fixture folder, because the update offer re-checks that a stale player still exists before offering its update.
public sealed class RobloxUpdateIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-roblox-update-" + Guid.NewGuid().ToString("N"));
    private const string StaleFolder = "version-1111111111111111", CurrentFolder = "version-9ac8595712b447a7", NewerFolder = "version-2222222222222222";
    private static readonly ClientExecutableSigner Roblox = new("Roblox Corporation", "Roblox Corporation", new string('B', 64),
        new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private static PublicRobloxClientRelease Release(string upload = CurrentFolder, string version = "0.744.0.7441080") => new(version, upload, DateTimeOffset.UtcNow);
    private string SettingsPath => Path.Combine(_root, "settings.json");

    [Fact]
    public Task AutomaticChecksAreOffByDefaultAndAnOlderSettingsFileStillLoads() => OnUi(async () =>
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(SettingsPath, "{\"Version\":1,\"Theme\":\"ocean-blue\",\"IdleLockMinutes\":0}");
        var releases = new Releases { Answer = Release() }; var inventory = new Inventory(_root, StaleFolder); var installer = new Installer();
        await using var shell = await ReadyAsync(new Monitor(), releases, inventory, installer);
        Assert.False(shell.CheckRobloxUpdates); Assert.Null(shell.ClientUpdateCheckDue);
        Assert.Equal(0, releases.Calls); Assert.Equal(0, inventory.Calls);
        Assert.False(shell.CanDownloadRobloxUpdate); Assert.False(shell.CanInstallRobloxUpdate); Assert.False(shell.IsRobloxUpdateBusy);
        Assert.Equal("", shell.RobloxUpdateConfirmation);
        // A tick with the preference off does nothing (the timer is never armed; this is the body it would run).
        await shell.RunAutomaticUpdateCheckNowAsync();
        Assert.Equal(0, releases.Calls); Assert.Equal(0, inventory.Calls); Assert.Null(shell.ClientUpdateCheckDue);
        Assert.Equal("Settings saved", shell.SaveStatus);
    });

    [Fact]
    public Task SavingTheCheckOnRunsItThreeSecondsAfterStartThenEveryThirtyMinutesAndAnnouncesAPendingUpdateOnce() => OnUi(async () =>
    {
        var releases = new Releases { Answer = Release() }; var inventory = new Inventory(_root, StaleFolder); var installer = new Installer();
        await using var shell = await ReadyAsync(new Monitor(), releases, inventory, installer);
        shell.CheckRobloxUpdates = true; Assert.Equal("Unsaved settings", shell.SaveStatus);
        shell.SaveSettings.Execute(null); await shell.SaveSettings.Execution;
        Assert.Contains("\"CheckRobloxUpdates\":true", await File.ReadAllTextAsync(SettingsPath), StringComparison.Ordinal);
        Assert.Equal(RobloxClientUpdate.CheckDelay, shell.ClientUpdateCheckDue);

        // Nothing is selected, so the check is a full refresh: the rows are read and compared with Roblox's current release.
        await shell.RunAutomaticUpdateCheckNowAsync();
        Assert.Equal(1, inventory.Calls); Assert.Equal(1, releases.Calls);
        Assert.Equal(RobloxClientUpdate.CheckInterval, shell.ClientUpdateCheckDue);
        Assert.Equal("Not current", Assert.Single(shell.InstallationCandidates).ReleaseStatus);
        Assert.StartsWith("Update pending:", shell.InstallationReleaseStatus, StringComparison.Ordinal);
        Assert.Contains("Roblox update available", shell.Notice, StringComparison.Ordinal); Assert.Contains("0.744.0.7441080", shell.Notice, StringComparison.Ordinal);
        Assert.Contains("Last automatic check", shell.RobloxUpdateCheckStatus, StringComparison.Ordinal);
        Assert.True(shell.CanDownloadRobloxUpdate); Assert.False(shell.CanInstallRobloxUpdate); Assert.Equal(0, installer.Downloads);

        // The same release again is checked but not announced a second time.
        shell.Inform("Something else.");
        await shell.RunAutomaticUpdateCheckNowAsync();
        Assert.Equal(2, releases.Calls); Assert.Equal("Something else.", shell.Notice);
        Assert.Equal(1, shell.Events.Count(line => line.Contains("Roblox update available", StringComparison.Ordinal)));

        // A failed read keeps the rows and says the check could not finish; the next check is still scheduled.
        releases.Answer = null;
        await shell.RunAutomaticUpdateCheckNowAsync();
        Assert.Equal(3, releases.Calls); Assert.Contains("could not", shell.RobloxUpdateCheckStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RobloxClientUpdate.CheckInterval, shell.ClientUpdateCheckDue);

        // Saved off again: the timer stops and nothing is checked.
        shell.CheckRobloxUpdates = false; shell.SaveSettings.Execute(null); await shell.SaveSettings.Execution;
        Assert.Null(shell.ClientUpdateCheckDue);
        Assert.Contains("\"CheckRobloxUpdates\":false", await File.ReadAllTextAsync(SettingsPath), StringComparison.Ordinal);
        await shell.RunAutomaticUpdateCheckNowAsync(); Assert.Equal(3, releases.Calls);
    });

    [Fact]
    public Task WithASelectedRowTheAutomaticCheckReadsOnlyTheReleaseAndKeepsTheSelection() => OnUi(async () =>
    {
        var releases = new Releases { Answer = Release(StaleFolder) }; var inventory = new Inventory(_root, StaleFolder); var installer = new Installer();
        await using var shell = await ReadyAsync(new Monitor(), releases, inventory, installer);
        await SaveCheckOnAsync(shell);
        await shell.RunAutomaticUpdateCheckNowAsync();
        var row = Assert.Single(shell.InstallationCandidates);
        Assert.Equal("Current release", row.ReleaseStatus); Assert.False(shell.CanDownloadRobloxUpdate);
        shell.SelectedInstallation = row;

        // A selected row is someone's trial installation: the check must not clear the list, only ask Roblox again and fill the rows in place.
        releases.Answer = Release(NewerFolder, "0.745.0.7451137");
        await shell.RunAutomaticUpdateCheckNowAsync();
        Assert.Equal(1, inventory.Calls); Assert.Equal(2, releases.Calls);
        Assert.Same(row, shell.SelectedInstallation); Assert.Same(row, Assert.Single(shell.InstallationCandidates));
        Assert.Equal("Not current", row.ReleaseStatus);
        Assert.Contains("0.745.0.7451137", shell.Notice, StringComparison.Ordinal);
        Assert.True(shell.CanDownloadRobloxUpdate);
    });

    [Fact]
    public Task DownloadVerifiesThroughTheInstallerAndInstallAsksAboutRunningClientsThenRefreshes() => OnUi(async () =>
    {
        var monitor = new Monitor { Count = 2 }; var releases = new Releases { Answer = Release() }; var inventory = new Inventory(_root, StaleFolder); var installer = new Installer();
        await using var shell = await ReadyAsync(monitor, releases, inventory, installer);
        await SaveCheckOnAsync(shell); await shell.RunAutomaticUpdateCheckNowAsync();
        await RefreshDashboardAsync(shell); Assert.Equal(2, shell.Sessions.Count);
        var offered = Assert.Single(shell.InstallationCandidates).ComparedWith; Assert.NotNull(offered);

        shell.DownloadRobloxUpdate.Execute(null); await shell.DownloadRobloxUpdate.Execution;
        Assert.Equal(1, installer.Downloads); Assert.Equal(offered!.ClientVersionUpload, installer.Requested!.ClientVersionUpload);
        Assert.Contains("verified", shell.ClientUpdateStatus, StringComparison.Ordinal); Assert.Contains("SHA-256", shell.ClientUpdateStatus, StringComparison.Ordinal);
        Assert.Contains("Roblox Corporation", shell.ClientUpdateStatus, StringComparison.Ordinal); Assert.Contains("4.0 MB", shell.ClientUpdateStatus, StringComparison.Ordinal);
        Assert.True(shell.CanInstallRobloxUpdate); Assert.False(shell.IsRobloxUpdateBusy); Assert.Equal("", shell.ErrorNotice);

        // The question names the release, the verified publisher and the clients the installer will close.
        string ask = shell.RobloxUpdateConfirmation;
        Assert.Contains("0.744.0.7441080", ask, StringComparison.Ordinal); Assert.Contains("Roblox Corporation", ask, StringComparison.Ordinal);
        Assert.Contains("2 Roblox clients", ask, StringComparison.Ordinal); Assert.Contains("closes every running Roblox client", ask, StringComparison.Ordinal);
        monitor.Count = 0; await RefreshDashboardAsync(shell); Assert.Empty(shell.Sessions);
        Assert.Contains("No Roblox client is running", shell.RobloxUpdateConfirmation, StringComparison.Ordinal);

        // The run goes through the installer; afterwards the installations are refreshed so the rows show what is installed now.
        inventory.Folder = CurrentFolder;
        shell.InstallRobloxUpdate.Execute(null); await shell.InstallRobloxUpdate.Execution;
        Assert.Equal(1, installer.Installs); Assert.Contains("finished", shell.ClientUpdateStatus, StringComparison.Ordinal);
        Assert.Equal(2, inventory.Calls); Assert.Equal("Current release", Assert.Single(shell.InstallationCandidates).ReleaseStatus);
        Assert.False(shell.CanInstallRobloxUpdate); Assert.False(shell.CanDownloadRobloxUpdate); Assert.Equal("", shell.RobloxUpdateConfirmation);
        Assert.Contains("Roblox's installer finished", shell.Notice, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, shell.ClientUpdateStatus, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public Task AnUnverifiedOrFailedDownloadIsReportedAsAnErrorAndCannotBeInstalled() => OnUi(async () =>
    {
        var releases = new Releases { Answer = Release() }; var inventory = new Inventory(_root, StaleFolder);
        var installer = new Installer { DownloadResult = new(ClientUpdateDownloadOutcome.NotVerified, "Synthetic: not verified.", 4096, TrustReason: ClientExecutableTrustReason.Unsigned) };
        await using var shell = await ReadyAsync(new Monitor(), releases, inventory, installer);
        await SaveCheckOnAsync(shell); await shell.RunAutomaticUpdateCheckNowAsync();
        shell.DownloadRobloxUpdate.Execute(null); await shell.DownloadRobloxUpdate.Execution;
        Assert.Equal(1, installer.Downloads);
        Assert.Contains("not verified", shell.ErrorNotice, StringComparison.OrdinalIgnoreCase); Assert.Contains("deleted", shell.ErrorNotice, StringComparison.OrdinalIgnoreCase);
        Assert.False(shell.CanInstallRobloxUpdate); Assert.Equal("", shell.RobloxUpdateConfirmation); Assert.True(shell.CanDownloadRobloxUpdate);
        shell.InstallRobloxUpdate.Execute(null); await shell.InstallRobloxUpdate.Execution; Assert.Equal(0, installer.Installs);

        installer.DownloadResult = new(ClientUpdateDownloadOutcome.Failed, "Synthetic: the request failed.", Failure: new System.Net.Http.HttpRequestException("synthetic"));
        shell.DownloadRobloxUpdate.Execute(null); await shell.DownloadRobloxUpdate.Execution;
        Assert.Equal(2, installer.Downloads); Assert.Contains("Synthetic: the request failed.", shell.ErrorNotice, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic\"", shell.ErrorNotice, StringComparison.Ordinal); Assert.False(shell.CanInstallRobloxUpdate);
        Assert.Contains(shell.Events, line => line.Contains("HttpRequestException", StringComparison.Ordinal));
    });

    [Fact]
    public Task AnInstallerStillRunningAfterTheWaitIsReportedAndNothingIsRefreshed() => OnUi(async () =>
    {
        var releases = new Releases { Answer = Release() }; var inventory = new Inventory(_root, StaleFolder);
        var installer = new Installer { InstallResult = new(ClientUpdateInstallOutcome.StillRunning, null, "Synthetic: still running.") };
        await using var shell = await ReadyAsync(new Monitor(), releases, inventory, installer);
        await SaveCheckOnAsync(shell); await shell.RunAutomaticUpdateCheckNowAsync();
        shell.DownloadRobloxUpdate.Execute(null); await shell.DownloadRobloxUpdate.Execution;
        shell.InstallRobloxUpdate.Execute(null); await shell.InstallRobloxUpdate.Execution;
        Assert.Equal(1, installer.Installs); Assert.Contains("still running", shell.ClientUpdateStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, inventory.Calls); Assert.False(shell.CanInstallRobloxUpdate);
    });

    [Fact]
    public Task PanicStopsADownloadAndLockDiscardsAHeldInstaller() => OnUi(async () =>
    {
        var releases = new Releases { Answer = Release() }; var inventory = new Inventory(_root, StaleFolder);
        var installer = new Installer { DownloadGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var shell = await ReadyAsync(new Monitor(), releases, inventory, installer);
        await SaveCheckOnAsync(shell); await shell.RunAutomaticUpdateCheckNowAsync();
        shell.DownloadRobloxUpdate.Execute(null);
        Assert.True(shell.IsRobloxUpdateBusy); Assert.False(shell.CanDownloadRobloxUpdate);
        shell.Panic.Execute(null);
        Assert.True(installer.Token.IsCancellationRequested);
        installer.DownloadGate.TrySetResult(); await shell.DownloadRobloxUpdate.Execution;
        Assert.False(shell.IsRobloxUpdateBusy); Assert.Contains("stopped", shell.ClientUpdateStatus, StringComparison.OrdinalIgnoreCase);
        Assert.False(shell.CanInstallRobloxUpdate); Assert.Contains("Panic stop", shell.Notice, StringComparison.Ordinal);
        // Panic cleared the installation list; the automatic check (a read, not automation) stays scheduled and re-lists it.
        Assert.Equal(RobloxClientUpdate.CheckInterval, shell.ClientUpdateCheckDue);
        installer.DownloadGate = null;
        await shell.RunAutomaticUpdateCheckNowAsync(); Assert.Equal(2, inventory.Calls); Assert.True(shell.CanDownloadRobloxUpdate);

        shell.DownloadRobloxUpdate.Execute(null); await shell.DownloadRobloxUpdate.Execution;
        Assert.True(shell.CanInstallRobloxUpdate); Assert.True(installer.HasVerifiedInstaller);
        shell.LockDashboard();
        Assert.False(installer.HasVerifiedInstaller); Assert.Equal(1, installer.Discards); Assert.Null(shell.ClientUpdateCheckDue);
        shell.Unlock.Execute(null);
        Assert.False(shell.CanInstallRobloxUpdate); Assert.Equal("", shell.RobloxUpdateConfirmation);
        Assert.Equal(RobloxClientUpdate.CheckDelay, shell.ClientUpdateCheckDue);
    });

    [Fact]
    public Task TheOfferWaitsWhileAPublisherCheckIsRunning() => OnUi(async () =>
    {
        var releases = new Releases { Answer = Release() }; var inventory = new Inventory(_root, StaleFolder); var installer = new Installer();
        var trust = new GatedTrust();
        await using var shell = await ReadyAsync(new Monitor(), releases, inventory, installer, trust: trust);
        await SaveCheckOnAsync(shell); await shell.RunAutomaticUpdateCheckNowAsync();
        Assert.True(shell.CanDownloadRobloxUpdate);
        var row = Assert.Single(shell.InstallationCandidates); shell.SelectedInstallation = row;
        shell.VerifyInstallationPublisher.Execute(null);
        try
        {
            Assert.True(shell.IsPublisherVerificationBusy); Assert.False(shell.CanDownloadRobloxUpdate);
            shell.DownloadRobloxUpdate.Execute(null); await shell.DownloadRobloxUpdate.Execution; Assert.Equal(0, installer.Downloads);
        }
        finally { trust.Gate.TrySetResult(); }
        await shell.VerifyInstallationPublisher.Execution;
        Assert.True(shell.CanDownloadRobloxUpdate);
    });

    [Fact]
    public Task SampleModeNeverChecksDownloadsOrOffers() => OnUi(async () =>
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(SettingsPath, "{\"Version\":1,\"Theme\":\"graphite-mint\",\"CheckRobloxUpdates\":true}");
        var releases = new Releases { Answer = Release() }; var inventory = new Inventory(_root, StaleFolder);
        await using var shell = await ReadyAsync(new Monitor(), releases, inventory, installer: null, sample: true);
        Assert.True(shell.CheckRobloxUpdates); Assert.Null(shell.ClientUpdateCheckDue);
        await shell.RunAutomaticUpdateCheckNowAsync();
        Assert.Equal(0, releases.Calls); Assert.Equal(0, inventory.Calls);
        Assert.False(shell.CanDownloadRobloxUpdate); Assert.False(shell.CanInstallRobloxUpdate);
    });

    private async Task<ShellViewModel> ReadyAsync(Monitor monitor, Releases releases, Inventory inventory, Installer? installer, bool sample = false, IClientExecutableTrustVerifier? trust = null)
    {
        var shell = new ShellViewModel(_root, sample, Dispatcher.CurrentDispatcher, monitor, installationDiscovery: inventory, clientReleases: releases,
            clientUpdateInstaller: installer, executableTrust: trust);
        await shell.InitializeAsync();
        return shell;
    }
    private static async Task SaveCheckOnAsync(ShellViewModel shell)
    {
        shell.CheckRobloxUpdates = true; shell.SaveSettings.Execute(null); await shell.SaveSettings.Execution;
        Assert.Equal(RobloxClientUpdate.CheckDelay, shell.ClientUpdateCheckDue);
    }
    private static async Task RefreshDashboardAsync(ShellViewModel shell)
    { shell.Refresh.Execute(null); await shell.Refresh.Execution; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }

    private sealed class Installer : IClientUpdateInstaller
    {
        public int Downloads, Installs, Discards, Disposals; public PublicRobloxClientRelease? Requested; public CancellationToken Token;
        public ClientUpdateDownloadResult DownloadResult = new(ClientUpdateDownloadOutcome.Verified, "Synthetic: verified.", 4 * 1024 * 1024, new string('C', 64), Roblox);
        public ClientUpdateInstallResult InstallResult = new(ClientUpdateInstallOutcome.Exited, 0, "Synthetic: exited.");
        public TaskCompletionSource? DownloadGate;
        public bool HasVerifiedInstaller { get; private set; }
        public async Task<ClientUpdateDownloadResult> DownloadAsync(PublicRobloxClientRelease release, IProgress<ClientUpdateDownloadProgress>? progress, CancellationToken ct)
        {
            Downloads++; Requested = release; Token = ct; HasVerifiedInstaller = false;
            if (DownloadGate is { } gate) await gate.Task.WaitAsync(ct);
            progress?.Report(new(1024, 4 * 1024 * 1024));
            HasVerifiedInstaller = DownloadResult.Outcome == ClientUpdateDownloadOutcome.Verified;
            return DownloadResult;
        }
        public Task<ClientUpdateInstallResult> InstallAsync(CancellationToken ct)
        {
            Installs++; Token = ct;
            if (!HasVerifiedInstaller) return Task.FromResult(new ClientUpdateInstallResult(ClientUpdateInstallOutcome.NotStarted, null, "Synthetic: nothing held."));
            HasVerifiedInstaller = false; return Task.FromResult(InstallResult);
        }
        public void Discard() { Discards++; HasVerifiedInstaller = false; }
        public ValueTask DisposeAsync() { Disposals++; HasVerifiedInstaller = false; return default; }
    }
    private sealed class GatedTrust : IClientExecutableTrustVerifier
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ClientExecutableTrustResult> VerifyAsync(ClientInstallationCandidate candidate, CancellationToken ct)
        {
            await Gate.Task.WaitAsync(ct);
            return new(candidate.Product, candidate.ExecutablePath, ClientExecutableTrustStatus.Unknown, ClientExecutableTrustReason.VerificationUnavailable, DateTimeOffset.UtcNow);
        }
        public ValueTask DisposeAsync() => default;
    }
    private sealed class Releases : IPublicRobloxClientReleaseDirectory
    {
        public int Calls; public PublicRobloxClientRelease? Answer;
        public Task<PublicDirectoryResult<PublicRobloxClientRelease>> GetCurrentWindowsPlayerReleaseAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            return Task.FromResult(Answer is null ? new PublicDirectoryResult<PublicRobloxClientRelease>(PublicDirectoryState.Failure, null, "Synthetic failure.")
                : new(PublicDirectoryState.Success, Answer, "Synthetic public release."));
        }
    }
    private sealed class Inventory(string root, string folder) : IClientInstallationDiscovery
    {
        public int Calls; public string Folder = folder;
        public Task<ClientInstallationSnapshot> DiscoverAsync(CancellationToken ct)
        {
            Calls++;
            // An empty stand-in file, never a program: the offer only asks whether the stale player still exists.
            string player = Path.Combine(root, "Roblox", "Versions", Folder, "RobloxPlayerBeta.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(player)!);
            if (!File.Exists(player)) File.WriteAllBytes(player, []);
            return Task.FromResult(new ClientInstallationSnapshot(DateTimeOffset.UtcNow, ClientInstallationSnapshotStatus.Complete,
                [new(ClientInstallationProduct.Roblox, player, "Synthetic Roblox", "0.744.0.35509", ClientInstallationFileState.PresentUnverified,
                    [new(ClientInstallationHintSource.VersionDirectory, ClientInstallationProduct.Roblox)])], []));
        }
    }
    private sealed class Monitor : IDesktopMonitor
    {
        public int Count;
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        {
            var now = DateTimeOffset.UtcNow;
            var rows = Enumerable.Range(0, Count).Select(index => new SessionObservation(70001 + index, now.AddMinutes(-5).UtcTicks, "Synthetic", 0, "",
                false, true, 1, 1, null, now)).ToArray();
            return Task.FromResult(new DesktopSnapshot(new(now, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 10), rows,
                ProcessObservedUtc: now, ProcessRevision: 1, ProcessInventoryComplete: true));
        }
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
    }
    private static Task OnUi(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.InvokeShutdown(); }
            }); Dispatcher.Run();
        }) { IsBackground = true }; thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    public void Dispose()
    {
        string full = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(full) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(full).StartsWith("zenith-roblox-update-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
