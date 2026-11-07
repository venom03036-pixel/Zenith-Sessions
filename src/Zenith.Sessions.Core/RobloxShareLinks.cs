using System.Globalization;
using System.Runtime.InteropServices;

namespace Zenith.Sessions.Core;

public enum RobloxShareLinkType { ExperienceDetails, Server, Other }

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

public enum RobloxShareLinkOutcome { Experience, PrivateServer, Expired, Invalid, SignInRequired, Unavailable, Cancelled, Busy }

/// <summary>
/// What a share link resolved to. Public place/universe IDs only, plus the owned private server code when the link was
/// a server invitation. Resolution proves neither access nor arrival. ToString never shows a code.
/// </summary>
public sealed class RobloxShareLinkResolution(RobloxShareLinkOutcome outcome, long? placeId = null, long? universeId = null,
    long? ownerUserId = null, ProtectedPrivateServerCode? privateServerCode = null) : IDisposable
{
    public RobloxShareLinkOutcome Outcome { get; } = outcome;
    public long? PlaceId { get; } = placeId;
    public long? UniverseId { get; } = universeId;
    public long? OwnerUserId { get; } = ownerUserId;
    public ProtectedPrivateServerCode? PrivateServerCode { get; } = privateServerCode;
    public override string ToString() => "Share link resolution { Outcome = " + Outcome + ", Place = "
        + (PlaceId?.ToString(CultureInfo.InvariantCulture) ?? "none") + ", Code = [redacted] }";
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
/// Pure, bounded parsing of https://www.roblox.com/share?code=…&amp;type=… links. The existing destination parser keeps
/// discarding share codes; this one keeps the code only inside a <see cref="ProtectedShareCode"/>. No resolution or network access.
/// </summary>
public static class RobloxShareLinkParser
{
    public const int MaximumInputLength = RobloxDestinationParser.MaximumInputLength;
    public const int MaximumCodeLength = 64;

    public static bool TryParse(string? input, out RobloxShareLink? link)
    {
        link = null;
        if (string.IsNullOrWhiteSpace(input) || input.Length > MaximumInputLength || input.Any(char.IsControl)) return false;
        string text = input.Trim();
        if (text.Any(char.IsWhiteSpace) || text.Contains('\\') || !ValidEscapes(text)) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0
            || uri.Port != 443 || uri.Fragment.Length != 0 || uri.Host is not ("www.roblox.com" or "roblox.com")) return false;
        // Do not let Uri normalization turn a different input into an allowlisted host.
        int start = text.IndexOf("://", StringComparison.Ordinal) + 3;
        int authorityEnd = text.IndexOfAny(['/', '?', '#'], start);
        string authority = authorityEnd < 0 ? text[start..] : text[start..authorityEnd];
        if (!authority.Equals(uri.Host, StringComparison.OrdinalIgnoreCase) && !authority.Equals(uri.Host + ":443", StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.AbsolutePath.TrimEnd('/').Equals("/share", StringComparison.OrdinalIgnoreCase)) return false;
        if (!TryQuery(uri.Query.TrimStart('?'), out var query) || query.Count != 2
            || !query.TryGetValue("code", out string? code) || !query.TryGetValue("type", out string? type)) return false;
        if (code.Length is 0 or > MaximumCodeLength || !code.All(char.IsAsciiLetterOrDigit)) return false;
        var kind = type switch
        {
            "ExperienceDetails" => RobloxShareLinkType.ExperienceDetails,
            "Server" => RobloxShareLinkType.Server,
            _ => RobloxShareLinkType.Other
        };
        link = new RobloxShareLink(new ProtectedShareCode(code), kind);
        return true;
    }

    private static bool ValidEscapes(string text)
    {
        for (int index = text.IndexOf('%'); index >= 0; index = text.IndexOf('%', index + 1))
            if (index + 2 >= text.Length || !char.IsAsciiHexDigit(text[index + 1]) || !char.IsAsciiHexDigit(text[index + 2])) return false;
        return true;
    }

    private static bool TryQuery(string query, out Dictionary<string, string> fields)
    {
        fields = new(StringComparer.OrdinalIgnoreCase);
        if (query.Length == 0) return true;
        foreach (string part in query.Split('&'))
        {
            int separator = part.IndexOf('=');
            if (separator <= 0) return false;
            string key = Uri.UnescapeDataString(part[..separator]), value = Uri.UnescapeDataString(part[(separator + 1)..]);
            if (!fields.TryAdd(key, value)) return false;
        }
        return true;
    }
}
