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

public sealed class PublicDirectoryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData(" 123")]
    [InlineData("123 ")]
    [InlineData("1,000")]
    [InlineData("1e3")]
    [InlineData("１２３")]
    [InlineData("9223372036854775808")]
    [InlineData("1000000000000000000000000000000000000000")]
    public void UserIdParsingRejectsMalformedAndOversizedInput(string? input) => Assert.False(PublicRobloxUserId.TryParse(input, out _));

    [Fact]
    public void AnonymousDirectoryClientFollowsTheSameSystemProxyPolicyAsServerBrowsing()
    {
        // On a proxy-only network, game search, pictures and headshots must reach Roblox the same way
        // server browsing, sign-in checks and share links already do: through the Windows system proxy.
        using var directory = RobloxPublicDirectory.CreateAnonymousHandler();
        using var servers = RobloxServerDirectory.CreatePublicHandler();

        Assert.True(servers.UseProxy, "Server browsing should use the system proxy.");
        Assert.Null(servers.Proxy);
        Assert.True(directory.UseProxy, "The public directory bypasses the system proxy that server browsing uses.");
        Assert.Null(directory.Proxy);
        Assert.Equal(servers.UseProxy, directory.UseProxy);

        // Using the proxy must not loosen the anonymous guarantees.
        Assert.False(directory.AllowAutoRedirect);
        Assert.False(directory.UseCookies);
        Assert.Null(directory.Credentials);
        Assert.False(directory.PreAuthenticate);
        Assert.Null(directory.DefaultProxyCredentials);
        Assert.Equal(RobloxPublicDirectory.MaximumConcurrentRequests, directory.MaxConnectionsPerServer);
        Assert.Equal(32, directory.MaxResponseHeadersLength);
        Assert.Equal(TimeSpan.FromSeconds(10), directory.ConnectTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), directory.PooledConnectionLifetime);
    }

    [Fact]
    public async Task IdentityUsesInt64AndIsExplicitlyPublicInformation()
    {
        const long id = long.MaxValue;
        using var handler = new FakeHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://users.roblox.com/v1/users/9223372036854775807", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Single(request.Headers);
            Assert.Equal("application/json", request.Headers.Accept.Single().MediaType);
            return Task.FromResult(Json(UserJson(id)));
        });
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).LookupUserAsync(id, CancellationToken.None);
        Assert.True(PublicRobloxUserId.TryParse(id.ToString(), out long parsed));
        Assert.Equal(id, parsed);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        Assert.Equal(id, result.Value!.UserId);
        Assert.Equal("Synthetic_User", result.Value.Username);
        Assert.Equal("Synthetic Display", result.Value.DisplayName);
        Assert.Equal("Public profile information", result.Value.EvidenceLabel);
        Assert.Contains("does not verify sign-in", result.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public async Task InvalidIdsNeverSendRequests(long id)
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("No request expected."));
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http);
        Assert.Equal(PublicDirectoryState.InvalidInput, (await directory.LookupUserAsync(id, CancellationToken.None)).State);
        Assert.Equal(PublicDirectoryState.InvalidInput, (await directory.GetAvatarAsync(id, CancellationToken.None)).State);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"id\":1,\"name\":\"Synthetic\",\"displayName\":\"D\"}")]
    [InlineData("{\"id\":9223372036854775808,\"name\":\"Synthetic\",\"displayName\":\"D\"}")]
    [InlineData("{\"id\":\"42\",\"name\":\"Synthetic\",\"displayName\":\"D\"}")]
    [InlineData("{\"id\":42,\"id\":42,\"name\":\"Synthetic\",\"displayName\":\"D\"}")]
    [InlineData("{\"id\":42,\"name\":\"Bad Name\",\"displayName\":\"D\"}")]
    [InlineData("{\"id\":42,\"name\":\"Synthetic\",\"displayName\":\"bad\\nname\"}")]
    [InlineData("{\"id\":42,\"name\":\"Synthetic\",\"displayName\":\"bad\\u202ename\"}")]
    [InlineData("{\"id\":42,\"name\":\"\\uD800\",\"displayName\":\"Synthetic\"}")]
    [InlineData("{\"id\":42,\"name\":\"Synthetic\",\"displayName\":\"\\uDC00\"}")]
    [InlineData("{\"id\":42,\"name\":\"Synthetic\",\"displayName\":\"D\",\"\\uD800\":1}")]
    [InlineData("untrusted-response-marker")]
    public async Task UntrustedIdentityResponsesFailWithoutReturningRawData(string json)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Json(json)));
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).LookupUserAsync(42, CancellationToken.None);
        Assert.Equal(PublicDirectoryState.Failure, result.State);
        Assert.Null(result.Value);
        Assert.DoesNotContain("untrusted-response-marker", result.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedJsonIsBoundedWithAndWithoutContentLength(bool unknownLength)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(new string('x', RobloxPublicDirectory.MaximumJsonBytes + 1));
        using var handler = new FakeHandler((_, _) => Task.FromResult(Response(bytes, "application/json", unknownLength)));
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).LookupUserAsync(42, CancellationToken.None);
        Assert.Equal(PublicDirectoryState.Failure, result.State);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData(301, PublicDirectoryState.Failure)]
    [InlineData(302, PublicDirectoryState.Failure)]
    [InlineData(401, PublicDirectoryState.Failure)]
    [InlineData(403, PublicDirectoryState.Failure)]
    [InlineData(404, PublicDirectoryState.Unknown)]
    [InlineData(500, PublicDirectoryState.Failure)]
    [InlineData(503, PublicDirectoryState.Failure)]
    public async Task StatusFailuresNeverTriggerRetriesOrCredentialFallback(int status, PublicDirectoryState expected)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent("untrusted-status-body") }));
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).LookupUserAsync(42, CancellationToken.None);
        Assert.Equal(expected, result.State);
        Assert.Null(result.Value);
        Assert.DoesNotContain("untrusted-status-body", result.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RateLimitingDefersTheHostWithoutRetryingAndHonorsRetryAfter()
    {
        var clock = new FakeTime();
        using var handler = new FakeHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http, clock);
        var first = await directory.LookupUserAsync(42, CancellationToken.None);
        Assert.Equal(PublicDirectoryState.Deferred, first.State);
        Assert.Equal(TimeSpan.FromSeconds(90), first.RetryAfter);
        Assert.Equal(PublicDirectoryState.Deferred, (await directory.LookupUserAsync(43, CancellationToken.None)).State);
        Assert.Equal(1, handler.Calls);
        clock.Advance(TimeSpan.FromSeconds(91));
        Assert.Equal(PublicDirectoryState.Deferred, (await directory.LookupUserAsync(44, CancellationToken.None)).State);
        Assert.Equal(2, handler.Calls);
    }

    // The window reset counts only when the route window is used up (remaining is 0). A 429 from a
    // different limiter while the window still has room waits for Retry-After, not the whole window,
    // because the pause is shared by every public-directory lookup on that host.
    [Theory]
    [InlineData("5", "0", "50", 50)]
    [InlineData("90", "0", "10", 90)]
    [InlineData(null, "0", "50", 50)]
    [InlineData("5", "0", "soon", 5)]
    [InlineData("5", "5", "50", 5)]
    [InlineData("5", null, "50", 5)]
    public async Task RateLimitPauseUsesTheLongerOfRetryAfterAndTheWindowReset(string? retryAfter, string? remaining, string reset, int expectedSeconds)
    {
        var clock = new FakeTime();
        using var handler = new FakeHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            if (remaining is not null) response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", remaining);
            response.Headers.TryAddWithoutValidation("x-ratelimit-reset", reset);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http, clock);
        TimeSpan expected = TimeSpan.FromSeconds(expectedSeconds);
        Assert.Equal(expected, (await directory.LookupUserAsync(42, default)).RetryAfter);
        clock.Advance(expected - TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), (await directory.LookupUserAsync(43, default)).RetryAfter);
        Assert.Equal(1, handler.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await directory.LookupUserAsync(44, default);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongProviderPauseIsNotShortenedAndNoRequestStartsBeforeItsDeadline(bool dateHeader)
    {
        var clock = new FakeTime();
        TimeSpan pause = dateHeader ? TimeSpan.FromDays(3) : TimeSpan.FromHours(2);
        using var handler = new FakeHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = dateHeader ? new RetryConditionHeaderValue(clock.GetUtcNow().Add(pause)) : new RetryConditionHeaderValue(pause);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http, clock);
        Assert.Equal(pause, (await directory.LookupUserAsync(42, default)).RetryAfter);
        clock.Advance(pause - TimeSpan.FromTicks(1));
        var early = await directory.LookupUserAsync(43, default);
        Assert.Equal(PublicDirectoryState.Deferred, early.State);
        Assert.Equal(TimeSpan.FromTicks(1), early.RetryAfter);
        Assert.Equal(1, handler.Calls);
        clock.Advance(TimeSpan.FromTicks(1));
        await directory.LookupUserAsync(44, default);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task HugeValidDecimalPauseSaturatesAtClockHorizonWithoutOverflowOrEarlyRetry()
    {
        var clock = new FakeTime();
        using var handler = new FakeHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", new string('9', 1000));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http, clock);
        TimeSpan horizon = DateTimeOffset.MaxValue - clock.GetUtcNow();
        Assert.Equal(horizon, (await directory.LookupUserAsync(42, default)).RetryAfter);
        clock.Advance(horizon - TimeSpan.FromTicks(1));
        Assert.Equal(TimeSpan.FromTicks(1), (await directory.LookupUserAsync(43, default)).RetryAfter);
        Assert.Equal(1, handler.Calls);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(TimeSpan.Zero, (await directory.LookupUserAsync(44, default)).RetryAfter);
        Assert.Equal(2, handler.Calls);
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
    public async Task MissingInvalidOrNonpositiveRateLimitHeaderUsesThirtySecondPause(string? header)
    {
        var clock = new FakeTime();
        using var handler = new FakeHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            if (header is not null) response.Headers.TryAddWithoutValidation("Retry-After", header);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http, clock);
        Assert.Equal(TimeSpan.FromSeconds(30), (await directory.LookupUserAsync(42, default)).RetryAfter);
        clock.Advance(TimeSpan.FromSeconds(29));
        await directory.LookupUserAsync(43, default);
        Assert.Equal(1, handler.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await directory.LookupUserAsync(44, default);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ConcurrentShorterRateLimitResponseCannotReduceAnExistingProviderDeadline()
    {
        var clock = new FakeTime();
        var firstRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var handler = new FakeHandler(async (_, ct) =>
        {
            int index = Interlocked.Increment(ref calls);
            await (index == 1 ? firstRelease.Task : secondRelease.Task).WaitAsync(ct);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(index == 1 ? TimeSpan.FromHours(2) : TimeSpan.FromSeconds(30));
            return response;
        });
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http, clock);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = directory.LookupUserAsync(42, deadline.Token);
        var second = directory.LookupUserAsync(43, deadline.Token);
        try
        {
            Assert.Equal(2, handler.Calls);
            firstRelease.SetResult(true);
            Assert.Equal(TimeSpan.FromHours(2), (await first).RetryAfter);
            secondRelease.SetResult(true);
            Assert.Equal(TimeSpan.FromHours(2), (await second).RetryAfter);
            clock.Advance(TimeSpan.FromHours(2) - TimeSpan.FromSeconds(1));
            Assert.Equal(TimeSpan.FromSeconds(1), (await directory.LookupUserAsync(44, default)).RetryAfter);
            Assert.Equal(2, handler.Calls);
        }
        finally { firstRelease.TrySetResult(true); secondRelease.TrySetResult(true); }
    }

    [Fact]
    public async Task CancellationPropagatesAndReleasesCapacity()
    {
        int requests = 0;
        using var handler = new FakeHandler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref requests) == 1) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Json(UserJson(42));
        });
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http);
        using var cancel = new CancellationTokenSource();
        var pending = directory.LookupUserAsync(42, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(PublicDirectoryState.Success, (await directory.LookupUserAsync(42, CancellationToken.None)).State);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => directory.GetAvatarAsync(42, cancel.Token));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task HttpTimeoutAndConnectionFailureAreExplicitResults()
    {
        using var timedHandler = new FakeHandler((_, _) => throw new TaskCanceledException("synthetic transport deadline"));
        using var timedHttp = new HttpClient(timedHandler);
        Assert.Equal(PublicDirectoryState.Deferred, (await new RobloxPublicDirectory(timedHttp).LookupUserAsync(42, CancellationToken.None)).State);
        using var failedHandler = new FakeHandler((_, _) => throw new HttpRequestException("untrusted-error-text"));
        using var failedHttp = new HttpClient(failedHandler);
        var failure = await new RobloxPublicDirectory(failedHttp).LookupUserAsync(42, CancellationToken.None);
        Assert.Equal(PublicDirectoryState.Failure, failure.State);
        Assert.DoesNotContain("untrusted-error-text", failure.Message);
    }

    [Fact]
    public async Task TwoRequestsAreAllowedAndAdditionalWorkIsDeferredWithoutQueuing()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FakeHandler(async (request, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return Json(UserJson(long.Parse(request.RequestUri!.Segments.Last())));
        });
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = Enumerable.Range(1, 2).Select(id => directory.LookupUserAsync(id, timeout.Token)).ToArray();
        Assert.Equal(2, handler.Calls);
        Assert.Equal(PublicDirectoryState.Deferred, (await directory.GetAvatarAsync(5, timeout.Token)).State);
        Assert.Equal(2, handler.Calls);
        release.SetResult(true);
        Assert.All(await Task.WhenAll(pending), result => Assert.Equal(PublicDirectoryState.Success, result.State));
    }

    [Fact]
    public async Task InheritedCredentialsOrCustomDefaultHeadersAreRejectedBeforeSending()
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("No request expected."));
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-never-send");
        var directory = new RobloxPublicDirectory(http);
        Assert.Equal(PublicDirectoryState.Failure, (await directory.LookupUserAsync(42, CancellationToken.None)).State);
        http.DefaultRequestHeaders.Clear();
        http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", "synthetic-never-send");
        Assert.Equal(PublicDirectoryState.Failure, (await directory.GetAvatarAsync(42, CancellationToken.None)).State);
        http.DefaultRequestHeaders.Clear();
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Secret", "synthetic-never-send");
        Assert.Equal(PublicDirectoryState.Failure, (await directory.FetchAvatarAsync(Avatar(), CancellationToken.None)).State);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task InvalidOrStaleAvatarDescriptorsNeverSendRequests()
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("No request expected."));
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http);
        Assert.Equal(PublicDirectoryState.InvalidInput, (await directory.FetchAvatarAsync(Avatar() with
            { ImageUri = new Uri("https://evil.invalid/avatar.png") }, CancellationToken.None)).State);
        Assert.Equal(PublicDirectoryState.InvalidInput, (await directory.FetchAvatarAsync(Avatar() with
            { Width = 10000 }, CancellationToken.None)).State);
        Assert.Equal(PublicDirectoryState.Deferred, (await directory.FetchAvatarAsync(Avatar(DateTimeOffset.UtcNow.AddHours(-1)), CancellationToken.None)).State);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/relative")]
    [InlineData("http://tr.rbxcdn.com/avatar.png")]
    [InlineData("https://tr.rbxcdn.com.evil.invalid/avatar.png")]
    [InlineData("https://evil.invalid/tr.rbxcdn.com/avatar.png")]
    [InlineData("https://localhost/avatar.png")]
    [InlineData("https://127.0.0.1/avatar.png")]
    [InlineData("https://tr.rbxcdn.com:444/avatar.png")]
    [InlineData("https://user:password@tr.rbxcdn.com/avatar.png")]
    [InlineData("https://tr.rbxcdn.com/avatar.png?token=do-not-send")]
    [InlineData("https://tr.rbxcdn.com/avatar.png#fragment")]
    [InlineData("https://tr.rbxcdn.com./avatar.png")]
    [InlineData("https://tr.rbxcdn.com/%2favatar.png")]
    [InlineData("https://t8.rbxcdn.com/avatar.png")]
    [InlineData("https://tr.rbxcdn.com\\@evil.invalid/avatar.png")]
    public void AvatarHostsAndUntrustedUrisAreStrictlyValidated(string? uri) => Assert.False(RobloxPublicDirectory.TryValidateAvatarUri(uri, out _));

    [Theory]
    [InlineData("https://tr.rbxcdn.com/30DAY-AvatarHeadshot-SYNTHETIC-Png/150/150/AvatarHeadshot/Png/noFilter")]
    [InlineData("https://t0.rbxcdn.com/synthetic.png")]
    [InlineData("https://t7.rbxcdn.com/synthetic.png")]
    public void ExpectedHttpsCdnHostsAreAccepted(string uri) => Assert.True(RobloxPublicDirectory.TryValidateAvatarUri(uri, out _));

    [Fact]
    public async Task DescriptorRequiresMatchingTargetAndValidatedCdnAndNeverFetchesItAutomatically()
    {
        using var handler = new FakeHandler((request, _) =>
        {
            Assert.Equal("thumbnails.roblox.com", request.RequestUri!.Host);
            Assert.Contains("userIds=42", request.RequestUri.Query);
            Assert.Contains("size=150x150", request.RequestUri.Query);
            return Task.FromResult(Json(AvatarJson("Completed", "https://tr.rbxcdn.com/synthetic.png")));
        });
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).GetAvatarAsync(42, CancellationToken.None);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        Assert.Equal(42, result.Value!.UserId);
        Assert.Equal(150, result.Value.Width);
        Assert.Equal(150, result.Value.Height);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("Pending", PublicDirectoryState.Deferred, "The public avatar is still being prepared.")]
    [InlineData("InReview", PublicDirectoryState.Deferred, "The public avatar is still being prepared.")]
    [InlineData("Blocked", PublicDirectoryState.Unknown, "The public avatar is unavailable.")]
    [InlineData("Error", PublicDirectoryState.Unknown, "The public avatar is unavailable.")] // Roblox's own name for a picture it could not make.
    [InlineData("NewUnknownState", PublicDirectoryState.Unknown, "The public avatar state is not supported.")] // A name Zenith does not know yet.
    public async Task UnreadyAvatarsAreExplicitAndNeverInvented(string state, PublicDirectoryState expected, string message)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Json(AvatarJson(state, null))));
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).GetAvatarAsync(42, CancellationToken.None);
        Assert.Equal(expected, result.State);
        Assert.Null(result.Value);
        Assert.Equal(message, result.Message);
    }

    [Theory]
    [InlineData("{\"data\":[]}", PublicDirectoryState.Unknown)]
    [InlineData("{\"data\":[{\"targetId\":43,\"state\":\"Pending\"}]}", PublicDirectoryState.Failure)]
    [InlineData("{\"data\":[{\"targetId\":9223372036854775808,\"state\":\"Pending\"}]}", PublicDirectoryState.Failure)]
    [InlineData("{\"data\":[{\"targetId\":42,\"state\":\"Completed\",\"imageUrl\":\"https://evil.invalid/avatar.png\"}]}", PublicDirectoryState.Failure)]
    [InlineData("{\"data\":[{\"targetId\":42,\"state\":\"Completed\",\"imageUrl\":\"https://tr.rbxcdn.com/avatar.png?secret=do-not-send\"}]}", PublicDirectoryState.Failure)]
    public async Task MalformedAvatarDescriptorsAreNotExposedOrFetched(string json, PublicDirectoryState expected)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Json(json)));
        using var http = new HttpClient(handler);
        var result = await new RobloxPublicDirectory(http).GetAvatarAsync(42, CancellationToken.None);
        Assert.Equal(expected, result.State);
        Assert.Null(result.Value);
        Assert.DoesNotContain("do-not-send", result.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task EncodedAvatarCacheExpiresAndCanBeCleared()
    {
        var clock = new FakeTime();
        using var handler = new FakeHandler((_, _) => Task.FromResult(Response(PngHeader(), "image/png")));
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http, clock);
        var avatar = Avatar(clock.GetUtcNow());
        Assert.Equal(PublicDirectoryState.Success, (await directory.FetchAvatarAsync(avatar, CancellationToken.None)).State);
        Assert.Equal(PublicDirectoryState.Success, (await directory.FetchAvatarAsync(avatar, CancellationToken.None)).State);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(33, directory.CachedEncodedBytes);
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(PublicDirectoryState.Success, (await directory.FetchAvatarAsync(avatar, CancellationToken.None)).State);
        Assert.Equal(2, handler.Calls);
        directory.ClearMemoryCache();
        Assert.Equal(0, directory.CachedEncodedBytes);
        Assert.Equal(0, directory.CachedAvatarCount);
    }

    [Fact]
    public async Task ConcurrentDuplicateFetchDefersAndClearingPreventsOldFetchRepopulation()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FakeHandler(async (_, ct) => { await release.Task.WaitAsync(ct); return Response(PngHeader(), "image/png"); });
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = directory.FetchAvatarAsync(Avatar(), timeout.Token);
        Assert.Equal(PublicDirectoryState.Deferred, (await directory.FetchAvatarAsync(Avatar(), timeout.Token)).State);
        Assert.Equal(1, handler.Calls);
        directory.ClearMemoryCache();
        release.SetResult(true);
        Assert.Equal(PublicDirectoryState.Success, (await first).State);
        Assert.Equal(0, directory.CachedAvatarCount);
    }

    [Fact]
    public async Task EncodedCacheStaysWithinThe64MiBBudgetAndEvictsOldestEntries()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Response(PngHeader(RobloxPublicDirectory.MaximumAvatarBytes), "image/png")));
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http);
        for (int index = 0; index < 33; index++)
        {
            var avatar = Avatar() with { ImageUri = new Uri($"https://tr.rbxcdn.com/synthetic-{index}.png") };
            Assert.Equal(PublicDirectoryState.Success, (await directory.FetchAvatarAsync(avatar, CancellationToken.None)).State);
            Assert.InRange(directory.CachedEncodedBytes, 0, RobloxPublicDirectory.MaximumEncodedMemoryBytes);
        }
        Assert.Equal(32, directory.CachedAvatarCount);
        Assert.Equal(RobloxPublicDirectory.MaximumEncodedMemoryBytes, directory.CachedEncodedBytes);
        Assert.Equal(33, handler.Calls);
        await directory.FetchAvatarAsync(Avatar() with { ImageUri = new Uri("https://tr.rbxcdn.com/synthetic-0.png") }, CancellationToken.None);
        Assert.Equal(34, handler.Calls);
    }

    [Fact]
    public async Task CustomEncodedBudgetEvictsLeastRecentlyUsedEntries()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Response(PngHeader(), "image/png")));
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http, maximumEncodedMemoryBytes: 66);
        var a = Avatar() with { ImageUri = new Uri("https://tr.rbxcdn.com/custom-a.png") };
        var b = Avatar() with { ImageUri = new Uri("https://tr.rbxcdn.com/custom-b.png") };
        var c = Avatar() with { ImageUri = new Uri("https://tr.rbxcdn.com/custom-c.png") };
        await directory.FetchAvatarAsync(a, default);
        await directory.FetchAvatarAsync(b, default);
        await directory.FetchAvatarAsync(a, default);
        Assert.Equal(2, handler.Calls);
        await directory.FetchAvatarAsync(c, default);
        Assert.Equal(66, directory.EncodedMemoryBudgetBytes);
        Assert.Equal(66, directory.CachedEncodedBytes);
        Assert.Equal(2, directory.CachedAvatarCount);
        await directory.FetchAvatarAsync(a, default);
        Assert.Equal(3, handler.Calls);
        await directory.FetchAvatarAsync(b, default);
        Assert.Equal(4, handler.Calls);
        Assert.Equal(66, directory.CachedEncodedBytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    public async Task ValidEncodedImageLargerThanCustomBudgetIsReturnedWithoutBeingCached(long budget)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Response(PngHeader(), "image/png")));
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http, maximumEncodedMemoryBytes: budget);
        var result = await directory.FetchAvatarAsync(Avatar(), default);
        Assert.Equal(PublicDirectoryState.Success, result.State);
        Assert.NotNull(result.Value);
        Assert.Equal(33, result.Value.Bytes.Length);
        Assert.Equal(0, directory.CachedAvatarCount);
        Assert.Equal(0, directory.CachedEncodedBytes);
        Assert.Equal(PublicDirectoryState.Success, (await directory.FetchAvatarAsync(Avatar(), default)).State);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(RobloxPublicDirectory.MaximumEncodedMemoryBytes + 1)]
    [InlineData(long.MaxValue)]
    public void InvalidEncodedMemoryBudgetsAreRejectedBeforeNetworkWork(long budget)
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("No request expected."));
        using var http = new HttpClient(handler);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RobloxPublicDirectory(http, maximumEncodedMemoryBytes: budget));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("image/jpeg", 33, 150)]
    [InlineData("image/png", 32, 150)]
    [InlineData("image/png", 33, 10000)]
    [InlineData("image/png", RobloxPublicDirectory.MaximumAvatarBytes + 1, 150)]
    public async Task EncodedFetchRejectsUnsupportedOversizedOrUnsafeDimensions(string mediaType, int size, int width)
    {
        byte[] bytes = PngHeader(Math.Max(size, 33));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), (uint)width);
        if (size < bytes.Length) Array.Resize(ref bytes, size);
        using var handler = new FakeHandler((_, _) => Task.FromResult(Response(bytes, mediaType, true)));
        using var http = new HttpClient(handler);
        var directory = new RobloxPublicDirectory(http);
        var result = await directory.FetchAvatarAsync(Avatar(), CancellationToken.None);
        Assert.Equal(PublicDirectoryState.Failure, result.State);
        Assert.Null(result.Value);
        Assert.Equal(0, directory.CachedAvatarCount);
    }

    private static PublicRobloxAvatar Avatar(DateTimeOffset? now = null) => new(42, new Uri("https://tr.rbxcdn.com/synthetic.png"), 150, 150, now ?? DateTimeOffset.UtcNow);
    private static string UserJson(long id) => $"{{\"id\":{id},\"name\":\"Synthetic_User\",\"displayName\":\"Synthetic Display\"}}";
    private static string AvatarJson(string state, string? uri) => JsonSerializer.Serialize(new { data = new[] { new { targetId = 42, state, imageUrl = uri } } });
    private static HttpResponseMessage Json(string json) => Response(Encoding.UTF8.GetBytes(json), "application/json");
    private static HttpResponseMessage Response(byte[] bytes, string mediaType, bool unknownLength = false)
    {
        HttpContent content = unknownLength ? new StreamContent(new NonSeekableStream(bytes)) : new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
    // Only a synthetic PNG header: the transport validates format and dimensions; UI decoding must still validate full images.
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
