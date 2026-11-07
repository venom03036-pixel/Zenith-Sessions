using MacroSuite.Core.Models;

namespace MacroSuite.Core.Execution;

public readonly record struct TrajectoryPosition(double X, double Y);

/// <summary>
/// Piecewise-linear cumulative path. Built once from recorded samples.
/// Does not classify, ease, or drop axes.
/// </summary>
public sealed class RecordedTrajectory
{
    public const int PauseGapMs = 48;

    private readonly MouseTrajectorySample[] _knots;

    public IReadOnlyList<MouseTrajectorySample> Knots => _knots;
    public double DurationMs { get; }
    public IReadOnlyList<int> ProtectedTimesMs { get; }

    public RecordedTrajectory(MouseTrajectorySample[] knots, int[]? protectedTimesMs = null)
    {
        ArgumentNullException.ThrowIfNull(knots);
        if (knots.Length < 2)
            throw new ArgumentException("At least two knots are required.", nameof(knots));

        _knots = knots;
        DurationMs = MouseTrajectoryPlayback.DurationMs(_knots);
        ProtectedTimesMs = protectedTimesMs ?? [];
    }

    public static RecordedTrajectory FromSamples(
        IReadOnlyList<MouseTrajectorySample> samples,
        TrajectoryGapPolicy gapPolicy = TrajectoryGapPolicy.PreservePause)
    {
        var normalized = MouseTrajectoryPlayback.Normalize(samples);
        if (normalized.Length < 2)
            normalized = [new MouseTrajectorySample(0, 0, 0), new MouseTrajectorySample(16, 0, 0)];

        if (gapPolicy == TrajectoryGapPolicy.PreservePause)
            normalized = InsertPauseKnots(normalized);

        normalized = SmoothKnotsBounded(normalized, 1.5f);

        int[] protectedTimes = CollectProtectedTimes(normalized);
        return new RecordedTrajectory(normalized, protectedTimes);
    }

    /// <summary>
    /// One 1-2-1 pass on yaw only. Pitch stays on the recording so
    /// look-up / look-down is not flattened. A point whose X would move
    /// more than <paramref name="maxError"/> stays on the recording.
    /// </summary>
    public static MouseTrajectorySample[] SmoothKnotsBounded(
        IReadOnlyList<MouseTrajectorySample> samples,
        float maxError)
    {
        if (samples is null)
            return [];
        if (samples.Count < 4)
            return samples as MouseTrajectorySample[] ?? [.. samples];

        var output = new MouseTrajectorySample[samples.Count];
        output[0] = samples[0];
        output[samples.Count - 1] = samples[samples.Count - 1];
        for (int i = 1; i < samples.Count - 1; i++)
        {
            float x = (samples[i - 1].X + samples[i].X * 2f + samples[i + 1].X) * 0.25f;
            float dx = x - samples[i].X;
            if (dx > maxError || dx < -maxError)
                output[i] = samples[i];
            else
                output[i] = new MouseTrajectorySample(samples[i].T, x, samples[i].Y);
        }

        return output;
    }

    public TrajectoryPosition EvaluateMs(double timeMs)
    {
        MouseTrajectoryPlayback.Interpolate(_knots, timeMs, out double x, out double y);
        return new TrajectoryPosition(x, y);
    }

    public static MouseTrajectorySample[] InsertPauseKnots(IReadOnlyList<MouseTrajectorySample> samples)
    {
        if (samples.Count < 2)
            return samples as MouseTrajectorySample[] ?? [.. samples];

        var output = new List<MouseTrajectorySample>(samples.Count + 4);
        output.Add(samples[0]);
        for (int i = 1; i < samples.Count; i++)
        {
            var prev = output[^1];
            var next = samples[i];
            int dt = next.T - prev.T;
            bool moved = next.X != prev.X || next.Y != prev.Y;
            if (moved && dt >= PauseGapMs)
            {
                int holdT = next.T - 1;
                if (holdT <= prev.T)
                    holdT = prev.T;
                if (holdT < next.T)
                    output.Add(new MouseTrajectorySample(holdT, prev.X, prev.Y));
            }

            output.Add(next);
        }

        return output.ToArray();
    }

    private static int[] CollectProtectedTimes(IReadOnlyList<MouseTrajectorySample> knots)
    {
        var times = new List<int>(8);
        for (int i = 1; i < knots.Count; i++)
        {
            var a = knots[i - 1];
            var b = knots[i];
            if (b.T - a.T >= PauseGapMs && a.X == b.X && a.Y == b.Y)
                times.Add(b.T);
        }

        return times.Count == 0 ? [] : times.ToArray();
    }

    /// <summary>
    /// Times at which playback must emit. Every knot is included so
    /// look-down / look-up / yaw extrema are not skipped by a fixed Hz
    /// grid. Slow long segments may get extra in-between times.
    /// </summary>
    public const float FlickPxPerMs = 0.4f;

    public static int[] BuildEmitTimesMs(
        IReadOnlyList<MouseTrajectorySample> knots,
        int hz,
        IReadOnlyList<int>? extraTimes = null)
    {
        if (knots is null || knots.Count == 0)
            return UniqueSorted(extraTimes);

        int step = Math.Max(4, 1000 / MouseTrajectoryPlayback.ClampHz(hz));
        var times = new List<int>(knots.Count + 8);
        times.Add(knots[0].T);

        for (int i = 1; i < knots.Count; i++)
        {
            var a = knots[i - 1];
            var b = knots[i];
            int t1 = b.T;
            int dt = t1 - a.T;
            // A stationary stretch needs only its endpoints; do not wake up
            // hundreds of times per second to emit zero movement.
            if (dt > step && (a.X != b.X || a.Y != b.Y))
            {
                long t = (long)a.T + step;
                while (t < t1)
                {
                    times.Add((int)t);
                    t += step;
                }
            }

            if (times.Count == 0 || times[^1] != t1)
                times.Add(t1);
        }

        if (extraTimes != null)
        {
            for (int i = 0; i < extraTimes.Count; i++)
            {
                if (extraTimes[i] >= 0)
                    times.Add(extraTimes[i]);
            }
        }

        times.Sort();
        return UniqueInPlace(times);
    }

    private static int[] UniqueSorted(IReadOnlyList<int>? times)
    {
        if (times is null || times.Count == 0)
            return [];

        var copy = new List<int>(times.Count);
        for (int i = 0; i < times.Count; i++)
        {
            if (times[i] >= 0)
                copy.Add(times[i]);
        }

        copy.Sort();
        return UniqueInPlace(copy);
    }

    private static int[] UniqueInPlace(List<int> times)
    {
        if (times.Count == 0)
            return [];

        int w = 1;
        for (int i = 1; i < times.Count; i++)
        {
            if (times[i] != times[w - 1])
                times[w++] = times[i];
        }

        if (w == times.Count)
            return times.ToArray();

        var compact = new int[w];
        for (int i = 0; i < w; i++)
            compact[i] = times[i];
        return compact;
    }
}
