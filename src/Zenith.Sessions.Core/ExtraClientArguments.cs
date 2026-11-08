namespace Zenith.Sessions.Core;

/// <summary>
/// Extra command-line arguments a user adds to Zenith's own direct Roblox player start (R-launch-16, owner decision of
/// 2026-11-08). They are typed in Settings (the default for every account) or in Edit account profile (one account's own
/// set, which replaces the default for that account), kept in settings.json and the account library, validated here on
/// every save and again before a start, and appended after Roblox's own launch request only by the direct player start
/// (<c>RobloxPlayerBeta.exe</c>), never through a bootstrapper, where percent expansion is a risk. They are never written
/// to logs, diagnostics, exports or ToString. Off until the user enters something.
/// </summary>
public static class ExtraClientArgumentPolicy
{
    public const int MaximumCharacters = 512, MaximumArguments = 16;
    private const string AllowedPunctuation = "-_.:/=+,@#~[]{}()";
    private const string Allowed = "letters, digits and - _ . : / = + , @ # ~ [ ] { } ( )";

    /// <summary>
    /// Splits <paramref name="text"/> into arguments on spaces and checks every rule. Null, empty and whitespace-only text
    /// is valid and means no extra arguments. <paramref name="problem"/> is plain, specific wording for the user; it never
    /// repeats the text.
    /// </summary>
    public static bool TryParse(string? text, out IReadOnlyList<string> arguments, out string problem)
    {
        arguments = Array.Empty<string>(); problem = "";
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (text.Length > MaximumCharacters) { problem = $"Extra client arguments must be at most {MaximumCharacters} characters."; return false; }
        foreach (char value in text)
        {
            if (value == ' ' || IsAllowed(value)) continue;
            problem = Refusal(value); return false;
        }
        string[] tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > MaximumArguments) { problem = $"Extra client arguments must be at most {MaximumArguments} arguments separated by spaces."; return false; }
        foreach (string token in tokens)
        {
            if (IsLaunchRequest(token)) { problem = "Extra client arguments cannot contain a launch request (roblox-player: or roblox:); Zenith always passes Roblox's own request first."; return false; }
            if (LooksLikeASecret(token)) { problem = "Do not enter sign-in cookies or tokens in extra client arguments."; return false; }
        }
        arguments = Array.AsReadOnly(tokens); return true;
    }

    /// <summary>The canonical saved form: the arguments joined by single spaces, or empty. Throws <see cref="ArgumentException"/> with the problem.</summary>
    public static string Validate(string? text)
    {
        if (!TryParse(text, out var arguments, out string problem)) throw new ArgumentException(problem);
        return string.Join(' ', arguments);
    }

    /// <summary>
    /// One argument as it would reach the client: never empty, no spaces, only allowed characters, not a launch request
    /// and not a cookie or token. The direct player start checks each argument with this again right before the start.
    /// </summary>
    public static bool IsValidArgument(string? argument)
    {
        if (argument is not { Length: > 0 and <= MaximumCharacters }) return false;
        foreach (char value in argument) if (!IsAllowed(value)) return false;
        return !IsLaunchRequest(argument) && !LooksLikeASecret(argument);
    }

    /// <summary>
    /// The arguments one account's direct start receives: the account's own saved set when it has one, else the saved
    /// default; empty when neither is set. Both texts are re-validated, so a tampered saved value refuses the start.
    /// </summary>
    public static IReadOnlyList<string> Effective(string? accountArguments, string? defaultArguments)
    {
        if (!TryParse(accountArguments, out var own, out string problem)) throw new ArgumentException(problem);
        if (own.Count > 0) return own;
        if (!TryParse(defaultArguments, out var fallback, out problem)) throw new ArgumentException(problem);
        return fallback;
    }

    /// <summary>Plain wording for a review or status line: which set applies and its arguments, or "none".</summary>
    public static string Describe(string? accountArguments, string? defaultArguments)
    {
        if (TryParse(accountArguments, out var own, out _) && own.Count > 0) return "Extra client arguments (this account): " + string.Join(' ', own);
        if (TryParse(defaultArguments, out var fallback, out _) && fallback.Count > 0) return "Extra client arguments (default): " + string.Join(' ', fallback);
        return "Extra client arguments: none";
    }

    private static bool IsAllowed(char value) => char.IsAsciiLetterOrDigit(value) || AllowedPunctuation.Contains(value);
    private static bool IsLaunchRequest(string token)
        => token.StartsWith("roblox-player:", StringComparison.OrdinalIgnoreCase) || token.StartsWith("roblox:", StringComparison.OrdinalIgnoreCase);
    private static bool LooksLikeASecret(string token)
        => token.Contains("ROBLOSECURITY", StringComparison.OrdinalIgnoreCase) || token.Contains("DO-NOT-SHARE", StringComparison.OrdinalIgnoreCase);
    private static string Refusal(char value) => value switch
    {
        '"' or '\'' => "Quotes are not allowed in extra client arguments.",
        '%' => "Percent signs are not allowed in extra client arguments.",
        '<' or '>' or '|' or '&' => "Redirection and pipe characters (< > | &) are not allowed in extra client arguments.",
        '\\' or '^' or '`' => "Backslashes and the escape characters ^ and ` are not allowed in extra client arguments.",
        _ when char.IsControl(value) || value > '~' => $"Extra client arguments must be plain text on one line, using {Allowed}; line breaks, tabs and other characters are not allowed.",
        _ => $"Extra client arguments may use only {Allowed}."
    };
}

/// <summary>
/// Reads the extra client arguments one account's direct start receives, from saved state at preparation time (never
/// from an unsaved draft). The App implements it over the account library and the saved default; a native provider
/// without one passes no extra arguments. A failure or an invalid saved value refuses that start.
/// </summary>
public interface IExtraClientArgumentsSource
{
    Task<IReadOnlyList<string>> ResolveAsync(Guid profileId, CancellationToken ct);
}
