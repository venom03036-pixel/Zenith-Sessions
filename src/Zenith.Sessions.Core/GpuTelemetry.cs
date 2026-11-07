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
/// Optional WDDM counter evidence. Missing counters/unsupported drivers are unknown, never zero.
/// There is deliberately no total formed by adding independent engines, adapters or PID maxima.
/// </summary>
public sealed record GpuTelemetrySnapshot(DateTimeOffset? ObservedUtc,
    IReadOnlyList<GpuAdapterTelemetry> Adapters, string? Problem = null)
{
    public const int FreshnessMilliseconds = 3000;
    public bool IsFresh(DateTimeOffset now) => ObservedUtc is { } at && at <= now.AddSeconds(1)
        && now - at <= TimeSpan.FromMilliseconds(FreshnessMilliseconds);
    public static GpuTelemetrySnapshot Unavailable(string problem) => new(null, Array.Empty<GpuAdapterTelemetry>(), problem);
}
