namespace MacroSuite.SDK.Steps;

/// <summary>
/// Describes a parameter accepted by a plugin step.
/// </summary>
public sealed class StepParameterDescriptor
{
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public Type ParameterType { get; init; } = typeof(string);
    public bool IsRequired { get; init; }
    public object? DefaultValue { get; init; }
}

/// <summary>
/// Describes metadata, visual grouping, and parameter schema for an extensible macro step.
/// </summary>
public sealed class StepDescriptor
{
    /// <summary>Identifier for this step within the plugin.</summary>
    public string StepType { get; init; } = string.Empty;

    /// <summary>Human-readable name displayed in block editors.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Visual group or category in the editor palette.</summary>
    public string Category { get; init; } = "General";

    /// <summary>Explanation of step behavior.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>List of supported parameters.</summary>
    public IReadOnlyList<StepParameterDescriptor> Parameters { get; init; } = Array.Empty<StepParameterDescriptor>();
}
