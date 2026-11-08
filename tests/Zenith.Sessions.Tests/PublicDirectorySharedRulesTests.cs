using System.IO;
using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// The public directory's batch rules each have one home, so a later fix to one rule cannot miss a copy of it.
public sealed class PublicDirectorySharedRulesTests
{
    // Each rule as it was once written inline in several parsers.
    [Theory]
    [InlineData("\"Pending\" or \"InReview\" => PublicRobloxThumbnailState.Pending")] // Thumbnail state: avatar batch and game pictures.
    [InlineData("char.IsAsciiLetterOrDigit(c) || c == '_'")] // Username characters: typed names, user-ID replies and username replies.
    [InlineData("!distinct.Add(id)")] // Distinct positive IDs: avatar batch, game pictures and game listings.
    public void EachPublicDirectoryRuleIsWrittenOnce(string rule)
    {
        string root = PowerShellScriptCompatibilityTests.RepositoryRoot;
        var files = Directory.GetFiles(Path.Combine(root, "src", "Zenith.Sessions.Windows"), "RobloxPublic*.cs")
            .Append(Path.Combine(root, "src", "Zenith.Sessions.Core", "PublicRobloxDirectory.cs"));
        string[] copies = files.SelectMany(file => File.ReadAllLines(file).Select((text, index) => (File: Path.GetFileName(file), Line: index + 1, Text: text)))
            .Where(line => line.Text.Contains(rule, StringComparison.Ordinal)).Select(line => line.File + ":" + line.Line).ToArray();
        Assert.True(copies.Length == 1, "The rule is written " + copies.Length + " times: " + string.Join(", ", copies));
    }

    [Theory]
    [InlineData("Roblox_1", true)]
    [InlineData("_", true)]
    [InlineData("ab", true)] // The length is the caller's rule, not part of the character check.
    [InlineData("Rob lox", false)]
    [InlineData("Rob-lox", false)]
    [InlineData("Rob.lox", false)]
    public void UsernameCharactersAreAsciiLettersDigitsOrUnderscores(string text, bool expected)
    {
        Assert.Equal(expected, PublicRobloxUsername.HasValidCharacters(text));
        // Typed names add only the 3 to 20 length rule on top of the shared character check.
        Assert.Equal(expected && text.Length >= PublicRobloxUsername.MinimumLength, PublicRobloxUsername.IsValid(text));
    }

    [Fact]
    public void NonAsciiLettersAndDigitsAreNotUsernameCharacters()
    {
        // Each of these is a letter or digit to char.IsLetterOrDigit, so only an ASCII-only rule refuses it.
        foreach (char c in new[] { (char)0xF6, (char)0x0661, (char)0xFF21 })
            Assert.False(PublicRobloxUsername.HasValidCharacters("Rob" + c), "U+" + ((int)c).ToString("X4"));
        Assert.Throws<ArgumentNullException>(() => PublicRobloxUsername.HasValidCharacters(null!));
    }
}
