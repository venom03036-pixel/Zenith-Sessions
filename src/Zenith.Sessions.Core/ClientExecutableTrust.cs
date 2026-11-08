namespace Zenith.Sessions.Core;

/// <summary>
/// VerifiedRobloxPublisher: the Roblox player, signed by Roblox Corporation. VerifiedAcceptedPublisher (R-launch-17): a
/// bootstrapper's own executable, validly signed by the certificate the user accepted for that product. Both may be held for a start.
/// </summary>
public enum ClientExecutableTrustStatus { VerifiedRobloxPublisher, Unknown, Rejected, Deferred, VerifiedAcceptedPublisher }
public enum ClientExecutableTrustReason
{
    Verified, BootstrapperNotEvaluated, InvalidCandidate, UnsupportedPlatform, UnsafePath, MissingFile,
    AccessDenied, FileTooLarge, FileChanged, Unsigned, InvalidSignature, RevocationUnavailable,
    UntrustedChain, PublisherMismatch, MetadataUnavailable, Busy, VerificationUnavailable,
    // R-launch-17: a validly signed bootstrapper whose publisher the user has not accepted for that product yet, or whose
    // signer is not the accepted one. The signer is reported with either so the user can decide; neither obtains a start.
    PublisherNotAccepted, AcceptedPublisherChanged
}

/// <summary>One exact local file observation. It is neither a persistent trust decision nor a launch lease.</summary>
public sealed record ClientExecutableFileSnapshot(string FullPath, long Length, DateTimeOffset CreationUtc,
    DateTimeOffset LastWriteUtc, ulong VolumeSerialNumber, string FileId)
{
    public override string ToString() => $"ClientExecutableFileSnapshot {{ Length = {Length}, Path = [local metadata] }}";
}

public sealed record ClientExecutableSigner(string DisplayName, string Organization, string CertificateSha256,
    DateTimeOffset NotBeforeUtc, DateTimeOffset NotAfterUtc);

public sealed record ClientExecutableTrustResult(ClientInstallationProduct Product, string ExecutablePath,
    ClientExecutableTrustStatus Status, ClientExecutableTrustReason Reason, DateTimeOffset ObservedUtc,
    ClientExecutableFileSnapshot? File = null, string? Sha256 = null, ClientExecutableSigner? Signer = null)
{
    public bool NativeLaunchAuthorized => false;
    public bool UsesCachedRevocationOnly => true;
    public string EvidenceLabel => "Selected-file signature evidence only. It does not establish compatibility, account selection, concurrent clients or arrival; recheck the exact file before any future handoff.";
    public string Summary => Reason switch
    {
        ClientExecutableTrustReason.Verified when Status == ClientExecutableTrustStatus.VerifiedAcceptedPublisher =>
            $"Windows verified this exact file's signature using cached chain and revocation evidence, and its publisher is the one you accepted for {Product}. Publisher acceptance does not enable launching. Bootstrapper launches and trials are deferred; select the standard Roblox player for this build. {Product} manages its own client settings and FFlags; Zenith does not write client files.",
        ClientExecutableTrustReason.Verified => "Windows verified this exact file's signature and Roblox publisher using cached chain and revocation evidence. Native launch remains unavailable.",
        ClientExecutableTrustReason.BootstrapperNotEvaluated => "This file is the Roblox launcher (RobloxPlayerLauncher.exe), not the player or a bootstrapper's own executable; no publisher policy applies to it. It remains unverified.",
        ClientExecutableTrustReason.PublisherNotAccepted => $"Windows verified this file's signature, but you have not accepted a publisher for {Product} yet. Review the signer shown here before choosing Accept publisher. Publisher acceptance does not enable launching. Bootstrapper launches and trials are deferred; select the standard Roblox player for this build.",
        ClientExecutableTrustReason.AcceptedPublisherChanged => $"Windows verified this file's signature, but its signer is not the one you accepted for {Product}. {Product} may have changed its signing certificate, or the file is not the one you accepted. Review the signer shown here before accepting it again. Publisher acceptance does not enable launching. Bootstrapper launches and trials are deferred; select the standard Roblox player for this build.",
        ClientExecutableTrustReason.InvalidCandidate => "Choose a present native Roblox executable, or a bootstrapper's own executable, from the installation results. Registry, filename and version hints alone cannot establish trust.",
        ClientExecutableTrustReason.UnsupportedPlatform => "Executable publisher verification is unavailable on this platform.",
        ClientExecutableTrustReason.UnsafePath => "The selected file is nonlocal, redirected or otherwise unsafe for this read-only check.",
        ClientExecutableTrustReason.MissingFile => "The selected executable is no longer present. Refresh installation discovery.",
        ClientExecutableTrustReason.AccessDenied => "Windows denied read-only access or the file is being changed. No trust was established.",
        ClientExecutableTrustReason.FileTooLarge => "The executable exceeds the bounded verification size. No trust was established.",
        ClientExecutableTrustReason.FileChanged => "The selected file or its path changed during verification. Discard this result and refresh discovery.",
        ClientExecutableTrustReason.Unsigned => "Windows did not establish an embedded Authenticode signature for this executable.",
        ClientExecutableTrustReason.InvalidSignature => "Windows rejected the executable's signature. It must not receive an account handoff.",
        ClientExecutableTrustReason.RevocationUnavailable => "Cached certificate revocation evidence is missing or unavailable. No online retrieval was attempted; trust remains unknown.",
        ClientExecutableTrustReason.UntrustedChain => "Windows could not establish the current code-signing certificate chain from cached evidence. Trust remains unknown.",
        ClientExecutableTrustReason.PublisherMismatch => "The verified signer did not establish the expected Roblox Corporation publisher. Trust remains unknown.",
        ClientExecutableTrustReason.MetadataUnavailable => "The exact file or signer identity could not be established. Trust remains unknown.",
        ClientExecutableTrustReason.Busy => "Another executable verification is still owned. No additional check was queued.",
        _ => "Windows executable verification is unavailable. Trust remains unknown."
    };
    public override string ToString() => $"ClientExecutableTrustResult {{ Status = {Status}, Reason = {Reason}, Path = [local metadata] }}";
}

public interface IClientExecutableTrustVerifier : IAsyncDisposable
{
    // One owned operation, no queued checks. Cancellation/disposal joins actual native work;
    // Windows trust providers have no cancellable API or guaranteed hard execution deadline.
    Task<ClientExecutableTrustResult> VerifyAsync(ClientInstallationCandidate candidate, CancellationToken ct);
}
