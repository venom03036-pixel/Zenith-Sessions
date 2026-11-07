using MacroSuite.Core.Execution;
using MacroSuite.Core.Steps;
using MacroSuite.SDK.Contracts;

namespace MacroSuite.Core.Extensibility;

/// <summary>
/// Host orchestration boundary between the macro execution engine and third-party plugins.
/// </summary>
public interface IPluginHost
{
    /// <summary>
    /// Dispatches execution of a <see cref="PluginStep"/> to its registered <see cref="SDK.Steps.IStepPlugin"/>.
    /// Enforces licensing, parameter validation, and exception isolation (Rule 18).
    /// </summary>
    Task<StepResult> ExecutePluginStepAsync(PluginStep step, MacroContext context, CancellationToken ct = default);
}
