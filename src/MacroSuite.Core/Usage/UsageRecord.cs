using MacroSuite.Core.Licensing;

namespace MacroSuite.Core.Usage;

/// <summary>
/// Represents an immutable telemetry and cost record for an automated or AI-assisted operation.
/// STRICT RULE 10: Never contains API keys, tokens, credentials, or sensitive prompt secrets.
/// </summary>
public sealed class UsageRecord
{
    /// <summary>Unique identifier for the record.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>UTC timestamp when the operation was invoked or recorded.</summary>
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Identifier of the provider (e.g., 'openai', 'anthropic', 'gemini', 'local').</summary>
    public string ProviderId { get; init; } = string.Empty;

    /// <summary>Specific model or engine identifier (e.g., 'gpt-4o', 'gemini-1.5-pro').</summary>
    public string ModelId { get; init; } = string.Empty;

    /// <summary>Optional provider-assigned response or transaction identifier.</summary>
    public string? ProviderRequestId { get; init; }

    /// <summary>Count of prompt or input tokens consumed.</summary>
    public int InputTokens { get; init; }

    /// <summary>Count of output or completion tokens generated.</summary>
    public int OutputTokens { get; init; }

    /// <summary>Estimated or actual cost of the invocation in USD.</summary>
    public decimal EstimatedCostUsd { get; init; }

    /// <summary>The licensed feature associated with this usage.</summary>
    public FeatureId Feature { get; init; }

    /// <summary>Optional identifier of the executing macro.</summary>
    public Guid? MacroId { get; init; }

    /// <summary>Display string for the usage grid: input and output tokens from the billed request.</summary>
    public string TokenDisplay => $"{InputTokens} in / {OutputTokens} out";
}
