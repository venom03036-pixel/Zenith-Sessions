namespace MacroSuite.SDK.Contracts;

/// <summary>
/// Result of executing a plugin step.
/// </summary>
public sealed class StepResult
{
    /// <summary>True if execution finished successfully.</summary>
    public bool Success { get; }

    /// <summary>Optional status or error explanation message.</summary>
    public string? Message { get; }

    /// <summary>Recorded execution duration.</summary>
    public TimeSpan Duration { get; }

    private StepResult(bool success, string? message, TimeSpan duration)
    {
        Success = success;
        Message = message;
        Duration = duration;
    }

    /// <summary>Creates a successful step execution result.</summary>
    public static StepResult Ok(string? message = null, TimeSpan duration = default) =>
        new(true, message, duration);

    /// <summary>Creates a failed step execution result.</summary>
    public static StepResult Failed(string message, TimeSpan duration = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new(false, message, duration);
    }
}
