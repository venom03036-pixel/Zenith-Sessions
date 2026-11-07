using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// Read-only, anonymous public-server observations; never signs in, launches, or confirms arrival.
/// The caller owns the injected client. Production callers must use CreatePublicHttpClient, or an
/// equivalent dedicated client with redirects, cookies, and credentials disabled. HttpClient does
/// not expose its handler settings, so an arbitrary injected client's settings cannot be inspected.
/// </summary>
public sealed class RobloxServerDirectory : IRobloxPublicServerDirectory
{
    public const int MaximumResponseBytes = 262144;
    private readonly HttpClient _http;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeProvider _time;

    public RobloxServerDirectory(HttpClient http, TimeSpan? requestTimeout = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(20);
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _time = time ?? TimeProvider.System;
        RequireAnonymousClient();
    }

    public static HttpClient CreatePublicHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        MaxResponseHeadersLength = 16
    }) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = MaximumResponseBytes };

    public async Task<RobloxPublicServerResult> BrowseAsync(RobloxPublicServerQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validated();
        ct.ThrowIfCancellationRequested();
        RequireAnonymousClient();
        var servers = new List<RobloxPublicServer>(query.PageSize * query.MaxPages);
        var jobIds = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = query.Cursor;
        if (cursor is not null) cursors.Add(cursor);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // One deadline bounds the entire browse, including all requested pages and body reads.
        deadline.CancelAfter(_requestTimeout);
        try
        {
            for (int page = 0; page < query.MaxPages; page++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                RequireAnonymousClient();
                Uri endpoint = Endpoint(query, cursor);
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (response.RequestMessage?.RequestUri is { } actual && actual != endpoint)
                    return Result(RobloxPublicServerStatus.InvalidResponse, cursor, null, "The public server client followed an unexpected redirect. Use the dedicated anonymous client.");
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    return Result(RobloxPublicServerStatus.RateLimited, cursor, ReadRetryAfter(response), "Roblox rate-limited public server browsing. No automatic retry was made.");
                if ((int)response.StatusCode is >= 300 and <= 399 || response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.ServiceUnavailable)
                    return Result(RobloxPublicServerStatus.Deferred, cursor, ReadRetryAfter(response), "Roblox deferred public server browsing or returned a redirect. No redirect or automatic retry was requested.");
                if (response.StatusCode != HttpStatusCode.OK)
                    return Result(RobloxPublicServerStatus.Unavailable, cursor, ReadRetryAfter(response), "The public server directory is unavailable for this place. No authentication or private-server fallback was attempted.");
                if (response.Content.Headers.ContentType?.MediaType is not "application/json"
                    || response.Content.Headers.ContentEncoding.Count != 0)
                    return Result(RobloxPublicServerStatus.InvalidResponse, cursor, null, "Roblox returned an unsupported public server response format.");

                using var json = await ReadJsonAsync(response, deadline.Token).ConfigureAwait(false);
                var parsed = ParsePage(json.RootElement, query.PlaceId, query.PageSize, _time.GetUtcNow());
                deadline.Token.ThrowIfCancellationRequested();
                if (parsed.NextCursor is not null && !cursors.Add(parsed.NextCursor))
                    return Result(RobloxPublicServerStatus.InvalidResponse, cursor, null, "Roblox returned a repeated pagination cursor. Browsing stopped without following it.");
                // Commit a page only after its complete schema, server kind, and cursor validate.
                foreach (var server in parsed.Servers)
                    if (jobIds.Add(server.JobId)) servers.Add(server);
                cursor = parsed.NextCursor;
                if (cursor is null)
                    return Result(RobloxPublicServerStatus.Complete, null, null, "Public server observations loaded. Availability can change; selection is a requested destination, not confirmed arrival.");
            }
            return Result(RobloxPublicServerStatus.PageLimit, cursor, null, "The requested page limit was reached. More public servers may exist; no additional request was made.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result(RobloxPublicServerStatus.Deferred, cursor, null, "Public server browsing reached its connection deadline. No automatic retry was made.");
        }
        catch (HttpRequestException)
        {
            ct.ThrowIfCancellationRequested();
            return Result(RobloxPublicServerStatus.Unavailable, cursor, null, "Could not connect to the public Roblox server directory. Retry when the connection is available.");
        }
        catch (IOException)
        {
            ct.ThrowIfCancellationRequested();
            return Result(RobloxPublicServerStatus.Unavailable, cursor, null, "The public Roblox server response could not be read. Retry when the connection is available.");
        }
        catch (JsonException)
        {
            ct.ThrowIfCancellationRequested();
            return Result(RobloxPublicServerStatus.InvalidResponse, cursor, null, "Roblox returned an unreadable or unsupported public server response.");
        }

        RobloxPublicServerResult Result(RobloxPublicServerStatus status, string? next, TimeSpan? retry, string explanation)
        {
            ct.ThrowIfCancellationRequested();
            return new(status, Array.AsReadOnly(servers.ToArray()), next, retry, explanation);
        }
    }

    private void RequireAnonymousClient()
    {
        if (_http.DefaultRequestHeaders.Authorization is not null || _http.DefaultRequestHeaders.Contains("Cookie")
            || _http.DefaultRequestHeaders.Contains("Proxy-Authorization") || _http.DefaultRequestHeaders.Contains("x-api-key"))
            throw new ArgumentException("Public server browsing requires a dedicated anonymous HTTP client without authorization headers.");
    }
    private static Uri Endpoint(RobloxPublicServerQuery query, string? cursor) => new("https://games.roblox.com/v1/games/"
        + query.PlaceId.ToString(CultureInfo.InvariantCulture) + "/servers/Public?sortOrder=" + query.SortOrder
        + "&excludeFullGames=" + (query.ExcludeFullGames ? "true" : "false") + "&limit=" + query.PageSize.ToString(CultureInfo.InvariantCulture)
        + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));

    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        return PublicDirectoryRetryAfter.Read(response, _time.GetUtcNow(),
            useDefaultPause: response.StatusCode == HttpStatusCode.TooManyRequests);
    }
    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new JsonException("Oversized public server response.");
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] bytes = new byte[4096];
        try
        {
            int read;
            while ((read = await input.ReadAsync(bytes, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaximumResponseBytes) throw new JsonException("Oversized public server response.");
                buffer.Write(bytes, 0, read);
            }
            buffer.Position = 0;
            return await JsonDocument.ParseAsync(buffer, new JsonDocumentOptions { MaxDepth = 12 }, ct).ConfigureAwait(false);
        }
        finally
        {
            // Unexpected private/access-code fields are never retained in these mutable transport buffers.
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(buffer.GetBuffer());
        }
    }

    private static (IReadOnlyList<RobloxPublicServer> Servers, string? NextCursor) ParsePage(JsonElement root, long placeId, int limit, DateTimeOffset observed)
    {
        RequireObject(root);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > limit)
            throw new JsonException("Invalid public server page.");
        if (!root.TryGetProperty("nextPageCursor", out var next)) throw new JsonException("Missing pagination cursor.");
        string? cursor = next.ValueKind == JsonValueKind.Null ? null : ReadString(next, 2048);
        if (cursor == "") cursor = null;
        var rows = new List<RobloxPublicServer>(data.GetArrayLength());
        foreach (var row in data.EnumerateArray())
        {
            RequireObject(row);
            if (!row.TryGetProperty("id", out var id) || !RobloxRequestedDestination.TryNormalizeJobId(ReadString(id, 36), out string? jobId))
                throw new JsonException("Invalid server job ID.");
            // A public endpoint must never silently turn a private/reserved record into a public destination.
            if (HasNonzeroId(row, "vipServerId") || HasNonemptyValue(row, "privateServerId") || HasNonemptyValue(row, "accessCode")
                || HasNonemptyValue(row, "linkCode") || HasNonemptyValue(row, "reservedServerAccessCode")
                || (row.TryGetProperty("serverType", out var kind) && (kind.ValueKind != JsonValueKind.String || kind.GetString() != "Public")))
                throw new JsonException("A nonpublic server was returned by the public endpoint.");
            int playing = ReadInt(row, "playing", 0, 1000000);
            int maxPlayers = ReadInt(row, "maxPlayers", 1, 1000000);
            double? fps = null;
            if (row.TryGetProperty("fps", out var fpsValue) && fpsValue.ValueKind != JsonValueKind.Null)
            {
                if (fpsValue.ValueKind != JsonValueKind.Number || !fpsValue.TryGetDouble(out double number) || !double.IsFinite(number) || number is < 0 or > 10000)
                    throw new JsonException("Invalid server frame-rate observation.");
                fps = number;
            }
            int? ping = row.TryGetProperty("ping", out var pingValue) && pingValue.ValueKind != JsonValueKind.Null
                ? ReadInt(row, "ping", 0, 3600000) : null;
            rows.Add(new(placeId, jobId!, playing, maxPlayers, fps, ping, observed));
        }
        return (rows, cursor);
    }
    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a JSON object.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw new JsonException("Repeated JSON field.");
    }
    private static string ReadString(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > maximum || text.Any(char.IsControl))
            throw new JsonException("Invalid string field.");
        return text;
    }
    private static int ReadInt(JsonElement root, string name, int minimum, int maximum)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result)
            || result < minimum || result > maximum) throw new JsonException("Invalid numeric observation.");
        return result;
    }
    private static bool HasNonzeroId(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return false;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long id)) throw new JsonException("Invalid server type metadata.");
        return id != 0;
    }
    private static bool HasNonemptyValue(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return false;
        return ReadString(value, 2048).Length != 0;
    }
}
