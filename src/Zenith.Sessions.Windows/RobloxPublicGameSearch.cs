using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed partial class RobloxPublicDirectory : IPublicRobloxGameSearchDirectory
{
    public Task<PublicDirectoryResult<PublicRobloxGameSearchPage>> SearchGamesAsync(PublicRobloxGameSearchQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        PublicRobloxGameSearchQuery exact;
        try { exact = query?.Validated() ?? throw new ArgumentException(); }
        catch (ArgumentException)
        { return Task.FromResult(Invalid<PublicRobloxGameSearchPage>("Enter a game name; use the destination parser for place IDs, links or private destinations.")); }
        // Roblox's official migration announcement names this public search route:
        // https://devforum.roblox.com/t/official-list-of-deprecated-web-endpoints/62889/59
        // The current grouped response is observed first-party HTTP data, not a promised stable
        // Creator Hub schema. See the sanitized Fisch fixture; no proxy/authenticated fallback.
        var uri = new Uri("https://apis.roblox.com/search-api/omni-search?searchQuery=" + Uri.EscapeDataString(exact.Text)
            + "&pageType=all&sessionId=" + exact.SessionId.ToString("D")
            + (exact.PageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(exact.PageToken)));
        return ReadJsonAsync(uri, root => ParseGameSearchPage(root, exact, ct), ct, restrictedIsUnknown: true);
    }

    private PublicDirectoryResult<PublicRobloxGameSearchPage> ParseGameSearchPage(JsonElement root,
        PublicRobloxGameSearchQuery query, CancellationToken ct)
    {
        JsonElement groups = ReadField(root, "searchResults");
        if (groups.ValueKind != JsonValueKind.Array || groups.GetArrayLength() > PublicRobloxGameSearchLimits.MaximumScannedItems)
            throw new InvalidPublicResponseException();
        var results = new List<PublicRobloxGameSearchResult>(PublicRobloxGameSearchLimits.MaximumItems);
        var universes = new Dictionary<long, PublicRobloxGameSearchResult>(); var places = new Dictionary<long, long>();
        int scanned = 0; bool truncated = false;
        foreach (var group in groups.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (ReadText(group, "contentGroupType", 64) != "Game") continue;
            JsonElement contents = ReadField(group, "contents");
            if (contents.ValueKind != JsonValueKind.Array) throw new InvalidPublicResponseException();
            foreach (var content in contents.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                if (++scanned > PublicRobloxGameSearchLimits.MaximumScannedItems) throw new InvalidPublicResponseException();
                long universe = ReadId(content, "universeId"), place = ReadId(content, "rootPlaceId");
                PublicRobloxGameSearchResult item;
                try
                {
                    item = new PublicRobloxGameSearchResult(universe, place, ReadText(content, "name", PublicRobloxGameSearchLimits.MaximumNameCharacters),
                        SearchOptionalCount(content, "creatorId", zeroIsUnknown: true), SearchOptionalCreator(content),
                        SearchOptionalCount(content, "playerCount"), SearchOptionalBoolean(content, "isSponsored"),
                        SearchOptionalBoolean(content, "creatorHasVerifiedBadge"), SearchOptionalCount(content, "totalUpVotes"),
                        SearchOptionalCount(content, "totalDownVotes")).Validated();
                }
                catch (ArgumentException) { throw new InvalidPublicResponseException(); }
                if (universes.TryGetValue(universe, out var previous))
                {
                    if (previous != item) throw new InvalidPublicResponseException();
                    continue;
                }
                if (places.TryGetValue(place, out long mapped) && mapped != universe) throw new InvalidPublicResponseException();
                universes.Add(universe, item); places[place] = universe;
                if (results.Count < PublicRobloxGameSearchLimits.MaximumItems) results.Add(item); else truncated = true;
            }
        }
        string? next = null;
        if (root.TryGetProperty("nextPageToken", out var token) && token.ValueKind != JsonValueKind.Null)
        {
            if (token.ValueKind != JsonValueKind.String) throw new InvalidPublicResponseException();
            next = token.GetString();
        }
        PublicRobloxGameSearchPage page;
        try { page = new PublicRobloxGameSearchPage(query, results, next, _clock.GetUtcNow(), truncated).Validated(); }
        catch (ArgumentException) { throw new InvalidPublicResponseException(); }
        return Success(page, truncated
            ? "Only the first fifty public results are shown. Refine the name; a next-page token does not recover omitted rows."
            : results.Count == 0 ? "No public game-name results were returned."
            : "Choose a public search result explicitly. Results do not establish playability, server capacity or native arrival.");
    }

    private static long? SearchOptionalCount(JsonElement item, string name, bool zeroIsUnknown = false)
    {
        if (!item.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return null;
        if (field.ValueKind != JsonValueKind.Number || !field.TryGetInt64(out long value) || value < 0)
            throw new InvalidPublicResponseException();
        return zeroIsUnknown && value == 0 ? null : value;
    }

    private static string? SearchOptionalCreator(JsonElement item)
    {
        if (!item.TryGetProperty("creatorName", out var field) || field.ValueKind == JsonValueKind.Null) return null;
        if (field.ValueKind != JsonValueKind.String) throw new InvalidPublicResponseException();
        string? value = field.GetString();
        return value == "" ? null : ReadText(item, "creatorName", PublicRobloxGameSearchLimits.MaximumCreatorNameCharacters);
    }

    private static bool? SearchOptionalBoolean(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return null;
        return field.ValueKind == JsonValueKind.True ? true : field.ValueKind == JsonValueKind.False ? false
            : throw new InvalidPublicResponseException();
    }
}
