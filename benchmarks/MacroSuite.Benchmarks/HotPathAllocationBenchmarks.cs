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
    /// <summary>Number of mouse move events fed to the compression benchmark per op.</summary>
    public const int DenseMouseEventCount = 10_000;

    private InputEvent[] _denseMouseEvents = null!;
    private byte[] _rawScreenBuffer = null!;
    private MouseCompressionEngine _compressionEngine = null!;
    private List<InputEvent> _compressionOutput = null!;

    [GlobalSetup]
    public void Setup()
    {
        // 10,000 sequential mouse movement events
        _denseMouseEvents = new InputEvent[DenseMouseEventCount];
        long ticks = 1000;
        for (int i = 0; i < DenseMouseEventCount; i++)
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

        // Built once so the benchmark measures Process and Flush only. A new engine and a new
        // 10,000-event list per op cost about 320 KB (the list lands on the large object heap),
        // which hid whether the engine itself allocates.
        _compressionEngine = new MouseCompressionEngine(new CompressionOptions
        {
            MinDistancePx = 5,
            MinTimeMs = 15,
            PreserveDirectionChanges = true
        });
        _compressionOutput = new List<InputEvent>(DenseMouseEventCount);
    }

    [Benchmark(Description = "Rule 7 Hot-Path: MouseCompressionEngine 10,000 events")]
    public int Benchmark_MouseCompression()
    {
        // Reset and Clear return to a fresh-engine start without allocating.
        // MouseCompressionEngineTests checks that a reset engine matches a new one.
        _compressionEngine.Reset();
        _compressionOutput.Clear();

        for (int i = 0; i < _denseMouseEvents.Length; i++)
        {
            _compressionEngine.Process(in _denseMouseEvents[i], _compressionOutput);
        }
        _compressionEngine.Flush(_compressionOutput);

        return _compressionOutput.Count;
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
