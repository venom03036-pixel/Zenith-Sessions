using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private IClientExecutableTrustVerifier _executableTrust = null!;
    private CancellationTokenSource? _publisherStop;
    private long _publisherGeneration;
    private bool _publisherBusy;
    private string _publisherStatus = "Select a native Roblox installation, then explicitly verify its publisher.";
    public string PublisherVerificationStatus { get => _publisherStatus; private set => Set(ref _publisherStatus, value); }
    public bool IsPublisherVerificationBusy
    {
        get => _publisherBusy;
        private set { if (Set(ref _publisherBusy, value)) { Raise(nameof(CanVerifyInstallationPublisher)); Raise(nameof(CanRefreshInstallations)); } }
    }
    public bool CanVerifyInstallationPublisher => _ready && !IsLocked && !_shuttingDown && !IsSample
        && !IsInstallationDiscoveryBusy && !IsPublisherVerificationBusy
        && SelectedInstallation?.Candidate is { Product: ClientInstallationProduct.Roblox, FileState: ClientInstallationFileState.PresentUnverified };
    public AsyncCommand VerifyInstallationPublisher { get; private set; } = null!;
    public Command CancelPublisherVerification { get; private set; } = null!;

    private void InitializePublisherVerification(IClientExecutableTrustVerifier? verifier)
    {
        _executableTrust = verifier ?? new WindowsClientExecutableTrust();
        VerifyInstallationPublisher = new(_ => VerifyPublisherAsync(), ReportPublisherFailure);
        CancelPublisherVerification = new(_ => CancelPublisherVerificationWork());
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
            if (!Enum.IsDefined(result.Status) || !Enum.IsDefined(result.Reason) || result.Product != selected.Candidate.Product
                || !string.Equals(result.ExecutablePath, selected.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                || result.ObservedUtc < DateTimeOffset.UtcNow.AddMinutes(-5) || result.ObservedUtc > DateTimeOffset.UtcNow.AddSeconds(2)
                || result.NativeLaunchAuthorized || result.Reason == ClientExecutableTrustReason.Verified && result.Status != ClientExecutableTrustStatus.VerifiedRobloxPublisher
                || result.Status == ClientExecutableTrustStatus.VerifiedRobloxPublisher
                    && (result.Reason != ClientExecutableTrustReason.Verified || result.File is null || result.Signer is null
                        || !string.Equals(result.File.FullPath,selected.ExecutablePath,StringComparison.OrdinalIgnoreCase)
                        || result.File.Length is <=0 or >536870912 || result.File.FileId.Length!=32 || !result.File.FileId.All(char.IsAsciiHexDigit)
                        || !string.Equals(result.Signer.Organization,"Roblox Corporation",StringComparison.OrdinalIgnoreCase) || result.Signer.CertificateSha256.Length!=64 || !result.Signer.CertificateSha256.All(char.IsAsciiHexDigit)
                        || result.Signer.NotAfterUtc<=result.Signer.NotBeforeUtc
                        || result.Sha256 is not { Length: 64 } hash || !hash.All(char.IsAsciiHexDigit)))
                throw new InvalidOperationException("Inconsistent executable verification evidence.");
            PublisherVerificationStatus = result.Summary + " Checked " + result.ObservedUtc.ToLocalTime().ToString("T")
                + (result.Status == ClientExecutableTrustStatus.VerifiedRobloxPublisher ? " · SHA-256 " + result.Sha256 : "");
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
        Interlocked.Increment(ref _publisherGeneration); _publisherStop?.Cancel();
        PublisherVerificationStatus = IsPublisherVerificationBusy
            ? "Verification cancelled. Waiting for Windows to finish its owned check; late results are discarded."
            : "Select a native Roblox installation, then explicitly verify its publisher.";
    }
}
