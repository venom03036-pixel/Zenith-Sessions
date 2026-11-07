using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>One explicit selected-session identity request. No OAuth, cookie jar, redirects or retries.</summary>
public sealed class RobloxSessionClient : IRobloxSessionClient, IDisposable
{
    public static readonly Uri IdentityEndpoint = new("https://users.roblox.com/v1/users/authenticated");
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _time;
    public RobloxSessionClient(TimeSpan? timeout = null, TimeProvider? timeProvider = null) : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, timeout, timeProvider) { }
    // Injected transports are for controlled tests; production uses the constructor above.
    public RobloxSessionClient(HttpMessageHandler handler, TimeSpan? timeout = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (handler is HttpClientHandler native) { native.AllowAutoRedirect = false; native.UseCookies = false; }
        if (handler is SocketsHttpHandler sockets) { sockets.AllowAutoRedirect = false; sockets.UseCookies = false; }
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
        _time = timeProvider ?? TimeProvider.System;
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        _http = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<RobloxSessionValidation> ValidateAsync(RobloxSessionCredential credential, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credential); ct.ThrowIfCancellationRequested();
        if (credential.ExpiresUtc is { } expiry && expiry <= _time.GetUtcNow()) throw Failure(RobloxSessionFailure.InvalidSession);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct); stop.CancelAfter(_timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, IdentityEndpoint);
            request.Headers.Add("Cookie", ".ROBLOSECURITY=" + credential.CopyValue());
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } actual && actual != IdentityEndpoint) throw Failure(RobloxSessionFailure.InvalidResponse);
            if (!response.IsSuccessStatusCode)
            {
                if (response.Headers.Contains("rblx-challenge-id") || response.Headers.Contains("rblx-challenge-type")) throw Failure(RobloxSessionFailure.Challenge);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw Failure(RobloxSessionFailure.InvalidSession);
                // A busy refusal without any session cookie did not replace the session. Gateway errors
                // (500/502/504) can follow upstream processing, so they stay ambiguous like a timeout.
                throw Failure((response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable) && !CarriesSessionCookie(response)
                    ? RobloxSessionFailure.NotProcessed : RobloxSessionFailure.Network);
            }
            using var json = await ReadJsonAsync(response, stop.Token).ConfigureAwait(false);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Failure(RobloxSessionFailure.InvalidResponse);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in root.EnumerateObject()) if (!names.Add(field.Name)) throw Failure(RobloxSessionFailure.InvalidResponse);
            if (!root.TryGetProperty("id", out var idField) || !idField.TryGetInt64(out long id) || id <= 0
                || !root.TryGetProperty("name", out var nameField) || nameField.ValueKind != JsonValueKind.String
                || nameField.GetString() is not { Length: > 0 and <= 20 } name
                || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) throw Failure(RobloxSessionFailure.InvalidResponse);
            stop.Token.ThrowIfCancellationRequested();
            var identity = new LinkedIdentity(id, name, RobloxSessionIdentity.Issuer, id.ToString(CultureInfo.InvariantCulture));
            return new(identity, Rotation(response, credential, _time.GetUtcNow()));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Failure(RobloxSessionFailure.TimedOut); }
        // Connect-phase failures: the request, and so the cookie, never reached Roblox.
        catch (HttpRequestException error) when (error.HttpRequestError is HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError or HttpRequestError.ProxyTunnelError) { throw Failure(RobloxSessionFailure.NotProcessed); }
        catch (HttpRequestException) { throw Failure(RobloxSessionFailure.Network); }
        catch (IOException) { throw Failure(RobloxSessionFailure.Network); }
        catch (JsonException) { throw Failure(RobloxSessionFailure.InvalidResponse); }
        catch (InvalidOperationException) { throw Failure(RobloxSessionFailure.InvalidResponse); }
    }

    // Deliberately broader than Rotation's parser: any header naming the session cookie, in any
    // case or form, counts as possible replacement and keeps the outcome ambiguous.
    private static bool CarriesSessionCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var headers)
        && headers.Any(header => header.Contains("ROBLOSECURITY", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The one replacement .ROBLOSECURITY a signed-in response may set, as an unverified disposable candidate; null when
    /// there is none or it repeats the original. Shared with the share-link client so every signed-in request reads a
    /// rotation the same way. A foreign domain, a duplicate, a deletion or an unreadable lifetime throws
    /// RobloxSessionException (InvalidResponse or InvalidSession) without the header text.
    /// </summary>
    internal static RobloxSessionCredential? Rotation(HttpResponseMessage response, RobloxSessionCredential original, DateTimeOffset now)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers)) return null;
        RobloxSessionCredential? candidate = null; int count = 0, sessionCookies = 0;
        try
        {
            foreach (string header in headers)
            {
                if (++count > 16 || header.Length > 32768) throw Failure(RobloxSessionFailure.InvalidResponse);
                var parts = header.Split(';');
                if (!parts[0].TrimStart().StartsWith(".ROBLOSECURITY=", StringComparison.Ordinal)) continue;
                if (++sessionCookies > 1) throw Failure(RobloxSessionFailure.InvalidResponse);
                string value = parts[0].Trim()[".ROBLOSECURITY=".Length..]; DateTimeOffset? expiry = null; long? maximumAge = null;
                foreach (string attribute in parts.Skip(1))
                {
                    string part = attribute.Trim();
                    if (part.StartsWith("Domain=", StringComparison.OrdinalIgnoreCase)
                        && !part[7..].TrimStart('.').Equals("roblox.com", StringComparison.OrdinalIgnoreCase)) throw Failure(RobloxSessionFailure.InvalidResponse);
                    if (part.StartsWith("Expires=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!DateTimeOffset.TryParse(part[8..], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)) throw Failure(RobloxSessionFailure.InvalidResponse);
                        expiry = time;
                    }
                    if (part.StartsWith("Max-Age=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!long.TryParse(part[8..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long seconds) || seconds <= 0)
                            throw Failure(RobloxSessionFailure.InvalidSession);
                        maximumAge = seconds;
                    }
                }
                if (maximumAge is { } age)
                {
                    // Whole-second cookie lifetimes need an integer bound: TotalSeconds can round
                    // up near MaxValue and admit an expiry one tick beyond the representable range.
                    long remainingTicks = DateTimeOffset.MaxValue.UtcTicks - now.UtcTicks;
                    if (age > remainingTicks / TimeSpan.TicksPerSecond) throw Failure(RobloxSessionFailure.InvalidResponse);
                    expiry = now.ToUniversalTime().AddTicks(age * TimeSpan.TicksPerSecond);
                }
                if (value.Length == 0 || expiry is { } until && until <= now) throw Failure(RobloxSessionFailure.InvalidSession);
                if (value == original.CopyValue()) continue;
                try { candidate = new(value, expiry); }
                catch (ArgumentException) { throw Failure(RobloxSessionFailure.InvalidResponse); }
            }
            return candidate;
        }
        catch { candidate?.Dispose(); throw; }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        const int limit = 65536;
        if (response.Content.Headers.ContentLength > limit) throw Failure(RobloxSessionFailure.InvalidResponse);
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream(); byte[] chunk = new byte[4096];
        try
        {
            int read;
            while ((read = await input.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            { if (buffer.Length + read > limit) throw Failure(RobloxSessionFailure.InvalidResponse); buffer.Write(chunk, 0, read); }
            buffer.Position = 0;
            return await JsonDocument.ParseAsync(buffer, new JsonDocumentOptions { MaxDepth = 8 }, ct).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(chunk); CryptographicOperations.ZeroMemory(buffer.GetBuffer()); }
    }
    private static RobloxSessionException Failure(RobloxSessionFailure kind) => new(kind, kind switch
    {
        RobloxSessionFailure.InvalidSession => "This saved Roblox session needs sign-in. No native client was started.",
        RobloxSessionFailure.Challenge => "Roblox requires a human sign-in check. Complete sign-in in the official window.",
        RobloxSessionFailure.Network => "Roblox could not be reached. The session's current validity is unknown.",
        RobloxSessionFailure.TimedOut => "The Roblox session check timed out. Its current validity is unknown.",
        RobloxSessionFailure.NotProcessed => "Roblox could not be reached or was busy, so the session was not checked. Its current validity is unknown.",
        _ => "Roblox returned an unsupported session response. No replacement session was saved."
    });
    public void Dispose() => _http.Dispose();
}
