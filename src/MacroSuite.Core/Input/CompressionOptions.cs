namespace MacroSuite.Core.Input;

/// <summary>
/// Strategy for sampling high-frequency native mouse movement events during recording.
/// </summary>
public enum MouseSamplingMode
{
    /// <summary>
    /// Samples a new point only when the cursor has moved beyond a minimum distance
    /// threshold or after a minimum time interval has elapsed.
    /// </summary>
    DistanceAndTime,

    /// <summary>
    /// Records start point, end point, duration, and key trajectory via-points
    /// for smooth interpolation during playback.
    /// </summary>
    PathInterpolation
}

/// <summary>
/// Configuration for <see cref="MouseCompressionEngine"/>.
/// All values are validated at construction time.
/// </summary>
public sealed class CompressionOptions
{
    /// <summary>
    /// Default options: DistanceAndTime mode, 4px distance threshold, 16ms time threshold,
    /// direction changes preserved.
    /// </summary>
    public static readonly CompressionOptions Default = new();

    /// <summary>
    /// The sampling strategy to apply to mouse move streams.
    /// Default: <see cref="MouseSamplingMode.DistanceAndTime"/>.
    /// </summary>
    public MouseSamplingMode SamplingMode { get; init; } = MouseSamplingMode.DistanceAndTime;

    /// <summary>
    /// Minimum pixel distance (Euclidean) from the last <em>emitted</em> position to the
    /// current new position before a pending move is eligible for emission.
    /// Must be >= 0. Default: 4.
    /// </summary>
    public int MinDistancePx { get; init; } = 4;

    /// <summary>
    /// Minimum elapsed time in milliseconds from the last <em>emitted</em> event before a
    /// pending move is eligible for emission.
    /// Must be >= 0. Default: 16 (one 60 Hz frame).
    /// </summary>
    public double MinTimeMs { get; init; } = 16.0;

    /// <summary>
    /// When <see langword="true"/>, direction changes at the pending move point are always
    /// emitted regardless of distance/time thresholds, preserving trajectory inflection points.
    /// Default: <see langword="true"/>.
    /// </summary>
    public bool PreserveDirectionChanges { get; init; } = true;

    /// <summary>
    /// Maximum number of intermediate via-points to record per movement segment when using
    /// <see cref="MouseSamplingMode.PathInterpolation"/>. Default: 5.
    /// </summary>
    public int MaxViaPoints { get; init; } = 5;

    /// <summary>
    /// Validates and returns this instance, throwing <see cref="ArgumentOutOfRangeException"/>
    /// if any value is out of range.
    /// </summary>
    public CompressionOptions Validate()
    {
        if (MinDistancePx < 0)
            throw new ArgumentOutOfRangeException(nameof(MinDistancePx), "Must be >= 0.");
        if (MinTimeMs < 0)
            throw new ArgumentOutOfRangeException(nameof(MinTimeMs), "Must be >= 0.");
        if (MaxViaPoints < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxViaPoints), "Must be >= 0.");
        return this;
    }
}
