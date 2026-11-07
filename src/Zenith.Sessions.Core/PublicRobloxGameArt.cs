namespace Zenith.Sessions.Core;

public enum PublicRobloxThumbnailState { Ready, Pending, Blocked, Unavailable }

public static class PublicRobloxGameArtLimits
{
    /// <summary>One card page: the presentation shows at most this many pictures at a time.</summary>
    public const int MaximumBatch = 12;
    public const int Width = 384;
    public const int Height = 216;
}

/// <summary>
/// One public 16:9 picture descriptor for a universe's root place. Only Ready carries a URI. A picture
/// is listing decoration; it never establishes playability, server capacity or native arrival.
/// </summary>
public sealed record PublicRobloxGameThumbnail(long UniverseId, Uri? ImageUri, PublicRobloxThumbnailState State,
    DateTimeOffset RetrievedUtc);

/// <summary>
/// Separate optional capability so existing directory mocks and providers remain unchanged. One explicit
/// caller-owned request per batch of 1–12 distinct universes; implementations create no polling, retries
/// or cache beyond the shared bounded encoded-image cache.
/// </summary>
public interface IPublicRobloxGameArtDirectory
{
    Task<PublicDirectoryResult<IReadOnlyList<PublicRobloxGameThumbnail>>> GetGameThumbnailsAsync(
        IReadOnlyList<long> universeIds, CancellationToken ct);
    Task<PublicDirectoryResult<EncodedPublicGameIcon>> FetchGameThumbnailAsync(
        PublicRobloxGameThumbnail thumbnail, CancellationToken ct);
}
