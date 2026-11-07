namespace MacroSuite.SDK.Contracts;

/// <summary>
/// Execution scope provided to a plugin step or condition during macro playback.
/// Isolates plugins from internal engine types while preserving cancellation and logging.
/// </summary>
public interface IPluginExecutionContext
{
    /// <summary>Unique execution identifier for the overall macro run.</summary>
    Guid RunId { get; }

    /// <summary>Unique identifier for the specific step being executed.</summary>
    Guid StepId { get; }

    /// <summary>Identifier of the plugin executing.</summary>
    string PluginId { get; }

    /// <summary>Specific step type name.</summary>
    string StepType { get; }

    /// <summary>Read-only dictionary of authored step configuration parameters.</summary>
    IReadOnlyDictionary<string, object?> Parameters { get; }

    /// <summary>Variables scoped to the current execution run.</summary>
    IPluginVariables Variables { get; }

    /// <summary>
    /// Cancellation token signaled on pause/stop or emergency panic (Rule 11, 12).
    /// Plugins must check this token frequently and terminate promptly upon cancellation.
    /// </summary>
    CancellationToken CancellationToken { get; }

    /// <summary>Logs an informational message to the host execution log.</summary>
    void LogInfo(string message);

    /// <summary>Logs a warning message to the host execution log.</summary>
    void LogWarning(string message);

    /// <summary>Logs an error message to the host execution log.</summary>
    void LogError(string message, Exception? ex = null);
}
