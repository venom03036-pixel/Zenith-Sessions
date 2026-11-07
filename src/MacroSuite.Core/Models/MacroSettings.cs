namespace MacroSuite.Core.Models;

/// <summary>
/// Execution and playback configuration for a <see cref="MacroModel"/>.
/// </summary>
public sealed class MacroSettings
{
    /// <summary>Explicit run target; null preserves legacy step/window behavior.</summary>
    public MacroTargetRule? Target { get; set; }

    /// <summary>
    /// Maximum runtime duration allowed before execution is automatically aborted.
    /// <see cref="TimeSpan.Zero"/> or negative disables the cap.
    /// Ignored when <see cref="RepeatUntilStopped"/> is true — Stop / Panic end the run.
    /// </summary>
    public TimeSpan MaxRuntime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Maximum iterations allowed for repeat executions and unbounded loops.
    /// Ignored for the outer playback loop when <see cref="RepeatUntilStopped"/> is true.
    /// </summary>
    public int MaxIterations { get; set; } = 100_000;

    /// <summary>Maximum allowable loop nesting depth.</summary>
    public int MaxLoopDepth { get; set; } = 8;

    /// <summary>Playback speed multiplier (e.g. 1.0 = normal, 2.0 = 2x speed).</summary>
    public double PlaybackSpeed { get; set; } = 1.0;

    /// <summary>Number of times to repeat execution of the macro sequence (1 = run once).</summary>
    public int RepeatCount { get; set; } = 1;

    /// <summary>
    /// When true, repeats continuously until Stop or Panic.
    /// Disables <see cref="MaxRuntime"/> and the outer <see cref="MaxIterations"/> ceiling.
    /// </summary>
    public bool RepeatUntilStopped { get; set; } = false;

    /// <summary>Hotkey trigger assigned to this macro (e.g. "F6", ".", "Ctrl+2").</summary>
    public string Hotkey { get; set; } = string.Empty;

    /// <summary>Mouse movement sampling strategy during recording.</summary>
    public Input.MouseSamplingMode MouseSamplingMode { get; set; } = Input.MouseSamplingMode.DistanceAndTime;

    /// <summary>Whether this macro is active for execution.</summary>
    public bool IsActive { get; set; } = true;
}
