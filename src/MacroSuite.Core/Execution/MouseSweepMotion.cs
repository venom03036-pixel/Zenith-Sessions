using MacroSuite.Core.Models;

namespace MacroSuite.Core.Execution;

/// <summary>
/// Pure camera-sweep trajectory math. No OS input, no allocations on the tick path.
/// Position is generated from elapsed time; leftover fractions stay until they form a whole pixel.
/// There is no end-of-sweep correction dump — a final t=1 sample through the same accumulator
/// finishes the integer destination without a sudden extra move.
/// </summary>
public static class MouseSweepMotion
{
    public const int MinHz = 60;
    public const int MaxHz = 360;
    public const int DefaultHz = 240;
    public const int DefaultHorizontalSmoothing = 20;
    public const int DefaultVerticalSmoothing = 75;
    public const int DefaultJitterPercent = DefaultVerticalSmoothing;
    public const double DefaultAxisNoiseRatio = 0.05;
    public const double DefaultVerticalDeadzonePixels = 1.5;
    /// <summary>Other axis must be at least this large before we treat the minor axis as noise.</summary>
    public const int MinDominantPixelsForDeadzone = 80;

    public static int ClampHz(int hz)
        => Math.Clamp(hz <= 0 ? DefaultHz : hz, MinHz, MaxHz);

    public static int ClampJitter(int percent)
        => Math.Clamp(percent, 0, 100);

    public static double ClampSensitivity(double value)
        => value < 0.05 ? 1.0 : Math.Clamp(value, 0.05, 8.0);

    public static void ApplyProfile(
        MouseSweepProfile profile,
        out int hz,
        out MouseInterpolation interpolation,
        out int horizontalSmoothing,
        out int verticalSmoothing,
        out bool filterVerticalJitter)
    {
        filterVerticalJitter = true;
        switch (profile)
        {
            case MouseSweepProfile.Precise:
                hz = 240;
                interpolation = MouseInterpolation.Linear;
                horizontalSmoothing = 0;
                verticalSmoothing = 0;
                break;
            case MouseSweepProfile.Cinematic:
                hz = 120;
                interpolation = MouseInterpolation.SmootherStep;
                horizontalSmoothing = 35;
                verticalSmoothing = 80;
                break;
            default:
                hz = DefaultHz;
                interpolation = MouseInterpolation.SmootherStep;
                horizontalSmoothing = DefaultHorizontalSmoothing;
                verticalSmoothing = DefaultVerticalSmoothing;
                break;
        }
    }

    public static MouseSweepProfile InferProfile(
        int hz,
        MouseInterpolation interpolation,
        int horizontalSmoothing,
        int verticalSmoothing,
        bool filterVerticalJitter)
    {
        if (!filterVerticalJitter)
            return MouseSweepProfile.Custom;
        if (hz == 240 && interpolation == MouseInterpolation.Linear
            && horizontalSmoothing == 0 && verticalSmoothing == 0)
            return MouseSweepProfile.Precise;
        if (hz == 240 && interpolation == MouseInterpolation.SmootherStep
            && horizontalSmoothing == DefaultHorizontalSmoothing
            && verticalSmoothing == DefaultVerticalSmoothing)
            return MouseSweepProfile.Smooth;
        if (hz == 120 && interpolation == MouseInterpolation.SmootherStep
            && horizontalSmoothing == 35 && verticalSmoothing == 80)
            return MouseSweepProfile.Cinematic;
        return MouseSweepProfile.Custom;
    }

    /// <summary>
    /// Zeros the minor axis only on a clearly dominant turn.
    /// A slow look up/down (small dy, small or modest dx) is kept.
    /// A wide yaw with a couple of noise pixels (-300,+2) drops Y.
    /// </summary>
    public static void ApplyAxisDeadzone(
        ref int destX,
        ref int destY,
        bool filterVertical,
        bool filterHorizontal,
        double pixelDeadzone = DefaultVerticalDeadzonePixels,
        double ratioThreshold = DefaultAxisNoiseRatio)
    {
        double ax = Math.Abs(destX);
        double ay = Math.Abs(destY);
        double mag = Math.Sqrt(ax * ax + ay * ay);
        if (mag < 0.5)
            return;

        if (filterVertical && IsNoiseOnDominantAxis(ay, ax, mag, pixelDeadzone, ratioThreshold))
            destY = 0;
        if (filterHorizontal && IsNoiseOnDominantAxis(ax, ay, mag, pixelDeadzone, ratioThreshold))
            destX = 0;
    }

    /// <summary>
    /// Shared 0..1 progress for both axes so X and Y stay on one straight trajectory.
    /// Smoothing blends the selected curve toward quintic SmootherStep. Always ends at 1.
    /// </summary>
    public static double Progress(double t, MouseInterpolation interpolation, int smoothingPercent)
    {
        if (t <= 0)
            return 0;
        if (t >= 1)
            return 1;

        double eased = Ease(t, interpolation);
        int smoothing = ClampJitter(smoothingPercent);
        if (smoothing <= 0)
            return eased;

        double extra = SmootherStep(t);
        return eased + (extra - eased) * (smoothing / 100.0);
    }

    /// <summary>Dominant-axis smoothing so X and Y never use different ease curves.</summary>
    public static int SharedSmoothing(int destX, int destY, int horizontalSmoothing, int verticalSmoothing)
    {
        if (destY == 0)
            return ClampJitter(horizontalSmoothing);
        if (destX == 0)
            return ClampJitter(verticalSmoothing);
        return Math.Abs(destX) >= Math.Abs(destY)
            ? ClampJitter(horizontalSmoothing)
            : ClampJitter(verticalSmoothing);
    }

    /// <summary>
    /// Emit the integer delta from the previous position to dest * progress.
    /// Leftover fractions stay in <paramref name="fracX"/> / <paramref name="fracY"/>.
    /// Does not dump dest - sent as a separate correction.
    /// </summary>
    public static void Sample(
        double destX,
        double destY,
        double progress,
        ref double lastX,
        ref double lastY,
        ref double fracX,
        ref double fracY,
        out int sendX,
        out int sendY)
    {
        if (progress < 0)
            progress = 0;
        else if (progress > 1)
            progress = 1;

        double currentX = destX * progress;
        double currentY = destY * progress;
        fracX += currentX - lastX;
        fracY += currentY - lastY;
        lastX = currentX;
        lastY = currentY;
        sendX = (int)Math.Round(fracX, MidpointRounding.ToEven);
        sendY = (int)Math.Round(fracY, MidpointRounding.ToEven);
        fracX -= sendX;
        fracY -= sendY;
    }

    public static void ResolveDestination(
        int deltaX,
        int deltaY,
        double sensitivity,
        double sensitivityX,
        double sensitivityY,
        out int destX,
        out int destY)
    {
        double sx = ClampSensitivity(sensitivityX);
        double sy = ClampSensitivity(sensitivityY);
        double shared = ClampSensitivity(sensitivity);
        if (Math.Abs(shared - 1.0) > 0.0001 && Math.Abs(sx - 1.0) < 0.0001 && Math.Abs(sy - 1.0) < 0.0001)
        {
            sx = shared;
            sy = shared;
        }

        destX = (int)Math.Round(deltaX * sx, MidpointRounding.AwayFromZero);
        destY = (int)Math.Round(deltaY * sy, MidpointRounding.AwayFromZero);
    }

    public static double SmootherStep(double t)
    {
        if (t <= 0)
            return 0;
        if (t >= 1)
            return 1;
        return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
    }

    private static bool IsNoiseOnDominantAxis(
        double minorAbs,
        double dominantAbs,
        double magnitude,
        double pixelDeadzone,
        double ratioThreshold)
    {
        if (dominantAbs < MinDominantPixelsForDeadzone)
            return false;
        if (dominantAbs < minorAbs * 4.0)
            return false;
        if (minorAbs <= pixelDeadzone)
            return true;
        return minorAbs <= 8.0 && minorAbs / magnitude < ratioThreshold;
    }

    private static double Ease(double t, MouseInterpolation mode) => mode switch
    {
        MouseInterpolation.Linear => t,
        MouseInterpolation.EaseInOut => t < 0.5
            ? 4.0 * t * t * t
            : 1.0 - Math.Pow(-2.0 * t + 2.0, 3.0) / 2.0,
        MouseInterpolation.Bezier => Cubic(t, 0.0, 0.15, 0.85, 1.0),
        MouseInterpolation.SmootherStep => SmootherStep(t),
        _ => t * t * (3.0 - 2.0 * t)
    };

    private static double Cubic(double t, double p0, double p1, double p2, double p3)
    {
        double u = 1.0 - t;
        return u * u * u * p0
             + 3.0 * u * u * t * p1
             + 3.0 * u * t * t * p2
             + t * t * t * p3;
    }

    private static int TruncateTowardZero(double value)
        => (int)Math.Truncate(value);
}
