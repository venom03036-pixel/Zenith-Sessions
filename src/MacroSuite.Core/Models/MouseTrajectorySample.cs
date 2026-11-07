namespace MacroSuite.Core.Models;

/// <summary>
/// One point on a recorded camera path: cumulative position at time T
/// (milliseconds from the start of this gesture).
/// </summary>
public readonly struct MouseTrajectorySample
{
    public int T { get; init; }
    public float X { get; init; }
    public float Y { get; init; }

    public MouseTrajectorySample(int t, float x, float y)
    {
        T = t < 0 ? 0 : t;
        X = x;
        Y = y;
    }
}
