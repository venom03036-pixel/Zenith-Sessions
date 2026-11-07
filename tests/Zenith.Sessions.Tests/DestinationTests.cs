using System.Net;
using System.Net.Http;
using System.IO;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class DestinationTests
{
    private const string Job = "12345678-1234-4234-8234-123456789abc";
    private const string OtherJob = "22345678-1234-4234-8234-123456789abc";

    [Theory]
    [InlineData("123", 123)]
    [InlineData(" 123 ", 123)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("https://www.roblox.com/games/123", 123)]
    [InlineData("https://roblox.com/games/123/An-Experience", 123)]
    [InlineData("https://www.roblox.com:443/games/123/An-Experience/", 123)]
    [InlineData("https://www.roblox.com/games/start?placeId=123", 123)]
    [InlineData("https://www.roblox.com/games/start?%70laceId=%31%32%33", 123)]
    [InlineData("roblox://placeId=123", 123)]
    public void PlaceInputsResolveOnlyARequestedDestination(string input, long placeId)
    {
        var parsed = RobloxDestinationParser.Parse(input);
        Assert.Equal(RobloxDestinationStatus.Resolved, parsed.Status);
        Assert.Equal(RobloxDestinationKind.Place, parsed.Kind);
        Assert.NotNull(parsed.RequestedDestination);
        Assert.Equal(placeId, parsed.RequestedDestination.PlaceId);
        Assert.Null(parsed.RequestedDestination.JobId);
        Assert.Equal(RobloxServerKind.Unknown, parsed.RequestedDestination.ServerKind);
        Assert.Contains("unverified", parsed.Explanation);
    }

    [Theory]
    [InlineData("https://www.roblox.com/games/start?placeId=123&gameInstanceId=")]
    [InlineData("roblox://placeId=123&gameInstanceId=")]
    public void DocumentedInstanceLinksPreserveTheExplicitJobWithoutAssumingPublicAccess(string prefix)
    {
        var parsed = RobloxDestinationParser.Parse(prefix + Job.ToUpperInvariant());
        Assert.Equal(RobloxDestinationStatus.Resolved, parsed.Status);
        Assert.Equal(RobloxDestinationKind.ServerInstance, parsed.Kind);
        Assert.Equal(Job, parsed.RequestedDestination!.JobId);
        Assert.Equal(RobloxServerKind.Unknown, parsed.RequestedDestination.ServerKind);
        Assert.Equal("https://www.roblox.com/games/start?placeId=123&gameInstanceId=" + Job, parsed.RequestedDestination.ToWebStartUri().AbsoluteUri);
        Assert.Equal("https://www.roblox.com/games/123", parsed.RequestedDestination.ToGamePageUri().AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("9223372036854775808")]
    [InlineData("１２３")]
    [InlineData("https://www.roblox.com/games/-1")]
    [InlineData("https://www.roblox.com/games/9223372036854775808")]
    [InlineData("https://www.roblox.com/games/start?placeId=0")]
    [InlineData("https://www.roblox.com/games/start?placeId=-1")]
    [InlineData("https://www.roblox.com/games/start?placeId=%2B1")]
    [InlineData("https://www.roblox.com/games/start?placeId=9223372036854775808")]
    [InlineData("https://www.roblox.com/games/start?placeId=123&placeId=456")]
    [InlineData("https://www.roblox.com/games/start?placeId=123&PlaceId=123")]
    [InlineData("https://www.roblox.com/games/start?placeId=123&%70laceId=123")]
    [InlineData("https://www.roblox.com/games/123?placeId=123")]
    [InlineData("https://www.roblox.com/games/start?placeId=123&gameInstanceId=not-a-job")]
    [InlineData("https://www.roblox.com/games/start?placeId=123&gameInstanceId=00000000-0000-0000-0000-000000000000")]
    [InlineData("https://www.roblox.com/games/start?placeId=123&x=%ZZ")]
    [InlineData("https://www.roblox.com/games/start?placeId=123&x=%0a")]
    [InlineData("https://www.roblox.com/games/start?placeId=123&&x=1")]
    [InlineData("https://m.roblox.com/de/games/-1")]
    [InlineData("https://www.roblox.com/users/0/profile")]
    [InlineData("https://www.roblox.com/de/users/9223372036854775808")]
    [InlineData("roblox://experiences/start?placeId=0")]
    [InlineData("roblox://experiences/start?placeId=123&gameInstanceId=not-a-job")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void InvalidAndAmbiguousIdsNeverBecomeTargets(string? input)
    {
        var parsed = RobloxDestinationParser.Parse(input);
        Assert.Equal(RobloxDestinationStatus.Invalid, parsed.Status);
        Assert.Null(parsed.RequestedDestination);
    }

    [Theory]
    [InlineData("http://www.roblox.com/games/123")]
    [InlineData("https://www.roblox.com.evil.invalid/games/123")]
    [InlineData("https://www.roblox.com@evil.invalid/games/123")]
    [InlineData("https://user:password@www.roblox.com/games/123")]
    [InlineData("https://localhost/games/123")]
    [InlineData("https://127.0.0.1/games/123")]
    [InlineData("https://www.roblox.com:444/games/123")]
    [InlineData("https://www.roblox.com./games/123")]
    [InlineData("https://www%2eroblox.com/games/123")]
    [InlineData("file:///games/123")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://www.roblox.com\\games\\123")]
    [InlineData("https://www.roblox.com/games/123#redirect")]
    [InlineData("https://www.roblox.com/ignored/../games/123")]
    [InlineData("https://www.roblox.com/ignored/%2e%2e/games/123")]
    [InlineData("https://www.roblox.com/games%2f123")]
    [InlineData("roblox://user@placeId=123")]
    [InlineData("roblox-player:1+launchmode:play")]
    [InlineData("http://m.roblox.com/games/123")]
    [InlineData("https://m.roblox.com.evil.invalid/games/123")]
    [InlineData("https://web.roblox.com@evil.invalid/games/123")]
    [InlineData("https://web.roblox.com:444/games/123")]
    [InlineData("https://mobile.roblox.com/games/123")]
    [InlineData("roblox://experiences/start/?placeId=123")]
    [InlineData("roblox://experiences/start?placeId=123#fragment")]
    [InlineData("roblox://experiences/join?placeId=123")]
    public void UntrustedUrlsAreRejectedWithoutBeingOpened(string input)
    {
        var parsed = RobloxDestinationParser.Parse(input);
        Assert.Equal(RobloxDestinationStatus.Invalid, parsed.Status);
        Assert.Null(parsed.RequestedDestination);
    }

    [Theory]
    [InlineData("https://www.roblox.com/share?code=synthetic-sensitive-value&type=Server", RobloxDestinationKind.UnresolvedShareLink)]
    [InlineData("https://ro.blox.com/Ebh5?af_dp=synthetic-sensitive-value", RobloxDestinationKind.UnresolvedShareLink)]
    [InlineData("https://www.roblox.com/games/123?privateServerLinkCode=synthetic-sensitive-value", RobloxDestinationKind.UnresolvedPrivateServer)]
    [InlineData("https://www.roblox.com/games/start?placeId=123&linkCode=synthetic-sensitive-value", RobloxDestinationKind.UnresolvedPrivateServer)]
    [InlineData("roblox://placeId=123&accessCode=synthetic-sensitive-value", RobloxDestinationKind.UnresolvedPrivateServer)]
    [InlineData("roblox://placeId=123&reservedServerAccessCode=synthetic-sensitive-value", RobloxDestinationKind.UnresolvedReservedServer)]
    [InlineData("https://www.roblox.com/games/start?userId=456", RobloxDestinationKind.UnresolvedFollowUser)]
    [InlineData("https://www.roblox.com/games/start?placeId=123&launchData=synthetic-sensitive-value", RobloxDestinationKind.UnresolvedUnknownLink)]
    [InlineData("https://www.roblox.com/unknown?placeId=123", RobloxDestinationKind.UnresolvedUnknownLink)]
    public void OpaqueOrUnsupportedDestinationsStayUnresolvedAndDiscardSensitiveInputs(string input, RobloxDestinationKind expected)
    {
        var parsed = RobloxDestinationParser.Parse(input);
        Assert.Equal(RobloxDestinationStatus.Unresolved, parsed.Status);
        Assert.Equal(expected, parsed.Kind);
        Assert.Null(parsed.RequestedDestination);
        Assert.DoesNotContain("synthetic-sensitive-value", parsed.ToString());
        Assert.DoesNotContain("synthetic-sensitive-value", JsonSerializer.Serialize(parsed));
        Assert.DoesNotContain(input, parsed.Explanation);
    }

    // Forms copied from other Roblox surfaces: the web. and m. hosts, one language prefix, the path form of the deep link,
    // and an AppsFlyer ro.blox.com wrapper whose Roblox link is decoded once and parsed here, never fetched.
    [Theory]
    [InlineData("https://web.roblox.com/games/123/An-Experience", null)]
    [InlineData("https://m.roblox.com/games/123", null)]
    [InlineData("https://www.roblox.com/de/games/123/Ein-Erlebnis", null)]
    [InlineData("https://roblox.com/PT-BR/games/123/", null)]
    [InlineData("https://www.roblox.com/zh-hans/games/start?placeId=123&gameInstanceId=" + Job, Job)]
    [InlineData("roblox://experiences/start?placeId=123", null)]
    [InlineData("ROBLOX://Experiences/Start?placeId=123&gameInstanceId=" + Job, Job)]
    [InlineData("https://ro.blox.com/Ebh5?pid=share&af_dp=roblox%3A%2F%2FplaceId%3D123", null)]
    [InlineData("https://ro.blox.com/Ebh5?af_web_dp=https%3A%2F%2Fm.roblox.com%2Fde%2Fgames%2F123%2FAn-Experience", null)]
    [InlineData("https://ro.blox.com/Ebh5?deep_link_value=roblox%3A%2F%2Fexperiences%2Fstart%3FplaceId%3D123%26gameInstanceId%3D" + Job, Job)]
    [InlineData("https://ro.blox.com/Ebh5?af_dp=roblox%3A%2F%2Fnavigation%2Fgame_details%3FgameId%3D9&af_web_dp=https%3A%2F%2Fwww.roblox.com%2Fgames%2F123", null)]
    public void OtherRobloxSurfaceFormsResolveTheSameRequestedDestination(string input, string? job)
    {
        var parsed = RobloxDestinationParser.Parse(input);
        Assert.Equal(RobloxDestinationStatus.Resolved, parsed.Status);
        Assert.Equal(job is null ? RobloxDestinationKind.Place : RobloxDestinationKind.ServerInstance, parsed.Kind);
        Assert.Equal(123, parsed.RequestedDestination!.PlaceId);
        Assert.Equal(job, parsed.RequestedDestination.JobId);
        Assert.Equal(RobloxServerKind.Unknown, parsed.RequestedDestination.ServerKind);
    }

    [Theory]
    [InlineData("https://www.roblox.com/users/456/profile", RobloxDestinationKind.UnresolvedFollowUser)]
    [InlineData("https://m.roblox.com/users/456/", RobloxDestinationKind.UnresolvedFollowUser)]
    [InlineData("https://www.roblox.com/de/users/456/profile?friendshipSourceType=synthetic-sensitive-value", RobloxDestinationKind.UnresolvedFollowUser)]
    [InlineData("https://www.roblox.com/fr/share?code=synthetic-sensitive-value&type=Server", RobloxDestinationKind.UnresolvedShareLink)]
    [InlineData("https://web.roblox.com/share?code=synthetic-sensitive-value&type=Server", RobloxDestinationKind.UnresolvedShareLink)]
    [InlineData("https://ro.blox.com/Ebh5?af_web_dp=https%3A%2F%2Fwww.roblox.com%2Fgames%2F123%3FprivateServerLinkCode%3Dsynthetic-sensitive-value", RobloxDestinationKind.UnresolvedPrivateServer)]
    // A wrapper inside a wrapper is not unwrapped again; a wrapped foreign, unreadable or unsupported link leaves the wrapper opaque.
    [InlineData("https://ro.blox.com/Ebh5?af_dp=https%3A%2F%2Fro.blox.com%2FEbh5%3Faf_dp%3D123", RobloxDestinationKind.UnresolvedShareLink)]
    [InlineData("https://ro.blox.com/Ebh5?af_web_dp=https%3A%2F%2Fevil.invalid%2Fgames%2F123", RobloxDestinationKind.UnresolvedShareLink)]
    [InlineData("https://ro.blox.com/Ebh5?af_web_dp=https%3A%2F%2Fwww.roblox.com%2Fhome&af_dp=synthetic-sensitive-value", RobloxDestinationKind.UnresolvedShareLink)]
    // Only one leading language segment, only before a page this parser reads.
    [InlineData("https://www.roblox.com/deu/games/123", RobloxDestinationKind.UnresolvedUnknownLink)]
    [InlineData("https://www.roblox.com/de/fr/games/123", RobloxDestinationKind.UnresolvedUnknownLink)]
    [InlineData("https://www.roblox.com/de/catalog/123", RobloxDestinationKind.UnresolvedUnknownLink)]
    public void OtherRobloxSurfaceFormsThatNameNoDestinationStayUnresolved(string input, RobloxDestinationKind expected)
    {
        var parsed = RobloxDestinationParser.Parse(input);
        Assert.Equal(RobloxDestinationStatus.Unresolved, parsed.Status);
        Assert.Equal(expected, parsed.Kind);
        Assert.Null(parsed.RequestedDestination);
        Assert.DoesNotContain("synthetic-sensitive-value", parsed.ToString());
        Assert.DoesNotContain("synthetic-sensitive-value", JsonSerializer.Serialize(parsed));
        Assert.DoesNotContain("synthetic-sensitive-value", parsed.Explanation);
    }

    // A server (job) ID names a server but not its game, and the game cannot be worked out locally.
    [Theory]
    [InlineData(Job)]
    [InlineData(" 12345678-1234-4234-8234-123456789ABC ")]
    public void ABareServerIdAsksForItsGameInsteadOfGuessing(string input)
    {
        var parsed = RobloxDestinationParser.Parse(input);
        Assert.Equal(RobloxDestinationStatus.Unresolved, parsed.Status);
        Assert.Equal(RobloxDestinationKind.UnresolvedJobWithoutPlace, parsed.Kind);
        Assert.Null(parsed.RequestedDestination);
        Assert.Contains("server ID", parsed.Explanation);
        Assert.Contains("place ID", parsed.Explanation);
    }

    [Fact]
    public void NonStartGamePageCannotClaimToHonorAnInstanceParameter()
    {
        var parsed = RobloxDestinationParser.Parse("https://www.roblox.com/games/123?gameInstanceId=" + Job);
        Assert.Equal(RobloxDestinationStatus.Unresolved, parsed.Status);
        Assert.Null(parsed.RequestedDestination);
    }

    [Fact]
    public void InputAndQueryBoundsAreEnforced()
    {
        Assert.Equal(RobloxDestinationStatus.Invalid, RobloxDestinationParser.Parse(new string('1', 4097)).Status);
        Assert.Equal(RobloxDestinationStatus.Invalid, RobloxDestinationParser.Parse("https://www.roblox.com/games/123?code=" + new string('a', 2049)).Status);
        Assert.Equal(RobloxDestinationStatus.Invalid, RobloxDestinationParser.Parse("roblox://placeId=123&" + string.Join('&', Enumerable.Range(0, 33).Select(i => $"k{i}=v"))).Status);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RobloxRequestedDestination(0));
        Assert.Throws<ArgumentException>(() => new RobloxRequestedDestination(123, "not-a-job"));
    }

    [Fact]
    public async Task PublicBrowserUsesOnlyTheBoundedAnonymousEndpointAndEscapesOpaqueCursors()
    {
        const string cursor = "opaque&cursor=/+?%";
        var handler = new SyntheticHandler((request, index, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("games.roblox.com", request.RequestUri.Host);
            Assert.Equal("/v1/games/123/servers/Public", request.RequestUri.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Contains("sortOrder=Asc", request.RequestUri.Query);
            Assert.Contains("excludeFullGames=false", request.RequestUri.Query);
            Assert.Contains("limit=10", request.RequestUri.Query);
            if (index == 1) Assert.DoesNotContain("cursor=", request.RequestUri.Query);
            else Assert.Contains("cursor=" + Uri.EscapeDataString(cursor), request.RequestUri.OriginalString);
            return Task.FromResult(Json(index == 1 ? Page(Job, cursor) : Page(OtherJob)));
        });
        using var http = new HttpClient(handler);
        var result = await new RobloxServerDirectory(http).BrowseAsync(new(123, MaxPages: 2), default);
        Assert.Equal(RobloxPublicServerStatus.Complete, result.Status);
        Assert.Equal(2, handler.Requests);
        Assert.Equal(2, result.Servers.Count);
        Assert.All(result.Servers, server =>
        {
            Assert.Equal(123, server.PlaceId);
            Assert.True(server.HasOpenSlots);
            Assert.Equal(RobloxServerKind.Public, server.RequestedDestination.ServerKind);
            Assert.True(server.ObservedUtc > DateTimeOffset.MinValue);
        });
        Assert.Null(result.NextPageCursor);
        Assert.Contains("not confirmed arrival", result.Explanation);
        Assert.DoesNotContain(cursor, new RobloxPublicServerQuery(123, Cursor: cursor).ToString());
    }

    [Theory]
    [InlineData(RobloxPublicServerSortOrder.Asc, false)]
    [InlineData(RobloxPublicServerSortOrder.Asc, true)]
    [InlineData(RobloxPublicServerSortOrder.Desc, false)]
    [InlineData(RobloxPublicServerSortOrder.Desc, true)]
    public async Task RemoteOrderAndExcludeFullAreExactAnonymousQueryParametersAcrossPages(RobloxPublicServerSortOrder order,bool excludeFull)
    {
        const string cursor="opaque&cursor=/+?%";
        var handler=new SyntheticHandler((request,index,_)=>
        {
            string expected="https://games.roblox.com/v1/games/9223372036854775807/servers/Public?sortOrder="+order
                +"&excludeFullGames="+(excludeFull?"true":"false")+"&limit=25"
                +(index==1?"":"&cursor="+Uri.EscapeDataString(cursor));
            Assert.Equal(expected,request.RequestUri!.AbsoluteUri);Assert.Equal(HttpMethod.Get,request.Method);Assert.Null(request.Content);
            Assert.Equal("application/json",Assert.Single(request.Headers.Accept).MediaType);
            Assert.Null(request.Headers.Authorization);Assert.False(request.Headers.Contains("Cookie"));
            Assert.False(request.Headers.Contains("Proxy-Authorization"));Assert.False(request.Headers.Contains("x-api-key"));
            Assert.Empty(request.RequestUri.UserInfo);
            return Task.FromResult(Json(index==1?Page(Job,cursor):Page(OtherJob)));
        });
        using var http=new HttpClient(handler);
        var query=new RobloxPublicServerQuery(long.MaxValue,PageSize:25,MaxPages:2,SortOrder:order,ExcludeFullGames:excludeFull);
        Assert.Same(query,query.Validated());
        var result=await new RobloxServerDirectory(http).BrowseAsync(query,default);
        Assert.Equal(RobloxPublicServerStatus.Complete,result.Status);Assert.Equal(2,handler.Requests);Assert.Equal(2,result.Servers.Count);
        Assert.All(result.Servers,server=>Assert.Equal(long.MaxValue,server.PlaceId));
    }

    [Fact]
    public void LegacyQueryCallersRetainAscendingOrderAndIncludeFullGames()
    {
        var query=new RobloxPublicServerQuery(123,25,1,"opaque");
        Assert.Equal(RobloxPublicServerSortOrder.Asc,query.SortOrder);Assert.False(query.ExcludeFullGames);
        Assert.DoesNotContain("opaque",query.ToString());
    }

    [Fact]
    public async Task PageLimitReturnsTheCursorWithoutFetchingAnotherPage()
    {
        using var http = new HttpClient(new SyntheticHandler((_, _, _) => Task.FromResult(Json(Page(Job, "next-page")))));
        var result = await new RobloxServerDirectory(http).BrowseAsync(new(123), default);
        Assert.Equal(RobloxPublicServerStatus.PageLimit, result.Status);
        Assert.Equal("next-page", result.NextPageCursor);
        Assert.Single(result.Servers);
        Assert.DoesNotContain("next-page", result.ToString());
    }

    [Fact]
    public async Task RateLimitPreservesPreviousObservationsAndDefersWithoutRetry()
    {
        var handler = new SyntheticHandler((_, index, _) =>
        {
            if (index == 1) return Task.FromResult(Json(Page(Job, "next-page")));
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var result = await new RobloxServerDirectory(http).BrowseAsync(new(123, MaxPages: 3), default);
        Assert.Equal(RobloxPublicServerStatus.RateLimited, result.Status);
        Assert.Single(result.Servers);
        Assert.Equal("next-page", result.NextPageCursor);
        Assert.Equal(TimeSpan.FromSeconds(17), result.RetryAfter);
        Assert.Equal(2, handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicServerRateLimitPreservesLongDeltaOrFutureDateWithoutAutomaticRetries(bool dateHeader)
    {
        var clock = new RetryClock();
        TimeSpan pause = dateHeader ? TimeSpan.FromDays(3) : TimeSpan.FromHours(2);
        using var handler = new SyntheticHandler((_, _, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = dateHeader ? new RetryConditionHeaderValue(clock.GetUtcNow().Add(pause)) : new RetryConditionHeaderValue(pause);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var result = await new RobloxServerDirectory(http, time: clock).BrowseAsync(new(123), default);
        Assert.Equal(RobloxPublicServerStatus.RateLimited, result.Status);
        Assert.Equal(pause, result.RetryAfter);
        clock.Advance(pause - TimeSpan.FromTicks(1));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task PublicServerHugeDecimalPauseSaturatesWithoutOverflow()
    {
        var clock = new RetryClock();
        using var handler = new SyntheticHandler((_, _, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", new string('9', 1000));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var result = await new RobloxServerDirectory(http, time: clock).BrowseAsync(new(123), default);
        Assert.Equal(RobloxPublicServerStatus.RateLimited, result.Status);
        Assert.Equal(DateTimeOffset.MaxValue - clock.GetUtcNow(), result.RetryAfter);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("invalid-date")]
    [InlineData("999999999999999999999999999999oops")]
    [InlineData("7200, 10")]
    [InlineData("Wed, 31 Dec 2025 23:59:59 GMT")]
    public async Task PublicServerMissingInvalidOrNonpositiveHeaderUsesThirtySeconds(string? header)
    {
        using var handler = new SyntheticHandler((_, _, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            if (header is not null) response.Headers.TryAddWithoutValidation("Retry-After", header);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var result = await new RobloxServerDirectory(http, time: new RetryClock()).BrowseAsync(new(123), default);
        Assert.Equal(RobloxPublicServerStatus.RateLimited, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Accepted, RobloxPublicServerStatus.Deferred)]
    [InlineData(HttpStatusCode.ServiceUnavailable, RobloxPublicServerStatus.Deferred)]
    [InlineData(HttpStatusCode.Found, RobloxPublicServerStatus.Deferred)]
    [InlineData(HttpStatusCode.Unauthorized, RobloxPublicServerStatus.Unavailable)]
    [InlineData(HttpStatusCode.Forbidden, RobloxPublicServerStatus.Unavailable)]
    public async Task NonSuccessResponsesNeverTriggerAuthenticationOrRedirectFallback(HttpStatusCode status, RobloxPublicServerStatus expected)
    {
        var handler = new SyntheticHandler((_, _, _) =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.Location = new Uri("http://localhost/sensitive");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var result = await new RobloxServerDirectory(http).BrowseAsync(new(123, MaxPages: 10), default);
        Assert.Equal(expected, result.Status);
        Assert.Empty(result.Servers);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"data\":[],\"data\":[],\"nextPageCursor\":null}")]
    [InlineData("{\"data\":[],\"nextPageCursor\":{}}")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[{\"id\":\"not-a-job\",\"playing\":1,\"maxPlayers\":10}],\"nextPageCursor\":null}")]
    public async Task MalformedResponseNeverReturnsInventedDestinations(string payload)
    {
        using var http = new HttpClient(new SyntheticHandler((_, _, _) => Task.FromResult(Json(payload))));
        var result = await new RobloxServerDirectory(http).BrowseAsync(new(123), default);
        Assert.Equal(RobloxPublicServerStatus.InvalidResponse, result.Status);
        Assert.Empty(result.Servers);
    }

    [Theory]
    [InlineData("\"vipServerId\":123")]
    [InlineData("\"accessCode\":\"synthetic-sensitive-value\"")]
    [InlineData("\"reservedServerAccessCode\":\"synthetic-sensitive-value\"")]
    [InlineData("\"serverType\":\"Private\"")]
    public async Task NonpublicRecordsCannotBeMisclassifiedOrLeakAccessCodes(string extra)
    {
        string payload = "{\"nextPageCursor\":null,\"data\":[{\"id\":\"" + Job + "\",\"playing\":1,\"maxPlayers\":10," + extra + "}]}";
        using var http = new HttpClient(new SyntheticHandler((_, _, _) => Task.FromResult(Json(payload))));
        var result = await new RobloxServerDirectory(http).BrowseAsync(new(123), default);
        Assert.Equal(RobloxPublicServerStatus.InvalidResponse, result.Status);
        Assert.Empty(result.Servers);
        Assert.DoesNotContain("synthetic-sensitive-value", result.ToString());
        Assert.DoesNotContain("synthetic-sensitive-value", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task OversizedDeclaredAndStreamingResponsesAreBounded()
    {
        string oversized = new(' ', RobloxServerDirectory.MaximumResponseBytes + 1);
        foreach (bool declaredLength in new[] { true, false })
        {
            var handler = new SyntheticHandler((_, _, _) =>
            {
                var response = Json(oversized);
                if (!declaredLength)
                {
                    response.Content.Dispose();
                    response.Content = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(oversized)));
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                }
                return Task.FromResult(response);
            });
            using var http = new HttpClient(handler);
            var result = await new RobloxServerDirectory(http).BrowseAsync(new(123), default);
            Assert.Equal(RobloxPublicServerStatus.InvalidResponse, result.Status);
            Assert.Empty(result.Servers);
        }
    }

    [Fact]
    public async Task RepeatedCursorsStopPaginationAndDuplicateJobsDoNotMultiplyObservations()
    {
        var handler = new SyntheticHandler((_, _, _) => Task.FromResult(Json(Page(Job, "same"))));
        using var http = new HttpClient(handler);
        var result = await new RobloxServerDirectory(http).BrowseAsync(new(123, MaxPages: 10), default);
        Assert.Equal(RobloxPublicServerStatus.InvalidResponse, result.Status);
        Assert.Single(result.Servers);
        Assert.Equal(2, handler.Requests);

        var duplicateHandler = new SyntheticHandler((_, index, _) => Task.FromResult(Json(Page(Job, index == 1 ? "next" : null))));
        using var duplicateHttp = new HttpClient(duplicateHandler);
        var deduplicated = await new RobloxServerDirectory(duplicateHttp).BrowseAsync(new(123, MaxPages: 2), default);
        Assert.Equal(RobloxPublicServerStatus.Complete, deduplicated.Status);
        Assert.Single(deduplicated.Servers);
    }

    [Fact]
    public async Task PageAndCursorBoundsAreCheckedBeforeNetwork()
    {
        var handler = new SyntheticHandler((_, _, _) => throw new InvalidOperationException("No request expected."));
        using var http = new HttpClient(handler);
        var directory = new RobloxServerDirectory(http);
        foreach (var query in new[] { new RobloxPublicServerQuery(0), new(123, PageSize: 101), new(123, PageSize: 1),
            new(123, MaxPages: 0), new(123, MaxPages: 11), new(123, Cursor: new string('x', 2049)), new(123, Cursor: "bad\r\n"),
            new(123, SortOrder: (RobloxPublicServerSortOrder)(-1)), new(123, SortOrder: (RobloxPublicServerSortOrder)999) })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => directory.BrowseAsync(query, default));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task DeadlineIsDeferredAndUserCancellationRemainsCancellation()
    {
        var handler = new SyntheticHandler(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("The synthetic request must be cancelled.");
        });
        using var http = new HttpClient(handler);
        var directory = new RobloxServerDirectory(http, TimeSpan.FromMilliseconds(25));
        var deferred = await directory.BrowseAsync(new(123), default);
        Assert.Equal(RobloxPublicServerStatus.Deferred, deferred.Status);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => directory.BrowseAsync(new(123), stop.Token));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task PublicBrowserRejectsClientsWithAuthenticationHeadersIncludingLaterChanges()
    {
        using var http = new HttpClient(new SyntheticHandler((_, _, _) => throw new InvalidOperationException("No request expected.")));
        var directory = new RobloxServerDirectory(http);
        http.DefaultRequestHeaders.Add("Cookie", "synthetic=not-for-public-browsing");
        Assert.Throws<ArgumentException>(() => new RobloxServerDirectory(http));
        await Assert.ThrowsAsync<ArgumentException>(() => directory.BrowseAsync(new(123), default));
    }

    private static string Page(string job, string? cursor = null) => JsonSerializer.Serialize(new
    {
        nextPageCursor = cursor,
        data = new[] { new { id = job, playing = 1, maxPlayers = 10, fps = 59.5, ping = 40 } }
    });
    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json")
    };
    private sealed class SyntheticHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return send(request, ++Requests, ct);
        }
    }
    private sealed class RetryClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _source = new(bytes, writable: false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _source.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _source.ReadAsync(buffer, ct);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _source.Dispose(); base.Dispose(disposing); }
    }
}
