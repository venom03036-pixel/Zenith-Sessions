using System.Globalization;
using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed partial class RobloxPublicDirectory
{
    /// <summary>
    /// Anonymous detail-page media discovery followed by explicit-ID thumbnails. Original media
    /// order is retained independently of thumbnail response order. No default/homepage substitution.
    /// </summary>
    public async Task<PublicDirectoryResult<PublicRobloxGameGallery>> GetGameGalleryAsync(long requestedPlaceId, long universeId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (requestedPlaceId <= 0 || universeId <= 0) return Invalid<PublicRobloxGameGallery>("Choose positive public place and experience IDs.");
        // Experimental public media endpoint; the observed schema is retained in the research fixtures.
        // https://create.roblox.com/docs/cloud/reference/domains/games
        var mediaUri = new Uri("https://games.roblox.com/v2/games/" + universeId.ToString(CultureInfo.InvariantCulture) + "/media");
        var media = await ReadJsonAsync(mediaUri, ParseGalleryMedia, ct, restrictedIsUnknown: true).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (media.State != PublicDirectoryState.Success || media.Value is not { } entries)
            return new(media.State, null, media.Message, media.RetryAfter);

        var requestedIds = entries.Where(item => item.Kind == PublicRobloxGameGalleryKind.Image && item.Approved)
            .Select(item => item.ImageId!.Value).ToArray();
        Dictionary<long, GalleryThumbnail> thumbnails = new();
        if (requestedIds.Length > 0)
        {
            // The documented route can filter missing IDs. Never infer association from array position.
            // https://thumbnails.roblox.com/docs/json/v1
            string csv = string.Join(",", requestedIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
            var thumbnailUri = new Uri("https://thumbnails.roblox.com/v1/games/" + universeId.ToString(CultureInfo.InvariantCulture)
                + "/thumbnails?thumbnailIds=" + csv + "&size=576x324&format=Png&isCircular=false");
            var result = await ReadJsonAsync(thumbnailUri, root => ParseGalleryThumbnails(root, requestedIds), ct, restrictedIsUnknown: true).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (result.State != PublicDirectoryState.Success || result.Value is null)
                return new(result.State, null, result.Message, result.RetryAfter);
            thumbnails = result.Value;
        }

        DateTimeOffset observed = _clock.GetUtcNow(); var items = new PublicRobloxGameGalleryImage[entries.Length];
        for (int index = 0; index < entries.Length; index++)
        {
            ct.ThrowIfCancellationRequested(); var entry = entries[index]; Uri? uri = null;
            var state = entry.Kind != PublicRobloxGameGalleryKind.Image ? PublicRobloxGameGalleryState.Unsupported
                : !entry.Approved ? PublicRobloxGameGalleryState.Blocked : PublicRobloxGameGalleryState.Unavailable;
            if (entry.Kind == PublicRobloxGameGalleryKind.Image && entry.Approved && entry.ImageId is { } id && thumbnails.TryGetValue(id, out var thumbnail))
            { state = thumbnail.State; uri = thumbnail.ImageUri; }
            items[index] = new(requestedPlaceId, universeId, index, entry.ImageId, entry.Kind, state, uri,
                PublicRobloxGameGalleryLimits.Width, PublicRobloxGameGalleryLimits.Height, observed);
        }
        var gallery = new PublicRobloxGameGallery(requestedPlaceId, universeId, Array.AsReadOnly(items), observed).Validated();
        return Success(gallery, items.Length == 0 ? "No public detail-page media was returned for this experience."
            : "Public experience media in source order. Images do not verify access, an account or game arrival.");
    }

    public async Task<PublicDirectoryResult<EncodedPublicGameGalleryImage>> FetchGameGalleryImageAsync(PublicRobloxGameGalleryImage image, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { image?.Validated(); }
        catch (ArgumentException) { return Invalid<EncodedPublicGameGalleryImage>("The public gallery image descriptor is invalid."); }
        if (image is null || image.Kind != PublicRobloxGameGalleryKind.Image || image.State != PublicRobloxGameGalleryState.Ready
            || image.ThumbnailId is not { } id || image.ImageUri is null || !TryValidateAvatarUri(image.ImageUri.OriginalString, out Uri? uri))
            return Invalid<EncodedPublicGameGalleryImage>("The public gallery image is unavailable or has an invalid address.");
        var result = await FetchPublicPngAsync(PublicImageKind.GameGallery, image.UniverseId, uri!, image.RetrievedUtc, ct, id).ConfigureAwait(false);
        return new(result.State, result.Value is { } bytes ? new EncodedPublicGameGalleryImage(bytes.Bytes, bytes.MediaType, bytes.RetrievedUtc) : null,
            result.Message, result.RetryAfter);
    }

    private static PublicDirectoryResult<GalleryMediaEntry[]> ParseGalleryMedia(JsonElement root)
    {
        JsonElement data = ReadField(root, "data");
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > PublicRobloxGameGalleryLimits.MaximumItems)
            throw new InvalidPublicResponseException();
        var entries = new GalleryMediaEntry[data.GetArrayLength()]; var ids = new HashSet<long>(); int index = 0;
        foreach (var item in data.EnumerateArray())
        {
            long type = ReadId(item, "assetTypeId"); string name = ReadText(item, "assetType", 40);
            JsonElement approval = ReadField(item, "approved");
            if (approval.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidPublicResponseException();
            bool approved = approval.GetBoolean();
            var kind = type == 1 && name == "Image" ? PublicRobloxGameGalleryKind.Image
                : type == 62 && name == "Video" || type == 33 && name == "YouTubeVideo" ? PublicRobloxGameGalleryKind.Video
                : PublicRobloxGameGalleryKind.Unsupported;
            if (type == 1 && kind != PublicRobloxGameGalleryKind.Image) throw new InvalidPublicResponseException();
            long? id = null;
            if (item.TryGetProperty("imageId", out var imageId) && imageId.ValueKind != JsonValueKind.Null)
            {
                if (imageId.ValueKind != JsonValueKind.Number || !imageId.TryGetInt64(out long parsed) || parsed <= 0 || !ids.Add(parsed))
                    throw new InvalidPublicResponseException();
                id = parsed;
            }
            if (kind == PublicRobloxGameGalleryKind.Image && approved && id is null) throw new InvalidPublicResponseException();
            // Video hashes/titles/addresses and public alt text are deliberately not retained or used.
            entries[index++] = new(id, kind, approved);
        }
        return Success(entries, "Public experience media descriptors.");
    }

    private static PublicDirectoryResult<Dictionary<long, GalleryThumbnail>> ParseGalleryThumbnails(JsonElement root, long[] requestedIds)
    {
        JsonElement data = ReadField(root, "data");
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > requestedIds.Length) throw new InvalidPublicResponseException();
        var expected = requestedIds.ToHashSet(); var result = new Dictionary<long, GalleryThumbnail>();
        foreach (var item in data.EnumerateArray())
        {
            long id = ReadId(item, "targetId");
            if (!expected.Contains(id) || result.ContainsKey(id)) throw new InvalidPublicResponseException();
            string rawState = ReadText(item, "state", 32); Uri? imageUri = null;
            var state = rawState switch
            {
                "Completed" => PublicRobloxGameGalleryState.Ready,
                "Pending" or "InReview" => PublicRobloxGameGalleryState.Pending,
                "Blocked" => PublicRobloxGameGalleryState.Blocked,
                _ => PublicRobloxGameGalleryState.Unavailable
            };
            if (state == PublicRobloxGameGalleryState.Ready && !TryValidateAvatarUri(ReadText(item, "imageUrl", 2048), out imageUri))
                throw new InvalidPublicResponseException();
            result.Add(id, new(state, imageUri));
        }
        return Success(result, "Explicit public gallery thumbnails.");
    }

    private sealed record GalleryMediaEntry(long? ImageId, PublicRobloxGameGalleryKind Kind, bool Approved);
    private sealed record GalleryThumbnail(PublicRobloxGameGalleryState State, Uri? ImageUri);
}
