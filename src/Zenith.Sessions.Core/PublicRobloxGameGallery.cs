namespace Zenith.Sessions.Core;

public static class PublicRobloxGameGalleryLimits
{
    public const int MaximumItems = 10, Width = 576, Height = 324;
}

public enum PublicRobloxGameGalleryKind { Image, Video, Unsupported }
public enum PublicRobloxGameGalleryState { Ready, Pending, Blocked, Unavailable, Unsupported }

/// <summary>
/// One source-order public media descriptor, never a client image or authorization. Width/height
/// are the fixed requested PNG dimensions; the fetched bytes still require complete validation.
/// Unsupported videos never carry an external address. No raw media title/response is retained.
/// </summary>
public sealed record PublicRobloxGameGalleryImage(long RequestedPlaceId, long UniverseId, int Position,
    long? ThumbnailId, PublicRobloxGameGalleryKind Kind, PublicRobloxGameGalleryState State,
    Uri? ImageUri, int Width, int Height, DateTimeOffset RetrievedUtc)
{
    public PublicRobloxGameGalleryImage Validated()
    {
        if (RequestedPlaceId <= 0 || UniverseId <= 0 || Position is < 0 or >= PublicRobloxGameGalleryLimits.MaximumItems
            || ThumbnailId is <= 0 || !Enum.IsDefined(Kind) || !Enum.IsDefined(State)
            || Width != PublicRobloxGameGalleryLimits.Width || Height != PublicRobloxGameGalleryLimits.Height || RetrievedUtc == default)
            throw new ArgumentException("The public gallery descriptor has invalid context or bounds.");
        if (Kind != PublicRobloxGameGalleryKind.Image && State != PublicRobloxGameGalleryState.Unsupported
            || State == PublicRobloxGameGalleryState.Ready && (Kind != PublicRobloxGameGalleryKind.Image || ThumbnailId is null || ImageUri is null)
            || State != PublicRobloxGameGalleryState.Ready && ImageUri is not null
            || Kind == PublicRobloxGameGalleryKind.Image && State == PublicRobloxGameGalleryState.Pending && ThumbnailId is null)
            throw new ArgumentException("The public gallery descriptor has an inconsistent image state.");
        if (ImageUri is { } uri && (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.OriginalString.Length > 2048))
            throw new ArgumentException("The public gallery descriptor has an invalid image address.");
        return this;
    }
}

/// <summary>Privately copied ordered public media. A genuine empty gallery stays empty.</summary>
public sealed record PublicRobloxGameGallery(long RequestedPlaceId, long UniverseId,
    IReadOnlyList<PublicRobloxGameGalleryImage> Items, DateTimeOffset RetrievedUtc)
{
    public PublicRobloxGameGallery Validated()
    {
        if (RequestedPlaceId <= 0 || UniverseId <= 0 || RetrievedUtc == default || Items is null
            || Items.Count > PublicRobloxGameGalleryLimits.MaximumItems)
            throw new ArgumentException("The public gallery has invalid context or bounds.");
        var owned = new PublicRobloxGameGalleryImage[Items.Count]; var ids = new HashSet<long>();
        for (int index = 0; index < owned.Length; index++)
        {
            var item = Items[index]?.Validated() ?? throw new ArgumentException("The public gallery contains an invalid item.");
            if (item.RequestedPlaceId != RequestedPlaceId || item.UniverseId != UniverseId || item.Position != index
                || item.ThumbnailId is { } id && !ids.Add(id)) throw new ArgumentException("The public gallery contains inconsistent or duplicate media.");
            owned[index] = item;
        }
        return this with { Items = Array.AsReadOnly(owned) };
    }
}

/// <summary>Bounded PNG bytes; the caller must fully decode/check the fixed dimensions before display.</summary>
public sealed record EncodedPublicGameGalleryImage(ReadOnlyMemory<byte> Bytes, string MediaType, DateTimeOffset RetrievedUtc);

/// <summary>Optional anonymous public media. No sign-in, native-client, capture or input capability.</summary>
public interface IPublicRobloxGameGallery
{
    Task<PublicDirectoryResult<PublicRobloxGameGallery>> GetGameGalleryAsync(long requestedPlaceId, long universeId, CancellationToken ct);
    Task<PublicDirectoryResult<EncodedPublicGameGalleryImage>> FetchGameGalleryImageAsync(PublicRobloxGameGalleryImage image, CancellationToken ct);
}
