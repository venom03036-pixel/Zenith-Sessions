namespace Zenith.Sessions.Core;

public enum PublicRobloxCreatorKind { User, Group }

/// <summary>
/// A public, point-in-time game observation. RequestedPlaceId remains the requested target even when
/// it is a subplace; RootPlaceId is descriptive metadata and must never replace that target.
/// Playing is the universe-wide public count, not a particular server's occupancy or admission limit.
/// </summary>
public sealed record PublicRobloxGame(long RequestedPlaceId, long UniverseId, long RootPlaceId,
    string Name, long CreatorId, string CreatorName, PublicRobloxCreatorKind CreatorKind,
    long Playing, int MaxPlayers, DateTimeOffset RetrievedUtc)
{
    public string EvidenceLabel => "Public game information";
}

/// <summary>A public icon for a universe's root place; never an avatar or a requested-place binding.</summary>
public sealed record PublicRobloxGameIcon(long UniverseId, Uri ImageUri, int Width, int Height,
    DateTimeOffset RetrievedUtc);

/// <summary>
/// Bounded encoded PNG bytes. The presentation owner must fully validate and decode with explicit
/// frame/pixel limits before display; successful transport does not establish a valid full PNG.
/// </summary>
public sealed record EncodedPublicGameIcon(ReadOnlyMemory<byte> Bytes, string MediaType,
    DateTimeOffset RetrievedUtc);

/// <summary>
/// Separate optional capability so existing public-profile clients and mocks remain unchanged.
/// Operations are explicit caller-owned requests; implementations create no polling or retries.
/// None of this public metadata proves authorization, playability, or a native client's identity.
/// </summary>
public interface IPublicRobloxGameDirectory
{
    Task<PublicDirectoryResult<PublicRobloxGame>> LookupGameAsync(long requestedPlaceId, CancellationToken ct);
    Task<PublicDirectoryResult<PublicRobloxGameIcon>> GetGameIconAsync(long universeId, CancellationToken ct);
    Task<PublicDirectoryResult<EncodedPublicGameIcon>> FetchGameIconAsync(PublicRobloxGameIcon icon, CancellationToken ct);
    void ClearMemoryCache();
}

public static class PublicRobloxGameBatchLimits
{
    /// <summary>Recently played names: a handful of universes per request.</summary>
    public const int MaximumUniverses = 6;
}

/// <summary>
/// Separate optional capability: root-place listings for 1–6 distinct universes in one explicit request. Each item
/// carries RequestedPlaceId equal to its RootPlaceId. Restricted or omitted universes are left out, never guessed.
/// </summary>
public interface IPublicRobloxGameBatchDirectory
{
    Task<PublicDirectoryResult<IReadOnlyList<PublicRobloxGame>>> LookupGamesAsync(IReadOnlyList<long> universeIds, CancellationToken ct);
}
