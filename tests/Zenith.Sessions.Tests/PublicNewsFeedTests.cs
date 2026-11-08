using System.Net;
using System.Net.Http;
using System.Text;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class PublicNewsFeedTests
{
    private const string Rss = "<rss version='2.0'><channel><item><title>A &amp; B</title><link>https://devforum.roblox.com/t/example/123</link><description>&lt;p&gt;Hello &lt;b&gt;reader&lt;/b&gt;&lt;/p&gt;&lt;script&gt;secret-script&lt;/script&gt;</description><pubDate>Sun, 08 Nov 2026 12:00:00 GMT</pubDate></item></channel></rss>";

    [Fact]
    public async Task RssUsesOneAnonymousFixedRequestAndRendersOnlyPlainText()
    {
        using var handler = new Fake(_ => Reply(Rss)); using var http = new HttpClient(handler);
        var result = await new RobloxNewsFeed(http).ReadAsync(default);
        Assert.Equal(PublicNewsStatus.Available, result.Status);
        var article = Assert.Single(result.Items);
        Assert.Equal("A & B", article.Title); Assert.Equal("Hello reader", article.Summary);
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero), article.PublishedUtc);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AtomUsesTheAlternateHttpsLinkAndStripsXhtml()
    {
        const string body = "<feed xmlns='http://www.w3.org/2005/Atom'><entry><title>Update</title><link rel='self' href='https://invalid.example/self'/><link rel='alternate' href='https://devforum.roblox.com/t/updates/99'/><summary type='xhtml'><div xmlns='http://www.w3.org/1999/xhtml'>A <b>new</b> update</div></summary><updated>2026-11-08T12:00:00Z</updated></entry></feed>";
        using var http = new HttpClient(new Fake(_ => Reply(body, media: "application/atom+xml")));
        var result = await new RobloxNewsFeed(http).ReadAsync(default);
        Assert.Equal(PublicNewsStatus.Available, result.Status);
        Assert.Equal("A new update", Assert.Single(result.Items).Summary);
    }

    [Theory]
    [InlineData("http://devforum.roblox.com/t/example/123")]
    [InlineData("https://devforum.roblox.com.evil.example/t/example/123")]
    [InlineData("https://owner:secret@devforum.roblox.com/t/example/123")]
    [InlineData("https://devforum.roblox.com/t/example/123?redirect=evil")]
    [InlineData("file:///C:/Windows/example.txt")]
    public async Task UntrustedArticleAddressesAreNeverExposed(string address)
    {
        using var http = new HttpClient(new Fake(_ => Reply(Rss.Replace("https://devforum.roblox.com/t/example/123", address))));
        var result = await new RobloxNewsFeed(http).ReadAsync(default);
        Assert.Equal(PublicNewsStatus.Available, result.Status); Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("<!DOCTYPE rss [<!ENTITY stolen SYSTEM 'file:///C:/private'>]><rss><channel><item><title>&stolen;</title></item></channel></rss>")]
    [InlineData("<html><body>This is a page, not a feed.</body></html>")]
    [InlineData("<rss><channel>")]
    public async Task EntitiesHtmlAndMalformedXmlAreRefused(string body)
    {
        using var http = new HttpClient(new Fake(_ => Reply(body)));
        Assert.Equal(PublicNewsStatus.InvalidResponse, (await new RobloxNewsFeed(http).ReadAsync(default)).Status);
    }

    [Theory]
    [InlineData(302, PublicNewsStatus.Unavailable)]
    [InlineData(404, PublicNewsStatus.Unavailable)]
    [InlineData(429, PublicNewsStatus.RateLimited)]
    public async Task RefusalsReturnNoItemsAndNeverRetry(int code, PublicNewsStatus expected)
    {
        using var handler = new Fake(_ => Reply("remote-body-do-not-show", code)); using var http = new HttpClient(handler);
        var result = await new RobloxNewsFeed(http).ReadAsync(default);
        Assert.Equal(expected, result.Status); Assert.Empty(result.Items); Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("remote-body", result.Message);
    }

    [Fact]
    public async Task OversizedBodiesAndHtmlMediaTypeAreRejected()
    {
        using var huge = new HttpClient(new Fake(_ => Reply(new string('x', 262145))));
        Assert.Equal(PublicNewsStatus.InvalidResponse, (await new RobloxNewsFeed(huge).ReadAsync(default)).Status);
        using var html = new HttpClient(new Fake(_ => Reply(Rss, media: "text/html")));
        Assert.Equal(PublicNewsStatus.InvalidResponse, (await new RobloxNewsFeed(html).ReadAsync(default)).Status);
    }

    [Fact]
    public async Task ItemCountIsBoundedAndRepeatedLinksAreDeduplicated()
    {
        string item = Rss[(Rss.IndexOf("<item>", StringComparison.Ordinal))..Rss.IndexOf("</channel>", StringComparison.Ordinal)];
        string body = "<rss><channel>" + string.Concat(Enumerable.Range(1, 80).Select(i => item.Replace("/123", "/" + i))) + item + "</channel></rss>";
        using var http = new HttpClient(new Fake(_ => Reply(body)));
        var result = await new RobloxNewsFeed(http).ReadAsync(default);
        Assert.Equal(PublicNewsStatus.Available, result.Status); Assert.Equal(30, result.Items.Count);
        Assert.Equal(30, result.Items.Select(i => i.Link).Distinct().Count());
    }

    [Fact]
    public async Task CancellationSendsNothingAndCredentialBearingInjectedClientsAreRefused()
    {
        using var handler = new Fake(_ => Reply(Rss)); using var http = new HttpClient(handler);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RobloxNewsFeed(http).ReadAsync(cancelled.Token));
        http.DefaultRequestHeaders.Add("Cookie", "synthetic-never-send");
        await Assert.ThrowsAsync<ArgumentException>(() => new RobloxNewsFeed(http).ReadAsync(default));
        Assert.Equal(0, handler.Calls);
    }

    private static HttpResponseMessage Reply(string body, int status = 200, string media = "application/rss+xml")
        => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, media) };
    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://devforum.roblox.com/c/updates/announcements/36.rss", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("ZenithSessions/0.50", request.Headers.UserAgent.ToString());
            return Task.FromResult(reply(request));
        }
    }
}
