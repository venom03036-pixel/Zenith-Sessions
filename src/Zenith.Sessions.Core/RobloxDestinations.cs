using System.Globalization;

namespace Zenith.Sessions.Core;

public enum RobloxDestinationStatus { Resolved, Unresolved, Invalid }
public enum RobloxDestinationKind
{
    Place, ServerInstance, UnresolvedShareLink, UnresolvedPrivateServer,
    UnresolvedReservedServer, UnresolvedFollowUser, UnresolvedUnknownLink, Invalid
}
public enum RobloxServerKind { Unknown, Public }

/// <summary>A requested destination only. It never establishes account identity, server availability, or arrival.</summary>
public sealed class RobloxRequestedDestination
{
    public long PlaceId { get; }
    public string? JobId { get; }
    public RobloxServerKind ServerKind { get; }

    public RobloxRequestedDestination(long placeId, string? jobId = null, RobloxServerKind serverKind = RobloxServerKind.Unknown)
    {
        if (placeId <= 0) throw new ArgumentOutOfRangeException(nameof(placeId), "Place IDs must be positive Int64 values.");
        if (serverKind is not (RobloxServerKind.Unknown or RobloxServerKind.Public)) throw new ArgumentOutOfRangeException(nameof(serverKind));
        if (jobId is not null && !TryNormalizeJobId(jobId, out _)) throw new ArgumentException("A server job ID must be a nonempty GUID in dashed format.", nameof(jobId));
        if (serverKind == RobloxServerKind.Public && jobId is null) throw new ArgumentException("A public server observation needs a job ID.", nameof(jobId));
        PlaceId = placeId;
        JobId = jobId is null ? null : Guid.ParseExact(jobId, "D").ToString("D");
        ServerKind = serverKind;
    }

    // Builds only the documented HTTPS form. This method neither opens nor launches anything.
    public Uri ToWebStartUri() => new("https://www.roblox.com/games/start?placeId=" + PlaceId.ToString(CultureInfo.InvariantCulture)
        + (JobId is null ? "" : "&gameInstanceId=" + Uri.EscapeDataString(JobId)));
    public Uri ToGamePageUri() => new("https://www.roblox.com/games/" + PlaceId.ToString(CultureInfo.InvariantCulture));
    public override string ToString() => $"Requested Roblox destination {{ PlaceId = {PlaceId}, JobId = {JobId ?? "unspecified"}, ServerKind = {ServerKind}, Arrival = unverified }}";

    public static bool TryNormalizeJobId(string? value, out string? normalized)
    {
        normalized = null;
        if (value is not { Length: 36 } || !Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty) return false;
        normalized = id.ToString("D");
        return true;
    }
}

/// <summary>Safe metadata only: the original input and share/private/reserved codes are deliberately discarded.</summary>
public sealed record RobloxDestinationParseResult(RobloxDestinationStatus Status, RobloxDestinationKind Kind,
    RobloxRequestedDestination? RequestedDestination, string Explanation)
{
    public override string ToString() => $"Roblox destination {{ Status = {Status}, Kind = {Kind}, Sensitive link values = [discarded] }}";
}

/// <summary>Pure, bounded parsing. No redirects, resolution, network access, or native launch.</summary>
public static class RobloxDestinationParser
{
    public const int MaximumInputLength = 4096;
    // Roblox documents these legacy deep-link parameters at:
    // https://create.roblox.com/docs/production/promotion/deeplinks
    // Share links supersede public deep links, but opaque share codes cannot be resolved locally.
    public static RobloxDestinationParseResult Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > MaximumInputLength || input.Any(char.IsControl))
            return Invalid("Enter a place ID or a Roblox link of at most 4,096 readable characters.");
        string text = input.Trim();
        if (text.All(char.IsAsciiDigit))
            return TryPlaceId(text, out long id) ? Resolved(id, null) : Invalid("Place IDs must be positive Int64 values.");
        if (text.Any(char.IsWhiteSpace) || text.Contains('\\') || !ValidEscapes(text))
            return Invalid("The link contains whitespace, invalid escaping, or an unsupported path separator.");

        if (text.StartsWith("roblox://", StringComparison.OrdinalIgnoreCase))
        {
            string fields = text[9..];
            // The documented protocol form puts fields directly after //, not in a URL host.
            if (fields.IndexOfAny(['/', '?', '#', '@', ':']) >= 0)
                return Invalid("Use the documented roblox://placeId=... deep-link format.");
            if (!TryQuery(fields, out var nativeQuery)) return InvalidQuery();
            return ParseFields(nativeQuery, null, true);
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length != 0 || uri.Port != 443 || uri.Fragment.Length != 0
            || uri.Host is not ("www.roblox.com" or "roblox.com" or "ro.blox.com"))
            return Invalid("Only exact-host HTTPS Roblox links and the documented Roblox deep-link format are accepted.");
        // Do not let Uri path/host normalization turn a different input into an allowlisted destination.
        int authorityEnd = text.IndexOfAny(['/', '?', '#'], text.IndexOf("://", StringComparison.Ordinal) + 3);
        string authority = authorityEnd < 0 ? text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..]
            : text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..authorityEnd];
        if (!authority.Equals(uri.Host, StringComparison.OrdinalIgnoreCase)
            && !authority.Equals(uri.Host + ":443", StringComparison.OrdinalIgnoreCase))
            return Invalid("The link host is not in the exact-host allowlist.");
        string rawPath = authorityEnd >= 0 && text[authorityEnd] == '/' ? text[authorityEnd..].Split('?', 2)[0] : "/";
        string decodedPath = Uri.UnescapeDataString(rawPath);
        if (decodedPath.Split('/').Any(p => p is "." or "..") || rawPath.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || rawPath.Contains("%5c", StringComparison.OrdinalIgnoreCase) || decodedPath.Any(char.IsControl))
            return Invalid("The link contains an ambiguous or unsupported path.");
        if (!TryQuery(uri.Query.TrimStart('?'), out var query)) return InvalidQuery();

        if (uri.Host == "ro.blox.com" || uri.AbsolutePath.TrimEnd('/').Equals("/share", StringComparison.OrdinalIgnoreCase))
            return Unresolved(RobloxDestinationKind.UnresolvedShareLink, "This opaque share or deferred link needs official Roblox resolution. Its code was discarded; no place or job ID was inferred.");
        if (uri.AbsolutePath.TrimEnd('/').Equals("/games/start", StringComparison.OrdinalIgnoreCase))
            return ParseFields(query, null, true);

        string[] path = uri.AbsolutePath.Split('/');
        if (path.Length is >= 3 and <= 5 && path[0] == "" && path[1].Equals("games", StringComparison.OrdinalIgnoreCase)
            && TryPlaceId(path[2], out long placeId)
            && (path.Length < 5 || path[4] == "" && path[3].Length > 0) && (path.Length < 4 || path[3].Length <= 500))
            return ParseFields(query, placeId, false);
        if (path.Length >= 3 && path[1].Equals("games", StringComparison.OrdinalIgnoreCase) && IsNumericLike(path[2]))
            return Invalid("The game link has an invalid or overflowing place ID.");
        return Unresolved(RobloxDestinationKind.UnresolvedUnknownLink, "This Roblox URL format is not supported. No destination was inferred.");
    }

    private static RobloxDestinationParseResult ParseFields(Dictionary<string, string> query, long? pathPlaceId, bool isStartLink)
    {
        long? placeId = pathPlaceId;
        if (query.TryGetValue("placeId", out string? value))
        {
            if (pathPlaceId.HasValue || !TryPlaceId(value, out long id))
                return Invalid("The place ID is invalid or appears in both the path and query.");
            placeId = id;
        }
        if (query.TryGetValue("userId", out string? userId) && !TryPlaceId(userId, out _))
            return Invalid("The follow-user ID is invalid.");
        string? job = null;
        if (query.TryGetValue("gameInstanceId", out string? jobValue) && !RobloxRequestedDestination.TryNormalizeJobId(jobValue, out job))
            return Invalid("The server job ID must be a nonempty GUID in dashed format.");
        bool reserved = query.ContainsKey("reservedServerAccessCode");
        bool privateServer = query.ContainsKey("privateServerLinkCode") || query.ContainsKey("vipServerLinkCode")
            || query.ContainsKey("linkCode") || query.ContainsKey("accessCode");
        if (reserved && privateServer)
            return Unresolved(RobloxDestinationKind.UnresolvedUnknownLink, "The link mixes private and reserved server fields. Sensitive values were discarded.");
        if (reserved)
            return Unresolved(RobloxDestinationKind.UnresolvedReservedServer, "A reserved server access code cannot be resolved as a public job ID. The code was discarded.");
        if (privateServer)
            return Unresolved(RobloxDestinationKind.UnresolvedPrivateServer, "A private server link or access code needs an authorized Roblox flow. Its code was discarded; private versus reserved access was not inferred.");
        if (query.ContainsKey("code") || query.ContainsKey("shareCode"))
            return Unresolved(RobloxDestinationKind.UnresolvedShareLink, "This link has an opaque share code. Its code was discarded; no destination was inferred.");
        if (query.ContainsKey("userId"))
            return Unresolved(RobloxDestinationKind.UnresolvedFollowUser, "Following a user needs current authorized Roblox resolution. No destination was inferred.");
        if (query.Keys.Any(k => !k.Equals("placeId", StringComparison.OrdinalIgnoreCase) && !k.Equals("gameInstanceId", StringComparison.OrdinalIgnoreCase)))
            return Unresolved(RobloxDestinationKind.UnresolvedUnknownLink, "This link contains unsupported parameters, including any launch data. Their values were discarded; no destination was inferred.");
        if (!placeId.HasValue) return Invalid("A place ID is required for a place or server destination.");
        if (job is not null && !isStartLink)
            return Unresolved(RobloxDestinationKind.UnresolvedUnknownLink, "A server job ID is supported only in the documented /games/start or roblox://placeId=... format.");
        return Resolved(placeId.Value, job);
    }

    // Also the share link parser's query reader, so both parsers share one set of query limits.
    internal static bool TryQuery(string fields, out Dictionary<string, string> query)
    {
        query = new(StringComparer.OrdinalIgnoreCase);
        if (fields.Length == 0) return true;
        string[] entries = fields.Split('&');
        if (entries.Length > 32) return false;
        foreach (string entry in entries)
        {
            if (entry.Length == 0) return false;
            string[] pair = entry.Split('=', 2);
            string key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            string value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
            if (key.Length is < 1 or > 80 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')
                || value.Length > 2048 || value.Any(char.IsControl) || !query.TryAdd(key, value)) return false;
        }
        return true;
    }
    private static bool ValidEscapes(string value)
    {
        for (int i = 0; i < value.Length; i++)
            if (value[i] == '%' && (i + 2 >= value.Length || !char.IsAsciiHexDigit(value[++i]) || !char.IsAsciiHexDigit(value[++i]))) return false;
        return true;
    }
    private static bool TryPlaceId(string value, out long id) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id)
        && value.All(char.IsAsciiDigit) && id > 0;
    private static bool IsNumericLike(string value) => value.Length == 0 || value[0] is '+' or '-' || value.All(char.IsAsciiDigit);
    private static RobloxDestinationParseResult Resolved(long placeId, string? jobId) => new(RobloxDestinationStatus.Resolved,
        jobId is null ? RobloxDestinationKind.Place : RobloxDestinationKind.ServerInstance, new(placeId, jobId),
        "Requested destination parsed. Account identity, server availability, and arrival remain unverified.");
    private static RobloxDestinationParseResult Unresolved(RobloxDestinationKind kind, string explanation) => new(RobloxDestinationStatus.Unresolved, kind, null, explanation);
    private static RobloxDestinationParseResult Invalid(string explanation) => new(RobloxDestinationStatus.Invalid, RobloxDestinationKind.Invalid, null, explanation);
    private static RobloxDestinationParseResult InvalidQuery() => Invalid("The link has duplicate, malformed, oversized, or unreadable query fields.");
}

public enum RobloxPublicServerSortOrder { Asc, Desc }

public sealed record RobloxPublicServerQuery(long PlaceId, int PageSize = 10, int MaxPages = 1, string? Cursor = null,
    RobloxPublicServerSortOrder SortOrder = RobloxPublicServerSortOrder.Asc, bool ExcludeFullGames = false)
{
    public RobloxPublicServerQuery Validated()
    {
        if (PlaceId <= 0) throw new ArgumentOutOfRangeException(nameof(PlaceId));
        if (PageSize is not (10 or 25 or 50 or 100)) throw new ArgumentOutOfRangeException(nameof(PageSize), "Choose a page size of 10, 25, 50, or 100.");
        if (MaxPages is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(MaxPages), "A browse operation is limited to ten pages.");
        if (!Enum.IsDefined(SortOrder)) throw new ArgumentOutOfRangeException(nameof(SortOrder));
        if (Cursor is not null && (Cursor.Length is < 1 or > 2048 || Cursor.Any(char.IsControl))) throw new ArgumentException("The pagination cursor is invalid.", nameof(Cursor));
        return this;
    }
    public override string ToString() => $"Public Roblox server query {{ PlaceId = {PlaceId}, PageSize = {PageSize}, MaxPages = {MaxPages}, SortOrder = {SortOrder}, ExcludeFullGames = {ExcludeFullGames}, Cursor = [omitted] }}";
}

public sealed record RobloxPublicServer(long PlaceId, string JobId, int Playing, int MaxPlayers, double? Fps, int? Ping, DateTimeOffset ObservedUtc)
{
    public bool HasOpenSlots => Playing < MaxPlayers;
    public RobloxRequestedDestination RequestedDestination => new(PlaceId, JobId, RobloxServerKind.Public);
}

public enum RobloxPublicServerStatus { Complete, PageLimit, RateLimited, Deferred, Unavailable, InvalidResponse }
public sealed record RobloxPublicServerResult(RobloxPublicServerStatus Status, IReadOnlyList<RobloxPublicServer> Servers,
    string? NextPageCursor, TimeSpan? RetryAfter, string Explanation)
{
    public override string ToString() => $"Public Roblox server result {{ Status = {Status}, Count = {Servers.Count}, Cursor = [omitted] }}";
}
public interface IRobloxPublicServerDirectory
{
    Task<RobloxPublicServerResult> BrowseAsync(RobloxPublicServerQuery query, CancellationToken ct);
}
