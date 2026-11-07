namespace MacroSuite.Core.Rules;

/// <summary>
/// Three-way condition result. Cancellation is not an outcome — it remains
/// <see cref="OperationCanceledException"/>.
/// </summary>
public enum ConditionOutcome
{
    Satisfied = 0,
    Unsatisfied = 1,
    Failed = 2
}

/// <summary>
/// Canonical condition evaluation. Failures must never be coerced into business true/false.
/// </summary>
public sealed record ConditionEvaluation(
    ConditionOutcome Outcome,
    string? ErrorCode = null,
    Exception? Exception = null)
{
    /// <summary>Optional actionable diagnostic; routing continues to use <see cref="ErrorCode"/>.</summary>
    public string? ErrorMessage { get; init; }

    public static ConditionEvaluation Satisfied { get; } = new(ConditionOutcome.Satisfied);
    public static ConditionEvaluation Unsatisfied { get; } = new(ConditionOutcome.Unsatisfied);

    public static ConditionEvaluation Fail(string errorCode, Exception? exception = null)
        => new(ConditionOutcome.Failed, errorCode, exception);

    public bool IsSatisfied => Outcome == ConditionOutcome.Satisfied;
    public bool IsFailed => Outcome == ConditionOutcome.Failed;

    public void ThrowIfFailed(string? operation = null)
    {
        if (Outcome != ConditionOutcome.Failed)
            return;

        if (Exception is not null)
        {
            throw new ConditionEvaluationException(
                operation is null
                    ? (ErrorMessage ?? ErrorCode ?? "Condition evaluation failed.")
                    : $"{operation}: {ErrorMessage ?? ErrorCode ?? Exception.Message}",
                Exception)
            {
                ErrorCode = ErrorCode
            };
        }

        throw new ConditionEvaluationException(
            operation is null
                ? (ErrorMessage ?? ErrorCode ?? "Condition evaluation failed.")
                : $"{operation}: {ErrorMessage ?? ErrorCode}")
        {
            ErrorCode = ErrorCode
        };
    }
}

/// <summary>Raised when a condition evaluation fails closed (not a business miss).</summary>
public sealed class ConditionEvaluationException : InvalidOperationException
{
    public string? ErrorCode { get; init; }

    public ConditionEvaluationException(string message)
        : base(message)
    {
    }

    public ConditionEvaluationException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
