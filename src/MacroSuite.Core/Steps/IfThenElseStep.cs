using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;

namespace MacroSuite.Core.Steps;

/// <summary>
/// Conditional branching step: evaluates <see cref="Condition"/> and executes
/// either <see cref="ThenSteps"/> or <see cref="ElseSteps"/>.
/// </summary>
public sealed class IfThenElseStep : MacroStep
{
    /// <summary>The condition to evaluate.</summary>
    public ICondition Condition { get; init; }

    /// <summary>Steps executed when condition evaluates to true.</summary>
    public IReadOnlyList<MacroStep> ThenSteps { get; init; }

    /// <summary>Steps executed when condition evaluates to false.</summary>
    public IReadOnlyList<MacroStep> ElseSteps { get; init; }

    public BlockRoutingOptions Routing { get; init; } = new();

    /// <summary>
    /// Constructs an IfThenElseStep with then and optional else steps.
    /// </summary>
    [System.Text.Json.Serialization.JsonConstructor]
    public IfThenElseStep(
        ICondition condition,
        IReadOnlyList<MacroStep> thenSteps,
        IReadOnlyList<MacroStep>? elseSteps = null)
    {
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        ThenSteps = thenSteps ?? Array.Empty<MacroStep>();
        ElseSteps = elseSteps ?? Array.Empty<MacroStep>();
        Label = $"If ({condition.Description})";
    }

    /// <inheritdoc/>
    public override async Task ExecuteAsync(MacroContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!IsEnabled || context.IsAborted || ct.IsCancellationRequested)
        {
            return;
        }

        if (context.Control.IsPaused)
            await context.Control.WaitIfPausedAsync(ct).ConfigureAwait(false);

        using IDisposable blockScope = context.EnterBlock();
        int retries = 0;
        while (!context.IsAborted && !ct.IsCancellationRequested)
        {
            try
            {
                if (await ExecuteAttemptAsync(context, ct).ConfigureAwait(false)) return;
                if (!await BlockRouting.ApplyAsync(this, Routing, Routing.OnNotSatisfied, retries++,
                    context, ct, "IF condition was not satisfied and has no enabled ELSE branch.").ConfigureAwait(false)) return;
            }
            catch (RecoverableBlockFailureException ex)
            {
                // The failed attempt has fully unwound. Release only macro-owned input
                // before retrying or continuing so its abandoned tail cannot leave a hold.
                context.InputPlayer?.ReleaseAllHeldKeys();
                if (!await BlockRouting.ApplyAsync(this, Routing, BlockRouting.ResolveFailureRoute(Routing, ex.IsBlockCondition ? Condition : null), retries++,
                    context, ct, ex.Message).ConfigureAwait(false)) return;
            }
        }
    }

    private async Task<bool> ExecuteAttemptAsync(MacroContext context, CancellationToken ct)
    {
        var policy = ConditionWaitPolicy.FromCondition(
            Condition,
            timeout: TimeSpan.FromMilliseconds(
                Condition is VisualPresenceCondition vis && vis.TimeoutMs > 0
                    ? vis.TimeoutMs
                    : Condition is ColorCondition color && color.TimeoutMs > 0
                        ? color.TimeoutMs
                        : 10_000),
            pollInterval: TimeSpan.FromMilliseconds(
                Condition is VisualPresenceCondition vp
                    ? ConditionReliability.ClampPollIntervalMs(vp.PollIntervalMs)
                    : Condition is ColorCondition cc
                        ? ConditionReliability.ClampPollIntervalMs(cc.PollIntervalMs)
                        : ConditionReliability.DefaultPollIntervalMs),
            requiredTrueConfirmations: 1,
            invert: false,
            goal: ConditionWaitGoal.UntilEitherStable);

        ConditionEvaluation evaluation = await ConditionWaitEvaluator.WaitDetailedAsync(
            context,
            Condition,
            policy,
            ct).ConfigureAwait(false);
        if (evaluation.IsFailed)
        {
            context.WriteTrace(
                "ControlFlow",
                $"IF condition evaluation failed in '{Label}'.",
                ExecutionTraceLevel.Error,
                StepId,
                exception: evaluation.Exception);
            if (BlockRouting.IsRecoverable(evaluation))
                throw new RecoverableBlockFailureException($"IF '{Label}': {evaluation.ErrorCode}", evaluation.Exception, isBlockCondition: true);
            evaluation.ThrowIfFailed($"IF '{Label}'");
        }

        bool satisfied = evaluation.IsSatisfied;

        if (!satisfied && !ElseSteps.Any(step => step.IsEnabled)) return false;

        var targetSteps = satisfied ? ThenSteps : ElseSteps;
        RepeatingImageDispatcher.Validate(targetSteps);
        context.WriteTrace(
            "ControlFlow",
            $"IF condition={satisfied}; entering {(satisfied ? "THEN" : "ELSE")} branch.",
            actionId: StepId,
            data: context.Variables.Get<object?>(MacroVariableNames.LastDetection));
        context.PublishStatus(new ExecutionStatusSnapshot
        {
            ActionId = StepId,
            Phase = "BranchSelected",
            Message = satisfied ? "THEN" : "ELSE",
            Detection = context.LastColorDetection
        });

        await MacroExecutor.ExecuteActionBlockAsync(targetSteps, 0, targetSteps.Count, context, ct).ConfigureAwait(false);
        return true;
    }
}
