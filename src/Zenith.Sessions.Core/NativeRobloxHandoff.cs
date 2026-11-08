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
    [JsonIgnore] internal PrivateServerLaunchLease? PrivateLease { get; }
    public NativeHandoffContext(Guid launchOperationId, Guid profileId, long expectedUserId,
        long pendingAuthorizationGeneration, RobloxRequestedDestination destination, PrivateServerLaunchLease? privateLease = null)
    {
        LaunchOperationId = launchOperationId; ProfileId = profileId; ExpectedUserId = expectedUserId;
        PendingAuthorizationGeneration = pendingAuthorizationGeneration; Destination = destination;
        PrivateLease = privateLease;
        Validated();
    }
    internal bool CanDispatch => Volatile.Read(ref _state) == 1 && (PrivateLease is null || PrivateLease.IsActive);
    internal bool TryClaimCapture() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;
    internal bool TryBeginStart() => (PrivateLease is null || PrivateLease.IsActive) && Interlocked.CompareExchange(ref _state, 3, 1) == 1;
    public void Dispose() { Interlocked.Exchange(ref _state, 2); PrivateLease?.Dispose(); }
    public NativeHandoffContext Validated()
    {
        if (OperationId == Guid.Empty || LaunchOperationId == Guid.Empty || ProfileId == Guid.Empty || ExpectedUserId <= 0
            || PendingAuthorizationGeneration is <= 0 or >= long.MaxValue - 1 || Destination is null)
            throw new ArgumentException("A native preparation needs a pinned operation, saved account, generation and destination.");
        if (Destination.ServerKind == RobloxServerKind.Private
            ? PrivateLease is not { IsActive: true } || PrivateLease.Reference != Destination.PrivateServer || PrivateLease.PlaceId != Destination.PlaceId
            : PrivateLease is not null)
            throw new ArgumentException("A private preparation needs the matching protected record lease.");
        return this;
    }
    public override string ToString() => "NativeHandoffContext { Binding = [redacted], Native identity = unverified }";
}

public enum NativeHandoffCaptureStatus
{ AcceptedSourceGrammar, ContextRejected, OriginRejected, InvalidShape, UnsupportedVariant, DestinationMismatch, StaleTimestamp, OperationAlreadyCaptured }
/// <summary>First rejecting guard only. Never carries input field names, values or launch material.</summary>
public enum NativeHandoffRejectionReason
{
    ContextInvalid, PageMismatch, OriginMismatch, InputLength, ProtocolPrefix, InputCharacters,
    OuterFieldCount, OuterFieldFormat, UnknownOuterField, DuplicateOuterField, OuterEncoding, MissingOuterField,
    LaunchMode, LaunchExperience, NonDefaultChannel, TicketShape, LocaleShape, TrackerShape, TimestampShape, StaleTimestamp,
    DestinationEndpoint, DestinationFieldCount, DestinationFieldFormat, UnknownDestinationField, DuplicateDestinationField,
    DestinationEncoding, RequestVariant, NestedTracker, PlaceShape, AttemptShape, MissingAttemptOrigin,
    PlaceMismatch, PrivateTargetMismatch, PlayTogetherVariant, GameTargetMismatch, JobTargetMismatch, AlreadyCaptured,
    UnsupportedReferrerField, UnsupportedLaunchDataField, UnsupportedEventField
}

internal static class NativeHandoffRejectionDiagnostics
{
    internal static bool IsValid(NativeHandoffCaptureStatus? status, NativeHandoffRejectionReason? reason)
        => reason is null || Enum.IsDefined(reason.Value) && status is not null and not NativeHandoffCaptureStatus.AcceptedSourceGrammar;
}

public sealed record NativeHandoffCaptureResult(NativeHandoffCaptureStatus Status, NativeHandoffCandidate? Candidate = null,
    NativeHandoffRejectionReason? RejectionReason = null);

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
    internal NativeHandoffCandidate(string original, NativeHandoffContext context, TimeProvider time, long browserTrackerId)
    { _original = original; Context = context; _time = time; _captured = time.GetTimestamp(); BrowserTrackerId = browserTrackerId; }
    public NativeHandoffContext Context { get; }
    /// <summary>
    /// The validated browser tracker ID the request carries, kept apart from the ticket-bearing original. Not a
    /// secret, unlike the ticket, and corroboration only: it proves neither the account nor arrival.
    /// </summary>
    public long BrowserTrackerId { get; }
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

/// <summary>
/// Conservative prospective grammar. No browser, HTTP or native dispatch. <see cref="Capture"/> accepts the official page's
/// own request after its page and origin checks; <see cref="AcceptBuilt"/> accepts a request Zenith built itself in the
/// Direct ticket mode (R-api-7) through exactly the same grammar, destination, time-window and one-claim rules, so the
/// request is validated as-is and nothing is rewritten after validation.
/// </summary>
public static class NativeRobloxHandoff
{
    public const int MaximumCharacters = 32 * 1024;
    private const string Prefix = "roblox-player:1+", Endpoint = "https://www.roblox.com/Game/PlaceLauncher.ashx?";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly HashSet<string> OuterNames = new(StringComparer.Ordinal)
    { "launchmode", "gameinfo", "launchtime", "placelauncherurl", "browsertrackerid", "robloxLocale", "gameLocale", "LaunchExp", "channel" };
    private static readonly HashSet<string> QueryNames = new(StringComparer.Ordinal)
    { "request", "browserTrackerId", "placeId", "gameId", "isPlayTogetherGame", "joinAttemptId", "joinAttemptOrigin", "linkCode" };

    public static NativeHandoffCaptureResult Capture(NativeHandoffContext? context, string? currentPage,
        string? initiatingOrigin, string? original, TimeProvider? time = null)
    {
        try { context = context?.Validated(); }
        catch (ArgumentException) { return Reject(NativeHandoffCaptureStatus.ContextRejected, NativeHandoffRejectionReason.ContextInvalid); }
        if (context is null) return Reject(NativeHandoffCaptureStatus.ContextRejected, NativeHandoffRejectionReason.ContextInvalid);
        if (!NativeHandoffNavigation.IsAllowedPage(context, currentPage))
            return Reject(NativeHandoffCaptureStatus.ContextRejected, NativeHandoffRejectionReason.PageMismatch);
        if (!RobloxHandoffProbePolicy.IsAllowedOrigin(initiatingOrigin)) return Reject(NativeHandoffCaptureStatus.OriginRejected, NativeHandoffRejectionReason.OriginMismatch);
        return Accept(context, original, time ?? TimeProvider.System);
    }

    /// <summary>
    /// Direct ticket mode: there is no page or initiating origin to check, because Zenith built the request itself from a
    /// ticket Roblox issued for the saved sign-in; every other rule is the one <see cref="Capture"/> applies.
    /// </summary>
    public static NativeHandoffCaptureResult AcceptBuilt(NativeHandoffContext? context, string? original, TimeProvider? time = null)
    {
        try { context = context?.Validated(); }
        catch (ArgumentException) { return Reject(NativeHandoffCaptureStatus.ContextRejected, NativeHandoffRejectionReason.ContextInvalid); }
        if (context is null || context.Destination.ServerKind == RobloxServerKind.Private) return Reject(NativeHandoffCaptureStatus.ContextRejected, NativeHandoffRejectionReason.ContextInvalid);
        return Accept(context, original, time ?? TimeProvider.System);
    }

    private static NativeHandoffCaptureResult Accept(NativeHandoffContext context, string? original, TimeProvider time)
    {
        if (original is not { Length: > 0 and <= MaximumCharacters }) return Reject(NativeHandoffCaptureStatus.InvalidShape, NativeHandoffRejectionReason.InputLength);
        if (!original.StartsWith(Prefix, StringComparison.Ordinal)) return Reject(NativeHandoffCaptureStatus.InvalidShape, NativeHandoffRejectionReason.ProtocolPrefix);
        if (original.Any(value => value is < '!' or > '~')) return Reject(NativeHandoffCaptureStatus.InvalidShape, NativeHandoffRejectionReason.InputCharacters);
        var exceptionReason = NativeHandoffRejectionReason.MissingOuterField;
        try
        {
            string[] pieces = original[Prefix.Length..].Split('+', 10);
            if (pieces.Length is < 8 or > 9) return Reject(NativeHandoffCaptureStatus.InvalidShape, NativeHandoffRejectionReason.OuterFieldCount);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string piece in pieces)
            {
                int colon = piece.IndexOf(':');
                if (colon <= 0) return Reject(NativeHandoffCaptureStatus.InvalidShape, NativeHandoffRejectionReason.OuterFieldFormat);
                if (!OuterNames.Contains(piece[..colon])) return Reject(NativeHandoffCaptureStatus.InvalidShape, NativeHandoffRejectionReason.UnknownOuterField);
                exceptionReason = NativeHandoffRejectionReason.OuterEncoding;
                if (!values.TryAdd(piece[..colon], Decode(piece[(colon + 1)..], false)))
                    return Reject(NativeHandoffCaptureStatus.InvalidShape, NativeHandoffRejectionReason.DuplicateOuterField);
            }
            if (values.Count - (values.ContainsKey("channel") ? 1 : 0) != 8) return Reject(NativeHandoffCaptureStatus.InvalidShape, NativeHandoffRejectionReason.MissingOuterField);
            exceptionReason = NativeHandoffRejectionReason.MissingOuterField;
            if (values["launchmode"] != "play") return Reject(NativeHandoffCaptureStatus.UnsupportedVariant, NativeHandoffRejectionReason.LaunchMode);
            if (values["LaunchExp"] is not ("InApp" or "PreferInApp")) return Reject(NativeHandoffCaptureStatus.UnsupportedVariant, NativeHandoffRejectionReason.LaunchExperience);
            if (values.TryGetValue("channel", out string? channel) && channel.Length != 0)
                return Reject(NativeHandoffCaptureStatus.UnsupportedVariant, NativeHandoffRejectionReason.NonDefaultChannel);
            string ticket = values["gameinfo"];
            if (ticket.Length is < 1 or > 8192 || ticket.Contains("ROBLOSECURITY", StringComparison.OrdinalIgnoreCase)
                || ticket.Contains("DO-NOT-SHARE", StringComparison.OrdinalIgnoreCase) || ticket.Any(character => character is < '!' or > '~'))
                return Reject(NativeHandoffCaptureStatus.InvalidShape, NativeHandoffRejectionReason.TicketShape);
            if (!Locale(values["robloxLocale"]) || !Locale(values["gameLocale"])) return Reject(NativeHandoffCaptureStatus.InvalidShape, NativeHandoffRejectionReason.LocaleShape);
            if (!Positive(values["browsertrackerid"], out long tracker) || tracker > LaunchProcessReceipt.MaximumBrowserTrackerId)
                return Reject(NativeHandoffCaptureStatus.InvalidShape, NativeHandoffRejectionReason.TrackerShape);
            exceptionReason = NativeHandoffRejectionReason.TimestampShape;
            if (!Positive(values["launchtime"], out long milliseconds)) return Reject(NativeHandoffCaptureStatus.InvalidShape, NativeHandoffRejectionReason.TimestampShape);
            DateTimeOffset issued = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            if ((time.GetUtcNow() - issued).Duration() > TimeSpan.FromSeconds(30)) return Reject(NativeHandoffCaptureStatus.StaleTimestamp, NativeHandoffRejectionReason.StaleTimestamp);
            exceptionReason = NativeHandoffRejectionReason.DestinationEncoding;
            var destination = ParseDestination(values["placelauncherurl"], tracker, context);
            if (destination.Status != NativeHandoffCaptureStatus.AcceptedSourceGrammar) return destination;
            if (!context.TryClaimCapture()) return Reject(NativeHandoffCaptureStatus.OperationAlreadyCaptured, NativeHandoffRejectionReason.AlreadyCaptured);
            // Retain the original opaque request. Never rebuild or normalize its ticket-bearing URI.
            // The tracker (validated above, equal in both places it appears) leaves only as its own number.
            return new(NativeHandoffCaptureStatus.AcceptedSourceGrammar, new(original, context, time, tracker));
        }
        catch (Exception error) when (error is ArgumentException or KeyNotFoundException or FormatException or OverflowException)
        { return Reject(NativeHandoffCaptureStatus.InvalidShape, exceptionReason); }
    }

    private static NativeHandoffCaptureResult Reject(NativeHandoffCaptureStatus status, NativeHandoffRejectionReason reason)
        => new(status, RejectionReason: reason);

    private static NativeHandoffCaptureResult ParseDestination(string address, long tracker, NativeHandoffContext context)
    {
        var expected = context.Destination;
        if (!address.StartsWith(Endpoint, StringComparison.Ordinal) || address.Contains('#') || address.Contains('\\')
            || address.Any(char.IsWhiteSpace)) return Reject(NativeHandoffCaptureStatus.InvalidShape, NativeHandoffRejectionReason.DestinationEndpoint);
        string[] fields = address[Endpoint.Length..].Split('&', 8);
        if (fields.Length is < 6 or > 7) return Reject(NativeHandoffCaptureStatus.UnsupportedVariant, NativeHandoffRejectionReason.DestinationFieldCount);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string field in fields)
        {
            int equals = field.IndexOf('=');
            if (equals < 1) return Reject(NativeHandoffCaptureStatus.InvalidShape, NativeHandoffRejectionReason.DestinationFieldFormat);
            if (!QueryNames.Contains(field[..equals])) return Reject(NativeHandoffCaptureStatus.InvalidShape, field[..equals] switch
            {
                // Known source examples remain unsupported. Classify only these fixed names; never echo an arbitrary name or value.
                "referredByPlayerId" => NativeHandoffRejectionReason.UnsupportedReferrerField,
                "launchData" => NativeHandoffRejectionReason.UnsupportedLaunchDataField,
                "eventId" => NativeHandoffRejectionReason.UnsupportedEventField,
                _ => NativeHandoffRejectionReason.UnknownDestinationField
            });
            if (!values.TryAdd(field[..equals], Decode(field[(equals + 1)..], true)))
                return Reject(NativeHandoffCaptureStatus.InvalidShape, NativeHandoffRejectionReason.DuplicateDestinationField);
        }
        if (!values.TryGetValue("request", out string? request) || request is not ("RequestGame" or "RequestGameJob" or "RequestPrivateGame"))
            return Reject(NativeHandoffCaptureStatus.UnsupportedVariant, NativeHandoffRejectionReason.RequestVariant);
        if (!values.TryGetValue("browserTrackerId", out string? nestedTracker) || !Positive(nestedTracker, out long nested) || nested != tracker)
            return Reject(NativeHandoffCaptureStatus.InvalidShape, NativeHandoffRejectionReason.NestedTracker);
        if (!values.TryGetValue("placeId", out string? place) || !Positive(place, out long placeId)) return Reject(NativeHandoffCaptureStatus.InvalidShape, NativeHandoffRejectionReason.PlaceShape);
        if (!values.TryGetValue("joinAttemptId", out string? attempt) || !RobloxRequestedDestination.TryNormalizeJobId(attempt, out _))
            return Reject(NativeHandoffCaptureStatus.InvalidShape, NativeHandoffRejectionReason.AttemptShape);
        if (!values.TryGetValue("joinAttemptOrigin", out string? origin)) return Reject(NativeHandoffCaptureStatus.InvalidShape, NativeHandoffRejectionReason.MissingAttemptOrigin);
        if (placeId != expected.PlaceId) return Reject(NativeHandoffCaptureStatus.DestinationMismatch, NativeHandoffRejectionReason.PlaceMismatch);
        if (request == "RequestPrivateGame")
            return expected.ServerKind == RobloxServerKind.Private && values.Count == 6 && origin == "PlayButton"
                && values.TryGetValue("linkCode", out string? code) && context.PrivateLease?.MatchesCode(code) == true
                ? new(NativeHandoffCaptureStatus.AcceptedSourceGrammar) : Reject(NativeHandoffCaptureStatus.DestinationMismatch, NativeHandoffRejectionReason.PrivateTargetMismatch);
        if (expected.ServerKind == RobloxServerKind.Private) return Reject(NativeHandoffCaptureStatus.DestinationMismatch, NativeHandoffRejectionReason.PrivateTargetMismatch);
        if (!values.TryGetValue("isPlayTogetherGame", out string? together) || together != "false") return Reject(NativeHandoffCaptureStatus.InvalidShape, NativeHandoffRejectionReason.PlayTogetherVariant);
        if (request == "RequestGame")
            return expected.JobId is null && !values.ContainsKey("gameId") && origin == "PlayButton" && values.Count == 6
                ? new(NativeHandoffCaptureStatus.AcceptedSourceGrammar) : Reject(NativeHandoffCaptureStatus.DestinationMismatch, NativeHandoffRejectionReason.GameTargetMismatch);
        return expected.JobId is not null && values.TryGetValue("gameId", out string? job)
            && RobloxRequestedDestination.TryNormalizeJobId(job, out string? normalized) && normalized == expected.JobId
            && origin == "JoinInstance" && values.Count == 7
            ? new(NativeHandoffCaptureStatus.AcceptedSourceGrammar) : Reject(NativeHandoffCaptureStatus.DestinationMismatch, NativeHandoffRejectionReason.JobTargetMismatch);
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
