using System.Globalization;
using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed partial class RobloxPublicDirectory : IPublicRobloxGameBatchDirectory
{
    public async Task<PublicDirectoryResult<PublicRobloxGame>> LookupGameAsync(long requestedPlaceId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (requestedPlaceId <= 0) return Invalid<PublicRobloxGame>("Enter a positive 64-bit Roblox place ID.");
        // Roblox's official migration announcement explicitly recommends this public replacement:
        // https://devforum.roblox.com/t/action-needed-sunsetting-apirobloxcom/1796897
        // Unlike /v1/games/multiget-place-details it needs no cookie. Its absence from the current
        // Creator Hub reference is a documentation limitation, not permission to use authentication.
        var mappingUri = new Uri("https://apis.roblox.com/universes/v1/places/"
            + requestedPlaceId.ToString(CultureInfo.InvariantCulture) + "/universe");
        var mapping = await ReadJsonAsync(mappingUri, root =>
        {
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidPublicResponseException();
            if (root.TryGetProperty("placeId", out _)
                && ReadId(root, "placeId") != requestedPlaceId) throw new InvalidPublicResponseException();
            return Success(new PublicUniverseMapping(ReadId(root, "universeId")), "Public place mapping received.");
        }, ct, restrictedIsUnknown: true).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (mapping.Value is not { } mapped || mapping.State != PublicDirectoryState.Success)
            return new(mapping.State, null, mapping.Message, mapping.RetryAfter);

        // Anonymous security alternative and the response schema are published here:
        // https://create.roblox.com/docs/cloud/reference/domains/games#games_get_v1_games
        // Request only one universe and retain the requested place separately from rootPlaceId.
        var gameUri = new Uri("https://games.roblox.com/v1/games?universeIds="
            + mapped.UniverseId.ToString(CultureInfo.InvariantCulture));
        return await ReadJsonAsync(gameUri, root =>
        {
            JsonElement data = ReadField(root, "data");
            if (data.ValueKind != JsonValueKind.Array) throw new InvalidPublicResponseException();
            if (data.GetArrayLength() == 0) return Unknown<PublicRobloxGame>("No public game information was returned.");
            if (data.GetArrayLength() != 1) throw new InvalidPublicResponseException();
            JsonElement item = data[0];
            bool restricted = false;
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidPublicResponseException();
            if (item.TryGetProperty("isContentRestricted", out JsonElement restriction))
                restricted = restriction.ValueKind == JsonValueKind.True ? true
                    : restriction.ValueKind == JsonValueKind.False ? false : throw new InvalidPublicResponseException();
            JsonElement idField = ReadField(item, "id");
            if (idField.ValueKind != JsonValueKind.Number || !idField.TryGetInt64(out long id)
                || (id != mapped.UniverseId && !(restricted && id == 0))) throw new InvalidPublicResponseException();
            // The documented restriction flag deliberately replaces content with placeholders/zeroes.
            // Do not infer restriction from localized names or expose those placeholders as real data.
            if (restricted) return Unknown<PublicRobloxGame>("The requested public game information is unavailable or restricted.");
            return Success(ParseGameFields(item, mapped.UniverseId, requestedPlaceId),
                "Public game information. This does not establish playability, server capacity, or native client arrival.");
        }, ct, restrictedIsUnknown: true).ConfigureAwait(false);
    }

    // One parser for a games listing item, shared by the single and the batch lookup.
    private PublicRobloxGame ParseGameFields(JsonElement item, long universeId, long requestedPlaceId)
    {
        long rootPlaceId = ReadId(item, "rootPlaceId");
        JsonElement creator = ReadField(item, "creator");
        PublicRobloxCreatorKind creatorKind = ReadText(creator, "type", 16) switch
        {
            "User" => PublicRobloxCreatorKind.User,
            "Group" => PublicRobloxCreatorKind.Group,
            _ => throw new InvalidPublicResponseException()
        };
        JsonElement playingField = ReadField(item, "playing");
        if (playingField.ValueKind != JsonValueKind.Number || !playingField.TryGetInt64(out long playing) || playing < 0)
            throw new InvalidPublicResponseException();
        JsonElement maximumField = ReadField(item, "maxPlayers");
        if (maximumField.ValueKind != JsonValueKind.Number || !maximumField.TryGetInt32(out int maximum) || maximum <= 0)
            throw new InvalidPublicResponseException();
        // Optional listing fields: absent stays null (unknown), never 0 or "not allowed"; present but malformed is
        // invalid. Duplicate names were already rejected for the whole document before parsing.
        long? visits = null;
        if (item.TryGetProperty("visits", out JsonElement visitsField))
            visits = visitsField.ValueKind == JsonValueKind.Number && visitsField.TryGetInt64(out long count) && count >= 0
                ? count : throw new InvalidPublicResponseException();
        bool? privateServers = null;
        if (item.TryGetProperty("createVipServersAllowed", out JsonElement privateField))
            privateServers = privateField.ValueKind == JsonValueKind.True ? true
                : privateField.ValueKind == JsonValueKind.False ? false : throw new InvalidPublicResponseException();
        return new PublicRobloxGame(requestedPlaceId, universeId, rootPlaceId, ReadText(item, "name", 256), ReadId(creator, "id"),
            ReadText(creator, "name", 100), creatorKind, playing, maximum, _clock.GetUtcNow(), visits, privateServers);
    }

    /// <summary>Root-place listings for a few universes in one request; restricted or omitted universes are left out, never guessed.</summary>
    public Task<PublicDirectoryResult<IReadOnlyList<PublicRobloxGame>>> LookupGamesAsync(IReadOnlyList<long> universeIds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        const string invalid = "Request between one and six distinct positive 64-bit universe IDs.";
        if (universeIds is null || universeIds.Count is 0 or > PublicRobloxGameBatchLimits.MaximumUniverses)
            return Task.FromResult(Invalid<IReadOnlyList<PublicRobloxGame>>(invalid));
        var requested = new long[universeIds.Count]; var distinct = new HashSet<long>();
        for (int index = 0; index < requested.Length; index++)
        {
            long id = universeIds[index];
            if (id <= 0 || !distinct.Add(id)) return Task.FromResult(Invalid<IReadOnlyList<PublicRobloxGame>>(invalid));
            requested[index] = id;
        }
        // The same documented anonymous route as the single lookup, with several universes.
        var uri = new Uri("https://games.roblox.com/v1/games?universeIds="
            + string.Join(",", requested.Select(id => id.ToString(CultureInfo.InvariantCulture))));
        return ReadJsonAsync(uri, root => ParseGameBatch(root, distinct), ct, restrictedIsUnknown: true);
    }

    private PublicDirectoryResult<IReadOnlyList<PublicRobloxGame>> ParseGameBatch(JsonElement root, HashSet<long> requested)
    {
        JsonElement data = ReadField(root, "data");
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > requested.Count) throw new InvalidPublicResponseException();
        var games = new List<PublicRobloxGame>(data.GetArrayLength()); var seen = new HashSet<long>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidPublicResponseException();
            if (item.TryGetProperty("isContentRestricted", out JsonElement restriction))
            {
                if (restriction.ValueKind == JsonValueKind.True) continue; // A placeholder entry (id 0): omitted, never exposed.
                if (restriction.ValueKind != JsonValueKind.False) throw new InvalidPublicResponseException();
            }
            long universe = ReadId(item, "id");
            if (!requested.Contains(universe) || !seen.Add(universe)) throw new InvalidPublicResponseException();
            var game = ParseGameFields(item, universe, requestedPlaceId: 0);
            games.Add(game with { RequestedPlaceId = game.RootPlaceId });
        }
        return Success<IReadOnlyList<PublicRobloxGame>>(games.AsReadOnly(),
            "Public game information. This does not establish playability, server capacity, or native client arrival.");
    }

    public Task<PublicDirectoryResult<PublicRobloxGameIcon>> GetGameIconAsync(long universeId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (universeId <= 0)
            return Task.FromResult(Invalid<PublicRobloxGameIcon>("Enter a positive 64-bit Roblox universe ID."));
        // The official reference promises targetId correlation, not response ordering, and supports
        // anonymous PlaceHolder/150x150/Png requests. No auto-generated/authenticated policy is used.
        // https://create.roblox.com/docs/cloud/reference/domains/thumbnails#thumbnails_get_v1_games_icons
        var uri = new Uri("https://thumbnails.roblox.com/v1/games/icons?universeIds="
            + universeId.ToString(CultureInfo.InvariantCulture)
            + "&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false");
        return ReadJsonAsync(uri, root =>
        {
            JsonElement data = ReadField(root, "data");
            if (data.ValueKind != JsonValueKind.Array) throw new InvalidPublicResponseException();
            if (data.GetArrayLength() == 0) return Unknown<PublicRobloxGameIcon>("No public game icon was returned.");
            if (data.GetArrayLength() != 1) throw new InvalidPublicResponseException();
            JsonElement item = data[0];
            if (ReadId(item, "targetId") != universeId) throw new InvalidPublicResponseException();
            string state = ReadText(item, "state", 32);
            if (state is "Pending" or "InReview")
                return Deferred<PublicRobloxGameIcon>("The public game icon is still being prepared.", TimeSpan.FromSeconds(30));
            if (state is "Blocked" or "Error") return Unknown<PublicRobloxGameIcon>("The public game icon is unavailable.");
            if (state != "Completed") return Unknown<PublicRobloxGameIcon>("The public game icon state is not supported.");
            if (!TryValidateAvatarUri(ReadText(item, "imageUrl", 2048), out Uri? imageUri)) throw new InvalidPublicResponseException();
            return Success(new PublicRobloxGameIcon(universeId, imageUri!, 150, 150, _clock.GetUtcNow()),
                "Public icon of the universe's root place.");
        }, ct, restrictedIsUnknown: true);
    }

    public async Task<PublicDirectoryResult<EncodedPublicGameIcon>> FetchGameIconAsync(PublicRobloxGameIcon icon, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (icon is null || icon.UniverseId <= 0 || icon.Width != 150 || icon.Height != 150
            || icon.ImageUri is null || !TryValidateAvatarUri(icon.ImageUri.OriginalString, out Uri? uri))
            return Invalid<EncodedPublicGameIcon>("The public game icon descriptor is invalid.");
        var result = await FetchPublicPngAsync(PublicImageKind.GameIcon, icon.UniverseId, uri!, icon.RetrievedUtc, ct).ConfigureAwait(false);
        return new(result.State, result.Value is { } value
            ? new EncodedPublicGameIcon(value.Bytes, value.MediaType, value.RetrievedUtc) : null, result.Message, result.RetryAfter);
    }

    private sealed record PublicUniverseMapping(long UniverseId);
}
