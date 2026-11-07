using MacroSuite.Core.Abstractions;

namespace MacroSuite.Core.Execution;

/// <summary>
/// Emits ΔQ = round(P(t) * gain) − Q_sent. Sent position advances only after
/// SendRelativeMove returns.
/// </summary>
public sealed class CumulativeMouseEmitter
{
    private long _sentX;
    private long _sentY;

    public void Reset()
    {
        _sentX = 0;
        _sentY = 0;
    }

    public void Emit(
        TrajectoryPosition target,
        double gainX,
        double gainY,
        IInputPlayer player,
        bool dualBackend = false)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!double.IsFinite(target.X) || !double.IsFinite(target.Y))
            throw new ArgumentOutOfRangeException(nameof(target));

        long targetX = (long)Math.Round(target.X * gainX, MidpointRounding.ToEven);
        long targetY = (long)Math.Round(target.Y * gainY, MidpointRounding.ToEven);
        int dx = checked((int)(targetX - _sentX));
        int dy = checked((int)(targetY - _sentY));
        if (dx == 0 && dy == 0)
            return;

        player.SendRelativeMove(dx, dy, dualBackend);
        _sentX = targetX;
        _sentY = targetY;
    }
}
