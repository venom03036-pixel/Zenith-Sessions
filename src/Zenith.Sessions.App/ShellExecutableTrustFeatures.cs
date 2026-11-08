using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private IClientExecutableTrustVerifier _executableTrust = null!;
    private CancellationTokenSource? _publisherStop;
    private long _publisherGeneration;
    private bool _publisherBusy;
    private const string PublisherIdleStatus = "Select a native Roblox installation, then explicitly verify its publisher.";
    private string _publisherStatus = PublisherIdleStatus;
    public string PublisherVerificationStatus { get => _publisherStatus; private set => Set(ref _publisherStatus, value); }
    public bool IsPublisherVerificationBusy
    {
        get => _publisherBusy;
        private set { if (Set(ref _publisherBusy, value)) { Raise(nameof(CanVerifyInstallationPublisher)); Raise(nameof(CanRefreshInstallations)); RefreshBootstrapperLaunch(); } }
    }
    private const string PublisherLauncherStatus = "This row is the Roblox launcher (RobloxPlayerLauncher.exe), which installs and updates Roblox. Its publisher is not checked here; select a RobloxPlayerBeta.exe row to verify the player.";
    // The verifier checks the direct player files and, since R-launch-17, a bootstrapper's own executable; a launcher row would
    // always answer "not evaluated". A refresh that has shown its rows and only awaits Roblox's release answer does not hold
    // verification: that answer never replaces a row.
    public bool CanVerifyInstallationPublisher => _ready && !IsLocked && !_shuttingDown && !IsSample
        && !_installationListPending && !IsPublisherVerificationBusy
        && SelectedInstallation?.Candidate is { FileState: ClientInstallationFileState.PresentUnverified } candidate
        && (candidate.Product == ClientInstallationProduct.Roblox ? RobloxNativePlayerFiles.IsDirectPlayer(candidate.ExecutablePath)
            : BootstrapperProducts.IsOwnExecutable(candidate.Product, candidate.ExecutablePath));
    private string PublisherIdleText() => SelectedInstallation?.Candidate is { Product: ClientInstallationProduct.Roblox } candidate
        && RobloxNativePlayerFiles.IsLauncher(candidate.ExecutablePath) ? PublisherLauncherStatus : PublisherIdleStatus;
    public AsyncCommand VerifyInstallationPublisher { get; private set; } = null!;
    public Command CancelPublisherVerification { get; private set; } = null!;

    private void InitializePublisherVerification(IClientExecutableTrustVerifier? verifier)
    {
        // The real verifier reads each bootstrapper's accepted publisher from the saved settings (R-launch-17), like the provider's own.
        _executableTrust = verifier ?? new WindowsClientExecutableTrust(new SavedAcceptedBootstrapperPublishers(this));
        VerifyInstallationPublisher = new(_ => VerifyPublisherAsync(), ReportPublisherFailure);
        CancelPublisherVerification = new(_ => CancelPublisherVerificationWork());
        InitializeBootstrapperLaunchFeatures();
    }

    private async Task VerifyPublisherAsync()
    {
        if (!CanVerifyInstallationPublisher || SelectedInstallation is not { } selected) return;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _publisherStop = stop; long generation = _publisherGeneration;
        IsPublisherVerificationBusy = true;
        PublisherVerificationStatus = "Checking the selected file with cached Windows trust evidence. No program is opened or changed.";
        bool Current() => !stop.IsCancellationRequested && generation == _publisherGeneration && !IsLocked && !_shuttingDown
            && ReferenceEquals(SelectedInstallation, selected);
        try
        {
            var result = await _executableTrust.VerifyAsync(selected.Candidate, stop.Token);
            if (!Current()) return;
            bool bootstrapper = BootstrapperProducts.IsBootstrapper(selected.Candidate.Product);
            bool verified = result.Status is ClientExecutableTrustStatus.VerifiedRobloxPublisher or ClientExecutableTrustStatus.VerifiedAcceptedPublisher;
            // A bootstrapper answer that names a signer (verified, not accepted yet, or a changed signer) must carry a whole, well-formed one.
            bool signerShown = bootstrapper && (verified || result.Reason is ClientExecutableTrustReason.PublisherNotAccepted or ClientExecutableTrustReason.AcceptedPublisherChanged);
            if (!Enum.IsDefined(result.Status) || !Enum.IsDefined(result.Reason) || result.Product != selected.Candidate.Product
                || !string.Equals(result.ExecutablePath, selected.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                || result.ObservedUtc < DateTimeOffset.UtcNow.AddMinutes(-5) || result.ObservedUtc > DateTimeOffset.UtcNow.AddSeconds(2)
                || result.NativeLaunchAuthorized || (result.Reason == ClientExecutableTrustReason.Verified) != verified
                || result.Status == ClientExecutableTrustStatus.VerifiedRobloxPublisher && bootstrapper || result.Status == ClientExecutableTrustStatus.VerifiedAcceptedPublisher && !bootstrapper
                || verified
                    && (result.File is null || result.Signer is null
                        || !string.Equals(result.File.FullPath,selected.ExecutablePath,StringComparison.OrdinalIgnoreCase)
                        || result.File.Length is <=0 or >WindowsClientExecutableTrust.MaximumFileBytes || result.File.FileId.Length!=32 || !result.File.FileId.All(char.IsAsciiHexDigit)
                        || !bootstrapper && !string.Equals(result.Signer.Organization,"Roblox Corporation",StringComparison.OrdinalIgnoreCase)
                        || bootstrapper && AcceptedPublisherFor(selected.Candidate.Product)?.Matches(result.Signer) != true
                        || result.Sha256 is not { Length: 64 } hash || !hash.All(char.IsAsciiHexDigit))
                || signerShown && (result.Signer is null || !AcceptedBootstrapperPublisher.IsThumbprint(result.Signer.CertificateSha256)
                        || result.Signer.Organization.Length is <1 or >AcceptedBootstrapperPublisher.MaximumPublisherCharacters || result.Signer.Organization.Any(char.IsControl)
                        || result.Signer.DisplayName.Length is <1 or >256 || result.Signer.DisplayName.Any(char.IsControl)
                        || result.Signer.NotAfterUtc<=result.Signer.NotBeforeUtc)
                || !bootstrapper && result.Reason is ClientExecutableTrustReason.PublisherNotAccepted or ClientExecutableTrustReason.AcceptedPublisherChanged)
                throw new InvalidOperationException("Inconsistent executable verification evidence.");
            PublisherVerificationStatus = result.Summary + " Checked " + result.ObservedUtc.ToLocalTime().ToString("T")
                + (verified ? " · SHA-256 " + result.Sha256 : "")
                + (signerShown ? $" · Signer {result.Signer!.DisplayName} ({result.Signer.Organization}) · certificate SHA-256 {result.Signer.CertificateSha256.ToUpperInvariant()}" : "");
            OfferBootstrapperPublisher(result);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (Current()) ReportPublisherFailure(error);
            else _diagnostics.RecordFailure(ManagerSubsystem.Installation, error.GetType());
        }
        finally
        {
            if (ReferenceEquals(_publisherStop, stop)) _publisherStop = null;
            // Only CancelPublisherVerificationWork changes the generation, and it left the "waiting for
            // Windows" text up while this owned check finished. Busy blocks a newer check until the line
            // below runs, so settling here cannot overwrite a newer result. Shutdown leaves it alone.
            if (generation != _publisherGeneration && !_shuttingDown)
                PublisherVerificationStatus = ReferenceEquals(SelectedInstallation, selected)
                    ? "Verification cancelled. Trust remains unknown; verify again to check this file."
                    : PublisherIdleText();
            IsPublisherVerificationBusy = false;
        }
    }

    private void ReportPublisherFailure(Exception error)
    {
        PublisherVerificationStatus = "The publisher check failed. Trust remains unknown; no launch was enabled.";
        _diagnostics.RecordFailure(ManagerSubsystem.Installation, error.GetType());
    }

    private void CancelPublisherVerificationWork()
    {
        OfferBootstrapperPublisher(null); // A signer offered for acceptance belongs to the check and selection that produced it.
        Interlocked.Increment(ref _publisherGeneration); _publisherStop?.Cancel();
        PublisherVerificationStatus = IsPublisherVerificationBusy
            ? "Verification cancelled. Waiting for Windows to finish its owned check; late results are discarded."
            : PublisherIdleText();
    }
}
