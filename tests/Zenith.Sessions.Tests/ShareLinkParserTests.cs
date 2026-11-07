using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// Pure parsing of roblox.com/share links. The code is a secret: it lives in a protected holder and never in text.
public sealed class ShareLinkParserTests
{
    private const string Code = "4b9c7e0ae7b2f84bb7a7d24f6d0f6c2e";

    [Theory]
    [InlineData("https://www.roblox.com/share?code=" + Code + "&type=Server", RobloxShareLinkType.Server, Code)]
    [InlineData("https://roblox.com/share?code=" + Code + "&type=ExperienceDetails", RobloxShareLinkType.ExperienceDetails, Code)]
    [InlineData("  https://www.roblox.com/share/?type=Server&code=abc123  ", RobloxShareLinkType.Server, "abc123")]
    public void ParsesExperienceAndServerShareLinks(string input, RobloxShareLinkType type, string expectedCode)
    {
        Assert.True(RobloxShareLinkParser.TryParse(input, out var link)); using var owned = link!;
        Assert.Equal(RobloxDestinationKind.UnresolvedShareLink, RobloxDestinationParser.Parse(input).Kind);
        Assert.Equal(type, owned.Type); Assert.Equal(expectedCode.Length, owned.Code.Length);
        string revealed = ""; owned.Code.Reveal(code => revealed = new string(code));
        Assert.Equal(expectedCode, revealed);
    }

    // Friend invites can name the inviter's server; v2 links carry no type and use the deeplinks/v2 resolver.
    [Theory]
    [InlineData("https://www.roblox.com/share?code=" + Code + "&type=ExperienceInvite", RobloxShareLinkType.ExperienceInvite)]
    [InlineData("https://www.roblox.com/share?code=" + Code + "&type=NotificationExperienceInvite", RobloxShareLinkType.NotificationExperienceInvite)]
    [InlineData("https://www.roblox.com/share?code=" + Code + "&v=v2", RobloxShareLinkType.DeepLinkV2)]
    [InlineData("https://roblox.com/share/?v=V2&code=" + Code, RobloxShareLinkType.DeepLinkV2)]
    public void ParsesInviteAndVersion2ShareLinks(string input, RobloxShareLinkType type)
    {
        Assert.True(RobloxShareLinkParser.TryParse(input, out var link)); using var owned = link!;
        Assert.Equal(RobloxDestinationKind.UnresolvedShareLink, RobloxDestinationParser.Parse(input).Kind);
        Assert.Equal(type, owned.Type);
        string revealed = ""; owned.Code.Reveal(code => revealed = new string(code));
        Assert.Equal(Code, revealed); Assert.DoesNotContain(Code, owned.ToString(), StringComparison.Ordinal);
    }

    // The share parser accepts the same web hosts and one language prefix as the destination parser that classifies it.
    [Theory]
    [InlineData("https://m.roblox.com/share?code=" + Code + "&type=Server")]
    [InlineData("https://web.roblox.com/share?code=" + Code + "&type=Server")]
    [InlineData("https://www.roblox.com/de/share?code=" + Code + "&type=Server")]
    [InlineData("https://roblox.com/pt-br/share/?type=Server&code=" + Code)]
    public void AcceptsTheDestinationParsersWebHostsAndLanguagePrefix(string input)
    {
        Assert.Equal(RobloxDestinationKind.UnresolvedShareLink, RobloxDestinationParser.Parse(input).Kind);
        Assert.True(RobloxShareLinkParser.TryParse(input, out var link)); using var owned = link!;
        Assert.Equal(RobloxShareLinkType.Server, owned.Type);
        string revealed = ""; owned.Code.Reveal(code => revealed = new string(code));
        Assert.Equal(Code, revealed);
    }

    [Theory]
    [InlineData("https://www.roblox.com/share?code=abc&v=v3")]
    [InlineData("https://www.roblox.com/share?code=abc&v=")]
    [InlineData("https://www.roblox.com/share?v=v2")]
    [InlineData("https://www.roblox.com/share?code=abc&v=v2&type=Server")]
    [InlineData("https://www.roblox.com/share?code=abc&v=v2&extra=1")]
    [InlineData("https://www.roblox.com/share?code=abc&v=v2&v=v2")]
    public void RejectsMalformedVersion2Links(string input)
    {
        Assert.False(RobloxShareLinkParser.TryParse(input, out var link)); Assert.Null(link);
    }

    [Fact]
    public void UnknownTypeIsOtherAndMissingCodeOrTypeFails()
    {
        Assert.True(RobloxShareLinkParser.TryParse("https://www.roblox.com/share?code=abc&type=Profile", out var other));
        using (other) Assert.Equal(RobloxShareLinkType.Other, other!.Type);
        Assert.False(RobloxShareLinkParser.TryParse("https://www.roblox.com/share?code=abc", out var noType)); Assert.Null(noType);
        Assert.False(RobloxShareLinkParser.TryParse("https://www.roblox.com/share?type=Server", out var noCode)); Assert.Null(noCode);
        Assert.False(RobloxShareLinkParser.TryParse("https://www.roblox.com/share?code=&type=Server", out var empty)); Assert.Null(empty);
    }

    [Theory]
    [InlineData("http://www.roblox.com/share?code=abc&type=Server")]
    [InlineData("https://www.roblox.com.evil.invalid/share?code=abc&type=Server")]
    [InlineData("https://evil.invalid/share?code=abc&type=Server")]
    [InlineData("https://ro.blox.com/Ebh5?af_dp=roblox%3A%2F%2Fnavigation%2Fshare_links")]
    [InlineData("https://www.roblox.com/share?code=abc&type=Server&extra=1")]
    [InlineData("https://www.roblox.com/share?code=abc&code=def&type=Server")]
    [InlineData("https://www.roblox.com/share?code=abc%20def&type=Server")]
    [InlineData("https://www.roblox.com/share?code=abc.def&type=Server")]
    [InlineData("https://www.roblox.com/games/123?code=abc&type=Server")]
    [InlineData("https://user@www.roblox.com/share?code=abc&type=Server")]
    [InlineData("https://www.roblox.com:8443/share?code=abc&type=Server")]
    [InlineData("https://www.roblox.com/share?code=abc&type=Server#fragment")]
    [InlineData("https://www.roblox.com/share?code=abc&type=Server extra")]
    [InlineData("roblox://share?code=abc&type=Server")]
    [InlineData("https://www.roblox.com/deu/share?code=abc&type=Server")]
    [InlineData("https://www.roblox.com/de/fr/share?code=abc&type=Server")]
    [InlineData("https://m.roblox.com.evil.invalid/share?code=abc&type=Server")]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsOtherHostsHttpExtraParametersAndBadCodes(string input)
    {
        Assert.False(RobloxShareLinkParser.TryParse(input, out var link)); Assert.Null(link);
    }

    // One definition of a safe roblox.com link: the share parser accepts only what the destination parser already
    // classifies as a share link (path, escape and query hardening included), so the two cannot drift apart.
    [Theory]
    [InlineData("https://www.roblox.com/x/../share?code=abc&type=Server")]
    [InlineData("https://www.roblox.com/share?code=abc&type=Server%00")]
    public void ShareParserRejectsWhatTheDestinationParserRejects(string input)
    {
        Assert.NotEqual(RobloxDestinationKind.UnresolvedShareLink, RobloxDestinationParser.Parse(input).Kind);
        Assert.False(RobloxShareLinkParser.TryParse(input, out var link)); Assert.Null(link);
    }

    [Fact]
    public void RejectsOverlongCodesAndInputs()
    {
        Assert.False(RobloxShareLinkParser.TryParse("https://www.roblox.com/share?code=" + new string('a', 65) + "&type=Server", out var longCode)); Assert.Null(longCode);
        Assert.True(RobloxShareLinkParser.TryParse("https://www.roblox.com/share?code=" + new string('a', 64) + "&type=Server", out var maxCode)); maxCode!.Dispose();
        Assert.False(RobloxShareLinkParser.TryParse(new string('x', 4097), out var huge)); Assert.Null(huge);
    }

    [Fact]
    public void CodeNeverAppearsInToStringOrAfterDispose()
    {
        Assert.True(RobloxShareLinkParser.TryParse("https://www.roblox.com/share?code=secretcode123&type=Server", out var link));
        Assert.DoesNotContain("secretcode123", link!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secretcode123", link.Code.ToString(), StringComparison.Ordinal);
        Assert.Equal(13, link.Code.Length);
        link.Dispose(); Assert.Throws<ObjectDisposedException>(() => link.Code.Reveal(_ => { })); Assert.Equal(0, link.Code.Length);
        link.Dispose(); // Idempotent.
    }

    [Fact]
    public void PrivateServerCodeExposesOnlyItsLastFourCharacters()
    {
        using var code = new ProtectedPrivateServerCode("98765432109876543210");
        Assert.Equal("3210", code.Last4); Assert.Equal(20, code.Length);
        Assert.DoesNotContain("9876543210987", code.ToString(), StringComparison.Ordinal);
        string revealed = ""; code.Reveal(span => revealed = new string(span)); Assert.Equal("98765432109876543210", revealed);
        Assert.Throws<ArgumentException>(() => new ProtectedPrivateServerCode("abc"));
        Assert.Throws<ArgumentException>(() => new ProtectedPrivateServerCode(new string('1', 129)));
        Assert.Throws<ArgumentException>(() => new ProtectedPrivateServerCode("1234 5678"));
    }

    [Fact]
    public void ExistingDestinationParserStillDiscardsShareCodes()
    {
        var parsed = RobloxDestinationParser.Parse("https://www.roblox.com/share?code=secretcode123&type=Server");
        Assert.Equal(RobloxDestinationStatus.Unresolved, parsed.Status); Assert.Equal(RobloxDestinationKind.UnresolvedShareLink, parsed.Kind);
        Assert.Null(parsed.RequestedDestination);
        Assert.DoesNotContain("secretcode123", parsed.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secretcode123", parsed.Explanation, StringComparison.Ordinal);
    }
}
