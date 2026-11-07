namespace MacroSuite.Core.Usage;

/// <summary>
/// Repository contract for persisting and aggregating usage and cost telemetry across restarts (Rule 18).
/// </summary>
public interface IUsageRepository : IDisposable
{
    /// <summary>
    /// Persists a single usage record asynchronously.
    /// </summary>
    Task RecordAsync(UsageRecord record, CancellationToken ct = default);

    /// <summary>
    /// Calculates the sum of all estimated costs in USD recorded since the specified timestamp.
    /// </summary>
    Task<decimal> GetTotalEstimatedCostAsync(DateTimeOffset since, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the most recent usage records ordered descending by timestamp.
    /// </summary>
    Task<IReadOnlyList<UsageRecord>> GetRecentAsync(int count, CancellationToken ct = default);

    /// <summary>
    /// Aggregates spend and token counts recorded since <paramref name="since"/>.
    /// </summary>
    Task<UsageTotals> GetTotalsAsync(DateTimeOffset since, CancellationToken ct = default);
}
