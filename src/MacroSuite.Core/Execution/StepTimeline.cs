using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;

namespace MacroSuite.Core.Execution;

/// <summary>
/// Recorded clicks / moves / keys store <c>RelativeTimestamp</c>. Playback must honor
/// those gaps (scaled by speed) or the macro dumps instantly and looks unfinished.
/// </summary>
public static class StepTimeline
{
    public static bool TryGetRelativeTimestamp(MacroStep step, out TimeSpan timestamp)
    {
        timestamp = step switch
        {
            ClickStep click => click.RelativeTimestamp,
            MouseMoveStep move => move.RelativeTimestamp,
            KeyPressStep key => key.RelativeTimestamp,
            MouseWheelStep wheel => wheel.RelativeTimestamp,
            MouseSweepStep sweep => sweep.RelativeTimestamp,
            MouseTrajectoryStep path => path.TimestampIsStart
                ? path.RelativeTimestamp
                : TimeSpan.FromTicks(Math.Max(0, (path.RelativeTimestamp - path.RecordedDuration).Ticks)),
            _ => TimeSpan.Zero
        };
        return timestamp > TimeSpan.Zero || step is MouseTrajectoryStep pathStep
            && (pathStep.TimestampIsStart || pathStep.RelativeTimestamp > TimeSpan.Zero);
    }

    /// <summary>A path consumes its recorded duration; the following gap starts at its end.</summary>
    public static TimeSpan GetEndTimestamp(MacroStep step, TimeSpan start)
        => step is MouseTrajectoryStep path ? start + path.RecordedDuration : start;

    public static TimeSpan Scale(TimeSpan duration, double playbackSpeed)
    {
        if (duration <= TimeSpan.Zero)
            return TimeSpan.Zero;

        double speed = double.IsFinite(playbackSpeed) && playbackSpeed > 0.05 ? playbackSpeed : 1.0;
        if (Math.Abs(speed - 1.0) < 0.01)
            return duration;

        return TimeSpan.FromTicks((long)(duration.Ticks / speed));
    }
}
