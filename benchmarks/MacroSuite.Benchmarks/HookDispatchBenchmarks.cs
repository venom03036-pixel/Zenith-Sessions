using System.Diagnostics;
using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using MacroSuite.Core.Models;

namespace MacroSuite.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class HookDispatchBenchmarks
{
    private Channel<InputEvent> _channel = null!;

    [GlobalSetup]
    public void Setup()
    {
        _channel = Channel.CreateBounded<InputEvent>(new BoundedChannelOptions(100_000)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    [Benchmark(Description = "Subsystem Rule: Low-Level Hook Event Enqueue + Drain (steady-state channel, includes TryRead)")]
    public bool Benchmark_HookCallback_StackAllocAndEnqueue()
    {
        // Target: zero allocation and well under 1 ms per op. This is not asserted here;
        // read the Allocated and Mean columns of the MemoryDiagnoser output.
        // The read drains the event just written so the bounded channel stays near empty.
        // Without it the channel fills after 100,000 ops, every later write measures the
        // drop-oldest eviction path, and TryWrite returns true whether or not anything was queued.
        long ticks = Stopwatch.GetTimestamp();
        var inputEvent = new InputEvent(InputEventType.MouseMove, ticks, 500, 400, 0, 0);
        return _channel.Writer.TryWrite(inputEvent) && _channel.Reader.TryRead(out _);
    }
}
