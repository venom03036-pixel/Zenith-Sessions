using System.Globalization;
using System.Runtime.InteropServices;

namespace Zenith.Sessions.Core;

/// <summary>
/// The declared kind of a share link. ExperienceInvite and NotificationExperienceInvite are friend invites that can name
/// the inviter's server; DeepLinkV2 is a share?code=…&amp;v=v2 link (no type), resolved by a different endpoint.
/// </summary>
public enum RobloxShareLinkType { ExperienceDetails, Server, Other, ExperienceInvite, NotificationExperienceInvite, DeepLinkV2 }

/// <summary>Reads a protected code for the duration of one call. The span must not escape the delegate.</summary>
public delegate void ProtectedCodeReader(ReadOnlySpan<char> code);

/// <summary>
/// A secret code held in a pinned char array: revealed only to a caller-owned span, zeroed on dispose, never
/// serialized, logged or printed. Every subclass redacts ToString.
/// </summary>
public abstract class ProtectedCode : IDisposable
{
    private char[]? _value;
    private GCHandle _pin;

    protected ProtectedCode(ReadOnlySpan<char> code, int minimumLength, int maximumLength, string label)
    {
        if (code.Length < minimumLength || code.Length > maximumLength || !IsSafe(code))
            throw new ArgumentException($"A {label} is {minimumLength}–{maximumLength} ASCII letters or digits.");
        _value = code.ToArray();
        _pin = GCHandle.Alloc(_value, GCHandleType.Pinned);
    }
    ~ProtectedCode() => Dispose(false);

    public int Length => _value?.Length ?? 0;

    public void Reveal(ProtectedCodeReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        char[] value = _value ?? throw new ObjectDisposedException(GetType().Name);
        reader(value);
    }

    public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
    private void Dispose(bool disposing)
    {
        _ = disposing;
        char[]? value = _value; _value = null;
        if (value is null) return;
        Array.Clear(value);
        if (_pin.IsAllocated) _pin.Free();
    }
    private static bool IsSafe(ReadOnlySpan<char> code)
    {
        foreach (char c in code) if (!char.IsAsciiLetterOrDigit(c)) return false;
        return true;
    }
}

/// <summary>The opaque code of a roblox.com/share link. Treated as a secret: it only ever leaves the app inside a resolve request.</summary>
public sealed class ProtectedShareCode(ReadOnlySpan<char> code) : ProtectedCode(code, 1, 64, "share code")
{
    public override string ToString() => "[share code]";
}

/// <summary>A private server link code. Anyone holding it can join, so only its last four characters are ever shown.</summary>
public sealed class ProtectedPrivateServerCode : ProtectedCode
{
    public ProtectedPrivateServerCode(ReadOnlySpan<char> code) : base(code, 4, 128, "private server link code")
        => Last4 = new string(code[^4..]);
    public string Last4 { get; }
    public override string ToString() => "[private server code]";
}

/// <summary>A parsed share link: its protected code and the declared type. Disposing zeroes the code.</summary>
public sealed class RobloxShareLink(ProtectedShareCode code, RobloxShareLinkType type) : IDisposable
{
    public ProtectedShareCode Code { get; } = code ?? throw new ArgumentNullException(nameof(code));
    public RobloxShareLinkType Type { get; } = type;
    public override string ToString() => "Roblox share link { Type = " + Type + ", Code = [redacted] }";
    public void Dispose() => Code.Dispose();
}

public enum RobloxShareLinkOutcome { Experience, PrivateServer, Expired, Invalid, SignInRequired, Unavailable, Cancelled, Busy, ServerInstance }

/// <summary>
/// What a share link resolved to. Public place/universe IDs only, plus the owned private server code when the link was
/// a server invitation, or the canonical job ID of the inviter's server for a ServerInstance (a requested server, never
/// proof of access or arrival). Resolution proves neither access nor arrival. ToString never shows a code.
/// </summary>
public sealed class RobloxShareLinkResolution(RobloxShareLinkOutcome outcome, long? placeId = null, long? universeId = null,
    long? ownerUserId = null, ProtectedPrivateServerCode? privateServerCode = null, string? jobId = null) : IDisposable
{
    public RobloxShareLinkOutcome Outcome { get; } = outcome;
    public long? PlaceId { get; } = placeId;
    public long? UniverseId { get; } = universeId;
    public long? OwnerUserId { get; } = ownerUserId;
    public ProtectedPrivateServerCode? PrivateServerCode { get; } = privateServerCode;
    /// <summary>Set exactly when Outcome is ServerInstance: a place plus a nonempty dashed GUID, stored in canonical form.</summary>
    public string? JobId { get; } = CanonicalJob(outcome, placeId, jobId);
    public override string ToString() => "Share link resolution { Outcome = " + Outcome + ", Place = "
        + (PlaceId?.ToString(CultureInfo.InvariantCulture) ?? "none") + ", Server = " + (JobId is null ? "none" : "requested") + ", Code = [redacted] }";
    private static string? CanonicalJob(RobloxShareLinkOutcome outcome, long? placeId, string? jobId)
    {
        if (outcome != RobloxShareLinkOutcome.ServerInstance)
            return jobId is null ? null : throw new ArgumentException("Only a server invite carries a server job ID.", nameof(jobId));
        if (placeId is not > 0) throw new ArgumentException("A server invite needs a positive place ID.", nameof(placeId));
        return RobloxRequestedDestination.TryNormalizeJobId(jobId, out string? job) ? job
            : throw new ArgumentException("A server job ID must be a nonempty GUID in dashed format.", nameof(jobId));
    }
    public void Dispose() => PrivateServerCode?.Dispose();
}

/// <summary>One signed-in resolve request. The credential is borrowed for the call and never retained or logged.</summary>
public interface IRobloxShareLinkClient
{
    Task<RobloxShareLinkResolution> ResolveAsync(RobloxShareLink link, RobloxSessionCredential credential, CancellationToken ct);
}

/// <summary>Resolves a share link with the selected account's saved session under the link service's one-operation ownership.</summary>
public interface IRobloxShareLinkResolver
{
    Task<RobloxShareLinkResolution> ResolveShareLinkAsync(Guid profileId, RobloxShareLink link, CancellationToken ct);
}

/// <summary>
/// Completes a resolution whose payload named only a universe. Roblox's web page opens such a link at the universe's
/// root place, so the root place is read from the anonymous public games listing: one request, no saved session, after
/// the signed-in resolve has released its credential and account operation. A restricted, missing or failed lookup is
/// Unavailable, never a guess. The result stays a requested destination; nothing launches.
/// </summary>
public sealed class RootPlaceShareLinkResolver(IRobloxShareLinkResolver inner, IPublicRobloxGameBatchDirectory? games) : IRobloxShareLinkResolver
{
    private readonly IRobloxShareLinkResolver _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public async Task<RobloxShareLinkResolution> ResolveShareLinkAsync(Guid profileId, RobloxShareLink link, CancellationToken ct)
    {
        var resolution = await _inner.ResolveShareLinkAsync(profileId, link, ct).ConfigureAwait(false);
        if (resolution.Outcome is not (RobloxShareLinkOutcome.Experience or RobloxShareLinkOutcome.PrivateServer) || resolution.PlaceId is not null)
            return resolution;
        bool owned = true; // Until the private server code moves to the completed resolution, this method owns it.
        try
        {
            if (games is null || resolution.UniverseId is not { } universeId) return new(RobloxShareLinkOutcome.Unavailable);
            var listing = await games.LookupGamesAsync([universeId], ct).ConfigureAwait(false);
            long? rootPlaceId = null;
            if (listing.State == PublicDirectoryState.Success && listing.Value is { } listed)
                foreach (var game in listed)
                    if (game.UniverseId == universeId && game.RootPlaceId > 0) rootPlaceId = game.RootPlaceId;
            if (rootPlaceId is not { } placeId) return new(RobloxShareLinkOutcome.Unavailable);
            owned = false; // Explicit transfer: the completed resolution now owns and disposes the code; the original is dropped.
            return new(resolution.Outcome, placeId, universeId, resolution.OwnerUserId, resolution.PrivateServerCode);
        }
        finally { if (owned) resolution.Dispose(); }
    }
}

/// <summary>
/// Pure, bounded parsing of https://www.roblox.com/share?code=…&amp;type=… links and of share?code=…&amp;v=v2 links (which
/// carry no type). The query must hold exactly those two fields. The existing destination parser keeps discarding share
/// codes; this one keeps the code only inside a <see cref="ProtectedShareCode"/>. No resolution or network access.
/// </summary>
/// <remarks>
/// One definition of a safe roblox.com link: input is first classified by <see cref="RobloxDestinationParser.Parse"/>, so its
/// readable-input, HTTPS, exact-host, authority, userinfo, port, fragment, escape, path and query rules apply here unchanged.
/// This parser only narrows that result, by design: ro.blox.com, roblox:// fields, a code on another path (or as shareCode)
/// and any extra query field are not share links it can resolve.
/// </remarks>
public static class RobloxShareLinkParser
{
    public const int MaximumInputLength = RobloxDestinationParser.MaximumInputLength;
    public const int MaximumCodeLength = 64;

    public static bool TryParse(string? input, out RobloxShareLink? link)
    {
        link = null;
        if (input is null || RobloxDestinationParser.Parse(input).Kind != RobloxDestinationKind.UnresolvedShareLink) return false;
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host is not ("www.roblox.com" or "roblox.com")) return false;
        if (!uri.AbsolutePath.TrimEnd('/').Equals("/share", StringComparison.OrdinalIgnoreCase)) return false;
        if (!RobloxDestinationParser.TryQuery(uri.Query.TrimStart('?'), out var query) || query.Count != 2
            || !query.TryGetValue("code", out string? code)) return false;
        if (code.Length is 0 or > MaximumCodeLength || !code.All(char.IsAsciiLetterOrDigit)) return false;
        RobloxShareLinkType kind;
        if (query.TryGetValue("type", out string? type))
            kind = type switch
            {
                "ExperienceDetails" => RobloxShareLinkType.ExperienceDetails,
                "Server" => RobloxShareLinkType.Server,
                "ExperienceInvite" => RobloxShareLinkType.ExperienceInvite,
                "NotificationExperienceInvite" => RobloxShareLinkType.NotificationExperienceInvite,
                _ => RobloxShareLinkType.Other
            };
        else if (query.TryGetValue("v", out string? version) && version.Equals("v2", StringComparison.OrdinalIgnoreCase)) kind = RobloxShareLinkType.DeepLinkV2;
        else return false;
        link = new RobloxShareLink(new ProtectedShareCode(code), kind);
        return true;
    }
}
