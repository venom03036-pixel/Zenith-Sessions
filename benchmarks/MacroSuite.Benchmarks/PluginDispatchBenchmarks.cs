using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Extensibility;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using MacroSuite.SDK.Contracts;
using MacroSuite.SDK.Steps;
using DrawingRect = System.Drawing.Rectangle;

namespace MacroSuite.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class PluginDispatchBenchmarks
{
    private sealed class FastBenchmarkStepPlugin : IStepPlugin
    {
        public string PluginId => "bench-plugin";
        public string StepType => "fast-step";
        public string DisplayName => "Benchmark Step";
        public Version PluginVersion => new(1, 0, 0);
        public Version MinHostVersion => new(1, 0, 0);
        public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();

        public Task InitializeAsync(IPluginServices services, CancellationToken ct = default) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;

        public StepDescriptor GetDescriptor() => new()
        {
            StepType = StepType,
            DisplayName = DisplayName
        };

        public ValidationResult Validate(IReadOnlyDictionary<string, object?> parameters) => ValidationResult.Success();

        public Task<StepResult> ExecuteAsync(IPluginExecutionContext context, CancellationToken ct)
        {
            return Task.FromResult(StepResult.Ok());
        }
    }

    private sealed class PermissiveFeatureGate : IFeatureGate
    {
        public bool IsAvailable(FeatureId feature) => true;
        public void Require(FeatureId feature) { }
    }

    private sealed class DummyCapture : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;
        public Task<CaptureFrame> CaptureRegionAsync(DrawingRect region, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public void Dispose() { }
    }

    private PluginLifecycleManager _manager = null!;
    private MacroContext _context = null!;
    private PluginStep _step = null!;

    [GlobalSetup]
    public void Setup()
    {
        var registry = new PluginRegistry();
        registry.RegisterStepPlugin(new FastBenchmarkStepPlugin());

        _manager = new PluginLifecycleManager(registry, new PermissiveFeatureGate());
        var controller = new MacroController();
        _context = new MacroContext(new DummyCapture(), controller)
        {
            PluginHost = _manager
        };

        _step = new PluginStep("bench-plugin", "fast-step");
    }

    [Benchmark(Description = "Phase 9 Spec: Plugin Dispatch Overhead (< 1 ms target)")]
    public async Task<StepResult> Benchmark_PluginDispatch()
    {
        return await _manager.ExecutePluginStepAsync(_step, _context);
    }
}
