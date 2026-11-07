using BenchmarkDotNet.Running;

namespace MacroSuite.Benchmarks;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--smoke-test", StringComparison.OrdinalIgnoreCase))
        {
            return RunSmokeTest();
        }

        Console.WriteLine("Running Zenith Macros Performance & Hot-Path Benchmarks...");
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }

    /// <summary>
    /// Runs each benchmark routine once and checks what it returned.
    /// Returns 0 when every check passed and 1 when any check failed.
    /// </summary>
    private static int RunSmokeTest()
    {
        Console.WriteLine("Executing smoke test of all benchmark routines...");
        int failed = 0;

        var hotPath = new HotPathAllocationBenchmarks();
        hotPath.Setup();
        const int inputCount = HotPathAllocationBenchmarks.DenseMouseEventCount;
        int firstCount = hotPath.Benchmark_MouseCompression();
        int secondCount = hotPath.Benchmark_MouseCompression();
        if (!Check(
                firstCount > 0 && firstCount < inputCount && secondCount == firstCount,
                "MouseCompression benchmark",
                $"kept {firstCount} then {secondCount} of {inputCount} moves",
                $"kept {firstCount} then {secondCount} of {inputCount} moves; expected the same count on both runs, more than 0 and fewer than {inputCount}"))
        {
            failed++;
        }

        bool colorMatch = hotPath.Benchmark_ColorDetector_SpanMatching();
        Console.WriteLine($"[INFO] ColorDetector benchmark ran: match={colorMatch} (random frame, so there is no expected value to check)");

        var hook = new HookDispatchBenchmarks();
        hook.Setup();
        bool hookRoundTrip = hook.Benchmark_HookCallback_StackAllocAndEnqueue();
        if (!Check(
                hookRoundTrip,
                "Hook dispatch benchmark",
                "event written to the channel and read back",
                "the event was not written to the channel or could not be read back"))
        {
            failed++;
        }

        var plugin = new PluginDispatchBenchmarks();
        plugin.Setup();
        var pluginResult = plugin.Benchmark_PluginDispatch().GetAwaiter().GetResult();
        if (!Check(
                pluginResult.Success,
                "Plugin dispatch benchmark",
                "step returned success",
                $"step returned failure: {pluginResult.Message}"))
        {
            failed++;
        }

        if (failed == 0)
        {
            Console.WriteLine("All benchmark smoke checks passed.");
            return 0;
        }

        Console.WriteLine($"{failed} benchmark smoke check(s) failed.");
        return 1;
    }

    private static bool Check(bool passed, string name, string passDetail, string failDetail)
    {
        Console.WriteLine(passed ? $"[PASS] {name}: {passDetail}" : $"[FAIL] {name}: {failDetail}");
        return passed;
    }
}
