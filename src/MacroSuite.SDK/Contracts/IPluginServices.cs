namespace MacroSuite.SDK.Contracts;

/// <summary>
/// Host application services provided to plugins during initialization.
/// </summary>
public interface IPluginServices
{
    /// <summary>Version of the host application runtime.</summary>
    string HostVersion { get; }

    /// <summary>Directory on disk where the plugin may store persistent data.</summary>
    string PluginDataDirectory { get; }

    /// <summary>Checks whether a specific capability or feature is active under the host's license.</summary>
    bool IsFeatureLicensed(string featureName);

    /// <summary>Logs an informational message to the host application log.</summary>
    void LogInfo(string message);

    /// <summary>Logs a warning message to the host application log.</summary>
    void LogWarning(string message);

    /// <summary>Logs an error message to the host application log.</summary>
    void LogError(string message, Exception? ex = null);
}
