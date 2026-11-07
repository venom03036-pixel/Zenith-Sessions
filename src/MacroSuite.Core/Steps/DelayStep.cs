using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Steps;

/// <summary>
/// Pauses execution for a specified duration with explicit cancellation and abort checking (Rule 12).
/// </summary>
public sealed class DelayStep : MacroStep
{
    /// <summary>Duration to wait.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Constructs a default delay step.</summary>
    public DelayStep()
    {
        Duration = TimeSpan.FromMilliseconds(100);
        Label = "Delay 100ms";
    }

    /// <summary>Constructs a delay step with specified duration.</summary>
    public DelayStep(TimeSpan duration)
    {
        Duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        Label = $"Delay {duration.TotalMilliseconds:N0}ms";
    }

    /// <inheritdoc/>
    public override async Task ExecuteAsync(MacroContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!IsEnabled || context.IsAborted || ct.IsCancellationRequested || Duration <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            TimeSpan remaining = StepTimeline.Scale(Duration, context.PlaybackSpeed);
            if (context.PlaybackSession != null) { await context.DelayAsync(remaining, ct).ConfigureAwait(false); return; }
            while (remaining > TimeSpan.Zero && !context.IsAborted && !ct.IsCancellationRequested)
            {
                if (context.Control.IsPaused)
                {
                    await context.Control.WaitIfPausedAsync(ct).ConfigureAwait(false);
                    continue;
                }

                TimeSpan slice = remaining > TimeSpan.FromMilliseconds(50)
                    ? TimeSpan.FromMilliseconds(50)
                    : remaining;
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                await Task.Delay(slice, ct).ConfigureAwait(false);
                remaining -= System.Diagnostics.Stopwatch.GetElapsedTime(started);
            }
        }
        catch (OperationCanceledException)
        {
            // Clean cancellation exit
        }
    }
}
