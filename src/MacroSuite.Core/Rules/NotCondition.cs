using MacroSuite.Core.Execution;

namespace MacroSuite.Core.Rules;

/// <summary>
/// Composite boolean condition: inverts the result of the child condition (logical NOT).
/// </summary>
public sealed class NotCondition : ICondition
{
    /// <summary>The inner condition to invert.</summary>
    public ICondition InnerCondition { get; }

    /// <inheritdoc/>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Description => $"NOT ({InnerCondition.Description})";

    /// <summary>
    /// Constructs a NOT condition.
    /// </summary>
    [System.Text.Json.Serialization.JsonConstructor]
    public NotCondition(ICondition innerCondition)
    {
        InnerCondition = innerCondition ?? throw new ArgumentNullException(nameof(innerCondition));
    }

    /// <inheritdoc/>
    public async Task<ConditionEvaluation> EvaluateDetailedAsync(MacroContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        if (context.IsAborted)
            throw new OperationCanceledException("Macro execution was aborted.", ct);

        ConditionEvaluation inner = await InnerCondition.EvaluateDetailedAsync(context, ct).ConfigureAwait(false);
        if (inner.IsFailed)
            return inner;

        return inner.IsSatisfied ? ConditionEvaluation.Unsatisfied : ConditionEvaluation.Satisfied;
    }
}
