using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>Explicit account requests only. Fixed origins/paths, no redirects/cookie jar, no retained responses/tokens.</summary>
public sealed class RobloxAccountDataClient : IRobloxAccountDataClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    public RobloxAccountDataClient() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { }
    public RobloxAccountDataClient(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        if (handler is HttpClientHandler native) { native.AllowAutoRedirect = false; native.UseCookies = false; }
        if (handler is SocketsHttpHandler sockets) { sockets.AllowAutoRedirect = false; sockets.UseCookies = false; }
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        _http = new(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public async Task<RobloxAccountResponse> ReadAsync(RobloxAccountRead kind, long userId, long? targetUserId,
        RobloxSessionCredential credential, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (userId <= 0 || !Enum.IsDefined(kind)) throw new ArgumentException("Choose a verified account and a supported observation.");
        bool friendship = kind is RobloxAccountRead.FriendshipStatus or RobloxAccountRead.RequestFriendship or RobloxAccountRead.AcceptFriendship;
        if (friendship != targetUserId.HasValue || friendship && (targetUserId <= 0 || targetUserId == userId))
            throw new ArgumentException("Friendship requires two different reviewed accounts.");
        Uri endpoint = Endpoint(kind, userId, targetUserId);
        bool post = kind is RobloxAccountRead.Presence or RobloxAccountRead.RequestFriendship or RobloxAccountRead.AcceptFriendship;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct); stop.CancelAfter(_timeout);
        RobloxSessionCredential? rotation = null;
        try
        {
            string? csrf = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                byte[]? body = post ? kind == RobloxAccountRead.Presence ? JsonSerializer.SerializeToUtf8Bytes(new { userIds = new[] { userId } }) : "{}"u8.ToArray() : null;
                try
                {
                    using var request = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, endpoint);
                    if (body is not null) { request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new("application/json"); }
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    request.Headers.UserAgent.ParseAdd("ZenithSessions/1.0");
                    request.Headers.Add("Cookie", ".ROBLOSECURITY=" + credential.CopyValue());
                    if (csrf is not null) request.Headers.Add("X-CSRF-TOKEN", csrf);
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token).ConfigureAwait(false);
                    if (response.RequestMessage?.RequestUri is { } actual && actual != endpoint) throw Failure();
                    rotation = RobloxSessionClient.Rotation(response, credential, DateTimeOffset.UtcNow);
                    if (response.Headers.Contains("rblx-challenge-id") || response.Headers.Contains("rblx-challenge-type")) return Answer(RobloxAccountOutcome.Challenge);
                    if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable) return Answer(RobloxAccountOutcome.RateLimited);
                    // Only Roblox's explicit CSRF refusal gets one retry. A rotation must be verified and saved by
                    // the owner first, so never retry the original cookie after a response replaced it.
                    if (post && attempt == 0 && rotation is null && response.StatusCode == HttpStatusCode.Forbidden
                        && response.Headers.TryGetValues("x-csrf-token", out var tokens)
                        && tokens.Take(2).ToArray() is [string token] && token.Length is > 0 and <= 256
                        && token.All(c => c is >= (char)33 and <= (char)126)) { csrf = token; continue; }
                    if (response.StatusCode == HttpStatusCode.Unauthorized) return Answer(RobloxAccountOutcome.SignInRequired);
                    if (!response.IsSuccessStatusCode) return Answer(RobloxAccountOutcome.Unavailable);
                    var observation = await ReadObservationAsync(response, kind, userId, targetUserId, stop.Token).ConfigureAwait(false);
                    return Answer(observation is null ? RobloxAccountOutcome.Unavailable : RobloxAccountOutcome.Success, observation);
                }
                finally { if (body is not null) CryptographicOperations.ZeroMemory(body); }
            }
            return Answer(RobloxAccountOutcome.Unavailable);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new RobloxSessionException(RobloxSessionFailure.TimedOut, "The account request timed out. Check the saved sign-in before retrying."); }
        catch (HttpRequestException) { throw Failure(); }
        catch (IOException) { throw Failure(); }
        // Malformed response bodies can still carry a rotation: let the owner verify it, but show no data.
        catch (JsonException) { return Answer(RobloxAccountOutcome.Unavailable); }
        finally { rotation?.Dispose(); }
        RobloxAccountResponse Answer(RobloxAccountOutcome outcome, RobloxAccountObservation? data = null)
        { var answer = new RobloxAccountResponse(outcome, data, rotation); rotation = null; return answer; }
    }
    private static RobloxSessionException Failure() => new(RobloxSessionFailure.Network, "The account request could not be completed. Check the saved sign-in before retrying.");
    private static Uri Endpoint(RobloxAccountRead kind, long userId, long? target)
    {
        string id = userId.ToString(CultureInfo.InvariantCulture), other = target?.ToString(CultureInfo.InvariantCulture) ?? "";
        return new(kind switch
        {
            RobloxAccountRead.Presence => "https://presence.roblox.com/v1/presence/users",
            RobloxAccountRead.Robux => "https://economy.roblox.com/v1/user/currency",
            RobloxAccountRead.Premium => $"https://premiumfeatures.roblox.com/v1/users/{id}/validate-membership",
            RobloxAccountRead.EmailVerified => "https://accountsettings.roblox.com/v1/email",
            RobloxAccountRead.TwoStepMethods => $"https://twostepverification.roblox.com/v1/users/{id}/configuration",
            RobloxAccountRead.BanState => $"https://users.roblox.com/v1/users/{id}",
            RobloxAccountRead.FriendsCount => $"https://friends.roblox.com/v1/users/{id}/friends/count",
            RobloxAccountRead.FollowersCount => $"https://friends.roblox.com/v1/users/{id}/followers/count",
            RobloxAccountRead.FriendshipStatus => $"https://friends.roblox.com/v1/users/{id}/friends/statuses?userIds={other}",
            RobloxAccountRead.RequestFriendship => $"https://friends.roblox.com/v1/users/{other}/request-friendship",
            RobloxAccountRead.AcceptFriendship => $"https://friends.roblox.com/v1/users/{other}/accept-friend-request",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        });
    }
    private static async Task<RobloxAccountObservation?> ReadObservationAsync(HttpResponseMessage response, RobloxAccountRead kind, long userId, long? target, CancellationToken ct)
    {
        const int limit = 65536;
        if (response.Content.Headers.ContentLength > limit) throw new JsonException();
        byte[] buffer = new byte[limit + 1]; int length = 0;
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false)) > 0)
            { length += count; if (length > limit) throw new JsonException(); }
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 8, AllowDuplicateProperties = false });
            return Parse(kind, document.RootElement, userId, target);
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
    private static RobloxAccountObservation? Parse(RobloxAccountRead kind, JsonElement root, long userId, long? target)
    {
        if (kind == RobloxAccountRead.Premium) return Bool(root) is { } premium ? new(Flag: premium) : null;
        if (root.ValueKind != JsonValueKind.Object) return null;
        switch (kind)
        {
            case RobloxAccountRead.Presence:
                if (!root.TryGetProperty("userPresences", out var presences) || presences.ValueKind != JsonValueKind.Array || presences.GetArrayLength() != 1) return null;
                var row = presences[0];
                var state = Number(row, "userPresenceType");
                if (Number(row, "userId") != userId || state is not (>= 0 and <= 3)) return null;
                long? place = Number(row, "placeId") is > 0 and var value ? value : null;
                string? game = row.TryGetProperty("gameId", out var gameField) && gameField.ValueKind == JsonValueKind.String
                    && RobloxRequestedDestination.TryNormalizeJobId(gameField.GetString(), out var job) ? job : null;
                return new(Presence: new((RobloxReportedPresence)state.Value, state == 2 ? place : null, state == 2 ? game : null));
            case RobloxAccountRead.Robux: return Number(root, "robux") is >= 0 and var robux ? new(Number: robux) : null;
            case RobloxAccountRead.FriendsCount:
            case RobloxAccountRead.FollowersCount: return Number(root, "count") is >= 0 and var count ? new(Number: count) : null;
            case RobloxAccountRead.EmailVerified: return FieldBool(root, "verified") is { } verified ? new(Flag: verified) : null;
            case RobloxAccountRead.BanState: return Number(root, "id") == userId && FieldBool(root, "isBanned") is { } banned ? new(Flag: banned) : null;
            case RobloxAccountRead.TwoStepMethods:
                if (!root.TryGetProperty("methods", out var methods) || methods.ValueKind != JsonValueKind.Array || methods.GetArrayLength() > 16) return null;
                var enabled = new HashSet<string>(StringComparer.Ordinal);
                foreach (var method in methods.EnumerateArray())
                {
                    if (FieldBool(method, "enabled") is not { } on || !method.TryGetProperty("mediaType", out var type)) return null;
                    string? label = type.ValueKind == JsonValueKind.String ? type.GetString() switch
                    { "Email" => "Email", "Authenticator" => "Authenticator", "SMS" or "Sms" => "SMS", "RecoveryCode" => "Recovery code", "SecurityKey" => "Security key", "CrossDevice" => "Cross-device", "Password" => "Password", "Passkey" => "Passkey", _ => null }
                        : type.ValueKind == JsonValueKind.Number && type.TryGetInt32(out int media) ? media switch
                        { 0 => "Email", 1 => "SMS", 2 => "Authenticator", 3 => "Recovery code", 4 => "Security key", 5 => "Cross-device", 6 => "Password", 7 => "Passkey", _ => null } : null;
                    // Unknown enum values are not guessed or presented as disabled.
                    if (on) enabled.Add(label ?? "Other enabled method");
                }
                return new(Methods: enabled.Count == 0 ? "No enabled methods reported" : string.Join(", ", enabled.Order()));
            case RobloxAccountRead.FriendshipStatus:
                if (!root.TryGetProperty("data", out var statuses) || statuses.ValueKind != JsonValueKind.Array || statuses.GetArrayLength() != 1
                    || Number(statuses[0], "id") != target || !statuses[0].TryGetProperty("status", out var status)) return null;
                int? relation = status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out int n) ? n
                    : status.ValueKind == JsonValueKind.String ? status.GetString() switch { "NotFriends" => 0, "Friends" => 1, "RequestSent" => 2, "RequestReceived" => 3, _ => null } : null;
                return relation is >= 0 and <= 3 ? new(Friendship: (RobloxFriendshipStatus)relation.Value) : null;
            case RobloxAccountRead.RequestFriendship:
            case RobloxAccountRead.AcceptFriendship:
                if (root.TryGetProperty("errors", out _) || root.TryGetProperty("success", out var success) && Bool(success) != true
                    || root.TryGetProperty("isCaptchaRequired", out var captcha) && Bool(captcha) != false) return null;
                return new(); // Acceptance is verified separately with friendship status.
            default: return null;
        }
    }
    private static long? Number(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var field)
        && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out long value) ? value : null;
    private static bool? Bool(JsonElement value) => value.ValueKind is JsonValueKind.True ? true : value.ValueKind is JsonValueKind.False ? false : null;
    private static bool? FieldBool(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var field) ? Bool(field) : null;
    public void Dispose() => _http.Dispose();
}
