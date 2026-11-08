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
    private const string V2Endpoint = "https://apis.roblox.com/deeplinks/v2/resolve";
    private const string Job = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private static readonly RobloxShareLinkTokenScope Account = new(Guid.Parse("6f1c2a52-8d0e-4c1b-9a43-0b7d2f1e5c11"), 456);

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

    // The token Roblox accepted for an account is kept in memory, so that account's next resolve is one request, not two.
    [Fact]
    public async Task ASecondResolveForTheSameAccountSendsItsKeptTokenInOneRequest()
    {
        var roblox = new TokenIssuer("token-1");
        using var client = new RobloxShareLinkClient(roblox.Handler);
        using (var first = await Resolve(client, Account)) Assert.Equal(RobloxShareLinkOutcome.Experience, first.Outcome);
        Assert.Equal(2, roblox.Handler.Calls); // The first resolve still needs the handshake.
        using (var second = await Resolve(client, Account, RobloxShareLinkType.DeepLinkV2)) Assert.Equal(RobloxShareLinkOutcome.Experience, second.Outcome);
        Assert.Equal(3, roblox.Handler.Calls); // Exactly one request, to either endpoint.
        Assert.Equal(new string?[] { null, "token-1", "token-1" }, roblox.Sent.ToArray());
    }

    [Fact]
    public async Task AKeptTokenIsNeverSentForAnotherAccountOrProfile()
    {
        var roblox = new TokenIssuer("token-1");
        using var client = new RobloxShareLinkClient(roblox.Handler);
        foreach (var scope in new[] { Account, Account with { RobloxUserId = 789 }, Account with { ProfileId = Guid.NewGuid() } })
            using (var resolution = await Resolve(client, scope)) Assert.Equal(RobloxShareLinkOutcome.Experience, resolution.Outcome);
        Assert.Equal(new string?[] { null, "token-1", null, "token-1", null, "token-1" }, roblox.Sent.ToArray());
        await Assert.ThrowsAsync<ArgumentException>(() => Resolve(client, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Resolve(client, Account with { RobloxUserId = 0 }));
        Assert.Equal(6, roblox.Handler.Calls); // An unscoped resolve never reaches Roblox.
    }

    [Fact]
    public async Task ARefusedKeptTokenIsReplacedWithTheOneRetryAndAFailureKeepsNothing()
    {
        var roblox = new TokenIssuer("token-1");
        using var client = new RobloxShareLinkClient(roblox.Handler);
        using (await Resolve(client, Account)) { }
        roblox.Current = "token-2"; // Roblox rotated the session's token.
        using (var rotated = await Resolve(client, Account)) Assert.Equal(RobloxShareLinkOutcome.Experience, rotated.Outcome);
        using (await Resolve(client, Account)) { }
        Assert.Equal(new string?[] { null, "token-1", "token-1", "token-2", "token-2" }, roblox.Sent.ToArray());

        roblox.Refuse = HttpStatusCode.Unauthorized; // The session ended: the token Roblox issued is not kept.
        roblox.Current = "token-3";
        using (var ended = await Resolve(client, Account)) Assert.Equal(RobloxShareLinkOutcome.SignInRequired, ended.Outcome);
        roblox.Refuse = null;
        using (await Resolve(client, Account)) { }
        Assert.Equal(new string?[] { null, "token-1", "token-1", "token-2", "token-2", "token-2", "token-3", null, "token-3" }, roblox.Sent.ToArray());
    }

    [Fact]
    public async Task ForgettingDropsAProfilesTokensOrAllOfThemEvenDuringAResolve()
    {
        var roblox = new TokenIssuer("token-1"); var other = Account with { ProfileId = Guid.NewGuid() };
        using var client = new RobloxShareLinkClient(roblox.Handler);
        using (await Resolve(client, Account)) { }
        using (await Resolve(client, other)) { }
        client.ForgetTokens(Account.ProfileId); // Sign-out of one profile.
        using (await Resolve(client, other)) { }
        using (await Resolve(client, Account)) { }
        Assert.Equal(new string?[] { null, "token-1", null, "token-1", "token-1", null, "token-1" }, roblox.Sent.ToArray());

        client.ForgetAllTokens(); // Lock or Panic.
        using (await Resolve(client, other)) { }
        Assert.Equal(new string?[] { null, "token-1" }, roblox.Sent.Skip(7).ToArray());

        client.ForgetAllTokens();
        roblox.OnAccepted = client.ForgetAllTokens; // Lock or Panic lands while Roblox is answering.
        using (var late = await Resolve(client, Account)) Assert.Equal(RobloxShareLinkOutcome.Experience, late.Outcome);
        roblox.OnAccepted = null;
        using (await Resolve(client, Account)) { }
        Assert.Equal(new string?[] { null, "token-1", null, "token-1" }, roblox.Sent.Skip(9).ToArray());
    }

    // Memory stays bounded: a full set keeps working for known accounts, and one more account drops every kept token.
    [Fact]
    public async Task KeptTokensAreBoundedAndOneMoreAccountDropsThemAll()
    {
        var roblox = new TokenIssuer("token-1");
        using var client = new RobloxShareLinkClient(roblox.Handler);
        var scopes = Enumerable.Range(1, RobloxShareLinkClient.MaximumKeptTokens).Select(id => new RobloxShareLinkTokenScope(Guid.NewGuid(), id)).ToArray();
        foreach (var scope in scopes) using (await Resolve(client, scope)) { }
        int calls = roblox.Handler.Calls;
        using (await Resolve(client, scopes[0])) { }
        Assert.Equal(calls + 1, roblox.Handler.Calls); // Still kept at the limit.
        using (await Resolve(client, Account)) { }
        using (await Resolve(client, scopes[0])) { }
        Assert.Equal(new string?[] { null, "token-1", null, "token-1" }, roblox.Sent.Skip(calls + 1).ToArray());
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

    // A repeated key makes the response ambiguous (which linkCode or status is meant?), so it is Unavailable, never
    // the last value. The real endpoint is not known to send repeated keys.
    [Theory]
    [InlineData(RobloxShareLinkType.Server, "{\"privateServerInviteData\":{\"status\":\"Valid\",\"placeId\":1,\"universeId\":2,\"linkCode\":\"12345678901234567890\",\"linkCode\":\"" + LinkCode + "\"}}")]
    [InlineData(RobloxShareLinkType.Server, "{\"privateServerInviteData\":{\"status\":\"Invalid\",\"status\":\"Valid\",\"placeId\":1,\"universeId\":2,\"linkCode\":\"" + LinkCode + "\"}}")]
    [InlineData(RobloxShareLinkType.ExperienceInvite, "{\"experienceInviteData\":{\"status\":\"Valid\",\"placeId\":1818,\"instanceId\":\"" + Job + "\",\"instanceId\":\"" + Job + "\"}}")]
    [InlineData(RobloxShareLinkType.DeepLinkV2, "{\"linkType\":\"Experience\",\"status\":\"Valid\",\"targetId\":1,\"targetId\":1818}")]
    public async Task DuplicateFieldsAreUnavailable(RobloxShareLinkType type, string json)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(json)));
        using var resolution = await Resolve(handler, type);
        Assert.Equal(RobloxShareLinkOutcome.Unavailable, resolution.Outcome);
        Assert.Null(resolution.PrivateServerCode); Assert.Null(resolution.PlaceId); Assert.Null(resolution.JobId); Assert.Equal(1, handler.Calls);
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

    // Friend invites: a Valid invite with a well-formed instanceId is a request for that exact server (not arrival).
    [Theory]
    [InlineData(RobloxShareLinkType.ExperienceInvite, "ExperienceInvite", "experienceInviteData")]
    [InlineData(RobloxShareLinkType.NotificationExperienceInvite, "NotificationExperienceInvite", "notificationExperienceInviteData")]
    public async Task AValidInviteWithAServerResolvesToThatServer(RobloxShareLinkType type, string typeName, string key)
    {
        using var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal(Endpoint, request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(ShareCode, body.RootElement.GetProperty("linkId").GetString()); Assert.Equal(typeName, body.RootElement.GetProperty("linkType").GetString());
            return Json("{\"" + key + "\":{\"status\":\"Valid\",\"placeId\":1818,\"instanceId\":\"" + Job.ToUpperInvariant() + "\",\"inviterId\":9,\"launchData\":\"synthetic-launch-data\"}}");
        });
        using var resolution = await Resolve(handler, type);
        Assert.Equal(RobloxShareLinkOutcome.ServerInstance, resolution.Outcome); Assert.Equal(1818, resolution.PlaceId);
        Assert.Equal(Job, resolution.JobId); Assert.Null(resolution.UniverseId); Assert.Null(resolution.OwnerUserId); Assert.Null(resolution.PrivateServerCode);
        Assert.DoesNotContain("synthetic-launch-data", resolution.ToString(), StringComparison.Ordinal); Assert.Equal(1, handler.Calls);
    }

    // As on Roblox's web page, an invite whose server cannot be used still opens its game; it never names a server.
    [Theory]
    [InlineData("{\"experienceInviteData\":{\"status\":\"Expired\",\"placeId\":1818,\"instanceId\":\"" + Job + "\"}}")]
    [InlineData("{\"experienceInviteData\":{\"status\":\"InviterNotInExperience\",\"placeId\":1818,\"instanceId\":\"" + Job + "\"}}")]
    [InlineData("{\"experienceInviteData\":{\"status\":\"Valid\",\"placeId\":1818}}")]
    [InlineData("{\"experienceInviteData\":{\"status\":\"Valid\",\"placeId\":1818,\"instanceId\":null}}")]
    [InlineData("{\"experienceInviteData\":{\"status\":\"Valid\",\"placeId\":1818,\"instanceId\":\"\"}}")]
    public async Task AnInviteWithoutAUsableServerFallsBackToItsGame(string json)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(json)));
        using var resolution = await Resolve(handler, RobloxShareLinkType.ExperienceInvite);
        Assert.Equal(RobloxShareLinkOutcome.Experience, resolution.Outcome); Assert.Equal(1818, resolution.PlaceId); Assert.Null(resolution.JobId);
    }

    [Theory]
    [InlineData("{\"experienceInviteData\":{\"status\":\"Valid\",\"placeId\":1818,\"instanceId\":\"not-a-server\"}}", RobloxShareLinkOutcome.Unavailable)]
    [InlineData("{\"experienceInviteData\":{\"status\":\"Valid\",\"placeId\":1818,\"instanceId\":\"00000000-0000-0000-0000-000000000000\"}}", RobloxShareLinkOutcome.Unavailable)]
    [InlineData("{\"experienceInviteData\":{\"status\":\"Valid\",\"placeId\":1818,\"instanceId\":42}}", RobloxShareLinkOutcome.Unavailable)]
    [InlineData("{\"experienceInviteData\":{\"status\":\"Valid\",\"instanceId\":\"" + Job + "\"}}", RobloxShareLinkOutcome.Unavailable)]
    [InlineData("{\"experienceInviteData\":{\"status\":\"Valid\",\"placeId\":0,\"instanceId\":\"" + Job + "\"}}", RobloxShareLinkOutcome.Unavailable)]
    [InlineData("{\"experienceInviteData\":{\"status\":\"Expired\"}}", RobloxShareLinkOutcome.Expired)]
    [InlineData("{\"experienceInviteData\":{\"status\":\"Invalid\"}}", RobloxShareLinkOutcome.Invalid)]
    [InlineData("{\"experienceInviteData\":{\"status\":\"Unexpected\",\"placeId\":1818}}", RobloxShareLinkOutcome.Unavailable)]
    [InlineData("{\"experienceDetailsInviteData\":{\"status\":\"Valid\",\"placeId\":1818,\"universeId\":6}}", RobloxShareLinkOutcome.Unavailable)]
    public async Task MalformedInvitesAreNeverGuessed(string json, RobloxShareLinkOutcome outcome)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(json)));
        using var resolution = await Resolve(handler, RobloxShareLinkType.ExperienceInvite);
        Assert.Equal(outcome, resolution.Outcome); Assert.Null(resolution.PlaceId); Assert.Null(resolution.JobId);
    }

    // share?code=…&v=v2 links resolve at deeplinks/v2/resolve with only the code; only Experience links name a place.
    [Fact]
    public async Task Version2LinksPostOnlyTheCodeToTheirOwnEndpoint()
    {
        int calls = 0;
        using var handler = new Handler(async (request, ct) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal(V2Endpoint, request.RequestUri!.AbsoluteUri);
            Assert.Equal(".ROBLOSECURITY=" + Secret, Assert.Single(request.Headers.GetValues("Cookie")));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(ShareCode, body.RootElement.GetProperty("linkId").GetString()); Assert.Single(body.RootElement.EnumerateObject());
            if (calls == 1) return Denied("v2token");
            Assert.Equal("v2token", Assert.Single(request.Headers.GetValues("X-CSRF-TOKEN")));
            return Json("{\"linkType\":\"Experience\",\"status\":\"Valid\",\"targetId\":1818,\"customData\":\"synthetic-launch-data\"}");
        });
        using var resolution = await Resolve(handler, RobloxShareLinkType.DeepLinkV2);
        Assert.Equal(RobloxShareLinkOutcome.Experience, resolution.Outcome); Assert.Equal(1818, resolution.PlaceId);
        Assert.Null(resolution.UniverseId); Assert.Null(resolution.JobId); Assert.Equal(2, handler.Calls);
        Assert.Equal(V2Endpoint, RobloxShareLinkClient.V2Endpoint.AbsoluteUri);
    }

    [Theory]
    [InlineData("{\"linkType\":\"Experience\",\"targetId\":1818}", RobloxShareLinkOutcome.Experience)]
    [InlineData("{\"linkType\":\"StudioEdit\",\"status\":\"Valid\",\"targetId\":1818}", RobloxShareLinkOutcome.Invalid)]
    [InlineData("{\"linkType\":\"Experience\",\"status\":\"Invalid\",\"targetId\":1818}", RobloxShareLinkOutcome.Invalid)]
    [InlineData("{\"linkType\":\"Experience\",\"status\":\"Expired\",\"targetId\":1818}", RobloxShareLinkOutcome.Expired)]
    [InlineData("{\"linkType\":\"Experience\",\"status\":\"Unexpected\",\"targetId\":1818}", RobloxShareLinkOutcome.Unavailable)]
    [InlineData("{\"linkType\":\"Experience\",\"status\":\"Valid\",\"targetId\":0}", RobloxShareLinkOutcome.Unavailable)]
    [InlineData("{\"linkType\":\"Experience\",\"status\":\"Valid\",\"targetId\":\"1818\"}", RobloxShareLinkOutcome.Unavailable)]
    [InlineData("{\"linkType\":\"Experience\",\"status\":\"Valid\"}", RobloxShareLinkOutcome.Unavailable)]
    [InlineData("{\"status\":\"Valid\",\"targetId\":1818}", RobloxShareLinkOutcome.Unavailable)]
    [InlineData("[]", RobloxShareLinkOutcome.Unavailable)]
    public async Task Version2ResponsesAreMappedStrictly(string json, RobloxShareLinkOutcome outcome)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(json)));
        using var resolution = await Resolve(handler, RobloxShareLinkType.DeepLinkV2);
        Assert.Equal(outcome, resolution.Outcome);
        Assert.Equal(outcome == RobloxShareLinkOutcome.Experience ? 1818L : null, resolution.PlaceId); Assert.Null(resolution.JobId);
    }

    [Fact]
    public void AResolutionKeepsOnlyACanonicalServerForAServerOutcome()
    {
        using var server = new RobloxShareLinkResolution(RobloxShareLinkOutcome.ServerInstance, 1818, jobId: Job.ToUpperInvariant());
        Assert.Equal(Job, server.JobId); Assert.Contains("Place = 1818", server.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new RobloxShareLinkResolution(RobloxShareLinkOutcome.ServerInstance, 1818, jobId: "not-a-server"));
        Assert.Throws<ArgumentException>(() => new RobloxShareLinkResolution(RobloxShareLinkOutcome.ServerInstance, 1818));
        Assert.Throws<ArgumentException>(() => new RobloxShareLinkResolution(RobloxShareLinkOutcome.ServerInstance, null, jobId: Job));
        Assert.Throws<ArgumentException>(() => new RobloxShareLinkResolution(RobloxShareLinkOutcome.Experience, 1818, jobId: Job));
    }

    [Fact]
    public async Task CancellationAndTimeoutNeverLeakTheCodes()
    {
        using var slow = new Handler(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return Json(Experience("Valid")); });
        using var client = new RobloxShareLinkClient(slow, TimeSpan.FromMilliseconds(150));
        using var credential = new RobloxSessionCredential(Secret);
        using var link = new RobloxShareLink(new ProtectedShareCode(ShareCode), RobloxShareLinkType.ExperienceDetails);
        using var timedOut = await client.ResolveAsync(link, credential, Account, default);
        Assert.Equal(RobloxShareLinkOutcome.Unavailable, timedOut.Outcome);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ResolveAsync(link, credential, Account, stop.Token));
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

    // R-research-11: the production handler, over a real loopback socket, follows no redirect, keeps no cookie jar and
    // refuses an oversized header. Only the origin moves to the loopback server; the paths are the production ones.
    [Fact]
    public async Task TheProductionHandlerOverARealSocketFollowsNoRedirectKeepsNoCookieAndRefusesAnOversizedHeader()
    {
        string answer = LoopbackHttpServer.Response("200 OK", Experience("Valid"), "Content-Type: application/json");

        // A real 302 is an answer, not a hop: the success waiting at its Location is never collected.
        await using (var server = new LoopbackHttpServer(LoopbackHttpServer.Response("302 Found", "", "Location: /followed"), answer))
        {
            using var client = new RobloxShareLinkClient(server.BaseAddress);
            using (var redirected = await Resolve(client, Account)) Assert.Equal(RobloxShareLinkOutcome.Unavailable, redirected.Outcome);
            Assert.Equal("POST /sharelinks/v1/resolve-link HTTP/1.1", LoopbackHttpServer.RequestLine(Assert.Single(server.Requests)));
        }

        // A cookie set during the CSRF handshake is neither stored nor sent with the retry; only the session cookie goes.
        await using (var server = new LoopbackHttpServer(
            LoopbackHttpServer.Response("403 Forbidden", "{}", "Content-Type: application/json", "x-csrf-token: synthetic-csrf", "Set-Cookie: RBXEventTrackerV2=synthetic-tracker; Path=/"),
            answer))
        {
            using var client = new RobloxShareLinkClient(server.BaseAddress);
            using (var resolution = await Resolve(client, Account))
            { Assert.Equal(RobloxShareLinkOutcome.Experience, resolution.Outcome); Assert.Null(resolution.TakeRotatedCredential()); }
            Assert.Equal(2, server.Requests.Count);
            Assert.Equal("synthetic-csrf", Assert.Single(LoopbackHttpServer.HeaderValues(server.Requests[1], "X-CSRF-TOKEN")));
            Assert.All(server.Requests, sent =>
            {
                Assert.Equal(".ROBLOSECURITY=" + Secret, Assert.Single(LoopbackHttpServer.HeaderValues(sent, "Cookie")));
                Assert.DoesNotContain("synthetic-tracker", sent, StringComparison.Ordinal);
            });
        }

        // A header past the handler's limit (64 KiB by default) fails the request instead of being read.
        await using (var server = new LoopbackHttpServer(LoopbackHttpServer.Response("200 OK", "{\"linkType\":\"Experience\",\"status\":\"Valid\",\"targetId\":1818}",
            "Content-Type: application/json", "X-Padding: " + new string('a', 70 * 1024))))
        {
            using var client = new RobloxShareLinkClient(server.BaseAddress);
            using (var oversized = await Resolve(client, Account, RobloxShareLinkType.DeepLinkV2)) Assert.Equal(RobloxShareLinkOutcome.Unavailable, oversized.Outcome);
            Assert.Equal("POST /deeplinks/v2/resolve HTTP/1.1", LoopbackHttpServer.RequestLine(Assert.Single(server.Requests)));
        }

        // The seam never sends a session cookie over plain http to another host, and the production origin is unchanged.
        Assert.Throws<ArgumentException>(() => new RobloxShareLinkClient(new Uri("http://apis.roblox.com/")));
        Assert.Throws<ArgumentException>(() => new RobloxShareLinkClient(new Uri("https://127.0.0.1/")));
        Assert.Equal(Endpoint, RobloxShareLinkClient.Endpoint.AbsoluteUri); Assert.Equal(V2Endpoint, RobloxShareLinkClient.V2Endpoint.AbsoluteUri);
    }

    private static HttpResponseMessage Rotating(HttpResponseMessage response, string cookie)
    { response.Headers.Add("Set-Cookie", ".ROBLOSECURITY=" + cookie + "; Domain=.roblox.com; Path=/; Secure; HttpOnly; Max-Age=3600"); return response; }

    private static async Task<RobloxShareLinkResolution> Resolve(Handler handler, RobloxShareLinkType type)
    {
        using var client = new RobloxShareLinkClient(handler);
        return await Resolve(client, Account, type);
    }
    private static async Task<RobloxShareLinkResolution> Resolve(RobloxShareLinkClient client, RobloxShareLinkTokenScope scope, RobloxShareLinkType type = RobloxShareLinkType.ExperienceDetails)
    {
        using var credential = new RobloxSessionCredential(Secret);
        using var link = new RobloxShareLink(new ProtectedShareCode(ShareCode), type);
        return await client.ResolveAsync(link, credential, scope, default);
    }
    private static string Experience(string status) => "{\"experienceDetailsInviteData\":{\"status\":\"" + status + "\",\"placeId\":5,\"universeId\":6},\"privateServerInviteData\":null}";
    private static string Server(string status) => "{\"experienceDetailsInviteData\":null,\"privateServerInviteData\":{\"status\":\"" + status + "\",\"placeId\":5,\"universeId\":6,\"ownerUserId\":7,\"linkCode\":\"" + LinkCode + "\"}}";
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Denied(string token)
    {
        var denied = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"errors\":[{\"code\":0,\"message\":\"XSRF token invalid\"}]}", Encoding.UTF8, "application/json") };
        denied.Headers.Add("x-csrf-token", token); return denied;
    }
    // A synthetic Roblox that accepts only its current token and otherwise issues it with a 403, like the real handshake.
    private sealed class TokenIssuer
    {
        public string Current;
        public HttpStatusCode? Refuse;
        public Action? OnAccepted;
        public List<string?> Sent { get; } = [];
        public Handler Handler { get; }
        public TokenIssuer(string current)
        {
            Current = current;
            Handler = new Handler((request, _) =>
            {
                string? sent = request.Headers.TryGetValues("X-CSRF-TOKEN", out var values) ? string.Join(",", values) : null;
                Sent.Add(sent);
                if (sent != Current) return Task.FromResult(Denied(Current));
                if (Refuse is { } status) return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
                OnAccepted?.Invoke();
                return Task.FromResult(Json(request.RequestUri!.AbsoluteUri == V2Endpoint ? "{\"linkType\":\"Experience\",\"status\":\"Valid\",\"targetId\":5}" : Experience("Valid")));
            });
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        private int _calls; public int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Interlocked.Increment(ref _calls); return handle(request, ct); }
    }
}
