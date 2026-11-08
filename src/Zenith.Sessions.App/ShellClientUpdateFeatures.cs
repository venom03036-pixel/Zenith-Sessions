using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

/// <summary>
/// Settings > Roblox installation > Roblox updates. Two separate things, both off until chosen:
/// (1) An optional automatic check (the saved CheckRobloxUpdates preference, off by default): 3 seconds after the manager is
/// ready and every 30 minutes after a check finishes, Zenith reads Roblox's current public release, the same anonymous read
/// Refresh installation makes. With no installation row selected it runs the full refresh (rows and release); with a row
/// selected (someone's trial installation) it only asks for the release and fills the listed rows in place, so the selection and
/// any native preparation are kept. A pending update is announced once per release. One one-shot DispatcherTimer, re-armed after
/// each check (Rule 14); nothing polls while off, locked, suspended by Panic or in the sample profile.
/// (2) An explicit two-step install. Download Roblox update fetches Roblox's own installer for the offered release from Roblox's
/// setup host into a fresh temporary folder and verifies it with the executable trust code; the result (publisher, SHA-256) is
/// shown and the verified file stays held with read sharing. Install Roblox update asks first (the view shows
/// <see cref="RobloxUpdateConfirmation"/>, which warns about running clients), then runs that held file and waits for it. Both
/// steps wait while an installation check, a publisher check or a native trial is in progress. Panic and Lock cancel a download or
/// the wait (the installer itself is never closed) and discard a held installer; Lock also idles the automatic check until Unlock.
/// Nothing here writes Roblox's settings or files; only Roblox's own installer, verified, is ever run.
/// </summary>
public sealed partial class ShellViewModel
{
    private const string ClientUpdateIdle = "Download Roblox update fetches Roblox's own installer for the current release into a temporary folder and checks its signature. Nothing is run until you choose Install Roblox update and confirm.";
    private const string ClientUpdateDownloadStopped = "Download stopped. Nothing was run.";
    private IClientUpdateInstaller? _clientUpdateInstaller;
    private bool _clientUpdatePermitted, _checkRobloxUpdates, _clientUpdateBusy, _automaticUpdateCheckRunning, _clientUpdateChecksPaused;
    private DispatcherTimer? _clientUpdateCheckTimer;
    private CancellationTokenSource? _clientUpdateStop;
    private CancellationTokenSource? _automaticUpdateStop;
    private Task _clientUpdateWork = Task.CompletedTask, _automaticUpdateCheck = Task.CompletedTask;
    private long _clientUpdateGeneration;
    private long _clientUpdateCheckGeneration;
    // The release the listed rows say the native player is behind (null: no update offered), and the release a verified
    // installer is held for. The installer component holds the file; this only remembers which release it belongs to.
    private PublicRobloxClientRelease? _offeredRelease, _heldRelease;
    private string? _announcedUpdateUpload;
    private string _clientUpdateStatus = ClientUpdateIdle, _robloxUpdateCheckStatus = "";

    /// <summary>Settings draft of the saved "Check for Roblox updates automatically" preference; only the saved value is applied.</summary>
    public bool CheckRobloxUpdates { get => _checkRobloxUpdates; set { if (Set(ref _checkRobloxUpdates, value)) PreferencesEdited(); } }
    public string ClientUpdateStatus { get => _clientUpdateStatus; private set => Set(ref _clientUpdateStatus, value); }
    public string RobloxUpdateCheckStatus { get => _robloxUpdateCheckStatus; private set => Set(ref _robloxUpdateCheckStatus, value); }
    /// <summary>A download, a verification or the wait for a started installer is in progress; Cancel stops it.</summary>
    public bool IsRobloxUpdateBusy => _clientUpdateBusy;
    private bool ClientUpdateAvailable => _clientUpdatePermitted && _ready && !IsLocked && !_shuttingDown && !_clientUpdateBusy && !ClientUpdateLaunchWorkActive;
    // An installation check replaces the rows the offer came from; a publisher check or native trial owns the selected player.
    private bool ClientUpdateLaunchWorkActive => IsInstallationDiscoveryBusy || IsPublisherVerificationBusy || HasNativeTrialInProgress();
    public bool CanDownloadRobloxUpdate => ClientUpdateAvailable && _offeredRelease is not null;
    public bool CanInstallRobloxUpdate => ClientUpdateAvailable && SameUpdateRelease(_heldRelease, _offeredRelease);
    private static bool SameUpdateRelease(PublicRobloxClientRelease? left, PublicRobloxClientRelease? right) => left is not null && right is not null
        && string.Equals(left.ClientVersionUpload, right.ClientVersionUpload, StringComparison.Ordinal);
    /// <summary>The question the view asks before Install Roblox update; empty while nothing verified is held.</summary>
    public string RobloxUpdateConfirmation
    {
        get
        {
            if (!CanInstallRobloxUpdate || _heldRelease is not { } release) return "";
            int running = Sessions.Count;
            string clients = running == 0 ? "No Roblox client is running as far as Zenith can see."
                : (running == 1 ? "1 Roblox client is" : running.ToString(CultureInfo.CurrentCulture) + " Roblox clients are")
                    + " running: Roblox's installer closes every running Roblox client while it updates, and anything unsaved in them is lost.";
            return $"Run Roblox's installer for release {release.Version} now? Windows verified its signature from Roblox Corporation. {clients}"
                + " Zenith only starts the official installer and waits for it to finish; it changes no Roblox setting or file itself.";
        }
    }
    /// <summary>Tests: how long the armed automatic check waits, or null when none is armed.</summary>
    internal TimeSpan? ClientUpdateCheckDue => _clientUpdateCheckTimer is { IsEnabled: true } timer ? timer.Interval : null;
    public AsyncCommand DownloadRobloxUpdate { get; private set; } = null!;
    public AsyncCommand InstallRobloxUpdate { get; private set; } = null!;
    public Command CancelRobloxUpdate { get; private set; } = null!;

    private void InitializeClientUpdateFeatures(IClientUpdateInstaller? installer)
    {
        // The sample profile never downloads or runs anything unless a test injects an installer.
        _clientUpdatePermitted = !IsSample || installer is not null;
        _clientUpdateInstaller = installer ?? (IsSample ? null : new WindowsRobloxClientUpdateInstaller());
        DownloadRobloxUpdate = new(_ => _clientUpdateWork = DownloadRobloxUpdateAsync(),
            error => ReportClientUpdateFailure(error, "The Roblox update download could not finish. Nothing was run."));
        InstallRobloxUpdate = new(_ => _clientUpdateWork = InstallRobloxUpdateAsync(),
            error => ReportClientUpdateFailure(error, "Roblox's installer could not be run or waited for. Refresh installation to see what is installed."));
        CancelRobloxUpdate = new(_ => CancelClientUpdateWork());
        PropertyChanged += ClientUpdateOwnerChanged;
    }

    private void ClientUpdateOwnerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(IsReady) or nameof(IsLocked) or nameof(IsInstallationDiscoveryBusy) or nameof(IsPublisherVerificationBusy)
            or nameof(SelectedInstallation) or nameof(NativeTrialStatus)) RaiseClientUpdateAvailability();
    }
    private void RaiseClientUpdateAvailability()
    { Raise(nameof(CanDownloadRobloxUpdate)); Raise(nameof(CanInstallRobloxUpdate)); Raise(nameof(RobloxUpdateConfirmation)); Raise(nameof(IsRobloxUpdateBusy)); }

    // ----- The automatic check -----

    // UI thread, after settings load, after a save that changed the preference, and after Unlock. restart arms the first check
    // CheckDelay from now; otherwise an armed timer keeps its schedule. Off, locked, closing or the sample profile: nothing is armed.
    private void ApplySavedClientUpdateCheck(bool restart)
    {
        if (restart) { _clientUpdateChecksPaused = false; _clientUpdateCheckGeneration++; _automaticUpdateStop?.Cancel(); }
        if (!_savedSettings.CheckRobloxUpdates || !_ready || IsLocked || _shuttingDown || IsSample || !_clientUpdatePermitted || _clientUpdateChecksPaused)
        {
            StopClientUpdateCheckTimer();
            _automaticUpdateStop?.Cancel();
            RobloxUpdateCheckStatus = _savedSettings.CheckRobloxUpdates && IsSample ? "Automatic checks are not made in the sample profile." : "";
            return;
        }
        if (!restart && _clientUpdateCheckTimer is { IsEnabled: true }) return;
        ArmClientUpdateCheck(RobloxClientUpdate.CheckDelay);
        RobloxUpdateCheckStatus = "Next automatic check in 3 seconds, then every 30 minutes after each check.";
    }
    private void ClientUpdateCheckPreferenceSaved(bool wasOn) { if (_savedSettings.CheckRobloxUpdates != wasOn) ApplySavedClientUpdateCheck(restart: true); }
    private void ArmClientUpdateCheck(TimeSpan due)
    {
        if (_clientUpdateCheckTimer is null) { _clientUpdateCheckTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher); _clientUpdateCheckTimer.Tick += ClientUpdateCheckTick; }
        _clientUpdateCheckTimer.Stop(); _clientUpdateCheckTimer.Interval = due; _clientUpdateCheckTimer.Start();
    }
    private void StopClientUpdateCheckTimer() => _clientUpdateCheckTimer?.Stop();
    // One-shot: the check re-arms the timer when it finishes, so a slow read never overlaps the next one (Rule 14).
    private void ClientUpdateCheckTick(object? sender, EventArgs e) { _clientUpdateCheckTimer?.Stop(); _automaticUpdateCheck = RunAutomaticUpdateCheckAsync(); }
    /// <summary>Tests: runs the body the armed timer would run, now.</summary>
    internal Task RunAutomaticUpdateCheckNowAsync() { _clientUpdateCheckTimer?.Stop(); return _automaticUpdateCheck = RunAutomaticUpdateCheckAsync(); }

    private async Task RunAutomaticUpdateCheckAsync()
    {
        if (_automaticUpdateCheckRunning || !_savedSettings.CheckRobloxUpdates || !_ready || IsLocked || _shuttingDown || IsSample || !_clientUpdatePermitted || _clientUpdateChecksPaused) return;
        _automaticUpdateCheckRunning = true;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _automaticUpdateStop = stop;
        long checkGeneration = _clientUpdateCheckGeneration;
        bool Current() => !IsLocked && !_shuttingDown && !_clientUpdateChecksPaused && _savedSettings.CheckRobloxUpdates
            && checkGeneration == _clientUpdateCheckGeneration && !stop.IsCancellationRequested;
        try
        {
            string? problem = null;
            if (_clientReleases is null) problem = "no release source is available in this configuration";
            else if (_clientUpdateBusy) problem = "skipped while a download or installation is in progress";
            else if (SelectedInstallation is null && CanRefreshInstallations)
            {
                RobloxUpdateCheckStatus = "Automatic check: refreshing installations and asking Roblox's public version service for its current release…";
                RefreshInstallations.Execute(null); await RefreshInstallations.Execution;
                if (!Current()) return;
                if (InstallationCandidates.Count == 0) problem = "no Roblox installation was found";
                else if (!InstallationCandidates.Any(row => row.ComparedWith is not null)) problem = "Roblox's current release could not be read";
            }
            else if (SelectedInstallation is not null)
            {
                // A selected row is a trial installation: ask for the release only and fill the listed rows in place.
                RobloxUpdateCheckStatus = "Automatic check: asking Roblox's public version service for its current release…";
                stop.CancelAfter(TimeSpan.FromSeconds(15));
                var result = await ReadCurrentReleaseAsync(stop.Token);
                if (!Current()) return;
                var latest = result is { State: PublicDirectoryState.Success } && RobloxClientRelease.IsValid(result.Value) ? result.Value : null;
                if (latest is null) problem = "Roblox's current release could not be read";
                else { _releaseCache?.Remember(latest); PublishLaunchRelease(); }
            }
            else problem = "skipped while an installation check, a publisher check or a native trial is in progress";
            if (!Current()) return;
            RefreshUpdateOffer(announce: true);
            string outcome = problem ?? (_offeredRelease is { } offered ? $"an update to release {offered.Version} is pending" : "the installed Roblox player is current or nothing could be compared");
            RobloxUpdateCheckStatus = $"Last automatic check {DateTime.Now:T}: {outcome}. Next in 30 minutes.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            _diagnostics.RecordFailure(ManagerSubsystem.ClientUpdate, error.GetType());
            if (Current()) RobloxUpdateCheckStatus = $"Last automatic check {DateTime.Now:T} failed. Next in 30 minutes.";
        }
        finally
        {
            if (ReferenceEquals(_automaticUpdateStop, stop)) _automaticUpdateStop = null;
            _automaticUpdateCheckRunning = false;
            if (_savedSettings.CheckRobloxUpdates && _ready && !IsLocked && !_shuttingDown && !IsSample && _clientUpdatePermitted
                && !_clientUpdateChecksPaused && _clientUpdateCheckTimer is not { IsEnabled: true })
                ArmClientUpdateCheck(checkGeneration == _clientUpdateCheckGeneration ? RobloxClientUpdate.CheckInterval : RobloxClientUpdate.CheckDelay);
        }
    }

    // Dispatcher, after any change to what the listed rows show about Roblox's release. An update is offered when a native (not
    // bootstrapper-managed) player row reads "Not current", no native row reads "Current release", and that stale player's file
    // still exists (a player updated outside Zenith leaves its old version folder gone; refresh then re-lists). The release offered is
    // the one the stale row was compared with. A held installer for a release the rows no longer point at is discarded.
    private void RefreshUpdateOffer(bool announce)
    {
        PublicRobloxClientRelease? offered = null; bool current = false;
        foreach (var row in InstallationCandidates)
        {
            if (row.ManagedBy is not null) continue;
            if (row.Release == ClientReleaseMatch.Current) { current = true; break; }
            if (row.Release == ClientReleaseMatch.NotCurrent && offered is null && row.ComparedWith is { } compared && File.Exists(row.Candidate.ExecutablePath)) offered = compared;
        }
        if (current) offered = null;
        if (_heldRelease is { } held && (current || offered is not null && !SameUpdateRelease(offered, held)))
        {
            DiscardHeldInstaller();
            ClientUpdateStatus = $"The downloaded installer for release {held.Version} no longer matches the installed player or Roblox's current release, so it was deleted. Nothing was run.";
        }
        _offeredRelease = offered;
        if (offered is not null && announce && !string.Equals(offered.ClientVersionUpload, _announcedUpdateUpload, StringComparison.Ordinal))
        {
            _announcedUpdateUpload = offered.ClientVersionUpload;
            string text = $"Roblox update available: release {offered.Version}. In Settings > Roblox installation, choose Download Roblox update to fetch Roblox's installer and check its signature; nothing is installed until you confirm.";
            AddEvent(text); Inform(text);
        }
        RaiseClientUpdateAvailability();
    }

    // ----- Download and install -----

    private async Task DownloadRobloxUpdateAsync()
    {
        if (!CanDownloadRobloxUpdate || _offeredRelease is not { } release || _clientUpdateInstaller is not { } installer) return;
        var stop = BeginClientUpdateWork($"Downloading Roblox's installer for release {release.Version} from {RobloxClientUpdate.SetupHost}…");
        long generation = _clientUpdateGeneration;
        _heldRelease = null;
        try
        {
            var progress = new DispatcherProgress(_dispatcher, value =>
            {
                if (stop.IsCancellationRequested || generation != _clientUpdateGeneration) return;
                ClientUpdateStatus = $"Downloading Roblox's installer for release {release.Version}: {Megabytes(value.Bytes)}" + (value.Total is { } total ? $" of {Megabytes(total)}" : "") + "…";
            });
            var result = await installer.DownloadAsync(release, progress, stop.Token);
            if (stop.IsCancellationRequested || generation != _clientUpdateGeneration || IsLocked || _shuttingDown)
            {
                // Panic, Lock, Cancel or exit arrived while the installer worked: nothing from before it may be run later.
                if (result.Outcome == ClientUpdateDownloadOutcome.Verified) installer.Discard();
                ClientUpdateStatus = ClientUpdateDownloadStopped; return;
            }
            if (result.Outcome == ClientUpdateDownloadOutcome.Verified && !SameUpdateRelease(release, _offeredRelease))
            {
                installer.Discard();
                ClientUpdateStatus = "The current update offer changed while downloading. The downloaded installer was discarded; refresh installation before trying again. Nothing was run.";
                return;
            }
            switch (result.Outcome)
            {
                case ClientUpdateDownloadOutcome.Verified:
                    _heldRelease = release;
                    ClientUpdateStatus = $"Installer for release {release.Version} downloaded ({Megabytes(result.Bytes)}) and verified: Windows confirmed its signature from {result.Signer?.Organization ?? "Roblox Corporation"} using cached trust evidence (SHA-256 {result.Sha256}). Choose Install Roblox update to run it; it asks first.";
                    AddEvent($"Roblox installer for release {release.Version} downloaded and verified.");
                    break;
                case ClientUpdateDownloadOutcome.NotVerified:
                    ClientUpdateStatus = "The downloaded installer was not verified and was deleted. Nothing was run.";
                    Fail(new ClientUpdateRefusedException(), ManagerSubsystem.ClientUpdate,
                        "The downloaded Roblox installer was not verified; it was deleted and nothing was run. " + result.Summary);
                    break;
                default:
                    ClientUpdateStatus = "The installer could not be downloaded. Nothing was run.";
                    Fail(result.Failure ?? new ClientUpdateRefusedException(), ManagerSubsystem.ClientUpdate, result.Summary);
                    break;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { ClientUpdateStatus = ClientUpdateDownloadStopped; }
        finally { FinishClientUpdateWork(stop); }
    }

    private async Task InstallRobloxUpdateAsync()
    {
        if (!CanInstallRobloxUpdate || _heldRelease is not { } release || _clientUpdateInstaller is not { } installer) return;
        var stop = BeginClientUpdateWork($"Running Roblox's installer for release {release.Version} and waiting for it to finish (at most {(int)RobloxClientUpdate.InstallerWaitLimit.TotalMinutes} minutes). Roblox's installer closes every running Roblox client while it updates.");
        long generation = _clientUpdateGeneration;
        _heldRelease = null; // Used once: the installer consumes the held file whatever happens next.
        try
        {
            var result = await installer.InstallAsync(stop.Token);
            bool stopped = stop.IsCancellationRequested || generation != _clientUpdateGeneration || IsLocked || _shuttingDown;
            switch (result.Outcome)
            {
                case ClientUpdateInstallOutcome.Exited:
                    string text = result.ExitCode is 0 or null ? $"Roblox's installer finished for release {release.Version}."
                        : $"Roblox's installer exited with code {result.ExitCode} for release {release.Version}.";
                    AddEvent(text);
                    if (stopped) { ClientUpdateStatus = text + " Refresh installation to see what is installed now."; break; }
                    Inform(text);
                    if (CanRefreshInstallations)
                    {
                        bool selectionCleared = SelectedInstallation is not null;
                        // The rows the offer came from describe the player before the installer ran; re-read them now.
                        ClientUpdateStatus = text + " Refreshing installations to compare what is installed with Roblox's current release…";
                        RefreshInstallations.Execute(null); await RefreshInstallations.Execution;
                        if (stop.IsCancellationRequested || generation != _clientUpdateGeneration || IsLocked || _shuttingDown) return;
                        ClientUpdateStatus = text + " Installations were refreshed; the rows above show what is installed now.";
                        if (selectionCleared)
                        {
                            ClientUpdateStatus += " The launch queue is paused because its selected installation was cleared. Select and review the updated installation before resuming launches.";
                            Inform(ClientUpdateStatus);
                        }
                    }
                    else ClientUpdateStatus = text + " Refresh installation to see what is installed now.";
                    break;
                case ClientUpdateInstallOutcome.StillRunning:
                    AddEvent(result.Summary); ClientUpdateStatus = result.Summary + " Refresh installation once it finishes."; break;
                case ClientUpdateInstallOutcome.Stopped:
                    ClientUpdateStatus = result.Summary + " Refresh installation once it finishes.";
                    if (result.Failure is { } fault) _diagnostics.RecordFailure(ManagerSubsystem.ClientUpdate, fault.GetType());
                    break;
                default:
                    ClientUpdateStatus = result.Summary + " Download the update again to retry.";
                    Fail(result.Failure ?? new ClientUpdateRefusedException(), ManagerSubsystem.ClientUpdate, "Roblox's installer was not run. " + result.Summary);
                    break;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { ClientUpdateStatus = "Zenith stopped waiting for Roblox's installer. If it had started, it keeps running on its own; refresh installation once it finishes."; }
        finally { FinishClientUpdateWork(stop); }
    }

    private CancellationTokenSource BeginClientUpdateWork(string status)
    {
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _clientUpdateStop = stop; _clientUpdateBusy = true;
        ClientUpdateStatus = status; RaiseClientUpdateAvailability();
        return stop;
    }
    private void FinishClientUpdateWork(CancellationTokenSource stop)
    {
        if (ReferenceEquals(_clientUpdateStop, stop)) _clientUpdateStop = null;
        _clientUpdateBusy = false; stop.Dispose();
        RaiseClientUpdateAvailability();
    }
    /// <summary>Cancel: stops a download or verification, or stops waiting for a started installer (which keeps running).</summary>
    private void CancelClientUpdateWork() { _clientUpdateGeneration++; _clientUpdateStop?.Cancel(); }
    /// <summary>
    /// Panic, Lock and exit: cancels the work in progress and discards a held installer, so nothing verified before the stop is
    /// run after it. All stops suspend the automatic check; Unlock or explicitly saving the preference off/on arms it again.
    /// </summary>
    private void StopClientUpdateWork(bool locking)
    {
        CancelClientUpdateWork();
        DiscardHeldInstaller();
        _clientUpdateChecksPaused = true; _clientUpdateCheckGeneration++;
        StopClientUpdateCheckTimer(); _automaticUpdateStop?.Cancel();
        RobloxUpdateCheckStatus = _savedSettings.CheckRobloxUpdates
            ? "Automatic checks stopped. Unlock, or save automatic checks off and on, to resume; Refresh installation remains available when unlocked." : "";
        RaiseClientUpdateAvailability();
    }
    private void DiscardHeldInstaller()
    {
        bool had = _heldRelease is not null; _heldRelease = null;
        if (_clientUpdateInstaller is { } installer && (had || installer.HasVerifiedInstaller)) installer.Discard();
    }
    private void ReportClientUpdateFailure(Exception error, string message)
    {
        ClientUpdateStatus = message;
        Fail(error, ManagerSubsystem.ClientUpdate, message);
    }

    private async Task DisposeClientUpdateFeaturesAsync()
    {
        StopClientUpdateWork(locking: true);
        if (_clientUpdateCheckTimer is { } timer) { timer.Stop(); timer.Tick -= ClientUpdateCheckTick; _clientUpdateCheckTimer = null; }
        try { await _automaticUpdateCheck; }
        catch (Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.ClientUpdate, error.GetType()); }
        try { await _clientUpdateWork; }
        catch (Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.ClientUpdate, error.GetType()); }
        PropertyChanged -= ClientUpdateOwnerChanged;
        if (_clientUpdateInstaller is { } installer) await StopOwnerAsync(() => installer.DisposeAsync());
    }

    private static string Megabytes(long bytes) => (bytes / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    /// <summary>Progress reports land on the dispatcher whatever thread the installer reports from; late reports are dropped by the caller's check.</summary>
    private sealed class DispatcherProgress(Dispatcher dispatcher, Action<ClientUpdateDownloadProgress> report) : IProgress<ClientUpdateDownloadProgress>
    {
        public void Report(ClientUpdateDownloadProgress value)
        {
            if (dispatcher.CheckAccess()) report(value);
            else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => report(value)));
        }
    }
}
