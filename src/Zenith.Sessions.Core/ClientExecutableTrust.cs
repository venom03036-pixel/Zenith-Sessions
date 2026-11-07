namespace Zenith.Sessions.Core;

public enum ClientExecutableTrustStatus { VerifiedRobloxPublisher, Unknown, Rejected, Deferred }
public enum ClientExecutableTrustReason
{
    Verified, BootstrapperNotEvaluated, InvalidCandidate, UnsupportedPlatform, UnsafePath, MissingFile,
    AccessDenied, FileTooLarge, FileChanged, Unsigned, InvalidSignature, RevocationUnavailable,
    UntrustedChain, PublisherMismatch, MetadataUnavailable, Busy, VerificationUnavailable
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
        ClientExecutableTrustReason.Verified => "Windows verified this exact file's signature and Roblox publisher using cached chain and revocation evidence. Native launch remains unavailable.",
        ClientExecutableTrustReason.BootstrapperNotEvaluated => "Bootstrapper publisher policies have not been established. This candidate remains unverified.",
        ClientExecutableTrustReason.InvalidCandidate => "Choose a present native Roblox executable from the installation results. Registry, filename and version hints alone cannot establish trust.",
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
