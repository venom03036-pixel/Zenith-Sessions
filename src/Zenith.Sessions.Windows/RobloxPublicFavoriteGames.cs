using System.Globalization;
using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed partial class RobloxPublicDirectory : IPublicRobloxFavoriteGamesDirectory
{
    public Task<PublicDirectoryResult<IReadOnlyList<PublicRobloxFavoriteGame>>> GetFavoriteGamesAsync(long userId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (userId <= 0) return Task.FromResult(Invalid<IReadOnlyList<PublicRobloxFavoriteGame>>("Enter a positive 64-bit Roblox player ID."));
        // Public favorites route of the games domain (v2 users favorite games). Newest first; one page only, the
        // cursor is never followed. The route accepts only limit 10, 25, 50 or 100 (observed 2026-11-07).
        var uri = new Uri("https://games.roblox.com/v2/users/" + userId.ToString(CultureInfo.InvariantCulture)
            + "/favorite/games?limit=" + PublicRobloxFavoriteGameLimits.MaximumItems.ToString(CultureInfo.InvariantCulture) + "&sortOrder=Desc");
        return ReadJsonAsync(uri, ParseFavoriteGames, ct, restrictedIsUnknown: true);
    }

    private static PublicDirectoryResult<IReadOnlyList<PublicRobloxFavoriteGame>> ParseFavoriteGames(JsonElement root)
    {
        JsonElement data = ReadField(root, "data");
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > PublicRobloxFavoriteGameLimits.MaximumItems)
            throw new InvalidPublicResponseException();
        var items = new List<PublicRobloxFavoriteGame>(data.GetArrayLength()); var universes = new HashSet<long>();
        foreach (var item in data.EnumerateArray())
        {
            long universe = ReadId(item, "id");
            JsonElement rootPlace = ReadField(item, "rootPlace");
            if (rootPlace.ValueKind != JsonValueKind.Object) throw new InvalidPublicResponseException();
            long place = ReadId(rootPlace, "id");
            string name = ReadText(item, "name", PublicRobloxGameSearchLimits.MaximumNameCharacters);
            // The public listing contract already bounds IDs and names; a favorite is a listing with no extras.
            PublicRobloxGameSearchResult listing;
            try { listing = new PublicRobloxGameSearchResult(universe, place, name).Validated(); }
            catch (ArgumentException) { throw new InvalidPublicResponseException(); }
            if (!universes.Add(listing.UniverseId)) throw new InvalidPublicResponseException();
            items.Add(new(listing.UniverseId, listing.RootPlaceId, listing.Name));
        }
        return Success<IReadOnlyList<PublicRobloxFavoriteGame>>(items.AsReadOnly(),
            "Public favorites of this profile. They do not establish access, capacity or arrival.");
    }
}
