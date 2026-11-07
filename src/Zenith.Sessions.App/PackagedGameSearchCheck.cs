using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

// Synthetic transport only. Executes the staged production parser without real network or accounts.
internal static class PackagedGameSearchCheck
{
    internal static async Task VerifyAsync(CancellationToken ct)
    {
        using var transport = new Fixture();
        using var http = new HttpClient(transport) { Timeout = TimeSpan.FromSeconds(2) };
        IPublicRobloxGameSearchDirectory directory = new RobloxPublicDirectory(http);
        var query = new PublicRobloxGameSearchQuery("Synthetic place", new Guid("62d6231d-bce6-4677-9bdb-ce149d515976"));
        var result = await directory.SearchGamesAsync(query, ct);
        if (result.State != PublicDirectoryState.Success || result.Value is not { } page
            || page.Query != query || page.Items.Count != 1 || page.Items[0].RootPlaceId != 9007199254740993L
            || page.Items[0].UniverseId != 9007199254740995L || page.Items[0].CreatorId is not null
            || page.Items[0].CreatorName is not null || page.Items[0].Playing != 3000000000L
            || page.NextPageToken != "synthetic-next" || page.ResultsTruncated || transport.Calls != 1)
            throw new InvalidDataException("Packaged game-name search lost query, Int64 or unknown-metadata boundaries.");

        var rejected = await directory.SearchGamesAsync(query with
        {
            Text = "https://www.roblox.com/games/123?privateServerLinkCode=synthetic-private-marker"
        }, ct);
        if (rejected.State != PublicDirectoryState.InvalidInput || rejected.Value is not null || transport.Calls != 1)
            throw new InvalidDataException("Packaged game-name search sent a private destination to public search.");

        var parsed = RobloxDestinationParser.Parse("https://www.roblox.com/games/9007199254740993/Example");
        if (parsed.RequestedDestination is not { PlaceId: 9007199254740993L, JobId: null })
            throw new InvalidDataException("The direct destination parser changed a requested subplace.");
    }

    private sealed class Fixture : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            if (request.Method != HttpMethod.Get || request.RequestUri?.Host != "apis.roblox.com"
                || request.RequestUri.AbsolutePath != "/search-api/omni-search"
                || request.Headers.Authorization is not null || request.Headers.Contains("Cookie"))
                throw new InvalidOperationException("The packaged search requested an unexpected or authenticated route.");
            const string json = """
                {"searchResults":[{"contentGroupType":"Game","contents":[{"universeId":9007199254740995,"rootPlaceId":9007199254740993,"name":"Synthetic place","creatorId":0,"creatorName":"","playerCount":3000000000,"isSponsored":false}]}],"nextPageToken":"synthetic-next"}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
