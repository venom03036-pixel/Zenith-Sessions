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
