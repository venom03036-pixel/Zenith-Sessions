using System.Globalization;
using System.IO;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed partial class RobloxPublicDirectory : IPublicRobloxClientReleaseDirectory
{
    /// <summary>The recorded body is about 100 bytes; anything past this cap is refused before parsing.</summary>
    public const int MaximumClientReleaseBytes = 4 * 1024;
    // Roblox's public client-version endpoint for the Windows player. Anonymous and read-only: no cookie, no channel,
    // no download. The caller asks once per explicit refresh; the shared slots, rate-limit pause and redirect refusal apply.
    private static readonly Uri ClientReleaseUri = new("https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer");

    public Task<PublicDirectoryResult<PublicRobloxClientRelease>> GetCurrentWindowsPlayerReleaseAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ReadJsonAsync(ClientReleaseUri, root =>
        {
            var release = new PublicRobloxClientRelease(ReadText(root, "version", 43), ReadText(root, "clientVersionUpload", 24), _clock.GetUtcNow());
            if (!RobloxClientRelease.IsValid(release)) throw new InvalidPublicResponseException();
            return Success(release, "Roblox's current public Windows player release. Nothing was downloaded or installed.");
        }, ct, maximumBytes: MaximumClientReleaseBytes);
    }
}

/// <summary>
/// Compares already-discovered installation metadata with Roblox's current public release. Pure string work:
/// no file, registry or network access, and no claim that a matching file is genuine.
/// </summary>
public static class RobloxClientRelease
{
    private const string UploadPrefix = "version-";

    /// <summary>"version-" followed by exactly 16 lowercase hex digits, as the release service names an upload.</summary>
    public static bool IsUploadName(string? value) => value is { Length: 24 } && value.StartsWith(UploadPrefix, StringComparison.Ordinal)
        && value.AsSpan(UploadPrefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    public static bool IsValid(PublicRobloxClientRelease? release) => release is not null
        && IsUploadName(release.ClientVersionUpload) && TryParseBuild(release.Version, out _);

    public static ClientReleaseMatch Compare(ClientInstallationCandidate candidate, PublicRobloxClientRelease? latest)
    {
        if (!IsValid(latest)) return ClientReleaseMatch.NotChecked;
        // Only a found Roblox player is compared. The launcher and a bootstrapper report their own versions, not the player's.
        if (candidate.Product != ClientInstallationProduct.Roblox || !RobloxNativePlayerFiles.IsDirectPlayer(candidate.ExecutablePath)
            || candidate.FileState is not (ClientInstallationFileState.PresentUnverified or ClientInstallationFileState.MetadataUnavailable
                or ClientInstallationFileState.TooLarge)) return ClientReleaseMatch.NotCompared;
        // A release unpacks into Versions\<its upload name>, so that folder identifies the deployment exactly.
        if (UploadFolder(candidate.ExecutablePath) is { } folder)
            return string.Equals(folder, latest!.ClientVersionUpload, StringComparison.Ordinal) ? ClientReleaseMatch.Current : ClientReleaseMatch.NotCurrent;
        if (!TryParseBuild(candidate.FileVersion, out var installed) || !TryParseBuild(latest!.Version, out var current)) return ClientReleaseMatch.NotCompared;
        return Same(installed.Major, current.Major) && Same(installed.Minor, current.Minor) && Same(installed.Patch, current.Patch)
            && Same(installed.Revision, current.Revision) ? ClientReleaseMatch.Current : ClientReleaseMatch.NotCurrent;
    }

    // Windows keeps 16 bits per fixed file-version part, so build 0.744.0.7441080 reads back as 0.744.0.35512.
    private static bool Same(uint installed, uint release) => installed == release || installed == (release & 0xFFFF);

    private static string? UploadFolder(string path)
    {
        string? folder = Path.GetDirectoryName(path);
        if (folder is null) return null;
        string name = Path.GetFileName(folder).ToLowerInvariant();
        return IsUploadName(name) && string.Equals(Path.GetFileName(Path.GetDirectoryName(folder)), "Versions", StringComparison.OrdinalIgnoreCase) ? name : null;
    }

    private static bool TryParseBuild(string? text, out (uint Major, uint Minor, uint Patch, uint Revision) build)
    {
        build = default;
        if (text is not { Length: >= 7 and <= 43 }) return false;
        string[] parts = text.Split('.');
        if (parts.Length != 4) return false;
        var values = new uint[4];
        for (int index = 0; index < 4; index++)
            if (parts[index] is not { Length: >= 1 and <= 10 } part || !part.All(char.IsAsciiDigit)
                || !uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out values[index])) return false;
        build = (values[0], values[1], values[2], values[3]);
        return true;
    }
}
