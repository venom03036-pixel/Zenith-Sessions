using System.Globalization;
using System.Text;

namespace Zenith.Sessions.Core;

public static class PublicRobloxGameSearchLimits
{
    public const int MaximumItems = 50, MaximumScannedItems = 1024;
    public const int MaximumQueryCharacters = 100, MaximumQueryUtf8Bytes = 400;
    public const int MaximumPageTokenUtf8Bytes = 2048;
    public const int MaximumNameCharacters = 256, MaximumCreatorNameCharacters = 100;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool SafeText(string? value, int maximumCharacters, int maximumBytes)
    {
        if (value is null || value.Length == 0 || value.Length > maximumCharacters || string.IsNullOrWhiteSpace(value)) return false;
        foreach (char character in value)
            if (char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format) return false;
        try { return StrictUtf8.GetByteCount(value) <= maximumBytes; }
        catch (EncoderFallbackException) { return false; }
    }

    internal static bool SafeToken(string? value)
        => value is null || SafeText(value, MaximumPageTokenUtf8Bytes, MaximumPageTokenUtf8Bytes)
            && !value.Any(char.IsWhiteSpace);
}

/// <summary>
/// Plain-name search only. Trim is the sole text normalization; URLs, destination IDs and
/// recognizable private/session material belong to the destination parser and never this transport.
/// The random session ID is ephemeral search context, never an authenticated Roblox session.
/// </summary>
public sealed record PublicRobloxGameSearchQuery(string Text, Guid SessionId, string? PageToken = null)
{
    public PublicRobloxGameSearchQuery Validated()
    {
        if (Text is null || Text.Length > RobloxDestinationParser.MaximumInputLength || SessionId == Guid.Empty
            || Text.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format))
            throw Invalid();
        string text = Text.Trim();
        string? token = PageToken == "" ? null : PageToken;
        if (!PublicRobloxGameSearchLimits.SafeText(text, PublicRobloxGameSearchLimits.MaximumQueryCharacters,
                PublicRobloxGameSearchLimits.MaximumQueryUtf8Bytes)
            || !PublicRobloxGameSearchLimits.SafeToken(token) || DestinationOrSessionSyntax(text)) throw Invalid();
        return this with { Text = text, PageToken = token };
    }

    // Deliberately conservative classification shared by callers through Validated(). A rejected
    // value may be offered to the pure destination parser; it must never be retried as name search.
    private static bool DestinationOrSessionSyntax(string text)
    {
        if (text.IndexOfAny(['/', '\\', '?', '#', '%']) >= 0 || text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return true;
        // A server (job) ID: the destination parser explains that it needs its game.
        if (RobloxRequestedDestination.TryNormalizeJobId(text, out _)) return true;
        bool numeric = true, digit = false;
        foreach (char character in text)
        {
            if (char.IsDigit(character)) digit = true;
            else if (character is not '+' and not '-' and not '.' and not 'e' and not 'E') { numeric = false; break; }
        }
        if (numeric && digit) return true;
        int colon = text.IndexOf(':');
        if (colon > 0 && char.IsAsciiLetter(text[0]))
        {
            bool scheme = true;
            for (int index = 1; index < colon; index++)
                if (!char.IsAsciiLetterOrDigit(text[index]) && text[index] is not '+' and not '-' and not '.') { scheme = false; break; }
            if (scheme) return true;
        }
        if (!text.Any(char.IsWhiteSpace) && text.Contains('.')
            && text.All(character => char.IsLetterOrDigit(character) || character is '.' or '-')) return true;
        string compact = new(text.Where(character => !char.IsWhiteSpace(character)).ToArray());
        string[] markers = ["roblox.com", "ro.blox.com", "roblosecurity", "authenticationticket", "gameinfo=", "placelauncherurl",
            "privateserver", "reservedserver", "privatelink", "reservedlink", "sharelink", "sharecode", "privatecode", "reservedcode", "accesscode", "linkcode", "joincode",
            "code=", "code:", "share=", "share:", "reserved=", "private=", "cookie=", "cookie:", "placeid=", "gameinstanceid=", "jobid="];
        foreach (string marker in markers)
            if (compact.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static ArgumentException Invalid() => new("Enter a bounded game name; links, place IDs and session material use the destination parser.");
    public override string ToString() => "Public game-name query { Text = [redacted], SessionId = [ephemeral], PageToken = [redacted] }";
}

/// <summary>Public listing metadata. Creator type is unknown; Playing is not a server's available seats.</summary>
public sealed record PublicRobloxGameSearchResult(long UniverseId, long RootPlaceId, string Name,
    long? CreatorId = null, string? CreatorName = null, long? Playing = null, bool? Sponsored = null,
    bool? CreatorVerified = null, long? UpVotes = null, long? DownVotes = null)
{
    public PublicRobloxGameSearchResult Validated()
    {
        if (UniverseId <= 0 || RootPlaceId <= 0 || CreatorId is <= 0 || Playing is < 0 || UpVotes is < 0 || DownVotes is < 0
            || !PublicRobloxGameSearchLimits.SafeText(Name, PublicRobloxGameSearchLimits.MaximumNameCharacters, 1024)
            || CreatorName is not null && !PublicRobloxGameSearchLimits.SafeText(CreatorName,
                PublicRobloxGameSearchLimits.MaximumCreatorNameCharacters, 400))
            throw new ArgumentException("The public game-name result has invalid metadata.");
        return this;
    }
}

/// <summary>
/// One detached bounded page, correlated to exact normalized query/session/token. A page is never
/// exhaustive search coverage. ResultsTruncated means this response omitted otherwise valid rows;
/// its server continuation does not recover those omitted rows. Refine the name to avoid that loss.
/// </summary>
public sealed record PublicRobloxGameSearchPage(PublicRobloxGameSearchQuery Query,
    IReadOnlyList<PublicRobloxGameSearchResult> Items, string? NextPageToken, DateTimeOffset RetrievedUtc,
    bool ResultsTruncated = false)
{
    public PublicRobloxGameSearchPage Validated()
    {
        var query = Query?.Validated() ?? throw new ArgumentException("The search page has no query context.");
        string? next = NextPageToken == "" ? null : NextPageToken;
        if (Items is null || Items.Count is < 0 or > PublicRobloxGameSearchLimits.MaximumItems || RetrievedUtc == default
            || !PublicRobloxGameSearchLimits.SafeToken(next) || next is not null && next == query.PageToken)
            throw new ArgumentException("The public search page has invalid bounds or pagination context.");
        var owned = new PublicRobloxGameSearchResult[Items.Count];
        var universes = new HashSet<long>(); var places = new HashSet<long>();
        for (int index = 0; index < owned.Length; index++)
        {
            var item = Items[index]?.Validated() ?? throw new ArgumentException("The public search page contains an invalid result.");
            if (!universes.Add(item.UniverseId) || !places.Add(item.RootPlaceId))
                throw new ArgumentException("The public search page contains duplicate or conflicting game identities.");
            owned[index] = item;
        }
        return this with { Query = query, NextPageToken = next, Items = Array.AsReadOnly(owned) };
    }
    public override string ToString() => "Public game-name page { Query = [redacted], Results = "
        + (Items?.Count ?? 0).ToString(CultureInfo.InvariantCulture) + ", Pagination = [redacted] }";
}

/// <summary>Optional anonymous one-page lookup. No automatic selection, credentials, launch, cache or retries.</summary>
public interface IPublicRobloxGameSearchDirectory
{
    Task<PublicDirectoryResult<PublicRobloxGameSearchPage>> SearchGamesAsync(PublicRobloxGameSearchQuery query, CancellationToken ct);
}
