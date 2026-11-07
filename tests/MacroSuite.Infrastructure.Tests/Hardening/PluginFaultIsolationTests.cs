using FluentAssertions;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Extensibility;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using MacroSuite.SDK.Contracts;
using MacroSuite.SDK.Steps;
using Xunit;
using DrawingRect = System.Drawing.Rectangle;

namespace MacroSuite.Infrastructure.Tests.Hardening;

public class PluginFaultIsolationTests
{
    private sealed class FaultyStepPlugin : IStepPlugin
    {
        public string PluginId => "faulty-plugin";
        public string StepType => "crash-step";
        public string DisplayName => "Crashing Step Plugin";
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
            throw new InvalidOperationException("Fatal simulated hardware driver crash in plugin DLL!");
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

    [Fact]
    public async Task FaultingPlugin_DoesNotCrashHost_ReturnsGracefulFailureResult()
    {
        var registry = new PluginRegistry();
        var plugin = new FaultyStepPlugin();
        registry.RegisterStepPlugin(plugin);

        var manager = new PluginLifecycleManager(registry, new PermissiveFeatureGate());
        var controller = new MacroController();
        var context = new MacroContext(new DummyCapture(), controller)
        {
            PluginHost = manager
        };

        var step = new PluginStep("faulty-plugin", "crash-step");

        var result = await manager.ExecutePluginStepAsync(step, context);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("threw an unhandled exception");
        result.Message.Should().Contain("Fatal simulated hardware driver crash");
    }

    [Fact]
    public async Task UnregisteredPlugin_ReturnsHelpfulDiagnostic_WithoutThrowing()
    {
        var registry = new PluginRegistry();
        var manager = new PluginLifecycleManager(registry, new PermissiveFeatureGate());
        var controller = new MacroController();
        var context = new MacroContext(new DummyCapture(), controller)
        {
            PluginHost = manager
        };

        var step = new PluginStep("nonexistent-plugin", "missing-step");

        var result = await manager.ExecutePluginStepAsync(step, context);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("not registered or loaded");
    }
}
