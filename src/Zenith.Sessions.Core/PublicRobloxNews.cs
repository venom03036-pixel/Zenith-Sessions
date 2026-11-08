namespace Zenith.Sessions.Core;

public enum PublicNewsStatus { Available, Unavailable, RateLimited, InvalidResponse }
public sealed record PublicNewsItem(string Title, string Summary, Uri Link, DateTimeOffset? PublishedUtc);
public sealed record PublicNewsResult(PublicNewsStatus Status, IReadOnlyList<PublicNewsItem> Items, string Message);
public interface IPublicRobloxNewsFeed
{
    Task<PublicNewsResult> ReadAsync(CancellationToken ct);
}

public static class PublicNewsLinks
{
    public static readonly Uri Newsroom = new("https://about.roblox.com/newsroom");
    public static bool IsAllowedArticle(Uri? uri) => uri is { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true }
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && ((uri.Host == "devforum.roblox.com" && uri.AbsolutePath.StartsWith("/t/", StringComparison.Ordinal))
            || (uri.Host == "about.roblox.com" && uri.AbsolutePath.StartsWith("/newsroom/", StringComparison.Ordinal)));
    public static bool CanOpen(Uri? uri) => uri == Newsroom || IsAllowedArticle(uri);
}
