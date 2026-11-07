namespace MacroSuite.Core.Abstractions;

/// <summary>
/// Service contract for querying system display hardware and monitor refresh rates.
/// Isolates Win32 display enumeration behind a testable abstraction (Rule 3).
/// </summary>
public interface IDisplayInfoService
{
    /// <summary>
    /// Gets the highest refresh rate (in Hz/FPS) supported across all active monitors,
    /// or from the primary display. Defaults to at least 60.
    /// </summary>
    int GetMaxMonitorRefreshRate();

    /// <summary>
    /// Gets the current active refresh rate of the primary monitor.
    /// </summary>
    int GetPrimaryMonitorRefreshRate();
}
