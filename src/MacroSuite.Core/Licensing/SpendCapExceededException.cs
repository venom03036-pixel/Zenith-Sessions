namespace MacroSuite.Core.Licensing;

/// <summary>
/// Thrown when an operation would exceed the configured spend cap limit (Rule 18).
/// </summary>
public sealed class SpendCapExceededException : InvalidOperationException
{
    /// <summary>Total accumulated expenditure in USD for the active period.</summary>
    public decimal CurrentSpendUsd { get; }

    /// <summary>The estimated cost in USD of the operation that was blocked.</summary>
    public decimal AttemptedCostUsd { get; }

    /// <summary>The maximum allowable expenditure in USD.</summary>
    public decimal SpendCapUsd { get; }

    public SpendCapExceededException(decimal currentSpendUsd, decimal attemptedCostUsd, decimal spendCapUsd)
        : base($"Spend cap exceeded. Current spend: ${currentSpendUsd:F4}, attempted cost: ${attemptedCostUsd:F4}, spend cap: ${spendCapUsd:F2}. Operation blocked.")
    {
        CurrentSpendUsd = currentSpendUsd;
        AttemptedCostUsd = attemptedCostUsd;
        SpendCapUsd = spendCapUsd;
    }

    public SpendCapExceededException(decimal currentSpendUsd, decimal attemptedCostUsd, decimal spendCapUsd, string message)
        : base(message)
    {
        CurrentSpendUsd = currentSpendUsd;
        AttemptedCostUsd = attemptedCostUsd;
        SpendCapUsd = spendCapUsd;
    }

    public SpendCapExceededException(decimal currentSpendUsd, decimal attemptedCostUsd, decimal spendCapUsd, string message, Exception innerException)
        : base(message, innerException)
    {
        CurrentSpendUsd = currentSpendUsd;
        AttemptedCostUsd = attemptedCostUsd;
        SpendCapUsd = spendCapUsd;
    }
}
