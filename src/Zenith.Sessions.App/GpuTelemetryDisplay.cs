using System.Globalization;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

public sealed record GpuAdapterDisplay(string Identity, string Engine, string Dedicated, string Shared, string Status);

/// <summary>Optional adapter evidence, aged on the existing presentation tick. No GPU polling or summed utilization.</summary>
public sealed class GpuTelemetryDisplay : Observable
{
    private GpuTelemetrySnapshot? _source;
    private bool _fresh;
    private string _status = "GPU counters have not been sampled.";
    public IReadOnlyList<GpuAdapterDisplay> Adapters { get; private set; } = [];
    public string Status { get => _status; private set => Set(ref _status, value); }

    public void Update(GpuTelemetrySnapshot? source, DateTimeOffset now)
    {
        bool fresh = source?.IsFresh(now) == true;
        if (!ReferenceEquals(source, _source) || fresh != _fresh)
        {
            _source = source; _fresh = fresh;
            // The native reader already bounds these collections; retain a display bound for other providers.
            Adapters = Array.AsReadOnly((source?.Adapters ?? []).Take(16).Select(adapter =>
            {
                bool complete = fresh && adapter.EngineDataComplete && adapter.BusiestEnginePercent is { } load
                    && double.IsFinite(load) && load is >=0 and <=100;
                string engine = complete && adapter.BusiestEnginePercent is { } value && double.IsFinite(value) && value is >= 0 and <= 100
                    ? value.ToString("0.0", CultureInfo.CurrentCulture) + "% busiest engine" : "Engine load unavailable";
                return new GpuAdapterDisplay(
                    "Adapter " + adapter.PhysicalAdapterIndex + " · " + SafeIdentity(adapter.AdapterLuid), engine,
                    "Dedicated usage · " + Bytes(fresh ? adapter.DedicatedBytes : null),
                    "Shared usage · " + Bytes(fresh ? adapter.SharedBytes : null),
                    !fresh ? "Last-known adapter · counters expired" : complete ? "Measured adapter counters" : "Partial counters · engine load unknown");
            }).ToArray());
            Raise(nameof(Adapters));
        }
        Status = source is null ? "GPU counters have not been sampled."
            : !fresh ? "GPU readings unavailable or stale. No zero load is assumed."
            : source.Adapters.Count == 0 ? "GPU counters unavailable on this driver."
            : "Adapter-wide counters · " + source.ObservedUtc!.Value.ToLocalTime().ToString("T", CultureInfo.CurrentCulture)
                + (source.Problem is null ? " · independent engines and adapters are not added." : " · some counters unavailable.");
    }

    private static string Bytes(ulong? value) => value is { } bytes
        ? (bytes / (double)CapacityPolicy.GiB).ToString("0.00", CultureInfo.CurrentCulture) + " GiB" : "Unavailable";
    private static string SafeIdentity(string value)
    {
        if(value.Length is >0 and <=64 && value.All(c=>char.IsAsciiHexDigit(c)||c=='_')) return value;
        return value.Length==21 && value.StartsWith("0x",StringComparison.Ordinal) && value[10]=='_'
            && value.AsSpan(11,2).SequenceEqual("0x")
            && uint.TryParse(value.AsSpan(2,8),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out _)
            && uint.TryParse(value.AsSpan(13,8),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out _)
            ?value:"Unknown identity";
    }
}
