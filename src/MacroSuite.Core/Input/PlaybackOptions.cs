namespace MacroSuite.Core.Input;

/// <summary>
/// Controls the behaviour of <c>IInputPlayer.PlayAsync</c>.
/// </summary>
public sealed class PlaybackOptions
{
    /// <summary>Optional execution-owned pause, target and held-input state. Never serialized.</summary>
    public PlaybackSessionContext? Session { get; init; }
    /// <summary>Default 1× speed, no loop.</summary>
    public static readonly PlaybackOptions Default = new();

    /// <summary>
    /// Playback speed multiplier. 1.0 = real-time. 2.0 = twice as fast.
    /// Must be > 0. Default: 1.0.
    /// </summary>
    public double SpeedMultiplier { get; init; } = 1.0;

    /// <summary>
    /// Number of times to repeat the event list.
    /// 1 = play once (no repeat). 0 or negative = play once.
    /// Must be >= 1. Default: 1.
    /// </summary>
    public int RepeatCount { get; init; } = 1;

    /// <summary>
    /// When <see langword="true"/>, synthesized events are tagged with a sentinel
    /// <c>dwExtraInfo</c> value so that the application's own hook can ignore them.
    /// Default: <see langword="true"/>.
    /// </summary>
    public bool TagSynthesizedEvents { get; init; } = true;

    /// <summary>
    /// The sentinel value written to <c>dwExtraInfo</c> for synthesized events.
    /// Only used when <see cref="TagSynthesizedEvents"/> is <see langword="true"/>.
    /// </summary>
    public uint SynthesizedEventTag { get; init; } = 0xBEEFCAFEu;

    /// <summary>
    /// When <see langword="true"/>, smooth interpolation is applied to mouse movement
    /// steps so cursor movement looks natural instead of teleporting. Default: <see langword="true"/>.
    /// </summary>
    public bool InterpolateMouseMoves { get; init; } = true;

    /// <summary>
    /// Target interval in milliseconds between interpolated mouse steps. Default: 8 ms.
    /// </summary>
    public int MouseInterpolationStepMs { get; init; } = 8;

    /// <summary>
    /// When <see langword="true"/>, translates target-window relative coordinates
    /// back to absolute screen coordinates using current window position. Default: <see langword="true"/>.
    /// </summary>
    public bool UseWindowRelativeCoordinates { get; init; } = true;

    /// <summary>
    /// Optional window handle of the target application. If set and <see cref="UseWindowRelativeCoordinates"/>
    /// is enabled, input coordinates are re-translated relative to this window at playback time.
    /// </summary>
    public IntPtr TargetWindowHandle { get; init; } = IntPtr.Zero;
    public int RecordedClientWidth { get; init; }
    public int RecordedClientHeight { get; init; }

    /// <summary>
    /// Validates this instance. Throws <see cref="ArgumentOutOfRangeException"/> on failure.
    /// </summary>
    public PlaybackOptions Validate()
    {
        if (!double.IsFinite(SpeedMultiplier) || SpeedMultiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(SpeedMultiplier), "Must be > 0.");
        if (RepeatCount < 1)
            throw new ArgumentOutOfRangeException(nameof(RepeatCount), "Must be >= 1.");
        if (MouseInterpolationStepMs < 1)
            throw new ArgumentOutOfRangeException(nameof(MouseInterpolationStepMs), "Must be >= 1.");
        if (RecordedClientWidth is < 0 or > 1_000_000 || RecordedClientHeight is < 0 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(RecordedClientWidth), "Recorded client dimensions are invalid.");
        return this;
    }
}
