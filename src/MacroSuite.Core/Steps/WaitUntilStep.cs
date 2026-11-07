using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;

namespace MacroSuite.Core.Steps;

/// <summary>
/// Execution step that blocks until a specified <see cref="ICondition"/> is met, or until timeout occurs.
/// </summary>
public sealed class WaitUntilStep : MacroStep
{
    private static readonly IRuleEngine s_defaultRuleEngine = new RuleEngine();

    /// <summary>The condition to wait for.</summary>
    public ICondition Condition { get; init; }

    /// <summary>Maximum duration to wait before timing out.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Interval between condition checks.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Consecutive successful checks required before the wait completes.</summary>
    public int RequiredConfirmations { get; init; } = 1;

    /// <summary>Consecutive failed checks required before Wait While / absence completes.</summary>
    public int RequiredFalseConfirmations { get; init; } = 1;

    /// <summary>Minimum time the satisfied result must hold. Zero disables the window.</summary>
    public int StabilityDurationMs { get; init; }

    /// <summary>Whether to throw a <see cref="TimeoutException"/> if the timeout expires without the condition being satisfied.</summary>
    public bool ThrowOnTimeout { get; init; }

    /// <summary>
    /// When true, this is Wait While: poll until the condition is false.
    /// When false, this is Wait Until: poll until the condition is true.
    /// </summary>
    public bool WaitWhile { get; init; }

    /// <summary>
    /// Monitors this image and repeats the following action block while it is visible.
    /// Defaults to false so existing saved macros retain their one-shot wait behavior.
    /// The enclosing action-list dispatcher owns monitoring and serializes all blocks.
    /// </summary>
    public bool RepeatWhileVisible { get; init; }

    /// <summary>Minimum time after a block completes before it can run again.</summary>
    public int RepeatDelayMs { get; init; } = 500;

    /// <summary>Optional run-wide appearance declaration; handled by the executor, not as a sequential wait.</summary>
    public AppearanceRedirectOptions? AppearanceRedirect { get; init; }

    /// <summary>
    /// Constructs a new <see cref="WaitUntilStep"/>.
    /// </summary>
    public static string FormatLabel(ICondition condition, bool waitWhile)
        => waitWhile
            ? $"Wait while: {condition.Description}"
            : $"Wait until: {condition.Description}";

    [System.Text.Json.Serialization.JsonConstructor]
    public WaitUntilStep(ICondition condition)
    {
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        Label = FormatLabel(condition, waitWhile: false);
    }

    /// <inheritdoc/>
    public override async Task ExecuteAsync(MacroContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!IsEnabled || context.IsAborted)
        {
            return;
        }

        ConditionFailurePolicy policy = BlockRouting.ConditionPolicy(Condition);
        int attempts = policy == ConditionFailurePolicy.Retry ? 4 : 1;
        bool satisfied = false;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                satisfied = await s_defaultRuleEngine.WaitUntilAsync(
                    Condition,
                    context,
                    Timeout,
                    PollInterval,
                    ct,
                    RequiredConfirmations,
                    invert: WaitWhile,
                    RequiredFalseConfirmations,
                    StabilityDurationMs).ConfigureAwait(false);
                if (satisfied) break;
            }
            catch (ConditionEvaluationException ex)
            {
                var failure = ConditionEvaluation.Fail(ex.ErrorCode ?? "UnknownConditionFailure", ex.InnerException);
                if (!BlockRouting.IsRecoverable(failure)) throw;
                if (policy == ConditionFailurePolicy.Stop) throw;
                if (policy == ConditionFailurePolicy.Skip) return;
                if (attempt + 1 >= attempts)
                {
                    if (context.CurrentBlockDepth > 0)
                        throw new RecoverableBlockFailureException($"Wait '{Label}' failed: {ex.ErrorCode}", ex);
                    throw;
                }
            }
            if (attempt + 1 < attempts)
                await Task.Delay(100, ct).ConfigureAwait(false);
        }

        if (!satisfied && (ThrowOnTimeout || policy == ConditionFailurePolicy.Stop)
            && !ct.IsCancellationRequested && !context.IsAborted)
        {
            throw new TimeoutException($"Step '{Label}' timed out after {Timeout.TotalSeconds:N1}s. Condition '{Condition.Description}' was not met.");
        }

        if (!satisfied && policy != ConditionFailurePolicy.Skip && context.CurrentBlockDepth > 0
            && !ct.IsCancellationRequested && !context.IsAborted)
            throw new RecoverableBlockFailureException($"Wait '{Label}' timed out without satisfying its condition.");

        if (satisfied
            && DetectionCursorMover.ShouldMoveAfterWait(Condition, WaitWhile)
            && !ct.IsCancellationRequested
            && !context.IsAborted)
        {
            await DetectionCursorMover.MoveToLastDetectionAsync(context, ct, requireCoordinates: true)
                .ConfigureAwait(false);
        }
    }
}
