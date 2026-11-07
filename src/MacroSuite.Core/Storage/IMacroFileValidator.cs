namespace MacroSuite.Core.Storage;

/// <summary>
/// Validates the content of a macro JSON document for security threats.
/// Implementations must be stateless and safe to call from multiple threads concurrently.
/// </summary>
/// <remarks>
/// Per Rule 8 (AI output is untrusted) and the general principle that downloaded files
/// are untrusted data, all macro files loaded from external sources must pass through
/// a validator before deserialization.
/// </remarks>
public interface IMacroFileValidator
{
    /// <summary>
    /// Validates a raw macro JSON string for malicious or malformed content.
    /// </summary>
    /// <param name="json">The raw JSON text to validate. Must not be null or empty.</param>
    /// <param name="sourceTag">A human-readable label for the source (e.g. file name) used in violation messages.</param>
    /// <returns>
    /// A <see cref="MacroValidationResult"/> indicating whether the document is safe
    /// and listing any violations found.
    /// </returns>
    MacroValidationResult Validate(string json, string sourceTag);
}

/// <summary>
/// The result of a <see cref="IMacroFileValidator.Validate"/> call.
/// </summary>
/// <param name="IsValid">
/// <c>true</c> if no blocking violations were found and the macro may be safely deserialized.
/// </param>
/// <param name="Violations">
/// Human-readable descriptions of every problem detected. Empty when <see cref="IsValid"/> is <c>true</c>.
/// </param>
public sealed record MacroValidationResult(bool IsValid, IReadOnlyList<string> Violations)
{
    /// <summary>Convenience singleton for a clean (no violations) result.</summary>
    public static readonly MacroValidationResult Clean =
        new(true, Array.Empty<string>());
}
