using MacroSuite.Core.Execution;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Steps;

/// <summary>
/// Executes a third-party plugin step registered through the SDK (Phase 6).
/// </summary>
public sealed class PluginStep : MacroStep
{
    /// <summary>Identifier of the plugin providing this step.</summary>
    public string PluginId { get; init; } = string.Empty;

    /// <summary>Custom step type name within the plugin.</summary>
    public string StepType { get; init; } = string.Empty;

    /// <summary>Custom configuration parameters for this plugin step.</summary>
    public Dictionary<string, object?> Parameters { get; init; } = new();

    public PluginStep()
    {
        Label = "Plugin Step";
    }

    public PluginStep(string pluginId, string stepType, Dictionary<string, object?>? parameters = null)
    {
        PluginId = pluginId ?? string.Empty;
        StepType = stepType ?? string.Empty;
        Parameters = parameters ?? new();
        Label = $"Plugin: {pluginId}/{stepType}";
    }

    /// <inheritdoc/>
    public override async Task ExecuteAsync(MacroContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!IsEnabled || context.IsAborted || ct.IsCancellationRequested)
        {
            return;
        }

        if (context.FeatureGate is null)
            throw new FeatureNotLicensedException(FeatureId.PluginExecution);
        context.FeatureGate.Require(FeatureId.PluginExecution);

        if (context.PluginHost is null)
        {
            throw new InvalidOperationException($"Cannot execute plugin step '{PluginId}/{StepType}': no IPluginHost is registered in MacroContext.");
        }

        var result = await context.PluginHost.ExecutePluginStepAsync(this, context, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Plugin step '{PluginId}/{StepType}' failed: {result.Message}");
        }
    }
}
