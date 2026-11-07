namespace Zenith.Sessions.Core;

public static class PublicRobloxFavoriteGameLimits
{
    /// <summary>One page, newest first. The route accepts only 10, 25, 50 or 100 (observed 2026-11-07).</summary>
    public const int MaximumItems = 25;
}

/// <summary>
/// One entry of a profile's public favorites list. Listing metadata only: it needs no sign-in and proves
/// neither access nor playability, and it is never a saved Zenith favorite destination.
/// </summary>
public sealed record PublicRobloxFavoriteGame(long UniverseId, long RootPlaceId, string Name);

/// <summary>
/// Separate optional capability so existing directory mocks and providers remain unchanged. One explicit
/// caller-owned page per request; a continuation cursor is never followed. No credentials, retries or cache.
/// </summary>
public interface IPublicRobloxFavoriteGamesDirectory
{
    Task<PublicDirectoryResult<IReadOnlyList<PublicRobloxFavoriteGame>>> GetFavoriteGamesAsync(long userId, CancellationToken ct);
}
