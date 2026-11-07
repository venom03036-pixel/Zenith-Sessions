using System.Drawing;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;

namespace MacroSuite.Core.Execution;

/// <summary>
/// Optional image-trigger suffix of one action list. One awaited scan owns frame acquisition
/// and executes matching blocks serially; no watcher creates a task or capture loop.
/// </summary>
public static partial class RepeatingImageDispatcher
{
    private static long MonotonicMilliseconds => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1000d / System.Diagnostics.Stopwatch.Frequency);
    public static bool StartsMonitoring(MacroStep step)
        => step is WaitUntilStep { RepeatWhileVisible: true, Condition: ImageCondition };

    /// <summary>Reject ambiguous layouts before setup actions or input can run.</summary>
    public static void Validate(IReadOnlyList<MacroStep> steps, bool insideRepeatingBlock = false)
    {
        bool monitoring = false;
        foreach (MacroStep step in steps)
        {
            // Run-wide appearance declarations are not sequential waits or block delimiters.
            if (step is WaitUntilStep { AppearanceRedirect: not null }) continue;
            if (step is WaitUntilStep { RepeatWhileVisible: true } wait)
            {
                if (wait.Condition is not ImageCondition || wait.WaitWhile)
                    throw new InvalidOperationException("Repeat while visible requires a positive Wait Image action (Wait Until, not Wait While).");
                if (insideRepeatingBlock)
                    throw new InvalidOperationException("A repeating image block cannot contain another repeating monitor. Put those Wait Image triggers beside each other so all images can be checked fairly.");
                monitoring = true;
            }
            else if (monitoring && step is WaitUntilStep { Condition: ImageCondition, IsEnabled: true })
            {
                throw new InvalidOperationException("Every enabled Wait Image after a repeating image trigger must also have Repeat while visible enabled. Move one-time waits before the repeating section.");
            }

            switch (step)
            {
                case IfThenElseStep branch:
                    Validate(branch.ThenSteps, insideRepeatingBlock || monitoring);
                    Validate(branch.ElseSteps, insideRepeatingBlock || monitoring);
                    break;
                case LoopStep loop:
                    Validate(loop.BodySteps, insideRepeatingBlock || monitoring);
                    break;
            }
        }
    }

    /// <summary>
    /// Runs from the first repeating trigger to this list's end. Image waits delimit blocks,
    /// including disabled waits; disabling a trigger suppresses its entire block.
    /// </summary>
    public static Task ExecuteAsync(
        IReadOnlyList<MacroStep> steps, int startIndex, MacroContext context, CancellationToken ct,
        int? maxExecutions = null, long deadlineTick = long.MaxValue, Action<int>? executionCount = null)
        => ExecuteAsync(steps, startIndex, context, ct, enforceIterationLimit: true,
            maxExecutions: maxExecutions, deadlineTick: deadlineTick, executionCount: executionCount);

    public static async Task ExecuteAsync(
        IReadOnlyList<MacroStep> steps, int startIndex, MacroContext context, CancellationToken ct,
        bool enforceIterationLimit, int? maxExecutions = null, long deadlineTick = long.MaxValue,
        Action<int>? executionCount = null)
    {
        // Remove declarations once for this invocation, preserving every following action's
        // owning repeat block. No per-scan allocation or independent capture worker is added.
        for (int declarationIndex = startIndex; declarationIndex < steps.Count; declarationIndex++)
        {
            if (steps[declarationIndex] is not WaitUntilStep { AppearanceRedirect: not null }) continue;
            var actions = new List<MacroStep>(steps.Count - startIndex);
            for (int i = startIndex; i < steps.Count; i++)
                if (steps[i] is not WaitUntilStep { AppearanceRedirect: not null }) actions.Add(steps[i]);
            steps = actions;
            startIndex = 0;
            break;
        }
        var groups = new List<TriggerGroup>();
        for (int i = startIndex; i < steps.Count;)
        {
            var wait = (WaitUntilStep)steps[i];
            int next = i + 1;
            while (next < steps.Count && steps[next] is not WaitUntilStep { Condition: ImageCondition })
                next++;
            if (wait.IsEnabled)
                groups.Add(new TriggerGroup(wait, i + 1, next));
            i = next;
        }
        if (groups.Count == 0)
            return;

        int executions = 0;
        int nextGroup = 0;
        int cap = Math.Min(maxExecutions ?? context.MaxIterations, context.MaxIterations);
        void CountActions(int count)
        {
            // Root repeat-until-stopped playback has no action budget. Nested
            // monitors retain the default cap, and unlimited counters cannot overflow.
            if (!enforceIterationLimit)
                return;
            if (count > cap - executions)
                throw new InvalidOperationException($"Repeating image actions exceeded maximum allowable iterations ({cap}).");
            executions += count;
            executionCount?.Invoke(count);
        }
        if (context.EnforceRuntimeCap && context.MaxRuntime > TimeSpan.Zero)
        {
            double remainingMs = (context.MaxRuntime - context.ElapsedRuntime).TotalMilliseconds;
            // TickCount64 can advance in a coarse OS quantum. Use a monotonic
            // high-resolution deadline so it cannot finish before the runtime cap.
            deadlineTick = Math.Min(deadlineTick, MonotonicMilliseconds + (long)Math.Max(0, Math.Ceiling(remainingMs)) + 1);
        }
        using var duration = deadlineTick == long.MaxValue ? null
            : new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(0, deadlineTick - MonotonicMilliseconds)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, context.Control.StopToken, context.PanicToken, duration?.Token ?? CancellationToken.None);
        CancellationToken token = linked.Token;
        try
        {
            if (groups.Any(group => group.Image.ScanOptions is not null))
            {
                await ExecuteContinuousAsync(steps, groups, context, token, CountActions).ConfigureAwait(false);
                return;
            }
            while (!context.IsAborted && !token.IsCancellationRequested && MonotonicMilliseconds < deadlineTick)
            {
                if (context.Control.IsPaused)
                {
                    await context.Control.WaitIfPausedAsync(token).ConfigureAwait(false);
                    // A paused interval is not evidence that an image remained visible.
                    foreach (var group in groups)
                    {
                        group.ResetStreak();
                        group.NextCheck = 0;
                    }
                }
                if (context.IsAborted || token.IsCancellationRequested || MonotonicMilliseconds >= deadlineTick)
                    return;

                long now = MonotonicMilliseconds;
                Rectangle union = Rectangle.Empty;
                bool fullScreen = false;
                bool anyDue = false;
                foreach (var group in groups)
                {
                    group.Ready = false;
                    group.Due = now >= group.NextCheck;
                    if (!group.Due)
                        continue;
                    anyDue = true;
                    // Repeat Image watches the screen unless the user chose an explicit
                    // region. A compact overlay taking focus must not shrink every watch
                    // to its client area; one-shot ImageCondition keeps its own fallback.
                    group.Region = group.Image.SearchRegion is { Width: > 0, Height: > 0 } searchRegion
                        ? searchRegion : null;
                    if (group.Region is { } region)
                        union = union.IsEmpty ? region : Rectangle.Union(union, region);
                    else
                        fullScreen = true;
                }

                if (anyDue)
                {
                    // All due images inspect the same frame. Frame ownership ends before input.
                    using (CaptureFrame frame = fullScreen
                        ? await context.CaptureService.CaptureScreenAsync(token).ConfigureAwait(false)
                        : await context.CaptureService.CaptureRegionAsync(union, token).ConfigureAwait(false))
                    {
                        foreach (var group in groups)
                        {
                            if (!group.Due)
                                continue;
                            if (context.Control.IsPaused || context.IsAborted || token.IsCancellationRequested)
                                break;
                            Rectangle? relative = null;
                            if (group.Region is { } region)
                            {
                                Rectangle clipped = Rectangle.Intersect(region,
                                    new Rectangle(frame.OriginX, frame.OriginY, frame.Width, frame.Height));
                                if (clipped.Width <= 0 || clipped.Height <= 0)
                                    throw new InvalidOperationException($"Wait Image '{group.Wait.Label}' has a search region outside the captured screen.");
                                relative = new Rectangle(clipped.X - frame.OriginX, clipped.Y - frame.OriginY, clipped.Width, clipped.Height);
                            }
                            ConditionEvaluation result = await group.Image.EvaluateFrameDetailedAsync(context, frame, relative, token).ConfigureAwait(false);
                            result.ThrowIfFailed($"Repeating Wait Image '{group.Wait.Label}'");
                            now = MonotonicMilliseconds;
                            group.NextCheck = now + group.PollMs;
                            if (!result.IsSatisfied)
                            {
                                group.ResetStreak();
                                continue;
                            }

                            if (group.Confirmations == 0)
                                group.StreakStarted = now;
                            group.Confirmations = Math.Min(group.Confirmations + 1, group.RequiredConfirmations);
                            group.Ready = group.Confirmations >= group.RequiredConfirmations
                                && now - group.StreakStarted >= group.StabilityMs
                                && now >= group.NextExecution;
                            if (group.Ready)
                            {
                                group.X = context.Variables.Get<int>(ImageCondition.LastMatchXVariable);
                                group.Y = context.Variables.Get<int>(ImageCondition.LastMatchYVariable);
                                group.Confidence = context.Variables.Get<double>(MacroVariableNames.LastDetectedConfidence);
                            }
                        }
                    }

                    // Run one block from this snapshot. The next turn captures again because
                    // this body may hide or move another image. Round-robin selection gives
                    // each ready trigger a turn without overlapping bodies or queuing input.
                    for (int offset = 0; offset < groups.Count; offset++)
                    {
                        int groupIndex = (nextGroup + offset) % groups.Count;
                        TriggerGroup group = groups[groupIndex];
                        if (context.Control.IsPaused || context.IsAborted || token.IsCancellationRequested
                            || MonotonicMilliseconds >= deadlineTick)
                            break;
                        if (!group.Ready)
                            continue;
                        CountActions(1);
                        ImageCondition.StoreMatch(context, group.X, group.Y, group.Confidence);
                        context.PublishStatus(new ExecutionStatusSnapshot
                        {
                            ActionId = group.Wait.StepId,
                            Phase = "ImageTrigger",
                            Message = "Image visible; running its action block."
                        });
                        await DetectionCursorMover.MoveToLastDetectionAsync(context, token, requireCoordinates: true).ConfigureAwait(false);
                        await MacroExecutor.ExecuteActionBlockAsync(steps, group.Start, group.End, context, token, CountActions).ConfigureAwait(false);
                        group.NextExecution = MonotonicMilliseconds + group.RepeatDelayMs;
                        nextGroup = (groupIndex + 1) % groups.Count;
                        // Other ready groups have earned a turn, but this body may have
                        // changed their images. Revalidate them promptly instead of losing
                        // their turn until the next (possibly much slower) polling interval.
                        foreach (TriggerGroup pending in groups)
                            if (pending != group && pending.Ready) pending.NextCheck = 0;
                        if (context.PendingControlFlow != ControlFlowSignal.None || context.PendingNavigation is not null)
                            return;
                        break;
                    }
                }

                if (context.IsAborted || token.IsCancellationRequested)
                    return;
                long nextCheck = long.MaxValue;
                foreach (var group in groups)
                    nextCheck = Math.Min(nextCheck, group.NextCheck);
                // Bounded sleep also checks runtime and pause promptly when polling is slow.
                int delay = (int)Math.Clamp(nextCheck - MonotonicMilliseconds, 1, 50);
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (duration?.IsCancellationRequested == true
            && !ct.IsCancellationRequested && !context.Control.StopToken.IsCancellationRequested
            && !context.PanicToken.IsCancellationRequested)
        {
            // A runtime cap ends monitoring even while paused. External cancellation
            // and panic are never consumed by this local deadline.
        }
    }

    private sealed class TriggerGroup
    {
        public WaitUntilStep Wait { get; }
        public ImageCondition Image { get; }
        public int Start { get; }
        public int End { get; }
        public int PollMs { get; }
        public int RequiredConfirmations { get; }
        public int StabilityMs { get; }
        public int RepeatDelayMs { get; }
        public long NextCheck;
        public long NextExecution;
        public long StreakStarted;
        public int Confirmations;
        public bool Due;
        public bool Ready;
        public Rectangle? Region;
        public int X;
        public int Y;
        public double Confidence;

        public TriggerGroup(WaitUntilStep wait, int start, int end)
        {
            Wait = wait;
            Image = (ImageCondition)wait.Condition;
            Start = start;
            End = end;
            PollMs = (int)Math.Clamp(wait.PollInterval.TotalMilliseconds, 10, 10_000);
            RequiredConfirmations = ConditionReliability.ClampConfirmations(wait.RequiredConfirmations);
            StabilityMs = ConditionReliability.ClampStabilityMs(wait.StabilityDurationMs);
            RepeatDelayMs = Math.Clamp(wait.RepeatDelayMs, 50, 60_000);
        }

        public void ResetStreak()
        {
            Confirmations = 0;
            StreakStarted = 0;
            Ready = false;
        }
    }
}
