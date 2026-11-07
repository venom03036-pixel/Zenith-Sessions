namespace Zenith.Sessions.Core;

/// <summary>
/// Combined process I/O accounting, including files, network and devices. These values do not
/// isolate disk activity, network traffic, paging or hard faults. Null means unknown, never zero.
/// ObservedUtc belongs to the actual counter observation, not a later UI or resource refresh.
/// </summary>
public sealed record ProcessIoRates(DateTimeOffset ObservedUtc, double? ReadBytesPerSecond,
    double? WriteBytesPerSecond, double? OtherBytesPerSecond, double? ReadOperationsPerSecond,
    double? WriteOperationsPerSecond, double? OtherOperationsPerSecond);

/// <summary>Cumulative unsigned accounting counters for one explicitly identified process instance.</summary>
public sealed record ProcessIoCounters(ulong ReadBytes, ulong WriteBytes, ulong OtherBytes,
    ulong ReadOperations, ulong WriteOperations, ulong OtherOperations);

/// <summary>
/// Bounded, memory-only deltas driven by the existing sampler. There are no workers, timers,
/// native reads or retained rate histories here. Elapsed intervals use the monotonic clock.
/// </summary>
public sealed class ProcessIoRateTracker(TimeProvider? timeProvider = null)
{
    public const int MaximumTrackedProcesses = 128;
    public static readonly TimeSpan MinimumSampleInterval = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan MaximumSampleInterval = TimeSpan.FromSeconds(15);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<SessionProcessKey, Baseline> _baselines = new();
    private readonly LinkedList<SessionProcessKey> _order = new();

    public int TrackedProcessCount { get { lock (_gate) return _baselines.Count; } }

    public ProcessIoRates Observe(SessionProcessKey process, ProcessIoCounters counters, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(counters);
        if (!process.IsValid) throw new ArgumentException("Process I/O needs a valid process-instance key.", nameof(process));
        lock (_gate)
        {
            long timestamp = _clock.GetTimestamp(); DateTimeOffset observedUtc = _clock.GetUtcNow();
            _baselines.TryGetValue(process, out var previous);
            ProcessIoRates? rates = null;
            if (previous is not null)
            {
                TimeSpan elapsed = _clock.GetElapsedTime(previous.Timestamp, timestamp);
                if (elapsed >= MinimumSampleInterval && elapsed <= MaximumSampleInterval)
                {
                    double seconds = elapsed.TotalSeconds;
                    rates = new(observedUtc, Rate(counters.ReadBytes, previous.Counters.ReadBytes, seconds),
                        Rate(counters.WriteBytes, previous.Counters.WriteBytes, seconds),
                        Rate(counters.OtherBytes, previous.Counters.OtherBytes, seconds),
                        Rate(counters.ReadOperations, previous.Counters.ReadOperations, seconds),
                        Rate(counters.WriteOperations, previous.Counters.WriteOperations, seconds),
                        Rate(counters.OtherOperations, previous.Counters.OtherOperations, seconds));
                }
            }
            ct.ThrowIfCancellationRequested();
            if (previous is not null)
            {
                previous.Counters = counters; previous.Timestamp = timestamp;
                _order.Remove(previous.Node); _order.AddLast(previous.Node);
            }
            else
            {
                // A reused PID must not resurrect the prior instance's baseline, even if a delayed
                // observation later offers the old key. Host contexts remain independent.
                SessionProcessKey? replaced = null;
                foreach (var key in _baselines.Keys)
                    if (key.ProcessId == process.ProcessId && string.Equals(key.HostId, process.HostId, StringComparison.Ordinal))
                    { replaced = key; break; }
                if (replaced is { } old) Remove(old);
                if (_baselines.Count >= MaximumTrackedProcesses) Remove(_order.First!.Value);
                var node = _order.AddLast(process);
                _baselines.Add(process, new(counters, timestamp, node));
            }
            return rates ?? Unknown(observedUtc);
        }
    }

    /// <summary>A failed/denied/missing observation invalidates its baseline; recovery starts unknown.</summary>
    public void Unavailable(SessionProcessKey process) { lock (_gate) Remove(process); }

    /// <summary>Only the first 128 offered keys are inspected; this never drains an unbounded enumerable.</summary>
    public void Retain(IEnumerable<SessionProcessKey> liveKeys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(liveKeys); ct.ThrowIfCancellationRequested();
        var live = new HashSet<SessionProcessKey>();
        using (var iterator = liveKeys.GetEnumerator())
            for (int index = 0; index < MaximumTrackedProcesses; index++)
            {
                ct.ThrowIfCancellationRequested();
                if (!iterator.MoveNext()) break;
                if (iterator.Current.IsValid) live.Add(iterator.Current);
            }
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var node = _order.First;
            while (node is not null)
            {
                ct.ThrowIfCancellationRequested();
                var next = node.Next;
                if (!live.Contains(node.Value)) Remove(node.Value);
                node = next;
            }
        }
    }

    public void Clear() { lock (_gate) { _baselines.Clear(); _order.Clear(); } }
    private void Remove(SessionProcessKey process)
    { if (_baselines.Remove(process, out var baseline)) _order.Remove(baseline.Node); }
    private static double? Rate(ulong current, ulong previous, double seconds)
        => current >= previous ? (current - previous) / seconds : null;
    private static ProcessIoRates Unknown(DateTimeOffset at) => new(at, null, null, null, null, null, null);
    private sealed class Baseline(ProcessIoCounters counters, long timestamp, LinkedListNode<SessionProcessKey> node)
    {
        public ProcessIoCounters Counters = counters;
        public long Timestamp = timestamp;
        public LinkedListNode<SessionProcessKey> Node { get; } = node;
    }
}
