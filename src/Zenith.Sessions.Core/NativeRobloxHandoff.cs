using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Zenith.Sessions.Core;

/// <summary>Preparation binding, not verified native account or arrival evidence.</summary>
public sealed class NativeHandoffContext : IDisposable
{
    private int _state; // 0 awaiting capture, 1 captured, 2 closed, 3 final start claimed.
    // Preparation owner is fresh and non-reconstructible through this API. It is distinct from
    // the persistent queue operation ID and becomes the final protected mutation's owner.
    public Guid OperationId { get; } = Guid.NewGuid();
    public Guid LaunchOperationId { get; }
    public Guid ProfileId { get; }
    public long ExpectedUserId { get; }
    public long PendingAuthorizationGeneration { get; }
    public RobloxRequestedDestination Destination { get; }
    public NativeHandoffContext(Guid launchOperationId, Guid profileId, long expectedUserId,
        long pendingAuthorizationGeneration, RobloxRequestedDestination destination)
    {
        LaunchOperationId = launchOperationId; ProfileId = profileId; ExpectedUserId = expectedUserId;
        PendingAuthorizationGeneration = pendingAuthorizationGeneration; Destination = destination;
        Validated();
    }
    internal bool CanDispatch => Volatile.Read(ref _state) == 1;
    internal bool TryClaimCapture() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;
    internal bool TryBeginStart() => Interlocked.CompareExchange(ref _state, 3, 1) == 1;
    public void Dispose() => Interlocked.Exchange(ref _state, 2);
    public NativeHandoffContext Validated()
    {
        if (OperationId == Guid.Empty || LaunchOperationId == Guid.Empty || ProfileId == Guid.Empty || ExpectedUserId <= 0
            || PendingAuthorizationGeneration is <= 0 or >= long.MaxValue - 1 || Destination is null)
            throw new ArgumentException("A native preparation needs a pinned operation, saved account, generation and destination.");
        return this;
    }
    public override string ToString() => "NativeHandoffContext { Binding = [redacted], Native identity = unverified }";
}

public enum NativeHandoffCaptureStatus
{ AcceptedSourceGrammar, ContextRejected, OriginRejected, InvalidShape, UnsupportedVariant, DestinationMismatch, StaleTimestamp, OperationAlreadyCaptured }
public sealed record NativeHandoffCaptureResult(NativeHandoffCaptureStatus Status, NativeHandoffCandidate? Candidate = null);

/// <summary>
/// Opaque, single-use original bytes from an already-cancelled official-page event. The parser
/// vocabulary is source-derived, not a claim of current Roblox protocol or native compatibility.
/// No URI/ticket getter exists. Managed strings cannot be reliably erased from process memory.
/// </summary>
public sealed class NativeHandoffCandidate : IDisposable
{
    private string? _original;
    private readonly long _captured;
    private readonly TimeProvider _time;
    internal NativeHandoffCandidate(string original, NativeHandoffContext context, TimeProvider time)
    { _original = original; Context = context; _time = time; _captured = time.GetTimestamp(); }
    public NativeHandoffContext Context { get; }
    [JsonIgnore] public bool IsConsumed => Volatile.Read(ref _original) is null;
    [JsonIgnore] public bool IsClosed => !Context.CanDispatch;
    [JsonIgnore] public bool IsExpired => Expired();
    public string Evidence => "Source-derived grammar only; native compatibility and arrival remain unverified.";
    public override string ToString() => "NativeHandoffCandidate { Handoff = [redacted], Evidence = source-derived only }";
    public void Dispose() { Interlocked.Exchange(ref _original, null); Context.Dispose(); }

    internal bool MatchesFinalGrant(AuthorizationWriteSnapshot snapshot)
        => Context.CanDispatch && snapshot.ProfileId == Context.ProfileId && snapshot.HighestMutationId == Context.OperationId
        && snapshot.Record is { Health: AuthorizationHealth.Linked } record && record.ProfileId == Context.ProfileId
        && record.Provider == RobloxSessionIdentity.Provider && record.VerifiedUserId == Context.ExpectedUserId
        && record.Generation == Context.PendingAuthorizationGeneration + 1
        && snapshot.HighestAllocated == record.Generation && snapshot.LastCommitted == record.Generation;

    internal bool Expired()
    {
        TimeSpan age = _time.GetElapsedTime(_captured);
        return age < TimeSpan.Zero || age >= TimeSpan.FromSeconds(10);
    }

    internal NativeHandoffDispatchResult Invoke(IPreparedNativeHandoffStart prepared, CancellationToken ct, ref bool entered)
    {
        ct.ThrowIfCancellationRequested();
        if (Expired()) return new(NativeHandoffDispatchOutcome.NotStarted);
        string? original = Interlocked.Exchange(ref _original, null);
        if (original is null || !Context.TryBeginStart()) return new(NativeHandoffDispatchOutcome.NotStarted);
        ct.ThrowIfCancellationRequested();
        entered = true;
        return prepared.Start(original);
    }
}

/// <summary>Conservative prospective grammar. No browser, HTTP, URI construction or native dispatch.</summary>
public static class NativeRobloxHandoff
{
    public const int MaximumCharacters = 32 * 1024;
    private const string Prefix = "roblox-player:1+", Endpoint = "https://www.roblox.com/Game/PlaceLauncher.ashx?";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly HashSet<string> OuterNames = new(StringComparer.Ordinal)
    { "launchmode", "gameinfo", "launchtime", "placelauncherurl", "browsertrackerid", "robloxLocale", "gameLocale", "LaunchExp", "channel" };
    private static readonly HashSet<string> QueryNames = new(StringComparer.Ordinal)
    { "request", "browserTrackerId", "placeId", "gameId", "isPlayTogetherGame", "joinAttemptId", "joinAttemptOrigin" };

    public static NativeHandoffCaptureResult Capture(NativeHandoffContext? context, string? currentPage,
        string? initiatingOrigin, string? original, TimeProvider? time = null)
    {
        try { context = context?.Validated(); }
        catch (ArgumentException) { return new(NativeHandoffCaptureStatus.ContextRejected); }
        if (context is null || !NativeHandoffNavigation.IsAllowedPage(context, currentPage))
            return new(NativeHandoffCaptureStatus.ContextRejected);
        if (!RobloxHandoffProbePolicy.IsAllowedOrigin(initiatingOrigin)) return new(NativeHandoffCaptureStatus.OriginRejected);
        if (original is not { Length: > 0 and <= MaximumCharacters } || !original.StartsWith(Prefix, StringComparison.Ordinal)
            || original.Any(value => value is < '!' or > '~')) return new(NativeHandoffCaptureStatus.InvalidShape);
        time ??= TimeProvider.System;
        try
        {
            string[] pieces = original[Prefix.Length..].Split('+', 10);
            if (pieces.Length is < 8 or > 9) return new(NativeHandoffCaptureStatus.InvalidShape);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string piece in pieces)
            {
                int colon = piece.IndexOf(':');
                if (colon <= 0 || !OuterNames.Contains(piece[..colon]) || !values.TryAdd(piece[..colon], Decode(piece[(colon + 1)..], false)))
                    return new(NativeHandoffCaptureStatus.InvalidShape);
            }
            if (values.Count - (values.ContainsKey("channel") ? 1 : 0) != 8) return new(NativeHandoffCaptureStatus.InvalidShape);
            if (values["launchmode"] != "play" || values["LaunchExp"] is not ("InApp" or "PreferInApp")
                || values.TryGetValue("channel", out string? channel) && channel.Length != 0)
                return new(NativeHandoffCaptureStatus.UnsupportedVariant);
            string ticket = values["gameinfo"];
            if (ticket.Length is < 1 or > 8192 || ticket.Contains("ROBLOSECURITY", StringComparison.OrdinalIgnoreCase)
                || ticket.Contains("DO-NOT-SHARE", StringComparison.OrdinalIgnoreCase) || ticket.Any(character => character is < '!' or > '~')
                || !Locale(values["robloxLocale"]) || !Locale(values["gameLocale"]) || !Positive(values["browsertrackerid"], out long tracker)
                || tracker > 9_007_199_254_740_991)
                return new(NativeHandoffCaptureStatus.InvalidShape);
            if (!Positive(values["launchtime"], out long milliseconds)) return new(NativeHandoffCaptureStatus.InvalidShape);
            DateTimeOffset issued = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            if ((time.GetUtcNow() - issued).Duration() > TimeSpan.FromSeconds(30)) return new(NativeHandoffCaptureStatus.StaleTimestamp);
            var destination = ParseDestination(values["placelauncherurl"], tracker, context!.Destination);
            if (destination != NativeHandoffCaptureStatus.AcceptedSourceGrammar) return new(destination);
            if (!context.TryClaimCapture()) return new(NativeHandoffCaptureStatus.OperationAlreadyCaptured);
            // Retain the original opaque request. Never rebuild or normalize its ticket-bearing URI.
            return new(NativeHandoffCaptureStatus.AcceptedSourceGrammar, new(original, context, time));
        }
        catch (Exception error) when (error is ArgumentException or KeyNotFoundException or FormatException or OverflowException)
        { return new(NativeHandoffCaptureStatus.InvalidShape); }
    }

    private static NativeHandoffCaptureStatus ParseDestination(string address, long tracker, RobloxRequestedDestination expected)
    {
        if (!address.StartsWith(Endpoint, StringComparison.Ordinal) || address.Contains('#') || address.Contains('\\')
            || address.Any(char.IsWhiteSpace)) return NativeHandoffCaptureStatus.InvalidShape;
        string[] fields = address[Endpoint.Length..].Split('&', 8);
        if (fields.Length is < 6 or > 7) return NativeHandoffCaptureStatus.UnsupportedVariant;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string field in fields)
        {
            int equals = field.IndexOf('=');
            if (equals < 1 || !QueryNames.Contains(field[..equals]) || !values.TryAdd(field[..equals], Decode(field[(equals + 1)..], true)))
                return NativeHandoffCaptureStatus.InvalidShape;
        }
        if (!values.TryGetValue("request", out string? request) || request is not ("RequestGame" or "RequestGameJob"))
            return NativeHandoffCaptureStatus.UnsupportedVariant;
        if (!values.TryGetValue("browserTrackerId", out string? nestedTracker) || !Positive(nestedTracker, out long nested) || nested != tracker
            || !values.TryGetValue("placeId", out string? place) || !Positive(place, out long placeId)
            || !values.TryGetValue("isPlayTogetherGame", out string? together) || together != "false"
            || !values.TryGetValue("joinAttemptId", out string? attempt) || !RobloxRequestedDestination.TryNormalizeJobId(attempt, out _)
            || !values.TryGetValue("joinAttemptOrigin", out string? origin)) return NativeHandoffCaptureStatus.InvalidShape;
        if (placeId != expected.PlaceId) return NativeHandoffCaptureStatus.DestinationMismatch;
        if (request == "RequestGame")
            return expected.JobId is null && !values.ContainsKey("gameId") && origin == "PlayButton" && values.Count == 6
                ? NativeHandoffCaptureStatus.AcceptedSourceGrammar : NativeHandoffCaptureStatus.DestinationMismatch;
        return expected.JobId is not null && values.TryGetValue("gameId", out string? job)
            && RobloxRequestedDestination.TryNormalizeJobId(job, out string? normalized) && normalized == expected.JobId
            && origin == "JoinInstance" && values.Count == 7
            ? NativeHandoffCaptureStatus.AcceptedSourceGrammar : NativeHandoffCaptureStatus.DestinationMismatch;
    }

    private static bool Positive(string text, out long value)
        => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0 && text[0] != '0' && text.All(char.IsAsciiDigit);
    private static bool Locale(string value) => value.Length is >= 2 and <= 24 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    private static string Decode(string encoded, bool query)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(encoded.Length);
        try
        {
            int count = 0;
            for (int index = 0; index < encoded.Length; index++)
            {
                char value = encoded[index];
                if (value == '%')
                {
                    if (index + 2 >= encoded.Length || !char.IsAsciiHexDigit(encoded[index + 1]) || !char.IsAsciiHexDigit(encoded[index + 2])) throw new FormatException();
                    rented[count++] = (byte)((Hex(encoded[index + 1]) << 4) | Hex(encoded[index + 2])); index += 2;
                }
                else if (query && value == '+') rented[count++] = (byte)' ';
                else if (char.IsAsciiLetterOrDigit(value) || "-_.!~*'()".Contains(value)) rented[count++] = (byte)value;
                else throw new FormatException();
            }
            string decoded = Utf8.GetString(rented, 0, count);
            if (decoded.Any(char.IsControl)) throw new FormatException();
            return decoded;
        }
        finally { ArrayPool<byte>.Shared.Return(rented, clearArray: true); }
    }
    private static int Hex(char value) => value <= '9' ? value - '0' : (value | 0x20) - 'a' + 10;
}
