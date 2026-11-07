using System.Diagnostics;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Steps;

/// <summary>
/// One held-mouse camera look: net displacement over a duration, replayed
/// as a fixed-rate relative stream. Does not press or release the button —
/// surrounding Down / Up steps own the hold.
/// </summary>
public sealed class MouseSweepStep : MacroStep
{
    public int DeltaX { get; init; }
    public int DeltaY { get; init; }
    public int DurationMs { get; init; } = 250;
    public MouseInterpolation Interpolation { get; init; } = MouseInterpolation.SmootherStep;
    public MouseSweepProfile Profile { get; init; } = MouseSweepProfile.Smooth;
    public int PlaybackHz { get; init; } = MouseSweepMotion.DefaultHz;
    public int JitterReduction { get; init; } = MouseSweepMotion.DefaultVerticalSmoothing;
    public int HorizontalSmoothing { get; init; } = MouseSweepMotion.DefaultHorizontalSmoothing;
    public int VerticalSmoothing { get; init; } = MouseSweepMotion.DefaultVerticalSmoothing;
    public bool FilterVerticalJitter { get; init; } = true;
    public bool FilterHorizontalJitter { get; init; }
    public double Sensitivity { get; init; } = 1.0;
    public double SensitivityX { get; init; } = 1.0;
    public double SensitivityY { get; init; } = 1.0;
    public TimeSpan RelativeTimestamp { get; init; } = TimeSpan.Zero;

    public MouseSweepStep()
    {
        Label = "Camera Sweep";
    }

    public MouseSweepStep(int deltaX, int deltaY, int durationMs, TimeSpan relativeTimestamp = default)
    {
        DeltaX = deltaX;
        DeltaY = deltaY;
        DurationMs = Math.Max(16, durationMs);
        RelativeTimestamp = relativeTimestamp;
        Label = $"Sweep dx={deltaX} dy={deltaY} {DurationMs}ms";
    }

    public override Task ExecuteAsync(MacroContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsEnabled || context.IsAborted || ct.IsCancellationRequested)
            return Task.CompletedTask;
        if (context.InputPlayer is null)
            return Task.CompletedTask;

        MouseSweepMotion.ResolveDestination(
            DeltaX, DeltaY, Sensitivity, SensitivityX, SensitivityY,
            out int destX, out int destY);
        MouseSweepMotion.ApplyAxisDeadzone(
            ref destX, ref destY, FilterVerticalJitter, FilterHorizontalJitter);
        if (destX == 0 && destY == 0)
            return Task.CompletedTask;

        PlaySweep(context, context.InputPlayer, destX, destY, ct);
        return Task.CompletedTask;
    }

    private void PlaySweep(
        MacroContext context,
        IInputPlayer player,
        int destX,
        int destY,
        CancellationToken ct)
    {
        int hz = MouseSweepMotion.ClampHz(PlaybackHz);
        int smoothing = MouseSweepMotion.SharedSmoothing(destX, destY, HorizontalSmoothing, VerticalSmoothing);
        int durationMs = Math.Max(16, DurationMs);
        TimeSpan scaled = StepTimeline.Scale(TimeSpan.FromMilliseconds(durationMs), context.PlaybackSpeed);
        durationMs = Math.Max(16, (int)Math.Round(scaled.TotalMilliseconds));

        long freq = Stopwatch.Frequency;
        long durationTicks = (long)(durationMs / 1000.0 * freq);
        long periodTicks = freq / hz;
        if (periodTicks < 1)
            periodTicks = 1;

        using IDisposable? period = context.TimerResolution?.Request(1);
        bool highRes = period != null;

        long start = context.PlaybackSession?.ActiveTimestamp ?? Stopwatch.GetTimestamp();
        long nextTick = start + periodTicks;
        double lastX = 0;
        double lastY = 0;
        double fracX = 0;
        double fracY = 0;

        while (!context.IsAborted && !ct.IsCancellationRequested)
        {
            if (context.Control.IsPaused)
            {
                if (context.PlaybackSession is { } session) { session.WaitUntilReadyAsync(ct).GetAwaiter().GetResult(); continue; }
                long pauseStart = Stopwatch.GetTimestamp();
                while (context.Control.IsPaused && !context.IsAborted && !ct.IsCancellationRequested)
                    Thread.Sleep(8);
                long paused = Stopwatch.GetTimestamp() - pauseStart;
                start += paused;
                nextTick += paused;
                continue;
            }

            long now = context.PlaybackSession?.ActiveTimestamp ?? Stopwatch.GetTimestamp();
            long elapsed = now - start;
            bool last = elapsed >= durationTicks;
            double t = last ? 1.0 : (double)elapsed / durationTicks;
            double progress = MouseSweepMotion.Progress(t, Interpolation, smoothing);
            MouseSweepMotion.Sample(
                destX, destY, progress,
                ref lastX, ref lastY, ref fracX, ref fracY,
                out int mx, out int my);
            if (mx != 0 || my != 0)
                player.SendRelativeMove(mx, my, dualBackend: false);

            if (last)
                break;

            if (context.PlaybackSession != null)
            { if (!HighResolutionScheduler.WaitForTimeline(ref start, nextTick - start, highRes, context, ct)) break; }
            else HighResolutionScheduler.WaitUntil(nextTick, highRes, context, ct);
            nextTick += periodTicks;

            long latest = context.PlaybackSession?.ActiveTimestamp ?? Stopwatch.GetTimestamp();
            if (latest - nextTick > periodTicks)
            {
                long skipped = (latest - start) / periodTicks;
                nextTick = start + (skipped + 1) * periodTicks;
            }
        }
    }
}
