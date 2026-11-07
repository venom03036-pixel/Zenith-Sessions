using MacroSuite.SDK.AI;

namespace MacroSuite.Core.AI;

/// <summary>
/// Pluggable AI vision backend interface (Rule 9, Rule 16).
/// Implementations must not leak provider-specific SDK dependencies or error shapes.
/// </summary>
public interface IAIVisionProvider
{
    /// <summary>Canonical provider identifier (e.g., 'openai', 'anthropic', 'gemini').</summary>
    string ProviderId { get; }

    /// <summary>User-visible provider name.</summary>
    string DisplayName { get; }

    /// <summary>List of supported vision/multimodal model identifiers.</summary>
    IReadOnlyList<string> SupportedModels { get; }

    /// <summary>Default recommended model identifier for this provider.</summary>
    string DefaultModel { get; }

    /// <summary>
    /// Submits a vision analysis request using direct HTTP (Rule 16).
    /// Returns a normalized <see cref="AIResponse"/> (Rule 8).
    /// </summary>
    Task<AIResponse> AnalyzeAsync(
        AIRequest request,
        string apiKey,
        CancellationToken ct = default);

    /// <summary>
    /// Actively validates that an API key is authentic, authorized, and functional against the provider.
    /// Used for testing credentials in settings or pre-flight verification.
    /// </summary>
    Task<ApiKeyValidationResult> ValidateApiKeyAsync(
        string apiKey,
        string? modelId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Size and coordinate space for the JPEG this provider should receive.
    /// Pre-scale to this so the provider does not silently resize (Rule 9).
    /// </summary>
    VisionEncodePlan PlanEncode(int cropWidth, int cropHeight, string? modelId)
        => VisionEncodePlanner.Fit720p(cropWidth, cropHeight);
}
