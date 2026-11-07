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

namespace MacroSuite.Core.Tests.Extensibility;

public sealed class PluginLifecycleTests
{
    private sealed class MockStepPlugin : IStepPlugin
    {
        public string PluginId => "test.vendor.sample";
        public string DisplayName => "Sample Test Plugin";
        public Version PluginVersion => new(1, 0, 0);
        public Version MinHostVersion => new(1, 0, 0);
        public IReadOnlyList<string> RequiredFeatures => new[] { "PluginExecution" };

        public bool Executed { get; private set; }
        public bool ThrowsOnExecute { get; set; }
        public bool FailsValidation { get; set; }
        public int DelayMs { get; set; }

        public StepDescriptor GetDescriptor() => new()
        {
            StepType = "sample_action",
            DisplayName = "Sample Action"
        };

        public async Task<StepResult> ExecuteAsync(IPluginExecutionContext context, CancellationToken ct)
        {
            if (ThrowsOnExecute)
            {
                throw new InvalidOperationException("Fatal crash inside third-party plugin code!");
            }

            if (DelayMs > 0)
            {
                await Task.Delay(DelayMs, ct);
            }

            Executed = true;
            context.Variables.Set("PluginOutput", 42);
            return StepResult.Ok("Action completed successfully.");
        }

        public ValidationResult Validate(IReadOnlyDictionary<string, object?> parameters)
        {
            if (FailsValidation)
            {
                return ValidationResult.Failure("Custom validation rule failed.");
            }

            return ValidationResult.Success();
        }

        public Task InitializeAsync(IPluginServices services, CancellationToken ct = default) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FaultyInitPlugin : IPluginDescriptor
    {
        public string PluginId => "faulty.init";
        public string DisplayName => "Faulty";
        public Version PluginVersion => new(1, 0);
        public Version MinHostVersion => new(1, 0);
        public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();

        public Task InitializeAsync(IPluginServices services, CancellationToken ct = default) =>
            throw new DllNotFoundException("Native dependent library missing!");

        public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static (PluginLifecycleManager Manager, PluginRegistry Registry, MockStepPlugin Plugin) CreateSetup(bool isLicensed = true)
    {
        var registry = new PluginRegistry();
        var license = new LicenseInfo
        {
            LicenseKey = "TEST-KEY",
            Type = LicenseType.Enterprise,
            LicensedFeatures = isLicensed
                ? new HashSet<FeatureId> { FeatureId.PluginExecution, FeatureId.BasicPlayback }
                : new HashSet<FeatureId>()
        };

        var licenseService = new MockLicenseService(license);
        var gate = new FeatureGate(licenseService);
        var manager = new PluginLifecycleManager(registry, gate);
        var plugin = new MockStepPlugin();
        registry.RegisterStepPlugin(plugin);

        return (manager, registry, plugin);
    }

    private sealed class MockLicenseService : ILicenseService
    {
        private readonly LicenseInfo _license;
        public event EventHandler<LicenseChangedEventArgs>? LicenseChanged { add { } remove { } }

        public MockLicenseService(LicenseInfo license) => _license = license;
        public LicenseInfo GetCurrentLicense() => _license;
        public Task<LicenseValidationResult> ValidateAsync(CancellationToken ct = default) =>
            Task.FromResult(LicenseValidationResult.Success(_license));
        public bool IsFeatureLicensed(FeatureId feature) => _license.IsFeatureLicensed(feature);
    }

    private sealed class DummyScreenCapture : IScreenCaptureService
    {
        public CaptureSource CurrentSource => CaptureSource.Gdi;
        public bool IsSupported => true;
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<CaptureFrame> CaptureRegionAsync(System.Drawing.Rectangle region, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public void Dispose() { }
    }

    [Fact]
    public async Task RegisterAndExecute_ValidStepPlugin_ExecutesSuccessfullyAndUpdatesVariables()
    {
        var (manager, _, plugin) = CreateSetup(isLicensed: true);
        var context = new MacroContext(new DummyScreenCapture())
        {
            PluginHost = manager
        };

        var step = new PluginStep("test.vendor.sample", "sample_action");
        var result = await manager.ExecutePluginStepAsync(step, context);

        result.Success.Should().BeTrue();
        plugin.Executed.Should().BeTrue();
        context.Variables.Get<int>("PluginOutput").Should().Be(42);
    }

    [Fact]
    public async Task ExecutePluginStepAsync_WhenFeatureNotLicensed_ThrowsFeatureNotLicensedException()
    {
        var (manager, _, _) = CreateSetup(isLicensed: false);
        var context = new MacroContext(new DummyScreenCapture())
        {
            PluginHost = manager
        };

        var step = new PluginStep("test.vendor.sample", "sample_action");
        var act = () => manager.ExecutePluginStepAsync(step, context);

        await act.Should().ThrowAsync<FeatureNotLicensedException>()
            .Where(ex => ex.Feature == FeatureId.PluginExecution);
    }

    [Fact]
    public async Task ExecutePluginStepAsync_WhenParameterValidationFails_ReturnsFailureWithoutCallingExecute()
    {
        var (manager, _, plugin) = CreateSetup(isLicensed: true);
        plugin.FailsValidation = true;

        var context = new MacroContext(new DummyScreenCapture())
        {
            PluginHost = manager
        };

        var step = new PluginStep("test.vendor.sample", "sample_action");
        var result = await manager.ExecutePluginStepAsync(step, context);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Custom validation rule failed");
        plugin.Executed.Should().BeFalse();
    }

    [Fact]
    public async Task ExecutePluginStepAsync_WhenPluginThrowsException_IsolatesFaultAndReturnsFailureWithoutCrashingHost()
    {
        var (manager, _, plugin) = CreateSetup(isLicensed: true);
        plugin.ThrowsOnExecute = true;

        var context = new MacroContext(new DummyScreenCapture())
        {
            PluginHost = manager
        };

        var step = new PluginStep("test.vendor.sample", "sample_action");

        // Rule 18 Soft Isolation: Third-party exceptions must never crash the host
        var result = await manager.ExecutePluginStepAsync(step, context);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Fatal crash inside third-party plugin code");
    }

    [Fact]
    public async Task ExecutePluginStepAsync_WhenPanicTriggered_TerminatesImmediately()
    {
        var (manager, _, plugin) = CreateSetup(isLicensed: true);
        plugin.DelayMs = 2000;

        var controller = new MacroController();
        var context = new MacroContext(new DummyScreenCapture(), controller)
        {
            PluginHost = manager
        };

        var step = new PluginStep("test.vendor.sample", "sample_action");

        var executeTask = manager.ExecutePluginStepAsync(step, context);

        // Signal panic after brief start
        await Task.Delay(50);
        controller.Panic();

        var result = await executeTask;
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("canceled by stop or emergency panic");
    }

    [Fact]
    public async Task InitializePluginAsync_FaultyPlugin_CatchesExceptionAndHostContinues()
    {
        var (manager, registry, _) = CreateSetup(isLicensed: true);
        var faulty = new FaultyInitPlugin();

        bool success = await manager.InitializePluginAsync(faulty);

        success.Should().BeFalse();
        registry.GetAllPlugins().Should().NotContain(faulty);
    }

    [Fact]
    public async Task DefaultPluginServices_ConsultsFeatureGate()
    {
        var probe = new LicenseProbePlugin();
        var (licensedManager, _, _) = CreateSetup(isLicensed: true);
        (await licensedManager.InitializePluginAsync(probe)).Should().BeTrue();
        probe.Licensed.Should().BeTrue();

        var unlicensedProbe = new LicenseProbePlugin();
        var (unlicensedManager, _, _) = CreateSetup(isLicensed: false);
        (await unlicensedManager.InitializePluginAsync(unlicensedProbe)).Should().BeTrue();
        unlicensedProbe.Licensed.Should().BeFalse();
    }

    [Fact]
    public async Task PluginStep_WithoutFeatureGate_FailsClosed()
    {
        using var capture = new DummyScreenCapture();
        var context = new MacroContext(capture);
        var step = new PluginStep("test.vendor.sample", "sample_action");
        var act = () => step.ExecuteAsync(context);
        await act.Should().ThrowAsync<FeatureNotLicensedException>()
            .Where(ex => ex.Feature == FeatureId.PluginExecution);
    }

    private sealed class LicenseProbePlugin : IPluginDescriptor
    {
        public string PluginId => "probe.license";
        public string DisplayName => "Probe";
        public Version PluginVersion => new(1, 0);
        public Version MinHostVersion => new(1, 0);
        public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();
        public bool? Licensed { get; private set; }

        public Task InitializeAsync(IPluginServices services, CancellationToken ct = default)
        {
            Licensed = services.IsFeatureLicensed("PluginExecution");
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
