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

    [Benchmark(Description = "Subsystem Rule: Low-Level Hook Stack Allocation & Channel Dispatch")]
    public bool Benchmark_HookCallback_StackAllocAndEnqueue()
    {
        // Must be zero-allocation and well under 1ms (< 1,000,000 ns)
        long ticks = Stopwatch.GetTimestamp();
        var inputEvent = new InputEvent(InputEventType.MouseMove, ticks, 500, 400, 0, 0);
        return _channel.Writer.TryWrite(inputEvent);
    }
}
