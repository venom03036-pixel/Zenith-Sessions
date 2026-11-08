namespace Zenith.Sessions.Core;

public enum RequestedServerCapacityState { Unknown, SpaceReported, InsufficientSlots, Full, Stale, InvalidRecipientCount }

/// <summary>Public occupancy advice only. It never reserves a seat or establishes authorization or arrival.</summary>
public sealed record RequestedServerCapacityAssessment(RequestedServerCapacityState State, int RecipientCount,
    int? OpenSlots, DateTimeOffset? ObservedUtc, DateTimeOffset? ExpiresUtc, string Explanation)
{
    public bool HasFreshObservation => State is RequestedServerCapacityState.SpaceReported
        or RequestedServerCapacityState.InsufficientSlots or RequestedServerCapacityState.Full;
    public bool Warns => State != RequestedServerCapacityState.SpaceReported;
}

public static class RequestedServerCapacity
{
    public static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromSeconds(5);

    /// <summary>The freshness rule <see cref="Assess"/> applies: timed, at most five minutes old and at most five seconds ahead.</summary>
    public static bool IsFresh(DateTimeOffset observedUtc, DateTimeOffset now)
        => observedUtc != default && observedUtc - now <= MaximumFutureSkew && now - observedUtc <= MaximumAge;

    public static RequestedServerCapacityAssessment Assess(RobloxRequestedDestination? requested,
        RobloxPublicServer? observation, int recipientCount, DateTimeOffset now)
    {
        if (recipientCount <= 0)
            return new(RequestedServerCapacityState.InvalidRecipientCount, recipientCount, null, null, null,
                "Choose at least one recipient before comparing public server capacity. No seats are reserved.");
        if (requested is null || requested.ServerKind != RobloxServerKind.Public || observation is null || observation.PlaceId <= 0
            || requested.PlaceId != observation.PlaceId || !RobloxRequestedDestination.TryNormalizeJobId(observation.JobId, out var job)
            || !string.Equals(requested.JobId, job, StringComparison.Ordinal) || observation.Playing < 0 || observation.MaxPlayers <= 0)
            return new(RequestedServerCapacityState.Unknown, recipientCount, null, null, null,
                "Capacity is unknown for the requested destination. Browse and explicitly use a fresh matching public server observation; no seats are reserved.");

        var observed = observation.ObservedUtc.ToUniversalTime();
        now = now.ToUniversalTime();
        DateTimeOffset expires = observed > DateTimeOffset.MaxValue - MaximumAge
            ? DateTimeOffset.MaxValue : observed + MaximumAge;
        if (!IsFresh(observed, now))
            return new(RequestedServerCapacityState.Stale, recipientCount, null, observed, expires,
                "The requested server capacity observation is stale or its timestamp cannot be trusted. Browse and explicitly use a fresh observation; no seats are reserved.");

        // Widen before subtraction; overfull observations report zero, never negative or wrapped capacity.
        int open = (int)Math.Max(0L, (long)observation.MaxPlayers - observation.Playing);
        if (open == 0)
            return new(RequestedServerCapacityState.Full, recipientCount, 0, observed, expires,
                "The requested public server was full when observed. Capacity can change; this request is not a reserved seat or verified arrival.");
        if (recipientCount > open)
            return new(RequestedServerCapacityState.InsufficientSlots, recipientCount, open, observed, expires,
                $"The requested public server reported {open} open slots for {recipientCount} recipients. The batch exceeds that observation; seats are not reserved and arrival remains unverified.");
        return new(RequestedServerCapacityState.SpaceReported, recipientCount, open, observed, expires,
            $"The requested public server reported {open} open slots for {recipientCount} recipients. Capacity can change; seats are not reserved and arrival remains unverified.");
    }
}
