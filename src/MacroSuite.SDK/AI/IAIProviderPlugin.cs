using MacroSuite.SDK.Contracts;

namespace MacroSuite.SDK.AI;

/// <summary>
/// Describes metadata for an AI provider plugin.
/// </summary>
public sealed class AIProviderDescriptor
{
    public string ProviderId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public IReadOnlyList<string> SupportedModels { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Contract implemented by an initialized AI vision provider instance.
/// </summary>
public interface IAIPluginProvider
{
    /// <summary>
    /// Analyzes an image and prompt, producing a proposed normalized AI action.
    /// </summary>
    Task<AIResponse> AnalyzeAsync(AIRequest request, CancellationToken ct = default);
}

/// <summary>
/// Plugin contract for providing custom AI vision backends (Rule 9).
/// </summary>
public interface IAIProviderPlugin : IPluginDescriptor
{
    /// <summary>Returns descriptor metadata for this AI provider.</summary>
    AIProviderDescriptor GetProviderDescriptor();

    /// <summary>Factory method creating the provider instance.</summary>
    IAIPluginProvider CreateProvider(IPluginServices services);
}
