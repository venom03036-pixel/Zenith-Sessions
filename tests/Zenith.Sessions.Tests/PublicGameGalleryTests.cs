using System.Buffers.Binary;
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

/// <summary>Injected HTTP only. Observed fixtures have public text/URLs removed; all other bodies/pixels are synthetic.</summary>
public sealed class PublicGameGalleryTests
{
    private const long Place = 9007199254740993, Universe = 9007199254740995, ImageId = long.MaxValue;
    private const string ImageUrl = "https://tr.rbxcdn.com/synthetic-gallery/576/324/Image/Png/noFilter";

    [Fact]
    public async Task ObservedEmptyMediaFixtureStaysEmptyWithoutThumbnailOrIconSubstitution()
    {
        using var handler = new Handler((request, _) =>
        {
            Anonymous(request, "application/json"); Assert.Equal("https://games.roblox.com/v2/games/2466591867/media", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json(Fixture("PublicGalleryEmpty.sanitized.json")));
        });
        using var http = new HttpClient(handler);
        IPublicRobloxGameGallery directory = new RobloxPublicDirectory(http);
        var result = await directory.GetGameGalleryAsync(6564161046, 2466591867, default);
        Assert.Equal(PublicDirectoryState.Success, result.State); Assert.Empty(result.Value!.Items);
        Assert.Equal(6564161046, result.Value.RequestedPlaceId); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ObservedSanitizedMediaSchemaKeepsItsSourceOrderWhenThumbnailResponseIsReversed()
    {
        var thumbs = JsonNode.Parse(Fixture("PublicGalleryThumbnails.sanitized.json"))!;
        var original = thumbs["data"]!.AsArray();
        thumbs["data"] = new JsonArray(original.Reverse().Select(item => item!.DeepClone()).ToArray());
        long[] expected = [104827490467627, 97386823072852, 123219959637729, 106328032578640, 102749649628626];
        using var handler = new Handler((request, _) =>
        {
            Anonymous(request, "application/json");
            if (request.RequestUri!.Host == "games.roblox.com") return Task.FromResult(Json(Fixture("PublicGalleryMedia.sanitized.json")));
            Assert.Equal("https://thumbnails.roblox.com/v1/games/6504986360/thumbnails?thumbnailIds=" + string.Join(",", expected)
                + "&size=576x324&format=Png&isCircular=false", request.RequestUri.AbsoluteUri);
            return Task.FromResult(Json(thumbs.ToJsonString()));
        });
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).GetGameGalleryAsync(85896571713843, 6504986360, default);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        var gallery = result.Value!; Assert.Equal(expected, gallery.Items.Select(item => item.ThumbnailId!.Value));
        Assert.Equal(Enumerable.Range(0, 5), gallery.Items.Select(item => item.Position));
        Assert.All(gallery.Items, item =>
        {
            Assert.Equal(85896571713843, item.RequestedPlaceId); Assert.Equal(6504986360, item.UniverseId);
            Assert.Equal(PublicRobloxGameGalleryState.Ready, item.State); Assert.Equal(576, item.Width); Assert.Equal(324, item.Height);
            Assert.Contains("sanitized-gallery-" + (item.Position + 1), item.ImageUri!.AbsoluteUri);
        });
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Int64ContextAndThumbnailIdentityNeverLosePrecisionOrReplaceTheRequestedSubplace()
    {
        using var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "games.roblox.com")
            { Assert.Equal($"https://games.roblox.com/v2/games/{Universe}/media", request.RequestUri.AbsoluteUri); return Task.FromResult(Json(Media(ImageId))); }
            Assert.Contains("thumbnailIds=" + long.MaxValue, request.RequestUri.AbsoluteUri);
            return Task.FromResult(Json(Thumbnails(ImageId)));
        });
        using var http = new HttpClient(handler); var result = await new RobloxPublicDirectory(http).GetGameGalleryAsync(Place, Universe, default);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal(Place, item.RequestedPlaceId); Assert.Equal(Universe, item.UniverseId); Assert.Equal(long.MaxValue, item.ThumbnailId);
    }

    [Fact]
    public async Task UnapprovedImagesAndUnsupportedVideoRemainExplicitAndNeverFetchExternalContent()
    {
        string media = JsonSerializer.Serialize(new { data = new object[] {
            new { assetTypeId=1,assetType="Image",imageId=(long?)11,approved=false,videoHash="private-marker",videoTitle="private-marker" },
            new { assetTypeId=62,assetType="Video",imageId=(long?)null,approved=true,videoHash="https://external.invalid/private-marker",videoTitle="private-marker" },
            new { assetTypeId=33,assetType="YouTubeVideo",imageId=(long?)null,approved=true },
            new { assetTypeId=999,assetType="Unknown",imageId=(long?)null,approved=true },
            new { assetTypeId=1,assetType="Image",imageId=(long?)12,approved=true }
        }});
        using var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "games.roblox.com") return Task.FromResult(Json(media));
            Assert.Contains("thumbnailIds=12&", request.RequestUri.AbsoluteUri);
            return Task.FromResult(Json(Thumbnails(12)));
        });
        using var http = new HttpClient(handler); var directory = new RobloxPublicDirectory(http);
        var result = await directory.GetGameGalleryAsync(Place, Universe, default); var items = result.Value!.Items;
        Assert.Equal(5, items.Count); Assert.Equal(PublicRobloxGameGalleryState.Blocked, items[0].State);
        Assert.Equal(PublicRobloxGameGalleryKind.Video, items[1].Kind); Assert.Equal(PublicRobloxGameGalleryKind.Video, items[2].Kind);
        Assert.Equal(PublicRobloxGameGalleryKind.Unsupported, items[3].Kind);
        Assert.All(items.Take(4), item => Assert.Null(item.ImageUri));
        Assert.All(items.Skip(1).Take(3), item => Assert.Equal(PublicRobloxGameGalleryState.Unsupported, item.State));
        Assert.Equal(PublicRobloxGameGalleryState.Ready, items[4].State);
        Assert.DoesNotContain("private-marker", JsonSerializer.Serialize(result));
        Assert.Equal(PublicDirectoryState.InvalidInput, (await directory.FetchGameGalleryImageAsync(items[1], default)).State);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task FilteredMissingThumbnailIdsStayUnavailableWithoutShiftingAnotherImageIntoTheirPosition()
    {
        using var handler = new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.Host == "games.roblox.com"
            ? Media(11, 12, 13) : Thumbnails(12))));
        using var http = new HttpClient(handler);
        var items = (await new RobloxPublicDirectory(http).GetGameGalleryAsync(Place, Universe, default)).Value!.Items;
        Assert.Equal(new long?[] { 11, 12, 13 }, items.Select(item => item.ThumbnailId));
        Assert.Equal(new[] { PublicRobloxGameGalleryState.Unavailable, PublicRobloxGameGalleryState.Ready, PublicRobloxGameGalleryState.Unavailable }, items.Select(item => item.State));
    }

    [Theory]
    [InlineData("Pending", PublicRobloxGameGalleryState.Pending)]
    [InlineData("InReview", PublicRobloxGameGalleryState.Pending)]
    [InlineData("Blocked", PublicRobloxGameGalleryState.Blocked)]
    [InlineData("Error", PublicRobloxGameGalleryState.Unavailable)]
    [InlineData("FutureState", PublicRobloxGameGalleryState.Unavailable)]
    public async Task NoncompletedImageStatesDoNotCarryOrRequestTheReturnedAddress(string state, PublicRobloxGameGalleryState expected)
    {
        using var handler = new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.Host == "games.roblox.com"
            ? Media(11) : Thumbnails(11, state, "https://external.invalid/private-marker"))));
        using var http = new HttpClient(handler); var directory = new RobloxPublicDirectory(http);
        var item = Assert.Single((await directory.GetGameGalleryAsync(Place, Universe, default)).Value!.Items);
        Assert.Equal(expected, item.State); Assert.Null(item.ImageUri);
        Assert.Equal(PublicDirectoryState.InvalidInput, (await directory.FetchGameGalleryImageAsync(item, default)).State);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{\"data\":[null]}")]
    [InlineData("{\"data\":[],\"data\":[]}")]
    [InlineData("{\"data\":[],\"ignored\":{\"x\":1,\"x\":2}}")]
    [InlineData("{\"data\":[{\"assetTypeId\":1,\"assetType\":\"Image\",\"approved\":true}]}")]
    [InlineData("{\"data\":[{\"assetTypeId\":1,\"assetType\":\"Image\",\"imageId\":0,\"approved\":true}]}")]
    [InlineData("{\"data\":[{\"assetTypeId\":1,\"assetType\":\"Image\",\"imageId\":\"11\",\"approved\":true}]}")]
    [InlineData("{\"data\":[{\"assetTypeId\":1,\"assetType\":\"Image\",\"imageId\":9223372036854775808,\"approved\":true}]}")]
    [InlineData("{\"data\":[{\"assetTypeId\":1,\"assetType\":\"Video\",\"imageId\":11,\"approved\":true}]}")]
    [InlineData("{\"data\":[{\"assetTypeId\":1,\"assetType\":\"Image\",\"imageId\":11,\"approved\":\"true\"}]}")]
    [InlineData("{\"data\":[{\"assetTypeId\":1,\"assetType\":\"\\uD800\",\"imageId\":11,\"approved\":true}]}")]
    [InlineData("untrusted-gallery-marker")]
    public async Task MalformedMediaFailsWithoutRawErrorsAndReleasesItsRequestSlot(string payload)
    {
        bool malformed = true;
        using var handler = new Handler((_, _) => Task.FromResult(Json(malformed ? payload : "{\"data\":[]}")));
        using var http = new HttpClient(handler); var directory = new RobloxPublicDirectory(http);
        var failed = await directory.GetGameGalleryAsync(Place, Universe, default);
        Assert.Equal(PublicDirectoryState.Failure, failed.State); Assert.Null(failed.Value); Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("untrusted-gallery-marker", failed.Message);
        malformed = false; Assert.Equal(PublicDirectoryState.Success, (await directory.GetGameGalleryAsync(Place, Universe, default)).State);
    }

    [Fact]
    public async Task DuplicateAndOversizedMediaInventoriesFailBeforeAnyThumbnailRequest()
    {
        foreach (string body in new[] { Media(11, 11), Media(Enumerable.Range(1, 11).Select(value => (long)value).ToArray()) })
        {
            using var handler = new Handler((_, _) => Task.FromResult(Json(body))); using var http = new HttpClient(handler);
            Assert.Equal(PublicDirectoryState.Failure, (await new RobloxPublicDirectory(http).GetGameGalleryAsync(Place, Universe, default)).State);
            Assert.Equal(1, handler.Calls);
        }
    }

    [Theory]
    [InlineData("{\"data\":[{\"targetId\":12,\"state\":\"Completed\",\"imageUrl\":\"https://tr.rbxcdn.com/a.png\"}]}")]
    [InlineData("{\"data\":[{\"targetId\":11,\"state\":\"Pending\"},{\"targetId\":11,\"state\":\"Pending\"}]}")]
    [InlineData("{\"data\":[{\"targetId\":\"11\",\"state\":\"Pending\"}]}")]
    [InlineData("{\"data\":[{\"targetId\":11,\"state\":null}]}")]
    [InlineData("{\"data\":[{\"targetId\":11,\"state\":\"Completed\"}]}")]
    [InlineData("{\"data\":[{\"targetId\":11,\"targetId\":11,\"state\":\"Pending\"}]}")]
    public async Task ForeignDuplicateOrMalformedThumbnailResultsCannotReplaceSourceMedia(string thumbnails)
    {
        using var handler = new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.Host == "games.roblox.com" ? Media(11) : thumbnails)));
        using var http = new HttpClient(handler); var result = await new RobloxPublicDirectory(http).GetGameGalleryAsync(Place, Universe, default);
        Assert.Equal(PublicDirectoryState.Failure, result.State); Assert.Null(result.Value); Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("http://tr.rbxcdn.com/image.png")]
    [InlineData("https://external.invalid/image.png")]
    [InlineData("https://tr.rbxcdn.com.external.invalid/image.png")]
    [InlineData("https://user@tr.rbxcdn.com/image.png")]
    [InlineData("https://tr.rbxcdn.com/image.png?accessCode=private-marker")]
    [InlineData("https://tr.rbxcdn.com/image.png#private-marker")]
    [InlineData("https://tr.rbxcdn.com:444/image.png")]
    [InlineData("https://tr.rbxcdn.com/image%2f.png")]
    public async Task CompletedThumbnailWithHostileAddressFailsBeforeImageDownload(string url)
    {
        using var handler = new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.Host == "games.roblox.com" ? Media(11) : Thumbnails(11, "Completed", url))));
        using var http = new HttpClient(handler); var result = await new RobloxPublicDirectory(http).GetGameGalleryAsync(Place, Universe, default);
        Assert.Equal(PublicDirectoryState.Failure, result.State); Assert.DoesNotContain("private-marker", result.Message); Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task InvalidContextAndUnavailableDescriptorsNeverStartNetworkWork()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No request expected.")); using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http);
        Assert.Equal(PublicDirectoryState.InvalidInput, (await directory.GetGameGalleryAsync(0, Universe, default)).State);
        Assert.Equal(PublicDirectoryState.InvalidInput, (await directory.GetGameGalleryAsync(Place, -1, default)).State);
        foreach (var item in new[] { Descriptor() with { Width = 150 }, Descriptor() with { Height = 150 }, Descriptor() with { ThumbnailId = null },
            Descriptor() with { RequestedPlaceId = 0 }, Descriptor() with { State = PublicRobloxGameGalleryState.Blocked, ImageUri = null },
            Descriptor() with { ImageUri = new Uri("https://external.invalid/image.png") } })
            Assert.Equal(PublicDirectoryState.InvalidInput, (await directory.FetchGameGalleryImageAsync(item, default)).State);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SharedPngCacheSeparatesGalleryAssetUniverseAndExistingImageKinds()
    {
        // All descriptors deliberately share one URL. The ordinal stays inside the fake handler.
        int calls = 0;
        using var counted = new Handler((request, ct) =>
        {
            Anonymous(request, "image/png"); int ordinal = Interlocked.Increment(ref calls);
            return Task.FromResult(Response(PngHeader(ordinal is 2 or 3 ? 150 : 576, ordinal is 2 or 3 ? 150 : 324), "image/Png"));
        });
        using var http = new HttpClient(counted); var directory = new RobloxPublicDirectory(http);
        var gallery = await directory.FetchGameGalleryImageAsync(Descriptor(), default); Assert.Equal(PublicDirectoryState.Success, gallery.State);
        Assert.Equal(PublicDirectoryState.Success, (await directory.FetchGameIconAsync(new(Universe, new Uri(ImageUrl), 150, 150, DateTimeOffset.UtcNow), default)).State);
        Assert.Equal(PublicDirectoryState.Success, (await directory.FetchAvatarAsync(new(Universe, new Uri(ImageUrl), 150, 150, DateTimeOffset.UtcNow), default)).State);
        Assert.Equal(PublicDirectoryState.Success, (await directory.FetchGameGalleryImageAsync(Descriptor(), default)).State); Assert.Equal(3, counted.Calls);
        Assert.Equal(PublicDirectoryState.Success, (await directory.FetchGameGalleryImageAsync(Descriptor() with { ThumbnailId = ImageId - 1 }, default)).State);
        Assert.Equal(PublicDirectoryState.Success, (await directory.FetchGameGalleryImageAsync(Descriptor() with { UniverseId = Universe - 1 }, default)).State);
        Assert.Equal(5, counted.Calls); Assert.Equal(3, directory.CachedGameGalleryImageCount); Assert.Equal(1, directory.CachedAvatarCount); Assert.Equal(1, directory.CachedGameIconCount);
        Assert.Equal(5 * 33, directory.CachedEncodedBytes);
    }

    [Fact]
    public async Task GallerySharesEncodedByteBudgetAndExpiryWithExistingPublicImages()
    {
        var clock = new Clock();
        using var handler = new Handler((_, _) => Task.FromResult(Response(PngHeader(), "image/png"))); using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http, clock, maximumEncodedMemoryBytes: 66);
        for (int index = 0; index < 3; index++)
            Assert.Equal(PublicDirectoryState.Success, (await directory.FetchGameGalleryImageAsync(Descriptor(clock.GetUtcNow()) with { ThumbnailId = ImageId - index }, default)).State);
        Assert.Equal(2, directory.CachedGameGalleryImageCount); Assert.Equal(66, directory.CachedEncodedBytes);
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(PublicDirectoryState.Success, (await directory.FetchGameGalleryImageAsync(Descriptor(clock.GetUtcNow()), default)).State);
        Assert.Equal(1, directory.CachedGameGalleryImageCount); Assert.Equal(33, directory.CachedEncodedBytes); Assert.Equal(4, handler.Calls);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task BothJsonStagesKeepTheExistingByteBound(bool thumbnails, bool unknownLength)
    {
        using var handler = new Handler((request, _) =>
        {
            if (thumbnails && request.RequestUri!.Host == "games.roblox.com") return Task.FromResult(Json(Media(11)));
            return Task.FromResult(Response(new byte[RobloxPublicDirectory.MaximumJsonBytes + 1], "application/json", unknownLength));
        });
        using var http = new HttpClient(handler); var result = await new RobloxPublicDirectory(http).GetGameGalleryAsync(Place, Universe, default);
        Assert.Equal(PublicDirectoryState.Failure, result.State); Assert.Null(result.Value); Assert.Equal(thumbnails ? 2 : 1, handler.Calls);
    }

    [Fact]
    public async Task DuplicateThumbnailIdentityWithinTheAllowedCountStillFails()
    {
        const string duplicate = "{\"data\":[{\"targetId\":11,\"state\":\"Pending\"},{\"targetId\":11,\"state\":\"Pending\"}]}";
        using var handler = new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.Host == "games.roblox.com" ? Media(11, 12) : duplicate)));
        using var http = new HttpClient(handler); var result = await new RobloxPublicDirectory(http).GetGameGalleryAsync(Place, Universe, default);
        Assert.Equal(PublicDirectoryState.Failure, result.State); Assert.Null(result.Value);
    }

    [Theory]
    [InlineData(150, 150)] [InlineData(768, 432)] [InlineData(576, 323)] [InlineData(577, 324)]
    public async Task GalleryTransportRejectsOtherDimensionsAndDoesNotRelaxAvatarOrIconPolicy(int width, int height)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(PngHeader(width, height), "image/png"))); using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http);
        Assert.Equal(PublicDirectoryState.Failure, (await directory.FetchGameGalleryImageAsync(Descriptor(), default)).State); Assert.Equal(0, directory.CachedImageCount);
        using var otherHandler = new Handler((_, _) => Task.FromResult(Response(PngHeader(), "image/png"))); using var otherHttp = new HttpClient(otherHandler);
        var other = new RobloxPublicDirectory(otherHttp);
        Assert.Equal(PublicDirectoryState.Failure, (await other.FetchAvatarAsync(new(11, new Uri(ImageUrl), 150, 150, DateTimeOffset.UtcNow), default)).State);
        Assert.Equal(PublicDirectoryState.Failure, (await other.FetchGameIconAsync(new(11, new Uri(ImageUrl), 150, 150, DateTimeOffset.UtcNow), default)).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GalleryImageByteLimitAppliesWithOrWithoutDeclaredLength(bool unknownLength)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(new byte[RobloxPublicDirectory.MaximumAvatarBytes + 1], "image/png", unknownLength)));
        using var http = new HttpClient(handler); var directory = new RobloxPublicDirectory(http);
        Assert.Equal(PublicDirectoryState.Failure, (await directory.FetchGameGalleryImageAsync(Descriptor(), default)).State); Assert.Equal(0, directory.CachedImageCount);
    }

    [Fact]
    public async Task ClearDuringActualGalleryDownloadFencesCachePublicationAndDoesNotAbandonTheRead()
    {
        var entered = Gate(); var released = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); bool first = true;
        using var handler = new Handler((_, _) => { if (!first) return Task.FromResult(Response(PngHeader(), "image/png")); first = false; entered.TrySetResult(); return released.Task; });
        using var http = new HttpClient(handler); var directory = new RobloxPublicDirectory(http);
        Task<PublicDirectoryResult<EncodedPublicGameGalleryImage>> active = directory.FetchGameGalleryImageAsync(Descriptor(), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); directory.ClearMemoryCache(); Assert.False(active.IsCompleted);
        released.SetResult(Response(PngHeader(), "image/png")); Assert.Equal(PublicDirectoryState.Success, (await active).State);
        Assert.Equal(0, directory.CachedImageCount);
        Assert.Equal(PublicDirectoryState.Success, (await directory.FetchGameGalleryImageAsync(Descriptor(), default)).State); Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancellationAtEitherJsonStageIsOwnedAndNeverStartsTheFollowingStage(bool thumbnail)
    {
        var entered = Gate(); using var stop = new CancellationTokenSource();
        using var handler = new Handler(async (request, ct) =>
        {
            if (thumbnail && request.RequestUri!.Host == "games.roblox.com") return Json(Media(11));
            entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Json("{\"data\":[]}");
        });
        using var http = new HttpClient(handler); var directory = new RobloxPublicDirectory(http);
        var active = directory.GetGameGalleryAsync(Place, Universe, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        Assert.Equal(thumbnail ? 2 : 1, handler.Calls);
    }

    [Theory]
    [InlineData("media")] [InlineData("thumbnail")] [InlineData("image")]
    public async Task RateLimitsPauseTheSharedHostWithoutAutomaticRetries(string stage)
    {
        var clock = new Clock();
        using var handler = new Handler((request, _) =>
        {
            if (stage != "media" && request.RequestUri!.Host == "games.roblox.com") return Task.FromResult(Json(Media(11)));
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("private-response-marker") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)); return Task.FromResult(response);
        });
        using var http = new HttpClient(handler); var directory = new RobloxPublicDirectory(http, clock);
        var descriptor = Descriptor(clock.GetUtcNow());
        var first = stage == "image" ? (await directory.FetchGameGalleryImageAsync(descriptor, default)).State : (await directory.GetGameGalleryAsync(Place, Universe, default)).State;
        Assert.Equal(PublicDirectoryState.Deferred, first); int calls = handler.Calls;
        var second = stage == "image" ? (await directory.FetchGameGalleryImageAsync(descriptor, default)).State : (await directory.GetGameGalleryAsync(Place, Universe, default)).State;
        Assert.Equal(PublicDirectoryState.Deferred, second); Assert.Equal(calls + (stage == "thumbnail" ? 1 : 0), handler.Calls);
    }

    [Fact]
    public async Task GalleryRequestsShareTheExistingTwoSlotsAndRejectInheritedCredentials()
    {
        using var stop = new CancellationTokenSource(); var entered = Gate(); int active = 0;
        using var handler = new Handler(async (_, ct) => { if (Interlocked.Increment(ref active) == 2) entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Json("{}"); });
        using var http = new HttpClient(handler); var directory = new RobloxPublicDirectory(http);
        var first = directory.GetGameGalleryAsync(Place, Universe, stop.Token); var second = directory.GetGameIconAsync(Universe, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PublicDirectoryState.Deferred, (await directory.LookupUserAsync(11, default)).State); Assert.Equal(2, handler.Calls);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        http.DefaultRequestHeaders.Add("Cookie", "synthetic-private-marker");
        var rejected = await directory.GetGameGalleryAsync(Place, Universe, default); Assert.Equal(PublicDirectoryState.Failure, rejected.State);
        Assert.DoesNotContain("synthetic-private-marker", rejected.Message); Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public void GalleryValidationOwnsItsCollectionAndRejectsContextOrderAndDuplicateIdentity()
    {
        var items = new[] { Descriptor(), Descriptor() with { Position = 1, ThumbnailId = ImageId - 1 } };
        var owned = new PublicRobloxGameGallery(Place, Universe, items, DateTimeOffset.UtcNow).Validated(); items[0] = items[0] with { RequestedPlaceId = 1 };
        Assert.Equal(Place, owned.Items[0].RequestedPlaceId);
        Assert.Throws<NotSupportedException>(() => ((IList<PublicRobloxGameGalleryImage>)owned.Items).Clear());
        Assert.Throws<ArgumentException>(() => new PublicRobloxGameGallery(Place, Universe, items, DateTimeOffset.UtcNow).Validated());
        Assert.Throws<ArgumentException>(() => (owned with { Items = new[] { Descriptor() with { Position = 1 } } }).Validated());
        Assert.Throws<ArgumentException>(() => (owned with { Items = new[] { Descriptor(), Descriptor() with { Position = 1 } } }).Validated());
    }

    private static PublicRobloxGameGalleryImage Descriptor(DateTimeOffset? now = null) => new(Place, Universe, 0, ImageId,
        PublicRobloxGameGalleryKind.Image, PublicRobloxGameGalleryState.Ready, new Uri(ImageUrl), 576, 324, now ?? DateTimeOffset.UtcNow);
    private static string Media(params long[] ids) => JsonSerializer.Serialize(new { data = ids.Select(id => new { assetTypeId = 1, assetType = "Image", imageId = id, approved = true, videoHash = (string?)null, videoTitle = (string?)null, altText = (string?)null }) });
    private static string Thumbnails(long id, string state = "Completed", string url = ImageUrl) => JsonSerializer.Serialize(new { data = new[] { new { targetId = id, state, imageUrl = url, version = "synthetic" } } });
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Fixture(string name)
    { using var stream = typeof(PublicGameGalleryTests).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("The sanitized public gallery fixture is not embedded."); using var reader = new StreamReader(stream); return reader.ReadToEnd(); }
    private static HttpResponseMessage Json(string json) => Response(Encoding.UTF8.GetBytes(json), "application/json");
    private static HttpResponseMessage Response(byte[] bytes, string contentType, bool unknownLength = false)
    { HttpContent content = unknownLength ? new StreamContent(new NonSeekable(bytes)) : new ByteArrayContent(bytes); content.Headers.ContentType = new MediaTypeHeaderValue(contentType); return new(HttpStatusCode.OK) { Content = content }; }
    private static void Anonymous(HttpRequestMessage request, string accept)
    { Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("https", request.RequestUri!.Scheme); Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie")); Assert.Single(request.Headers); Assert.Equal(accept, request.Headers.Accept.Single().MediaType); }
    // Header-only synthetic transport bytes; complete PNG validation belongs to the separate App codec tests.
    private static byte[] PngHeader(int width = 576, int height = 324)
    { byte[] bytes = new byte[33]; new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0); BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13); "IHDR"u8.CopyTo(bytes.AsSpan(12)); BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), (uint)width); BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), (uint)height); return bytes; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> run) : HttpMessageHandler
    { private int _calls; public int Calls => Volatile.Read(ref _calls); protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Interlocked.Increment(ref _calls); return run(request, ct); } }
    private sealed class Clock : TimeProvider
    { private DateTimeOffset _now = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => _now; public void Advance(TimeSpan value) => _now += value; }
    private sealed class NonSeekable(byte[] bytes) : Stream
    {
        private readonly MemoryStream _stream = new(bytes, false);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _stream.ReadAsync(buffer, ct);
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _stream.Dispose(); base.Dispose(disposing); }
    }
}
