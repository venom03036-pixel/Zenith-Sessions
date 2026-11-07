using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// Anonymous, read-only public profile/game lookup. The caller owns the injected, dedicated HttpClient and
/// request deadlines. Production callers must use CreateAnonymousHttpClient: a caller-supplied client's
/// underlying redirect/cookie/credential settings cannot be inspected through HttpClient.
/// No timers, background workers, pending queue, credential reads, or disk cache are created here.
/// The configurable budget covers retained encoded bytes; decoded/caller references and bounded
/// in-flight buffers have separate owners. It is not a process resident-memory guarantee.
/// </summary>
public sealed partial class RobloxPublicDirectory : IPublicRobloxDirectory, IPublicRobloxGameDirectory, IPublicRobloxGameGallery,
    IPublicRobloxAvatarBatchDirectory
{
    public const int MaximumConcurrentRequests = 2;
    public const int MaximumJsonBytes = 64 * 1024;
    public const int MaximumAvatarBytes = 2 * 1024 * 1024;
    public const long MaximumEncodedMemoryBytes = 64L * 1024 * 1024;
    private const int MaximumCacheEntries = 512;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly long _maximumEncodedMemoryBytes;
    private readonly object _gate = new();
    private readonly Dictionary<PublicImageKey, CacheEntry> _cache = new();
    private readonly LinkedList<PublicImageKey> _lru = new();
    private readonly HashSet<PublicImageKey> _inFlightImages = new();
    private readonly Dictionary<string, DateTimeOffset> _notBefore = new(StringComparer.Ordinal);
    private int _activeRequests;
    private long _cachedBytes;
    private long _cacheGeneration;

    public RobloxPublicDirectory(HttpClient http, TimeProvider? timeProvider = null,
        long maximumEncodedMemoryBytes = MaximumEncodedMemoryBytes)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (maximumEncodedMemoryBytes is < 1 or > MaximumEncodedMemoryBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumEncodedMemoryBytes), "The encoded public image cache budget must be between 1 byte and 64 MiB.");
        _http = http;
        _clock = timeProvider ?? TimeProvider.System;
        _maximumEncodedMemoryBytes = maximumEncodedMemoryBytes;
    }

    public static HttpClient CreateAnonymousHttpClient() => new(CreateAnonymousHandler()) { Timeout = TimeSpan.FromSeconds(15) };

    // Uses the Windows system proxy, like server browsing, sign-in checks and share links, so a
    // proxy-only network reaches every Roblox service the same way. No proxy credentials are set.
    internal static SocketsHttpHandler CreateAnonymousHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false,
        MaxConnectionsPerServer = MaximumConcurrentRequests,
        MaxResponseHeadersLength = 32,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    public long CachedEncodedBytes { get { lock (_gate) return _cachedBytes; } }
    public long EncodedMemoryBudgetBytes => _maximumEncodedMemoryBytes;
    public int CachedAvatarCount { get { lock (_gate) return _cache.Keys.Count(key => key.Kind == PublicImageKind.Avatar); } }
    public int CachedGameIconCount { get { lock (_gate) return _cache.Keys.Count(key => key.Kind == PublicImageKind.GameIcon); } }
    public int CachedGameGalleryImageCount { get { lock (_gate) return _cache.Keys.Count(key => key.Kind == PublicImageKind.GameGallery); } }
    public int CachedImageCount { get { lock (_gate) return _cache.Count; } }

    public Task<PublicDirectoryResult<PublicRobloxUser>> LookupUserAsync(long userId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (userId <= 0) return Task.FromResult(InvalidId<PublicRobloxUser>());
        // Anonymous endpoint documented at https://create.roblox.com/docs/cloud/reference/domains/users.
        // It supplies public metadata only; never call /users/authenticated or attach authorization.
        var uri = new Uri("https://users.roblox.com/v1/users/" + userId.ToString(CultureInfo.InvariantCulture));
        return ReadJsonAsync(uri, root =>
        {
            if (ReadId(root, "id") != userId) throw new InvalidPublicResponseException();
            string username = ReadText(root, "name", 20);
            if (username.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_'))) throw new InvalidPublicResponseException();
            string displayName = ReadText(root, "displayName", 100);
            return Success(new PublicRobloxUser(userId, username, displayName, _clock.GetUtcNow()),
                "Public profile information. This does not verify sign-in or identify a running client.");
        }, ct);
    }

    public Task<PublicDirectoryResult<PublicRobloxAvatar>> GetAvatarAsync(long userId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (userId <= 0) return Task.FromResult(InvalidId<PublicRobloxAvatar>());
        // Official public headshot endpoint; deliberately request one small PNG rather than full avatars.
        var uri = new Uri("https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds="
            + userId.ToString(CultureInfo.InvariantCulture) + "&size=150x150&format=Png&isCircular=false");
        return ReadJsonAsync(uri, root =>
        {
            JsonElement data = ReadField(root, "data");
            if (data.ValueKind != JsonValueKind.Array) throw new InvalidPublicResponseException();
            if (data.GetArrayLength() == 0) return Unknown<PublicRobloxAvatar>("No public avatar was returned.");
            if (data.GetArrayLength() != 1) throw new InvalidPublicResponseException();
            JsonElement item = data[0];
            if (ReadId(item, "targetId") != userId) throw new InvalidPublicResponseException();
            string state = ReadText(item, "state", 32);
            if (state is "Pending" or "InReview")
                return Deferred<PublicRobloxAvatar>("The public avatar is still being prepared.", TimeSpan.FromSeconds(30));
            if (state is "Blocked" or "Error") return Unknown<PublicRobloxAvatar>("The public avatar is unavailable.");
            if (state != "Completed") return Unknown<PublicRobloxAvatar>("The public avatar state is not supported.");
            if (!TryValidateAvatarUri(ReadText(item, "imageUrl", 2048), out Uri? imageUri)) throw new InvalidPublicResponseException();
            return Success(new PublicRobloxAvatar(userId, imageUri!, 150, 150, _clock.GetUtcNow()), "Public avatar information.");
        }, ct);
    }

    public Task<PublicDirectoryResult<IReadOnlyList<PublicRobloxAvatarBatchItem>>> GetAvatarsAsync(IReadOnlyList<long> userIds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        const string invalid = "Request between one and fifty distinct positive 64-bit Roblox user IDs.";
        if (userIds is null || userIds.Count is 0 or > PublicRobloxAvatarBatchLimits.MaximumBatch)
            return Task.FromResult(Invalid<IReadOnlyList<PublicRobloxAvatarBatchItem>>(invalid));
        var requested = new long[userIds.Count]; var distinct = new HashSet<long>();
        for (int index = 0; index < requested.Length; index++)
        {
            long id = userIds[index];
            if (id <= 0 || !distinct.Add(id)) return Task.FromResult(Invalid<IReadOnlyList<PublicRobloxAvatarBatchItem>>(invalid));
            requested[index] = id;
        }
        // The same official headshot endpoint as GetAvatarAsync, asked once for the whole batch.
        var uri = new Uri("https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds="
            + string.Join(",", requested.Select(id => id.ToString(CultureInfo.InvariantCulture)))
            + "&size=150x150&format=Png&isCircular=false");
        return ReadJsonAsync(uri, root => ParseAvatarBatch(root, requested), ct);
    }

    private PublicDirectoryResult<IReadOnlyList<PublicRobloxAvatarBatchItem>> ParseAvatarBatch(JsonElement root, long[] requested)
    {
        JsonElement data = ReadField(root, "data");
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > requested.Length) throw new InvalidPublicResponseException();
        // Roblox does not promise reply order, so replies are matched by targetId; an unasked or repeated user is invalid.
        var expected = new HashSet<long>(requested); var found = new Dictionary<long, PublicRobloxAvatarBatchItem>();
        DateTimeOffset now = _clock.GetUtcNow();
        foreach (var item in data.EnumerateArray())
        {
            long userId = ReadId(item, "targetId");
            if (!expected.Contains(userId) || found.ContainsKey(userId)) throw new InvalidPublicResponseException();
            var state = ReadText(item, "state", 32) switch
            {
                "Completed" => PublicRobloxThumbnailState.Ready,
                "Pending" or "InReview" => PublicRobloxThumbnailState.Pending,
                "Blocked" => PublicRobloxThumbnailState.Blocked,
                _ => PublicRobloxThumbnailState.Unavailable
            };
            PublicRobloxAvatar? avatar = null;
            if (state == PublicRobloxThumbnailState.Ready)
            {
                if (!TryValidateAvatarUri(ReadText(item, "imageUrl", 2048), out Uri? imageUri)) throw new InvalidPublicResponseException();
                avatar = new PublicRobloxAvatar(userId, imageUri!, 150, 150, now);
            }
            found.Add(userId, new(userId, state, avatar));
        }
        // A user the reply left out is unavailable, not an invalid reply: the rest of the batch stays usable.
        var result = new PublicRobloxAvatarBatchItem[requested.Length];
        for (int index = 0; index < result.Length; index++)
            result[index] = found.TryGetValue(requested[index], out var item) ? item
                : new(requested[index], PublicRobloxThumbnailState.Unavailable, null);
        return Success<IReadOnlyList<PublicRobloxAvatarBatchItem>>(Array.AsReadOnly(result),
            "Public avatar information. This does not verify sign-in or identify a running client.");
    }

    public async Task<PublicDirectoryResult<EncodedPublicAvatar>> FetchAvatarAsync(PublicRobloxAvatar avatar, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (avatar is null || avatar.UserId <= 0 || avatar.Width != 150 || avatar.Height != 150
            || avatar.ImageUri is null || !TryValidateAvatarUri(avatar.ImageUri.OriginalString, out Uri? uri))
            return Invalid<EncodedPublicAvatar>("The public avatar descriptor is invalid.");
        var result = await FetchPublicPngAsync(PublicImageKind.Avatar, avatar.UserId, uri!, avatar.RetrievedUtc, ct).ConfigureAwait(false);
        return new(result.State, result.Value is { } value
            ? new EncodedPublicAvatar(value.Bytes, value.MediaType, value.RetrievedUtc) : null, result.Message, result.RetryAfter);
    }

    // Avatars, game icons and gallery images share slots, pauses, bytes, expiry and the clear fence.
    // This transport checks only the PNG header; the presentation owner fully validates the image.
    private async Task<PublicDirectoryResult<EncodedPublicPng>> FetchPublicPngAsync(PublicImageKind kind,
        long identityId, Uri imageUri, DateTimeOffset retrievedUtc, CancellationToken ct, long galleryImageId = 0)
    {
        ct.ThrowIfCancellationRequested();
        DateTimeOffset now = _clock.GetUtcNow();
        if (retrievedUtc > now.AddMinutes(5) || retrievedUtc < now.AddMinutes(-30))
            return Deferred<EncodedPublicPng>("Refresh the public image descriptor before downloading it.");
        var key = new PublicImageKey(kind, identityId, imageUri.AbsoluteUri, galleryImageId);
        long cacheGeneration;
        lock (_gate)
        {
            cacheGeneration = _cacheGeneration;
            RemoveExpired(now);
            if (_cache.TryGetValue(key, out CacheEntry? entry))
            {
                _lru.Remove(entry.Node);
                _lru.AddFirst(entry.Node);
                return Success(entry.Image, "Public image loaded from the encoded memory cache.");
            }
            if (_inFlightImages.Contains(key))
                return Deferred<EncodedPublicPng>("This public image is already being downloaded.");
        }
        if (!TryStart<EncodedPublicPng>(imageUri, key, out var unavailable)) return unavailable!;
        try
        {
            using var response = await SendAsync(imageUri, "image/png", ct).ConfigureAwait(false);
            if (StatusResult<EncodedPublicPng>(response, imageUri, kind is PublicImageKind.GameIcon or PublicImageKind.GameGallery or PublicImageKind.GameThumbnail) is { } failure) return failure;
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "image/png", StringComparison.OrdinalIgnoreCase))
                return Failure<EncodedPublicPng>("Roblox returned an unsupported public image format.");
            byte[] bytes = await ReadBoundedAsync(response.Content, MaximumAvatarBytes, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!HasExpectedPngHeader(bytes, kind)) return Failure<EncodedPublicPng>("Roblox returned an invalid public image header.");
            var encoded = new EncodedPublicPng(bytes, "image/png", _clock.GetUtcNow());
            lock (_gate)
            {
                RemoveExpired(_clock.GetUtcNow());
                if (cacheGeneration == _cacheGeneration && bytes.Length <= _maximumEncodedMemoryBytes)
                {
                    if (_cache.TryGetValue(key, out CacheEntry? existing)) Remove(existing.Node);
                    while (_cachedBytes + bytes.Length > _maximumEncodedMemoryBytes || _cache.Count >= MaximumCacheEntries) RemoveOldest();
                    var node = _lru.AddFirst(key);
                    _cache.Add(key, new(encoded, _clock.GetUtcNow().Add(CacheLifetime), node));
                    _cachedBytes += bytes.Length;
                }
            }
            return Success(encoded, "Public image downloaded.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return Deferred<EncodedPublicPng>("The public image request timed out."); }
        catch (HttpRequestException) { return Failure<EncodedPublicPng>("The public image service could not be reached."); }
        catch (IOException) { return Failure<EncodedPublicPng>("The public image download was interrupted."); }
        catch (InvalidPublicResponseException) { return Failure<EncodedPublicPng>("Roblox returned an oversized or invalid public image response."); }
        finally { Finish(key); }
    }

    public void ClearMemoryCache()
    {
        lock (_gate) { _cacheGeneration++; _cache.Clear(); _lru.Clear(); _cachedBytes = 0; }
    }

    public static bool TryValidateAvatarUri(string? value, out Uri? uri)
    {
        uri = null;
        if (value is not { Length: > 0 and <= 2048 } || value.Any(c => c > 127 || char.IsControl(c) || c == '\\')
            || !Uri.TryCreate(value, UriKind.Absolute, out var parsed) || !parsed.IsWellFormedOriginalString()
            || parsed.Scheme != Uri.UriSchemeHttps || parsed.Port != 443 || parsed.UserInfo.Length != 0
            || parsed.Query.Length != 0 || parsed.Fragment.Length != 0) return false;
        string host = parsed.IdnHost;
        bool allowedHost = host == "tr.rbxcdn.com" || (host.Length == 13 && host.StartsWith('t')
            && host[1] is >= '0' and <= '7' && host[2..] == ".rbxcdn.com");
        if (!allowedHost || parsed.AbsolutePath.Length is < 2 or > 1024
            || parsed.AbsolutePath.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.'))) return false;
        uri = parsed;
        return true;
    }

    private async Task<PublicDirectoryResult<T>> ReadJsonAsync<T>(Uri uri, Func<JsonElement, PublicDirectoryResult<T>> parse,
        CancellationToken ct, bool restrictedIsUnknown = false) where T : class
    {
        if (!TryStart<T>(uri, null, out var unavailable)) return unavailable!;
        try
        {
            using var response = await SendAsync(uri, "application/json", ct).ConfigureAwait(false);
            if (StatusResult<T>(response, uri, restrictedIsUnknown) is { } failure) return failure;
            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not "application/json" and not "text/json") return Failure<T>("Roblox returned an unsupported public directory response.");
            byte[] bytes = await ReadBoundedAsync(response.Content, MaximumJsonBytes, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            try
            {
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
                RejectDuplicateFields(document.RootElement);
                return parse(document.RootElement);
            }
            catch (InvalidOperationException)
            {
                // JsonDocument can parse an unpaired Unicode escape but throw on GetString or
                // JsonProperty.Name. Contain this response-decoding failure here, not around
                // transport setup, and discard its provider-derived exception text.
                throw new InvalidPublicResponseException();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return Deferred<T>("The public Roblox request timed out."); }
        catch (HttpRequestException) { return Failure<T>("The public Roblox service could not be reached."); }
        catch (IOException) { return Failure<T>("The public Roblox response was interrupted."); }
        catch (JsonException) { return Failure<T>("Roblox returned an unreadable public directory response."); }
        catch (InvalidPublicResponseException) { return Failure<T>("Roblox returned an oversized or invalid public directory response."); }
        finally { Finish(null); }
    }

    private bool TryStart<T>(Uri uri, PublicImageKey? imageKey, out PublicDirectoryResult<T>? unavailable) where T : class
    {
        lock (_gate)
        {
            // This client must be dedicated to anonymous requests; reject even custom inherited headers.
            if (_http.DefaultRequestHeaders.Any())
            { unavailable = Failure<T>("The public directory requires a dedicated anonymous HTTP client with no default headers."); return false; }
            DateTimeOffset now = _clock.GetUtcNow();
            if (_notBefore.TryGetValue(uri.IdnHost, out var until) && until > now)
            { unavailable = Deferred<T>("The public Roblox service requested a pause.", until - now); return false; }
            if (_activeRequests >= MaximumConcurrentRequests || (imageKey is { } pendingKey && _inFlightImages.Contains(pendingKey)))
            { unavailable = Deferred<T>("The public directory is busy. Try again when current requests finish."); return false; }
            _activeRequests++;
            if (imageKey is { } key) _inFlightImages.Add(key);
            unavailable = null;
            return true;
        }
    }

    private void Finish(PublicImageKey? imageKey)
    {
        lock (_gate) { _activeRequests--; if (imageKey is { } key) _inFlightImages.Remove(key); }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, string accept, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private PublicDirectoryResult<T>? StatusResult<T>(HttpResponseMessage response, Uri expectedUri, bool restrictedIsUnknown = false) where T : class
    {
        if (response.RequestMessage?.RequestUri is { } actual && actual != expectedUri)
            return Failure<T>("The public Roblox request was redirected unexpectedly.");
        if (response.StatusCode == HttpStatusCode.OK) return null;
        if (response.StatusCode == HttpStatusCode.NotFound) return Unknown<T>("The requested public Roblox information was not found.");
        if (restrictedIsUnknown && (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
            return Unknown<T>("The requested public game information is unavailable or restricted.");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            DateTimeOffset now = _clock.GetUtcNow().ToUniversalTime();
            TimeSpan delay = PublicDirectoryRetryAfter.Read(response, now, useDefaultPause: true).GetValueOrDefault();
            DateTimeOffset until = now.Add(delay);
            lock (_gate)
            {
                // Another in-flight response must never shorten an existing provider pause.
                if (_notBefore.TryGetValue(expectedUri.IdnHost, out var existing) && existing > until) until = existing;
                _notBefore[expectedUri.IdnHost] = until;
            }
            return Deferred<T>("The public Roblox service is rate limited. Try again after the requested pause.", until - now);
        }
        return Failure<T>("The public Roblox service did not complete the request.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maximum) throw new InvalidPublicResponseException();
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > maximum) throw new InvalidPublicResponseException();
            output.Write(chunk, 0, read);
        }
        return output.ToArray();
    }

    private static bool HasExpectedPngHeader(ReadOnlySpan<byte> bytes, PublicImageKind kind)
    {
        // Closed policies only. Gallery support must not relax the existing 150px contracts.
        (uint width, uint height) = kind switch
        {
            PublicImageKind.Avatar or PublicImageKind.GameIcon => (150U, 150U),
            PublicImageKind.GameGallery => ((uint)PublicRobloxGameGalleryLimits.Width, (uint)PublicRobloxGameGalleryLimits.Height),
            PublicImageKind.GameThumbnail => ((uint)PublicRobloxGameArtLimits.Width, (uint)PublicRobloxGameArtLimits.Height),
            _ => (0U, 0U)
        };
        return width != 0 && bytes.Length >= 33
            && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            && BinaryPrimitives.ReadUInt32BigEndian(bytes[8..12]) == 13
            && bytes[12..16].SequenceEqual("IHDR"u8)
            && BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]) == width
            && BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24]) == height;
    }

    private static JsonElement ReadField(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidPublicResponseException();
        bool found = false;
        JsonElement result = default;
        foreach (var property in root.EnumerateObject())
            if (property.NameEquals(name))
            { if (found) throw new InvalidPublicResponseException(); found = true; result = property.Value; }
        return found ? result : throw new InvalidPublicResponseException();
    }

    private static void RejectDuplicateFields(JsonElement value)
    {
        // JSON depth and encoded length are already capped. Include unknown fields: accepting a
        // conflicting duplicate in an ignored subtree would make future parsing behavior ambiguous.
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidPublicResponseException();
                RejectDuplicateFields(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateFields(item);
    }

    private static long ReadId(JsonElement root, string name)
    {
        JsonElement field = ReadField(root, name);
        return field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out long id) && id > 0 ? id : throw new InvalidPublicResponseException();
    }

    private static string ReadText(JsonElement root, string name, int maximum)
    {
        JsonElement field = ReadField(root, name);
        if (field.ValueKind != JsonValueKind.String || field.GetString() is not { Length: > 0 } value
            || value.Length > maximum || string.IsNullOrWhiteSpace(value)
            || value.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format)) throw new InvalidPublicResponseException();
        return value;
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        // At most 512 entries; expiry is independent of access order, so inspect the whole bounded cache.
        var node = _lru.Last;
        while (node is not null)
        {
            var previous = node.Previous;
            if (_cache[node.Value].ExpiresUtc <= now) Remove(node);
            node = previous;
        }
    }

    private void RemoveOldest() { if (_lru.Last is { } node) Remove(node); }
    private void Remove(LinkedListNode<PublicImageKey> node)
    {
        _cachedBytes -= _cache[node.Value].Image.Bytes.Length;
        _cache.Remove(node.Value);
        _lru.Remove(node);
    }

    private static PublicDirectoryResult<T> Success<T>(T value, string message) where T : class => new(PublicDirectoryState.Success, value, message);
    private static PublicDirectoryResult<T> Unknown<T>(string message) where T : class => new(PublicDirectoryState.Unknown, null, message);
    private static PublicDirectoryResult<T> Deferred<T>(string message, TimeSpan? retry = null) where T : class => new(PublicDirectoryState.Deferred, null, message, retry);
    private static PublicDirectoryResult<T> Failure<T>(string message) where T : class => new(PublicDirectoryState.Failure, null, message);
    private static PublicDirectoryResult<T> Invalid<T>(string message) where T : class => new(PublicDirectoryState.InvalidInput, null, message);
    private static PublicDirectoryResult<T> InvalidId<T>() where T : class => Invalid<T>("Enter a positive 64-bit Roblox user ID.");
    private sealed class InvalidPublicResponseException : Exception { }
    private enum PublicImageKind { Avatar, GameIcon, GameGallery, GameThumbnail }
    private readonly record struct PublicImageKey(PublicImageKind Kind, long IdentityId, string AbsoluteUri, long GalleryImageId = 0);
    private sealed record EncodedPublicPng(ReadOnlyMemory<byte> Bytes, string MediaType, DateTimeOffset RetrievedUtc);
    private sealed record CacheEntry(EncodedPublicPng Image, DateTimeOffset ExpiresUtc, LinkedListNode<PublicImageKey> Node);
}

internal static class PublicDirectoryRetryAfter
{
    // RFC 9110 permits an HTTP date or arbitrarily large decimal delay-seconds. A large valid
    // provider pause is saturated only at the representable clock horizon, never shortened to a
    // convenience retry cap. These methods recommend a deadline; they schedule no work.
    public static TimeSpan? Read(HttpResponseMessage response, DateTimeOffset now, bool useDefaultPause)
    {
        now = now.ToUniversalTime();
        TimeSpan maximum = DateTimeOffset.MaxValue - now;
        TimeSpan? pause = ReadRetryAfter(response, now, maximum);
        // On a 429 Roblox's x-ratelimit-reset names when the request window really reopens. Retry-After
        // can be much shorter (5 s against a 50 s reset), and waiting only for it earns another 429.
        if (response.StatusCode == HttpStatusCode.TooManyRequests && ReadWindowReset(response, maximum) is { } reset
            && (pause is null || reset > pause)) pause = reset;
        if (pause is not null || !useDefaultPause) return pause;
        return maximum < TimeSpan.FromSeconds(30) ? maximum : TimeSpan.FromSeconds(30);
    }

    // A successful response whose x-ratelimit-remaining is 0 used the last request of the window, so
    // the next request would be refused until x-ratelimit-reset. Missing or unreadable values mean no pause.
    public static TimeSpan? ReadUsedUpWindow(HttpResponseMessage response, DateTimeOffset now)
    {
        if (ReadSingle(response, "x-ratelimit-remaining") is not { } remaining || remaining.Any(c => c != '0')) return null;
        return ReadWindowReset(response, DateTimeOffset.MaxValue - now.ToUniversalTime());
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response, DateTimeOffset now, TimeSpan maximum)
    {
        if (ReadSingle(response, "Retry-After") is not { } text) return null;
        if (ReadDeltaSeconds(text, maximum) is { } seconds) return seconds > TimeSpan.Zero ? seconds : null;
        if (RetryConditionHeaderValue.TryParse(text, out var parsed) && parsed.Date is { } date && date > now) return date - now;
        return null;
    }

    private static TimeSpan? ReadWindowReset(HttpResponseMessage response, TimeSpan maximum) =>
        ReadSingle(response, "x-ratelimit-reset") is { } text && ReadDeltaSeconds(text, maximum) is { } reset
            && reset > TimeSpan.Zero ? reset : null;

    // Exactly one non-empty header value, or null when the header is absent, repeated or blank.
    private static string? ReadSingle(HttpResponseMessage response, string name)
    {
        if (!response.Headers.NonValidated.TryGetValues(name, out var values)) return null;
        string? raw = null;
        foreach (string value in values) { if (raw is not null) return null; raw = value; }
        string text = raw?.Trim(' ', '\t') ?? "";
        return text.Length == 0 ? null : text;
    }

    // Plain decimal seconds (ASCII digits only), saturated at the representable clock horizon.
    private static TimeSpan? ReadDeltaSeconds(string text, TimeSpan maximum)
    {
        foreach (char c in text) if (!char.IsAsciiDigit(c)) return null;
        long seconds = 0, maximumSeconds = maximum.Ticks / TimeSpan.TicksPerSecond;
        foreach (char c in text)
        {
            int digit = c - '0';
            if (seconds > maximumSeconds / 10 || seconds == maximumSeconds / 10 && digit > maximumSeconds % 10) return maximum;
            seconds = seconds * 10 + digit;
        }
        return TimeSpan.FromTicks(seconds * TimeSpan.TicksPerSecond);
    }
}
