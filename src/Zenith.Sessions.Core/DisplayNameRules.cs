using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Zenith.Sessions.Core;

/// <summary>
/// The one rule for names given to favorites, recipient bundles, macro assignments and saved private servers: a short
/// label on one line, never a place to keep a link, an access code or a credential. Each type keeps its own length
/// limit and message.
/// </summary>
internal static class DisplayNameRules
{
    // Every one of the four validators refused these before they shared this rule, so no saved name contains one.
    private static readonly string[] SavedNameMarkers = ["://", ".ROBLOSECURITY", "Bearer ", "privateServerLinkCode=", "reservedServerAccessCode=",
        "accessCode=", "linkCode=", "refresh_token", "access_token", "client_secret", "authorization="];
    // Only some of them refused these. A new or changed name may not contain one; a name saved earlier may, and it must
    // stay readable, because every store re-checks names on load and treats one bad record as a damaged file.
    private static readonly string[] NewNameMarkers = ["www.", "roblox:", "roblox-player:", "cookie=", "code="];

    /// <summary>True when <paramref name="name"/> may be saved now, as a new name or a changed one.</summary>
    internal static bool IsPlain([NotNullWhen(true)] string? name, int maximumLength) => IsReadable(name, maximumLength) && !ContainsAny(name, NewNameMarkers);

    /// <summary>True when an already saved name is still acceptable. Weaker than <see cref="IsPlain"/> only by the markers
    /// that some types did not check when the name was saved.</summary>
    internal static bool IsReadable([NotNullWhen(true)] string? name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > maximumLength) return false;
        for (ReadOnlySpan<char> rest = name; !rest.IsEmpty; )
        {
            // A lone surrogate cannot be written as UTF-8 text, so it never belongs in a saved name.
            if (Rune.DecodeFromUtf16(rest, out Rune rune, out int used) != OperationStatus.Done) return false;
            if (Rune.IsControl(rune) || rune.Value is 0x2028 or 0x2029) return false; // Controls and line or paragraph separators.
            rest = rest[used..];
        }
        return !ContainsAny(name, SavedNameMarkers);
    }

    private static bool ContainsAny(string name, string[] markers)
    {
        foreach (string marker in markers) if (name.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
