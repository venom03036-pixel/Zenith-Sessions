using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

// Every request terminates at an owned synthetic handler. No Roblox contact, credentials or launches.
public sealed class PublicGameArtDirectoryTests
{
    private const string ImageUrl = "https://tr.rbxcdn.com/180DAY-a2846a75df26a929cc2f526aecdfaef9/384/216/Image/Png/noFilter";

    [Fact]
    public async Task BatchUsesTheExactAnonymousEndpointOnce()
    {
        using var handler = new Handler((request, _) =>
        {
            Anonymous(request);
            Assert.Equal("https://thumbnails.roblox.com/v1/games/multiget/thumbnails?universeIds=11,22&countPerUniverse=1&defaults=true&size=384x216&format=Png&isCircular=false",
                request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json(Batch(Item(11, "Completed", ImageUrl), Item(22, "Completed", ImageUrl))));
        });
        using var http = new HttpClient(handler); var clock = new Clock();
        var directory = new RobloxPublicDirectory(http, clock);
        var result = await directory.GetGameThumbnailsAsync([11, 22], default);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        Assert.Equal(new long[] { 11, 22 }, result.Value!.Select(t => t.UniverseId).ToArray());
        Assert.All(result.Value!, t => { Assert.Equal(clock.GetUtcNow(), t.RetrievedUtc); Assert.Equal(PublicRobloxThumbnailState.Ready, t.State); });
        Assert.Equal(1, handler.Calls); Assert.Equal(0, directory.CachedImageCount);
    }

    [Fact]
    public async Task CompletedPendingAndBlockedMapToStates()
    {
        string body = Batch(Item(1, "Completed", ImageUrl), Item(2, "Pending"), Item(3, "Blocked"), Item(4, "Error"),
            "{\"universeId\":5,\"error\":{\"code\":1,\"message\":\"synthetic\"},\"thumbnails\":[]}");
        var result = await Request([1, 2, 3, 4, 5, 6], body);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        var items = result.Value!; Assert.Equal(6, items.Count);
        Assert.Equal(PublicRobloxThumbnailState.Ready, items[0].State); Assert.Equal(new Uri(ImageUrl), items[0].ImageUri);
        Assert.Equal(PublicRobloxThumbnailState.Pending, items[1].State); Assert.Null(items[1].ImageUri);
        Assert.Equal(PublicRobloxThumbnailState.Blocked, items[2].State); Assert.Null(items[2].ImageUri);
        Assert.Equal(PublicRobloxThumbnailState.Unavailable, items[3].State); Assert.Null(items[3].ImageUri);
        Assert.Equal(PublicRobloxThumbnailState.Unavailable, items[4].State); // Per-universe error object.
        Assert.Equal(PublicRobloxThumbnailState.Unavailable, items[5].State); // Missing from the response.
        Assert.Equal(6, items[5].UniverseId);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    public async Task UnknownOrDuplicateUniverseInResponseIsInvalid(string shape)
    {
        string body = shape == "unknown"
            ? Batch(Item(1, "Completed", ImageUrl), Item(9, "Completed", ImageUrl))
            : Batch(Item(1, "Completed", ImageUrl), Item(1, "Completed", ImageUrl));
        var result = await Request([1, 2], body);
        Assert.Equal(PublicDirectoryState.Failure, result.State); Assert.Null(result.Value);
    }

    [Fact]
    public async Task NonRobloxImageHostIsInvalid()
    {
        var result = await Request([1], Batch(Item(1, "Completed", "https://example.com/x.png")));
        Assert.Equal(PublicDirectoryState.Failure, result.State); Assert.Null(result.Value);
    }

    [Fact]
    public async Task EmptyTooManyDuplicateOrNonPositiveIdsAreRejectedWithoutARequest()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No request expected."));
        using var http = new HttpClient(handler); var directory = new RobloxPublicDirectory(http);
        foreach (long[] ids in new[] { Array.Empty<long>(), Enumerable.Range(1, 13).Select(i => (long)i).ToArray(), new long[] { 5, 5 }, new long[] { 0 }, new long[] { -1 } })
            Assert.Equal(PublicDirectoryState.InvalidInput, (await directory.GetGameThumbnailsAsync(ids, default)).State);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task FetchUsesTheSharedBoundedImageTransport()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("image/png", Assert.Single(request.Headers.Accept).MediaType);
            return Task.FromResult(Png(PngHeader(384, 216)));
        });
        using var http = new HttpClient(handler); var clock = new Clock();
        var directory = new RobloxPublicDirectory(http, clock);
        var thumbnail = new PublicRobloxGameThumbnail(11, new Uri(ImageUrl), PublicRobloxThumbnailState.Ready, clock.GetUtcNow());
        var first = await directory.FetchGameThumbnailAsync(thumbnail, default);
        Assert.Equal(PublicDirectoryState.Success, first.State); Assert.Equal("image/png", first.Value!.MediaType); Assert.Equal(33, first.Value.Bytes.Length);
        Assert.Equal(PublicDirectoryState.Success, (await directory.FetchGameThumbnailAsync(thumbnail, default)).State);
        Assert.Equal(1, handler.Calls); Assert.Equal(1, directory.CachedImageCount); // Second fetch is served from the shared cache.
        Assert.Equal(PublicDirectoryState.InvalidInput,
            (await directory.FetchGameThumbnailAsync(thumbnail with { State = PublicRobloxThumbnailState.Pending, ImageUri = null }, default)).State);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task WrongPictureSizeIsAnInvalidImage()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Png(PngHeader(150, 150))));
        using var http = new HttpClient(handler); var clock = new Clock();
        var thumbnail = new PublicRobloxGameThumbnail(11, new Uri(ImageUrl), PublicRobloxThumbnailState.Ready, clock.GetUtcNow());
        var result = await new RobloxPublicDirectory(http, clock).FetchGameThumbnailAsync(thumbnail, default);
        Assert.Equal(PublicDirectoryState.Failure, result.State); Assert.Null(result.Value); Assert.Equal(1, handler.Calls);
    }

    private static string Item(long universe, string state, string? imageUrl = null) => "{\"universeId\":" + universe + ",\"error\":null,\"thumbnails\":[{\"targetId\":"
        + (universe * 100) + ",\"state\":\"" + state + "\"" + (imageUrl is null ? "" : ",\"imageUrl\":" + JsonSerializer.Serialize(imageUrl)) + "}]}";
    private static string Batch(params string[] items) => "{\"data\":[" + string.Join(",", items) + "]}";
    private static async Task<PublicDirectoryResult<IReadOnlyList<PublicRobloxGameThumbnail>>> Request(long[] ids, string json)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(json))); using var http = new HttpClient(handler);
        return await new RobloxPublicDirectory(http, new Clock()).GetGameThumbnailsAsync(ids, default);
    }
    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Png(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes); content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
    // Only a header: full validation stays in the shared App decoder.
    private static byte[] PngHeader(int width, int height)
    {
        byte[] bytes = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), (uint)height);
        return bytes;
    }
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
    private sealed class Clock : TimeProvider
    {
        private readonly DateTimeOffset _now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
