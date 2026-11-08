using System.Globalization;
using System.IO;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed partial class RobloxPublicDirectory : IPublicRobloxClientReleaseDirectory
{
    /// <summary>The recorded body is about 100 bytes; anything past this cap is refused before parsing.</summary>
    public const int MaximumClientReleaseBytes = 4 * 1024;
    // Roblox's public client-version endpoint for the Windows player. Anonymous and read-only: no cookie, no channel,
    // no download. The caller asks once per explicit refresh, and a native start through PublicClientReleaseCache asks only when
    // the last answer is ten minutes old; the shared slots, rate-limit pause and redirect refusal apply.
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
/// The newest valid public release Zenith has read, kept for the launch update hold. <see cref="ReadAsync"/> reuses it while
/// it is younger than <see cref="Lifetime"/> and otherwise asks once; a failed, malformed or slow answer keeps the last valid
/// one (or none), so an unreachable service never invents a release and never holds a launch. No timer, polling or retry;
/// the borrowed directory stays owned (and disposed) by its caller.
/// </summary>
public sealed class PublicClientReleaseCache(IPublicRobloxClientReleaseDirectory directory, TimeProvider? clock = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly IPublicRobloxClientReleaseDirectory _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private PublicRobloxClientRelease? _latest;

    public PublicRobloxClientRelease? Latest { get { lock (_gate) return _latest; } }
    /// <summary>True while the kept release was read less than <see cref="Lifetime"/> ago. A release dated in the future is not fresh.</summary>
    public bool IsFresh => Latest is { } latest && _clock.GetUtcNow() - latest.RetrievedUtc is var age && age >= TimeSpan.Zero && age < Lifetime;

    /// <summary>
    /// Keeps a valid release unless a newer one is already kept; one dated after now (the clock moved back) is always replaced,
    /// or every start would ask again without ever keeping the answer. An invalid or missing release changes nothing.
    /// </summary>
    public void Remember(PublicRobloxClientRelease? release)
    {
        if (!RobloxClientRelease.IsValid(release)) return;
        var now = _clock.GetUtcNow();
        lock (_gate) if (_latest is null || _latest.RetrievedUtc <= release!.RetrievedUtc || _latest.RetrievedUtc > now) _latest = release;
    }

    /// <summary>The kept release when fresh, else one request. Cancellation and directory exceptions reach the caller.</summary>
    public async Task<PublicRobloxClientRelease?> ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (IsFresh) return Latest;
        var result = await _directory.GetCurrentWindowsPlayerReleaseAsync(ct).ConfigureAwait(false);
        if (result is { State: PublicDirectoryState.Success }) Remember(result.Value);
        return Latest;
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

    /// <summary>
    /// The address of Roblox's own installer for <paramref name="release"/> on Roblox's setup host, built only from a valid
    /// upload name ("version-" and 16 lowercase hex digits), so no other text can reach the address. Nothing is requested here.
    /// </summary>
    public static bool TryInstallerUri(PublicRobloxClientRelease? release, out Uri? uri)
    {
        uri = null;
        if (!IsValid(release)) return false;
        uri = new Uri("https://" + RobloxClientUpdate.SetupHost + "/" + release!.ClientVersionUpload + "-" + RobloxClientUpdate.InstallerFileName);
        return true;
    }

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
