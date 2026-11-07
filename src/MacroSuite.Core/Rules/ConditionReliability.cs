namespace MacroSuite.Core.Rules;

/// <summary>
/// Shared wait/confirmation policy for visual and color conditions.
/// Confirmations, absence confirmations, and stability windows are enforced by
/// <see cref="ConditionWaitEvaluator"/> during waits.
/// </summary>
public enum ConditionFailurePolicy
{
    Stop = 0,
    Skip = 1,
    Retry = 2,
    /// <summary>Use the owning block's default continuation. Persisted legacy values are unchanged.</summary>
    Default = 3
}

/// <summary>Clamps persisted reliability fields so old constructors keep working.</summary>
public static class ConditionReliability
{
    public const int DefaultPollIntervalMs = 50;
    public const int DefaultVisualPollIntervalMs = 150;
    public const int DefaultConfirmations = 1;
    public const int DefaultTimeoutMs = 10_000;
    public const int MinPollIntervalMs = 10;
    public const int MaxPollIntervalMs = 10_000;
    public const int MaxConfirmations = 100;
    public const int MaxSampleStride = 64;

    public static int ClampPollIntervalMs(int milliseconds, int fallback = DefaultPollIntervalMs)
        => Math.Clamp(milliseconds <= 0 ? fallback : milliseconds, MinPollIntervalMs, MaxPollIntervalMs);

    public static int ClampConfirmations(int count)
        => Math.Clamp(count <= 0 ? DefaultConfirmations : count, 1, MaxConfirmations);

    public static int ClampTimeoutMs(int milliseconds)
        => Math.Max(0, milliseconds);

    public static int ClampStabilityMs(int milliseconds)
        => Math.Max(0, milliseconds);

    public static int ClampSampleStride(int stride)
        => Math.Clamp(stride <= 0 ? 1 : stride, 1, MaxSampleStride);

    public static ConditionFailurePolicy FromVisualTimeout(string? onTimeout)
        => onTimeout?.ToLowerInvariant() switch
        {
            "skip" => ConditionFailurePolicy.Skip,
            "retry" => ConditionFailurePolicy.Retry,
            _ => ConditionFailurePolicy.Stop
        };

    public static string ToVisualTimeout(ConditionFailurePolicy policy)
        => policy switch
        {
            ConditionFailurePolicy.Skip => "Skip",
            ConditionFailurePolicy.Retry => "Retry",
            _ => "Stop"
        };

    public static Vision.VisualTimeoutBehavior ToVisualTimeoutBehavior(ConditionFailurePolicy policy)
        => policy switch
        {
            ConditionFailurePolicy.Skip => Vision.VisualTimeoutBehavior.Skip,
            ConditionFailurePolicy.Retry => Vision.VisualTimeoutBehavior.Retry,
            _ => Vision.VisualTimeoutBehavior.Stop
        };

    public static ConditionFailurePolicy FromVisualTimeoutBehavior(Vision.VisualTimeoutBehavior behavior)
        => behavior switch
        {
            Vision.VisualTimeoutBehavior.Skip => ConditionFailurePolicy.Skip,
            Vision.VisualTimeoutBehavior.Retry => ConditionFailurePolicy.Retry,
            _ => ConditionFailurePolicy.Stop
        };
}
