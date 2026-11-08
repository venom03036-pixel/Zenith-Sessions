using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

/// <summary>
/// Launching through an installed bootstrapper (R-launch-17, owner decision of 2026-11-08). Off by default: a bootstrapper's own
/// row (Bloxstrap, Fishstrap, Froststrap or Voidstrap) becomes the trial installation only after the user verifies its publisher
/// and accepts it once for that product. The acceptance (product, certificate thumbprint, publisher name) is saved in settings.json
/// and read back by the trust verifier before every start, so a differently signed or unsigned executable is refused. Zenith
/// starts the bootstrapper with Roblox's launch request only; the bootstrapper applies its own client settings and FFlags, and
/// Zenith never writes client files. Extra client arguments (R-launch-16) are never passed through a bootstrapper.
/// </summary>
public sealed partial class ShellViewModel
{
    // The last publisher check of the selected bootstrapper row whose signer may be accepted (valid signature, not the accepted one).
    private ClientExecutableTrustResult? _bootstrapperPublisherOffer;
    public AsyncCommand AcceptBootstrapperPublisher { get; private set; } = null!;
    public AsyncCommand ForgetBootstrapperPublisher { get; private set; } = null!;

    private void InitializeBootstrapperLaunchFeatures()
    {
        AcceptBootstrapperPublisher = new(_ => AcceptBootstrapperPublisherAsync(), error => Fail(error, ManagerSubsystem.Settings));
        ForgetBootstrapperPublisher = new(_ => ForgetBootstrapperPublisherAsync(), error => Fail(error, ManagerSubsystem.Settings));
    }

    /// <summary>The acceptance saved for this bootstrapper, from the last saved settings (never a draft); null for the player or none.</summary>
    private AcceptedBootstrapperPublisher? AcceptedPublisherFor(ClientInstallationProduct product)
        => BootstrapperProducts.IsBootstrapper(product) ? AcceptedBootstrapperPublisher.For(Volatile.Read(ref _savedSettings).AcceptedBootstrapperPublishers, product) : null;
    /// <summary>True for a bootstrapper's own row whose product has an accepted publisher: the only bootstrapper row the trial may use.</summary>
    private bool IsAcceptedBootstrapper(ClientInstallationCandidate? candidate)
        => candidate is not null && BootstrapperProducts.IsBootstrapper(candidate.Product) && AcceptedPublisherFor(candidate.Product) is not null;

    public bool CanAcceptBootstrapperPublisher => _ready && !IsLocked && !_shuttingDown && !IsSample && !IsPublisherVerificationBusy && _pendingSaves == 0
        && _bootstrapperPublisherOffer is { Signer: not null } offer && SelectedInstallation?.Candidate is { } candidate
        && BootstrapperProducts.IsBootstrapper(candidate.Product) && offer.Product == candidate.Product
        && string.Equals(offer.ExecutablePath, candidate.ExecutablePath, StringComparison.OrdinalIgnoreCase)
        && offer.Reason is ClientExecutableTrustReason.PublisherNotAccepted or ClientExecutableTrustReason.AcceptedPublisherChanged;
    public bool CanForgetBootstrapperPublisher => _ready && !IsLocked && !_shuttingDown && !IsSample && _pendingSaves == 0
        && SelectedInstallation?.Candidate is { } candidate && AcceptedPublisherFor(candidate.Product) is not null;

    /// <summary>What launching through the selected row's bootstrapper would do now, in plain words; empty for a Roblox row.</summary>
    public string BootstrapperLaunchStatus
    {
        get
        {
            if (SelectedInstallation?.Candidate is not { } candidate || !BootstrapperProducts.IsBootstrapper(candidate.Product)) return "";
            var product = candidate.Product;
            string own = BootstrapperProducts.IsOwnExecutable(product, candidate.ExecutablePath) ? "" : $" This row is not {product}'s own executable ({BootstrapperProducts.ExecutableName(product)}), so it cannot be the trial installation.";
            return AcceptedPublisherFor(product) is { } accepted
                ? $"Launch through {product}: off for this build. Accepted publisher {accepted.Publisher} (certificate SHA-256 {accepted.CertificateSha256}) is retained, but launching is deferred until the exact Roblox player can be associated with its bootstrapper. Use the standard Roblox player. Zenith never writes client files.{own}"
                : $"Launch through {product}: off for this build. Publisher inspection and Accept publisher remain available, but do not enable launching. Bootstrapper support follows the standard Roblox player after exact player association is implemented.{own}";
        }
    }

    private void RefreshBootstrapperLaunch()
    { Raise(nameof(CanAcceptBootstrapperPublisher)); Raise(nameof(CanForgetBootstrapperPublisher)); Raise(nameof(BootstrapperLaunchStatus)); }

    // Called by the publisher check with the selected row's result: an acceptable signer is kept until the selection or check changes.
    private void OfferBootstrapperPublisher(ClientExecutableTrustResult? result)
    {
        _bootstrapperPublisherOffer = result is { Signer: not null, Reason: ClientExecutableTrustReason.PublisherNotAccepted or ClientExecutableTrustReason.AcceptedPublisherChanged }
            && BootstrapperProducts.IsBootstrapper(result.Product) ? result : null;
        RefreshBootstrapperLaunch();
    }

    private async Task AcceptBootstrapperPublisherAsync()
    {
        if (!CanAcceptBootstrapperPublisher || _bootstrapperPublisherOffer is not { Signer: { } signer } offer) return;
        var entry = new AcceptedBootstrapperPublisher(offer.Product.ToString(), signer.CertificateSha256, signer.Organization).Validated();
        var kept = Volatile.Read(ref _savedSettings).AcceptedBootstrapperPublishers ?? Array.Empty<AcceptedBootstrapperPublisher>();
        var list = kept.Where(item => !item.IsFor(offer.Product)).Append(entry).ToArray();
        // One settings write of the saved values plus this acceptance; unsaved drafts of other preferences stay unsaved.
        await SaveCurrentSettingsAsync(_savedSettings with { AcceptedBootstrapperPublishers = AcceptedBootstrapperPublisher.Validate(list) });
        _bootstrapperPublisherOffer = null;
        if (IsLocked || _shuttingDown) return;
        // The selected bootstrapper row may now be the trial installation; the same path as a click decides.
        ConfigureNativeTrialInstallation();
        PublisherVerificationStatus = $"Accepted publisher {entry.Publisher} for {offer.Product} (certificate SHA-256 {entry.CertificateSha256}). Bootstrapper launching remains deferred; this acceptance does not enable a trial.";
        Notice = $"Accepted publisher {entry.Publisher} for {offer.Product}. Use the standard Roblox player for this build; bootstrapper launching remains deferred.";
        RefreshBootstrapperLaunch();
    }

    private async Task ForgetBootstrapperPublisherAsync()
    {
        if (!CanForgetBootstrapperPublisher || SelectedInstallation?.Candidate is not { } candidate || AcceptedPublisherFor(candidate.Product) is not { } accepted) return;
        var kept = (Volatile.Read(ref _savedSettings).AcceptedBootstrapperPublishers ?? Array.Empty<AcceptedBootstrapperPublisher>()).Where(item => !item.IsFor(candidate.Product)).ToArray();
        await SaveCurrentSettingsAsync(_savedSettings with { AcceptedBootstrapperPublishers = AcceptedBootstrapperPublisher.Validate(kept) });
        if (IsLocked || _shuttingDown) return;
        // Off again: a selected row of that product stops being the trial installation (and stops any trial, like a selection change).
        ConfigureNativeTrialInstallation();
        PublisherVerificationStatus = $"Forgot the accepted publisher {accepted.Publisher} for {candidate.Product}. Bootstrapper launching remains deferred.";
        Notice = $"Launching through {candidate.Product} is off.";
        RefreshBootstrapperLaunch();
    }

    /// <summary>
    /// What the trust verifiers read when a bootstrapper's executable is checked: the acceptance in the last saved settings, never a
    /// draft. Runs on the verifier's worker; the saved settings reference is read once and the record is immutable.
    /// </summary>
    private sealed class SavedAcceptedBootstrapperPublishers(ShellViewModel owner) : IAcceptedBootstrapperPublisherSource
    {
        public AcceptedBootstrapperPublisher? Find(ClientInstallationProduct product)
            => AcceptedBootstrapperPublisher.For(Volatile.Read(ref owner._savedSettings).AcceptedBootstrapperPublishers, product);
    }
}
