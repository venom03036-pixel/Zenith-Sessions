using System.Globalization;
using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed partial class RobloxPublicDirectory : IPublicRobloxGameArtDirectory
{
    public Task<PublicDirectoryResult<IReadOnlyList<PublicRobloxGameThumbnail>>> GetGameThumbnailsAsync(
        IReadOnlyList<long> universeIds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        const string invalid = "Request between one and twelve distinct positive 64-bit universe IDs.";
        if (universeIds is null || universeIds.Count is 0 or > PublicRobloxGameArtLimits.MaximumBatch)
            return Task.FromResult(Invalid<IReadOnlyList<PublicRobloxGameThumbnail>>(invalid));
        var requested = new long[universeIds.Count]; var distinct = new HashSet<long>();
        for (int index = 0; index < requested.Length; index++)
        {
            long id = universeIds[index];
            if (id <= 0 || !distinct.Add(id)) return Task.FromResult(Invalid<IReadOnlyList<PublicRobloxGameThumbnail>>(invalid));
            requested[index] = id;
        }
        // Anonymous batch route documented at https://thumbnails.roblox.com/docs/json/v1 (multiget/thumbnails).
        // One 16:9 picture per universe; the per-universe object shape is observed first-party data (2026-11-07).
        var uri = new Uri("https://thumbnails.roblox.com/v1/games/multiget/thumbnails?universeIds="
            + string.Join(",", requested.Select(id => id.ToString(CultureInfo.InvariantCulture)))
            + "&countPerUniverse=1&defaults=true&size=" + PublicRobloxGameArtLimits.Width.ToString(CultureInfo.InvariantCulture)
            + "x" + PublicRobloxGameArtLimits.Height.ToString(CultureInfo.InvariantCulture) + "&format=Png&isCircular=false");
        return ReadJsonAsync(uri, root => ParseThumbnailBatch(root, requested), ct, restrictedIsUnknown: true);
    }

    private PublicDirectoryResult<IReadOnlyList<PublicRobloxGameThumbnail>> ParseThumbnailBatch(JsonElement root, long[] requested)
    {
        JsonElement data = ReadField(root, "data");
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > requested.Length) throw new InvalidPublicResponseException();
        var expected = new HashSet<long>(requested); var found = new Dictionary<long, PublicRobloxGameThumbnail>();
        DateTimeOffset now = _clock.GetUtcNow();
        foreach (var item in data.EnumerateArray())
        {
            long universe = ReadId(item, "universeId");
            if (!expected.Contains(universe) || found.ContainsKey(universe)) throw new InvalidPublicResponseException();
            found.Add(universe, ParseThumbnail(item, universe, now));
        }
        // A universe the response omitted is unavailable, not an invalid response: the batch stays usable.
        var result = new PublicRobloxGameThumbnail[requested.Length];
        for (int index = 0; index < result.Length; index++)
            result[index] = found.TryGetValue(requested[index], out var thumbnail) ? thumbnail
                : new(requested[index], null, PublicRobloxThumbnailState.Unavailable, now);
        return Success<IReadOnlyList<PublicRobloxGameThumbnail>>(Array.AsReadOnly(result),
            "Public experience pictures. They do not establish playability, capacity or arrival.");
    }

    private static PublicRobloxGameThumbnail ParseThumbnail(JsonElement item, long universe, DateTimeOffset now)
    {
        if (item.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            return new(universe, null, PublicRobloxThumbnailState.Unavailable, now);
        JsonElement thumbnails = ReadField(item, "thumbnails");
        if (thumbnails.ValueKind != JsonValueKind.Array || thumbnails.GetArrayLength() > 1) throw new InvalidPublicResponseException();
        if (thumbnails.GetArrayLength() == 0) return new(universe, null, PublicRobloxThumbnailState.Unavailable, now);
        JsonElement first = thumbnails[0];
        var state = ReadText(first, "state", 32) switch
        {
            "Completed" => PublicRobloxThumbnailState.Ready,
            "Pending" or "InReview" => PublicRobloxThumbnailState.Pending,
            "Blocked" => PublicRobloxThumbnailState.Blocked,
            _ => PublicRobloxThumbnailState.Unavailable
        };
        Uri? imageUri = null;
        if (state == PublicRobloxThumbnailState.Ready && !TryValidateAvatarUri(ReadText(first, "imageUrl", 2048), out imageUri))
            throw new InvalidPublicResponseException();
        return new(universe, imageUri, state, now);
    }

    public async Task<PublicDirectoryResult<EncodedPublicGameIcon>> FetchGameThumbnailAsync(PublicRobloxGameThumbnail thumbnail, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (thumbnail is null || thumbnail.UniverseId <= 0 || thumbnail.State != PublicRobloxThumbnailState.Ready
            || thumbnail.ImageUri is null || !TryValidateAvatarUri(thumbnail.ImageUri.OriginalString, out Uri? uri))
            return Invalid<EncodedPublicGameIcon>("The public game picture descriptor is invalid.");
        var result = await FetchPublicPngAsync(PublicImageKind.GameThumbnail, thumbnail.UniverseId, uri!, thumbnail.RetrievedUtc, ct).ConfigureAwait(false);
        return new(result.State, result.Value is { } value
            ? new EncodedPublicGameIcon(value.Bytes, value.MediaType, value.RetrievedUtc) : null, result.Message, result.RetryAfter);
    }
}
