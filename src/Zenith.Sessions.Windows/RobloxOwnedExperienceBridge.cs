using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>One bounded connection lane for a configured owned experience. No background polling or retries.</summary>
public sealed class RobloxOwnedExperienceBridge : IOwnedExperienceBridge, IDisposable
{
    private const string Topic = "ZenithSessionsV1";
    private const string Store = "ZenithSessionsReceiptsV1";
    private const int MaximumResponseBytes = 4096;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReadInterval = TimeSpan.FromMilliseconds(500), PublishInterval = TimeSpan.FromSeconds(1);
    private readonly long _universe;
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, ValueTask<string?>> _readKey;
    private readonly Func<CancellationToken, ValueTask<string?>> _publishKey;
    private readonly TimeProvider _clock;
    private DateTimeOffset _readAfter, _publishAfter;
    private int _busy, _disposed;

    // The owner supplies two separately protected Open Cloud keys. They are read for only the
    // requested call and are never put in a URI, a shared header collection or an error result.
    public RobloxOwnedExperienceBridge(long universe, Func<CancellationToken, ValueTask<string?>> readKey,
        Func<CancellationToken, ValueTask<string?>> publishKey)
        : this(universe, readKey, publishKey, new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }, TimeProvider.System) { }

    internal RobloxOwnedExperienceBridge(long universe, Func<CancellationToken, ValueTask<string?>> readKey,
        Func<CancellationToken, ValueTask<string?>> publishKey, HttpMessageHandler handler, TimeProvider clock)
    {
        if (!OwnedExperienceCommand.IsEngineId(universe)) throw new ArgumentOutOfRangeException(nameof(universe));
        _universe = universe;
        _readKey = readKey ?? throw new ArgumentNullException(nameof(readKey));
        _publishKey = publishKey ?? throw new ArgumentNullException(nameof(publishKey));
        _clock = clock;
        _http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<OwnedExperienceBridgeResult> PublishAsync(OwnedExperienceCommand command, CancellationToken cancellationToken)
        => SendAsync(command, publish: true, cancellationToken);

    public Task<OwnedExperienceBridgeResult> ReadAsync(OwnedExperienceCommand command, CancellationToken cancellationToken)
        => SendAsync(command, publish: false, cancellationToken);

    private async Task<OwnedExperienceBridgeResult> SendAsync(OwnedExperienceCommand command, bool publish, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.UniverseId != _universe) throw new ArgumentException("The command belongs to a different experience.", nameof(command));
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return new(OwnedExperienceBridgeState.Busy);
        try
        {
            var now = _clock.GetUtcNow();
            if (!Current(command, now)) return new(OwnedExperienceBridgeState.Expired);
            var retryUtc = publish ? _publishAfter : _readAfter;
            if (now < retryUtc) return new(OwnedExperienceBridgeState.RateLimited, RetryUtc: retryUtc);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(RequestTimeout);
            string? key;
            try { key = await (publish ? _publishKey : _readKey)(deadline.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
            { return new(OwnedExperienceBridgeState.AuthorizationRequired); }
            ct.ThrowIfCancellationRequested();
            if (key is not { Length: > 0 and <= 512 } || key.Any(c => c is < '!' or > '~'))
                return new(OwnedExperienceBridgeState.AuthorizationRequired);
            now = _clock.GetUtcNow();
            if (!Current(command, now)) return new(OwnedExperienceBridgeState.Expired);
            var path = FormattableString.Invariant($"https://apis.roblox.com/cloud/v2/universes/{_universe}");
            using var request = publish
                ? new HttpRequestMessage(HttpMethod.Post, path + ":publishMessage")
                : new HttpRequestMessage(HttpMethod.Get, path + FormattableString.Invariant($"/data-stores/{Store}/scopes/global/entries/u{command.UserId}"));
            request.Headers.Add("x-api-key", key);
            if (publish)
                request.Content = JsonContent.Create(new { topic = Topic, message = WriteCommand(command) });

            deadline.Token.ThrowIfCancellationRequested();
            if (publish) _publishAfter = AddDelay(now, PublishInterval);
            else _readAfter = AddDelay(now, ReadInterval);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            now = _clock.GetUtcNow();
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                retryUtc = response.Headers.RetryAfter?.Date ?? AddDelay(now, response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5));
                var floor = AddDelay(now, TimeSpan.FromSeconds(1));
                if (retryUtc < floor) retryUtc = floor;
                if (publish) _publishAfter = retryUtc;
                else _readAfter = retryUtc;
                return new(OwnedExperienceBridgeState.RateLimited, RetryUtc: retryUtc);
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(OwnedExperienceBridgeState.AuthorizationRequired);
            if (!Current(command, now)) return new(OwnedExperienceBridgeState.Expired);
            if (publish) return new(response.StatusCode == HttpStatusCode.OK
                ? OwnedExperienceBridgeState.Published : OwnedExperienceBridgeState.Unavailable);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(OwnedExperienceBridgeState.Pending);
            if (response.StatusCode != HttpStatusCode.OK) return new(OwnedExperienceBridgeState.Unavailable);
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                return new(OwnedExperienceBridgeState.InvalidResponse);

            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var bytes = new byte[MaximumResponseBytes + 1];
            int count = await body.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (!Current(command, _clock.GetUtcNow())) return new(OwnedExperienceBridgeState.Expired);
            if (count == bytes.Length) return new(OwnedExperienceBridgeState.InvalidResponse);
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 8, AllowDuplicateProperties = false });
            return ReadReceipt(document.RootElement.GetProperty("value"), command, _clock.GetUtcNow());
        }
        catch (Exception ex) when (ex is (JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
            && ex is not ObjectDisposedException && !ct.IsCancellationRequested)
        { return new(OwnedExperienceBridgeState.InvalidResponse); }
        catch (Exception ex) when (ex is (HttpRequestException or IOException or OperationCanceledException) && !ct.IsCancellationRequested)
        { return new(OwnedExperienceBridgeState.Unavailable); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private static bool Current(OwnedExperienceCommand command, DateTimeOffset now)
        => now >= command.IssuedUtc && now < command.ExpiresUtc;

    private static DateTimeOffset AddDelay(DateTimeOffset now, TimeSpan delay)
        => delay > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay;

    private static string WriteCommand(OwnedExperienceCommand command) => JsonSerializer.Serialize(new
    {
        version = 1,
        operationId = command.OperationId,
        universeId = command.UniverseId,
        userId = command.UserId,
        sourcePlaceId = command.Source.PlaceId,
        sourceJobId = command.Source.JobId,
        destinationPlaceId = command.Destination?.PlaceId,
        destinationJobId = command.Destination?.JobId,
        issuedUtc = command.IssuedUtc.ToUnixTimeSeconds(),
        expiresUtc = command.ExpiresUtc.ToUnixTimeSeconds()
    });

    private static OwnedExperienceBridgeResult ReadReceipt(JsonElement value, OwnedExperienceCommand command, DateTimeOffset now)
    {
        var sent = value.GetProperty("command");
        var operation = ReadJob(sent, "operationId");
        if (operation != command.OperationId) return new(OwnedExperienceBridgeState.Pending);

        var stage = value.GetProperty("stage").GetString() switch
        {
            "atSource" when command.Kind == OwnedExperienceCommandKind.Status => OwnedExperienceStage.AtSource,
            "received" when command.Destination is not null => OwnedExperienceStage.Received,
            "teleportInitiated" when command.Destination is not null => OwnedExperienceStage.TeleportInitiated,
            "arrived" when command.Destination is not null => OwnedExperienceStage.Arrived,
            "rejected" when command.Destination is not null => OwnedExperienceStage.Rejected,
            _ => throw new FormatException()
        };
        var server = stage == OwnedExperienceStage.Arrived ? command.Destination! : command.Source;
        long observed = value.GetProperty("observedUtc").GetInt64();
        if (sent.GetProperty("version").GetInt32() != 1 || sent.GetProperty("universeId").GetInt64() != command.UniverseId
            || sent.GetProperty("userId").GetInt64() != command.UserId
            || sent.GetProperty("sourcePlaceId").GetInt64() != command.Source.PlaceId || ReadJob(sent, "sourceJobId") != command.Source.JobId
            || Optional(sent, "destinationPlaceId")?.GetInt64() != command.Destination?.PlaceId
            || Optional(sent, "destinationJobId")?.GetGuid() != command.Destination?.JobId
            || sent.GetProperty("issuedUtc").GetInt64() != command.IssuedUtc.ToUnixTimeSeconds()
            || sent.GetProperty("expiresUtc").GetInt64() != command.ExpiresUtc.ToUnixTimeSeconds()
            || observed < command.IssuedUtc.ToUnixTimeSeconds() || observed >= command.ExpiresUtc.ToUnixTimeSeconds()
            || observed > now.ToUnixTimeSeconds() + 2
            || value.GetProperty("observedPlaceId").GetInt64() != server.PlaceId || ReadJob(value, "observedJobId") != server.JobId)
            return new(OwnedExperienceBridgeState.InvalidResponse);
        return new(OwnedExperienceBridgeState.Acknowledged, new(command.OperationId, command.UniverseId, command.UserId,
            stage, server, DateTimeOffset.FromUnixTimeSeconds(observed)));
    }

    private static JsonElement? Optional(JsonElement source, string name)
        => source.TryGetProperty(name, out var field) && field.ValueKind != JsonValueKind.Null ? field : null;

    private static Guid ReadJob(JsonElement source, string field)
    {
        var id = source.GetProperty(field).GetGuid();
        return id == Guid.Empty ? throw new FormatException() : id;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _http.Dispose();
    }
}
