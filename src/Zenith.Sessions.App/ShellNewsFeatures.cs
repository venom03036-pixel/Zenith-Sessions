using System.Net.Http;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private HttpClient? _newsHttp;
    public PublicNewsViewModel News { get; private set; } = null!;
    public bool IsNews => Tab == "News";
    public Uri? ReviewNewsLink(PublicNewsItem? item) => !_shuttingDown && !IsLocked && !IsSample && IsNews
        && item is not null && News.Items.Contains(item) && PublicNewsLinks.IsAllowedArticle(item.Link) ? item.Link : null;
    private void InitializeNews(IPublicRobloxNewsFeed? feed)
    {
        if (feed is null) { _newsHttp = RobloxNewsFeed.CreateAnonymousHttpClient(); feed = new RobloxNewsFeed(_newsHttp); }
        News = new(feed, () => !_shuttingDown && !IsLocked && !IsSample, ex => Fail(ex, ManagerSubsystem.Directory));
    }
    private async ValueTask DisposeNewsAsync()
    {
        await News.DisposeAsync();
        _newsHttp?.Dispose();
    }
}
