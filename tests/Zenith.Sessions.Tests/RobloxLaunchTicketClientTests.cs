using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// R-api-7 (owner decision 2026-11-08): the direct launch-ticket request. Every request ends at an owned synthetic handler
// or a loopback socket; no secret leaves the test. The wire contract uses Zenith's own User-Agent
// and Roblox's protocol Origin/Referer; redirects and automatic cookie jars remain disabled.
public sealed class RobloxLaunchTicketClientTests
{
    private const string Secret = "synthetic-session-cookie";
    private const string Assertion = "synthetic.client.assertion-value";
    private const string Ticket = "synthetic-launch-ticket+/=never-real";
    private const string AssertionEndpoint = "https://auth.roblox.com/v1/client-assertion/";
    private const string TicketEndpoint = "https://auth.roblox.com/v1/authentication-ticket/";
    private static readonly RobloxShareLinkTokenScope Account = new(Guid.Parse("6f1c2a52-8d0e-4c1b-9a43-0b7d2f1e5c11"), 456);

    [Fact]
    public async Task EachTicketStepUsesTheNewestCookieIncludingTheCsrfRetry()
    {
        int step = 0;
        using var handler = new Handler((request, _) =>
        {
            string expected = step switch { 0 => Secret, 1 => "assertion-replacement", _ => "csrf-replacement" };
            Assert.Equal(".ROBLOSECURITY=" + expected, Assert.Single(request.Headers.GetValues("Cookie")));
            var response = step switch { 0 => Json("{}"), 1 => Denied("retry-token"), _ => Issued(Ticket) };
            if (step < 2) response.Headers.Add("Set-Cookie", ".ROBLOSECURITY=" + (step == 0 ? "assertion-replacement" : "csrf-replacement") + "; Domain=.roblox.com; Path=/; Secure; HttpOnly; Max-Age=3600");
            step++; return Task.FromResult(response);
        });
        using var client = new RobloxLaunchTicketClient(handler);
        using var original = new RobloxSessionCredential(Secret);
        using var result = await client.RequestAsync(original, Account, default);
        Assert.Equal(RobloxLaunchTicketOutcome.Issued, result.Outcome); Assert.Equal(3, step);
        using var rotated = result.TakeRotatedCredential(); Assert.Equal("csrf-replacement", rotated!.CopyValue());
        Assert.Equal(Secret, original.CopyValue()); // The borrowed vault credential is never mutated or disposed here.
    }

    [Theory]
    [InlineData(".ROBLOSECURITY=; Domain=.roblox.com; Max-Age=0")]
    [InlineData(".ROBLOSECURITY=foreign; Domain=example.com")]
    public async Task AnInvalidReplacementStopsBeforeAnotherRequestOrAnIssuedTicketIsAccepted(string cookie)
    {
        using var handler = new Handler((request, _) =>
        {
            var response = request.Method == HttpMethod.Get ? Json("{}") : Issued(Ticket);
            response.Headers.Add("Set-Cookie", cookie); return Task.FromResult(response);
        });
        using var client = new RobloxLaunchTicketClient(handler);
        using var result = await Request(client, Account);
        Assert.NotEqual(RobloxLaunchTicketOutcome.Issued, result.Outcome);
        Assert.Null(result.TakeTicket()); Assert.Null(result.TakeRotatedCredential()); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TicketNegotiationSuppliesTheRobloxProtocolOriginWithZenithsOwnUserAgent()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("https://www.roblox.com/", request.Headers.Referrer?.AbsoluteUri);
            Assert.Equal("https://www.roblox.com", Assert.Single(request.Headers.GetValues("Origin")));
            Assert.Equal(RobloxLaunchTicketClient.UserAgent, Assert.Single(request.Headers.GetValues("User-Agent")));
            return Task.FromResult(request.Method == HttpMethod.Get ? Json("{}") : Issued(Ticket));
        });
        using var client = new RobloxLaunchTicketClient(handler);
        using var result = await Request(client, Account);
        Assert.Equal(RobloxLaunchTicketOutcome.Issued, result.Outcome);
    }

    [Fact]
    public async Task GetsTheAssertionThenPostsTheTicketRequestWithHonestHeadersAndReadsTheTicketHeader()
    {
        var seen = new List<string>();
        using var handler = new Handler(async (request, ct) =>
        {
            seen.Add(request.Method.Method + " " + request.RequestUri!.AbsoluteUri);
            Assert.Equal(".ROBLOSECURITY=" + Secret, Assert.Single(request.Headers.GetValues("Cookie")));
            Assert.Equal(RobloxLaunchTicketClient.UserAgent, Assert.Single(request.Headers.GetValues("User-Agent")));
            Assert.StartsWith("ZenithSessions/", RobloxLaunchTicketClient.UserAgent, StringComparison.Ordinal);
            Assert.Equal("https://www.roblox.com/", request.Headers.Referrer?.AbsoluteUri); Assert.Equal("https://www.roblox.com", Assert.Single(request.Headers.GetValues("Origin")));
            Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
            if (request.Method == HttpMethod.Get)
            {
                Assert.Null(request.Content); Assert.False(request.Headers.Contains("RBXAuthenticationNegotiation"));
                return Json("{\"clientAssertion\":\"" + Assertion + "\"}");
            }
            Assert.Equal("1", Assert.Single(request.Headers.GetValues("RBXAuthenticationNegotiation")));
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            Assert.Equal(Assertion, body.RootElement.GetProperty("clientAssertion").GetString());
            Assert.Single(body.RootElement.EnumerateObject());
            return Issued(Ticket);
        });
        using var client = new RobloxLaunchTicketClient(handler);
        using var result = await Request(client, Account);
        Assert.Equal(RobloxLaunchTicketOutcome.Issued, result.Outcome);
        Assert.Equal(new[] { "GET " + AssertionEndpoint, "POST " + TicketEndpoint }, seen.ToArray());
        using var ticket = result.TakeTicket(); Assert.NotNull(ticket); Assert.Null(result.TakeTicket());
        Assert.Equal(Ticket.Length, ticket!.Length); Assert.False(ticket.IsConsumed);
        string revealed = ""; ticket.Consume(span => revealed = new string(span));
        Assert.Equal(Ticket, revealed); Assert.True(ticket.IsConsumed); Assert.Equal(0, ticket.Length);
        Assert.Throws<ObjectDisposedException>(() => ticket.Consume(_ => { })); // One use.
        Assert.DoesNotContain(Ticket, result.ToString() + ticket + JsonSerializer.Serialize(result.Outcome), StringComparison.Ordinal);
        Assert.Equal(AssertionEndpoint, RobloxLaunchTicketClient.AssertionEndpoint.AbsoluteUri); Assert.Equal(TicketEndpoint, RobloxLaunchTicketClient.TicketEndpoint.AbsoluteUri);
    }

    [Fact]
    public async Task CsrfHandshakeRetriesExactlyOnceAndTheAcceptedTokenIsKeptPerIdentityOnly()
    {
        var roblox = new TokenIssuer("token-1");
        using var client = new RobloxLaunchTicketClient(roblox.Handler);
        using (var first = await Request(client, Account)) Assert.Equal(RobloxLaunchTicketOutcome.Issued, first.Outcome);
        Assert.Equal(new string?[] { null, "token-1" }, roblox.Sent.ToArray()); // One handshake, then the issued token.
        using (var second = await Request(client, Account)) Assert.Equal(RobloxLaunchTicketOutcome.Issued, second.Outcome);
        Assert.Equal(new string?[] { null, "token-1", "token-1" }, roblox.Sent.ToArray()); // A repeat is one POST.
        using (var other = await Request(client, Account with { RobloxUserId = 789 })) Assert.Equal(RobloxLaunchTicketOutcome.Issued, other.Outcome);
        Assert.Equal(new string?[] { null, "token-1" }, roblox.Sent.Skip(3).ToArray()); // Never another identity's token.
        client.ForgetTokens(Account.ProfileId);
        using (await Request(client, Account)) { }
        Assert.Equal(new string?[] { null, "token-1" }, roblox.Sent.Skip(5).ToArray());
        client.ForgetAllTokens();
        using (await Request(client, Account with { RobloxUserId = 789 })) { }
        Assert.Equal(new string?[] { null, "token-1" }, roblox.Sent.Skip(7).ToArray());

        using var stubborn = new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Get ? Json("{\"clientAssertion\":\"" + Assertion + "\"}") : Denied("again")));
        using var stubbornClient = new RobloxLaunchTicketClient(stubborn);
        using var refused = await Request(stubbornClient, Account);
        Assert.Equal(RobloxLaunchTicketOutcome.Refused, refused.Outcome); Assert.Equal(3, stubborn.Calls); // GET, POST, one retry, never a third POST.
        await Assert.ThrowsAsync<ArgumentException>(() => Request(client, default));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, false, RobloxLaunchTicketOutcome.SignInRequired)]
    [InlineData(HttpStatusCode.Forbidden, false, RobloxLaunchTicketOutcome.Refused)]
    [InlineData(HttpStatusCode.Forbidden, true, RobloxLaunchTicketOutcome.Challenge)]
    [InlineData(HttpStatusCode.TooManyRequests, false, RobloxLaunchTicketOutcome.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, false, RobloxLaunchTicketOutcome.Unavailable)]
    [InlineData(HttpStatusCode.OK, false, RobloxLaunchTicketOutcome.Unavailable)] // 200 without a ticket header is no ticket.
    public async Task TicketRefusalsAreTypedOutcomesAndNeverGuessed(HttpStatusCode status, bool challenge, RobloxLaunchTicketOutcome expected)
    {
        using var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Json("{\"clientAssertion\":\"" + Assertion + "\"}"));
            var response = new HttpResponseMessage(status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            if (challenge) { response.Headers.Add("rblx-challenge-id", "synthetic"); response.Headers.Add("rblx-challenge-type", "captcha"); }
            return Task.FromResult(response);
        });
        using var client = new RobloxLaunchTicketClient(handler);
        using var result = await Request(client, Account);
        Assert.Equal(expected, result.Outcome); Assert.Null(result.TakeTicket()); Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, RobloxLaunchTicketOutcome.SignInRequired)]
    [InlineData(HttpStatusCode.TooManyRequests, RobloxLaunchTicketOutcome.RateLimited)]
    public async Task ARefusedSessionOrRateLimitOnTheAssertionStopsBeforeTheTicketRequest(HttpStatusCode status, RobloxLaunchTicketOutcome expected)
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        });
        using var client = new RobloxLaunchTicketClient(handler);
        using var result = await Request(client, Account);
        Assert.Equal(expected, result.Outcome); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("server-error")]
    [InlineData("malformed")]
    [InlineData("duplicate-key")]
    public async Task AnUnusableAssertionStillRequestsTheTicketWithAnEmptyBodyAsTheWebsiteDoes(string failure)
    {
        string? posted = null;
        using var handler = new Handler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get) return failure switch
            {
                "server-error" => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}", Encoding.UTF8, "application/json") },
                "duplicate-key" => Json("{\"clientAssertion\":\"a\",\"clientAssertion\":\"b\"}"),
                _ => Json("{\"clientAssertion\":12}")
            };
            posted = await request.Content!.ReadAsStringAsync(ct);
            return Issued(Ticket);
        });
        using var client = new RobloxLaunchTicketClient(handler);
        using var result = await Request(client, Account);
        Assert.Equal(RobloxLaunchTicketOutcome.Issued, result.Outcome); Assert.Equal("{}", posted); Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("two")]
    [InlineData("oversized")]
    [InlineData("cookie")]
    public async Task AnUnreadableTicketHeaderIsUnavailableNeverAGuess(string kind)
    {
        using var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Json("{\"clientAssertion\":\"" + Assertion + "\"}"));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("", Encoding.UTF8, "application/json") };
            switch (kind)
            {
                case "two": response.Headers.Add("rbx-authentication-ticket", "one"); response.Headers.Add("rbx-authentication-ticket", "two"); break;
                case "oversized": response.Headers.Add("rbx-authentication-ticket", new string('a', ProtectedLaunchTicket.MaximumLength + 1)); break;
                case "cookie": response.Headers.Add("rbx-authentication-ticket", ".ROBLOSECURITY=not-a-ticket"); break;
                case "": break;
                default: response.Headers.TryAddWithoutValidation("rbx-authentication-ticket", kind); break;
            }
            return Task.FromResult(response);
        });
        using var client = new RobloxLaunchTicketClient(handler);
        using var result = await Request(client, Account);
        Assert.Equal(RobloxLaunchTicketOutcome.Unavailable, result.Outcome); Assert.Null(result.TakeTicket());
    }

    [Fact]
    public async Task ARotatedSessionCookieFromEitherResponseIsHandedBackUnverifiedAndNeverKeptHere()
    {
        using var handler = new Handler((request, _) =>
        {
            var response = request.Method == HttpMethod.Get ? Json("{\"clientAssertion\":\"" + Assertion + "\"}") : Issued(Ticket);
            if (request.Method == HttpMethod.Get) response.Headers.Add("Set-Cookie", ".ROBLOSECURITY=rotated-synthetic; Domain=.roblox.com; Path=/; Secure; HttpOnly; Max-Age=3600");
            return Task.FromResult(response);
        });
        using var client = new RobloxLaunchTicketClient(handler);
        using var result = await Request(client, Account);
        Assert.Equal(RobloxLaunchTicketOutcome.Issued, result.Outcome);
        using var rotated = result.TakeRotatedCredential(); Assert.NotNull(rotated); Assert.Equal("rotated-synthetic", rotated!.CopyValue()); Assert.Null(result.TakeRotatedCredential());
        using var again = await Request(client, Account);
        Assert.Equal(RobloxLaunchTicketOutcome.Issued, again.Outcome); // The replacement was handed back, not sent on the next request.
    }

    [Fact]
    public async Task TransportFailuresAndTimeoutsAreUnavailableWhileTheCallersOwnCancellationThrows()
    {
        using var failing = new Handler((_, _) => throw new HttpRequestException("synthetic transport failure"));
        using (var client = new RobloxLaunchTicketClient(failing))
        using (var result = await Request(client, Account)) Assert.Equal(RobloxLaunchTicketOutcome.Unavailable, result.Outcome);
        using var slow = new Handler(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Json("{}"); });
        using (var client = new RobloxLaunchTicketClient(slow, TimeSpan.FromMilliseconds(200)))
        using (var result = await Request(client, Account)) Assert.Equal(RobloxLaunchTicketOutcome.Unavailable, result.Outcome);
        using (var client = new RobloxLaunchTicketClient(slow))
        {
            using var stop = new CancellationTokenSource(); using var credential = new RobloxSessionCredential(Secret);
            var work = client.RequestAsync(credential, Account, stop.Token); stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        }
    }

    [Fact]
    public async Task TheProductionHandlerFollowsNoRedirectAndKeepsNoCookieJarOverARealSocket()
    {
        // A redirect answer to the assertion is not followed (the second canned response is never collected), and the
        // ticket request still carries only the saved session cookie, never a cookie a response tried to set.
        await using var server = new LoopbackHttpServer(
            LoopbackHttpServer.Response("302 Found", "", "Location: /v1/somewhere-else/", "Set-Cookie: RBXEventTrackerV2=synthetic-tracker; Path=/"),
            LoopbackHttpServer.Response("200 OK", "", "Content-Type: application/json", "rbx-authentication-ticket: " + Ticket),
            LoopbackHttpServer.Response("200 OK", "{\"unexpected\":true}", "Content-Type: application/json"));
        using var client = new RobloxLaunchTicketClient(server.BaseAddress);
        using var result = await Request(client, Account);
        Assert.Equal(RobloxLaunchTicketOutcome.Issued, result.Outcome);
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal("GET /v1/client-assertion/ HTTP/1.1", LoopbackHttpServer.RequestLine(server.Requests[0]));
        Assert.Equal("POST /v1/authentication-ticket/ HTTP/1.1", LoopbackHttpServer.RequestLine(server.Requests[1]));
        Assert.All(server.Requests, sent =>
        {
            Assert.Equal(".ROBLOSECURITY=" + Secret, Assert.Single(LoopbackHttpServer.HeaderValues(sent, "Cookie")));
            Assert.Equal(RobloxLaunchTicketClient.UserAgent, Assert.Single(LoopbackHttpServer.HeaderValues(sent, "User-Agent")));
            Assert.Equal("https://www.roblox.com/", Assert.Single(LoopbackHttpServer.HeaderValues(sent, "Referer"))); Assert.Equal("https://www.roblox.com", Assert.Single(LoopbackHttpServer.HeaderValues(sent, "Origin")));
            Assert.DoesNotContain("synthetic-tracker", sent, StringComparison.Ordinal);
        });
        Assert.Throws<ArgumentException>(() => new RobloxLaunchTicketClient(new Uri("http://auth.roblox.com/")));
    }

    [Fact]
    public void AProtectedTicketIsVisibleAsciiOnlyBoundedZeroedOnDisposeAndNeverACookie()
    {
        Assert.Throws<ArgumentException>(() => new ProtectedLaunchTicket(""));
        Assert.Throws<ArgumentException>(() => new ProtectedLaunchTicket("has space"));
        Assert.Throws<ArgumentException>(() => new ProtectedLaunchTicket("tab\there"));
        Assert.Throws<ArgumentException>(() => new ProtectedLaunchTicket(new string('a', ProtectedLaunchTicket.MaximumLength + 1)));
        Assert.Throws<ArgumentException>(() => new ProtectedLaunchTicket("x.ROBLOSECURITY=y"));
        Assert.Throws<ArgumentException>(() => new ProtectedLaunchTicket("_|WARNING:-DO-NOT-SHARE-THIS"));
        var ticket = new ProtectedLaunchTicket(new string('a', ProtectedLaunchTicket.MaximumLength));
        Assert.Equal(ProtectedLaunchTicket.MaximumLength, ticket.Length); Assert.Equal("[launch ticket]", ticket.ToString());
        ticket.Dispose(); Assert.True(ticket.IsConsumed); Assert.Throws<ObjectDisposedException>(() => ticket.Consume(_ => { }));
        Assert.Throws<ArgumentException>(() => new RobloxLaunchTicketResult(RobloxLaunchTicketOutcome.Issued)); // Issued needs a ticket.
        Assert.Throws<ArgumentException>(() => new RobloxLaunchTicketResult(RobloxLaunchTicketOutcome.Refused, new ProtectedLaunchTicket("t")));
    }

    private static async Task<RobloxLaunchTicketResult> Request(RobloxLaunchTicketClient client, RobloxShareLinkTokenScope scope)
    {
        using var credential = new RobloxSessionCredential(Secret);
        return await client.RequestAsync(credential, scope, default);
    }
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Issued(string ticket)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("", Encoding.UTF8, "application/json") };
        response.Headers.Add("rbx-authentication-ticket", ticket); return response;
    }
    private static HttpResponseMessage Denied(string token)
    {
        var denied = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"errors\":[{\"code\":0,\"message\":\"Token Validation Failed\"}]}", Encoding.UTF8, "application/json") };
        denied.Headers.Add("x-csrf-token", token); return denied;
    }
    // A synthetic Roblox that answers the assertion GET, and accepts a ticket POST only with its current token, otherwise issuing it with a 403.
    private sealed class TokenIssuer
    {
        public string Current;
        public List<string?> Sent { get; } = [];
        public Handler Handler { get; }
        public TokenIssuer(string current)
        {
            Current = current;
            Handler = new Handler((request, _) =>
            {
                if (request.Method == HttpMethod.Get) return Task.FromResult(Json("{\"clientAssertion\":\"" + Assertion + "\"}"));
                string? sent = request.Headers.TryGetValues("X-CSRF-TOKEN", out var values) ? string.Join(",", values) : null;
                Sent.Add(sent);
                return Task.FromResult(sent != Current ? Denied(Current) : Issued(Ticket));
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
