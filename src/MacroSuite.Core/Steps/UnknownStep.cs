using System.Text.Json;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Steps;

/// <summary>
/// Passthrough placeholder for unrecognized or third-party step types (Rule 18).
/// Preserves the raw type discriminator and attributes so data is not lost on round-trip serialization.
/// </summary>
public sealed class UnknownStep : MacroStep
{
    /// <summary>Original $type discriminator from the JSON document.</summary>
    public string RawType { get; init; } = string.Empty;

    /// <summary>Unrecognized payload data preserved as JSON elements.</summary>
    public Dictionary<string, JsonElement> RawData { get; init; } = new();

    public UnknownStep()
    {
        Label = "Unknown Step";
    }

    public UnknownStep(string rawType, Dictionary<string, JsonElement>? rawData = null)
    {
        RawType = rawType ?? string.Empty;
        RawData = rawData ?? new();
        Label = $"Unknown Step ({rawType})";
    }

    /// <inheritdoc/>
    public override Task ExecuteAsync(MacroContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        throw new InvalidOperationException(
            $"No executor is registered for action type '{RawType}'. The action was preserved but cannot be executed.");
    }
}
