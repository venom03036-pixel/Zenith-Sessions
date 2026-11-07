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

    [Fact]
    public async Task ARotatedSessionCookieFromEitherHandshakeResponseIsHandedBackUnverified()
    {
        // R-accounts-3: Roblox may replace the session on the answer itself.
        using var handler = new Handler((_, _) => Task.FromResult(Rotating(Json(Experience("Valid")), "synthetic-rotated-answer")));
        using var resolution = await Resolve(handler, RobloxShareLinkType.ExperienceDetails);
        Assert.Equal(RobloxShareLinkOutcome.Experience, resolution.Outcome); Assert.Equal(5, resolution.PlaceId);
        using (var candidate = resolution.TakeRotatedCredential())
        {
            Assert.NotNull(candidate); Assert.Equal("synthetic-rotated-answer", candidate.CopyValue());
            Assert.InRange(candidate.ExpiresUtc!.Value, DateTimeOffset.UtcNow.AddMinutes(59), DateTimeOffset.UtcNow.AddMinutes(61));
        }
        Assert.Null(resolution.TakeRotatedCredential()); // Moved once; the caller owns it.
        Assert.DoesNotContain("synthetic-rotated", resolution.ToString(), StringComparison.Ordinal);

        // ...or on the CSRF 403 that starts the handshake, even though the answer after it carries none.
        int calls = 0;
        using var handshake = new Handler((_, _) => Task.FromResult(++calls == 1 ? Rotating(Denied("jCcE5fGts0n2"), "synthetic-rotated-csrf") : Json(Server("Valid"))));
        using var server = await Resolve(handshake, RobloxShareLinkType.Server);
        Assert.Equal(RobloxShareLinkOutcome.PrivateServer, server.Outcome); Assert.Equal(2, handshake.Calls);
        using (var candidate = server.TakeRotatedCredential()) Assert.Equal("synthetic-rotated-csrf", candidate?.CopyValue());

        // When both replace it, the newest cookie wins, as in a browser's cookie store.
        calls = 0;
        using var twice = new Handler((_, _) => Task.FromResult(++calls == 1 ? Rotating(Denied("jCcE5fGts0n2"), "synthetic-rotated-first") : Rotating(Json(Experience("Valid")), "synthetic-rotated-second")));
        using var newest = await Resolve(twice, RobloxShareLinkType.ExperienceDetails);
        using (var candidate = newest.TakeRotatedCredential()) Assert.Equal("synthetic-rotated-second", candidate?.CopyValue());

        // A refused answer still hands back the replacement Roblox issued before refusing.
        calls = 0;
        using var refused = new Handler((_, _) => Task.FromResult(++calls == 1 ? Rotating(Denied("jCcE5fGts0n2"), "synthetic-rotated-refused") : new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}", Encoding.UTF8, "application/json") }));
        using var signIn = await Resolve(refused, RobloxShareLinkType.Server);
        Assert.Equal(RobloxShareLinkOutcome.SignInRequired, signIn.Outcome);
        using (var candidate = signIn.TakeRotatedCredential()) Assert.Equal("synthetic-rotated-refused", candidate?.CopyValue());
    }

    [Theory]
    [InlineData(".ROBLOSECURITY=synthetic-rotated; Domain=foreign.invalid")] // Not Roblox's cookie.
    [InlineData(".ROBLOSECURITY=; Max-Age=0")]                                // Roblox deleting the session.
    [InlineData(".ROBLOSECURITY=" + Secret + "; Domain=.roblox.com")]          // The same session again.
    public async Task AnUnusableOrUnchangedSessionCookieLeavesNoCandidateAndTheAnswerStands(string header)
    {
        using var handler = new Handler((_, _) => { var response = Json(Experience("Valid")); response.Headers.Add("Set-Cookie", header); return Task.FromResult(response); });
        using var resolution = await Resolve(handler, RobloxShareLinkType.ExperienceDetails);
        Assert.Equal(RobloxShareLinkOutcome.Experience, resolution.Outcome); Assert.Null(resolution.TakeRotatedCredential());
    }

    private static HttpResponseMessage Rotating(HttpResponseMessage response, string cookie)
    { response.Headers.Add("Set-Cookie", ".ROBLOSECURITY=" + cookie + "; Domain=.roblox.com; Path=/; Secure; HttpOnly; Max-Age=3600"); return response; }

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
