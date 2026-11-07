using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Core.Vision;

namespace MacroSuite.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class HotPathAllocationBenchmarks
{
    private InputEvent[] _denseMouseEvents = null!;
    private byte[] _rawScreenBuffer = null!;

    [GlobalSetup]
    public void Setup()
    {

        // 10,000 sequential mouse movement events
        _denseMouseEvents = new InputEvent[10_000];
        long ticks = 1000;
        for (int i = 0; i < 10_000; i++)
        {
            _denseMouseEvents[i] = new InputEvent(
                InputEventType.MouseMove,
                ticks += 10,
                100 + (i % 200),
                200 + (i % 200),
                0,
                0);
        }

        // 1920x1080 BGRA buffer (approx 8.3 MB)
        _rawScreenBuffer = new byte[1920 * 1080 * 4];
        new Random(42).NextBytes(_rawScreenBuffer);
    }

    [Benchmark(Description = "Rule 7 Hot-Path: MouseCompressionEngine 10,000 events")]
    public int Benchmark_MouseCompression()
    {
        var options = new CompressionOptions
        {
            MinDistancePx = 5,
            MinTimeMs = 15,
            PreserveDirectionChanges = true
        };

        var engine = new MouseCompressionEngine(options);
        var output = new List<InputEvent>(10_000);

        for (int i = 0; i < _denseMouseEvents.Length; i++)
        {
            engine.Process(in _denseMouseEvents[i], output);
        }
        engine.Flush(output);

        return output.Count;
    }

    [Benchmark(Description = "Rule 7 Hot-Path: ColorDetector Integer Span Evaluation")]
    public bool Benchmark_ColorDetector_SpanMatching()
    {
        // Sample pixel at (500, 500), stride 1920 * 4
        int offset = (500 * (1920 * 4)) + (500 * 4);
        return ColorDetector.IsMatch(
            _rawScreenBuffer,
            offset,
            FramePixelFormat.Bgra32,
            r: 128,
            g: 128,
            b: 128,
            tolerance: 10);
    }
}
