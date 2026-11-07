using MacroSuite.Core.Execution;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Steps;
using MacroSuite.SDK.Contracts;
using MacroSuite.SDK.Steps;

namespace MacroSuite.Core.Extensibility;

/// <summary>
/// Manages the full lifecycle of plugins (discovery, manifest validation, initialization, step dispatch, and shutdown).
/// Strictly isolates the host engine against third-party plugin failures (Rule 18).
/// </summary>
public sealed class PluginLifecycleManager : IPluginHost
{
    private readonly PluginRegistry _registry;
    private readonly IFeatureGate _featureGate;
    private readonly IPluginServices? _pluginServices;

    public PluginRegistry Registry => _registry;

    public PluginLifecycleManager(
        PluginRegistry registry,
        IFeatureGate featureGate,
        IPluginServices? pluginServices = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _featureGate = featureGate ?? throw new ArgumentNullException(nameof(featureGate));
        _pluginServices = pluginServices;
    }

    /// <inheritdoc/>
    public async Task<StepResult> ExecutePluginStepAsync(PluginStep step, MacroContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(context);

        // 1. Mandatory licensing enforcement boundary
        _featureGate.Require(FeatureId.PluginExecution);

        // 2. Lookup step plugin
        var plugin = _registry.TryGetStepPlugin(step.PluginId, step.StepType);
        if (plugin is null)
        {
            return StepResult.Failed($"Plugin '{step.PluginId}' step type '{step.StepType}' is not registered or loaded.");
        }

        // 3. Parameter schema validation
        var validation = plugin.Validate(step.Parameters);
        if (!validation.IsValid)
        {
            return StepResult.Failed($"Parameter validation failed for step '{step.StepType}': {validation.ErrorMessage}");
        }

        // 4. Create isolated execution context with panic token linking (Rule 11, 12)
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, context.PanicToken, context.StopToken);
        var execContext = new PluginExecutionContextAdapter(step, context, linkedCts.Token);

        // 5. Execute with full exception isolation (Rule 18)
        try
        {
            var result = await plugin.ExecuteAsync(execContext, linkedCts.Token).ConfigureAwait(false);
            return result ?? StepResult.Ok();
        }
        catch (OperationCanceledException) when (context.IsAborted || ct.IsCancellationRequested)
        {
            return StepResult.Failed("Step was canceled by stop or emergency panic.");
        }
        catch (Exception ex)
        {
            // Catch, log, and isolate — never let third-party plugins crash the executor
            return StepResult.Failed($"Plugin step '{step.PluginId}/{step.StepType}' threw an unhandled exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Initializes and registers a plugin into the host suite.
    /// Safely isolates initialization faults.
    /// </summary>
    public async Task<bool> InitializePluginAsync(IPluginDescriptor plugin, IPluginServices? services = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        try
        {
            var effectiveServices = services ?? _pluginServices ?? new DefaultPluginServices(_featureGate);
            await plugin.InitializeAsync(effectiveServices, ct).ConfigureAwait(false);

            if (plugin is IStepPlugin stepPlugin)
            {
                _registry.RegisterStepPlugin(stepPlugin);
            }
            else if (plugin is SDK.Conditions.IConditionPlugin conditionPlugin)
            {
                _registry.RegisterConditionPlugin(conditionPlugin);
            }
            else if (plugin is SDK.AI.IAIProviderPlugin aiPlugin)
            {
                _registry.RegisterAIProviderPlugin(aiPlugin);
            }

            return true;
        }
        catch
        {
            // Faulted plugin initialization is isolated and host continues
            return false;
        }
    }

    /// <summary>
    /// Gracefully shuts down all active plugins.
    /// </summary>
    public async Task ShutdownAllAsync(CancellationToken ct = default)
    {
        var allPlugins = _registry.GetAllPlugins();
        foreach (var plugin in allPlugins)
        {
            try
            {
                await plugin.ShutdownAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // Isolate shutdown failures
            }
        }

        _registry.Clear();
    }

    private sealed class DefaultPluginServices : IPluginServices
    {
        private readonly IFeatureGate _featureGate;

        public DefaultPluginServices(IFeatureGate featureGate)
        {
            _featureGate = featureGate;
        }

        public string HostVersion => "1.0.0";
        public string PluginDataDirectory => AppDomain.CurrentDomain.BaseDirectory;
        public bool IsFeatureLicensed(string featureName)
            => Enum.TryParse(featureName, ignoreCase: true, out FeatureId id)
               && _featureGate.IsAvailable(id);
        public void LogInfo(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message, Exception? ex = null) { }
    }
}
