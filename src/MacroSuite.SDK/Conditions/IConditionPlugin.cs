using MacroSuite.SDK.Contracts;

namespace MacroSuite.SDK.Conditions;

/// <summary>
/// Describes metadata for an extensible boolean rule condition.
/// </summary>
public sealed class ConditionDescriptor
{
    public string ConditionType { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// Implemented by plugins providing custom boolean condition evaluation.
/// </summary>
public interface IConditionPlugin : IPluginDescriptor
{
    /// <summary>Returns descriptor metadata for this condition type.</summary>
    ConditionDescriptor GetDescriptor();

    /// <summary>Evaluates whether the condition is satisfied.</summary>
    Task<bool> EvaluateAsync(IPluginExecutionContext context, CancellationToken ct);
}
