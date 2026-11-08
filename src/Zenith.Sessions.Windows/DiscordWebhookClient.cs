using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// Posts one composed message to a Discord webhook: https only, to the exact address the protected holder reveals for that request,
/// no redirects, no cookie jar, no credentials, Zenith's own User-Agent (never a Referer or Origin), a 10 s deadline. Only the status
/// code and Retry-After are read; the body is never read, so Discord's error text (which can name the webhook) is never kept. The
/// test-only loopback seam moves the origin alone, through <see cref="LoopbackTestOrigin"/>, so the production handler is exercised
/// over a real socket and a token can never travel over plain http to another host.
/// </summary>
public sealed class DiscordWebhookClient : IDiscordWebhookTransport
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    /// <summary>"Zenith-Sessions/major.minor": Zenith identifies itself honestly.</summary>
    public static readonly string UserAgent = "Zenith-Sessions/" + ProductVersion();
    private readonly HttpClient _http;
    private readonly string? _loopbackOrigin;

    public DiscordWebhookClient() : this(CreateHandler(), null) { }
    internal DiscordWebhookClient(Uri loopbackOrigin) : this(CreateHandler(), LoopbackTestOrigin.Require(loopbackOrigin)) { }
    /// <summary>Injected transports are for controlled tests; production uses the parameterless constructor and its handler.</summary>
    public DiscordWebhookClient(HttpMessageHandler handler) : this(handler, null) { }
    private DiscordWebhookClient(HttpMessageHandler handler, string? loopbackOrigin)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (handler is HttpClientHandler native) { native.AllowAutoRedirect = false; native.UseCookies = false; }
        if (handler is SocketsHttpHandler sockets) { sockets.AllowAutoRedirect = false; sockets.UseCookies = false; }
        _http = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _loopbackOrigin = loopbackOrigin;
    }

    // The Windows system proxy, like every other outbound client here; no proxy credentials are set.
    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseCookies = false, Credentials = null, PreAuthenticate = false,
        MaxConnectionsPerServer = 1, PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    public async Task<AlertDeliveryResult> SendAsync(DiscordWebhookAddress address, string content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(address); ArgumentNullException.ThrowIfNull(content); ct.ThrowIfCancellationRequested();
        Uri target = Target(address);
        // allowed_mentions.parse [] : nothing in an account label can page @everyone, a role or a user.
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new Payload(content, new([])));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct); stop.CancelAfter(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            request.Headers.UserAgent.ParseAdd(UserAgent);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token).ConfigureAwait(false);
            int code = (int)response.StatusCode;
            if (code is >= 200 and < 300) return new(AlertDeliveryStatus.Delivered, code);
            if (code == 429) return new(AlertDeliveryStatus.RateLimited, code, RetryAfter(response));
            if (code is 401 or 403 or 404) return new(AlertDeliveryStatus.Rejected, code);
            if (code is >= 400 and < 500) return new(AlertDeliveryStatus.Refused, code);
            return new(AlertDeliveryStatus.Unavailable, code); // 3xx (never followed) and 5xx.
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(AlertDeliveryStatus.Unavailable, ErrorType: typeof(TimeoutException)); }
        catch (HttpRequestException error) { return new(AlertDeliveryStatus.Unavailable, ErrorType: error.GetType()); }
        catch (IOException error) { return new(AlertDeliveryStatus.Unavailable, ErrorType: error.GetType()); }
    }

    /// <summary>Discord's Retry-After as a wait, or null when there is none or it cannot be read; the outbox bounds it.</summary>
    internal static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null) return null;
        if (header.Delta is { } delta) return delta;
        return header.Date is { } date ? date - DateTimeOffset.UtcNow : null;
    }

    public static TimeSpan BoundedRetryAfter(TimeSpan? retryAfter) => AlertMessages.BoundedRetryAfter(retryAfter);

    private Uri Target(DiscordWebhookAddress address)
    {
        string url = ""; address.Reveal(span => url = new string(span));
        var production = new Uri(url, UriKind.Absolute);
        return _loopbackOrigin is null ? production : new Uri(_loopbackOrigin + production.PathAndQuery, UriKind.Absolute);
    }

    private static string ProductVersion()
    {
        var version = (Assembly.GetEntryAssembly() ?? typeof(DiscordWebhookClient).Assembly).GetName().Version;
        return version is null ? "1.0" : version.Major.ToString(CultureInfo.InvariantCulture) + "." + version.Minor.ToString(CultureInfo.InvariantCulture);
    }

    public ValueTask DisposeAsync() { _http.Dispose(); return ValueTask.CompletedTask; }

    private sealed record Payload([property: JsonPropertyName("content")] string Content, [property: JsonPropertyName("allowed_mentions")] AllowedMentions AllowedMentions);
    private sealed record AllowedMentions([property: JsonPropertyName("parse")] string[] Parse);
}
