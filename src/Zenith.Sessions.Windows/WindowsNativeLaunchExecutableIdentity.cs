using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed partial class WindowsClientExecutableTrust
{
    internal static NativeLaunchExecutableIdentity? LaunchIdentity(ClientExecutableTrustResult trust)
    {
        if (trust.Product != ClientInstallationProduct.Roblox || trust.Status != ClientExecutableTrustStatus.VerifiedRobloxPublisher
            || trust.File is not { } file || trust.Sha256 is not { Length: 64 } hash) return null;
        return new NativeLaunchExecutableIdentity(PathDigest(file.FullPath), hash, FileDigest(file)).Validated();
    }

    // Cheap capability projection only. The existing held-file trust operation hashes the executable again during preparation;
    // that fresh digest, and the saved acceptance, are compared before ordinary dispatch. No hash loop runs on the UI thread.
    internal static bool MatchesLaunchIdentity(ClientInstallationCandidate candidate, NativeLaunchExecutableIdentity identity)
    {
        try
        {
            identity.Validated();
            if (candidate.Product != ClientInstallationProduct.Roblox || !TryPath(candidate.ExecutablePath, out string path)
                || PathDigest(path) != identity.PathSha256) return false;
            CheckAncestors(path, CancellationToken.None);
            using var current = OpenRead(path);
            return FileDigest(Snapshot(current)) == identity.FileIdentitySha256;
        }
        catch (Exception) { return false; } // Missing, replaced, redirected and unreadable files never inherit acceptance.
    }

    private static string PathDigest(string path) => Digest(path.Replace('/', '\\').ToUpperInvariant());
    private static string FileDigest(ClientExecutableFileSnapshot file) => Digest(string.Join("|",
        PathDigest(file.FullPath), file.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture), file.FileId,
        file.Length.ToString(CultureInfo.InvariantCulture), file.CreationUtc.UtcTicks.ToString(CultureInfo.InvariantCulture),
        file.LastWriteUtc.UtcTicks.ToString(CultureInfo.InvariantCulture)));
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
