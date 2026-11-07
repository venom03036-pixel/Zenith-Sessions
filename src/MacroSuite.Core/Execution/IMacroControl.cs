namespace MacroSuite.Core.Execution;

/// <summary>
/// Triple-tier macro execution controller (Pause, Stop, Panic) per Constitution Rule 11.
/// Panic stop is absolute and checked at every boundary.
/// </summary>
public interface IMacroControl : IDisposable
{
    /// <summary>Whether macro execution is currently paused.</summary>
    bool IsPaused { get; }

    /// <summary>Total monotonic time paused, including a current pause; used by recorded playback clocks.</summary>
    TimeSpan TotalPausedDuration => TimeSpan.Zero;

    /// <summary>Pauses execution at the next step boundary, preserving runtime state.</summary>
    Task PauseAsync(CancellationToken ct = default);

    /// <summary>Resumes execution from a paused state.</summary>
    Task ResumeAsync();

    /// <summary>Blocks if paused until resumed or cancelled.</summary>
    Task WaitIfPausedAsync(CancellationToken ct = default);

    /// <summary>Signals normal cooperative cancellation and awaits graceful completion.</summary>
    Task StopAsync();

    /// <summary>Cancellation token signaled on normal stop request.</summary>
    CancellationToken StopToken { get; }

    /// <summary>
    /// Immediate, unconditional emergency stop.
    /// Signals the panic token and synthesizes key-up events for all held keys (Rule 11).
    /// </summary>
    void Panic();

    /// <summary>Cancellation token signaled immediately upon panic stop.</summary>
    CancellationToken PanicToken { get; }

    /// <summary>True if emergency panic stop was triggered.</summary>
    bool IsPanicTriggered { get; }
}
