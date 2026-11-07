using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

// A profile's public favorites need no sign-in. Every request ends at an owned synthetic handler.
public sealed class PublicFavoriteGamesDirectoryTests
{
    [Fact]
    public async Task RequestsTheExactPublicFavoritesEndpoint()
    {
        using var handler = new Handler((request, _) =>
        {
            Anonymous(request);
            // Roblox accepts only limit 10, 25, 50 or 100 on this route (observed 2026-11-07).
            Assert.Equal("https://games.roblox.com/v2/users/156/favorite/games?limit=25&sortOrder=Desc", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json(Page(Item(10766456501, 74205509034203, "The Hunt: Roblox 20"))));
        });
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).GetFavoriteGamesAsync(156, default);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        var item = Assert.Single(result.Value!);
        Assert.Equal(10766456501, item.UniverseId); Assert.Equal(74205509034203, item.RootPlaceId); Assert.Equal("The Hunt: Roblox 20", item.Name);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ReadsIdRootPlaceAndNameAndIgnoresTheCursor()
    {
        var result = await Request(Page([Item(1, 11, "A"), Item(2, 22, "B")], next: "eyJrZXkiOiJpZF8y"));
        Assert.Equal(PublicDirectoryState.Success, result.State); var games = result.Value!;
        Assert.Equal(new long[] { 1, 2 }, games.Select(game => game.UniverseId).ToArray());
        Assert.Equal(new long[] { 11, 22 }, games.Select(game => game.RootPlaceId).ToArray());
        Assert.Equal(new[] { "A", "B" }, games.Select(game => game.Name).ToArray());
    }

    [Fact]
    public async Task EmptyListIsSuccessWithNoItems()
    {
        var result = await Request("{\"previousPageCursor\":null,\"nextPageCursor\":null,\"data\":[]}");
        Assert.Equal(PublicDirectoryState.Success, result.State); Assert.Empty(result.Value!);
    }

    [Theory]
    [InlineData("missing-root")]
    [InlineData("duplicate")]
    [InlineData("too-many")]
    [InlineData("bad-name")]
    [InlineData("negative-root")]
    public async Task MissingRootPlaceDuplicateIdOrMoreThan25ItemsIsInvalid(string shape)
    {
        string body = shape switch
        {
            "missing-root" => Page("{\"id\":1,\"name\":\"A\"}"),
            "duplicate" => Page(Item(1, 11, "A"), Item(1, 12, "B")),
            "too-many" => Page(Enumerable.Range(1, 26).Select(i => Item(i, 100 + i, "G" + i)).ToArray()),
            "bad-name" => Page(Item(1, 11, "")),
            _ => Page(Item(1, -5, "A"))
        };
        var result = await Request(body);
        Assert.Equal(PublicDirectoryState.Failure, result.State); Assert.Null(result.Value);
    }

    [Fact]
    public async Task NonPositiveUserIdIsRejectedWithoutARequest()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No request expected."));
        using var http = new HttpClient(handler); var directory = new RobloxPublicDirectory(http);
        foreach (long id in new[] { 0L, -1L })
            Assert.Equal(PublicDirectoryState.InvalidInput, (await directory.GetFavoriteGamesAsync(id, default)).State);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RestrictedOrMissingProfileIsUnknownNotAFailure()
    {
        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound })
        {
            using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") }));
            using var http = new HttpClient(handler);
            var result = await new RobloxPublicDirectory(http).GetFavoriteGamesAsync(156, default);
            Assert.Equal(PublicDirectoryState.Unknown, result.State); Assert.Null(result.Value);
        }
    }

    private static string Item(long universe, long root, string name) => JsonSerializer.Serialize(new
    { id = universe, name, rootPlace = new { id = root, type = "Place" }, creator = new { id = 4111519, type = "Group", name = "Roblox Presents" }, placeVisits = 10 });
    private static string Page(params string[] items) => Page(items, null);
    private static string Page(string[] items, string? next)
        => "{\"previousPageCursor\":null,\"nextPageCursor\":" + JsonSerializer.Serialize(next) + ",\"data\":[" + string.Join(",", items) + "]}";
    private static async Task<PublicDirectoryResult<IReadOnlyList<PublicRobloxFavoriteGame>>> Request(string json)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(json))); using var http = new HttpClient(handler);
        return await new RobloxPublicDirectory(http).GetFavoriteGamesAsync(156, default);
    }
    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static void Anonymous(HttpRequestMessage request)
    {
        Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Content);
        Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
        Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        private int _calls; public int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Interlocked.Increment(ref _calls); return handle(request, ct); }
    }
}
