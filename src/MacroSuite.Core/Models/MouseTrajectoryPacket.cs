namespace MacroSuite.Core.Models;

/// <summary>One captured relative move, in recorded-relative milliseconds.</summary>
public readonly record struct MouseTrajectoryPacket(int T, int Dx, int Dy, int Sequence)
{
    /// <summary>Optional precise offset in TimeSpan ticks (100 ns); legacy packets use T milliseconds.</summary>
    public long? OffsetTicks { get; init; }
}

/// <summary>Button down/up that occurred during a held-look path. Metadata only.</summary>
public readonly record struct TrajectoryButtonTransition(int T, int Sequence, MouseButton Button, bool Down);

public enum TrajectoryPlaybackMode
{
    Reconstructed = 0,
    PacketReference = 1
}

public enum TrajectoryGapPolicy
{
    PreservePause = 0,
    Interpolate = 1
}
