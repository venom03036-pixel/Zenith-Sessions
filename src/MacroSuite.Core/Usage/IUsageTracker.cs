namespace MacroSuite.Core.Usage;

/// <summary>
/// Spend-cap enforcement and usage recording. Local SQLite is a BYOK safety rail;
/// a server implementation can enforce paid-tier quotas with the same shape.
/// </summary>
public interface IUsageTracker
{
    event Action<UsageRecord>? UsageRecorded;

    /// <summary>Daily burst limit in USD (UTC day). Stops a runaway macro from spending the monthly budget in one run.</summary>
    decimal? DailySpendCapUsd { get; set; }

    /// <summary>Monthly budget in USD (UTC calendar month). This is the real ceiling.</summary>
    decimal? MonthlySpendCapUsd { get; set; }

    Task PreflightCheckAsync(decimal estimatedCostUsd, CancellationToken ct = default);

    /// <summary>
    /// Pre-flights a buffered estimate and returns a reservation. Does not persist spend until
    /// <see cref="CommitAsync"/>.
    /// </summary>
    Task<UsageReservation> ReserveAsync(decimal estimatedCostUsd, CancellationToken ct = default);

    Task CommitAsync(UsageReservation reservation, UsageRecord actual, CancellationToken ct = default);

    Task ReleaseAsync(UsageReservation reservation, CancellationToken ct = default);

    /// <summary>
    /// After recording provider-returned actuals, abort if spend already exceeds a cap
    /// so input is not dispatched for an over-budget call.
    /// </summary>
    Task ThrowIfCapsExceededAsync(CancellationToken ct = default);

    Task RecordAsync(UsageRecord record, CancellationToken ct = default);

    Task<UsageTotals> GetTotalsAsync(DateTimeOffset since, CancellationToken ct = default);

    Task<decimal> GetTotalSpendAsync(DateTimeOffset since, CancellationToken ct = default);

    Task<IReadOnlyList<UsageRecord>> GetRecentAsync(int count, CancellationToken ct = default);
}
