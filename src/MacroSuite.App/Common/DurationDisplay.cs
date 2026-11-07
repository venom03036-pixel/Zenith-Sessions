namespace MacroSuite.App.Common;

/// <summary>
/// Formats millisecond durations so telemetry and action rows stay readable
/// as waits grow from milliseconds into seconds, minutes, and hours.
/// </summary>
public static class DurationDisplay
{
    public static string Format(TimeSpan duration) => Format(duration.TotalMilliseconds);

    public static string Format(double milliseconds)
    {
        if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0)
            milliseconds = 0;

        if (milliseconds < 1000)
            return $"{milliseconds:F0}ms";

        double seconds = milliseconds / 1000.0;
        if (seconds < 60)
            return seconds < 10 ? $"{seconds:F2}s" : $"{seconds:F1}s";

        double minutes = seconds / 60.0;
        if (minutes < 60)
        {
            int wholeMinutes = (int)minutes;
            int leftoverSeconds = (int)Math.Round(seconds - wholeMinutes * 60);
            if (leftoverSeconds <= 0)
                return $"{wholeMinutes}m";
            if (leftoverSeconds >= 60)
                return $"{wholeMinutes + 1}m";
            return $"{wholeMinutes}m {leftoverSeconds}s";
        }

        int hours = (int)(minutes / 60);
        int leftoverMinutes = (int)Math.Round(minutes - hours * 60);
        if (leftoverMinutes <= 0)
            return $"{hours}h";
        if (leftoverMinutes >= 60)
            return $"{hours + 1}h";
        return $"{hours}h {leftoverMinutes}m";
    }
}
