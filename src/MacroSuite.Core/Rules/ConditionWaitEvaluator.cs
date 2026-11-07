using System.Diagnostics;
using MacroSuite.Core.Execution;

namespace MacroSuite.Core.Rules;

/// <summary>
/// Shared wait policy for Wait Until, Wait While, Visual Wait, IF, and loops.
/// Confirmations, absence confirmations, and stability windows are enforced here.
/// </summary>
public readonly record struct ConditionWaitPolicy
{
    public TimeSpan Timeout { get; init; }
    public TimeSpan PollInterval { get; init; }
    public int RequiredTrueConfirmations { get; init; }
    public int RequiredFalseConfirmations { get; init; }
    public int StabilityDurationMs { get; init; }
    public bool Invert { get; init; }
    public string Phase { get; init; }
    public ConditionWaitGoal Goal { get; init; }

    public ConditionWaitPolicy()
    {
        Phase = "";
    }

    public int ConfirmationsNeeded => Invert
        ? ConditionReliability.ClampConfirmations(RequiredFalseConfirmations)
        : ConditionReliability.ClampConfirmations(RequiredTrueConfirmations);

    public static ConditionWaitPolicy FromCondition(
        ICondition condition,
        TimeSpan timeout,
        TimeSpan pollInterval,
        int requiredTrueConfirmations,
        bool invert,
        int requiredFalseConfirmations = ConditionReliability.DefaultConfirmations,
        int stabilityDurationMs = 0,
        ConditionWaitGoal goal = ConditionWaitGoal.UntilSatisfied)
    {
        ArgumentNullException.ThrowIfNull(condition);

        int trueCount = ConditionReliability.ClampConfirmations(requiredTrueConfirmations);
        int falseCount = ConditionReliability.ClampConfirmations(requiredFalseConfirmations);
        int stability = ConditionReliability.ClampStabilityMs(stabilityDurationMs);
        TimeSpan poll = pollInterval;
        TimeSpan cap = timeout;

        switch (condition)
        {
            case ColorCondition color:
                trueCount = Math.Max(trueCount, color.RequiredConfirmations);
                falseCount = Math.Max(falseCount, color.RequiredFalseConfirmations);
                stability = Math.Max(stability, color.StabilityDurationMs);
                if (color.TimeoutMs > 0 && color.TimeoutMs < cap.TotalMilliseconds)
                    cap = TimeSpan.FromMilliseconds(color.TimeoutMs);
                break;
            case VisualPresenceCondition presence:
                trueCount = Math.Max(trueCount, presence.RequiredConfirmations);
                falseCount = Math.Max(falseCount, presence.RequiredFalseConfirmations);
                stability = Math.Max(stability, presence.StabilityDurationMs);
                if (presence.TimeoutMs > 0 && presence.TimeoutMs < cap.TotalMilliseconds)
                    cap = TimeSpan.FromMilliseconds(presence.TimeoutMs);
                break;
        }

        return new ConditionWaitPolicy
        {
            Timeout = cap,
            PollInterval = poll,
            RequiredTrueConfirmations = trueCount,
            RequiredFalseConfirmations = falseCount,
            StabilityDurationMs = stability,
            Invert = invert,
            Phase = invert ? "WaitWhile" : "WaitUntil",
            Goal = goal
        };
    }
}

public enum ConditionWaitGoal
{
    /// <summary>Poll until stably satisfied, otherwise timeout as unsatisfied.</summary>
    UntilSatisfied = 0,

    /// <summary>
    /// Poll until either side is stably confirmed. Timeout without a stable
    /// result is indeterminate (failed), not ELSE / not-found.
    /// </summary>
    UntilEitherStable = 1
}

/// <summary>
/// Single poll loop for waits, IF, and loops. Failures and cancellation never
/// become business true/false.
/// </summary>
public static class ConditionWaitEvaluator
{
    private static readonly TimeSpan s_minPollInterval = TimeSpan.FromMilliseconds(ConditionReliability.MinPollIntervalMs);

    public static async Task<bool> WaitAsync(
        MacroContext context,
        Func<MacroContext, CancellationToken, Task<bool>> evaluate,
        ConditionWaitPolicy policy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evaluate);
        ConditionEvaluation result = await WaitDetailedAsync(
            context,
            async (ctx, token) =>
            {
                bool value = await evaluate(ctx, token).ConfigureAwait(false);
                return value ? ConditionEvaluation.Satisfied : ConditionEvaluation.Unsatisfied;
            },
            policy,
            ct).ConfigureAwait(false);
        result.ThrowIfFailed(policy.Phase);
        return result.IsSatisfied;
    }

    public static async Task<ConditionEvaluation> WaitDetailedAsync(
        MacroContext context,
        ICondition condition,
        ConditionWaitPolicy policy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (condition is ImageCondition { ScanOptions: not null } image)
            return await image.EvaluateConfiguredWaitAsync(context, policy, ct).ConfigureAwait(false);
        return await WaitDetailedAsync(
            context,
            (ctx, token) => condition.EvaluateDetailedAsync(ctx, token),
            policy,
            ct).ConfigureAwait(false);
    }

    public static async Task<ConditionEvaluation> WaitDetailedAsync(
        MacroContext context,
        Func<MacroContext, CancellationToken, Task<ConditionEvaluation>> evaluate,
        ConditionWaitPolicy policy,
        CancellationToken ct,
        bool resetConfirmationsOnPause = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(evaluate);

        ThrowIfStopped(context, ct);

        TimeSpan interval = policy.PollInterval;
        if (interval < s_minPollInterval)
            interval = s_minPollInterval;

        int confirmationsNeeded = policy.ConfirmationsNeeded;
        int falseNeeded = ConditionReliability.ClampConfirmations(policy.RequiredFalseConfirmations);
        int stabilityMs = ConditionReliability.ClampStabilityMs(policy.StabilityDurationMs);
        double timeoutSeconds = Math.Max(0, policy.Timeout.TotalSeconds);
        long startTimestamp = Stopwatch.GetTimestamp();
        long streakStart = 0;
        int consecutiveSatisfied = 0;
        int consecutiveMisses = 0;
        int checks = 0;
        TimeSpan lastPausedDuration = context.Control.TotalPausedDuration;

        while (true)
        {
            ThrowIfStopped(context, ct);

            if (context.Control.IsPaused)
                await context.Control.WaitIfPausedAsync(ct).ConfigureAwait(false);

            ThrowIfStopped(context, ct);

            checks++;
            ConditionEvaluation rawEval = await evaluate(context, ct).ConfigureAwait(false);
            if (resetConfirmationsOnPause)
            {
                TimeSpan pausedDuration = context.Control.TotalPausedDuration;
                if (pausedDuration != lastPausedDuration)
                {
                    consecutiveSatisfied = consecutiveMisses = 0;
                    streakStart = 0;
                    lastPausedDuration = pausedDuration;
                }
            }
            if (rawEval.IsFailed)
            {
                context.WriteTrace(
                    "ConditionWait",
                    $"Check {checks}: evaluation failed ({rawEval.ErrorCode}).",
                    ExecutionTraceLevel.Error,
                    exception: rawEval.Exception);
                return rawEval;
            }

            bool raw = rawEval.IsSatisfied;
            bool satisfied = policy.Invert ? !raw : raw;
            if (satisfied)
            {
                if (consecutiveSatisfied == 0)
                    streakStart = Stopwatch.GetTimestamp();
                consecutiveSatisfied++;
                consecutiveMisses = 0;
            }
            else
            {
                if (consecutiveMisses == 0)
                    streakStart = Stopwatch.GetTimestamp();
                consecutiveSatisfied = 0;
                consecutiveMisses++;
                if (policy.Goal == ConditionWaitGoal.UntilSatisfied)
                    streakStart = 0;
            }

            bool stabilityMet = stabilityMs <= 0
                || (streakStart != 0 && ElapsedMilliseconds(streakStart) >= stabilityMs);
            bool trueStable = consecutiveSatisfied >= confirmationsNeeded && stabilityMet;
            bool falseStable = policy.Goal == ConditionWaitGoal.UntilEitherStable
                && consecutiveMisses >= falseNeeded
                && stabilityMet;

            context.WriteTrace(
                "ConditionWait",
                $"Check {checks}: condition={raw}{(policy.Invert ? " (wait-while inverted)" : "")}, confirmations={consecutiveSatisfied}/{confirmationsNeeded}, stability={stabilityMs}ms.",
                data: context.Variables.Get<object?>(MacroVariableNames.LastDetection));
            context.PublishStatus(new ExecutionStatusSnapshot
            {
                Phase = policy.Phase,
                Message = $"Check {checks}: {(satisfied ? "pass" : "continue")}",
                Checks = checks,
                ConsecutiveTrue = policy.Invert ? consecutiveMisses : consecutiveSatisfied,
                ConsecutiveFalse = policy.Invert ? consecutiveSatisfied : consecutiveMisses,
                Detection = context.LastColorDetection,
                Visual = context.LastVisualDiagnostics,
                Elapsed = Elapsed(startTimestamp)
            });

            if (trueStable)
                return ConditionEvaluation.Satisfied;
            if (policy.Goal == ConditionWaitGoal.UntilEitherStable && falseStable)
                return ConditionEvaluation.Unsatisfied;

            double elapsedSeconds = Elapsed(startTimestamp).TotalSeconds;
            double remainingSeconds = timeoutSeconds - elapsedSeconds;
            if (remainingSeconds <= 0)
            {
                context.WriteTrace(
                    "ConditionWait",
                    $"Condition timed out after {checks} checks and {elapsedSeconds:F2}s.",
                    ExecutionTraceLevel.Warning,
                    data: context.Variables.Get<object?>(MacroVariableNames.LastDetection));
                return policy.Goal == ConditionWaitGoal.UntilEitherStable
                    ? ConditionEvaluation.Fail("ConditionIndeterminate")
                    : ConditionEvaluation.Unsatisfied;
            }

            TimeSpan delay = interval;
            bool holdingTrue = consecutiveSatisfied >= confirmationsNeeded && stabilityMs > 0;
            bool holdingFalse = policy.Goal == ConditionWaitGoal.UntilEitherStable
                && consecutiveMisses >= falseNeeded
                && stabilityMs > 0;
            if ((holdingTrue || holdingFalse) && streakStart != 0)
            {
                long heldMs = ElapsedMilliseconds(streakStart);
                int remainingStability = Math.Max(1, stabilityMs - (int)Math.Min(int.MaxValue, heldMs));
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds, remainingStability));
            }

            if (delay.TotalSeconds > remainingSeconds)
                delay = TimeSpan.FromSeconds(remainingSeconds);

            await Task.Delay(delay, ct).ConfigureAwait(false);
            ThrowIfStopped(context, ct);
        }
    }

    public static void ThrowIfStopped(MacroContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (context.IsAborted)
            throw new OperationCanceledException("Macro execution was aborted.", ct);
    }

    private static TimeSpan Elapsed(long started)
        => TimeSpan.FromSeconds((double)(Stopwatch.GetTimestamp() - started) / Stopwatch.Frequency);

    private static long ElapsedMilliseconds(long started)
        => (long)Elapsed(started).TotalMilliseconds;
}
