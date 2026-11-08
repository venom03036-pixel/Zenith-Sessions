using System.Net;
using System.Net.Http;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class RobloxAccountDataClientTests
{
    private const string Cookie = "synthetic-account-observation-cookie";
    private sealed class Handler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => reply(request, ++Calls, ct);
    }
    private static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json) };
    private static async Task<RobloxAccountResponse> Read(RobloxAccountRead kind, string json, long? target = null)
    {
        using var client = new RobloxAccountDataClient(new Handler((_, _, _) => Task.FromResult(Reply(json))));
        using var cookie = new RobloxSessionCredential(Cookie); return await client.ReadAsync(kind, 456, target, cookie, default);
    }
    [Theory]
    [InlineData(0, RobloxReportedPresence.Offline)]
    [InlineData(1, RobloxReportedPresence.Online)]
    [InlineData(2, RobloxReportedPresence.InGame)]
    [InlineData(3, RobloxReportedPresence.InStudio)]
    public async Task PresenceUsesOnlyTheSelectedAccountsIdentity(int state, RobloxReportedPresence expected)
    {
        var handler = new Handler(async (request, _, ct) =>
        {
            Assert.Equal("https://presence.roblox.com/v1/presence/users", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("{\"userIds\":[456]}", await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(".ROBLOSECURITY=" + Cookie, request.Headers.GetValues("Cookie").Single());
            Assert.Equal("ZenithSessions/1.0", request.Headers.UserAgent.ToString());
            Assert.Null(request.Headers.Referrer); Assert.False(request.Headers.Contains("Origin"));
            return Reply($$"""{"userPresences":[{"userId":456,"userPresenceType":{{state}},"placeId":123,"gameId":"8c782b5d-2121-4bfa-9dca-435b63316b05"}]}""");
        });
        using var client = new RobloxAccountDataClient(handler); using var cookie = new RobloxSessionCredential(Cookie);
        using var result = await client.ReadAsync(RobloxAccountRead.Presence, 456, null, cookie, default);
        Assert.Equal(RobloxAccountOutcome.Success, result.Outcome); Assert.Equal(expected, result.Observation!.Presence!.State);
        Assert.Equal(state == 2 ? 123L : null, result.Observation.Presence.PlaceId);
        Assert.DoesNotContain("123", result.Observation.ToString()); Assert.Equal(1, handler.Calls);
    }
    [Theory]
    [InlineData("{\"userPresences\":[{\"userId\":999,\"userPresenceType\":2}]}")]
    [InlineData("{\"userPresences\":[{\"userId\":456,\"userPresenceType\":4}]}")]
    [InlineData("{\"userPresences\":[{\"userId\":456,\"userPresenceType\":null}]}")]
    [InlineData("{\"userPresences\":[]}")]
    [InlineData("{\"userPresences\":[{\"userId\":456,\"userPresenceType\":1},{\"userId\":999,\"userPresenceType\":2}]}")]
    [InlineData("{\"userPresences\":[{\"userId\":456,\"userId\":999,\"userPresenceType\":2}]}")]
    public async Task PresenceNeverAcceptsForeignAmbiguousOrUnknownStates(string json)
    { using var result = await Read(RobloxAccountRead.Presence, json); Assert.Equal(RobloxAccountOutcome.Unavailable, result.Outcome); Assert.Null(result.Observation); }
    [Fact]
    public async Task InvalidGameIdentifiersAreDroppedWithoutInventingArrival()
    {
        using var result = await Read(RobloxAccountRead.Presence, "{\"userPresences\":[{\"userId\":456,\"userPresenceType\":2,\"placeId\":-1,\"gameId\":\"bad value\"}]}");
        Assert.Equal(RobloxAccountOutcome.Success, result.Outcome); Assert.Null(result.Observation!.Presence!.GameId); Assert.Null(result.Observation.Presence.PlaceId);
    }
    [Theory]
    [InlineData(RobloxAccountRead.Robux, "{\"robux\":123}", "123")]
    [InlineData(RobloxAccountRead.FriendsCount, "{\"count\":0}", "0")]
    [InlineData(RobloxAccountRead.FollowersCount, "{\"count\":4567}", "4567")]
    [InlineData(RobloxAccountRead.Premium, "false", "False")]
    [InlineData(RobloxAccountRead.EmailVerified, "{\"verified\":true,\"emailAddress\":\"never-display@example.test\"}", "True")]
    [InlineData(RobloxAccountRead.BanState, "{\"id\":456,\"isBanned\":false}", "False")]
    [InlineData(RobloxAccountRead.TwoStepMethods, "{\"methods\":[{\"mediaType\":0,\"enabled\":true},{\"mediaType\":2,\"enabled\":true},{\"mediaType\":7,\"enabled\":false}]}", "Authenticator, Email")]
    public async Task DetailsAreTypedAndPrivateFieldsOutsideTheRequestAreDiscarded(RobloxAccountRead kind, string json, string expected)
    {
        using var result = await Read(kind, json); Assert.Equal(RobloxAccountOutcome.Success, result.Outcome);
        var data = result.Observation!; Assert.Equal(expected, data.Number?.ToString() ?? data.Methods ?? data.Flag?.ToString());
        Assert.DoesNotContain(expected, data.ToString()); Assert.DoesNotContain("never-display", data.ToString());
    }
    [Theory]
    [InlineData(RobloxAccountRead.Robux, "{\"robux\":-1}")]
    [InlineData(RobloxAccountRead.FriendsCount, "{\"count\":1.5}")]
    [InlineData(RobloxAccountRead.EmailVerified, "{}")]
    [InlineData(RobloxAccountRead.BanState, "{\"id\":123,\"isBanned\":false}")]
    [InlineData(RobloxAccountRead.Premium, "{\"premium\":false}")]
    [InlineData(RobloxAccountRead.TwoStepMethods, "{\"methods\":[{\"mediaType\":2}]}")]
    public async Task MissingOrMalformedPrivateFieldsStayUnknown(RobloxAccountRead kind, string json)
    { using var result = await Read(kind, json); Assert.Equal(RobloxAccountOutcome.Unavailable, result.Outcome); Assert.Null(result.Observation); }
    [Fact]
    public async Task OversizedAndMalformedBodiesStillReturnTheirRotationForVerification()
    {
        foreach (string json in new[] { new string('x', 65537), "{broken" })
        {
            using var client = new RobloxAccountDataClient(new Handler((_, _, _) =>
            { var response = Reply(json); response.Headers.Add("Set-Cookie", ".ROBLOSECURITY=synthetic-rotated; Domain=.roblox.com"); return Task.FromResult(response); }));
            using var cookie = new RobloxSessionCredential(Cookie);
            using var answer = await client.ReadAsync(RobloxAccountRead.Robux, 456, null, cookie, default);
            Assert.Equal(RobloxAccountOutcome.Unavailable, answer.Outcome); Assert.Equal("synthetic-rotated", answer.RotatedCredential!.CopyValue());
        }
    }
    [Fact]
    public async Task CsrfRetryIsExactBoundedAndDoesNotReuseARotatedCookie()
    {
        foreach (bool rotated in new[] { false, true })
        {
            var handler = new Handler((request, call, _) =>
            {
                Assert.Equal("https://friends.roblox.com/v1/users/789/request-friendship", request.RequestUri!.AbsoluteUri);
                if (call == 2) { Assert.Equal("synthetic-csrf", request.Headers.GetValues("X-CSRF-TOKEN").Single()); return Task.FromResult(Reply("{}")); }
                var response = Reply("{}", HttpStatusCode.Forbidden); response.Headers.Add("x-csrf-token", "synthetic-csrf");
                if (rotated) response.Headers.Add("Set-Cookie", ".ROBLOSECURITY=synthetic-rotated; Domain=.roblox.com");
                return Task.FromResult(response);
            });
            using var client = new RobloxAccountDataClient(handler); using var cookie = new RobloxSessionCredential(Cookie);
            using var result = await client.ReadAsync(RobloxAccountRead.RequestFriendship, 456, 789, cookie, default);
            Assert.Equal(rotated ? 1 : 2, handler.Calls); Assert.Equal(rotated ? RobloxAccountOutcome.Unavailable : RobloxAccountOutcome.Success, result.Outcome);
        }
    }
    [Theory]
    [InlineData(429, RobloxAccountOutcome.RateLimited)]
    [InlineData(503, RobloxAccountOutcome.RateLimited)]
    [InlineData(401, RobloxAccountOutcome.SignInRequired)]
    [InlineData(302, RobloxAccountOutcome.Unavailable)]
    [InlineData(403, RobloxAccountOutcome.Unavailable)]
    public async Task RefusedRequestsDoNotRetryOrFollowLocations(int status, RobloxAccountOutcome outcome)
    {
        var handler = new Handler((_, _, _) => { var response = Reply("{}", (HttpStatusCode)status); response.Headers.Location = new("https://example.test"); return Task.FromResult(response); });
        using var client = new RobloxAccountDataClient(handler); using var cookie = new RobloxSessionCredential(Cookie);
        using var result = await client.ReadAsync(RobloxAccountRead.Robux, 456, null, cookie, default);
        Assert.Equal(outcome, result.Outcome); Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task ChallengeAlwaysStopsEvenWhenACsrfTokenIsPresent()
    {
        var handler = new Handler((_, _, _) => { var response = Reply("{}", HttpStatusCode.Forbidden); response.Headers.Add("rblx-challenge-id", "synthetic"); response.Headers.Add("x-csrf-token", "synthetic"); return Task.FromResult(response); });
        using var client = new RobloxAccountDataClient(handler); using var cookie = new RobloxSessionCredential(Cookie);
        using var result = await client.ReadAsync(RobloxAccountRead.RequestFriendship, 456, 789, cookie, default);
        Assert.Equal(RobloxAccountOutcome.Challenge, result.Outcome); Assert.Equal(1, handler.Calls);
    }
    [Theory]
    [InlineData("{\"data\":[{\"id\":789,\"status\":99}]}")]
    [InlineData("{\"data\":[{\"id\":111,\"status\":0}]}")]
    [InlineData("{\"data\":[{\"id\":789}]}")]
    public async Task UnknownFriendshipStatusCannotCauseAMutation(string json)
    { using var result = await Read(RobloxAccountRead.FriendshipStatus, json, 789); Assert.Equal(RobloxAccountOutcome.Unavailable, result.Outcome); }
    [Fact]
    public async Task CancellationAndTimeoutNeverExposeTransportText()
    {
        using var client = new RobloxAccountDataClient(new Handler(async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return Reply("{}"); }), TimeSpan.FromMilliseconds(30));
        using var cookie = new RobloxSessionCredential(Cookie);
        var error = await Assert.ThrowsAsync<RobloxSessionException>(() => client.ReadAsync(RobloxAccountRead.Robux, 456, null, cookie, default));
        Assert.Equal(RobloxSessionFailure.TimedOut, error.Failure); Assert.DoesNotContain(Cookie, error.Message);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadAsync(RobloxAccountRead.Robux, 456, null, cookie, stop.Token));
    }
}
