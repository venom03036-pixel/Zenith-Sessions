using System.Globalization;

namespace Zenith.Sessions.Core;

public enum PublicDirectoryState { Success, Unknown, Deferred, Failure, InvalidInput }

// Messages are application-owned text. Provider response bodies and URLs are never error messages.
public sealed record PublicDirectoryResult<T>(PublicDirectoryState State, T? Value, string Message,
    TimeSpan? RetryAfter = null) where T : class;

/// <summary>Roblox's public profile information; never proof of authorization or a local client's identity.</summary>
public sealed record PublicRobloxUser(long UserId, string Username, string DisplayName, DateTimeOffset RetrievedUtc)
{
    public string EvidenceLabel => "Public profile information";
}

public sealed record PublicRobloxAvatar(long UserId, Uri ImageUri, int Width, int Height, DateTimeOffset RetrievedUtc);

/// <summary>Bounded encoded PNG bytes. Decode with explicit pixel limits; the directory never creates UI images.</summary>
public sealed record EncodedPublicAvatar(ReadOnlyMemory<byte> Bytes, string MediaType, DateTimeOffset RetrievedUtc);

public interface IPublicRobloxDirectory
{
    Task<PublicDirectoryResult<PublicRobloxUser>> LookupUserAsync(long userId, CancellationToken ct);
    Task<PublicDirectoryResult<PublicRobloxAvatar>> GetAvatarAsync(long userId, CancellationToken ct);
    Task<PublicDirectoryResult<EncodedPublicAvatar>> FetchAvatarAsync(PublicRobloxAvatar avatar, CancellationToken ct);
    void ClearMemoryCache();
}

public static class PublicRobloxAvatarBatchLimits
{
    /// <summary>
    /// Users per headshot request. Roblox accepts more, but one reply item is about 200 bytes, so 50 keeps a
    /// full reply (about 10 KiB) far inside the directory's 64 KiB JSON cap.
    /// </summary>
    public const int MaximumBatch = 50;
}

/// <summary>
/// One user's public headshot from a batch. Only Ready carries an avatar descriptor; download it with
/// <see cref="IPublicRobloxDirectory.FetchAvatarAsync"/>. A user Roblox left out of the reply is Unavailable.
/// </summary>
public sealed record PublicRobloxAvatarBatchItem(long UserId, PublicRobloxThumbnailState State, PublicRobloxAvatar? Avatar);

/// <summary>
/// Separate optional capability so existing directory fakes and providers stay unchanged. One explicit
/// caller-owned request per batch of 1–50 distinct users, sharing the directory's request slots, rate-limit
/// pause and response bounds. Implementations create no polling, retries or extra cache.
/// </summary>
public interface IPublicRobloxAvatarBatchDirectory
{
    Task<PublicDirectoryResult<IReadOnlyList<PublicRobloxAvatarBatchItem>>> GetAvatarsAsync(
        IReadOnlyList<long> userIds, CancellationToken ct);
}

public static class PublicRobloxUsername
{
    public const int MinimumLength = 3;
    public const int MaximumLength = 20;

    /// <summary>
    /// Names per lookup request. Roblox accepts more, but one reply item can reach about 750 bytes with a long
    /// escaped display name, so 50 keeps a full reply far inside the directory's 64 KiB JSON cap.
    /// </summary>
    public const int MaximumBatch = 50;

    // 3 to 20 ASCII letters, digits or underscores, exactly as typed: no trimming or culture-dependent rules.
    public static bool IsValid(string? text) => text is { Length: >= MinimumLength and <= MaximumLength }
        && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}

/// <summary>
/// A public profile Roblox found for one requested username. Roblox also matches a user's previous
/// usernames, so <see cref="PublicRobloxUser.Username"/> can differ from <see cref="RequestedUsername"/>.
/// </summary>
public sealed record PublicRobloxUsernameMatch(string RequestedUsername, PublicRobloxUser User);

/// <summary>
/// Separate optional capability so existing directory fakes and providers stay unchanged. One explicit,
/// anonymous, caller-owned request per batch of 1–50 distinct usernames (Roblox names are case-insensitive),
/// sharing the directory's request slots, rate-limit pause and response bounds. Matches come back in request
/// order; a name Roblox did not return (unknown or banned) is left out. No polling, retries or cache.
/// </summary>
public interface IPublicRobloxUsernameDirectory
{
    Task<PublicDirectoryResult<IReadOnlyList<PublicRobloxUsernameMatch>>> LookupUsersByNameAsync(
        IReadOnlyList<string> usernames, CancellationToken ct);
}

public static class PublicRobloxUserId
{
    // No signs, grouping separators, overflow, whitespace, or culture-dependent parsing.
    public static bool TryParse(string? text, out long userId)
    {
        userId = 0;
        return text is { Length: > 0 and <= 19 } && text.All(char.IsAsciiDigit)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out userId) && userId > 0;
    }
}
