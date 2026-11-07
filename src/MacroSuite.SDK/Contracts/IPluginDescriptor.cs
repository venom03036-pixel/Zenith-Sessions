namespace MacroSuite.SDK.Contracts;

/// <summary>
/// Root contract implemented by all MacroSuite plugins.
/// </summary>
public interface IPluginDescriptor
{
    /// <summary>Globally unique alphanumeric identifier (e.g., 'vendor.step.database').</summary>
    string PluginId { get; }

    /// <summary>Human-readable display title.</summary>
    string DisplayName { get; }

    /// <summary>SemVer version of this plugin assembly.</summary>
    Version PluginVersion { get; }

    /// <summary>Minimum required version of the MacroSuite host application.</summary>
    Version MinHostVersion { get; }

    /// <summary>Names of required licensed capabilities (e.g., 'PluginExecution', 'AdvancedConditions').</summary>
    IReadOnlyList<string> RequiredFeatures { get; }

    /// <summary>
    /// Called once after assembly loading to initialize plugin state.
    /// </summary>
    Task InitializeAsync(IPluginServices services, CancellationToken ct = default);

    /// <summary>
    /// Called when the host application unloads or shuts down.
    /// </summary>
    Task ShutdownAsync(CancellationToken ct = default);
}
