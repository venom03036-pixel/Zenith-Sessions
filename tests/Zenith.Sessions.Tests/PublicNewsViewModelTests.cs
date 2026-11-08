using System.IO;
using System.Windows.Threading;
using System.Xml.Linq;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class PublicNewsViewModelTests
{
    private static PublicNewsResult Result => new(PublicNewsStatus.Available,
        [new("Example", "Plain summary", new("https://devforum.roblox.com/t/example/123"), null)], "Loaded");

    [Fact]
    public async Task ReadsOnlyWhenOpenedOrExplicitlyRefreshedAndDoesNotPoll()
    {
        var feed = new Fake(_ => Task.FromResult(Result));
        await using var vm = new PublicNewsViewModel(feed, () => true, ex => throw ex);
        Assert.Equal(0, feed.Calls);
        vm.Refresh.Execute(null); await vm.Refresh.Execution; Assert.Equal(0, feed.Calls);
        vm.SetActive(true); await vm.Refresh.Execution; Assert.Equal(1, feed.Calls); Assert.Single(vm.Items);
        vm.SetActive(true); Assert.Equal(1, feed.Calls);
        vm.Refresh.Execute(null); await vm.Refresh.Execution; Assert.Equal(2, feed.Calls);
        vm.SetActive(false); Assert.Empty(vm.Items); Assert.False(vm.CanRefresh);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NavigationOrStopCancelsAndSuppressesLateResponses(bool stop)
    {
        var answer = new TaskCompletionSource<PublicNewsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        var feed = new Fake(ct => { observed = ct; return answer.Task; });
        await using var vm = new PublicNewsViewModel(feed, () => true, ex => throw ex);
        vm.SetActive(true); Assert.False(vm.CanRefresh);
        if (stop) vm.Stop(); else vm.SetActive(false);
        Assert.True(observed.IsCancellationRequested);
        answer.SetResult(Result); await vm.Refresh.Execution;
        Assert.Empty(vm.Items); Assert.Contains("stopped", vm.Status);
        Assert.Equal(1, feed.Calls);
    }

    [Fact]
    public async Task UnavailableSampleOrLockedStateNeverReadsAndDisposeJoinsTheActualOwner()
    {
        bool available = false;
        var answer = new TaskCompletionSource<PublicNewsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var feed = new Fake(_ => answer.Task);
        var vm = new PublicNewsViewModel(feed, () => available, ex => throw ex);
        vm.SetActive(true); await vm.Refresh.Execution; Assert.Equal(0, feed.Calls);
        available = true; vm.Refresh.Execute(null); Assert.Equal(1, feed.Calls);
        var disposal = vm.DisposeAsync().AsTask(); Assert.False(disposal.IsCompleted);
        var repeated = vm.DisposeAsync().AsTask(); Assert.Same(disposal, repeated); Assert.False(repeated.IsCompleted);
        answer.SetResult(Result); await disposal;
        Assert.Empty(vm.Items); Assert.False(vm.CanRefresh);
        vm.Refresh.Execute(null); await vm.Refresh.Execution; Assert.Equal(1, feed.Calls);
    }

    private sealed class Fake(Func<CancellationToken, Task<PublicNewsResult>> read) : IPublicRobloxNewsFeed
    {
        public int Calls { get; private set; }
        public Task<PublicNewsResult> ReadAsync(CancellationToken ct) { Calls++; return read(ct); }
    }

    [Fact]
    public Task ShellNavigationPanicLockAndShutdownOwnTheNewsRequest() => PackagedLayoutCheck.RunOnStaAsync(
        () => new object(), async _ =>
        {
            string root = Path.Combine(Path.GetTempPath(), "zenith-news-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var feed = new Fake(_ => Task.FromResult(Result));
                await using var shell = new ShellViewModel(root, false, Dispatcher.CurrentDispatcher, new Monitor(), newsFeed: feed);
                await shell.InitializeAsync(); Assert.Equal(0, feed.Calls);
                shell.Navigate.Execute("News"); await shell.News.Refresh.Execution;
                Assert.True(shell.IsNews); Assert.Equal("News", shell.PageName); Assert.Equal(1, feed.Calls);
                var item = Assert.Single(shell.News.Items); Assert.Equal(item.Link, shell.ReviewNewsLink(item));
                Assert.Null(shell.ReviewNewsLink(item with { Link = new("https://invalid.example/evil") }));
                shell.Panic.Execute(null); Assert.Empty(shell.News.Items); Assert.Null(shell.ReviewNewsLink(item));
                shell.News.Refresh.Execute(null); await shell.News.Refresh.Execution; Assert.Equal(2, feed.Calls);
                shell.Lock.Execute(null); Assert.Empty(shell.News.Items);
                shell.News.Refresh.Execute(null); await shell.News.Refresh.Execution; Assert.Equal(2, feed.Calls);
            }
            finally
            {
                Assert.Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(root));
                Assert.StartsWith("zenith-news-test-", Path.GetFileName(root));
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }, _ => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

    [Fact]
    public void NewsControlsUseThePageOwnedCommandsAndPlainTextBindings()
    {
        using var stream = typeof(PublicNewsViewModelTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        var doc = XDocument.Load(stream); XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var page = Assert.Single(doc.Descendants().Where(e => (string?)e.Attribute(x + "Name") == "NewsPage"));
        Assert.Equal("{Binding IsNews, Converter={StaticResource VisibleIf}}", (string?)page.Attribute("Visibility"));
        Assert.Single(page.Descendants().Where(e => (string?)e.Attribute("Command") == "{Binding News.Refresh}"));
        Assert.Single(page.Descendants().Where(e => (string?)e.Attribute("ItemsSource") == "{Binding News.Items}"));
        Assert.Single(page.Descendants().Where(e => (string?)e.Attribute("Click") == "OpenNewsArticleClick"));
        Assert.Contains(page.Descendants(), e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == "{Binding Summary}");
        Assert.DoesNotContain(page.Descendants(), e => e.Name.LocalName is "WebBrowser" or "WebView2" or "Hyperlink");
    }

    private sealed class Monitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct) => Task.FromResult(new DesktopSnapshot(
            new(DateTimeOffset.UtcNow, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 64 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB, 20), []));
        public Task<bool> FocusAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct) => Task.FromResult(false);
    }
}
