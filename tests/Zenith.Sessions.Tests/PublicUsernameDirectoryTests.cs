using System.Net;
using System.Net.Http;
using System.Text;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

// Every request terminates at an owned synthetic handler. No Roblox contact, credentials or launches.
public sealed class PublicUsernameDirectoryTests
{
    [Fact]
    public async Task OneAnonymousPostSendsTheExactBodyAndMatchesRepliesByRequestedName()
    {
        string? sentBody = null; string? sentType = null;
        using var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://users.roblox.com/v1/usernames/users", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
            Assert.False(request.Headers.Contains("X-CSRF-TOKEN"));
            Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
            sentType = request.Content!.Headers.ContentType!.MediaType;
            sentBody = await request.Content.ReadAsStringAsync(ct);
            // Roblox does not promise reply order, may echo a different letter case, and matches previous names.
            return Json(Reply(Item("roblox", 1, "Roblox", "Roblox"), Item("OldName_1", 77, "NewName", "Shown Name")));
        });
        using var http = new HttpClient(handler); var clock = new Clock();
        var directory = Assert.IsAssignableFrom<IPublicRobloxUsernameDirectory>(new RobloxPublicDirectory(http, clock));
        var result = await directory.LookupUsersByNameAsync(["OldName_1", "NoSuchUser9", "Roblox"], default);
        Assert.Equal("{\"usernames\":[\"OldName_1\",\"NoSuchUser9\",\"Roblox\"],\"excludeBannedUsers\":true}", sentBody);
        Assert.Equal("application/json", sentType);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        // Request order, with the name Roblox did not return left out.
        Assert.Equal(new[] { "OldName_1", "Roblox" }, result.Value!.Select(match => match.RequestedUsername).ToArray());
        Assert.Equal(new PublicRobloxUser(77, "NewName", "Shown Name", clock.GetUtcNow()), result.Value![0].User);
        Assert.Equal(new PublicRobloxUser(1, "Roblox", "Roblox", clock.GetUtcNow()), result.Value![1].User);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public async Task NoMatchesIsASuccessfulEmptyAnswer()
    {
        var result = await Request(["NoSuchUser9"], Reply());
        Assert.Equal(PublicDirectoryState.Success, result.State); Assert.Empty(result.Value!);
    }

    [Theory]
    [InlineData("unrequested")]
    [InlineData("duplicate")]
    [InlineData("duplicate-other-case")]
    [InlineData("too-many")]
    [InlineData("non-positive-id")]
    [InlineData("unsafe-name")]
    [InlineData("missing-display-name")]
    [InlineData("control-display-name")]
    public async Task UnrequestedDuplicateOrMalformedRepliesAreInvalid(string shape)
    {
        string body = shape switch
        {
            "unrequested" => Reply(Item("Roblox", 1, "Roblox", "Roblox"), Item("Someone_Else", 9, "Someone_Else", "do-not-send")),
            "duplicate" => Reply(Item("Roblox", 1, "Roblox", "Roblox"), Item("Roblox", 1, "Roblox", "Roblox")),
            "duplicate-other-case" => Reply(Item("Roblox", 1, "Roblox", "Roblox"), Item("ROBLOX", 2, "Other", "Other")),
            "too-many" => Reply(Item("Roblox", 1, "Roblox", "Roblox"), Item("Builderman", 156, "builderman", "builderman"),
                Item("Roblox", 1, "Roblox", "Roblox")),
            "non-positive-id" => Reply(Item("Roblox", 0, "Roblox", "Roblox")),
            "unsafe-name" => Reply(Item("Roblox", 1, "Rob lox", "Roblox")),
            "missing-display-name" => "{\"data\":[{\"requestedUsername\":\"Roblox\",\"id\":1,\"name\":\"Roblox\"}]}",
            _ => Reply(Item("Roblox", 1, "Roblox", "Rob" + (char)0x202E + "lox"))
        };
        var result = await Request(["Roblox", "Builderman"], body);
        Assert.Equal(PublicDirectoryState.Failure, result.State); Assert.Null(result.Value);
        Assert.DoesNotContain("do-not-send", result.Message);
    }

    [Fact]
    public async Task EmptyOversizedDuplicateOrMalformedNamesAreRejectedWithoutARequest()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No request expected."));
        using var http = new HttpClient(handler);
        IPublicRobloxUsernameDirectory directory = Assert.IsAssignableFrom<IPublicRobloxUsernameDirectory>(new RobloxPublicDirectory(http));
        string[] tooMany = Enumerable.Range(1, PublicRobloxUsername.MaximumBatch + 1).Select(i => "User" + i).ToArray();
        string[]?[] cases =
        [
            null, [], tooMany, ["Roblox", "roblox"], ["ab"], [new string('a', 21)], ["Rob lox"],
            [" Roblox"], ["Rob-lox"], ["R" + (char)0xF6 + "blox"], [null!], [""]
        ];
        foreach (string[]? names in cases)
        {
            var rejected = await directory.LookupUsersByNameAsync(names!, default);
            Assert.Equal(PublicDirectoryState.InvalidInput, rejected.State);
            Assert.Contains("username", rejected.Message);
        }
        Assert.Equal(0, handler.Calls);
        Assert.Equal(50, PublicRobloxUsername.MaximumBatch);
        Assert.True(PublicRobloxUsername.IsValid("abc")); Assert.True(PublicRobloxUsername.IsValid("A_" + new string('9', 18)));
    }

    [Fact]
    public async Task FullBatchIsOneRequest()
    {
        string[] names = Enumerable.Range(1, PublicRobloxUsername.MaximumBatch).Select(i => "Player_" + i).ToArray();
        using var handler = new Handler((_, _) =>
            Task.FromResult(Json(Reply(names.Select((name, i) => Item(name, 1_000_000_000L + i, name, name)).ToArray()))));
        using var http = new HttpClient(handler);
        var directory = Assert.IsAssignableFrom<IPublicRobloxUsernameDirectory>(new RobloxPublicDirectory(http, new Clock()));
        var result = await directory.LookupUsersByNameAsync(names, default);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        Assert.Equal(names, result.Value!.Select(match => match.User.Username).ToArray());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RateLimitPauseIsSharedWithUserIdLookups()
    {
        using var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("") };
            response.Headers.TryAddWithoutValidation("Retry-After", "60");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var publicDirectory = new RobloxPublicDirectory(http, new Clock());
        var directory = Assert.IsAssignableFrom<IPublicRobloxUsernameDirectory>(publicDirectory);
        var limited = await directory.LookupUsersByNameAsync(["Roblox"], default);
        Assert.Equal(PublicDirectoryState.Deferred, limited.State); Assert.Equal(TimeSpan.FromSeconds(60), limited.RetryAfter);
        Assert.Equal(PublicDirectoryState.Deferred, (await publicDirectory.LookupUserAsync(1, default)).State);
        Assert.Equal(PublicDirectoryState.Deferred, (await directory.LookupUsersByNameAsync(["Builderman"], default)).State);
        Assert.Equal(1, handler.Calls);
    }

    private static string Item(string requested, long id, string name, string displayName) =>
        "{\"requestedUsername\":\"" + requested + "\",\"hasVerifiedBadge\":false,\"id\":" + id
        + ",\"name\":\"" + name + "\",\"displayName\":\"" + displayName + "\"}";
    private static string Reply(params string[] items) => "{\"data\":[" + string.Join(",", items) + "]}";
    private static async Task<PublicDirectoryResult<IReadOnlyList<PublicRobloxUsernameMatch>>> Request(string[] names, string json)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(json))); using var http = new HttpClient(handler);
        var directory = Assert.IsAssignableFrom<IPublicRobloxUsernameDirectory>(new RobloxPublicDirectory(http, new Clock()));
        var result = await directory.LookupUsersByNameAsync(names, default);
        Assert.Equal(1, handler.Calls);
        return result;
    }
    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
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
