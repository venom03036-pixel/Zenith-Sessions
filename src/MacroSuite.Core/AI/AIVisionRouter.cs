using System.Collections.Concurrent;
using MacroSuite.Core.Security;
using MacroSuite.SDK.AI;

namespace MacroSuite.Core.AI;

/// <summary>
/// Multi-provider coordinator allowing concurrent configuration of multiple AI providers
/// (OpenAI, Anthropic, Gemini, Custom) and flexible model selection per step or globally.
/// Provides API key verification and testing tools (Rule 9, Rule 10, Rule 15).
/// </summary>
public sealed class AIVisionRouter
{
    private readonly ISecretVault _secretVault;
    private readonly ConcurrentDictionary<string, IAIVisionProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Default provider used when an execution step does not explicitly specify one.</summary>
    public string DefaultProviderId { get; set; } = "openai";

    /// <summary>Optional default model override across providers.</summary>
    public string? DefaultModelId { get; set; }

    public AIVisionRouter(ISecretVault secretVault, IEnumerable<IAIVisionProvider>? initialProviders = null)
    {
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));

        if (initialProviders != null)
        {
            foreach (var p in initialProviders)
            {
                RegisterProvider(p);
            }
        }
    }

    /// <summary>Registers an AI vision provider.</summary>
    public void RegisterProvider(IAIVisionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers[provider.ProviderId] = provider;
    }

    /// <summary>Retrieves a registered provider by its identifier.</summary>
    public IAIVisionProvider? TryGetProvider(string providerId)
    {
        _providers.TryGetValue(providerId, out var provider);
        return provider;
    }

    /// <summary>Returns all registered providers.</summary>
    public IReadOnlyList<IAIVisionProvider> GetRegisteredProviders() => _providers.Values.ToList();

    /// <summary>Returns the list of providers that have an encrypted API key saved in the vault.</summary>
    public IReadOnlyList<string> GetConfiguredProviderIds()
    {
        var keys = _secretVault.ListSecretKeys();
        return _providers.Keys.Where(pid => keys.Contains(pid, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Actively tests and verifies an API key against the provider service.
    /// Can test either an in-memory candidate key (e.g. before saving in settings) or the currently vault-stored key.
    /// </summary>
    public async Task<ApiKeyValidationResult> TestApiKeyAsync(
        string providerId,
        string? candidateApiKey = null,
        string? modelId = null,
        CancellationToken ct = default)
    {
        var provider = TryGetProvider(providerId);
        if (provider is null)
        {
            return ApiKeyValidationResult.Failure(providerId, modelId, $"Provider '{providerId}' is not registered.");
        }

        string? keyToTest = candidateApiKey;
        if (string.IsNullOrWhiteSpace(keyToTest))
        {
            keyToTest = _secretVault.GetSecret(providerId);
        }

        if (string.IsNullOrWhiteSpace(keyToTest))
        {
            return ApiKeyValidationResult.Failure(providerId, modelId, $"No API key found for provider '{providerId}'. Please enter a key.");
        }

        string targetModel = modelId ?? provider.DefaultModel;
        return await provider.ValidateApiKeyAsync(keyToTest, targetModel, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Routes a vision analysis request to the specified or default provider and model.
    /// Safely pulls decrypted API keys from the vault without exposing them to caller logs.
    /// </summary>
    public async Task<AIResponse> AnalyzeAsync(
        AIRequest request,
        string? providerId = null,
        string? modelId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string effectiveProviderId;
        IAIVisionProvider provider;
        if (request.IsMacroDraft)
        {
            // Screen sharing in the authoring dialog is consent to the displayed provider.
            // Never silently send that snapshot to a different provider.
            effectiveProviderId = string.IsNullOrWhiteSpace(providerId) ? DefaultProviderId : providerId;
            provider = TryGetProvider(effectiveProviderId)
                ?? throw new InvalidOperationException($"AI provider '{effectiveProviderId}' is unavailable. Choose a configured provider in Settings.");
        }
        else provider = ResolveProvider(providerId, out effectiveProviderId);

        string? apiKey = _secretVault.GetSecret(effectiveProviderId);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(BuildMissingKeyMessage(effectiveProviderId));
        }

        string effectiveModel = ResolveModel(provider, modelId);

        string prompt = request.Prompt ?? string.Empty;
        if (request.ImageWidth > 0 && request.ImageHeight > 0)
        {
            prompt =
                $"{prompt}\n\nThe attached screenshot is {request.ImageWidth}x{request.ImageHeight} pixels.";
        }

        var customizedRequest = new AIRequest
        {
            IsMacroDraft = request.IsMacroDraft,
            Prompt = prompt,
            ImageBytes = request.ImageBytes,
            ImageWidth = request.ImageWidth,
            ImageHeight = request.ImageHeight,
            ModelId = effectiveModel,
            Parameters = request.Parameters
        };

        AIResponse response = await provider.AnalyzeAsync(customizedRequest, apiKey, ct).ConfigureAwait(false);
        return new AIResponse
        {
            Action = response.Action,
            MacroDraftJson = response.MacroDraftJson,
            ProviderId = effectiveProviderId,
            InputTokens = response.InputTokens,
            OutputTokens = response.OutputTokens,
            EstimatedCostUsd = response.EstimatedCostUsd,
            ProviderRequestId = response.ProviderRequestId,
            ModelId = response.ModelId ?? effectiveModel
        };
    }

    private IAIVisionProvider ResolveProvider(string? requestedProviderId, out string effectiveProviderId)
    {
        string preferred = !string.IsNullOrWhiteSpace(requestedProviderId) ? requestedProviderId : DefaultProviderId;
        if (TryGetUsableProvider(preferred, out var provider))
        {
            effectiveProviderId = preferred;
            return provider;
        }

        if (!string.Equals(DefaultProviderId, preferred, StringComparison.OrdinalIgnoreCase)
            && TryGetUsableProvider(DefaultProviderId, out provider))
        {
            effectiveProviderId = DefaultProviderId;
            return provider;
        }

        foreach (string configuredId in GetConfiguredProviderIds())
        {
            if (TryGetUsableProvider(configuredId, out provider))
            {
                effectiveProviderId = configuredId;
                return provider;
            }
        }

        if (TryGetProvider(preferred) is null)
        {
            throw new InvalidOperationException($"AI provider '{preferred}' is not registered or loaded.");
        }

        throw new InvalidOperationException(BuildMissingKeyMessage(preferred));
    }

    private bool TryGetUsableProvider(string providerId, out IAIVisionProvider provider)
    {
        IAIVisionProvider? found = TryGetProvider(providerId);
        if (found is null)
        {
            provider = null!;
            return false;
        }

        provider = found;
        return !string.IsNullOrWhiteSpace(_secretVault.GetSecret(providerId));
    }

    private string ResolveModel(IAIVisionProvider provider, string? modelId)
    {
        if (!string.IsNullOrWhiteSpace(modelId))
            return modelId;

        if (!string.IsNullOrWhiteSpace(DefaultModelId) && ModelIsSupported(provider, DefaultModelId))
            return DefaultModelId;

        return provider.DefaultModel;
    }

    private static bool ModelIsSupported(IAIVisionProvider provider, string modelId)
    {
        if (provider.SupportedModels.Count == 0)
            return true;

        foreach (string supported in provider.SupportedModels)
        {
            if (string.Equals(supported, modelId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private string BuildMissingKeyMessage(string providerId)
    {
        var configured = GetConfiguredProviderIds();
        if (configured.Count == 0)
        {
            return $"No API key configured for provider '{providerId}'. Configure an API key in Settings first.";
        }

        return $"No API key configured for provider '{providerId}'. A key is saved for: {string.Join(", ", configured)}. Select that provider in Settings or add a key for '{providerId}'.";
    }

    /// <summary>
    /// Asks the resolved provider how to size the screenshot before upload.
    /// Does not require an API key (planning is local).
    /// </summary>
    public VisionEncodePlan PlanEncode(int cropWidth, int cropHeight, string? providerId = null, string? modelId = null)
    {
        string preferred = !string.IsNullOrWhiteSpace(providerId) ? providerId : DefaultProviderId;
        IAIVisionProvider? provider = TryGetProvider(preferred);
        if (provider is null && !string.Equals(DefaultProviderId, preferred, StringComparison.OrdinalIgnoreCase))
            provider = TryGetProvider(DefaultProviderId);
        if (provider is null)
        {
            foreach (var candidate in _providers.Values)
            {
                provider = candidate;
                break;
            }
        }

        if (provider is null)
            return VisionEncodePlanner.Fit720p(cropWidth, cropHeight);

        string model = ResolveModel(provider, modelId);
        return provider.PlanEncode(cropWidth, cropHeight, model);
    }
}
