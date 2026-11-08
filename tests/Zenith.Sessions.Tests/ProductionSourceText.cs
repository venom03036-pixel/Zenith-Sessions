using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Zenith.Sessions.Tests;

/// <summary>
/// Reads a production source embedded in this assembly, for wiring that only a live WebView2 would run. These checks
/// supplement the tests that run the extracted handler bodies: they show that the live handlers are exactly those calls
/// and are attached before the page opens. The text is compared without whitespace, line comments or string contents.
/// </summary>
internal static class ProductionSourceText
{
    internal static string Code(string resource)
    {
        using var stream = typeof(ProductionSourceText).Assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return Sanitize(reader.ReadToEnd());
    }

    /// <summary>Empties string literals, drops line comments, then removes all whitespace.</summary>
    internal static string Sanitize(string source)
    {
        string withoutStrings = Regex.Replace(source, @"""(?:\\.|[^""\\\n])*""", @"""""");
        return Compact(Regex.Replace(withoutStrings, @"//[^\n]*", ""));
    }

    /// <summary>The text between the braces of the first method or local function declared with <paramref name="signature"/>.</summary>
    internal static string HandlerBody(string code, string signature)
    {
        int start = code.IndexOf(Compact(signature), StringComparison.Ordinal);
        Assert.True(start > 0, $"No {signature} handler found.");
        int open = code.IndexOf('{', start), depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[(open + 1)..i];
        }
        throw new InvalidOperationException($"Unbalanced braces after {signature}");
    }

    /// <summary>Asserts that <paramref name="statement"/> runs on every path that reaches the first <paramref name="later"/>:
    /// it is a statement of its own, not the body of an if, else, loop or lambda, and no block it sits in closes before
    /// <paramref name="later"/>. A branch that skips both is allowed: then neither the guard nor the page runs.</summary>
    internal static void AssertRunsOnEveryPathTo(string code, string statement, string later, string message)
    {
        int at = code.IndexOf(Compact(statement), StringComparison.Ordinal), end = code.IndexOf(Compact(later), StringComparison.Ordinal);
        Assert.True(at > 0 && end > at, message);
        Assert.True(code[at - 1] is ';' or '{' or '}', message);
        int depth = 0;
        for (int i = at; i < end; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth < 0) Assert.Fail(message);
        }
    }

    internal static string Compact(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
}

public sealed class ProductionSourceTextTests
{
    // A guard the page can open without: in a branch, in an else, as an if's single statement, or inside a lambda.
    [Theory]
    [InlineData("void Run(){ if (ready) { core.Guard += Guard; } core.Navigate(\"https://x\"); }")]
    [InlineData("void Run(){ if (ready) { } else { core.Guard += Guard; } core.Navigate(\"https://x\"); }")]
    [InlineData("void Run(){ if (ready) core.Guard += Guard; core.Navigate(\"https://x\"); }")]
    [InlineData("void Run(){ Action a = () => { core.Guard += Guard; }; core.Navigate(\"https://x\"); }")]
    [InlineData("void Run(){ core.Navigate(\"https://x\"); core.Guard += Guard; }")]
    [InlineData("void Run(){ // core.Guard += Guard;\n core.Navigate(\"https://x\"); }")]
    public void GuardThePageCanOpenWithoutIsRejected(string source)
        => Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => ProductionSourceText.AssertRunsOnEveryPathTo(ProductionSourceText.Sanitize(source), "core.Guard += Guard;", "core.Navigate(", "synthetic"));

    // A guard every page-opening path runs: straight-line, or with a branch between that can only skip the page.
    [Theory]
    [InlineData("void Run(){ core.Guard += Guard; ready = true; core.Navigate(\"https://x\"); }")]
    [InlineData("void Run(){ core.Guard += Guard; if (expired) { throw new X(\"}\"); } core.Navigate(\"https://x\"); }")]
    [InlineData("void Run(){ if (ready) { core.Guard += Guard; core.Navigate(\"https://x\"); } }")]
    public void GuardEveryPageOpeningPathRunsIsAccepted(string source)
        => ProductionSourceText.AssertRunsOnEveryPathTo(ProductionSourceText.Sanitize(source), "core.Guard += Guard;", "core.Navigate(", "synthetic");
}
