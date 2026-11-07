using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// One explicit signed-in resolve request for a roblox.com/share link: exact endpoint, no redirects, no cookie jar,
/// one CSRF handshake retry and a 64 KiB body cap. The response shape is observed web-client behaviour, not a
/// documented contract; anything unexpected is Unavailable, never a guess. The request body and the response buffer
/// carry codes, so both are zeroed after use. A rotated session cookie in the response is ignored, never saved.
/// </summary>
public sealed class RobloxShareLinkClient : IRobloxShareLinkClient, IDisposable
{
    public static readonly Uri Endpoint = new("https://apis.roblox.com/sharelinks/v1/resolve-link");
    private const int MaximumBodyBytes = 65536;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public RobloxShareLinkClient(TimeSpan? timeout = null) : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, timeout) { }
    // Injected transports are for controlled tests; production uses the constructor above.
    public RobloxShareLinkClient(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (handler is HttpClientHandler native) { native.AllowAutoRedirect = false; native.UseCookies = false; }
        if (handler is SocketsHttpHandler sockets) { sockets.AllowAutoRedirect = false; sockets.UseCookies = false; }
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        _http = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<RobloxShareLinkResolution> ResolveAsync(RobloxShareLink link, RobloxSessionCredential credential, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(link); ArgumentNullException.ThrowIfNull(credential); ct.ThrowIfCancellationRequested();
        if (link.Type == RobloxShareLinkType.Other) return new(RobloxShareLinkOutcome.Invalid);
        string typeName = link.Type == RobloxShareLinkType.Server ? "Server" : "ExperienceDetails";
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct); stop.CancelAfter(_timeout);
        try
        {
            string? csrf = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var response = await SendAsync(link, typeName, credential, csrf, stop.Token).ConfigureAwait(false);
                if (response.RequestMessage?.RequestUri is { } actual && actual != Endpoint) return new(RobloxShareLinkOutcome.Unavailable);
                if (response.StatusCode == HttpStatusCode.Forbidden && csrf is null && response.Headers.TryGetValues("x-csrf-token", out var tokens)
                    && tokens.FirstOrDefault() is { Length: > 0 and <= 256 } token)
                { csrf = token; continue; } // The documented handshake: repeat once with the issued token, then stop.
                if (response.StatusCode == HttpStatusCode.Unauthorized) return new(RobloxShareLinkOutcome.SignInRequired);
                if (response.StatusCode != HttpStatusCode.OK) return new(RobloxShareLinkOutcome.Unavailable);
                return await ParseAsync(response, link.Type, stop.Token).ConfigureAwait(false);
            }
            return new(RobloxShareLinkOutcome.Unavailable);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(RobloxShareLinkOutcome.Unavailable); }
        catch (HttpRequestException) { return new(RobloxShareLinkOutcome.Unavailable); }
        catch (IOException) { return new(RobloxShareLinkOutcome.Unavailable); }
    }

    private async Task<HttpResponseMessage> SendAsync(RobloxShareLink link, string typeName, RobloxSessionCredential credential, string? csrf, CancellationToken ct)
    {
        string code = ""; link.Code.Reveal(span => code = new string(span));
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new ShareLinkRequest(code, typeName));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("Cookie", ".ROBLOSECURITY=" + credential.CopyValue());
            if (csrf is not null) request.Headers.Add("X-CSRF-TOKEN", csrf);
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    private static async Task<RobloxShareLinkResolution> ParseAsync(HttpResponseMessage response, RobloxShareLinkType type, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaximumBodyBytes) return new(RobloxShareLinkOutcome.Unavailable);
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] buffer = new byte[MaximumBodyBytes + 1]; int length = 0;
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false)) > 0)
            { length += read; if (length > MaximumBodyBytes) return new(RobloxShareLinkOutcome.Unavailable); }
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 8 });
            return Interpret(document.RootElement, type);
        }
        catch (JsonException) { return new(RobloxShareLinkOutcome.Unavailable); }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static RobloxShareLinkResolution Interpret(JsonElement root, RobloxShareLinkType type)
    {
        if (root.ValueKind != JsonValueKind.Object) return new(RobloxShareLinkOutcome.Unavailable);
        string key = type == RobloxShareLinkType.Server ? "privateServerInviteData" : "experienceDetailsInviteData";
        if (!root.TryGetProperty(key, out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("status", out var statusField) || statusField.ValueKind != JsonValueKind.String)
            return new(RobloxShareLinkOutcome.Unavailable);
        switch (statusField.GetString())
        {
            case "Expired": return new(RobloxShareLinkOutcome.Expired);
            case "Invalid": return new(RobloxShareLinkOutcome.Invalid);
            case "Valid": break;
            default: return new(RobloxShareLinkOutcome.Unavailable);
        }
        // Roblox's web page reads only universeId here and opens the universe's root place, so placeId is optional
        // (null when absent; RootPlaceShareLinkResolver fills it). A present placeId must still be a positive number.
        if (!TryId(data, "universeId", out long universeId) || !TryOptionalId(data, "placeId", out long? placeId)) return new(RobloxShareLinkOutcome.Unavailable);
        if (type == RobloxShareLinkType.ExperienceDetails) return new(RobloxShareLinkOutcome.Experience, placeId, universeId);
        long? owner = null;
        if (data.TryGetProperty("ownerUserId", out var ownerField) && ownerField.ValueKind != JsonValueKind.Null)
        {
            if (ownerField.ValueKind != JsonValueKind.Number || !ownerField.TryGetInt64(out long ownerId) || ownerId <= 0) return new(RobloxShareLinkOutcome.Unavailable);
            owner = ownerId;
        }
        if (!data.TryGetProperty("linkCode", out var codeField) || codeField.ValueKind != JsonValueKind.String) return new(RobloxShareLinkOutcome.Unavailable);
        ProtectedPrivateServerCode code;
        try { code = new ProtectedPrivateServerCode(codeField.GetString()); }
        catch (ArgumentException) { return new(RobloxShareLinkOutcome.Unavailable); }
        return new(RobloxShareLinkOutcome.PrivateServer, placeId, universeId, owner, code);
    }

    private static bool TryId(JsonElement item, string name, out long value)
    { value = 0; return item.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out value) && value > 0; }
    private static bool TryOptionalId(JsonElement item, string name, out long? value)
    {
        value = null;
        if (!item.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return true;
        if (!TryId(item, name, out long id)) return false;
        value = id; return true;
    }

    private sealed record ShareLinkRequest(string linkId, string linkType);
    public void Dispose() => _http.Dispose();
}
