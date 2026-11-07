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
public sealed class PublicAvatarBatchDirectoryTests
{
    private static string Url(long id) => "https://tr.rbxcdn.com/30DAY-AvatarHeadshot-SYNTHETIC" + id + "-Png/150/150/AvatarHeadshot/Png/noFilter";

    [Fact]
    public async Task OneAnonymousRequestCoversEveryUserAndMatchesRepliesByUserId()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Content);
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
            Assert.Equal("https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds=11,22,33&size=150x150&format=Png&isCircular=false",
                request.RequestUri!.AbsoluteUri);
            // Roblox does not promise reply order: answer in reverse.
            return Task.FromResult(Json(Batch(Item(33, "Completed", Url(33)), Item(22, "Completed", Url(22)), Item(11, "Completed", Url(11)))));
        });
        using var http = new HttpClient(handler); var clock = new Clock();
        IPublicRobloxAvatarBatchDirectory directory = new RobloxPublicDirectory(http, clock);
        var result = await directory.GetAvatarsAsync([11, 22, 33], default);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(new long[] { 11, 22, 33 }, result.Value!.Select(item => item.UserId).ToArray());
        Assert.All(result.Value!, item =>
        {
            Assert.Equal(PublicRobloxThumbnailState.Ready, item.State);
            var avatar = Assert.IsType<PublicRobloxAvatar>(item.Avatar);
            Assert.Equal(item.UserId, avatar.UserId);
            Assert.Equal(new Uri(Url(item.UserId)), avatar.ImageUri);
            Assert.Equal(150, avatar.Width); Assert.Equal(150, avatar.Height);
            Assert.Equal(clock.GetUtcNow(), avatar.RetrievedUtc);
        });
    }

    [Fact]
    public async Task PendingBlockedAndMissingAreReportedPerUser()
    {
        string body = Batch(Item(1, "Completed", Url(1)), Item(2, "Pending"), Item(3, "InReview"), Item(4, "Blocked"),
            Item(5, "Error"), Item(6, "NewUnknownState"));
        var result = await Request([1, 2, 3, 4, 5, 6, 7], body);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        var items = result.Value!; Assert.Equal(7, items.Count);
        Assert.Equal(PublicRobloxThumbnailState.Ready, items[0].State); Assert.NotNull(items[0].Avatar);
        Assert.Equal(PublicRobloxThumbnailState.Pending, items[1].State);
        Assert.Equal(PublicRobloxThumbnailState.Pending, items[2].State);
        Assert.Equal(PublicRobloxThumbnailState.Blocked, items[3].State);
        Assert.Equal(PublicRobloxThumbnailState.Unavailable, items[4].State);
        Assert.Equal(PublicRobloxThumbnailState.Unavailable, items[5].State);
        Assert.Equal(PublicRobloxThumbnailState.Unavailable, items[6].State); // Missing from the reply.
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7 }, items.Select(item => item.UserId).ToArray());
        Assert.All(items.Skip(1), item => Assert.Null(item.Avatar)); // Only a ready picture carries something to download.
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("too-many")]
    [InlineData("foreign-host")]
    [InlineData("missing-url")]
    public async Task UnknownDuplicateOrUnsafeRepliesAreInvalid(string shape)
    {
        string body = shape switch
        {
            "unknown" => Batch(Item(1, "Completed", Url(1)), Item(9, "Completed", Url(9))),
            "duplicate" => Batch(Item(1, "Completed", Url(1)), Item(1, "Pending")),
            "too-many" => Batch(Item(1, "Pending"), Item(2, "Pending"), Item(1, "Pending")),
            "foreign-host" => Batch(Item(1, "Completed", "https://evil.invalid/avatar.png?do-not-send")),
            _ => Batch(Item(1, "Completed"))
        };
        var result = await Request([1, 2], body);
        Assert.Equal(PublicDirectoryState.Failure, result.State); Assert.Null(result.Value);
        Assert.DoesNotContain("do-not-send", result.Message);
    }

    [Fact]
    public async Task EmptyOversizedDuplicateOrNonPositiveIdsAreRejectedWithoutARequest()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No request expected."));
        using var http = new HttpClient(handler); var directory = new RobloxPublicDirectory(http);
        long[] tooMany = Enumerable.Range(1, PublicRobloxAvatarBatchLimits.MaximumBatch + 1).Select(i => (long)i).ToArray();
        foreach (long[]? ids in new[] { null, Array.Empty<long>(), tooMany, new long[] { 5, 5 }, new long[] { 0 }, new long[] { 3, -1 } })
            Assert.Equal(PublicDirectoryState.InvalidInput, (await directory.GetAvatarsAsync(ids!, default)).State);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(50, PublicRobloxAvatarBatchLimits.MaximumBatch);
    }

    [Fact]
    public async Task FullBatchIsOneRequest()
    {
        long[] ids = Enumerable.Range(1, PublicRobloxAvatarBatchLimits.MaximumBatch).Select(i => 1_000_000_000L + i).ToArray();
        using var handler = new Handler((request, _) =>
        {
            Assert.Contains("userIds=" + string.Join(",", ids) + "&", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json(Batch(ids.Select(id => Item(id, "Completed", Url(id))).ToArray())));
        });
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http, new Clock()).GetAvatarsAsync(ids, default);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        Assert.Equal(ids.Length, result.Value!.Count(item => item.State == PublicRobloxThumbnailState.Ready));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ReadyDescriptorDownloadsThroughTheSharedImageTransport()
    {
        using var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "thumbnails.roblox.com"
            ? Json(Batch(Item(42, "Completed", Url(42)))) : Png(PngHeader())));
        using var http = new HttpClient(handler); var clock = new Clock();
        var directory = new RobloxPublicDirectory(http, clock);
        var batch = await directory.GetAvatarsAsync([42], default);
        var avatar = Assert.Single(batch.Value!).Avatar!;
        var picture = await directory.FetchAvatarAsync(avatar, default);
        Assert.Equal(PublicDirectoryState.Success, picture.State); Assert.Equal(33, picture.Value!.Bytes.Length);
        Assert.Equal(2, handler.Calls); Assert.Equal(1, directory.CachedAvatarCount);
    }

    [Fact]
    public async Task RateLimitPauseIsSharedWithSingleHeadshotRequests()
    {
        using var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("") };
            response.Headers.TryAddWithoutValidation("Retry-After", "60");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http, new Clock());
        var limited = await directory.GetAvatarsAsync([1, 2], default);
        Assert.Equal(PublicDirectoryState.Deferred, limited.State); Assert.Equal(TimeSpan.FromSeconds(60), limited.RetryAfter);
        Assert.Equal(PublicDirectoryState.Deferred, (await directory.GetAvatarAsync(3, default)).State);
        Assert.Equal(PublicDirectoryState.Deferred, (await directory.GetAvatarsAsync([4], default)).State);
        Assert.Equal(1, handler.Calls);
    }

    private static string Item(long userId, string state, string? imageUrl = null) => "{\"targetId\":" + userId + ",\"state\":\"" + state + "\""
        + (imageUrl is null ? ",\"imageUrl\":null" : ",\"imageUrl\":" + JsonSerializer.Serialize(imageUrl)) + ",\"version\":\"TN3\"}";
    private static string Batch(params string[] items) => "{\"data\":[" + string.Join(",", items) + "]}";
    private static async Task<PublicDirectoryResult<IReadOnlyList<PublicRobloxAvatarBatchItem>>> Request(long[] ids, string json)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(json))); using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http, new Clock()).GetAvatarsAsync(ids, default);
        Assert.Equal(1, handler.Calls);
        return result;
    }
    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Png(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes); content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
    // Only a header: full validation stays in the shared App decoder.
    private static byte[] PngHeader()
    {
        byte[] bytes = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), 150);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), 150);
        return bytes;
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
