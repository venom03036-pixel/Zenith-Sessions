namespace MacroSuite.Core.Usage;

/// <summary>
/// Client-side hold placed before an AI provider call. Local trackers do not debit until
/// <see cref="IUsageTracker.CommitAsync"/>; a future server tracker can treat this as a real reservation.
/// </summary>
public sealed class UsageReservation
{
    public required Guid Id { get; init; }

    /// <summary>Buffered estimate used for the pre-flight cap check, in USD.</summary>
    public required decimal EstimatedCostUsd { get; init; }

    /// <summary>Set when spend has been recorded so <see cref="IUsageTracker.ReleaseAsync"/> is skipped.</summary>
    public bool IsCommitted { get; set; }
}
