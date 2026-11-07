using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class RobloxSessionClientTests
{
    private const string Secret = "synthetic-session-not-real";
    private static HttpResponseMessage Json(string text = "{\"id\":456,\"name\":\"verified_player\"}") =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }

    [Fact]
    public async Task OnlySelectedCookieGoesToExactAuthenticatedEndpointOnce()
    {
        int calls = 0;
        using var client = new RobloxSessionClient(new Transport((request, _) =>
        {
            calls++; Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal(RobloxSessionClient.IdentityEndpoint, request.RequestUri);
            Assert.Equal(".ROBLOSECURITY=" + Secret, Assert.Single(request.Headers.GetValues("Cookie")));
            Assert.Null(request.Headers.Authorization); Assert.Null(request.Content); return Task.FromResult(Json());
        }));
        using var credential = new RobloxSessionCredential(Secret); using var result = await client.ValidateAsync(credential, default);
        Assert.Equal(1, calls); Assert.Equal(456, result.Identity.UserId); Assert.Equal("verified_player", result.Identity.Username);
        Assert.Equal(RobloxSessionIdentity.Issuer, result.Identity.Issuer); Assert.Equal("456", result.Identity.Subject);
        Assert.Null(result.RotatedCredential); Assert.DoesNotContain(Secret, credential.ToString()); Assert.DoesNotContain(Secret, result.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad;cookie")]
    [InlineData("bad,cookie")]
    [InlineData("bad\r\ncookie")]
    [InlineData("contains space")]
    [InlineData("unicode-é")]
    public void CredentialsRejectHeaderInjectionWithoutEchoingTheValue(string value)
    {
        var error = Assert.Throws<ArgumentException>(() => new RobloxSessionCredential(value));
        if (value.Length > 0) Assert.DoesNotContain(value, error.Message);
    }

    [Fact]
    public void CredentialIsBoundedRedactedAndUnavailableAfterDisposal()
    {
        Assert.Throws<ArgumentException>(() => new RobloxSessionCredential(new string('a', RobloxSessionCredential.MaximumLength + 1)));
        var credential = new RobloxSessionCredential(Secret); credential.Dispose(); credential.Dispose();
        Assert.Throws<ObjectDisposedException>(() => credential.CopyValue()); Assert.DoesNotContain(Secret, credential.ToString());
    }

    [Theory]
    [InlineData("{\"id\":0,\"name\":\"valid\"}")]
    [InlineData("{\"id\":\"456\",\"name\":\"valid\"}")]
    [InlineData("{\"id\":456,\"name\":\"bad name\"}")]
    [InlineData("{\"id\":456,\"name\":\"valid\",\"ID\":999}")]
    [InlineData("{\"id\":456,\"name\":\"valid\",\"name\":\"other\"}")]
    [InlineData("[]")]
    [InlineData("not-json synthetic-secret-body")]
    public async Task MalformedIdentityNeverReturnsPartialOrSecretBearingErrors(string body)
    {
        using var client = new RobloxSessionClient(new Transport((_, _) => Task.FromResult(Json(body))));
        using var credential = new RobloxSessionCredential(Secret);
        var error = await Assert.ThrowsAsync<RobloxSessionException>(() => client.ValidateAsync(credential, default));
        Assert.Equal(RobloxSessionFailure.InvalidResponse, error.Failure); Assert.DoesNotContain(body, error.ToString()); Assert.DoesNotContain(Secret, error.ToString());
    }

    [Fact]
    public async Task ResponseSizeIsBounded()
    {
        using var client = new RobloxSessionClient(new Transport((_, _) => Task.FromResult(Json(new string(' ', 65537)))));
        using var credential = new RobloxSessionCredential(Secret);
        var error = await Assert.ThrowsAsync<RobloxSessionException>(() => client.ValidateAsync(credential, default));
        Assert.Equal(RobloxSessionFailure.InvalidResponse, error.Failure);
    }

    [Theory]
    [InlineData(401, false, RobloxSessionFailure.InvalidSession)]
    [InlineData(403, true, RobloxSessionFailure.Challenge)]
    [InlineData(302, false, RobloxSessionFailure.Network)]
    [InlineData(429, false, RobloxSessionFailure.NotProcessed)]
    public async Task FailureStatesAreFixedAndNeverRetried(int status, bool challenge, RobloxSessionFailure expected)
    {
        int calls = 0;
        using var client = new RobloxSessionClient(new Transport((_, _) =>
        {
            calls++; var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(Secret) };
            if (challenge) response.Headers.Add("rblx-challenge-id", Secret);
            return Task.FromResult(response);
        }));
        using var credential = new RobloxSessionCredential(Secret);
        var error = await Assert.ThrowsAsync<RobloxSessionException>(() => client.ValidateAsync(credential, default));
        Assert.Equal(expected, error.Failure); Assert.Equal(1, calls); Assert.DoesNotContain(Secret, error.ToString());
    }

    // R-accounts-1: only a busy refusal that carries no session cookie provably left the saved session
    // in place. A gateway error or any session Set-Cookie may follow a rotation and stays ambiguous.
    [Theory]
    [InlineData(429, null, RobloxSessionFailure.NotProcessed)]
    [InlineData(503, null, RobloxSessionFailure.NotProcessed)]
    [InlineData(429, ".ROBLOSECURITY=synthetic-rotated; Domain=.roblox.com; Path=/; Secure; HttpOnly", RobloxSessionFailure.Network)]
    [InlineData(503, ".ROBLOSECURITY=synthetic-rotated; Domain=.roblox.com", RobloxSessionFailure.Network)]
    [InlineData(429, ".roblosecurity=synthetic-rotated; Path=/", RobloxSessionFailure.Network)]
    [InlineData(500, null, RobloxSessionFailure.Network)]
    [InlineData(502, null, RobloxSessionFailure.Network)]
    [InlineData(504, null, RobloxSessionFailure.Network)]
    public async Task OnlyABusyRefusalWithoutASessionCookieIsNotProcessed(int status, string? setCookie, RobloxSessionFailure expected)
    {
        int calls = 0;
        using var client = new RobloxSessionClient(new Transport((_, _) =>
        {
            calls++; var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(Secret) };
            if (setCookie is not null) response.Headers.Add("Set-Cookie", setCookie);
            return Task.FromResult(response);
        }));
        using var credential = new RobloxSessionCredential(Secret);
        var error = await Assert.ThrowsAsync<RobloxSessionException>(() => client.ValidateAsync(credential, default));
        Assert.Equal(expected, error.Failure); Assert.Equal(1, calls);
        Assert.DoesNotContain(Secret, error.ToString()); Assert.DoesNotContain("synthetic-rotated", error.ToString());
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, RobloxSessionFailure.NotProcessed)]
    [InlineData(HttpRequestError.ConnectionError, RobloxSessionFailure.NotProcessed)]
    [InlineData(HttpRequestError.ProxyTunnelError, RobloxSessionFailure.NotProcessed)]
    [InlineData(HttpRequestError.Unknown, RobloxSessionFailure.Network)]
    [InlineData(HttpRequestError.SecureConnectionError, RobloxSessionFailure.Network)]
    [InlineData(HttpRequestError.ResponseEnded, RobloxSessionFailure.Network)]
    [InlineData(HttpRequestError.InvalidResponse, RobloxSessionFailure.Network)]
    public async Task OnlyAConnectionThatNeverReachedRobloxIsNotProcessed(HttpRequestError kind, RobloxSessionFailure expected)
    {
        int calls = 0;
        using var client = new RobloxSessionClient(new Transport((_, _) => { calls++; throw new HttpRequestException(kind, Secret, new IOException(Secret)); }));
        using var credential = new RobloxSessionCredential(Secret);
        var error = await Assert.ThrowsAsync<RobloxSessionException>(() => client.ValidateAsync(credential, default));
        Assert.Equal(expected, error.Failure); Assert.Equal(1, calls);
        Assert.DoesNotContain(Secret, error.ToString()); Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(SocketError.ConnectionRefused)]
    [InlineData(SocketError.HostNotFound)]
    public async Task TheRealHandlersRefusedOrUnresolvedConnectionIsNotProcessed(SocketError socketError)
    {
        // The production handler type with a connect step that fails before any socket opens:
        // proves the platform labels these failures as connect-phase errors. Nothing leaves this PC.
        int connects = 0;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = (_, _) => { Interlocked.Increment(ref connects); throw new SocketException((int)socketError); }
        };
        using var client = new RobloxSessionClient(handler);
        using var credential = new RobloxSessionCredential(Secret);
        var error = await Assert.ThrowsAsync<RobloxSessionException>(() => client.ValidateAsync(credential, default));
        Assert.Equal(RobloxSessionFailure.NotProcessed, error.Failure); Assert.Equal(1, connects); Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task ABodyThatFailsAfterRobloxAnsweredStaysAmbiguous()
    {
        using var client = new RobloxSessionClient(new Transport((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingBody()) })));
        using var credential = new RobloxSessionCredential(Secret);
        var error = await Assert.ThrowsAsync<RobloxSessionException>(() => client.ValidateAsync(credential, default));
        Assert.Equal(RobloxSessionFailure.Network, error.Failure); Assert.DoesNotContain(Secret, error.ToString());
    }

    private sealed class FailingBody : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException(Secret);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => ValueTask.FromException<int>(new IOException(Secret));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.FromException<int>(new IOException(Secret));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task RotationIsOnlyADisposableCandidateWithBoundedExpiry()
    {
        using var client = new RobloxSessionClient(new Transport((_, _) =>
        { var response = Json(); response.Headers.Add("Set-Cookie", ".ROBLOSECURITY=synthetic-rotated; Domain=.roblox.com; Path=/; Secure; HttpOnly; Max-Age=3600"); return Task.FromResult(response); }));
        using var credential = new RobloxSessionCredential(Secret); var result = await client.ValidateAsync(credential, default);
        var candidate = result.RotatedCredential!; Assert.Equal("synthetic-rotated", candidate.CopyValue());
        Assert.InRange(candidate.ExpiresUtc!.Value, DateTimeOffset.UtcNow.AddMinutes(59), DateTimeOffset.UtcNow.AddMinutes(61));
        result.Dispose(); Assert.Throws<ObjectDisposedException>(() => candidate.CopyValue()); Assert.Equal(Secret, credential.CopyValue());
    }

    [Theory]
    [InlineData(".ROBLOSECURITY=rotated; Domain=foreign.invalid")]
    [InlineData(".ROBLOSECURITY=; Max-Age=0")]
    [InlineData(".ROBLOSECURITY=rotated; Max-Age=999999999999999999")]
    public async Task InvalidRotationCannotEscape(string header)
    {
        using var client = new RobloxSessionClient(new Transport((_, _) => { var response = Json(); response.Headers.Add("Set-Cookie", header); return Task.FromResult(response); }));
        using var credential = new RobloxSessionCredential(Secret);
        var error = await Assert.ThrowsAsync<RobloxSessionException>(() => client.ValidateAsync(credential, default));
        Assert.DoesNotContain(header, error.ToString());
    }

    [Fact]
    public async Task RedirectedTransportResponseIsRejected()
    {
        using var client = new RobloxSessionClient(new Transport((_, _) =>
        { var response = Json(); response.RequestMessage = new(HttpMethod.Get, "https://foreign.invalid/"); return Task.FromResult(response); }));
        using var credential = new RobloxSessionCredential(Secret);
        Assert.Equal(RobloxSessionFailure.InvalidResponse, (await Assert.ThrowsAsync<RobloxSessionException>(() => client.ValidateAsync(credential, default))).Failure);
    }

    [Fact]
    public async Task CancellationAndDeadlineAreDistinctAndNetworkExceptionTextIsDiscarded()
    {
        using var credential = new RobloxSessionCredential(Secret);
        using var waiting = new RobloxSessionClient(new Transport(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Json(); }), TimeSpan.FromMilliseconds(20));
        Assert.Equal(RobloxSessionFailure.TimedOut, (await Assert.ThrowsAsync<RobloxSessionException>(() => waiting.ValidateAsync(credential, default))).Failure);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.ValidateAsync(credential, cancelled.Token));
        using var failing = new RobloxSessionClient(new Transport((_, _) => throw new HttpRequestException(Secret)));
        var error = await Assert.ThrowsAsync<RobloxSessionException>(() => failing.ValidateAsync(credential, default));
        Assert.DoesNotContain(Secret, error.ToString()); Assert.Null(error.InnerException);
    }
}
