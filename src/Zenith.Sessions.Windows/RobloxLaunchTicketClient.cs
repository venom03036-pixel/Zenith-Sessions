using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// One explicit signed-in launch-ticket request for the Direct ticket mode (R-api-7, owner decision of 2026-11-08), with the
/// same discipline as the share-link resolver: exact endpoints (GET auth.roblox.com/v1/client-assertion/, then POST
/// auth.roblox.com/v1/authentication-ticket/), no redirects, no cookie jar, one anti-forgery (CSRF) handshake retry, a
/// 64 KiB body cap and a bounded deadline. The CSRF token Roblox last accepted is kept in memory per identity (profile plus
/// verified account) and sent first, never saved, logged or sent for another identity, and dropped on any failure, on Forget
/// and on Dispose. Zenith uses its own User-Agent, with the Roblox Origin/Referer required by this web launch protocol;
/// the RBXAuthenticationNegotiation protocol header the ticket endpoint expects is
/// sent on the ticket request only. The assertion is optional, as on the website: an unusable assertion answer still asks
/// for a ticket with an empty JSON body, while a refused session (401), a human check or a rate limit stops at once. The
/// ticket is read from the rbx-authentication-ticket response header into a one-use protected value; the body buffers that
/// carried the assertion are zeroed. A replacement session cookie in either response is read with the identity check's own
/// parser and handed back unverified; this client never saves it. Endpoint shapes are observed web-client behaviour, not a
/// documented contract: anything unexpected is Unavailable, never a guess.
/// </summary>
public sealed class RobloxLaunchTicketClient : IRobloxLaunchTicketClient, IDisposable
{
    public static readonly Uri AssertionEndpoint = new("https://auth.roblox.com/v1/client-assertion/");
    public static readonly Uri TicketEndpoint = new("https://auth.roblox.com/v1/authentication-ticket/");
    /// <summary>Zenith's own name on these requests; no browser User-Agent is impersonated.</summary>
    public const string UserAgent = "ZenithSessions/0.50";
    private const string TicketHeader = "rbx-authentication-ticket", NegotiationHeader = "RBXAuthenticationNegotiation", CsrfHeader = "X-CSRF-TOKEN";
    private const int MaximumBodyBytes = 65536, MaximumAssertionCharacters = 16384;
    internal const int MaximumKeptTokens = 256; // One short token per signed-in account; when full, all are dropped (bounded memory).
    private static readonly byte[] EmptyBody = "{}"u8.ToArray();
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly object _tokenGate = new();
    private readonly Dictionary<RobloxShareLinkTokenScope, string> _tokens = [];
    private long _tokenEpoch; // Bumped by every Forget, so a request still in flight cannot keep a token afterwards.
    private readonly TimeProvider _time;
    private readonly Uri _assertionEndpoint = AssertionEndpoint, _ticketEndpoint = TicketEndpoint;

    public RobloxLaunchTicketClient(TimeSpan? timeout = null) : this(CreateHandler(), timeout) { }
    // Test-only: the production handler over a real loopback socket. Only the origin moves; the paths stay the production
    // ones. The origin is checked before the handler exists, so a refused origin leaves nothing to dispose.
    internal RobloxLaunchTicketClient(Uri loopbackOrigin, TimeSpan? timeout = null) : this(LoopbackTestOrigin.Require(loopbackOrigin), timeout) { }
    private RobloxLaunchTicketClient(string loopbackOrigin, TimeSpan? timeout) : this(CreateHandler(), timeout)
    { _assertionEndpoint = new(loopbackOrigin + AssertionEndpoint.PathAndQuery); _ticketEndpoint = new(loopbackOrigin + TicketEndpoint.PathAndQuery); }
    private static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false, UseCookies = false };
    // Injected transports are for controlled tests; production uses the timeout-only constructor and its CreateHandler.
    public RobloxLaunchTicketClient(HttpMessageHandler handler, TimeSpan? timeout = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (handler is HttpClientHandler native) { native.AllowAutoRedirect = false; native.UseCookies = false; }
        if (handler is SocketsHttpHandler sockets) { sockets.AllowAutoRedirect = false; sockets.UseCookies = false; }
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
        _time = timeProvider ?? TimeProvider.System;
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        _http = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Sign-out of one profile: its kept tokens are dropped and no request already running can keep one.</summary>
    public void ForgetTokens(Guid profileId)
    {
        lock (_tokenGate)
        {
            _tokenEpoch++;
            foreach (var scope in _tokens.Keys) if (scope.ProfileId == profileId) _tokens.Remove(scope);
        }
    }

    /// <summary>Lock, Panic and shutdown: every kept token is dropped and no request already running can keep one.</summary>
    public void ForgetAllTokens() { lock (_tokenGate) { _tokenEpoch++; _tokens.Clear(); } }

    public async Task<RobloxLaunchTicketResult> RequestAsync(RobloxSessionCredential credential, RobloxShareLinkTokenScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credential); ct.ThrowIfCancellationRequested();
        if (!scope.IsValid) throw new ArgumentException("A launch ticket is requested for one saved profile and its verified Roblox account.", nameof(scope));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct); stop.CancelAfter(_timeout);
        string? csrf = KeptToken(scope, out long epoch); bool accepted = false;
        // Owned here until it moves onto the returned answer; any other exit disposes it.
        RobloxSessionCredential? rotated = null; byte[]? assertion = null;
        try
        {
            // The documented client assertion first. Only a refused session, a human check or a rate limit stops here; any other
            // answer (a refusal, a server error, an unreadable body) means the ticket is asked for without one, as the website does.
            using (var response = await SendAsync(HttpMethod.Get, _assertionEndpoint, null, credential, null, stop.Token).ConfigureAwait(false))
            {
                if (response.RequestMessage?.RequestUri is { } actual && actual != _assertionEndpoint) return Answer(new(RobloxLaunchTicketOutcome.Unavailable));
                rotated = Newest(rotated, response, credential);
                var early = Refusal(response);
                if (early is RobloxLaunchTicketOutcome.SignInRequired or RobloxLaunchTicketOutcome.Challenge or RobloxLaunchTicketOutcome.RateLimited)
                    return Answer(new(early.Value));
                if (response.StatusCode == HttpStatusCode.OK) assertion = await ReadAssertionBodyAsync(response, stop.Token).ConfigureAwait(false);
            }
            byte[] body = assertion ?? EmptyBody;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var response = await SendAsync(HttpMethod.Post, _ticketEndpoint, body, rotated ?? credential, csrf, stop.Token).ConfigureAwait(false);
                if (response.RequestMessage?.RequestUri is { } actual && actual != _ticketEndpoint) return Answer(new(RobloxLaunchTicketOutcome.Unavailable));
                rotated = Newest(rotated, response, credential);
                // The documented handshake: a missing or refused token gets one retry with the issued token, then stop.
                if (response.StatusCode == HttpStatusCode.Forbidden && attempt == 0 && !HasChallenge(response)
                    && response.Headers.TryGetValues("x-csrf-token", out var tokens) && tokens.FirstOrDefault() is { Length: > 0 and <= 256 } token)
                { csrf = token; continue; }
                if (Refusal(response) is { } refused) return Answer(new(refused));
                if (response.StatusCode != HttpStatusCode.OK) return Answer(new(RobloxLaunchTicketOutcome.Unavailable));
                accepted = true;
                return Answer(Issued(response));
            }
            return Answer(new(RobloxLaunchTicketOutcome.Unavailable));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Answer(new(RobloxLaunchTicketOutcome.Unavailable)); }
        catch (HttpRequestException) { return Answer(new(RobloxLaunchTicketOutcome.Unavailable)); }
        catch (IOException) { return Answer(new(RobloxLaunchTicketOutcome.Unavailable)); }
        catch (RobloxSessionException)
        {
            // A deleted/malformed replacement invalidates the chain. Never retry with an older cookie or return
            // an earlier replacement as usable when the server has explicitly replaced it again.
            rotated?.Dispose(); rotated = null;
            return new(RobloxLaunchTicketOutcome.SignInRequired);
        }
        finally
        {
            Keep(scope, epoch, accepted ? csrf : null); rotated?.Dispose();
            if (assertion is not null) CryptographicOperations.ZeroMemory(assertion);
        }

        // Moves the replacement session onto the answer, whatever the answer is: Roblox may already have replaced the session.
        RobloxLaunchTicketResult Answer(RobloxLaunchTicketResult result)
        {
            if (rotated is not { } candidate) return result;
            rotated = null;
            return new(result.Outcome, result.TakeTicket(), candidate);
        }
    }

    // A refusal Roblox typed for us. A human check wins over its status code, as in the saved-session check.
    private static RobloxLaunchTicketOutcome? Refusal(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return null;
        if (HasChallenge(response)) return RobloxLaunchTicketOutcome.Challenge;
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => RobloxLaunchTicketOutcome.SignInRequired,
            HttpStatusCode.Forbidden => RobloxLaunchTicketOutcome.Refused,
            HttpStatusCode.TooManyRequests => RobloxLaunchTicketOutcome.RateLimited,
            _ => null
        };
    }
    private static bool HasChallenge(HttpResponseMessage response) => response.Headers.Contains("rblx-challenge-id") || response.Headers.Contains("rblx-challenge-type");

    // Exactly one readable ticket header, within the ticket bounds; anything else is no ticket.
    private static RobloxLaunchTicketResult Issued(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(TicketHeader, out var values)) return new(RobloxLaunchTicketOutcome.Unavailable);
        string? ticket = null; int count = 0;
        foreach (string value in values) { count++; ticket = value; }
        if (count != 1 || ticket is null || !ProtectedLaunchTicket.IsValid(ticket)) return new(RobloxLaunchTicketOutcome.Unavailable);
        return new(RobloxLaunchTicketOutcome.Issued, new ProtectedLaunchTicket(ticket));
    }

    /// <summary>
    /// The newest session cookie wins, as in a browser's cookie store: a response without one keeps the earlier
    /// candidate. An unusable replacement stops the entire negotiation; it cannot fall back to an older cookie.
    /// </summary>
    private RobloxSessionCredential? Newest(RobloxSessionCredential? earlier, HttpResponseMessage response, RobloxSessionCredential original)
    {
        var next = RobloxSessionClient.Rotation(response, earlier ?? original, _time.GetUtcNow());
        if (next is null) return earlier;
        earlier?.Dispose(); return next;
    }

    private string? KeptToken(RobloxShareLinkTokenScope scope, out long epoch)
    { lock (_tokenGate) { epoch = _tokenEpoch; return _tokens.GetValueOrDefault(scope); } }

    // Only a token Roblox just accepted is kept; any other ending drops the identity's token. A Forget since the request
    // began wins, so nothing is kept after sign-out, Lock, Panic or Dispose.
    private void Keep(RobloxShareLinkTokenScope scope, long epoch, string? accepted)
    {
        lock (_tokenGate)
        {
            if (epoch != _tokenEpoch) return;
            if (accepted is null) { _tokens.Remove(scope); return; }
            if (_tokens.Count >= MaximumKeptTokens && !_tokens.ContainsKey(scope)) _tokens.Clear();
            _tokens[scope] = accepted;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri endpoint, byte[]? body, RobloxSessionCredential credential, string? csrf, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, endpoint);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add(NegotiationHeader, "1");
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Referrer = new Uri("https://www.roblox.com/");
        request.Headers.Add("Origin", "https://www.roblox.com");
        request.Headers.Add("Cookie", ".ROBLOSECURITY=" + credential.CopyValue());
        if (csrf is not null) request.Headers.Add(CsrfHeader, csrf);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    // The assertion answer as the ticket request's body ({"clientAssertion": ...}), or null when it is not the documented
    // one string in one object (a repeated key included). Every buffer that held it is zeroed; the returned body is the caller's to zero.
    private static async Task<byte[]?> ReadAssertionBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaximumBodyBytes) return null;
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] buffer = new byte[MaximumBodyBytes + 1]; int length = 0;
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false)) > 0)
            { length += read; if (length > MaximumBodyBytes) return null; }
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 8, AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("clientAssertion", out var field)
                || field.ValueKind != JsonValueKind.String || field.GetString() is not { Length: > 0 and <= MaximumAssertionCharacters } assertion
                || assertion.Any(character => character is < '!' or > '~')) return null;
            return JsonSerializer.SerializeToUtf8Bytes(new AssertionBody(assertion));
        }
        catch (JsonException) { return null; }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private sealed record AssertionBody(string clientAssertion);
    public void Dispose() { ForgetAllTokens(); _http.Dispose(); }
}
