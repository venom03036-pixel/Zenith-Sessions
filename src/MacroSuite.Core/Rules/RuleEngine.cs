namespace MacroSuite.Core.Rules;

/// <summary>
/// Default implementation of <see cref="IRuleEngine"/>.
/// Evaluates conditions with bounded polling intervals and explicit cancellation checks (Rule 12, Rule 14).
/// </summary>
public sealed class RuleEngine : IRuleEngine
{
    private static readonly TimeSpan s_defaultPollInterval = TimeSpan.FromMilliseconds(ConditionReliability.DefaultPollIntervalMs);

    /// <inheritdoc/>
    public async Task<bool> WaitUntilAsync(
        ICondition condition,
        Execution.MacroContext context,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default,
        int requiredConfirmations = 1,
        bool invert = false,
        int requiredFalseConfirmations = 1,
        int stabilityDurationMs = 0)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(context);

        var policy = ConditionWaitPolicy.FromCondition(
            condition,
            timeout,
            pollInterval ?? s_defaultPollInterval,
            requiredConfirmations,
            invert,
            requiredFalseConfirmations,
            stabilityDurationMs);

        ConditionEvaluation result = await ConditionWaitEvaluator.WaitDetailedAsync(
            context,
            condition,
            policy,
            ct).ConfigureAwait(false);
        result.ThrowIfFailed("WaitUntil");
        return result.IsSatisfied;
    }
}
