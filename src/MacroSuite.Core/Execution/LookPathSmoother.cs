using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Execution;

/// <summary>
/// Prepares a recorded relative look for smooth playback. Does not drop
/// axes or replace the path with an easing curve. Lock-cursor chatter is
/// removed; real reversals stay.
/// </summary>
public static class LookPathSmoother
{
    public const float SpikePx = 2.5f;
    public const float SmoothErrorPx = 4.0f;
    public const int MaxSendPx = 12;
    public const int SmoothPasses = 3;
    public const float HardReverseStepPx = 4.0f;
    public const float HardReverseOriginPx = 8.0f;

    public static MouseTrajectorySample[] Prepare(IReadOnlyList<MouseTrajectorySample> raw)
    {
        var samples = MouseTrajectoryPlayback.Normalize(raw);
        if (samples.Length < 2)
            return samples;

        samples = RemoveLockSpikes(samples);
        for (int pass = 0; pass < SmoothPasses; pass++)
            samples = SmoothBothBounded(samples, SmoothErrorPx);
        return samples;
    }

    public static double PathLength(IReadOnlyList<MouseTrajectorySample> samples)
    {
        if (samples is null || samples.Count < 2)
            return 0;

        double length = 0;
        for (int i = 1; i < samples.Count; i++)
        {
            double dx = samples[i].X - samples[i - 1].X;
            double dy = samples[i].Y - samples[i - 1].Y;
            length += Math.Sqrt(dx * dx + dy * dy);
        }

        return length;
    }

    public static MouseTrajectorySample[] RemoveLockSpikes(IReadOnlyList<MouseTrajectorySample> samples)
    {
        if (samples is null || samples.Count < 3)
            return samples as MouseTrajectorySample[] ?? (samples is null ? [] : [.. samples]);

        var output = new List<MouseTrajectorySample>(samples.Count);
        output.Add(samples[0]);
        for (int i = 1; i < samples.Count - 1; i++)
        {
            var prev = samples[i - 1];
            var cur = samples[i];
            var next = samples[i + 1];
            float dx0 = cur.X - prev.X;
            float dx1 = next.X - cur.X;
            float dy0 = cur.Y - prev.Y;
            float dy1 = next.Y - cur.Y;
            float dx2 = 0f;
            float dy2 = 0f;
            if (i + 2 < samples.Count)
            {
                dx2 = samples[i + 2].X - next.X;
                dy2 = samples[i + 2].Y - next.Y;
            }

            bool isolatedX = IsIsolatedSpike(dx0, dx1, dx2);
            bool isolatedY = IsIsolatedSpike(dy0, dy1, dy2);
            bool movedX = MathF.Abs(dx0) + MathF.Abs(dx1) > 0.01f;
            bool movedY = MathF.Abs(dy0) + MathF.Abs(dy1) > 0.01f;
            if ((isolatedX || !movedX) && (isolatedY || !movedY) && (isolatedX || isolatedY))
                continue;

            output.Add(cur);
        }

        output.Add(samples[samples.Count - 1]);
        return output.ToArray();
    }

    private static bool IsIsolatedSpike(float d0, float d1, float d2)
    {
        if (d0 * d1 >= 0f || MathF.Abs(d0) > SpikePx || MathF.Abs(d1) > SpikePx)
            return false;
        // A real reverse keeps going the new way. Lock chatter bounces back.
        if (d1 * d2 > 0f && MathF.Abs(d1) + MathF.Abs(d2) > SpikePx)
            return false;
        return true;
    }

    public static MouseTrajectorySample[] SmoothBothBounded(
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
            // Preserve both boundaries of an intentional hold. Blending either
            // endpoint causes the camera to drift during the recorded pause.
            var current = samples[i];
            if ((current.X == samples[i - 1].X && current.Y == samples[i - 1].Y)
                || (current.X == samples[i + 1].X && current.Y == samples[i + 1].Y))
            {
                output[i] = current;
                continue;
            }
            bool hardX = IsHardReverse(samples[i - 1].X, samples[i].X, samples[i + 1].X);
            bool hardY = IsHardReverse(samples[i - 1].Y, samples[i].Y, samples[i + 1].Y);
            float x = hardX
                ? samples[i].X
                : (samples[i - 1].X + samples[i].X * 2f + samples[i + 1].X) * 0.25f;
            float y = hardY
                ? samples[i].Y
                : (samples[i - 1].Y + samples[i].Y * 2f + samples[i + 1].Y) * 0.25f;
            if (!hardX)
                x = ClampToward(samples[i].X, x, maxError);
            if (!hardY)
                y = ClampToward(samples[i].Y, y, maxError);
            output[i] = new MouseTrajectorySample(samples[i].T, x, y);
        }

        return output;
    }

    /// <summary>
    /// Real look reversals stay sharp. Tiny opposite steps are lock
    /// chatter and may be blended.
    /// </summary>
    public static bool IsHardReverse(float prev, float cur, float next)
    {
        float d0 = cur - prev;
        float d1 = next - cur;
        if (d0 * d1 >= 0f)
            return false;
        if (MathF.Abs(d0) >= HardReverseStepPx && MathF.Abs(d1) >= HardReverseStepPx)
            return true;
        return MathF.Abs(cur) >= HardReverseOriginPx && MathF.Abs(d0) + MathF.Abs(d1) >= 3f;
    }

    private static float ClampToward(float original, float proposed, float maxError)
    {
        float delta = proposed - original;
        if (delta > maxError)
            return original + maxError;
        if (delta < -maxError)
            return original - maxError;
        return proposed;
    }

    public static void SendCapped(
        IInputPlayer player,
        int dx,
        int dy,
        MacroContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (dx == 0 && dy == 0)
            return;

        int leftX = dx;
        int leftY = dy;
        while ((leftX != 0 || leftY != 0) && !context.IsAborted && !ct.IsCancellationRequested)
        {
            SplitStep(leftX, leftY, MaxSendPx, out int sx, out int sy);
            player.SendRelativeMove(sx, sy, dualBackend: false);
            leftX -= sx;
            leftY -= sy;
            if (leftX == 0 && leftY == 0)
                return;
        }
    }

    public static void SplitStep(int dx, int dy, int maxPx, out int sx, out int sy)
    {
        int ax = Math.Abs(dx);
        int ay = Math.Abs(dy);
        int span = ax > ay ? ax : ay;
        if (span <= maxPx)
        {
            sx = dx;
            sy = dy;
            return;
        }

        sx = (int)Math.Round(dx * (double)maxPx / span, MidpointRounding.AwayFromZero);
        sy = (int)Math.Round(dy * (double)maxPx / span, MidpointRounding.AwayFromZero);
        if (sx == 0 && dx != 0)
            sx = dx > 0 ? 1 : -1;
        if (sy == 0 && dy != 0)
            sy = dy > 0 ? 1 : -1;
    }

}
