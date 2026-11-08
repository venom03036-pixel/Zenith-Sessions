namespace Zenith.Sessions.Core;

/// <summary>A physical engine occupancy, not a process or whole-machine GPU percentage.</summary>
public sealed record GpuEngineTelemetry(int EngineIndex, string EngineType, double? UtilizationPercent);

/// <summary>
/// LUID plus physical index is the counter provider's adapter identity, not Task Manager's GPU number.
/// Dedicated/shared usage is adapter-wide; shared usage is not additional physical RAM to subtract.
/// No memory capacity/budget or per-process identity is inferred from these usage counters.
/// </summary>
public sealed record GpuAdapterTelemetry(string AdapterLuid, int PhysicalAdapterIndex,
    IReadOnlyList<GpuEngineTelemetry> Engines, double? BusiestEnginePercent,
    ulong? DedicatedBytes, ulong? SharedBytes, bool EngineDataComplete, string? Problem = null);

/// <summary>
/// One client process's own GPU numbers (R-instances-15, owner decision 2026-11-08). The process is identified by PID and
/// start time, confirmed on both sides of the counter collection by the sampler that publishes it: the same PID carried this
/// start time in the process inventory taken before the collection and in a read taken after it, so a PID that Windows reused
/// in between is never credited with another process's rows. <see cref="BusiestEnginePercent"/> is the highest of this process's
/// own physical-engine percentages (never a sum across engines or adapters); <see cref="DedicatedBytes"/> is its dedicated GPU
/// memory in use, added across adapters because bytes add. Unknown stays null, never zero.
/// </summary>
public sealed record GpuClientTelemetry(int ProcessId, long ProcessStartUtcTicks, double? BusiestEnginePercent, ulong? DedicatedBytes);

/// <summary>
/// Optional WDDM counter evidence. Missing counters/unsupported drivers are unknown, never zero.
/// There is deliberately no total formed by adding independent engines, adapters or PID maxima.
/// <see cref="Clients"/> is empty unless the sampler was asked for per-client numbers and could confirm each client's identity.
/// </summary>
public sealed record GpuTelemetrySnapshot(DateTimeOffset? ObservedUtc,
    IReadOnlyList<GpuAdapterTelemetry> Adapters, string? Problem = null, IReadOnlyList<GpuClientTelemetry>? Clients = null)
{
    public const int FreshnessMilliseconds = 3000;
    /// <summary>Confirmed clients only, in PID order. Optional within the snapshot: older producers leave it empty.</summary>
    public IReadOnlyList<GpuClientTelemetry> Clients { get; init; } = Clients ?? Array.Empty<GpuClientTelemetry>();
    public bool IsFresh(DateTimeOffset now) => ObservedUtc is { } at && at <= now.AddSeconds(1)
        && now - at <= TimeSpan.FromMilliseconds(FreshnessMilliseconds);
    public static GpuTelemetrySnapshot Unavailable(string problem) => new(null, Array.Empty<GpuAdapterTelemetry>(), problem);
}

/// <summary>
/// A desktop monitor that can attribute GPU counters to client processes. Off until the saved option turns it on; while off
/// the monitor adds no per-process counter and opens no extra process handle.
/// </summary>
public interface IClientGpuTelemetryControl
{
    void ConfigureClientGpuTelemetry(bool enabled);
}
