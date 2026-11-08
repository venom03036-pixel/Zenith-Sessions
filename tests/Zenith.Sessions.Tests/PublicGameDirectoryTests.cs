using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class PublicGameDirectoryTests
{
    private const long Place = 9007199254740993;
    private const long Universe = 9007199254740995;
    private const long RootPlace = 9007199254740997;
    private const string ImageUrl = "https://tr.rbxcdn.com/synthetic-game.png";

    [Theory]
    [InlineData("User", PublicRobloxCreatorKind.User)]
    [InlineData("Group", PublicRobloxCreatorKind.Group)]
    public async Task AnonymousGameLookupPreservesInt64RequestedSubplaceAndPublicEvidence(string creatorType, PublicRobloxCreatorKind expected)
    {
        using var handler = new FakeHandler((request, _) =>
        {
            AssertAnonymous(request, "application/json");
            string uri = request.RequestUri!.AbsoluteUri;
            if (uri == $"https://apis.roblox.com/universes/v1/places/{Place}/universe")
                return Task.FromResult(Json($"{{\"universeId\":{Universe}}}"));
            Assert.Equal($"https://games.roblox.com/v1/games?universeIds={Universe}", uri);
            return Task.FromResult(Json(GameJson().Replace("\"Group\"", JsonSerializer.Serialize(creatorType))));
        });
        using var http = new HttpClient(handler);
        IPublicRobloxGameDirectory directory = new RobloxPublicDirectory(http);
        var result = await directory.LookupGameAsync(Place, default);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        var game = Assert.IsType<PublicRobloxGame>(result.Value);
        Assert.Equal(Place, game.RequestedPlaceId);
        Assert.Equal(Universe, game.UniverseId);
        Assert.Equal(RootPlace, game.RootPlaceId);
        Assert.NotEqual(game.RequestedPlaceId, game.RootPlaceId);
        Assert.Equal("Synthetic game!", game.Name);
        Assert.Equal(long.MaxValue, game.CreatorId);
        Assert.Equal("Synthetic Creator", game.CreatorName);
        Assert.Equal(expected, game.CreatorKind);
        Assert.Equal(long.MaxValue, game.Playing);
        Assert.Equal(25, game.MaxPlayers);
        Assert.Equal("Public game information", game.EvidenceLabel);
        Assert.Contains("does not establish playability", result.Message);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public async Task InvalidIdsNeverSendAnything(long id)
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("No request expected."));
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http);
        Assert.Equal(PublicDirectoryState.InvalidInput, (await directory.LookupGameAsync(id, default)).State);
        Assert.Equal(PublicDirectoryState.InvalidInput, (await directory.GetGameIconAsync(id, default)).State);
        Assert.Equal(PublicDirectoryState.InvalidInput, (await directory.FetchGameIconAsync(Icon() with { UniverseId = id }, default)).State);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"universeId\":0}")]
    [InlineData("{\"universeId\":-1}")]
    [InlineData("{\"universeId\":9223372036854775808}")]
    [InlineData("{\"universeId\":\"42\"}")]
    [InlineData("{\"universeId\":1.1}")]
    [InlineData("{\"universeId\":42,\"universeId\":42}")]
    [InlineData("{\"universeId\":42,\"ignored\":{\"x\":1,\"x\":2}}")]
    [InlineData("{\"universeId\":42,\"placeId\":1}")]
    [InlineData("{\"universeId\":42,\"\\uD800\":1}")]
    [InlineData("{\"universeId\":42} {}")]
    [InlineData("untrusted-game-response-marker")]
    // Only a body whose single field is a null universeId means "no such game"; any other null shape stays malformed.
    [InlineData("{\"universeId\":null,\"universeId\":null}")]
    [InlineData("{\"universeId\":null,\"placeId\":9007199254740993}")]
    [InlineData("{\"universeId\":null,\"extra\":1}")]
    [InlineData("{\"placeId\":null}")]
    [InlineData("{\"universeid\":null}")]
    [InlineData("{\"universeId\":false}")]
    [InlineData("null")]
    public async Task MalformedMappingFailsBeforeGameRequest(string json)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Json(json)));
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).LookupGameAsync(Place, default);
        Assert.Equal(PublicDirectoryState.Failure, result.State);
        Assert.Null(result.Value);
        Assert.DoesNotContain("untrusted-game-response-marker", result.Message);
        Assert.Equal(1, handler.Calls);
    }

    // Roblox answers HTTP 200 with {"universeId": null} for a place ID that no game uses (captured for both a
    // nonexistent and an invalid ID). That is a definite "no such game", not a broken response.
    [Theory]
    [InlineData("{\"universeId\":null}")]
    [InlineData("{\n  \"universeId\": null\n}")]
    public async Task PlaceIdThatNoGameUsesIsUnknownWithoutGameRequest(string json)
    {
        using var handler = new FakeHandler((request, _) =>
        {
            AssertAnonymous(request, "application/json");
            Assert.Equal($"https://apis.roblox.com/universes/v1/places/{Place}/universe", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json(json));
        });
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).LookupGameAsync(Place, default);
        Assert.Equal(PublicDirectoryState.Unknown, result.State);
        Assert.Null(result.Value);
        Assert.Null(result.RetryAfter);
        Assert.Equal("No Roblox game uses this place ID. Check the number and try again.", result.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("game-name", "\\uD800")]
    [InlineData("creator-name", "\\uDC00")]
    [InlineData("creator-kind", "\\uD800")]
    [InlineData("details-property", "\\uD800")]
    [InlineData("icon-url", "\\uD800")]
    [InlineData("icon-state", "\\uDC00")]
    [InlineData("icon-property", "\\uD800")]
    public async Task MalformedUnicodeReturnsTypedFailureWithoutLeakingTextAndReleasesRequestSlot(string field, string escape)
    {
        string malformed = field switch
        {
            "game-name" => GameJson().Replace("Synthetic game!", "untrusted-unicode-marker" + escape),
            "creator-name" => GameJson().Replace("Synthetic Creator", "untrusted-unicode-marker" + escape),
            "creator-kind" => GameJson().Replace("\"Group\"", "\"untrusted-unicode-marker" + escape + "\""),
            "details-property" => GameJson().Replace("\"isContentRestricted\":false", "\"isContentRestricted\":false,\"" + escape + "\":1"),
            "icon-url" => IconJson().Replace(ImageUrl, "untrusted-unicode-marker" + escape),
            "icon-state" => IconJson().Replace("Completed", "untrusted-unicode-marker" + escape),
            _ => IconJson().Replace("\"targetId\"", "\"" + escape + "\":1,\"targetId\"")
        };
        bool icon = field.StartsWith("icon-", StringComparison.Ordinal);
        bool returnMalformed = true;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FakeHandler(async (request, ct) =>
        {
            if (IsMapping(request)) return Json(MappingJson());
            if (returnMalformed) return Json(malformed);
            await release.Task.WaitAsync(ct);
            return Json(IconJson());
        });
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http);
        var result = await AtStage(directory, icon ? "icon" : "details");
        Assert.Equal(PublicDirectoryState.Failure, result.State);
        Assert.DoesNotContain("untrusted-unicode-marker", result.Message);
        Assert.DoesNotContain("D800", result.Message);
        Assert.DoesNotContain("DC00", result.Message);
        Assert.Equal(icon ? 1 : 2, handler.Calls);
        returnMalformed = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = directory.GetGameIconAsync(Universe, deadline.Token);
        var second = directory.GetGameIconAsync(Universe, deadline.Token);
        Assert.Equal((icon ? 1 : 2) + 2, handler.Calls);
        release.SetResult();
        Assert.All(await Task.WhenAll(first, second), item => Assert.Equal(PublicDirectoryState.Success, item.State));
    }

    [Fact]
    public async Task ValidSurrogatePairInGameNameRemainsValidUnicode()
    {
        string json = GameJson().Replace("Synthetic game!", "Synthetic \\uD83D\\uDE80");
        using var handler = new FakeHandler((request, _) => Task.FromResult(Json(IsMapping(request) ? MappingJson() : json)));
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).LookupGameAsync(Place, default);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        Assert.Equal("Synthetic 🚀", result.Value!.Name);
    }

    public static IEnumerable<object[]> InvalidDetails()
    {
        yield return new object[] { "{}" };
        yield return new object[] { "{\"data\":{}}" };
        yield return new object[] { "{\"data\":[{},{}]}" };
        yield return new object[] { "{\"data\":[null]}" };
        string valid = GameJson();
        foreach ((string from, string to) in new[]
        {
            ($"\"id\":{Universe}", "\"id\":42"),
            ($"\"id\":{Universe}", "\"id\":0"),
            ($"\"id\":{Universe}", "\"id\":9223372036854775808"),
            ($"\"id\":{Universe}", $"\"id\":{Universe},\"id\":{Universe}"),
            ($"\"rootPlaceId\":{RootPlace}", "\"rootPlaceId\":0"),
            ($"\"rootPlaceId\":{RootPlace}", "\"rootPlaceId\":\"42\""),
            ("Synthetic game!", "bad\\nname"),
            ("Synthetic game!", "bad\\u202ename"),
            ("Synthetic game!", new string('x', 257)),
            ("\"id\":9223372036854775807", "\"id\":-1"),
            ("\"Group\"", "\"Other\""),
            ("Synthetic Creator", new string('x', 101)),
            ("\"playing\":9223372036854775807", "\"playing\":-1"),
            ("\"playing\":9223372036854775807", "\"playing\":9223372036854775808"),
            ("\"playing\":9223372036854775807", "\"playing\":\"0\""),
            ("\"playing\":9223372036854775807", "\"playing\":0.5"),
            ("\"maxPlayers\":25", "\"maxPlayers\":0"),
            ("\"maxPlayers\":25", "\"maxPlayers\":2147483648"),
            ("\"maxPlayers\":25", "\"maxPlayers\":\"25\""),
            ("\"isContentRestricted\":false", "\"isContentRestricted\":\"false\""),
            ("\"isContentRestricted\":false", "\"isContentRestricted\":false,\"ignored\":{\"x\":1,\"x\":2}")
        }) yield return new object[] { valid.Replace(from, to, StringComparison.Ordinal) };
    }

    [Theory]
    [MemberData(nameof(InvalidDetails))]
    public async Task UntrustedDetailsRejectMismatchDuplicateFieldsAndUnsafeValues(string json)
    {
        using var handler = new FakeHandler((request, _) => Task.FromResult(Json(IsMapping(request) ? MappingJson() : json)));
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).LookupGameAsync(Place, default);
        Assert.Equal(PublicDirectoryState.Failure, result.State);
        Assert.Null(result.Value);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[{\"id\":0,\"isContentRestricted\":true}]}")]
    public async Task MissingOrExplicitlyRestrictedGameReturnsUnknownWithoutPlaceholderData(string json)
    {
        using var handler = new FakeHandler((request, _) => Task.FromResult(Json(IsMapping(request) ? MappingJson() : json)));
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).LookupGameAsync(Place, default);
        Assert.Equal(PublicDirectoryState.Unknown, result.State);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task ZeroPlayingAndOmittedRestrictionFlagRemainValidPublicData()
    {
        string json = GameJson().Replace("\"playing\":9223372036854775807", "\"playing\":0")
            .Replace(",\"isContentRestricted\":false", "");
        using var handler = new FakeHandler((request, _) => Task.FromResult(Json(IsMapping(request) ? MappingJson() : json)));
        using var http = new HttpClient(handler);
        Assert.Equal(0, (await new RobloxPublicDirectory(http).LookupGameAsync(Place, default)).Value!.Playing);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GameDetailsCarryVisitsAndPrivateServerFlagWhenListed(bool allowed)
    {
        string json = GameJson().Replace("\"isContentRestricted\":false",
            "\"isContentRestricted\":false,\"visits\":9223372036854775807,\"createVipServersAllowed\":" + (allowed ? "true" : "false"));
        using var handler = new FakeHandler((request, _) => Task.FromResult(Json(IsMapping(request) ? MappingJson() : json)));
        using var http = new HttpClient(handler);
        var game = (await new RobloxPublicDirectory(http).LookupGameAsync(Place, default)).Value!;
        Assert.Equal(long.MaxValue, game.Visits);
        Assert.Equal(allowed, game.PrivateServersAllowed);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task UnlistedVisitsAndPrivateServerFlagStayUnknownInsteadOfZero()
    {
        string zero = GameJson().Replace("\"isContentRestricted\":false", "\"isContentRestricted\":false,\"visits\":0");
        foreach (string json in new[] { GameJson(), zero })
        {
            using var handler = new FakeHandler((request, _) => Task.FromResult(Json(IsMapping(request) ? MappingJson() : json)));
            using var http = new HttpClient(handler);
            var game = (await new RobloxPublicDirectory(http).LookupGameAsync(Place, default)).Value!;
            Assert.Equal(json == zero ? 0L : (long?)null, game.Visits);
            Assert.Null(game.PrivateServersAllowed);
        }
    }

    [Theory]
    [InlineData("\"visits\":-1")]
    [InlineData("\"visits\":9223372036854775808")]
    [InlineData("\"visits\":\"5\"")]
    [InlineData("\"visits\":1.5")]
    [InlineData("\"visits\":null")]
    [InlineData("\"createVipServersAllowed\":\"true\"")]
    [InlineData("\"createVipServersAllowed\":1")]
    [InlineData("\"createVipServersAllowed\":null")]
    public async Task MalformedVisitsOrPrivateServerFlagIsUnknownInsteadOfAnInvalidListing(string field)
    {
        // Display-only extras: a malformed one is left unknown, the well-formed other one and the listing survive.
        bool visitsMalformed = field.StartsWith("\"visits\"", StringComparison.Ordinal);
        string other = visitsMalformed ? "\"createVipServersAllowed\":true" : "\"visits\":7";
        string json = GameJson().Replace("\"isContentRestricted\":false", "\"isContentRestricted\":false," + field + "," + other);
        using var handler = new FakeHandler((request, _) => Task.FromResult(Json(IsMapping(request) ? MappingJson() : json)));
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).LookupGameAsync(Place, default);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        var game = Assert.IsType<PublicRobloxGame>(result.Value);
        Assert.Equal("Synthetic game!", game.Name);
        Assert.Equal(visitsMalformed ? null : (long?)7, game.Visits);
        Assert.Equal(visitsMalformed ? (bool?)true : null, game.PrivateServersAllowed);
    }

    [Fact]
    public async Task BatchNamesSurviveMalformedVisitsAndPrivateServerFlagOnOneUniverse()
    {
        string odd = Game(11, 110, "Eleven").Replace("\"isContentRestricted\":false",
            "\"isContentRestricted\":false,\"visits\":null,\"createVipServersAllowed\":\"yes\"");
        using var handler = new FakeHandler((_, _) => Task.FromResult(Json("{\"data\":[" + odd + "," + Game(22, 220, "Twenty-two") + "]}")));
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).LookupGamesAsync([11, 22], default);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        var games = result.Value!;
        Assert.Equal(new[] { "Eleven", "Twenty-two" }, games.Select(game => game.Name));
        Assert.Null(games[0].Visits); Assert.Null(games[0].PrivateServersAllowed);
    }

    [Fact]
    public async Task BatchLookupCarriesVisitsAndPrivateServerFlagPerUniverse()
    {
        string listed = Game(11, 110, "Eleven").Replace("\"isContentRestricted\":false", "\"isContentRestricted\":false,\"visits\":42,\"createVipServersAllowed\":true");
        using var handler = new FakeHandler((_, _) => Task.FromResult(Json("{\"data\":[" + listed + "," + Game(22, 220, "Twenty-two") + "]}")));
        using var http = new HttpClient(handler);
        var games = (await new RobloxPublicDirectory(http).LookupGamesAsync([11, 22], default)).Value!;
        Assert.Equal(42, games[0].Visits); Assert.True(games[0].PrivateServersAllowed);
        Assert.Null(games[1].Visits); Assert.Null(games[1].PrivateServersAllowed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MappingAndDetailsHaveBoundedResponsesEvenWithoutContentLength(bool details, bool unknownLength)
    {
        byte[] oversized = Encoding.UTF8.GetBytes(new string('x', RobloxPublicDirectory.MaximumJsonBytes + 1));
        using var handler = new FakeHandler((request, _) => Task.FromResult(details && IsMapping(request)
            ? Json(MappingJson()) : Response(oversized, "application/json", unknownLength)));
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).LookupGameAsync(Place, default);
        Assert.Equal(PublicDirectoryState.Failure, result.State);
        Assert.Null(result.Value);
        Assert.Equal(details ? 2 : 1, handler.Calls);
    }

    [Fact]
    public async Task IconUsesDocumentedUniverseQueryAndCorrelatesTargetId()
    {
        using var handler = new FakeHandler((request, _) =>
        {
            AssertAnonymous(request, "application/json");
            Assert.Equal($"https://thumbnails.roblox.com/v1/games/icons?universeIds={Universe}&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false",
                request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json(IconJson()));
        });
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).GetGameIconAsync(Universe, default);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        Assert.Equal(Universe, result.Value!.UniverseId);
        Assert.Equal(150, result.Value.Width);
        Assert.Equal(150, result.Value.Height);
        Assert.Equal(ImageUrl, result.Value.ImageUri.AbsoluteUri);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("Pending", PublicDirectoryState.Deferred)]
    [InlineData("InReview", PublicDirectoryState.Deferred)]
    [InlineData("Blocked", PublicDirectoryState.Unknown)]
    [InlineData("Error", PublicDirectoryState.Unknown)]
    [InlineData("Other", PublicDirectoryState.Unknown)]
    public async Task IconPreparationAndUnavailableStatesDoNotDownloadAnything(string state, PublicDirectoryState expected)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Json(IconJson(state))));
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).GetGameIconAsync(Universe, default);
        Assert.Equal(expected, result.State);
        Assert.Null(result.Value);
        Assert.Equal<TimeSpan?>(expected == PublicDirectoryState.Deferred ? TimeSpan.FromSeconds(30) : null, result.RetryAfter);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":[{},{}]}")]
    [InlineData("{\"data\":[{\"targetId\":42,\"state\":\"Completed\",\"imageUrl\":\"https://tr.rbxcdn.com/synthetic.png\"}]}")]
    [InlineData("{\"data\":[{\"targetId\":9223372036854775808,\"state\":\"Pending\"}]}")]
    public async Task IconResponseIdentityAndShapeAreValidated(string json)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Json(json)));
        using var http = new HttpClient(handler);
        Assert.Equal(PublicDirectoryState.Failure, (await new RobloxPublicDirectory(http).GetGameIconAsync(Universe, default)).State);
    }

    [Fact]
    public async Task MissingIconReturnsUnknownWithoutCreatingAnImageFetch()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Json("{\"data\":[]}")));
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).GetGameIconAsync(Universe, default);
        Assert.Equal(PublicDirectoryState.Unknown, result.State);
        Assert.Null(result.Value);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportFailureAndTimeoutRemainFixedResultsForBothGameStages(bool timeout)
    {
        using var handler = new FakeHandler((_, _) => timeout
            ? throw new TaskCanceledException("untrusted-timeout-marker")
            : throw new HttpRequestException("untrusted-connection-marker"));
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http);
        foreach (string stage in new[] { "mapping", "icon", "png" })
        {
            var result = await AtStage(directory, stage);
            Assert.Equal(timeout ? PublicDirectoryState.Deferred : PublicDirectoryState.Failure, result.State);
            Assert.DoesNotContain("untrusted-", result.Message);
        }
        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData("http://tr.rbxcdn.com/icon.png")]
    [InlineData("https://evil.invalid/icon.png")]
    [InlineData("https://tr.rbxcdn.com.evil.invalid/icon.png")]
    [InlineData("https://user:secret@tr.rbxcdn.com/icon.png")]
    [InlineData("https://tr.rbxcdn.com/icon.png?secret=untrusted-marker")]
    [InlineData("https://tr.rbxcdn.com:444/icon.png")]
    [InlineData("https://tr.rbxcdn.com/icon.png#untrusted-marker")]
    public async Task UntrustedIconUrlsCannotBecomeFetchTargets(string imageUrl)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Json(IconJson(imageUrl: imageUrl))));
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http);
        var metadata = await directory.GetGameIconAsync(Universe, default);
        Assert.Equal(PublicDirectoryState.Failure, metadata.State);
        Assert.Null(metadata.Value);
        Assert.DoesNotContain("untrusted-marker", metadata.Message);
        Assert.Equal(PublicDirectoryState.InvalidInput,
            (await directory.FetchGameIconAsync(Icon() with { ImageUri = new Uri(imageUrl) }, default)).State);
        Assert.Equal(1, handler.Calls);
    }

    public static IEnumerable<object[]> StatusFailures()
    {
        foreach (string stage in new[] { "mapping", "details", "icon", "png" })
            foreach (int status in new[] { 301, 302, 401, 403, 404, 500, 503 })
                yield return new object[] { stage, status };
    }

    [Theory]
    [MemberData(nameof(StatusFailures))]
    public async Task StatusFailuresAreTypedAndNeverRetryRedirectOrReadCredentials(string stage, int status)
    {
        using var handler = new FakeHandler((request, _) =>
        {
            AssertAnonymous(request, stage == "png" ? "image/png" : "application/json");
            if (stage == "details" && IsMapping(request)) return Task.FromResult(Json(MappingJson()));
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("untrusted-status-marker") };
            response.Headers.Location = new Uri("https://evil.invalid/do-not-follow");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http);
        var result = await AtStage(directory, stage);
        Assert.Equal(status is 401 or 403 or 404 ? PublicDirectoryState.Unknown : PublicDirectoryState.Failure, result.State);
        Assert.DoesNotContain("untrusted-status-marker", result.Message);
        Assert.Equal(stage == "details" ? 2 : 1, handler.Calls);
    }

    [Theory]
    [InlineData("mapping")]
    [InlineData("details")]
    [InlineData("icon")]
    [InlineData("png")]
    public async Task UnexpectedFinalResponseUriIsRejectedWithoutExposingIt(string stage)
    {
        using var handler = new FakeHandler((request, _) =>
        {
            if (stage == "details" && IsMapping(request)) return Task.FromResult(Json(MappingJson()));
            var response = Json(stage == "mapping" ? MappingJson() : stage == "details" ? GameJson() : IconJson());
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.invalid/untrusted-redirect-marker");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var result = await AtStage(new RobloxPublicDirectory(http), stage);
        Assert.Equal(PublicDirectoryState.Failure, result.State);
        Assert.DoesNotContain("untrusted-redirect-marker", result.Message);
    }

    [Theory]
    [InlineData("mapping")]
    [InlineData("details")]
    [InlineData("icon")]
    [InlineData("png")]
    public async Task CallerCancellationPropagatesAndDoesNotLeaveAnySlotOwned(string stage)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FakeHandler(async (request, ct) =>
        {
            if (stage == "details" && IsMapping(request)) return Json(MappingJson());
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Canceled handler must not continue.");
        });
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http);
        using var canceled = new CancellationTokenSource();
        var pending = AtStage(directory, stage, canceled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();
        int calls = handler.Calls;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await AtStage(directory, stage, alreadyCanceled.Token));
        Assert.Equal(calls, handler.Calls);
        // Two more operations reach the transport immediately rather than seeing a leaked busy slot.
        using var cleanup = new CancellationTokenSource();
        var first = directory.GetGameIconAsync(Universe, cleanup.Token);
        var second = directory.GetGameIconAsync(Universe + 1, cleanup.Token);
        Assert.Equal(calls + 2, handler.Calls);
        cleanup.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await second);
    }

    [Theory]
    [InlineData("mapping")]
    [InlineData("details")]
    [InlineData("icon")]
    [InlineData("png")]
    public async Task GamePathsRejectInheritedHeadersBeforeIo(string stage)
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("No request expected."));
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http);
        foreach (string header in new[] { "Authorization", "Cookie", "X-Secret" })
        {
            http.DefaultRequestHeaders.Clear();
            http.DefaultRequestHeaders.TryAddWithoutValidation(header, "untrusted-never-send-marker");
            var result = await AtStage(directory, stage);
            Assert.Equal(PublicDirectoryState.Failure, result.State);
            Assert.DoesNotContain("untrusted-never-send-marker", result.Message);
        }
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("mapping", false)]
    [InlineData("details", false)]
    [InlineData("icon", false)]
    [InlineData("png", false)]
    [InlineData("icon", true)]
    public async Task ProviderPauseIsSharedByHostAndNeverShortenedOrRetried(string stage, bool date)
    {
        var clock = new FakeTime();
        TimeSpan pause = TimeSpan.FromHours(2);
        using var handler = new FakeHandler((request, _) =>
        {
            if (stage == "details" && IsMapping(request)) return Task.FromResult(Json(MappingJson()));
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = date ? new RetryConditionHeaderValue(clock.GetUtcNow().Add(pause)) : new RetryConditionHeaderValue(pause);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http, clock);
        var first = await AtStage(directory, stage, now: clock.GetUtcNow());
        Assert.Equal(PublicDirectoryState.Deferred, first.State);
        Assert.Equal(pause, first.RetryAfter);
        int initialCalls = handler.Calls;
        clock.Advance(pause - TimeSpan.FromTicks(1));
        var early = await AtStage(directory, stage, now: clock.GetUtcNow());
        Assert.Equal(PublicDirectoryState.Deferred, early.State);
        Assert.Equal(TimeSpan.FromTicks(1), early.RetryAfter);
        Assert.Equal(initialCalls + (stage == "details" ? 1 : 0), handler.Calls);
        if (stage == "icon")
        {
            Assert.Equal(PublicDirectoryState.Deferred, (await directory.GetAvatarAsync(42, default)).State);
            Assert.Equal(initialCalls, handler.Calls);
        }
        else if (stage == "png")
        {
            var avatar = new PublicRobloxAvatar(42, new Uri(ImageUrl), 150, 150, clock.GetUtcNow());
            Assert.Equal(PublicDirectoryState.Deferred, (await directory.FetchAvatarAsync(avatar, default)).State);
            Assert.Equal(initialCalls, handler.Calls);
        }
        clock.Advance(TimeSpan.FromTicks(1));
        await AtStage(directory, stage, now: clock.GetUtcNow());
        Assert.Equal(initialCalls + (stage == "details" ? 3 : 1), handler.Calls);
    }

    [Fact]
    public async Task AvatarAndGameOperationsShareExactlyTwoSlotsWithZeroQueuedWork()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FakeHandler(async (request, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return request.RequestUri!.Host == "users.roblox.com"
                ? Json("{\"id\":42,\"name\":\"Synthetic\",\"displayName\":\"Synthetic\"}") : Json(IconJson());
        });
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var user = directory.LookupUserAsync(42, deadline.Token);
        var icon = directory.GetGameIconAsync(Universe, deadline.Token);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(PublicDirectoryState.Deferred, (await directory.LookupGameAsync(Place, deadline.Token)).State);
        Assert.Equal(PublicDirectoryState.Deferred, (await directory.FetchGameIconAsync(Icon(), deadline.Token)).State);
        Assert.Equal(2, handler.Calls);
        release.SetResult();
        Assert.Equal(PublicDirectoryState.Success, (await user).State);
        Assert.Equal(PublicDirectoryState.Success, (await icon).State);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ImageCacheAndInflightKeysIncludeKindIdentityAndUriWithOneAggregateBudget()
    {
        using var handler = new FakeHandler((request, _) =>
        {
            AssertAnonymous(request, "image/png");
            return Task.FromResult(Response(PngHeader(), "image/png"));
        });
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http, maximumEncodedMemoryBytes: 99);
        var avatar = new PublicRobloxAvatar(Universe, new Uri(ImageUrl), 150, 150, DateTimeOffset.UtcNow);
        var icon = Icon();
        Assert.Equal(PublicDirectoryState.Success, (await directory.FetchAvatarAsync(avatar, default)).State);
        Assert.Equal(PublicDirectoryState.Success, (await directory.FetchGameIconAsync(icon, default)).State);
        Assert.Equal(PublicDirectoryState.Success, (await directory.FetchGameIconAsync(icon with { UniverseId = Universe + 1 }, default)).State);
        Assert.Equal(3, handler.Calls); // Same URI, different image kind/id never aliases.
        Assert.Equal(99, directory.CachedEncodedBytes);
        Assert.Equal(1, directory.CachedAvatarCount);
        Assert.Equal(2, directory.CachedGameIconCount);
        Assert.Equal(3, directory.CachedImageCount);
        await directory.FetchAvatarAsync(avatar, default); // Touch avatar so first icon is oldest.
        await directory.FetchGameIconAsync(icon with { ImageUri = new Uri("https://tr.rbxcdn.com/other.png") }, default);
        Assert.Equal(4, handler.Calls);
        Assert.Equal(99, directory.CachedEncodedBytes);
        await directory.FetchGameIconAsync(icon, default); // The evicted icon is fetched again.
        Assert.Equal(5, handler.Calls);
        directory.ClearMemoryCache();
        Assert.Equal(0, directory.CachedEncodedBytes);
        Assert.Equal(0, directory.CachedAvatarCount);
        Assert.Equal(0, directory.CachedGameIconCount);
    }

    [Fact]
    public async Task DuplicateIconFetchDefersAndClearFencesLateCachePublication()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FakeHandler(async (_, ct) => { await release.Task.WaitAsync(ct); return Response(PngHeader(), "image/png"); });
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var icon = Icon();
        var first = directory.FetchGameIconAsync(icon, deadline.Token);
        Assert.Equal(PublicDirectoryState.Deferred, (await directory.FetchGameIconAsync(icon, deadline.Token)).State);
        // A different identity may use the second slot even when the URI matches.
        var second = directory.FetchGameIconAsync(icon with { UniverseId = Universe + 1 }, deadline.Token);
        Assert.Equal(2, handler.Calls);
        directory.ClearMemoryCache();
        release.SetResult();
        Assert.Equal(PublicDirectoryState.Success, (await first).State);
        Assert.Equal(PublicDirectoryState.Success, (await second).State);
        Assert.Equal(0, directory.CachedEncodedBytes);
        await directory.FetchGameIconAsync(icon, deadline.Token);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(1, directory.CachedGameIconCount);
    }

    [Theory]
    [InlineData("image/jpeg", 33, 150)]
    [InlineData("image/png", 32, 150)]
    [InlineData("image/png", 33, 10000)]
    [InlineData("image/png", RobloxPublicDirectory.MaximumAvatarBytes + 1, 150)]
    public async Task IconEncodedFetchReusesPngFormatSizeAndDimensionBounds(string mediaType, int size, int width)
    {
        byte[] bytes = PngHeader(Math.Max(size, 33));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), (uint)width);
        if (size < bytes.Length) Array.Resize(ref bytes, size);
        using var handler = new FakeHandler((_, _) => Task.FromResult(Response(bytes, mediaType, unknownLength: true)));
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http);
        Assert.Equal(PublicDirectoryState.Failure, (await directory.FetchGameIconAsync(Icon(), default)).State);
        Assert.Equal(0, directory.CachedImageCount);
    }

    [Fact]
    public async Task IconExpiryAndTooSmallCacheBudgetDoNotCreateUnboundedRetention()
    {
        var clock = new FakeTime();
        using var handler = new FakeHandler((_, _) => Task.FromResult(Response(PngHeader(), "image/png")));
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http, clock, maximumEncodedMemoryBytes: 32);
        var icon = Icon(clock.GetUtcNow());
        Assert.Equal(PublicDirectoryState.Success, (await directory.FetchGameIconAsync(icon, default)).State);
        Assert.Equal(PublicDirectoryState.Success, (await directory.FetchGameIconAsync(icon, default)).State);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(0, directory.CachedEncodedBytes);
        Assert.Equal(PublicDirectoryState.InvalidInput, (await directory.FetchGameIconAsync(icon with { Width = 10000 }, default)).State);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(PublicDirectoryState.Deferred, (await directory.FetchGameIconAsync(icon, default)).State);
        Assert.Equal(PublicDirectoryState.Deferred,
            (await directory.FetchGameIconAsync(icon with { RetrievedUtc = clock.GetUtcNow().AddHours(1) }, default)).State);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task GameIconCacheExpiryUsesSharedTenMinuteLifetime()
    {
        var clock = new FakeTime();
        using var handler = new FakeHandler((_, _) => Task.FromResult(Response(PngHeader(), "image/png")));
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http, clock);
        var icon = Icon(clock.GetUtcNow());
        await directory.FetchGameIconAsync(icon, default);
        clock.Advance(TimeSpan.FromMinutes(9));
        await directory.FetchGameIconAsync(icon, default);
        Assert.Equal(1, handler.Calls);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(PublicDirectoryState.Success, (await directory.FetchGameIconAsync(icon, default)).State);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(33, directory.CachedEncodedBytes);
    }

    private static async Task<(PublicDirectoryState State, string Message, TimeSpan? RetryAfter)> AtStage(
        RobloxPublicDirectory directory, string stage, CancellationToken ct = default, DateTimeOffset? now = null)
    {
        if (stage is "mapping" or "details")
        { var result = await directory.LookupGameAsync(Place, ct); return (result.State, result.Message, result.RetryAfter); }
        if (stage == "icon")
        { var result = await directory.GetGameIconAsync(Universe, ct); return (result.State, result.Message, result.RetryAfter); }
        var encoded = await directory.FetchGameIconAsync(Icon(now), ct);
        return (encoded.State, encoded.Message, encoded.RetryAfter);
    }

    [Fact]
    public async Task BatchLookupUsesOneGamesRequest()
    {
        using var handler = new FakeHandler((request, _) =>
        {
            AssertAnonymous(request, "application/json");
            Assert.Equal("https://games.roblox.com/v1/games?universeIds=11,22", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json("{\"data\":[" + Game(11, 110, "Eleven") + "," + Game(22, 220, "Twenty-two") + "]}"));
        });
        using var http = new HttpClient(handler); var clock = new FakeTime();
        var result = await new RobloxPublicDirectory(http, clock).LookupGamesAsync([11, 22], default);
        Assert.Equal(PublicDirectoryState.Success, result.State); var games = result.Value!;
        Assert.Equal(new long[] { 11, 22 }, games.Select(game => game.UniverseId).ToArray());
        Assert.All(games, game => { Assert.Equal(game.RootPlaceId, game.RequestedPlaceId); Assert.Equal(clock.GetUtcNow(), game.RetrievedUtc); });
        Assert.Equal("Eleven", games[0].Name); Assert.Equal(110, games[0].RootPlaceId); Assert.Equal(PublicRobloxCreatorKind.User, games[0].CreatorKind);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task BatchLookupOmitsRestrictedOrMissingUniversesInsteadOfFailing()
    {
        // A restricted universe comes back as placeholders (id 0); an omitted one is simply absent. Neither invalidates the batch.
        using var handler = new FakeHandler((_, _) => Task.FromResult(Json("{\"data\":[" + Game(11, 110, "Eleven")
            + ",{\"id\":0,\"rootPlaceId\":0,\"name\":\"\",\"creator\":{\"id\":0,\"name\":\"\",\"type\":\"User\"},\"playing\":0,\"maxPlayers\":1,\"isContentRestricted\":true}]}")));
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).LookupGamesAsync([11, 22, 33], default);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        Assert.Equal(11, Assert.Single(result.Value!).UniverseId);
    }

    [Fact]
    public async Task BatchLookupRejectsMoreThanSixOrDuplicateIdsWithoutARequest()
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("No request expected."));
        using var http = new HttpClient(handler); var directory = new RobloxPublicDirectory(http);
        foreach (long[] ids in new[] { Array.Empty<long>(), Enumerable.Range(1, 7).Select(i => (long)i).ToArray(), new long[] { 5, 5 }, new long[] { 0 } })
            Assert.Equal(PublicDirectoryState.InvalidInput, (await directory.LookupGamesAsync(ids, default)).State);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task BatchResponseWithAnUnrequestedOrDuplicateUniverseIsInvalid()
    {
        foreach (string body in new[] { "{\"data\":[" + Game(99, 990, "Foreign") + "]}", "{\"data\":[" + Game(11, 110, "A") + "," + Game(11, 111, "B") + "]}" })
        {
            using var handler = new FakeHandler((_, _) => Task.FromResult(Json(body)));
            using var http = new HttpClient(handler);
            var result = await new RobloxPublicDirectory(http).LookupGamesAsync([11, 22], default);
            Assert.Equal(PublicDirectoryState.Failure, result.State); Assert.Null(result.Value);
        }
    }

    private static string Game(long universe, long rootPlace, string name)
        => $"{{\"id\":{universe},\"rootPlaceId\":{rootPlace},\"name\":\"{name}\",\"creator\":{{\"id\":7,\"name\":\"Creator\",\"type\":\"User\"}},\"playing\":5,\"maxPlayers\":20,\"isContentRestricted\":false}}";

    private static void AssertAnonymous(HttpRequestMessage request, string accept)
    {
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https", request.RequestUri!.Scheme);
        Assert.Equal("", request.RequestUri.UserInfo);
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.Single(request.Headers);
        Assert.Equal(accept, request.Headers.Accept.Single().MediaType);
        Assert.Null(request.Content);
    }

    private static bool IsMapping(HttpRequestMessage request) => request.RequestUri!.Host == "apis.roblox.com";
    private static string MappingJson() => $"{{\"universeId\":{Universe}}}";
    private static string GameJson() => $"{{\"data\":[{{\"id\":{Universe},\"rootPlaceId\":{RootPlace},\"name\":\"Synthetic game!\",\"creator\":{{\"id\":9223372036854775807,\"name\":\"Synthetic Creator\",\"type\":\"Group\"}},\"playing\":9223372036854775807,\"maxPlayers\":25,\"isContentRestricted\":false}}]}}";
    private static string IconJson(string state = "Completed", string imageUrl = ImageUrl) => JsonSerializer.Serialize(new
        { data = new[] { new { targetId = Universe, state, imageUrl } } });
    private static PublicRobloxGameIcon Icon(DateTimeOffset? now = null) => new(Universe, new Uri(ImageUrl), 150, 150, now ?? DateTimeOffset.UtcNow);
    private static HttpResponseMessage Json(string json) => Response(Encoding.UTF8.GetBytes(json), "application/json");
    private static HttpResponseMessage Response(byte[] bytes, string mediaType, bool unknownLength = false)
    {
        HttpContent content = unknownLength ? new StreamContent(new NonSeekableStream(bytes)) : new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    // Deliberately only a header: full CRC/zlib/frame validation remains in the shared App decoder.
    private static byte[] PngHeader(int size = 33)
    {
        byte[] bytes = new byte[size];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), 150);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), 150);
        return bytes;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Interlocked.Increment(ref _calls); return handle(request, ct); }
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
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
