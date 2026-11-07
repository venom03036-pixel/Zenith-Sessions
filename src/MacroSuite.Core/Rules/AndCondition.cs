using MacroSuite.Core.Execution;

namespace MacroSuite.Core.Rules;

/// <summary>
/// Composite boolean condition: returns true only if ALL child conditions evaluate to true (logical AND).
/// Short-circuits on the first false condition.
/// </summary>
public sealed class AndCondition : ICondition
{
    /// <summary>The child conditions to evaluate.</summary>
    public IReadOnlyList<ICondition> Conditions { get; }

    /// <inheritdoc/>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Description { get; }

    /// <summary>
    /// Constructs an AND composite condition.
    /// </summary>
    public AndCondition(params ICondition[] conditions)
        : this((IReadOnlyList<ICondition>)conditions)
    {
    }

    /// <summary>
    /// Constructs an AND composite condition.
    /// </summary>
    [System.Text.Json.Serialization.JsonConstructor]
    public AndCondition(IReadOnlyList<ICondition> conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        Conditions = conditions;
        Description = conditions.Count == 0
            ? "True"
            : $"({string.Join(" AND ", conditions.Select(c => c.Description))})";
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
            if (!result.IsSatisfied)
                return ConditionEvaluation.Unsatisfied;
        }

        return ConditionEvaluation.Satisfied;
    }
}
