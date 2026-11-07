namespace MacroSuite.Core.Usage;

/// <summary>
/// Aggregated spend and token totals for a time window.
/// </summary>
public readonly record struct UsageTotals(
    decimal CostUsd,
    int InputTokens,
    int OutputTokens,
    int RequestCount);
