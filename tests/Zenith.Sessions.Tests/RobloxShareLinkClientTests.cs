using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// The signed-in share-link resolver. Every request ends at an owned synthetic handler; no secret leaves the test.
public sealed class RobloxShareLinkClientTests
{
    private const string Secret = "synthetic-session-cookie";
    private const string ShareCode = "4b9c7e0ae7b2f84bb7a7d24f6d0f6c2e";
    private const string LinkCode = "98765432109876543210";
    private const string Endpoint = "https://apis.roblox.com/sharelinks/v1/resolve-link";

    [Fact]
    public async Task PostsToTheExactEndpointWithCookieOnlyThere()
    {
        using var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal(Endpoint, request.RequestUri!.AbsoluteUri);
            Assert.Equal(".ROBLOSECURITY=" + Secret, Assert.Single(request.Headers.GetValues("Cookie")));
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            Assert.Equal(ShareCode, body.RootElement.GetProperty("linkId").GetString()); Assert.Equal("Server", body.RootElement.GetProperty("linkType").GetString());
            Assert.Equal(2, body.RootElement.EnumerateObject().Count());
            return Json(Server("Valid"));
        });
        using var resolution = await Resolve(handler, RobloxShareLinkType.Server);
        Assert.Equal(RobloxShareLinkOutcome.PrivateServer, resolution.Outcome); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CsrfHandshakeRetriesExactlyOnce()
    {
        int calls = 0; var tokens = new List<string?>();
        using var handler = new Handler((request, _) =>
        {
            calls++; tokens.Add(request.Headers.TryGetValues("X-CSRF-TOKEN", out var values) ? string.Join(",", values) : null);
            return Task.FromResult(calls == 1 ? Denied("jCcE5fGts0n2") : Json(Experience("Valid")));
        });
        using var resolution = await Resolve(handler, RobloxShareLinkType.ExperienceDetails);
        Assert.Equal(RobloxShareLinkOutcome.Experience, resolution.Outcome); Assert.Equal(5, resolution.PlaceId); Assert.Equal(6, resolution.UniverseId);
        Assert.Null(resolution.PrivateServerCode); Assert.Equal(new string?[] { null, "jCcE5fGts0n2" }, tokens.ToArray());
        using var stubborn = new Handler((_, _) => Task.FromResult(Denied("again")));
        using var refused = await Resolve(stubborn, RobloxShareLinkType.ExperienceDetails);
        Assert.Equal(RobloxShareLinkOutcome.Unavailable, refused.Outcome); Assert.Equal(2, stubborn.Calls); // One retry, never a third request.
    }

    [Fact]
    public async Task UnauthorizedMeansSignInRequired()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}", Encoding.UTF8, "application/json") }));
        using var resolution = await Resolve(handler, RobloxShareLinkType.Server);
        Assert.Equal(RobloxShareLinkOutcome.SignInRequired, resolution.Outcome); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ServerResponseResolvesProtectedLinkCode()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(Server("Valid"))));
        using var resolution = await Resolve(handler, RobloxShareLinkType.Server);
        Assert.Equal(RobloxShareLinkOutcome.PrivateServer, resolution.Outcome);
        Assert.Equal(5, resolution.PlaceId); Assert.Equal(6, resolution.UniverseId); Assert.Equal(7, resolution.OwnerUserId);
        var code = Assert.IsType<ProtectedPrivateServerCode>(resolution.PrivateServerCode);
        Assert.Equal("3210", code.Last4); string revealed = ""; code.Reveal(span => revealed = new string(span)); Assert.Equal(LinkCode, revealed);
        Assert.DoesNotContain("9876543210987", resolution.ToString(), StringComparison.Ordinal); Assert.DoesNotContain(ShareCode, resolution.ToString(), StringComparison.Ordinal);
        resolution.Dispose(); Assert.Throws<ObjectDisposedException>(() => code.Reveal(_ => { }));
    }

    // Roblox's own web code reads only these fields (status, universeId, and linkCode for servers), then opens the
    // universe's root place. A payload without placeId must still resolve; the root place is looked up separately.
    [Fact]
    public async Task UniverseOnlyPayloadsResolveWithoutAPlace()
    {
        using var experienceHandler = new Handler((_, _) => Task.FromResult(Json("{\"experienceDetailsInviteData\":{\"status\":\"Valid\",\"universeId\":6}}")));
        using var experience = await Resolve(experienceHandler, RobloxShareLinkType.ExperienceDetails);
        Assert.Equal(RobloxShareLinkOutcome.Experience, experience.Outcome); Assert.Null(experience.PlaceId); Assert.Equal(6, experience.UniverseId);
        Assert.Null(experience.PrivateServerCode);

        using var serverHandler = new Handler((_, _) => Task.FromResult(Json("{\"privateServerInviteData\":{\"status\":\"Valid\",\"universeId\":6,\"linkCode\":\"" + LinkCode + "\"}}")));
        using var server = await Resolve(serverHandler, RobloxShareLinkType.Server);
        Assert.Equal(RobloxShareLinkOutcome.PrivateServer, server.Outcome); Assert.Null(server.PlaceId); Assert.Equal(6, server.UniverseId);
        Assert.Null(server.OwnerUserId); Assert.Equal("3210", Assert.IsType<ProtectedPrivateServerCode>(server.PrivateServerCode).Last4);

        using var nullPlaceHandler = new Handler((_, _) => Task.FromResult(Json("{\"experienceDetailsInviteData\":{\"status\":\"Valid\",\"placeId\":null,\"universeId\":6}}")));
        using var nullPlace = await Resolve(nullPlaceHandler, RobloxShareLinkType.ExperienceDetails);
        Assert.Equal(RobloxShareLinkOutcome.Experience, nullPlace.Outcome); Assert.Null(nullPlace.PlaceId); Assert.Equal(6, nullPlace.UniverseId);
    }

    [Fact]
    public async Task UniverseStaysRequiredAndAPresentPlaceMustBeValid()
    {
        foreach (string body in new[]
        {
            "{\"experienceDetailsInviteData\":{\"status\":\"Valid\",\"placeId\":5}}",
            "{\"experienceDetailsInviteData\":{\"status\":\"Valid\",\"placeId\":5,\"universeId\":0}}",
            "{\"experienceDetailsInviteData\":{\"status\":\"Valid\",\"placeId\":\"5\",\"universeId\":6}}",
            "{\"experienceDetailsInviteData\":{\"status\":\"Valid\",\"placeId\":0,\"universeId\":6}}"
        })
        {
            using var handler = new Handler((_, _) => Task.FromResult(Json(body)));
            using var resolution = await Resolve(handler, RobloxShareLinkType.ExperienceDetails);
            Assert.Equal(RobloxShareLinkOutcome.Unavailable, resolution.Outcome); Assert.Null(resolution.PlaceId); Assert.Null(resolution.UniverseId);
        }
    }

    [Theory]
    [InlineData("Expired", RobloxShareLinkOutcome.Expired)]
    [InlineData("Invalid", RobloxShareLinkOutcome.Invalid)]
    [InlineData("Unexpected", RobloxShareLinkOutcome.Unavailable)]
    public async Task ExpiredInvalidAndUnknownStatusesAreMapped(string status, RobloxShareLinkOutcome outcome)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(Experience(status))));
        using var resolution = await Resolve(handler, RobloxShareLinkType.ExperienceDetails);
        Assert.Equal(outcome, resolution.Outcome); Assert.Null(resolution.PlaceId); Assert.Null(resolution.PrivateServerCode);
    }

    [Fact]
    public async Task UnknownShapesOversizeRedirectRateLimitAndNetworkFailureAreUnavailable()
    {
        foreach (var factory in new Func<HttpResponseMessage>[]
        {
            () => Json("{}"),
            () => Json("{\"experienceDetailsInviteData\":{\"status\":\"Valid\"}}"),
            () => Json("{\"experienceDetailsInviteData\":{\"status\":\"Valid\",\"placeId\":-5,\"universeId\":6}}"),
            () => Json("{\"experienceDetailsInviteData\":" + new string(' ', 70 * 1024) + "{}}"),
            () => Json("not json"),
            () => { var redirect = new HttpResponseMessage(HttpStatusCode.Found); redirect.Headers.Location = new Uri("https://evil.invalid/"); return redirect; },
            () => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}", Encoding.UTF8, "application/json") },
            () => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}", Encoding.UTF8, "application/json") },
            () => throw new HttpRequestException("synthetic network failure " + ShareCode)
        })
        {
            using var handler = new Handler((_, _) => Task.FromResult(factory()));
            using var resolution = await Resolve(handler, RobloxShareLinkType.ExperienceDetails);
            Assert.Equal(RobloxShareLinkOutcome.Unavailable, resolution.Outcome); Assert.Null(resolution.PlaceId); Assert.Equal(1, handler.Calls);
        }
    }

    [Fact]
    public async Task ServerShapeIsOnlyAcceptedForServerLinksAndOtherTypesNeverRequest()
    {
        using var mismatched = new Handler((_, _) => Task.FromResult(Json(Server("Valid"))));
        using var wrong = await Resolve(mismatched, RobloxShareLinkType.ExperienceDetails);
        Assert.Equal(RobloxShareLinkOutcome.Unavailable, wrong.Outcome); // The response answers a different question.
        using var untouched = new Handler((_, _) => throw new InvalidOperationException("No request expected."));
        using var other = await Resolve(untouched, RobloxShareLinkType.Other);
        Assert.Equal(RobloxShareLinkOutcome.Invalid, other.Outcome); Assert.Equal(0, untouched.Calls);
    }

    [Fact]
    public async Task CancellationAndTimeoutNeverLeakTheCodes()
    {
        using var slow = new Handler(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return Json(Experience("Valid")); });
        using var client = new RobloxShareLinkClient(slow, TimeSpan.FromMilliseconds(150));
        using var credential = new RobloxSessionCredential(Secret);
        using var link = new RobloxShareLink(new ProtectedShareCode(ShareCode), RobloxShareLinkType.ExperienceDetails);
        using var timedOut = await client.ResolveAsync(link, credential, default);
        Assert.Equal(RobloxShareLinkOutcome.Unavailable, timedOut.Outcome);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ResolveAsync(link, credential, stop.Token));
        Assert.DoesNotContain(ShareCode, cancelled.ToString(), StringComparison.Ordinal); Assert.DoesNotContain(Secret, cancelled.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ShareCode, link.ToString(), StringComparison.Ordinal);
    }

    private static async Task<RobloxShareLinkResolution> Resolve(Handler handler, RobloxShareLinkType type)
    {
        using var client = new RobloxShareLinkClient(handler);
        using var credential = new RobloxSessionCredential(Secret);
        using var link = new RobloxShareLink(new ProtectedShareCode(ShareCode), type);
        return await client.ResolveAsync(link, credential, default);
    }
    private static string Experience(string status) => "{\"experienceDetailsInviteData\":{\"status\":\"" + status + "\",\"placeId\":5,\"universeId\":6},\"privateServerInviteData\":null}";
    private static string Server(string status) => "{\"experienceDetailsInviteData\":null,\"privateServerInviteData\":{\"status\":\"" + status + "\",\"placeId\":5,\"universeId\":6,\"ownerUserId\":7,\"linkCode\":\"" + LinkCode + "\"}}";
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Denied(string token)
    {
        var denied = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"errors\":[{\"code\":0,\"message\":\"XSRF token invalid\"}]}", Encoding.UTF8, "application/json") };
        denied.Headers.Add("x-csrf-token", token); return denied;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        private int _calls; public int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Interlocked.Increment(ref _calls); return handle(request, ct); }
    }
}
