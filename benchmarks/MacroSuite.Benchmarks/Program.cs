using BenchmarkDotNet.Running;

namespace MacroSuite.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--smoke-test", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Executing smoke test of all benchmark routines...");

            var hotPath = new HotPathAllocationBenchmarks();
            hotPath.Setup();
            int compressedCount = hotPath.Benchmark_MouseCompression();
            bool colorMatch = hotPath.Benchmark_ColorDetector_SpanMatching();
            Console.WriteLine($"[PASS] HotPath benchmarks: compressed={compressedCount}, colorMatch={colorMatch}");

            var hook = new HookDispatchBenchmarks();
            hook.Setup();
            bool hookSuccess = hook.Benchmark_HookCallback_StackAllocAndEnqueue();
            Console.WriteLine($"[PASS] Hook dispatch benchmark: success={hookSuccess}");

            var plugin = new PluginDispatchBenchmarks();
            plugin.Setup();
            var pluginResult = plugin.Benchmark_PluginDispatch().GetAwaiter().GetResult();
            Console.WriteLine($"[PASS] Plugin dispatch benchmark: success={pluginResult.Success}");

            Console.WriteLine("All benchmark smoke tests passed successfully!");
            return;
        }

        Console.WriteLine("Running Zenith Macros Performance & Hot-Path Benchmarks...");
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
