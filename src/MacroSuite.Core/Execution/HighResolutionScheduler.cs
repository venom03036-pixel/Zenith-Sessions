using System.Diagnostics;

namespace MacroSuite.Core.Execution;

/// <summary>
/// Stopwatch-based wait for fixed-rate input loops.
/// Sleeps only when remaining time is larger than the OS sleep quantum;
/// spins for the last slice so 120–240 Hz ticks do not drift.
/// </summary>
public static class HighResolutionScheduler
{
    /// <summary>Blocks until <paramref name="targetTimestamp"/>, abort, or cancel.</summary>
    /// <param name="targetTimestamp">Stopwatch timestamp to wait for.</param>
    /// <param name="highResolutionPeriod">
    /// True when a 1 ms multimedia timer period is active (Sleep(1) is actually ~1 ms).
    /// False uses a 16 ms sleep floor so default Windows 15.6 ms Sleep cannot overshoot a tick.
    /// </param>
    /// <param name="context">Execution context used for panic/stop checks.</param>
    /// <param name="ct">Cancellation token for the current run.</param>
    public static void WaitUntil(
        long targetTimestamp,
        bool highResolutionPeriod,
        MacroContext context,
        CancellationToken ct)
    {
        long freq = Stopwatch.Frequency;
        long sleepFloor = highResolutionPeriod
            ? freq * 2 / 1000
            : freq * 16 / 1000;
        if (sleepFloor < 1)
            sleepFloor = 1;

        while (true)
        {
            if (ct.IsCancellationRequested || context.IsAborted || context.Control.IsPaused)
                return;

            long remaining = targetTimestamp - Stopwatch.GetTimestamp();
            if (remaining <= 0)
                return;

            if (remaining > sleepFloor)
                Thread.Sleep((int)Math.Clamp((remaining - sleepFloor) * 1000 / freq, 1, 8));
            else
                Thread.SpinWait(48);
        }
    }

    /// <summary>Waits on a playback timeline, excluding time spent paused.</summary>
    public static bool WaitForTimeline(
        ref long origin, long offsetTicks, bool highResolutionPeriod,
        MacroContext context, CancellationToken ct)
    {
        if (context.PlaybackSession is { } session)
        {
            try { session.DelayUntilAsync(origin, offsetTicks, ct).GetAwaiter().GetResult(); return !context.IsAborted && !ct.IsCancellationRequested; }
            catch (OperationCanceledException) when (context.IsAborted || ct.IsCancellationRequested) { return false; }
        }
        while (!ct.IsCancellationRequested && !context.IsAborted)
        {
            if (context.Control.IsPaused)
            {
                long pauseStart = Stopwatch.GetTimestamp();
                if (context.EnforceRuntimeCap && context.MaxRuntime > TimeSpan.Zero)
                {
                    TimeSpan remaining = context.MaxRuntime - context.ElapsedRuntime;
                    if (remaining <= TimeSpan.Zero) return false;
                    // A paused macro still observes its wall-clock runtime limit.
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(remaining.TotalMilliseconds, uint.MaxValue - 1)));
                    context.Control.WaitIfPausedAsync(deadline.Token).GetAwaiter().GetResult();
                }
                else
                {
                    context.Control.WaitIfPausedAsync(ct).GetAwaiter().GetResult();
                }
                origin += Stopwatch.GetTimestamp() - pauseStart;
                continue;
            }

            WaitUntil(origin + offsetTicks, highResolutionPeriod, context, ct);
            if (ct.IsCancellationRequested || context.IsAborted)
                return false;
            if (!context.Control.IsPaused)
                return true;
        }
        return false;
    }
}
