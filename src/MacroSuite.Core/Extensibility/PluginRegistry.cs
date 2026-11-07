using System.Collections.Concurrent;
using MacroSuite.SDK.AI;
using MacroSuite.SDK.Conditions;
using MacroSuite.SDK.Contracts;
using MacroSuite.SDK.Steps;

namespace MacroSuite.Core.Extensibility;

/// <summary>
/// Thread-safe registry maintaining all currently loaded and registered plugins.
/// </summary>
public sealed class PluginRegistry
{
    private readonly ConcurrentDictionary<string, IPluginDescriptor> _pluginsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IStepPlugin> _stepPlugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IConditionPlugin> _conditionPlugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IAIProviderPlugin> _aiProviders = new(StringComparer.OrdinalIgnoreCase);

    private static string BuildStepKey(string pluginId, string stepType) => $"{pluginId}::{stepType}";
    private static string BuildConditionKey(string pluginId, string conditionType) => $"{pluginId}::{conditionType}";

    /// <summary>Registers a step plugin and its metadata.</summary>
    public void RegisterStepPlugin(IStepPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var desc = plugin.GetDescriptor();
        _pluginsById[plugin.PluginId] = plugin;
        _stepPlugins[BuildStepKey(plugin.PluginId, desc.StepType)] = plugin;
    }

    /// <summary>Registers a condition plugin and its metadata.</summary>
    public void RegisterConditionPlugin(IConditionPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var desc = plugin.GetDescriptor();
        _pluginsById[plugin.PluginId] = plugin;
        _conditionPlugins[BuildConditionKey(plugin.PluginId, desc.ConditionType)] = plugin;
    }

    /// <summary>Registers an AI provider plugin.</summary>
    public void RegisterAIProviderPlugin(IAIProviderPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        _pluginsById[plugin.PluginId] = plugin;
        _aiProviders[plugin.PluginId] = plugin;
    }

    /// <summary>Unregisters all extensions provided by a plugin identifier.</summary>
    public void UnregisterPlugin(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        _pluginsById.TryRemove(pluginId, out _);
        _aiProviders.TryRemove(pluginId, out _);

        foreach (var key in _stepPlugins.Keys.Where(k => k.StartsWith($"{pluginId}::", StringComparison.OrdinalIgnoreCase)))
        {
            _stepPlugins.TryRemove(key, out _);
        }

        foreach (var key in _conditionPlugins.Keys.Where(k => k.StartsWith($"{pluginId}::", StringComparison.OrdinalIgnoreCase)))
        {
            _conditionPlugins.TryRemove(key, out _);
        }
    }

    /// <summary>Retrieves a step plugin for the specified plugin and step type.</summary>
    public IStepPlugin? TryGetStepPlugin(string pluginId, string stepType)
    {
        _stepPlugins.TryGetValue(BuildStepKey(pluginId, stepType), out var plugin);
        return plugin;
    }

    /// <summary>Retrieves a condition plugin for the specified plugin and condition type.</summary>
    public IConditionPlugin? TryGetConditionPlugin(string pluginId, string conditionType)
    {
        _conditionPlugins.TryGetValue(BuildConditionKey(pluginId, conditionType), out var plugin);
        return plugin;
    }

    /// <summary>Retrieves an AI provider plugin by identifier.</summary>
    public IAIProviderPlugin? TryGetAIProviderPlugin(string pluginId)
    {
        _aiProviders.TryGetValue(pluginId, out var plugin);
        return plugin;
    }

    /// <summary>Gets all registered plugins.</summary>
    public IReadOnlyList<IPluginDescriptor> GetAllPlugins() => _pluginsById.Values.ToList();

    /// <summary>Gets all registered step plugins.</summary>
    public IReadOnlyList<IStepPlugin> GetAllStepPlugins() => _stepPlugins.Values.ToList();

    /// <summary>Clears all registrations.</summary>
    public void Clear()
    {
        _pluginsById.Clear();
        _stepPlugins.Clear();
        _conditionPlugins.Clear();
        _aiProviders.Clear();
    }
}
