using MacroSuite.Core.Execution;

namespace MacroSuite.Core.Rules;

/// <summary>
/// Service that evaluates rules and coordinates waiting for conditions to be satisfied.
/// </summary>
public interface IRuleEngine
{
    /// <summary>
    /// Waits until the specified condition evaluates to true, or until timeout/cancellation occurs.
    /// </summary>
    /// <param name="condition">Condition to evaluate.</param>
    /// <param name="context">Macro execution context.</param>
    /// <param name="timeout">Maximum duration to wait.</param>
    /// <param name="pollInterval">Interval between evaluations (defaults to 50ms).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="requiredConfirmations">Consecutive true evaluations required for presence.</param>
    /// <param name="invert">When true, wait until the condition is false (Wait While).</param>
    /// <param name="requiredFalseConfirmations">Consecutive false evaluations required when <paramref name="invert"/> is true.</param>
    /// <param name="stabilityDurationMs">Minimum time the satisfied result must hold. Zero disables the window.</param>
    /// <returns>True if condition was satisfied before timeout; otherwise false.</returns>
    Task<bool> WaitUntilAsync(
        ICondition condition,
        MacroContext context,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default,
        int requiredConfirmations = 1,
        bool invert = false,
        int requiredFalseConfirmations = 1,
        int stabilityDurationMs = 0);
}
