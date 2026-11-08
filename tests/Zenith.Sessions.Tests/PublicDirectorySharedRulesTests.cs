using System.IO;
using System.Text.RegularExpressions;
using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// The public directory's shared rules each have one home, so a later fix to one rule cannot miss a copy of it.
public sealed class PublicDirectorySharedRulesTests
{
    // Each rule as a pattern rather than one exact fragment, so the usual rewrites still count as copies: other
    // variable or set names, ==, !=, is or is not, either operand order, extra spaces and line breaks. A text check
    // cannot see every possible spelling, so a copy written some other way still needs a reviewer to spot it.
    private static readonly Dictionary<string, Regex> Rules = new()
    {
        // Roblox's raw thumbnail state names. One map turns them into states for every thumbnail reply.
        ["thumbnail states"] = new(@"""InReview""", RegexOptions.CultureInvariant),
        // ASCII letters, digits and '_' with no other character allowed beside them.
        ["username characters"] = new(
            @"Is(?:Ascii)?LetterOrDigit\(\s*(?<v>\w+)\s*\)\s*(?:\|\||&&)\s*\k<v>\s*(?:==|!=|is\s+not|is)\s*'_'(?!\s*(?:or\b|and\b|\|\||&&)\s*(?:\(?\s*\k<v>\b|'))"
            + @"|(?<!(?:\|\||&&|\bor|\band)\s*)\b(?<v>\w+)\s*(?:==|!=|is\s+not|is)\s*'_'\s*(?:\|\||&&)\s*!?\s*(?:char\.)?Is(?:Ascii)?LetterOrDigit\(\s*\k<v>\s*\)",
            RegexOptions.CultureInvariant),
        // Each ID positive and listed once.
        ["distinct positive IDs"] = new(
            @"\b(?<v>\w+)\s*(?:is\s*)?(?:<=\s*0|<\s*1)(?!\d)\s*\|\|\s*!\s*\w+\.Add\(\s*\k<v>\s*\)"
            + @"|!\s*\w+\.Add\(\s*(?<v>\w+)\s*\)\s*\|\|\s*\k<v>\s*(?:is\s*)?(?:<=\s*0|<\s*1)(?!\d)",
            RegexOptions.CultureInvariant),
    };

    // Code that matches a pattern but is a different rule, found by its file and a fragment of its line.
    private static readonly (string Rule, string File, string Line, string Reason)[] OtherRules =
    [
        ("username characters", "Zenith.Sessions.Core/RobloxDestinations.cs", "key.Any(", "query keys in a Roblox link"),
        ("username characters", "Zenith.Sessions.Windows/WindowsGpuTelemetryReader.cs", "in value)", "GPU engine type names"),
        ("distinct positive IDs", "Zenith.Sessions.Windows/RobloxPublicGameGallery.cs", "parsed <= 0", "image IDs inside Roblox's media reply, not a caller's batch"),
    ];

    [Theory]
    [InlineData("thumbnail states")] // Avatar headshots, avatar batches, game icons, game pictures and gallery pictures.
    [InlineData("username characters")] // Typed names, Roblox's name replies, saved profiles and signed-in identities.
    [InlineData("distinct positive IDs")] // Avatar batches, game pictures and game listings.
    public void EachPublicDirectoryRuleIsWrittenOnce(string rule)
    {
        // Every Zenith Sessions source project, not only the public-directory parsers: profiles and sign-in code check usernames too.
        string src = Path.Combine(PowerShellScriptCompatibilityTests.RepositoryRoot, "src");
        var matches = Directory.GetDirectories(src, "Zenith.Sessions.*")
            .SelectMany(project => Directory.GetFiles(project, "*.cs", SearchOption.AllDirectories))
            .Select(file => Path.GetRelativePath(src, file).Replace('\\', '/'))
            .Where(file => !file.Split('/').Any(part => part is "obj" or "bin"))
            .SelectMany(file => Find(Rules[rule], src, file)).ToArray();
        var others = OtherRules.Where(other => other.Rule == rule).ToArray();
        foreach (var other in others)
            Assert.True(matches.Count(match => Is(other, match)) == 1,
                "The listed different rule in " + other.File + " (" + other.Reason + ") no longer matches once. Update the list.");
        string[] copies = matches.Where(match => !others.Any(other => Is(other, match))).Select(match => match.File + ":" + match.Line).ToArray();
        Assert.True(copies.Length == 1, "The rule is written " + copies.Length + " times: " + string.Join(", ", copies));
    }

    [Theory]
    [InlineData("username characters", "name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')", true)]
    [InlineData("username characters", "if (!(char.IsAsciiLetterOrDigit(ch) || ch is '_')) return false;", true)]
    [InlineData("username characters", "c is not '_' && !char.IsAsciiLetterOrDigit(c)", true)]
    [InlineData("username characters", "char.IsLetterOrDigit(c)\r\n    || c == '_'", true)]
    [InlineData("username characters", "char.IsAsciiLetterOrDigit(c) || c is '_' or '-'", false)] // Also allows '-', so another rule.
    [InlineData("username characters", "char.IsAsciiLetterOrDigit(c) || c == '_' || c == '.'", false)]
    [InlineData("username characters", "!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')", false)]
    [InlineData("distinct positive IDs", "if (id <= 0 || !seen.Add(id)) return null;", true)]
    [InlineData("distinct positive IDs", "if (!unique.Add(universe) || universe < 1) return null;", true)]
    [InlineData("distinct positive IDs", "if (row.Revision <= 0 || !ids.Add(row.Id)) throw Invalid();", false)] // Checks one value, adds another.
    [InlineData("thumbnail states", "if (state is \"InReview\" or \"Pending\")", true)]
    public void RulePatternsCatchTheUsualRewrites(string rule, string code, bool expected) =>
        Assert.Equal(expected, Rules[rule].IsMatch(code));

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

    private static bool Is((string Rule, string File, string Line, string Reason) other, (string File, int Line, string Text) match) =>
        match.File == other.File && match.Text.Contains(other.Line, StringComparison.Ordinal);

    private static IEnumerable<(string File, int Line, string Text)> Find(Regex rule, string src, string file)
    {
        string text = File.ReadAllText(Path.Combine(src, file));
        foreach (Match match in rule.Matches(text))
        {
            int start = text.LastIndexOf('\n', Math.Max(match.Index - 1, 0)) + 1, end = text.IndexOf('\n', match.Index);
            int line = 1 + text.AsSpan(0, match.Index).Count('\n');
            yield return (file, line, text[start..(end < 0 ? text.Length : end)]);
        }
    }
}
