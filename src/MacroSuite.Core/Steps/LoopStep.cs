using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;

namespace MacroSuite.Core.Steps;

/// <summary>
/// Executes a block of steps repeatedly, bounded by a maximum iteration count and optional while-condition (Rule 18).
/// Enforces safety bounds against infinite runaway loops.
/// </summary>
public sealed class LoopStep : MacroStep
{
    /// <summary>The maximum number of times the loop body may execute.</summary>
    public int MaxIterations { get; init; }

    /// <summary>Optional condition checked before each iteration; if false, loop terminates.</summary>
    public ICondition? WhileCondition { get; init; }

    /// <summary>The steps to execute inside each iteration.</summary>
    public IReadOnlyList<MacroStep> BodySteps { get; init; }

    /// <summary>Optional wall-clock cap for while-loops (0 = iterations only).</summary>
    public int MaxDurationMs { get; init; }

    /// <summary>Minimum delay between fresh condition checks for conditional loops.</summary>
    public int CheckIntervalMs { get; init; } = 50;

    /// <summary>
    /// When true, <see cref="WhileCondition"/> is a REPEAT UNTIL predicate:
    /// the body runs while the condition is false and stops when it becomes true.
    /// Repeat until present = run while absent; repeat until gone = run while present.
    /// </summary>
    public bool RepeatUntil { get; init; }

    /// <summary>Optional delay after each body iteration (milliseconds). Zero disables.</summary>
    public int MinBodyDelayMs { get; init; }
    public BlockRoutingOptions Routing { get; init; } = new();

    /// <summary>
    /// Constructs a counted loop step.
    /// </summary>
    [System.Text.Json.Serialization.JsonConstructor]
    public LoopStep(int maxIterations, IReadOnlyList<MacroStep> bodySteps, ICondition? whileCondition = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxIterations);
        MaxIterations = maxIterations;
        BodySteps = bodySteps ?? Array.Empty<MacroStep>();
        WhileCondition = whileCondition;
        Label = whileCondition != null
            ? $"While ({whileCondition.Description}) [max {maxIterations}]"
            : $"Repeat {maxIterations} times";
    }

    /// <inheritdoc/>
    public override async Task ExecuteAsync(MacroContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsEnabled || context.IsAborted || ct.IsCancellationRequested) return;
        int effectiveCap = Math.Min(MaxIterations, context.MaxIterations);
        long started = Environment.TickCount64;
        long lastConditionStarted = 0;
        int completedIterations = 0;
        RepeatingImageDispatcher.Validate(BodySteps);
        CancellationToken callerToken = ct;
        using var duration = MaxDurationMs > 0 ? new CancellationTokenSource(MaxDurationMs) : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, context.Control.StopToken, context.PanicToken, duration?.Token ?? CancellationToken.None);
        ct = linked.Token;
        using IDisposable loopScope = context.EnterLoop(StepId);
        using IDisposable blockScope = context.EnterBlock();
        try
        {
            for (int iteration = 0; iteration < effectiveCap; iteration++)
            {
                int retries = 0;
                while (true)
                {
                    ConditionWaitEvaluator.ThrowIfStopped(context, ct);
                    if (context.Control.IsPaused)
                        await context.Control.WaitIfPausedAsync(ct).ConfigureAwait(false);
                    ConditionWaitEvaluator.ThrowIfStopped(context, ct);
                    try
                    {
                        if (WhileCondition is not null)
                        {
                            int pollMs = Math.Clamp(CheckIntervalMs, 10, 10_000);
                            long delay = lastConditionStarted == 0 ? 0
                                : pollMs - (Environment.TickCount64 - lastConditionStarted);
                            while (delay > 0)
                            {
                                await Task.Delay((int)Math.Min(delay, 50), ct).ConfigureAwait(false);
                                ConditionWaitEvaluator.ThrowIfStopped(context, ct);
                                delay = pollMs - (Environment.TickCount64 - lastConditionStarted);
                            }
                            lastConditionStarted = Environment.TickCount64;
                            bool proceed = await EvaluateProceedAsync(context, started, ct).ConfigureAwait(false);
                            if (!proceed)
                            {
                                // A satisfied REPEAT UNTIL ends normally. A false WHILE uses its
                                // configured continuation; the default seeks the next conditional.
                                if (RepeatUntil) return;
                                if (await BlockRouting.ApplyAsync(this, Routing, Routing.OnNotSatisfied, retries++,
                                    context, ct, "WHILE condition was not satisfied.").ConfigureAwait(false)) continue;
                                return;
                            }
                        }
                        await MacroExecutor.ExecuteActionBlockAsync(BodySteps, 0, BodySteps.Count, context, ct)
                            .ConfigureAwait(false);
                        // Some actions finish cancellation without throwing. Still unwind the
                        // local duration limit before treating this iteration as completed.
                        ConditionWaitEvaluator.ThrowIfStopped(context, ct);
                        break;
                    }
                    catch (RecoverableBlockFailureException ex)
                    {
                        context.InputPlayer?.ReleaseAllHeldKeys();
                        if (await BlockRouting.ApplyAsync(this, Routing,
                            BlockRouting.ResolveFailureRoute(Routing, ex.IsBlockCondition ? WhileCondition : null), retries++, context, ct, ex.Message)
                            .ConfigureAwait(false)) continue;
                        return;
                    }
                }
                if (context.PendingNavigation is not null) return;
                ControlFlowSignal signal = context.ConsumeControlFlow();
                completedIterations++;
                if (signal != ControlFlowSignal.None)
                {
                    context.WriteTrace("ControlFlow", signal == ControlFlowSignal.Break
                        ? "BREAK exited the current loop." : "CONTINUE advanced to the next iteration.", actionId: StepId);
                    context.PublishStatus(new ExecutionStatusSnapshot
                    {
                        ActionId = StepId, Phase = "LoopControl", Message = signal.ToString()
                    });
                    if (signal == ControlFlowSignal.Break) return;
                }
                if (MinBodyDelayMs > 0)
                    await Task.Delay(Math.Clamp(MinBodyDelayMs, 0, 10_000), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (duration?.IsCancellationRequested == true
            && !callerToken.IsCancellationRequested && !context.Control.StopToken.IsCancellationRequested
            && !context.PanicToken.IsCancellationRequested)
        {
            // The duration limit can skip the matching key/button-up action in this
            // block. Release macro-owned input before continuing outside the loop.
            context.InputPlayer?.ReleaseAllHeldKeys();
            context.WriteTrace("ControlFlow", $"Loop reached its duration cap ({MaxDurationMs} ms).",
                ExecutionTraceLevel.Warning, StepId);
        }
        if (completedIterations >= effectiveCap && WhileCondition is not null)
            context.WriteTrace("ControlFlow", $"Loop reached maximum iterations ({effectiveCap}).",
                ExecutionTraceLevel.Warning, StepId);
    }

    private async Task<bool> EvaluateProceedAsync(MacroContext context, long started, CancellationToken ct)
    {
        TimeSpan remaining = MaxDurationMs > 0
            ? TimeSpan.FromMilliseconds(Math.Max(1, MaxDurationMs - (Environment.TickCount64 - started)))
            : TimeSpan.FromMilliseconds(WhileCondition is VisualPresenceCondition visual && visual.TimeoutMs > 0
                ? visual.TimeoutMs : 10_000);
        var policy = ConditionWaitPolicy.FromCondition(WhileCondition!, remaining,
            TimeSpan.FromMilliseconds(Math.Clamp(CheckIntervalMs, 10, 10_000)), 1, false,
            goal: ConditionWaitGoal.UntilEitherStable) with { Phase = RepeatUntil ? "RepeatUntil" : "While" };
        ConditionEvaluation evaluation = await ConditionWaitEvaluator.WaitDetailedAsync(
            context, WhileCondition!, policy, ct).ConfigureAwait(false);
        if (evaluation.IsFailed)
        {
            context.WriteTrace("ControlFlow", $"Condition evaluation failed in loop '{Label}'.",
                ExecutionTraceLevel.Error, StepId, exception: evaluation.Exception);
            if (BlockRouting.IsRecoverable(evaluation))
                throw new RecoverableBlockFailureException($"Loop '{Label}': {evaluation.ErrorCode}", evaluation.Exception, isBlockCondition: true);
            evaluation.ThrowIfFailed($"Loop '{Label}'");
        }
        bool matched = evaluation.IsSatisfied;
        bool proceed = RepeatUntil ? !matched : matched;
        context.WriteTrace("ControlFlow", $"Loop condition={matched}; {(proceed ? "executing body" : "exiting")}.",
            actionId: StepId, data: context.Variables.Get<object?>(MacroVariableNames.LastDetection));
        return proceed;
    }
}
