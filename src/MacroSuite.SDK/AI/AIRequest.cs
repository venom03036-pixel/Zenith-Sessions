namespace MacroSuite.SDK.AI;

/// <summary>
/// Execution request passed to an AI vision provider.
/// </summary>
public sealed class AIRequest
{
    /// <summary>Draft authoring returns a reviewable macro document, never an executable live action.</summary>
    public bool IsMacroDraft { get; init; }
    /// <summary>Instructional prompt describing what the AI should locate or decide.</summary>
    public string Prompt { get; init; } = string.Empty;

    /// <summary>Raw image bytes (JPEG or PNG encoded) of the screen or region.</summary>
    public byte[]? ImageBytes { get; init; }

    /// <summary>Pixel width of the captured frame.</summary>
    public int ImageWidth { get; init; }

    /// <summary>Pixel height of the captured frame.</summary>
    public int ImageHeight { get; init; }

    /// <summary>Optional specific model identifier requested.</summary>
    public string? ModelId { get; init; }

    /// <summary>Additional provider options or parameters.</summary>
    public Dictionary<string, string> Parameters { get; init; } = new();
}

/// <summary>
/// Normalized response from an AI provider containing the proposed action and usage stats.
/// </summary>
public sealed class AIResponse
{
    /// <summary>Untrusted structured draft, present only for an explicit macro-authoring request.</summary>
    public string? MacroDraftJson { get; init; }

    /// <summary>Provider that actually served the request after routing.</summary>
    public string? ProviderId { get; init; }
    /// <summary>The proposed action returned by the model.</summary>
    public AIAction Action { get; init; } = new();

    /// <summary>Count of prompt tokens consumed.</summary>
    public int InputTokens { get; init; }

    /// <summary>Count of completion tokens generated.</summary>
    public int OutputTokens { get; init; }

    /// <summary>Estimated cost in USD.</summary>
    public decimal EstimatedCostUsd { get; init; }

    /// <summary>Optional request or transaction identifier from the upstream provider.</summary>
    public string? ProviderRequestId { get; init; }

    /// <summary>Model identifier actually used for the request after provider aliases/fallbacks.</summary>
    public string? ModelId { get; init; }
}
