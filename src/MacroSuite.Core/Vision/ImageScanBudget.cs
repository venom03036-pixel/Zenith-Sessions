using MacroSuite.Core.Models;

namespace MacroSuite.Core.Vision;

/// <summary>A soft whole-process operating target. It controls cadence, never macro lifetime.</summary>
public sealed class ImageScanBudget
{
    public static ImageScanBudget Shared { get; } = new();
    private readonly object _gate = new();
    private int _floorMs = 10;
    private int _highSamples;
    private ImageScanPerformanceSnapshot _snapshot = new(100, null, null, false, 3);
    private bool _enabled = true;
    public event Action<ImageScanPerformanceSnapshot>? SnapshotChanged;
    public event Action<bool>? EnabledChanged;
    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
        set
        {
            lock (_gate) { if (_enabled == value) return; _enabled = value; if (!value) _floorMs = 10; }
            EnabledChanged?.Invoke(value);
        }
    }
    public ImageScanPerformanceSnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    public int EffectiveInterval(int requested)
    {
        lock (_gate) return Math.Max(Math.Clamp(requested, 10, 10000), _enabled ? _floorMs : 10);
    }

    public void RecordScanInterval(Guid actionId, int requested, double measuredMilliseconds)
    {
        if (!double.IsFinite(measuredMilliseconds) || measuredMilliseconds <= 0) return;
        lock (_gate)
            _snapshot = _snapshot with { MeasuredActionId = actionId,
                MeasuredRequestedIntervalMs = Math.Clamp(requested, 10, 10000), MeasuredIntervalMs = measuredMilliseconds,
                RequestedIntervalMs = Math.Clamp(requested, 10, 10000), EffectiveIntervalMs = EffectiveInterval(requested) };
        // The existing once-per-second sampler publishes this with resource telemetry.
    }

    /// <summary>Called at most once per second by the resource sampler; unavailable GPU is null.</summary>
    public void Observe(double? cpuPercent, double? gpuPercent, int requested, System.Drawing.Size sampledSize)
    {
        ImageScanPerformanceSnapshot snapshot;
        lock (_gate)
        {
            double usage = Math.Max(cpuPercent ?? 0, gpuPercent ?? 0);
            // First remove expensive visuals. Sustained pressure then reduces capture/matching.
            if (_enabled && usage > 3)
            {
                _highSamples++;
                if (_highSamples >= 2)
                    _floorMs = Math.Min(10000, Math.Max(Math.Clamp(requested, 10, 10000), (int)Math.Ceiling(_floorMs * 1.6) + 10));
            }
            else
            {
                _highSamples = 0;
                if (!_enabled) _floorMs = 10;
                else if (usage < 2.4) _floorMs = Math.Max(10, (int)(_floorMs * 0.90));
            }
            snapshot = new ImageScanPerformanceSnapshot(EffectiveInterval(requested), cpuPercent, gpuPercent,
                _enabled && (_floorMs > requested || usage > 3), 3)
                { RequestedIntervalMs = Math.Clamp(requested, 10, 10000), SampledSize = sampledSize, Enabled = _enabled,
                    MeasuredIntervalMs = _snapshot.MeasuredIntervalMs, MeasuredActionId = _snapshot.MeasuredActionId,
                    MeasuredRequestedIntervalMs = _snapshot.MeasuredRequestedIntervalMs };
            _snapshot = snapshot;
        }
        SnapshotChanged?.Invoke(snapshot);
    }
}
