using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// One explicit signed-in resolve request for a roblox.com/share link: exact endpoint (sharelinks/v1/resolve-link, or
/// deeplinks/v2/resolve for a v2 link), no redirects, no cookie jar,
/// one CSRF handshake retry and a 64 KiB body cap. The CSRF token Roblox last accepted is kept in memory per scope (profile
/// plus verified account) and sent first, so a repeat resolve is normally one request instead of two; it is never saved,
/// logged or sent for another scope, and is dropped on any failure, on Forget and on Dispose. The response shape is observed web-client behaviour, not a
/// documented contract; anything unexpected (including a repeated key) is Unavailable, never a guess. The request body and the response buffer
/// carry codes, so both are zeroed after use. A rotated session cookie in the response is ignored, never saved.
/// </summary>
public sealed class RobloxShareLinkClient : IRobloxShareLinkClient, IDisposable
{
    public static readonly Uri Endpoint = new("https://apis.roblox.com/sharelinks/v1/resolve-link");
    public static readonly Uri V2Endpoint = new("https://apis.roblox.com/deeplinks/v2/resolve");
    private const int MaximumBodyBytes = 65536;
    internal const int MaximumKeptTokens = 256; // One short token per signed-in account; when full, all are dropped (bounded memory).
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly object _tokenGate = new();
    private readonly Dictionary<RobloxShareLinkTokenScope, string> _tokens = [];
    private long _tokenEpoch; // Bumped by every Forget, so a resolve still in flight cannot keep a token afterwards.

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

    /// <summary>Sign-out of one profile: its kept tokens are dropped and no resolve already running can keep one.</summary>
    public void ForgetTokens(Guid profileId)
    {
        lock (_tokenGate)
        {
            _tokenEpoch++;
            foreach (var scope in _tokens.Keys) if (scope.ProfileId == profileId) _tokens.Remove(scope);
        }
    }

    /// <summary>Lock, Panic and shutdown: every kept token is dropped and no resolve already running can keep one.</summary>
    public void ForgetAllTokens() { lock (_tokenGate) { _tokenEpoch++; _tokens.Clear(); } }

    public async Task<RobloxShareLinkResolution> ResolveAsync(RobloxShareLink link, RobloxSessionCredential credential, RobloxShareLinkTokenScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(link); ArgumentNullException.ThrowIfNull(credential); ct.ThrowIfCancellationRequested();
        if (!scope.IsValid) throw new ArgumentException("A share link is resolved for one saved profile and its verified Roblox account.", nameof(scope));
        // A v2 link has no type: its code alone goes to the deeplinks/v2 resolver. Every other kind names its type to v1.
        string? typeName = link.Type switch
        {
            RobloxShareLinkType.Server => "Server",
            RobloxShareLinkType.ExperienceDetails => "ExperienceDetails",
            RobloxShareLinkType.ExperienceInvite => "ExperienceInvite",
            RobloxShareLinkType.NotificationExperienceInvite => "NotificationExperienceInvite",
            _ => null
        };
        if (typeName is null && link.Type != RobloxShareLinkType.DeepLinkV2) return new(RobloxShareLinkOutcome.Invalid);
        Uri endpoint = typeName is null ? V2Endpoint : Endpoint;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct); stop.CancelAfter(_timeout);
        string? csrf = KeptToken(scope, out long epoch); bool accepted = false;
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var response = await SendAsync(link, endpoint, typeName, credential, csrf, stop.Token).ConfigureAwait(false);
                if (response.RequestMessage?.RequestUri is { } actual && actual != endpoint) return new(RobloxShareLinkOutcome.Unavailable);
                // The documented handshake: a missing or refused token gets one retry with the issued token, then stop.
                if (response.StatusCode == HttpStatusCode.Forbidden && attempt == 0 && response.Headers.TryGetValues("x-csrf-token", out var tokens)
                    && tokens.FirstOrDefault() is { Length: > 0 and <= 256 } token)
                { csrf = token; continue; }
                if (response.StatusCode == HttpStatusCode.Unauthorized) return new(RobloxShareLinkOutcome.SignInRequired);
                if (response.StatusCode != HttpStatusCode.OK) return new(RobloxShareLinkOutcome.Unavailable);
                accepted = true;
                return await ParseAsync(response, link.Type, stop.Token).ConfigureAwait(false);
            }
            return new(RobloxShareLinkOutcome.Unavailable);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(RobloxShareLinkOutcome.Unavailable); }
        catch (HttpRequestException) { return new(RobloxShareLinkOutcome.Unavailable); }
        catch (IOException) { return new(RobloxShareLinkOutcome.Unavailable); }
        finally { Keep(scope, epoch, accepted ? csrf : null); }
    }

    private string? KeptToken(RobloxShareLinkTokenScope scope, out long epoch)
    { lock (_tokenGate) { epoch = _tokenEpoch; return _tokens.GetValueOrDefault(scope); } }

    // Only a token Roblox just accepted is kept; any other ending drops the scope's token. A Forget since the resolve
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

    private async Task<HttpResponseMessage> SendAsync(RobloxShareLink link, Uri endpoint, string? typeName, RobloxSessionCredential credential, string? csrf, CancellationToken ct)
    {
        string code = ""; link.Code.Reveal(span => code = new string(span));
        byte[] body = typeName is null ? JsonSerializer.SerializeToUtf8Bytes(new DeepLinkRequest(code)) : JsonSerializer.SerializeToUtf8Bytes(new ShareLinkRequest(code, typeName));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
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
            // A repeated key (two linkCode or status fields) is ambiguous: the parser rejects it and the catch below maps it to Unavailable.
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 8, AllowDuplicateProperties = false });
            return Interpret(document.RootElement, type);
        }
        catch (JsonException) { return new(RobloxShareLinkOutcome.Unavailable); }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static RobloxShareLinkResolution Interpret(JsonElement root, RobloxShareLinkType type)
    {
        if (root.ValueKind != JsonValueKind.Object) return new(RobloxShareLinkOutcome.Unavailable);
        switch (type)
        {
            case RobloxShareLinkType.DeepLinkV2: return InterpretDeepLink(root);
            case RobloxShareLinkType.ExperienceInvite: return InterpretInvite(root, "experienceInviteData");
            case RobloxShareLinkType.NotificationExperienceInvite: return InterpretInvite(root, "notificationExperienceInviteData");
        }
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

    /// <summary>
    /// A friend invite. Roblox's web page joins the inviter's server only while the invite is Valid and otherwise opens
    /// the invite's place, so Expired and InviterNotInExperience fall back to the game without a server. Only status,
    /// placeId, an optional universeId and (when Valid) instanceId are read; inviterId and launchData are never kept.
    /// </summary>
    private static RobloxShareLinkResolution InterpretInvite(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("status", out var statusField) || statusField.ValueKind != JsonValueKind.String)
            return new(RobloxShareLinkOutcome.Unavailable);
        string? status = statusField.GetString();
        if (status == "Invalid") return new(RobloxShareLinkOutcome.Invalid);
        if (status is not ("Valid" or "Expired" or "InviterNotInExperience")) return new(RobloxShareLinkOutcome.Unavailable);
        if (!TryId(data, "placeId", out long placeId) || !TryOptionalId(data, "universeId", out long? universeId))
            return new(status == "Expired" ? RobloxShareLinkOutcome.Expired : RobloxShareLinkOutcome.Unavailable); // Nothing to open.
        if (status != "Valid" || !data.TryGetProperty("instanceId", out var instance) || instance.ValueKind == JsonValueKind.Null
            || instance.ValueKind == JsonValueKind.String && instance.GetString() is { Length: 0 })
            return new(RobloxShareLinkOutcome.Experience, placeId, universeId);
        // A Valid invite that names a server must name a well-formed one; anything else is unexpected, never a guess.
        if (instance.ValueKind != JsonValueKind.String || !RobloxRequestedDestination.TryNormalizeJobId(instance.GetString(), out string? jobId))
            return new(RobloxShareLinkOutcome.Unavailable);
        return new(RobloxShareLinkOutcome.ServerInstance, placeId, universeId, jobId: jobId);
    }

    /// <summary>
    /// A v2 link. Only linkType Experience leads into a game, and its targetId is the place ID; customData (launch data)
    /// is never read. An absent status is accepted; Invalid and Expired are reported, any other status is Unavailable.
    /// </summary>
    private static RobloxShareLinkResolution InterpretDeepLink(JsonElement root)
    {
        if (root.TryGetProperty("status", out var statusField) && statusField.ValueKind != JsonValueKind.Null)
        {
            if (statusField.ValueKind != JsonValueKind.String) return new(RobloxShareLinkOutcome.Unavailable);
            switch (statusField.GetString())
            {
                case "Invalid": return new(RobloxShareLinkOutcome.Invalid);
                case "Expired": return new(RobloxShareLinkOutcome.Expired);
                case "Valid": break;
                default: return new(RobloxShareLinkOutcome.Unavailable);
            }
        }
        if (!root.TryGetProperty("linkType", out var typeField) || typeField.ValueKind != JsonValueKind.String) return new(RobloxShareLinkOutcome.Unavailable);
        if (typeField.GetString() != "Experience") return new(RobloxShareLinkOutcome.Invalid); // Studio, referral and other v2 links are not games.
        return TryId(root, "targetId", out long placeId) ? new(RobloxShareLinkOutcome.Experience, placeId) : new(RobloxShareLinkOutcome.Unavailable);
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
    private sealed record DeepLinkRequest(string linkId);
    public void Dispose() { ForgetAllTokens(); _http.Dispose(); }
}
