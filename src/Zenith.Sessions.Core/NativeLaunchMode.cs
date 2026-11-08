namespace Zenith.Sessions.Core;

/// <summary>
/// How a native start gets its ticket-bearing roblox-player request. OfficialPage, the default, captures the official game
/// page's own request in a private browser when the user chooses Play. DirectTicket (R-api-7, owner decision of 2026-11-08)
/// asks Roblox for an authentication ticket with the account's saved sign-in and builds the request itself, with no page
/// and no click. Both requests go through the same grammar, admission and dispatch checks, and neither proves the
/// account or arrival; the direct mode is unverified until the live trial.
/// </summary>
public enum NativeLaunchMode { OfficialPage, DirectTicket }

/// <summary>The saved spelling and user-facing label of each mode. The saved value is optional within settings schema 1.</summary>
public static class NativeLaunchModes
{
    public const string OfficialPageValue = "official-page", DirectTicketValue = "direct-ticket";
    public static NativeLaunchMode Default => NativeLaunchMode.OfficialPage;

    public static string ToSettingsValue(NativeLaunchMode mode) => mode switch
    {
        NativeLaunchMode.OfficialPage => OfficialPageValue,
        NativeLaunchMode.DirectTicket => DirectTicketValue,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static string Label(NativeLaunchMode mode) => mode == NativeLaunchMode.DirectTicket ? "Direct ticket" : "Official page";

    /// <summary>Only the two named spellings parse (case and surrounding spaces ignored); anything else is refused, never guessed.</summary>
    public static bool TryParse(string? value, out NativeLaunchMode mode)
    {
        mode = Default;
        if (value is null) return false;
        ReadOnlySpan<char> text = value.AsSpan().Trim();
        if (text.Equals(OfficialPageValue, StringComparison.OrdinalIgnoreCase)) { mode = NativeLaunchMode.OfficialPage; return true; }
        if (text.Equals(DirectTicketValue, StringComparison.OrdinalIgnoreCase)) { mode = NativeLaunchMode.DirectTicket; return true; }
        return false;
    }
}
