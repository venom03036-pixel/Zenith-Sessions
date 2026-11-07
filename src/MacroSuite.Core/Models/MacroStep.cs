using MacroSuite.Core.Execution;

namespace MacroSuite.Core.Models;

/// <summary>
/// Abstract base class for all executable macro steps.
/// Serialized polymorphically with a $type discriminator property via <see cref="Storage.MacroJsonSerializer"/>.
/// </summary>
public abstract class MacroStep
{
    /// <summary>Unique identifier for this step.</summary>
    public Guid StepId { get; init; } = Guid.NewGuid();

    /// <summary>User-provided or auto-generated label describing the step.</summary>
    public string? Label { get; init; }

    /// <summary>Whether this step is enabled for execution.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// Executes this step within the provided <see cref="MacroContext"/>.
    /// Implementations must check <see cref="MacroContext.IsAborted"/> and honor <paramref name="ct"/> (Rule 11, Rule 12).
    /// </summary>
    public abstract Task ExecuteAsync(MacroContext context, CancellationToken ct = default);
}
