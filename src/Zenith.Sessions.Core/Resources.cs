namespace Zenith.Sessions.Core;

public sealed record ResourceSnapshot(DateTimeOffset ObservedUtc, ulong TotalPhysical, ulong AvailablePhysical,
    ulong CommitLimit, ulong CommitUsed, double? CpuPercent, string? Problem = null, GpuTelemetrySnapshot? Gpu = null)
{
    public bool IsValid => Problem is null && TotalPhysical > 0 && AvailablePhysical <= TotalPhysical
        && CommitLimit > 0 && CommitUsed <= CommitLimit;
}

public sealed record CapacitySettings(double SparePhysicalPercent = 20, double SparePhysicalGiB = 3,
    double SpareCommitPercent = 10, double SpareCommitGiB = 2, double NewClientPhysicalGiB = 2.5,
    double NewClientCommitGiB = 3, int? UserMaximum = null)
{
    public CapacitySettings Validated()
    {
        double[] values = [SparePhysicalPercent, SparePhysicalGiB, SpareCommitPercent, SpareCommitGiB, NewClientPhysicalGiB, NewClientCommitGiB];
        if (values.Any(v => !double.IsFinite(v)) || SparePhysicalPercent is < 0 or > 90 || SpareCommitPercent is < 0 or > 90
            || SparePhysicalGiB is < 0 or > 1024 || SpareCommitGiB is < 0 or > 1024
            || NewClientPhysicalGiB is < .1 or > 1024 || NewClientCommitGiB is < .1 or > 1024 || UserMaximum is <= 0)
            throw new ArgumentException("Capacity limits must be finite positive values within the displayed ranges.");
        return this;
    }
}

public sealed record ResourceReservation(Guid OperationId, ulong PhysicalBytes, ulong CommitBytes);
public sealed record CapacityDecision(int MemoryOnlySlots, ulong PhysicalHeadroom, ulong CommitHeadroom,
    ulong ReservedPhysical, ulong ReservedCommit, string Explanation, bool SamplesHealthy);

public static class CapacityPolicy
{
    public const ulong GiB = 1024UL * 1024 * 1024;
    /// <summary>
    /// Candidate-independent spare-capacity health for the pressure latch. A default estimate,
    /// queued forecast or user count limit must not latch out a different candidate or charge
    /// the current not-yet-emitted start twice. Actual admission still checks all reservations.
    /// </summary>
    public static CapacityDecision EvaluateSystemPressure(ResourceSnapshot sample, CapacitySettings policy, DateTimeOffset now)
        => Evaluate(sample, policy with { UserMaximum = null }, [], 0, now, new(1, 1));

    public static CapacityDecision Evaluate(ResourceSnapshot sample, CapacitySettings policy,
        IReadOnlyList<ResourceReservation> reservations, int occupied, DateTimeOffset now, LaunchResourceBudget? candidateBudget = null)
    {
        policy.Validated();
        var candidate = (candidateBudget ?? LaunchResourceBudget.FromSettings(policy)).Validated();
        if (!sample.IsValid || sample.ObservedUtc > now.AddSeconds(1) || now - sample.ObservedUtc > TimeSpan.FromSeconds(3))
            return new(0, 0, 0, 0, 0, "Waiting for a fresh, valid memory and commit sample.", false);
        ulong physicalHeadroom = Math.Max(Bytes(policy.SparePhysicalGiB), (ulong)(sample.TotalPhysical * policy.SparePhysicalPercent / 100));
        ulong commitHeadroom = Math.Max(Bytes(policy.SpareCommitGiB), (ulong)(sample.CommitLimit * policy.SpareCommitPercent / 100));
        ulong reservePhysical = 0, reserveCommit = 0;
        var seen = new HashSet<Guid>();
        foreach (var reservation in reservations)
        {
            if (reservation.OperationId == Guid.Empty || !seen.Add(reservation.OperationId))
                throw new ArgumentException("Reservations require distinct operation identities.");
            reservePhysical = SaturatingAdd(reservePhysical, reservation.PhysicalBytes);
            reserveCommit = SaturatingAdd(reserveCommit, reservation.CommitBytes);
        }
        ulong physicalFree = Subtract(sample.AvailablePhysical, SaturatingAdd(physicalHeadroom, reservePhysical));
        ulong commitFree = Subtract(Subtract(sample.CommitLimit, sample.CommitUsed), SaturatingAdd(commitHeadroom, reserveCommit));
        ulong slots = Math.Min(physicalFree / candidate.PhysicalBytes, commitFree / candidate.CommitBytes);
        if (policy.UserMaximum is { } maximum) slots = Math.Min(slots, (ulong)Math.Max(0, maximum - Math.Max(0, occupied)));
        int count = (int)Math.Min(slots, int.MaxValue);
        string reason = count == 0 ? "Memory, commit or the selected client limit leaves no additional client budget."
            : $"Memory-only budget for approximately {count} clients with this estimate; provider and sustained CPU/GPU checks still apply.";
        reason += $" After spare capacity and existing forecasts: {physicalFree / (double)GiB:0.00} GiB physical / {commitFree / (double)GiB:0.00} GiB commit available;"
            + $" this candidate requires {candidate.PhysicalBytes / (double)GiB:0.00} / {candidate.CommitBytes / (double)GiB:0.00} GiB.";
        return new(count, physicalHeadroom, commitHeadroom, reservePhysical, reserveCommit, reason, true);
    }
    private static ulong Bytes(double gib) => (ulong)Math.Ceiling(gib * GiB);
    private static ulong Subtract(ulong value, ulong amount) => value > amount ? value - amount : 0;
    private static ulong SaturatingAdd(ulong a, ulong b) => a > ulong.MaxValue - b ? ulong.MaxValue : a + b;
}

public sealed record SessionObservation(int ProcessId, long ProcessStartUtcTicks, string ClientVersion,
    nint Window, string WindowTitle, bool Minimized, bool Visible, long WorkingSetBytes,
    long PrivateCommitBytes, double? CpuPercent, DateTimeOffset ObservedUtc, string? Problem = null,
    long? PrivateWorkingSetBytes = null,long? VirtualAddressBytes = null,double? PageFaultsPerSecond = null,
    ProcessIoRates? IoRates = null, bool? WindowHung = null, bool NotResponding = false)
{
    // WindowHung: this sample's IsHungAppWindow result for the visible main window; null when it was not checked.
    // NotResponding: set only by ClientResponsivenessReducer after consecutive hung samples.
    public string InstanceKey => $"{ProcessId}:{ProcessStartUtcTicks}";
    public string ProcessLabel => $"PID {ProcessId}";
    public string Status => Problem is not null ? "Observation unavailable" : NotResponding ? "Not responding · account unverified"
        : Minimized ? "Minimized" : Visible ? "Open · account unverified" : "No visible window";
    public string MemoryDisplay => $"{WorkingSetBytes / (double)CapacityPolicy.GiB:0.00} GiB";
}

/// <summary>
/// Process evidence belongs to the actual enumeration, independently of resource measurements.
/// An incomplete or legacy inventory cannot establish that an absent process stopped or exited.
/// </summary>
public sealed record DesktopSnapshot(ResourceSnapshot Resources, IReadOnlyList<SessionObservation> Sessions, string? Problem = null,
    DateTimeOffset? ProcessObservedUtc = null, long ProcessRevision = 0, bool ProcessInventoryComplete = false);
public interface IDesktopMonitor
{
    Task<DesktopSnapshot> SampleAsync(CancellationToken ct);
    Task<bool> FocusAsync(SessionObservation session, CancellationToken ct);
    Task<bool> RequestCloseAsync(SessionObservation session, CancellationToken ct);
}

public interface IConfigurableDesktopMonitor : IDesktopMonitor
{
    void ConfigurePerformance(ManagerPerformancePolicy policy);
}
