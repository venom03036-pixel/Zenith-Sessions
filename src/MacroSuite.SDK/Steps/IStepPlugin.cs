using MacroSuite.SDK.Contracts;

namespace MacroSuite.SDK.Steps;

/// <summary>
/// Implemented by plugins that provide custom execution steps.
/// </summary>
public interface IStepPlugin : IPluginDescriptor
{
    /// <summary>Returns descriptor metadata for this step type.</summary>
    StepDescriptor GetDescriptor();

    /// <summary>
    /// Executes the custom step action.
    /// Execution must respect <see cref="IPluginExecutionContext.CancellationToken"/> and terminate promptly on panic or stop.
    /// </summary>
    Task<StepResult> ExecuteAsync(IPluginExecutionContext context, CancellationToken ct);

    /// <summary>
    /// Validates authored step parameters prior to playback.
    /// </summary>
    ValidationResult Validate(IReadOnlyDictionary<string, object?> parameters);
}
