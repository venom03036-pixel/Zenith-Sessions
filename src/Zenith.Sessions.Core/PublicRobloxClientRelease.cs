namespace Zenith.Sessions.Core;

/// <summary>
/// Roblox's current public Windows player release, as its public version service reported it at one moment.
/// Version is the four-part build (for example 0.744.0.7441080); ClientVersionUpload names the folder that
/// release unpacks into ("version-" and 16 lowercase hex digits). Unverified public metadata: never a download,
/// an install, or proof that a local file is genuine or compatible.
/// </summary>
public sealed record PublicRobloxClientRelease(string Version, string ClientVersionUpload, DateTimeOffset RetrievedUtc);

/// <summary>How one discovered installation relates to the current public release.</summary>
public enum ClientReleaseMatch { NotChecked, NotCompared, Current, NotCurrent }

/// <summary>
/// Separate optional capability. Each call is one explicit, anonymous, read-only request. Implementations create no
/// polling, retries, downloads or installs, and never read or write the client's files or settings.
/// </summary>
public interface IPublicRobloxClientReleaseDirectory
{
    Task<PublicDirectoryResult<PublicRobloxClientRelease>> GetCurrentWindowsPlayerReleaseAsync(CancellationToken ct);
}
