using MacroSuite.Core.Models;

namespace MacroSuite.Core.Execution;

public enum CameraLookKind
{
    None = 0,
    Horizontal = 1,
    Vertical = 2,
    Omnidirectional = 3
}

/// <summary>
/// Recorded-path math: cumulative samples, time-aware simplification,
/// linear interpolation. Single-axis lock jitter can be dropped; mixed
/// 2D holds keep both axes.
/// </summary>
public static class MouseTrajectoryPlayback
{
    public const int DefaultHz = 125;
    public const int MinHz = 60;
    public const int MaxHz = 250;
    public const float DefaultSimplifyEpsilon = 1.0f;
    public const int MaxStoredSamples = 2048;
    public const int SnapBackLockPx = 2;

    /// <summary>
    /// True when the game recentered the locked cursor. A 1-2 px snap
    /// must be skipped too — those were being stored as look jitter.
    /// Uses cursor distance to the lock point, never cumulative path.
    /// </summary>
    public static bool IsCursorSnapBack(int lastFromLock, int distFromLock)
        => distFromLock <= SnapBackLockPx && distFromLock < lastFromLock;

    public static int ClampHz(int hz)
        => Math.Clamp(hz <= 0 ? DefaultHz : hz, MinHz, MaxHz);

    /// <summary>
    /// Lossless-enough normalize for storage: collapse equal timestamps,
    /// force non-decreasing time, origin at zero. Does not drop axes.
    /// </summary>
    public static MouseTrajectorySample[] Normalize(IReadOnlyList<MouseTrajectorySample> raw)
    {
        if (raw is null || raw.Count == 0)
            return [];

        var compact = new List<MouseTrajectorySample>(raw.Count);
        compact.Add(raw[0]);
        for (int i = 1; i < raw.Count; i++)
        {
            var p = raw[i];
            var prev = compact[^1];
            if (p.T == prev.T)
            {
                compact[^1] = p;
                continue;
            }

            if (p.T < prev.T)
                p = new MouseTrajectorySample(prev.T, p.X, p.Y);
            compact.Add(p);
        }

        if (compact.Count == 1 && (compact[0].X != 0 || compact[0].Y != 0))
            compact.Insert(0, new MouseTrajectorySample(0, 0, 0));
        else if (compact.Count >= 1 && (compact[0].T != 0 || compact[0].X != 0 || compact[0].Y != 0))
            compact.Insert(0, new MouseTrajectorySample(0, 0, 0));

        if (compact.Count > MaxStoredSamples)
            ResampleEven(compact, MaxStoredSamples);

        return compact.ToArray();
    }

    public static MouseTrajectorySample[] Build(
        IReadOnlyList<MouseTrajectorySample> raw,
        float simplifyEpsilon = DefaultSimplifyEpsilon)
    {
        _ = simplifyEpsilon;
        return Normalize(raw);
    }

    public static MouseTrajectoryPacket[] PacketsFromSamples(IReadOnlyList<MouseTrajectorySample> samples)
    {
        if (samples is null || samples.Count < 2)
            return [];

        var packets = new List<MouseTrajectoryPacket>(samples.Count - 1);
        for (int i = 1; i < samples.Count; i++)
        {
            int dx = (int)Math.Round(samples[i].X - samples[i - 1].X, MidpointRounding.AwayFromZero);
            int dy = (int)Math.Round(samples[i].Y - samples[i - 1].Y, MidpointRounding.AwayFromZero);
            if (dx == 0 && dy == 0)
                continue;
            packets.Add(new MouseTrajectoryPacket(samples[i].T, dx, dy, packets.Count));
        }

        return packets.ToArray();
    }

    /// <summary>
    /// Drops lock-jitter only on a true single-axis look.
    /// Mixed / reversing / L-shaped holds keep both axes.
    /// </summary>
    public static MouseTrajectorySample[] Stabilize(IReadOnlyList<MouseTrajectorySample> samples)
    {
        if (samples is null || samples.Count == 0)
            return [];
        if (samples.Count == 1)
            return [samples[0]];

        ClassifyLook(samples, out bool dropX, out bool dropY);
        if (!dropX && !dropY)
        {
            var copy = new MouseTrajectorySample[samples.Count];
            for (int i = 0; i < samples.Count; i++)
                copy[i] = samples[i];
            return copy;
        }

        var output = new MouseTrajectorySample[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            var p = samples[i];
            output[i] = new MouseTrajectorySample(
                p.T,
                dropX ? 0 : p.X,
                dropY ? 0 : p.Y);
        }

        return output;
    }

    public static void ClassifyAxes(IReadOnlyList<MouseTrajectorySample> samples, out bool dropX, out bool dropY)
        => ClassifyLook(samples, out dropX, out dropY);

    public static CameraLookKind ClassifyLook(
        IReadOnlyList<MouseTrajectorySample> samples,
        out bool dropX,
        out bool dropY)
    {
        dropX = false;
        dropY = false;
        if (samples is null || samples.Count < 2)
            return CameraLookKind.None;

        MeasureLook(samples, out _, out _, out float peakX, out float peakY);
        MeasureRuns(samples, out float longestX, out float longestY);

        float absX = Math.Abs(peakX);
        float absY = Math.Abs(peakY);
        bool xJitter = absX < 8f && absX < Math.Max(8f, absY * 0.4f) && longestX < 8f;
        bool yJitter = absY < 8f && absY < Math.Max(6f, absX * 0.35f) && longestY < 8f;
        if (absY >= 8f)
            yJitter = false;

        bool xReal = !xJitter && (absX >= 8f || longestX >= 8f);
        bool yReal = !yJitter && absY >= 3f;

        if (xReal && yReal)
            return CameraLookKind.Omnidirectional;

        if (yReal && !xReal)
        {
            dropX = true;
            return CameraLookKind.Vertical;
        }

        if (xReal && !yReal)
        {
            dropY = absY < absX * 0.45f;
            return CameraLookKind.Horizontal;
        }

        if (absY >= absX && absY >= 3f)
        {
            dropX = absX < absY * 0.45f;
            return CameraLookKind.Vertical;
        }

        if (absX >= 3f)
        {
            dropY = absY < absX * 0.45f;
            return CameraLookKind.Horizontal;
        }

        return CameraLookKind.None;
    }

    private static void MeasureRuns(
        IReadOnlyList<MouseTrajectorySample> samples,
        out float longestX,
        out float longestY)
    {
        longestX = 0;
        longestY = 0;
        float runX = 0;
        float runY = 0;
        int signX = 0;
        int signY = 0;
        for (int i = 1; i < samples.Count; i++)
        {
            float dx = samples[i].X - samples[i - 1].X;
            float dy = samples[i].Y - samples[i - 1].Y;
            AccumulateRun(dx, ref signX, ref runX, ref longestX);
            AccumulateRun(dy, ref signY, ref runY, ref longestY);
        }

        if (runX > longestX)
            longestX = runX;
        if (runY > longestY)
            longestY = runY;
    }

    private static void AccumulateRun(float delta, ref int sign, ref float run, ref float longest)
    {
        int s = Math.Sign(delta);
        if (s == 0)
            return;
        if (sign == 0 || s == sign)
        {
            run += Math.Abs(delta);
            sign = s;
            return;
        }

        if (run > longest)
            longest = run;
        run = Math.Abs(delta);
        sign = s;
    }

    public enum LookPhaseKind
    {
        Horizontal = 0,
        Vertical = 1,
        Diagonal = 2
    }

    public readonly struct LookPhase
    {
        public LookPhaseKind Kind { get; init; }
        public int StartIndex { get; init; }
        public int EndIndex { get; init; }
        public int StartMs { get; init; }
        public int EndMs { get; init; }
        public float StartX { get; init; }
        public float StartY { get; init; }
        public float EndX { get; init; }
        public float EndY { get; init; }
        public float TargetX { get; init; }
        public float TargetY { get; init; }
    }

    /// <summary>
    /// Drops lock-jitter on the idle axis of each stretch (Y while spinning,
    /// X while flipping) and lightly smooths interiors. Sequential looks in
    /// one hold stay on both axes; they just stop fighting the cursor lock.
    /// </summary>
    public static MouseTrajectorySample[] CleanMixedPath(IReadOnlyList<MouseTrajectorySample> samples)
    {
        if (samples is null || samples.Count == 0)
            return [];
        if (samples.Count == 1)
            return [samples[0]];

        var rebuilt = new MouseTrajectorySample[samples.Count];
        float x = samples[0].X;
        float y = samples[0].Y;
        rebuilt[0] = new MouseTrajectorySample(samples[0].T, x, y);
        for (int i = 1; i < samples.Count; i++)
        {
            float dx = samples[i].X - samples[i - 1].X;
            float dy = samples[i].Y - samples[i - 1].Y;
            float ax = Math.Abs(dx);
            float ay = Math.Abs(dy);
            // Simultaneous motion is a real diagonal — never treat it as lock.
            bool diagonalSample = ax >= 3f && ay >= 3f;
            if (!diagonalSample)
            {
                ClassifyLocal(dx, dy, out LookPhaseKind kind);
                if (kind == LookPhaseKind.Horizontal && ay <= Math.Max(3f, ax * 0.4f) && Math.Abs(y) < 8f)
                    dy = 0;
                else if (kind == LookPhaseKind.Vertical && ax <= Math.Max(3f, ay * 0.4f) && Math.Abs(x) < 8f)
                    dx = 0;
            }

            x += dx;
            y += dy;
            rebuilt[i] = new MouseTrajectorySample(samples[i].T, x, y);
        }

        return rebuilt;
    }

    public static LookPhase[] SegmentPhases(IReadOnlyList<MouseTrajectorySample> samples)
    {
        if (samples is null || samples.Count < 2)
            return [];

        int segCount = samples.Count - 1;
        var kinds = new LookPhaseKind[segCount];
        for (int i = 0; i < segCount; i++)
        {
            float dx = samples[i + 1].X - samples[i].X;
            float dy = samples[i + 1].Y - samples[i].Y;
            if (dx == 0 && dy == 0 && i > 0)
                kinds[i] = kinds[i - 1];
            else
                ClassifyLocal(dx, dy, out kinds[i]);
        }

        var runs = new List<(LookPhaseKind Kind, int Start, int End)>(8);
        int runStart = 0;
        for (int i = 1; i <= segCount; i++)
        {
            if (i < segCount && kinds[i] == kinds[runStart])
                continue;
            runs.Add((kinds[runStart], runStart, i));
            runStart = i;
        }

        for (int i = 1; i < runs.Count;)
        {
            var run = runs[i];
            float travel = AxisTravel(samples, run.Start, run.End);
            int dt = samples[run.End].T - samples[run.Start].T;
            if (travel < 8f && dt < 48)
            {
                var prev = runs[i - 1];
                runs[i - 1] = (prev.Kind, prev.Start, run.End);
                runs.RemoveAt(i);
                continue;
            }

            i++;
        }

        for (int i = 1; i < runs.Count;)
        {
            if (runs[i].Kind == runs[i - 1].Kind)
            {
                runs[i - 1] = (runs[i - 1].Kind, runs[i - 1].Start, runs[i].End);
                runs.RemoveAt(i);
                continue;
            }

            i++;
        }

        var phases = new LookPhase[runs.Count];
        for (int i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            int a = run.Start;
            int b = run.End;
            float startX = samples[a].X;
            float startY = samples[a].Y;
            float endX = samples[b].X;
            float endY = samples[b].Y;
            float minX = startX;
            float maxX = startX;
            float minY = startY;
            float maxY = startY;
            for (int s = a; s <= b; s++)
            {
                float px = samples[s].X;
                float py = samples[s].Y;
                if (px < minX) minX = px;
                if (px > maxX) maxX = px;
                if (py < minY) minY = py;
                if (py > maxY) maxY = py;
            }

            float peakX = Math.Abs(minX) >= Math.Abs(maxX) ? minX : maxX;
            float peakY = Math.Abs(minY) >= Math.Abs(maxY) ? minY : maxY;
            phases[i] = new LookPhase
            {
                Kind = run.Kind,
                StartIndex = a,
                EndIndex = b,
                StartMs = samples[a].T,
                EndMs = samples[b].T,
                StartX = startX,
                StartY = startY,
                EndX = endX,
                EndY = endY,
                TargetX = PreferPeak(endX, peakX),
                TargetY = PreferPeak(endY, peakY)
            };
        }

        return phases;
    }

    public static bool HasSequentialAxes(IReadOnlyList<LookPhase> phases)
    {
        if (phases is null || phases.Count < 2)
            return false;

        bool horizontal = false;
        bool vertical = false;
        for (int i = 0; i < phases.Count; i++)
        {
            var p = phases[i];
            if (p.Kind == LookPhaseKind.Horizontal && Math.Abs(p.EndX - p.StartX) >= 12f)
                horizontal = true;
            if (p.Kind == LookPhaseKind.Vertical && Math.Abs(p.TargetY - p.StartY) >= 8f)
                vertical = true;
        }

        return horizontal && vertical;
    }

    public static bool AxisReturns(IReadOnlyList<MouseTrajectorySample> samples, bool vertical)
    {
        if (samples is null || samples.Count < 3)
            return false;

        MeasureLook(samples, out _, out _, out float peakX, out float peakY);
        float peak = vertical ? peakY : peakX;
        if (Math.Abs(peak) < 8f)
            return false;

        int peakIndex = 0;
        float best = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            float v = vertical ? samples[i].Y : samples[i].X;
            if (Math.Abs(v) > best)
            {
                best = Math.Abs(v);
                peakIndex = i;
            }
        }

        int towardZero = -Math.Sign(peak);
        if (towardZero == 0 || peakIndex >= samples.Count - 1)
            return false;

        float returnTravel = 0;
        for (int i = peakIndex + 1; i < samples.Count; i++)
        {
            float d = vertical
                ? samples[i].Y - samples[i - 1].Y
                : samples[i].X - samples[i - 1].X;
            if (Math.Sign(d) == towardZero)
                returnTravel += Math.Abs(d);
        }

        return returnTravel >= 12f;
    }

    public static bool HasDiagonalStretch(IReadOnlyList<LookPhase> phases)
    {
        if (phases is null)
            return false;

        for (int i = 0; i < phases.Count; i++)
        {
            var p = phases[i];
            float dx = Math.Abs(p.EndX - p.StartX);
            float dy = Math.Abs(p.EndY - p.StartY);
            if (p.Kind == LookPhaseKind.Diagonal && dx >= 8f && dy >= 8f)
                return true;
            if (p.Kind == LookPhaseKind.Horizontal && dy >= 8f)
                return true;
            if (p.Kind == LookPhaseKind.Vertical && dx >= 8f)
                return true;
        }

        return false;
    }

    public static bool ShouldFollowRecordedPath(
        CameraLookKind kind,
        IReadOnlyList<MouseTrajectorySample> samples,
        IReadOnlyList<LookPhase> phases)
    {
        if (kind == CameraLookKind.Omnidirectional && HasDiagonalStretch(phases))
            return true;

        MeasureLook(samples, out _, out _, out float peakX, out _);
        bool turning = Math.Abs(peakX) >= 8f;
        return turning && AxisReturns(samples, vertical: true);
    }

    public static void EvaluatePhased(
        IReadOnlyList<MouseTrajectorySample> path,
        IReadOnlyList<LookPhase> phases,
        double timeMs,
        out double x,
        out double y)
    {
        if (phases is null || phases.Count == 0)
        {
            Interpolate(path, timeMs, out x, out y);
            return;
        }

        int index = 0;
        for (int i = 0; i < phases.Count; i++)
        {
            index = i;
            if (timeMs <= phases[i].EndMs)
                break;
        }

        var phase = phases[index];
        if (phase.Kind == LookPhaseKind.Horizontal)
        {
            Interpolate(path, timeMs, out x, out _);
            y = phase.StartY;
            return;
        }

        if (phase.Kind == LookPhaseKind.Vertical)
        {
            double span = phase.EndMs - phase.StartMs;
            double u = span <= 0 ? 1 : (timeMs - phase.StartMs) / span;
            if (u < 0) u = 0;
            else if (u > 1) u = 1;
            x = phase.StartX;
            y = phase.StartY + (phase.TargetY - phase.StartY) * MouseSweepMotion.SmootherStep(u);
            return;
        }

        Interpolate(path, timeMs, out x, out y);
    }

    /// <summary>
    /// Softens interiors of each stretch without moving the endpoints, so
    /// spin/flip distance stays the same.
    /// </summary>
    public static MouseTrajectorySample[] SmoothPhaseInteriors(
        IReadOnlyList<MouseTrajectorySample> samples,
        IReadOnlyList<LookPhase> phases)
    {
        if (samples is null || samples.Count == 0)
            return [];

        var output = new MouseTrajectorySample[samples.Count];
        for (int i = 0; i < samples.Count; i++)
            output[i] = samples[i];
        if (phases is null || phases.Count == 0)
            return output;

        for (int p = 0; p < phases.Count; p++)
        {
            int a = phases[p].StartIndex;
            int b = phases[p].EndIndex;
            if (b - a < 3)
                continue;
            for (int i = a + 1; i < b; i++)
            {
                float x = (samples[i - 1].X + samples[i].X * 2f + samples[i + 1].X) * 0.25f;
                float y = (samples[i - 1].Y + samples[i].Y * 2f + samples[i + 1].Y) * 0.25f;
                output[i] = new MouseTrajectorySample(samples[i].T, x, y);
            }
        }

        return output;
    }

    private static void ClassifyLocal(float dx, float dy, out LookPhaseKind kind)
    {
        float ax = Math.Abs(dx);
        float ay = Math.Abs(dy);
        if (ax >= ay * 1.2f && ax >= 1f)
            kind = LookPhaseKind.Horizontal;
        else if (ay >= ax * 1.2f && ay >= 1f)
            kind = LookPhaseKind.Vertical;
        else if (ax >= 1f && ay >= 1f)
            kind = LookPhaseKind.Diagonal;
        else if (ax >= ay)
            kind = LookPhaseKind.Horizontal;
        else
            kind = LookPhaseKind.Vertical;
    }

    private static float AxisTravel(IReadOnlyList<MouseTrajectorySample> samples, int start, int end)
    {
        float travel = 0;
        for (int i = start + 1; i <= end && i < samples.Count; i++)
        {
            float dx = samples[i].X - samples[i - 1].X;
            float dy = samples[i].Y - samples[i - 1].Y;
            travel += Math.Abs(dx) + Math.Abs(dy);
        }

        return travel;
    }

    /// <summary>
    /// Farthest recorded look in each signed direction. min is ≤ 0, max is ≥ 0.
    /// A two-way hold (left then right, down then up) keeps both sides.
    /// </summary>
    public static void MeasureExtents(
        IReadOnlyList<MouseTrajectorySample> samples,
        out float minX,
        out float maxX,
        out float minY,
        out float maxY)
    {
        minX = 0;
        maxX = 0;
        minY = 0;
        maxY = 0;
        if (samples is null)
            return;

        for (int i = 0; i < samples.Count; i++)
        {
            float x = samples[i].X;
            float y = samples[i].Y;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
    }

    public static string FormatExtent(float min, float max)
    {
        int n = (int)Math.Round(min);
        int p = (int)Math.Round(max);
        if (n == 0 && p == 0)
            return "0";
        if (n == 0)
            return $"+{p}";
        if (p == 0)
            return n.ToString();
        return $"{n}/+{p}";
    }

    public const float ExtentScaleEpsilon = 0.5f;
    public const float MaxExtentPx = 20_000f;

    public static float ScaleAxis(float value, float oldMin, float oldMax, float newMin, float newMax)
    {
        if (value >= 0f)
        {
            if (Math.Abs(oldMax) < ExtentScaleEpsilon)
                return value;
            return value * (newMax / oldMax);
        }

        if (Math.Abs(oldMin) < ExtentScaleEpsilon)
            return value;
        return value * (newMin / oldMin);
    }

    public static MouseTrajectorySample[] ScaleByExtents(
        IReadOnlyList<MouseTrajectorySample> samples,
        float minX,
        float maxX,
        float minY,
        float maxY)
    {
        if (samples is null || samples.Count == 0)
            return [];

        MeasureExtents(samples, out float oldMinX, out float oldMaxX, out float oldMinY, out float oldMaxY);
        ClampTargetExtents(ref minX, ref maxX, ref minY, ref maxY);
        var output = new MouseTrajectorySample[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            var p = samples[i];
            output[i] = new MouseTrajectorySample(
                p.T,
                ScaleAxis(p.X, oldMinX, oldMaxX, minX, maxX),
                ScaleAxis(p.Y, oldMinY, oldMaxY, minY, maxY));
        }

        return output;
    }

    public static MouseTrajectoryPacket[] ScalePacketsByExtents(
        MouseTrajectoryPacket[] packets,
        IReadOnlyList<MouseTrajectorySample> originalSamples,
        float minX,
        float maxX,
        float minY,
        float maxY)
    {
        if (packets is null || packets.Length == 0)
            return packets ?? [];

        MeasureExtents(originalSamples, out float oldMinX, out float oldMaxX, out float oldMinY, out float oldMaxY);
        ClampTargetExtents(ref minX, ref maxX, ref minY, ref maxY);
        var output = new MouseTrajectoryPacket[packets.Length];
        float x = 0;
        float y = 0;
        for (int i = 0; i < packets.Length; i++)
        {
            float nx = x + packets[i].Dx;
            float ny = y + packets[i].Dy;
            int dx = (int)Math.Round(
                ScaleAxis(nx, oldMinX, oldMaxX, minX, maxX) - ScaleAxis(x, oldMinX, oldMaxX, minX, maxX),
                MidpointRounding.AwayFromZero);
            int dy = (int)Math.Round(
                ScaleAxis(ny, oldMinY, oldMaxY, minY, maxY) - ScaleAxis(y, oldMinY, oldMaxY, minY, maxY),
                MidpointRounding.AwayFromZero);
            output[i] = new MouseTrajectoryPacket(packets[i].T, dx, dy, packets[i].Sequence);
            x = nx;
            y = ny;
        }

        return output;
    }

    public static bool ExtentsMatch(
        IReadOnlyList<MouseTrajectorySample> samples,
        int minX,
        int maxX,
        int minY,
        int maxY)
    {
        MeasureExtents(samples, out float x0, out float x1, out float y0, out float y1);
        return (int)Math.Round(x0) == minX
            && (int)Math.Round(x1) == maxX
            && (int)Math.Round(y0) == minY
            && (int)Math.Round(y1) == maxY;
    }

    private static void ClampTargetExtents(ref float minX, ref float maxX, ref float minY, ref float maxY)
    {
        minX = Math.Clamp(Math.Min(0f, minX), -MaxExtentPx, 0f);
        maxX = Math.Clamp(Math.Max(0f, maxX), 0f, MaxExtentPx);
        minY = Math.Clamp(Math.Min(0f, minY), -MaxExtentPx, 0f);
        maxY = Math.Clamp(Math.Max(0f, maxY), 0f, MaxExtentPx);
    }

    /// <summary>
    /// Net end position plus the farthest point reached on each axis.
    /// Snap-back often leaves a small net Y while the true look is the peak.
    /// </summary>
    public static void MeasureLook(
        IReadOnlyList<MouseTrajectorySample> samples,
        out float netX,
        out float netY,
        out float peakX,
        out float peakY)
    {
        netX = 0;
        netY = 0;
        peakX = 0;
        peakY = 0;
        if (samples is null || samples.Count == 0)
            return;

        var last = samples[samples.Count - 1];
        netX = last.X;
        netY = last.Y;
        float minX = 0, maxX = 0, minY = 0, maxY = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            float x = samples[i].X;
            float y = samples[i].Y;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        peakX = Math.Abs(minX) >= Math.Abs(maxX) ? minX : maxX;
        peakY = Math.Abs(minY) >= Math.Abs(maxY) ? minY : maxY;
        peakX = PreferPeak(netX, peakX);
        peakY = PreferPeak(netY, peakY);
    }

    public static bool HasMeaningfulLook(IReadOnlyList<MouseTrajectorySample> samples)
    {
        MeasureLook(samples, out float netX, out float netY, out float peakX, out float peakY);
        return Math.Abs(peakX) >= 1f || Math.Abs(peakY) >= 1f
            || Math.Abs(netX) >= 1f || Math.Abs(netY) >= 1f;
    }

    private static float PreferPeak(float net, float peak)
    {
        if (Math.Abs(peak) < 0.001f)
            return net;
        if (Math.Abs(net) >= 3 && Math.Sign(net) != Math.Sign(peak))
            return net;
        return Math.Abs(peak) >= Math.Abs(net) ? peak : net;
    }

    public static void Interpolate(
        IReadOnlyList<MouseTrajectorySample> samples,
        double timeMs,
        out double x,
        out double y)
    {
        int n = samples.Count;
        if (n == 0)
        {
            x = 0;
            y = 0;
            return;
        }

        if (n == 1 || timeMs <= samples[0].T)
        {
            x = samples[0].X;
            y = samples[0].Y;
            return;
        }

        var last = samples[n - 1];
        if (timeMs >= last.T)
        {
            x = last.X;
            y = last.Y;
            return;
        }

        int hi = FindUpper(samples, timeMs);
        int lo = hi - 1;
        var a = samples[lo];
        var b = samples[hi];
        double span = b.T - a.T;
        double t = span <= 0 ? 1 : (timeMs - a.T) / span;
        if (t < 0) t = 0;
        else if (t > 1) t = 1;
        x = a.X + (b.X - a.X) * t;
        y = a.Y + (b.Y - a.Y) * t;
    }

    public static void AccumulateDelta(
        double desiredX,
        double desiredY,
        ref double previousX,
        ref double previousY,
        ref double remainderX,
        ref double remainderY,
        out int sendX,
        out int sendY)
    {
        remainderX += desiredX - previousX;
        remainderY += desiredY - previousY;
        previousX = desiredX;
        previousY = desiredY;
        sendX = (int)Math.Round(remainderX, MidpointRounding.ToEven);
        sendY = (int)Math.Round(remainderY, MidpointRounding.ToEven);
        remainderX -= sendX;
        remainderY -= sendY;
    }

    public static void NetDisplacement(IReadOnlyList<MouseTrajectorySample> samples, out int dx, out int dy)
    {
        if (samples is null || samples.Count == 0)
        {
            dx = 0;
            dy = 0;
            return;
        }

        var last = samples[samples.Count - 1];
        dx = (int)Math.Round(last.X, MidpointRounding.AwayFromZero);
        dy = (int)Math.Round(last.Y, MidpointRounding.AwayFromZero);
    }

    public static int DurationMs(IReadOnlyList<MouseTrajectorySample> samples)
    {
        if (samples is null || samples.Count == 0)
            return 0;
        int last = samples[samples.Count - 1].T;
        return last < 16 ? 16 : last;
    }

    private static int FindUpper(IReadOnlyList<MouseTrajectorySample> samples, double timeMs)
    {
        int lo = 1;
        int hi = samples.Count - 1;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (samples[mid].T < timeMs)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    private static void SimplifyRange(
        List<MouseTrajectorySample> src,
        int start,
        int end,
        float epsilon,
        List<MouseTrajectorySample> dest)
    {
        if (end <= start + 1)
        {
            dest.Add(src[start]);
            if (end > start)
                dest.Add(src[end]);
            return;
        }

        int cut = -1;
        double maxDist = epsilon;
        var a = src[start];
        var b = src[end];
        double span = b.T - a.T;

        for (int i = start + 1; i < end; i++)
        {
            var p = src[i];
            double t = span <= 0 ? 1 : (p.T - a.T) / span;
            double predX = a.X + (b.X - a.X) * t;
            double predY = a.Y + (b.Y - a.Y) * t;
            double dx = p.X - predX;
            double dy = p.Y - predY;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > maxDist)
            {
                maxDist = dist;
                cut = i;
            }
        }

        if (cut < 0)
        {
            dest.Add(a);
            dest.Add(b);
            return;
        }

        var left = new List<MouseTrajectorySample>(cut - start + 1);
        SimplifyRange(src, start, cut, epsilon, left);
        var right = new List<MouseTrajectorySample>(end - cut + 1);
        SimplifyRange(src, cut, end, epsilon, right);
        dest.AddRange(left);
        for (int i = 1; i < right.Count; i++)
            dest.Add(right[i]);
    }

    private static void ResampleEven(List<MouseTrajectorySample> points, int count)
    {
        if (points.Count <= count)
            return;

        var src = points.ToArray();
        points.Clear();
        double duration = src[^1].T;
        for (int i = 0; i < count; i++)
        {
            double t = count == 1 ? duration : duration * i / (count - 1);
            Interpolate(src, t, out double x, out double y);
            points.Add(new MouseTrajectorySample((int)Math.Round(t), (float)x, (float)y));
        }
    }
}
