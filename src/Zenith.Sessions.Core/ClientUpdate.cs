namespace Zenith.Sessions.Core;

/// <summary>
/// Fixed bounds for the optional Roblox update check and the explicit installer run. The check is the same anonymous read of
/// Roblox's public version service that Refresh installation makes; the installer is Roblox's own, from Roblox's setup host,
/// verified with the executable trust code before it is run. Zenith never writes the client's own files or settings.
/// </summary>
public static class RobloxClientUpdate
{
    /// <summary>The first automatic check runs this long after the manager is ready (when the saved preference is on).</summary>
    public static readonly TimeSpan CheckDelay = TimeSpan.FromSeconds(3);
    /// <summary>Later automatic checks run this long after the previous one finished.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);
    /// <summary>The installer download is refused past this many bytes (the bootstrapper is a few megabytes).</summary>
    public const long MaximumInstallerBytes = 64L * 1024 * 1024;
    /// <summary>A download that has not finished within this time fails and leaves nothing behind.</summary>
    public static readonly TimeSpan DownloadLimit = TimeSpan.FromMinutes(5);
    /// <summary>How long Zenith waits for the started installer to exit before it reports it as still running (it is never ended).</summary>
    public static readonly TimeSpan InstallerWaitLimit = TimeSpan.FromMinutes(10);
    /// <summary>The only file name the downloaded installer is saved under and the only name the verifier accepts for it.</summary>
    public const string InstallerFileName = "RobloxPlayerInstaller.exe";
    /// <summary>Roblox's own setup host, the one its bootstrapper downloads from.</summary>
    public const string SetupHost = "setup.rbxcdn.com";
}

public enum ClientUpdateDownloadOutcome
{
    /// <summary>Downloaded, Windows verified its signature from Roblox Corporation, and the file is held for one run.</summary>
    Verified,
    /// <summary>Downloaded, but the signature or publisher was not established; the file was deleted and nothing was run.</summary>
    NotVerified,
    /// <summary>Not downloaded: an invalid release name, an oversized body, or work already in progress.</summary>
    Refused,
    /// <summary>The request or the write failed (a refused redirect, an HTTP error, a network or disk fault, the time limit).</summary>
    Failed
}

public readonly record struct ClientUpdateDownloadProgress(long Bytes, long? Total);

/// <summary>
/// What a download ended with. Summary is Zenith's own fixed wording and carries no path, address text beyond the setup host,
/// or exception message; Failure is the caught exception, for recording by type only (Rule 10).
/// </summary>
public sealed record ClientUpdateDownloadResult(ClientUpdateDownloadOutcome Outcome, string Summary, long Bytes = 0, string? Sha256 = null,
    ClientExecutableSigner? Signer = null, ClientExecutableTrustReason? TrustReason = null, Exception? Failure = null)
{
    public override string ToString() => $"ClientUpdateDownloadResult {{ Outcome = {Outcome}, Bytes = {Bytes}, TrustReason = {TrustReason} }}";
}

public enum ClientUpdateInstallOutcome
{
    /// <summary>The installer ran and exited; ExitCode is its code when Windows reported one.</summary>
    Exited,
    /// <summary>The installer was still running when the wait limit passed. It was left running.</summary>
    StillRunning,
    /// <summary>The wait was cancelled (Panic, Lock, Cancel or exit). The installer, if started, was left running.</summary>
    Stopped,
    /// <summary>Nothing was run: no verified installer was held, or the held file changed before the run.</summary>
    NotStarted
}

/// <summary>What a run ended with. Summary is fixed wording; Failure is a caught exception, for recording by type only (Rule 10).</summary>
public sealed record ClientUpdateInstallResult(ClientUpdateInstallOutcome Outcome, int? ExitCode, string Summary, Exception? Failure = null)
{
    public override string ToString() => $"ClientUpdateInstallResult {{ Outcome = {Outcome}, ExitCode = {ExitCode} }}";
}

/// <summary>
/// Downloads, verifies, holds and runs Roblox's own installer, one operation at a time. A download writes into a fresh temporary
/// folder, verifies the file with the executable trust code and keeps it open with read sharing only, so the bytes that were
/// verified are the bytes that run. Discard and a finished run delete the folder. Cancellation stops a download (deleting the
/// partial file) or stops waiting for a started installer; a running installer is never ended.
/// </summary>
public interface IClientUpdateInstaller : IAsyncDisposable
{
    bool HasVerifiedInstaller { get; }
    Task<ClientUpdateDownloadResult> DownloadAsync(PublicRobloxClientRelease release, IProgress<ClientUpdateDownloadProgress>? progress, CancellationToken ct);
    Task<ClientUpdateInstallResult> InstallAsync(CancellationToken ct);
    void Discard();
}

/// <summary>Recorded by type when a downloaded installer is refused or not verified; never thrown with any detail in it.</summary>
public sealed class ClientUpdateRefusedException : Exception
{
    public ClientUpdateRefusedException() : base("The Roblox update was refused.") { }
}
