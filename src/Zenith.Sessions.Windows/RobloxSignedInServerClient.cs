using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>Explicit, bounded signed-in requests. The anonymous directory stays anonymous.</summary>
public sealed class RobloxSignedInServerClient : IRobloxSignedInServerClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    public RobloxSignedInServerClient(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        handler ??= new HttpClientHandler();
        if (handler is HttpClientHandler client) { client.AllowAutoRedirect = false; client.UseCookies = false; client.Credentials = null; client.MaxResponseHeadersLength=16; }
        if (handler is SocketsHttpHandler sockets) { sockets.AllowAutoRedirect = false; sockets.UseCookies = false; sockets.Credentials = null; sockets.MaxResponseHeadersLength=16; }
        _http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public Task<SignedInServerResponse> BrowseAsync(SignedInServerQuery query, long expectedUserId, RobloxSessionCredential credential, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query); query.Validated();
        if (expectedUserId <= 0) throw new ArgumentOutOfRangeException(nameof(expectedUserId));
        string place = query.PlaceId.ToString(CultureInfo.InvariantCulture);
        string route = query.Order == SignedInServerOrder.OwnedPrivate
            ? $"/v1/games/{place}/private-servers?sortOrder=Desc&excludeFullGames=false&limit=25"
            : $"/v2/games/{place}/servers/Public?orderBy={query.Order}&excludeFullGames={(query.ExcludeFull ? "true" : "false")}&limit=25";
        if (query.Cursor is not null) route += "&cursor=" + Uri.EscapeDataString(query.Cursor);
        return SendAsync(HttpMethod.Get, new("https://games.roblox.com" + route), credential, null,
            root => ParsePage(root, query, expectedUserId), ct);
    }
    public async Task<SignedInServerResponse> RenameAsync(OwnedPrivateServer server, string name, RobloxSessionCredential credential, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server.Id <= 0 || server.OwnerUserId <= 0 || !OwnedPrivateServer.IsNewName(name))
            throw new ArgumentException("Choose a short plain private-server name.");
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { name });
        try
        {
            return await SendAsync(HttpMethod.Patch, new("https://games.roblox.com/v1/vip-servers/" + server.Id.ToString(CultureInfo.InvariantCulture)),
                credential, body, root =>
                {
                    if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("errors", out _) || root.TryGetProperty("success", out var success) && success.ValueKind != JsonValueKind.True)
                        throw new JsonException();
                    return null; // Accepted only. An explicit fresh listing confirms the remote name.
                }, ct).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }
    private async Task<SignedInServerResponse> SendAsync(HttpMethod method, Uri endpoint, RobloxSessionCredential credential,
        byte[]? body, Func<JsonElement, SignedInServerPage?> parse, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct); stop.CancelAfter(_timeout);
        RobloxSessionCredential? rotation = null; TimeSpan? retryAfter = null;
        try
        {
            string? csrf = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var request = new HttpRequestMessage(method, endpoint);
                request.Headers.Accept.ParseAdd("application/json"); request.Headers.UserAgent.ParseAdd("ZenithSessions/1.0");
                request.Headers.Add("Cookie", ".ROBLOSECURITY=" + credential.CopyValue());
                if (csrf is not null) request.Headers.Add("X-CSRF-TOKEN", csrf);
                if (body is not null) { request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new("application/json"); }
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token).ConfigureAwait(false);
                stop.Token.ThrowIfCancellationRequested();
                if (response.RequestMessage?.RequestUri is { } actual && actual != endpoint) throw Failure();
                rotation = RobloxSessionClient.Rotation(response, credential, DateTimeOffset.UtcNow);
                retryAfter = PublicDirectoryRetryAfter.Read(response, DateTimeOffset.UtcNow, response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable);
                if(PublicDirectoryRetryAfter.ReadUsedUpWindow(response,DateTimeOffset.UtcNow) is { } reset && (retryAfter is null || reset>retryAfter)) retryAfter=reset;
                if (response.Headers.Contains("rblx-challenge-id") || response.Headers.Contains("rblx-challenge-type")) return Answer(RobloxAccountOutcome.Challenge);
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable) return Answer(RobloxAccountOutcome.RateLimited);
                if (body is not null && attempt == 0 && rotation is null && response.StatusCode == HttpStatusCode.Forbidden
                    && response.Headers.TryGetValues("x-csrf-token", out var tokens) && tokens.Take(2).ToArray() is [string token]
                    && token.Length is > 0 and <= 256 && token.All(c => c is >= (char)33 and <= (char)126)) { csrf = token; continue; }
                if (response.StatusCode == HttpStatusCode.Unauthorized) return Answer(RobloxAccountOutcome.SignInRequired);
                if (!response.IsSuccessStatusCode) return Answer(RobloxAccountOutcome.Unavailable);
                if (response.Content.Headers.ContentType?.MediaType != "application/json" || response.Content.Headers.ContentEncoding.Count != 0)
                    return Answer(RobloxAccountOutcome.Unavailable);
                const int limit = RobloxServerDirectory.MaximumResponseBytes;
                if (response.Content.Headers.ContentLength > limit) throw new JsonException();
                byte[] buffer = new byte[limit + 1]; int length = 0;
                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(stop.Token).ConfigureAwait(false);
                    int count;
                    while ((count = await stream.ReadAsync(buffer.AsMemory(length), stop.Token).ConfigureAwait(false)) > 0)
                    { length += count; if (length > limit) throw new JsonException(); }
                    using var document = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 12, AllowDuplicateProperties = false });
                    var page = parse(document.RootElement);
                    stop.Token.ThrowIfCancellationRequested();
                    return Answer(RobloxAccountOutcome.Success, page);
                }
                finally { CryptographicOperations.ZeroMemory(buffer); }
            }
            return Answer(RobloxAccountOutcome.Unavailable);
        }
        catch (JsonException) { return Answer(RobloxAccountOutcome.Unavailable); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new RobloxSessionException(RobloxSessionFailure.TimedOut, "The signed-in server request timed out. Check sign-in before retrying."); }
        catch (HttpRequestException) { throw Failure(); }
        catch (IOException) { throw Failure(); }
        finally { rotation?.Dispose(); }
        SignedInServerResponse Answer(RobloxAccountOutcome outcome, SignedInServerPage? page = null)
        { var result = new SignedInServerResponse(outcome, page, rotation, retryAfter); rotation = null; return result; }
    }
    private static SignedInServerPage ParsePage(JsonElement root, SignedInServerQuery query, long userId)
    {
        if (query.Order != SignedInServerOrder.OwnedPrivate)
        {
            var page = RobloxServerDirectory.ParsePage(root, query.PlaceId, SignedInServerQuery.PageSize, DateTimeOffset.UtcNow, includeFriends: true);
            if (query.Cursor is not null && page.NextCursor == query.Cursor) throw new JsonException();
            var rows = query.Order == SignedInServerOrder.BestLatency
                ? page.Servers.OrderBy(row => row.Ping ?? int.MaxValue).ToArray() : page.Servers.ToArray();
            return new(Array.AsReadOnly(rows), [], page.NextCursor);
        }
        RequireObject(root);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > SignedInServerQuery.PageSize
            || !root.TryGetProperty("nextPageCursor", out var cursor)) throw new JsonException();
        string? next = cursor.ValueKind == JsonValueKind.Null ? null : Plain(cursor, 2048);
        if (next == "") next = null;
        if (next is not null && next == query.Cursor) throw new JsonException();
        bool restricted = root.TryGetProperty("gameJoinRestricted", out var value) ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new JsonException() } : false;
        var servers = new List<OwnedPrivateServer>(); var ids = new HashSet<long>();
        foreach (var row in data.EnumerateArray())
        {
            RequireObject(row);
            long id = Positive(row, "vipServerId");
            if (!ids.Add(id) || !row.TryGetProperty("owner", out var owner)) throw new JsonException();
            RequireObject(owner); long ownerId = Positive(owner, "id");
            if (ownerId != userId) continue; // Accessible friends' servers are outside this owned-server list.
            if (!row.TryGetProperty("name", out var nameField)) throw new JsonException();
            string name = Plain(nameField, SavedPrivateServer.MaximumNameLength);
            if (!OwnedPrivateServer.IsReadableName(name)) throw new JsonException();
            int playing = Count(row, "playing", 0), maximum = Count(row, "maxPlayers", 1);
            servers.Add(new(id, query.PlaceId, ownerId, name, playing, maximum));
            // accessCode is a different token from a share-link code. Neither it nor link/joinCode is copied into metadata.
        }
        return new([], servers.AsReadOnly(), next, restricted);
    }
    private static string Plain(JsonElement value, int maximum)
        => value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Length <= maximum && !text.Any(char.IsControl) ? text : throw new JsonException();
    private static long Positive(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out long id) && id > 0 ? id : throw new JsonException();
    private static int Count(JsonElement value, string name, int minimum)
        => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out int count) && count >= minimum && count <= 1000000 ? count : throw new JsonException();
    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var field in value.EnumerateObject()) if(!names.Add(field.Name)) throw new JsonException();
    }
    private static RobloxSessionException Failure() => new(RobloxSessionFailure.Network, "The signed-in server request could not finish. Check sign-in before retrying.");
    public void Dispose() => _http.Dispose();
}
