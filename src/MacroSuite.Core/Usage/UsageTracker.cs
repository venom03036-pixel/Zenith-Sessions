using MacroSuite.Core.Licensing;

namespace MacroSuite.Core.Usage;

/// <summary>
/// Local spend-cap enforcement backed by <see cref="IUsageRepository"/> (Rule 18).
/// Daily is a burst limiter; monthly is the budget. Client-side counters are a safety rail
/// for BYOK — they can be patched, which is why paid-tier quotas must move to a server tracker.
/// </summary>
public sealed class UsageTracker : IUsageTracker
{
    /// <summary>Pre-flight estimates are inflated so under-counted vision output cannot sneak past the cap.</summary>
    public const decimal EstimateBufferMultiplier = 1.15m;

    /// <summary>Conservative USD hold used when the provider has not yet returned usage.</summary>
    public const decimal DefaultVisionEstimateUsd = 0.05m;

    private readonly IUsageRepository _repository;
    private readonly IFeatureGate _featureGate;

    /// <inheritdoc/>
    public event Action<UsageRecord>? UsageRecorded;

    /// <inheritdoc/>
    public decimal? MonthlySpendCapUsd { get; set; }

    /// <inheritdoc/>
    public decimal? DailySpendCapUsd { get; set; }

    public UsageTracker(IUsageRepository repository, IFeatureGate featureGate, decimal? monthlySpendCapUsd = null, decimal? dailySpendCapUsd = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _featureGate = featureGate ?? throw new ArgumentNullException(nameof(featureGate));
        MonthlySpendCapUsd = monthlySpendCapUsd;
        DailySpendCapUsd = dailySpendCapUsd;
    }

    public static decimal ApplyEstimateBuffer(decimal estimatedCostUsd)
    {
        if (estimatedCostUsd <= 0m)
            return 0m;

        return decimal.Round(estimatedCostUsd * EstimateBufferMultiplier, 4, MidpointRounding.AwayFromZero);
    }

    /// <inheritdoc/>
    public async Task PreflightCheckAsync(decimal estimatedCostUsd, CancellationToken ct = default)
    {
        _featureGate.Require(FeatureId.UsageTracking);

        if (estimatedCostUsd <= 0m)
            return;

        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (DailySpendCapUsd.HasValue)
        {
            decimal daySpend = await _repository.GetTotalEstimatedCostAsync(UtcStartOfDay(now), ct).ConfigureAwait(false);
            if (daySpend + estimatedCostUsd > DailySpendCapUsd.Value)
            {
                throw new SpendCapExceededException(
                    daySpend,
                    estimatedCostUsd,
                    DailySpendCapUsd.Value,
                    $"Daily burst limit of ${DailySpendCapUsd.Value:F2} exceeded. Wait until the next UTC day; monthly budget is unchanged except for spend already recorded. Current daily spend: ${daySpend:F4}, attempted: ${estimatedCostUsd:F4}.");
            }
        }

        if (MonthlySpendCapUsd.HasValue)
        {
            decimal monthSpend = await _repository.GetTotalEstimatedCostAsync(UtcStartOfMonth(now), ct).ConfigureAwait(false);
            if (monthSpend + estimatedCostUsd > MonthlySpendCapUsd.Value)
            {
                throw new SpendCapExceededException(
                    monthSpend,
                    estimatedCostUsd,
                    MonthlySpendCapUsd.Value,
                    $"Monthly budget of ${MonthlySpendCapUsd.Value:F2} exceeded. Wait until the next UTC calendar month. Current monthly spend: ${monthSpend:F4}, attempted: ${estimatedCostUsd:F4}.");
            }
        }
    }

    /// <inheritdoc/>
    public async Task<UsageReservation> ReserveAsync(decimal estimatedCostUsd, CancellationToken ct = default)
    {
        decimal buffered = ApplyEstimateBuffer(estimatedCostUsd);
        await PreflightCheckAsync(buffered, ct).ConfigureAwait(false);
        return new UsageReservation
        {
            Id = Guid.NewGuid(),
            EstimatedCostUsd = buffered
        };
    }

    /// <inheritdoc/>
    public async Task CommitAsync(UsageReservation reservation, UsageRecord actual, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        await RecordAsync(actual, ct).ConfigureAwait(false);
        reservation.IsCommitted = true;
    }

    /// <inheritdoc/>
    public Task ReleaseAsync(UsageReservation reservation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task ThrowIfCapsExceededAsync(CancellationToken ct = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (DailySpendCapUsd.HasValue)
        {
            decimal daySpend = await _repository.GetTotalEstimatedCostAsync(UtcStartOfDay(now), ct).ConfigureAwait(false);
            if (daySpend > DailySpendCapUsd.Value)
            {
                throw new SpendCapExceededException(
                    daySpend,
                    0m,
                    DailySpendCapUsd.Value,
                    $"Daily burst limit of ${DailySpendCapUsd.Value:F2} exceeded after the provider call (${daySpend:F4} spent today). Input was not sent and the macro will stop.");
            }
        }

        if (MonthlySpendCapUsd.HasValue)
        {
            decimal monthSpend = await _repository.GetTotalEstimatedCostAsync(UtcStartOfMonth(now), ct).ConfigureAwait(false);
            if (monthSpend > MonthlySpendCapUsd.Value)
            {
                throw new SpendCapExceededException(
                    monthSpend,
                    0m,
                    MonthlySpendCapUsd.Value,
                    $"Monthly budget of ${MonthlySpendCapUsd.Value:F2} exceeded after the provider call (${monthSpend:F4} spent this month). Input was not sent and the macro will stop.");
            }
        }
    }

    /// <inheritdoc/>
    public async Task RecordAsync(UsageRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _repository.RecordAsync(record, ct).ConfigureAwait(false);
        UsageRecorded?.Invoke(record);
    }

    /// <inheritdoc/>
    public Task<UsageTotals> GetTotalsAsync(DateTimeOffset since, CancellationToken ct = default)
        => _repository.GetTotalsAsync(since, ct);

    /// <inheritdoc/>
    public Task<decimal> GetTotalSpendAsync(DateTimeOffset since, CancellationToken ct = default)
        => _repository.GetTotalEstimatedCostAsync(since, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<UsageRecord>> GetRecentAsync(int count, CancellationToken ct = default)
        => _repository.GetRecentAsync(count, ct);

    internal static DateTimeOffset UtcStartOfDay(DateTimeOffset now)
        => new(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);

    internal static DateTimeOffset UtcStartOfMonth(DateTimeOffset now)
        => new(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
}
