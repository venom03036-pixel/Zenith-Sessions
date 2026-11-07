using Zenith.Sessions.Core;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class PublicGameSearchContractsTests
{
    private static readonly Guid Session = Guid.Parse("45e684cd-3008-4377-b439-f682d0cd8fee");
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void QueryTrimsOnlyOuterWhitespaceAndPreservesUnicodeAndOpaquePagination()
    {
        var query = new PublicRobloxGameSearchQuery("    Café & 🐟 + Friends    ", Session, "next+/=_-.").Validated();
        Assert.Equal("Café & 🐟 + Friends", query.Text); Assert.Equal(Session, query.SessionId);
        Assert.Equal("next+/=_-.", query.PageToken);
        Assert.Equal("é🐟", new PublicRobloxGameSearchQuery("Fisch", Session, "é🐟").Validated().PageToken);
        Assert.Equal("e", new PublicRobloxGameSearchQuery("e", Session).Validated().Text);
        Assert.Null(new PublicRobloxGameSearchQuery("Fisch", Session, "").Validated().PageToken);
        Assert.Equal(100, new PublicRobloxGameSearchQuery(new string('界', 100), Session).Validated().Text.Length);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("9223372036854775808")]
    [InlineData("https://www.roblox.com/games/123")]
    [InlineData("https://www.roblox.com/share?code=synthetic-private")]
    [InlineData("www.roblox.com/games/123")]
    [InlineData("example.com")]
    [InlineData("roblox://placeId=123")]
    [InlineData("roblox-player:1+gameinfo:synthetic-ticket")]
    [InlineData("file:///synthetic")]
    [InlineData("C:\\synthetic\\file")]
    [InlineData("/share")]
    [InlineData("https%3A%2F%2Froblox.com")]
    [InlineData("privateServerLinkCode=synthetic")]
    [InlineData("reserved server code = synthetic")]
    [InlineData("share link synthetic")]
    [InlineData("share code synthetic")]
    [InlineData(".ROBLOSECURITY=synthetic")]
    [InlineData("Cookie: synthetic")]
    public void DestinationOrSessionSyntaxCanNeverBecomeANameQuery(string text)
    {
        var query = new PublicRobloxGameSearchQuery(text, Session);
        var error = Assert.Throws<ArgumentException>(() => query.Validated());
        Assert.DoesNotContain("synthetic", error.Message);
        Assert.DoesNotContain(text, query.ToString());
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("long-name")]
    [InlineData("long-raw")]
    [InlineData("empty-session")]
    [InlineData("control")]
    [InlineData("format")]
    [InlineData("surrogate")]
    [InlineData("long-token")]
    [InlineData("token-byte-limit")]
    [InlineData("token-space")]
    [InlineData("token-malformed-unicode")]
    public void QueryRejectsInvalidBoundsWithoutEchoingInput(string kind)
    {
        string text = kind switch
        {
            "empty" => " ", "long-name" => new string('x', 101), "long-raw" => new string(' ', 4097),
            "control" => "Fisch\n", "format" => "Fi\u202esch", "surrogate" => "Fisch\ud800", _ => "Fisch"
        };
        string? token = kind switch
        {
            "long-token" => new string('a', 2049), "token-byte-limit" => new string('é', 1025),
            "token-space" => "synthetic token", "token-malformed-unicode" => "\ud800", _ => null
        };
        Assert.Throws<ArgumentException>(() => new PublicRobloxGameSearchQuery(text, kind == "empty-session" ? Guid.Empty : Session, token).Validated());
        Assert.DoesNotContain("synthetic token", new PublicRobloxGameSearchQuery(text, Session, token).ToString());
    }

    [Fact]
    public void ResultKeepsUnknownMetadataDistinctFromZeroAndAcceptsInt64Bounds()
    {
        var unknown = new PublicRobloxGameSearchResult(long.MaxValue, long.MaxValue - 1, "Unknown creator 🐟").Validated();
        Assert.Null(unknown.CreatorId); Assert.Null(unknown.CreatorName); Assert.Null(unknown.Playing); Assert.Null(unknown.Sponsored);
        var known = unknown with { CreatorId = long.MaxValue, CreatorName = "Public creator", Playing = 0, Sponsored = false };
        Assert.Equal(known, known.Validated());
        Assert.Equal(long.MaxValue, (known with { Playing = long.MaxValue }).Validated().Playing);
    }

    [Fact]
    public void PageDetachesRowsPreservesContextAndDoesNotExposeQueryOrCursorInToString()
    {
        var query = new PublicRobloxGameSearchQuery("Fisch", Session, "previous+/=");
        PublicRobloxGameSearchResult[] rows = [new(1, 2, "Public game")];
        var page = new PublicRobloxGameSearchPage(query, rows, "next+/=", Now, true).Validated();
        rows[0] = new(3, 4, "Replacement");
        Assert.Equal(1, Assert.Single(page.Items).UniverseId); Assert.Equal(query, page.Query);
        Assert.True(page.ResultsTruncated); Assert.Equal(Now, page.RetrievedUtc);
        var list = Assert.IsAssignableFrom<IList<PublicRobloxGameSearchResult>>(page.Items);
        Assert.True(list.IsReadOnly); Assert.Throws<NotSupportedException>(() => list[0] = rows[0]);
        Assert.DoesNotContain(query.Text, page.ToString()); Assert.DoesNotContain("next+/=", page.ToString());
    }

    [Theory]
    [InlineData("duplicate-universe")]
    [InlineData("duplicate-root")]
    [InlineData("too-many")]
    [InlineData("repeated-token")]
    [InlineData("bad-time")]
    [InlineData("bad-id")]
    [InlineData("bad-count")]
    [InlineData("bad-creator")]
    public void PageRejectsInvalidMetadataOrPagination(string kind)
    {
        var query = new PublicRobloxGameSearchQuery("Fisch", Session, "previous");
        PublicRobloxGameSearchResult[] rows = kind switch
        {
            "duplicate-universe" => [new(1, 2, "A"), new(1, 3, "B")],
            "duplicate-root" => [new(1, 2, "A"), new(3, 2, "B")],
            "too-many" => Enumerable.Range(1, 51).Select(id => new PublicRobloxGameSearchResult(id, id + 1000, "G")).ToArray(),
            "bad-id" => [new(0, 2, "G")], "bad-count" => [new(1, 2, "G", Playing: -1)],
            "bad-creator" => [new(1, 2, "G", CreatorId: 0)], _ => [new(1, 2, "G")]
        };
        Assert.Throws<ArgumentException>(() => new PublicRobloxGameSearchPage(query, rows,
            kind == "repeated-token" ? "previous" : null, kind == "bad-time" ? default : Now).Validated());
    }
}
