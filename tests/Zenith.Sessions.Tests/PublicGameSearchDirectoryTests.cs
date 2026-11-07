using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

// All requests terminate at an owned synthetic handler. The captured public fixture is embedded;
// these tests never contact Roblox, use credentials or launch anything.
public sealed class PublicGameSearchDirectoryTests
{
    private static readonly Guid Session = Guid.Parse("45e684cd-3008-4377-b439-f682d0cd8fee");
    private static PublicRobloxGameSearchQuery Query(string text = "fisch", string? token = null) => new(text, Session, token);

    [Fact]
    public async Task ObservedFirstPartyFixtureKeepsAllFortyResultsWithoutInventingUnknownCreators()
    {
        using var fixture = typeof(PublicGameSearchDirectoryTests).Assembly.GetManifestResourceStream("PublicGameSearch-Fisch.sanitized.json")
            ?? throw new InvalidOperationException("The bounded public search fixture is missing.");
        using var reader = new StreamReader(fixture);
        string json = await reader.ReadToEndAsync();
        using var handler = new Handler((request, _) => { Anonymous(request); return Task.FromResult(Json(json)); });
        using var http = new HttpClient(handler); var clock = new Clock();
        var directory = new RobloxPublicDirectory(http, clock);
        var result = await directory.SearchGamesAsync(Query(), default);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        var page = Assert.IsType<PublicRobloxGameSearchPage>(result.Value);
        Assert.Equal(Query(), page.Query); Assert.Equal(clock.GetUtcNow(), page.RetrievedUtc);
        Assert.Equal(40, page.Items.Count); Assert.False(page.ResultsTruncated); Assert.Equal(56, page.NextPageToken!.Length);
        Assert.Equal(5750914919, page.Items[0].UniverseId); Assert.Equal(16732694052, page.Items[0].RootPlaceId);
        Assert.Equal("Fisching", page.Items[0].CreatorName); Assert.Equal(7381705, page.Items[0].CreatorId);
        Assert.Equal(39, page.Items.Count(item => item.CreatorId is null && item.CreatorName is null));
        Assert.Equal(1, handler.Calls); Assert.Equal(0, directory.CachedImageCount);
        Assert.Contains("explicitly", result.Message);
    }

    [Fact]
    public async Task FixedAnonymousEndpointEscapesExactUnicodeQueryAndOpaqueTokenAsSingleValues()
    {
        var query = Query("  Café & 🐟 + Friends  ", "next+/=").Validated();
        using var handler = new Handler((request, _) =>
        {
            Anonymous(request); var uri = request.RequestUri!;
            Assert.Equal("https", uri.Scheme); Assert.Equal("apis.roblox.com", uri.Host); Assert.Equal("/search-api/omni-search", uri.AbsolutePath);
            var fields = uri.Query.TrimStart('?').Split('&').Select(field => field.Split('=', 2))
                .ToDictionary(field => field[0], field => Uri.UnescapeDataString(field[1]));
            Assert.Equal(4, fields.Count); Assert.Equal(query.Text, fields["searchQuery"]);
            Assert.Equal("all", fields["pageType"]); Assert.Equal(Session.ToString("D"), fields["sessionId"]);
            Assert.Equal(query.PageToken, fields["pageToken"]);
            return Task.FromResult(Json(Page([Row(1)])));
        });
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).SearchGamesAsync(query, default);
        Assert.Equal(PublicDirectoryState.Success, result.State); Assert.Equal(query, result.Value!.Query); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task NonGameCategoriesAreIgnoredAndIdenticalGamesDeduplicateInOriginalOrder()
    {
        string json = "{\"searchResults\":[{\"contentGroupType\":\"Avatar\",\"contents\":[{\"unsupported\":true}]},"
            + "{\"contentGroupType\":\"Game\",\"contents\":[" + Row(2) + "," + Row(1) + "," + Row(2) + "]}]}";
        var result = await Search(json);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        Assert.Equal(new long[] { 2, 1 }, result.Value!.Items.Select(item => item.UniverseId));
        Assert.All(result.Value.Items, item => { Assert.Null(item.CreatorId); Assert.Null(item.CreatorName); Assert.Null(item.Playing); Assert.Null(item.Sponsored); });
    }

    [Fact]
    public async Task MissingNullAndZeroOptionalMetadataRemainHonestUnknownOrMeasuredZero()
    {
        var row = JsonNode.Parse(Row(long.MaxValue))!.AsObject();
        row["creatorId"] = 0; row["creatorName"] = ""; row["playerCount"] = 0; row["isSponsored"] = false;
        var result = await Search(Page([row.ToJsonString()]));
        Assert.Equal(PublicDirectoryState.Success, result.State); var item = Assert.Single(result.Value!.Items);
        Assert.Equal(long.MaxValue, item.UniverseId); Assert.Null(item.CreatorId); Assert.Null(item.CreatorName);
        Assert.Equal(0, item.Playing); Assert.False(item.Sponsored);
        row["creatorId"] = null; row["creatorName"] = null; row["playerCount"] = null; row["isSponsored"] = null;
        var unknown = await Search(Page([row.ToJsonString()]));
        Assert.Null(Assert.Single(unknown.Value!.Items).Playing); Assert.Null(unknown.Value.Items[0].Sponsored);
    }

    [Theory]
    [InlineData(50, false)]
    [InlineData(51, true)]
    [InlineData(1024, true)]
    public async Task LargeValidResponseRetainsFiftyWithoutLosingTheServerCursorOrClaimingCoverage(int count, bool truncated)
    {
        var result = await Search(Page(Enumerable.Range(1, count).Select(id => Row(id)), "next-token"));
        Assert.Equal(PublicDirectoryState.Success, result.State);
        Assert.Equal(50, result.Value!.Items.Count); Assert.Equal(truncated, result.Value.ResultsTruncated);
        Assert.Equal("next-token", result.Value.NextPageToken);
        if (truncated) Assert.Contains("omitted", result.Message);
    }

    [Fact]
    public async Task ScanBudgetDoesNotPermitAnUnboundedGameArrayEvenWhenJsonFitsTheByteLimit()
    {
        string json = Page(Enumerable.Range(1, 1025).Select(id => Row(id)));
        Assert.True(Encoding.UTF8.GetByteCount(json) < RobloxPublicDirectory.MaximumJsonBytes);
        var result = await Search(json); Assert.Equal(PublicDirectoryState.Failure, result.State); Assert.Null(result.Value);
    }

    [Fact]
    public async Task VerifiedBadgeAndVotesAreReadWhenPresent()
    {
        var row = JsonNode.Parse(Row(1))!.AsObject();
        row["creatorHasVerifiedBadge"] = true; row["totalUpVotes"] = 920; row["totalDownVotes"] = 80;
        var result = await Search(Page([row.ToJsonString()]));
        Assert.Equal(PublicDirectoryState.Success, result.State);
        var item = Assert.Single(result.Value!.Items);
        Assert.True(item.CreatorVerified); Assert.Equal(920, item.UpVotes); Assert.Equal(80, item.DownVotes);
    }

    [Fact]
    public async Task MissingBadgeAndVotesStayUnknown()
    {
        var result = await Search(Page([Row(1)]));
        var item = Assert.Single(result.Value!.Items);
        Assert.Null(item.CreatorVerified); Assert.Null(item.UpVotes); Assert.Null(item.DownVotes);
    }

    [Theory]
    [InlineData("totalUpVotes", "-1")]
    [InlineData("totalDownVotes", "-5")]
    [InlineData("totalUpVotes", "\"9\"")]
    [InlineData("creatorHasVerifiedBadge", "\"true\"")]
    [InlineData("creatorHasVerifiedBadge", "1")]
    public async Task NegativeVotesOrNonBooleanBadgeIsAnInvalidResponse(string field, string value)
    {
        string prefix = Row(1);
        var result = await Search(Page([prefix[..^1] + "," + JsonSerializer.Serialize(field) + ":" + value + "}"]));
        Assert.Equal(PublicDirectoryState.Failure, result.State); Assert.Null(result.Value);
    }

    [Theory]
    [InlineData("universeId", "0")]
    [InlineData("rootPlaceId", "-1")]
    [InlineData("universeId", "9223372036854775808")]
    [InlineData("rootPlaceId", "\"42\"")]
    [InlineData("universeId", "1.1")]
    [InlineData("creatorId", "-1")]
    [InlineData("playerCount", "-1")]
    [InlineData("playerCount", "\"42\"")]
    [InlineData("playerCount", "9223372036854775808")]
    [InlineData("isSponsored", "\"true\"")]
    [InlineData("name", "\"\"")]
    [InlineData("creatorName", "\" \"")]
    [InlineData("name", "\"bad\\u202econtent\"")]
    [InlineData("name", "\"bad\\uD800\"")]
    public async Task MalformedGameMetadataFailsWithFixedFeedback(string field, string value)
    {
        var row = JsonNode.Parse(Row(1))!.AsObject(); row.Remove(field);
        string prefix = row.ToJsonString();
        // Preserve malformed escaped UTF-16 literally until the real bounded response parser reads it.
        string raw = prefix[..^1] + "," + JsonSerializer.Serialize(field) + ":" + value + "}";
        var result = await Search(Page([raw]));
        Assert.Equal(PublicDirectoryState.Failure, result.State); Assert.Null(result.Value); Assert.DoesNotContain("bad", result.Message);
    }

    [Theory]
    [InlineData("universe-root")]
    [InlineData("root-universe")]
    [InlineData("name")]
    [InlineData("after-retained-limit")]
    public async Task ContradictoryDuplicatesAnywhereInTheScannedPageRejectTheWholePublication(string kind)
    {
        var rows = new List<string> { Row(1) }; var duplicate = JsonNode.Parse(Row(1))!.AsObject();
        if (kind == "universe-root") duplicate["rootPlaceId"] = 99999;
        else if (kind == "root-universe") duplicate["universeId"] = 99999;
        else duplicate["name"] = "Contradiction";
        if (kind == "after-retained-limit") rows.AddRange(Enumerable.Range(2, 50).Select(id => Row(id)));
        rows.Add(duplicate.ToJsonString());
        var result = await Search(Page(rows)); Assert.Equal(PublicDirectoryState.Failure, result.State); Assert.Null(result.Value);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"searchResults\":{}}")]
    [InlineData("{\"searchResults\":[null]}")]
    [InlineData("{\"searchResults\":[{\"contentGroupType\":\"Game\",\"contents\":{}}]}")]
    [InlineData("{\"searchResults\":[],\"searchResults\":[]}")]
    [InlineData("{\"searchResults\":[],\"ignored\":{\"x\":1,\"x\":2}}")]
    [InlineData("{\"searchResults\":[],\"nextPageToken\":42}")]
    [InlineData("untrusted-search-body-marker")]
    public async Task ChangedOrMalformedResponseShapeCannotBecomeSuccessfulEmptySearch(string json)
    {
        var result = await Search(json); Assert.Equal(PublicDirectoryState.Failure, result.State);
        Assert.Null(result.Value); Assert.DoesNotContain("untrusted-search-body-marker", result.Message);
    }

    [Fact]
    public async Task GenuineEmptyPageIsSuccessfulAndReturnedRepeatedOrOversizedCursorsAreRejected()
    {
        var empty = await Search(Page([])); Assert.Equal(PublicDirectoryState.Success, empty.State); Assert.Empty(empty.Value!.Items);
        var repeated = await Search(Page([], "previous"), Query(token: "previous"));
        Assert.Equal(PublicDirectoryState.Failure, repeated.State); Assert.Null(repeated.Value);
        var oversized = await Search(Page([], new string('x', 2049)));
        Assert.Equal(PublicDirectoryState.Failure, oversized.State); Assert.Null(oversized.Value);
    }

    [Theory]
    [InlineData(401, PublicDirectoryState.Unknown)]
    [InlineData(403, PublicDirectoryState.Unknown)]
    [InlineData(404, PublicDirectoryState.Unknown)]
    [InlineData(302, PublicDirectoryState.Failure)]
    [InlineData(503, PublicDirectoryState.Failure)]
    public async Task HttpFailuresNeverRetryEnrichOrPublishResponseText(int code, PublicDirectoryState state)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code)
        { Content = new StringContent("synthetic-secret-response-marker") }));
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).SearchGamesAsync(Query(), default);
        Assert.Equal(state, result.State); Assert.Null(result.Value); Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("synthetic-secret-response-marker", result.Message);
    }

    [Fact]
    public async Task RateLimitCooldownIsSharedWithExistingPublicPlaceMappingAndDoesNotRetry()
    {
        var clock = new Clock();
        using var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(10)); return Task.FromResult(response);
        });
        using var http = new HttpClient(handler); var directory = new RobloxPublicDirectory(http, clock);
        var first = await directory.SearchGamesAsync(Query(), default);
        Assert.Equal(PublicDirectoryState.Deferred, first.State); Assert.Equal(TimeSpan.FromSeconds(10), first.RetryAfter);
        Assert.Equal(PublicDirectoryState.Deferred, (await directory.LookupGameAsync(123, default)).State);
        Assert.Equal(PublicDirectoryState.Deferred, (await directory.SearchGamesAsync(Query("Doors"), default)).State);
        Assert.Equal(1, handler.Calls);
        clock.Advance(TimeSpan.FromSeconds(10)); await directory.SearchGamesAsync(Query(), default); Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task SearchUsesTheSameTwoRequestSlotsAsPublicProfilesAndDoesNotCreateACache()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int users = 0;
        using var handler = new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "users.roblox.com")
            {
                if (Interlocked.Increment(ref users) == 2) entered.SetResult();
                await release.Task.WaitAsync(ct);
                long id = long.Parse(request.RequestUri.Segments[^1], System.Globalization.CultureInfo.InvariantCulture);
                return Json($"{{\"id\":{id},\"name\":\"User{id}\",\"displayName\":\"User\"}}");
            }
            return Json(Page([Row(1)]));
        });
        using var http = new HttpClient(handler); var directory = new RobloxPublicDirectory(http);
        var one = directory.LookupUserAsync(1, default); var two = directory.LookupUserAsync(2, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var busy = await directory.SearchGamesAsync(Query(), default);
            Assert.Equal(PublicDirectoryState.Deferred, busy.State); Assert.Equal(2, handler.Calls);
        }
        finally { release.TrySetResult(); await Task.WhenAll(one, two); }
        Assert.Equal(PublicDirectoryState.Success, (await directory.SearchGamesAsync(Query(), default)).State);
        Assert.Equal(PublicDirectoryState.Success, (await directory.SearchGamesAsync(Query(), default)).State);
        Assert.Equal(4, handler.Calls); Assert.Equal(0, directory.CachedImageCount);
    }

    [Fact]
    public async Task CancellationPropagatesAndReleasesTheSharedSlotAfterTheActualRequestEnds()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, requests = 0;
        using var handler = new Handler(async (_, ct) =>
        {
            int sequence = Interlocked.Increment(ref requests), current = Interlocked.Increment(ref active);
            try
            {
                if (sequence == 1) { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                else { if (current == 2) both.TrySetResult(); await release.Task.WaitAsync(ct); }
                return Json(Page([]));
            }
            finally { Interlocked.Decrement(ref active); }
        });
        using var http = new HttpClient(handler); var directory = new RobloxPublicDirectory(http);
        using var stop = new CancellationTokenSource(); var work = directory.SearchGamesAsync(Query(), stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work); Assert.Equal(0, active);
        }
        finally { stop.Cancel(); try { await work; } catch (OperationCanceledException) { } }
        var one = directory.SearchGamesAsync(Query(), default); var two = directory.SearchGamesAsync(Query("Doors"), default);
        try { await both.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(2, active); }
        finally { release.TrySetResult(); await Task.WhenAll(one, two); }
        Assert.Equal(PublicDirectoryState.Success, (await one).State); Assert.Equal(PublicDirectoryState.Success, (await two).State);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => directory.SearchGamesAsync(Query(), cancelled.Token));
        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EncodedByteLimitIsEnforcedForKnownAndStreamingLengths(bool unknownLength)
    {
        byte[] bytes = new byte[RobloxPublicDirectory.MaximumJsonBytes + 1];
        using var handler = new Handler((_, _) =>
        {
            HttpContent content = unknownLength ? new StreamContent(new NonSeekable(bytes)) : new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).SearchGamesAsync(Query(), default);
        Assert.Equal(PublicDirectoryState.Failure, result.State); Assert.Null(result.Value); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RejectedInputAndInheritedCredentialsCannotReachSearchHttp()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No request expected."));
        using var http = new HttpClient(handler); var directory = new RobloxPublicDirectory(http);
        foreach (string value in new[] { "123", "www.roblox.com/share?code=synthetic", ".ROBLOSECURITY=synthetic", "private server code = synthetic", new string('x', 101) })
            Assert.Equal(PublicDirectoryState.InvalidInput, (await directory.SearchGamesAsync(Query(value), default)).State);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-only");
        Assert.Equal(PublicDirectoryState.Failure, (await directory.SearchGamesAsync(Query(), default)).State);
        Assert.Equal(0, handler.Calls);
    }

    private static string Row(long id) => JsonSerializer.Serialize(new { universeId = id, rootPlaceId = id == long.MaxValue ? id - 1 : id + 10000, name = "G" });
    private static string Page(IEnumerable<string> rows, string? next = null) => "{\"searchResults\":[{\"contentGroupType\":\"Game\",\"contents\":["
        + string.Join(",", rows) + "]}],\"nextPageToken\":" + JsonSerializer.Serialize(next) + "}";
    private static async Task<PublicDirectoryResult<PublicRobloxGameSearchPage>> Search(string json, PublicRobloxGameSearchQuery? query = null)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(json))); using var http = new HttpClient(handler);
        return await new RobloxPublicDirectory(http, new Clock()).SearchGamesAsync(query ?? Query(), default);
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
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 6, 20, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
    private sealed class NonSeekable(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, false);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
