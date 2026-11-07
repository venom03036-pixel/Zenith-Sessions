namespace MacroSuite.Core.AI;

/// <summary>
/// Detailed diagnostic result returned when testing or verifying an AI provider API key.
/// </summary>
public sealed class ApiKeyValidationResult
{
    /// <summary>True if the API key authenticated and connected successfully.</summary>
    public bool IsValid { get; }

    /// <summary>Provider identifier tested (e.g., 'openai', 'anthropic', 'gemini').</summary>
    public string ProviderId { get; }

    /// <summary>Specific model verified during testing, if applicable.</summary>
    public string? ModelId { get; }

    /// <summary>Human-readable status or diagnostic error message.</summary>
    public string? Message { get; }

    /// <summary>Round-trip network latency measured during the test request.</summary>
    public TimeSpan Latency { get; }

    /// <summary>HTTP status code returned by the provider endpoint, if available.</summary>
    public int? HttpStatusCode { get; }

    /// <summary>Optional list of models discovered or verified as available for this key.</summary>
    public IReadOnlyList<string> AvailableModels { get; }

    private ApiKeyValidationResult(
        bool isValid,
        string providerId,
        string? modelId,
        string? message,
        TimeSpan latency,
        int? httpStatusCode,
        IReadOnlyList<string>? availableModels)
    {
        IsValid = isValid;
        ProviderId = providerId;
        ModelId = modelId;
        Message = message;
        Latency = latency;
        HttpStatusCode = httpStatusCode;
        AvailableModels = availableModels ?? Array.Empty<string>();
    }

    public static ApiKeyValidationResult Success(
        string providerId,
        string? modelId,
        TimeSpan latency,
        string message = "API key authenticated successfully.",
        IReadOnlyList<string>? availableModels = null)
    {
        return new ApiKeyValidationResult(true, providerId, modelId, message, latency, 200, availableModels);
    }

    public static ApiKeyValidationResult Failure(
        string providerId,
        string? modelId,
        string message,
        TimeSpan latency = default,
        int? httpStatusCode = null)
    {
        return new ApiKeyValidationResult(false, providerId, modelId, message, latency, httpStatusCode, null);
    }
}
