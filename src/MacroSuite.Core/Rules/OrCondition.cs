using MacroSuite.Core.Execution;

namespace MacroSuite.Core.Rules;

/// <summary>
/// Composite boolean condition: returns true if ANY child condition evaluates to true (logical OR).
/// Short-circuits on the first true condition.
/// </summary>
public sealed class OrCondition : ICondition
{
    /// <summary>The child conditions to evaluate.</summary>
    public IReadOnlyList<ICondition> Conditions { get; }

    /// <inheritdoc/>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Description { get; }

    /// <summary>
    /// Constructs an OR composite condition.
    /// </summary>
    public OrCondition(params ICondition[] conditions)
        : this((IReadOnlyList<ICondition>)conditions)
    {
    }

    /// <summary>
    /// Constructs an OR composite condition.
    /// </summary>
    [System.Text.Json.Serialization.JsonConstructor]
    public OrCondition(IReadOnlyList<ICondition> conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        Conditions = conditions;
        Description = conditions.Count == 0
            ? "False"
            : $"({string.Join(" OR ", conditions.Select(c => c.Description))})";
    }

    /// <inheritdoc/>
    public async Task<ConditionEvaluation> EvaluateDetailedAsync(MacroContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var condition in Conditions)
        {
            ct.ThrowIfCancellationRequested();
            if (context.IsAborted)
                throw new OperationCanceledException("Macro execution was aborted.", ct);

            ConditionEvaluation result = await condition.EvaluateDetailedAsync(context, ct).ConfigureAwait(false);
            if (result.IsFailed)
                return result;
            if (result.IsSatisfied)
                return ConditionEvaluation.Satisfied;
        }

        return ConditionEvaluation.Unsatisfied;
    }
}
