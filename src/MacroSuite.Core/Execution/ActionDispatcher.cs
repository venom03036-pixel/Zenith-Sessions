using System.Diagnostics;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Execution;

/// <summary>
/// Shared execution boundary for top-level and nested actions (pause, panic, trace, unknown-type errors).
/// </summary>
public static class ActionDispatcher
{
    public static async Task ExecuteAsync(MacroStep step, MacroContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(context);

        if (!step.IsEnabled)
            return;

        if (context.IsAborted || ct.IsCancellationRequested || context.PendingNavigation is not null)
            return;

        if (context.Control.IsPaused)
            await context.Control.WaitIfPausedAsync(ct).ConfigureAwait(false);

        if (context.IsAborted || ct.IsCancellationRequested || context.PendingNavigation is not null)
            return;

        context.WriteTrace(
            "ActionDispatcher",
            $"Resolved {step.GetType().Name}.",
            actionId: step.StepId);

        long start = Stopwatch.GetTimestamp();
        try
        {
            await step.ExecuteAsync(context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RecoverableBlockFailureException ex)
        {
            context.WriteTrace("ControlFlow", ex.Message, ExecutionTraceLevel.Warning, step.StepId, exception: ex);
            throw;
        }
        catch (Exception ex)
        {
            TimeSpan failedAfter = TimeSpan.FromSeconds((double)(Stopwatch.GetTimestamp() - start) / Stopwatch.Frequency);
            context.WriteTrace(
                "ActionDispatcher",
                $"Action '{step.Label}' failed after {failedAfter.TotalMilliseconds:F2} ms.",
                ExecutionTraceLevel.Error,
                step.StepId,
                exception: ex);
            context.PublishStatus(new ExecutionStatusSnapshot
            {
                ActionId = step.StepId,
                Phase = "ActionFailed",
                Message = ex.Message,
                Elapsed = failedAfter,
                Detection = context.LastColorDetection,
                Visual = context.LastVisualDiagnostics
            });
            throw;
        }

        TimeSpan duration = TimeSpan.FromSeconds((double)(Stopwatch.GetTimestamp() - start) / Stopwatch.Frequency);
        context.WriteTrace(
            "ActionDispatcher",
            $"Completed {step.GetType().Name} in {duration.TotalMilliseconds:F2} ms.",
            actionId: step.StepId,
            data: duration);
        context.PublishStatus(new ExecutionStatusSnapshot
        {
            ActionId = step.StepId,
            Phase = "ActionCompleted",
            Message = step.Label,
            Elapsed = duration,
            Detection = context.LastColorDetection,
            Visual = context.LastVisualDiagnostics
        });
    }
}
