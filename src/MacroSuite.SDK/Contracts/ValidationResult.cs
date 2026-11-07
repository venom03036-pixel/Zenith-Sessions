namespace MacroSuite.SDK.Contracts;

/// <summary>
/// Represents the result of validating a plugin step's parameters before execution.
/// </summary>
public sealed class ValidationResult
{
    /// <summary>True if parameter validation passed without error.</summary>
    public bool IsValid { get; }

    /// <summary>Human-readable error explanation if validation failed.</summary>
    public string? ErrorMessage { get; }

    private ValidationResult(bool isValid, string? errorMessage)
    {
        IsValid = isValid;
        ErrorMessage = errorMessage;
    }

    /// <summary>Creates a successful validation result.</summary>
    public static ValidationResult Success() => new(true, null);

    /// <summary>Creates a failed validation result with an explanatory message.</summary>
    public static ValidationResult Failure(string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        return new(false, errorMessage);
    }
}
