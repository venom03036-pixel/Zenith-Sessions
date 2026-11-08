using System.Text.RegularExpressions;
using Zenith.Sessions.App;

namespace Zenith.Sessions.Tests;

/// <summary>
/// Reads the production XAML's texts as the window shows them in English: a <c>{local:Text Key}</c> attribute is the catalog's
/// English text for that key, any other attribute value is itself. Tests that check what the Guide or a control says use this,
/// so moving a text into the catalog does not change what they read.
/// </summary>
internal static class ProductionWindowStrings
{
    private static readonly Regex Reference = new(@"^\{local:Text (?<key>[A-Za-z0-9_]+)\}$", RegexOptions.CultureInvariant);
    public static string Resolve(string? attribute) =>
        attribute is not null && Reference.Match(attribute) is { Success: true } match ? ShellStrings.English[match.Groups["key"].Value] : attribute ?? "";
    /// <summary>The key a <c>{local:Text Key}</c> attribute names, or null for any other value.</summary>
    public static string? Key(string? attribute) => attribute is not null && Reference.Match(attribute) is { Success: true } match ? match.Groups["key"].Value : null;
}
