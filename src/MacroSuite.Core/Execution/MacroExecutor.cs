using System.Diagnostics;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;

namespace MacroSuite.Core.Execution;

/// <summary>
/// Execution state reported during macro execution.
/// </summary>
public enum ExecutionState
{
    Idle,
    Running,
    Paused,
    Completed,
    Stopped,
    Aborted,
    Faulted
}

/// <summary>
/// Deterministic macro execution engine (Rule 1, Rule 2).
/// Walks <see cref="MacroStep"/> items sequentially, checking safety boundaries, pause gates, and panic signals before every action.
/// </summary>
public sealed class MacroExecutor
{
    /// <summary>Fired when execution state changes.</summary>
    public event EventHandler<ExecutionState>? StateChanged;

    /// <summary>Fired before each step executes, providing the step index and model.</summary>
    public event EventHandler<(int StepIndex, MacroStep Step)>? StepStarting;

    /// <summary>Fired after each step completes.</summary>
    public event EventHandler<(int StepIndex, MacroStep Step, TimeSpan Duration)>? StepCompleted;

    /// <summary>Current state of the executor.</summary>
    public ExecutionState State { get; private set; } = ExecutionState.Idle;

    /// <summary>
    /// Executes the specified <paramref name="macro"/> using the provided <paramref name="context"/>.
    /// </summary>
    public async Task ExecuteAsync(MacroModel macro, MacroContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(macro);
        ArgumentNullException.ThrowIfNull(context);

        SetState(ExecutionState.Running);
        context.WriteTrace("MacroExecutor", "Macro started.");

        bool repeatUntilStopped = macro.Settings.RepeatUntilStopped;
        int repeatCount = Math.Max(1, macro.Settings.RepeatCount);
        int totalIterations = 0;
        int maxIterations = Math.Min(macro.Settings.MaxIterations, context.MaxIterations);
        bool timedOut = false;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, context.Control.StopToken, context.PanicToken);
        if (!repeatUntilStopped && context.EnforceRuntimeCap && context.MaxRuntime > TimeSpan.Zero)
        {
            double remainingMs = (context.MaxRuntime - context.ElapsedRuntime).TotalMilliseconds;
            linkedCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(remainingMs, 0, uint.MaxValue - 1d)));
        }
        CancellationToken effectiveCt = linkedCts.Token;

        try
        {
            context.ActivationService?.RequireActive();
            context.FeatureGate?.Require(Licensing.FeatureId.BasicPlayback);
            macro.Settings.Target?.Validate();
            if (macro.Settings.Target != null && context.PlaybackSession?.Target?.Rule != macro.Settings.Target)
                throw new InvalidOperationException("Resolve this macro's selected target before starting playback.");
            RepeatingImageDispatcher.Validate(macro.Steps);
            BlockRouting.Validate(macro.Steps);
            AppearanceRedirectCoordinator.Validate(macro.Steps);
            await using var observer = new AppearanceRedirectCoordinator(macro.Steps, context, effectiveCt);
            int repeat = 0;
            while (true)
            {
                if (ShouldHaltRun(context, effectiveCt, repeatUntilStopped, out timedOut))
                    break;

                if (!repeatUntilStopped && repeat >= repeatCount)
                    break;

                var timeline = new TimelineCursor();
                bool haltPass = false;

                for (int stepIndex = 0; stepIndex < macro.Steps.Count; stepIndex++)
                {
                    // 1. Check emergency stop / normal stop / timeout boundary (Rule 11)
                    if (ShouldHaltRun(context, effectiveCt, repeatUntilStopped, out timedOut))
                    {
                        haltPass = true;
                        break;
                    }

                    // 2. Pause gate: suspend here if paused
                    if (context.Control.IsPaused)
                    {
                        SetState(ExecutionState.Paused);
                        await context.Control.WaitIfPausedAsync(effectiveCt).ConfigureAwait(false);
                        SetState(ExecutionState.Running);
                    }

                    // 3. Re-check abort after pause resume
                    if (ShouldHaltRun(context, effectiveCt, repeatUntilStopped, out timedOut))
                    {
                        haltPass = true;
                        break;
                    }

                    MacroStep step = macro.Steps[stepIndex];
                    if (step is WaitUntilStep { AppearanceRedirect: not null }) continue;

                    // 4. Enforce iteration ceiling (Rule 18) — skipped for repeat-until-stopped
                    if (!repeatUntilStopped && ++totalIterations > maxIterations)
                    {
                        throw new InvalidOperationException($"Execution exceeded maximum allowable iterations ({maxIterations}).");
                    }

                    bool monitoring = RepeatingImageDispatcher.StartsMonitoring(step);
                    AppearanceRedirectMatch? redirect = await observer.ExecuteActionAsync(async pathCt =>
                    {
                    if (monitoring)
                    {
                        // The first trigger was just counted at the execution boundary;
                        // its dispatcher counts it with the block, so give that one back.
                        int remainingBudget = repeatUntilStopped
                            ? maxIterations
                            : Math.Max(0, maxIterations - totalIterations + 1);
                        if (!repeatUntilStopped)
                            totalIterations--;
                        await RepeatingImageDispatcher.ExecuteAsync(macro.Steps, stepIndex, context, pathCt,
                            enforceIterationLimit: !repeatUntilStopped, maxExecutions: remainingBudget,
                            executionCount: repeatUntilStopped ? null : count => totalIterations += count).ConfigureAwait(false);
                    }
                    else
                    {
                        if (!step.IsEnabled) return;

                        await WaitForRecordedGapAsync(step, context, pathCt, timeline).ConfigureAwait(false);
                        pathCt.ThrowIfCancellationRequested();

                        StepStarting?.Invoke(this, (stepIndex, step));
                        long startTicks = Stopwatch.GetTimestamp();
                        if (step is MouseMoveStep { IsRelative: true } && context.InputPlayer is not null)
                        {
                            stepIndex = await PlayRelativeMoveRunAsync(
                                macro.Steps, stepIndex, macro.Steps.Count, context, pathCt, timeline).ConfigureAwait(false);
                        }
                        else
                        {
                            await ActionDispatcher.ExecuteAsync(step, context, pathCt).ConfigureAwait(false);
                        }
                        long elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
                        TimeSpan duration = TimeSpan.FromSeconds((double)elapsedTicks / Stopwatch.Frequency);

                        StepCompleted?.Invoke(this, (stepIndex, step, duration));
                    }
                    }, effectiveCt).ConfigureAwait(false);
                    if (redirect is not null)
                    {
                        effectiveCt.ThrowIfCancellationRequested();
                        context.ConsumeControlFlow();
                        context.ConsumeNavigation();
                        context.RequestNavigation(new(NavigationKind.Jump, redirect.SourceStepId, redirect.TargetStepId, "Image appeared"));
                    }
                    else if (monitoring)
                    {
                        if (ShouldHaltRun(context, effectiveCt, repeatUntilStopped, out timedOut)) haltPass = true;
                        if (haltPass || context.PendingNavigation is null) break;
                    }
                    if (context.PendingNavigation is { } navigation)
                    {
                        context.ConsumeNavigation();
                        if (ShouldHaltRun(context, effectiveCt, repeatUntilStopped, out timedOut)
                            || navigation.Kind == NavigationKind.Stop)
                        {
                            haltPass = true;
                            break;
                        }
                        int destination = navigation.Kind == NavigationKind.NextConditional
                            ? BlockRouting.FindNextConditional(macro.Steps, stepIndex, macro.Steps.Count)
                            : FindRootDestination(macro.Steps, navigation.TargetStepId);
                        // The abandoned block has fully returned before cleanup/entry. Only
                        // the controlled player releases owned input, never physical keys.
                        context.InputPlayer?.ReleaseAllHeldKeys();
                        context.WriteTrace("ControlFlow", destination < 0
                            ? "No eligible conditional remains; completing macro."
                            : $"Entering action {macro.Steps[destination].StepId} after {navigation.Kind}.",
                            actionId: navigation.SourceStepId, data: navigation);
                        if (destination < 0)
                        {
                            haltPass = true;
                            break;
                        }
                        timeline = new TimelineCursor { RebaseNextTimestamp = true };
                        stepIndex = destination - 1;
                    }
                }

                if (haltPass)
                    break;

                repeat++;
                if (!repeatUntilStopped && repeat >= repeatCount)
                    break;
            }

            if (timedOut)
            {
                context.WriteTrace(
                    "MacroExecutor",
                    $"Macro reached the maximum runtime cap ({context.MaxRuntime}).",
                    ExecutionTraceLevel.Warning);
            }

            if (context.Control.PanicToken.IsCancellationRequested)
            {
                SetState(ExecutionState.Aborted);
            }
            else if (context.Control.StopToken.IsCancellationRequested
                     || effectiveCt.IsCancellationRequested
                     || timedOut)
            {
                SetState(ExecutionState.Stopped);
            }
            else
            {
                SetState(ExecutionState.Completed);
            }
            context.WriteTrace("MacroExecutor", $"Macro finished with state {State}.");
        }
        catch (OperationCanceledException)
        {
            SetState(context.Control.PanicToken.IsCancellationRequested ? ExecutionState.Aborted : ExecutionState.Stopped);
        }
        catch (Exception ex)
        {
            SetState(ExecutionState.Faulted);
            context.WriteTrace(
                "MacroExecutor",
                "Macro execution faulted.",
                ExecutionTraceLevel.Error,
                exception: ex);
            throw;
        }
        finally
        {
            // End of this run (finished, stopped, or faulted). Panic already released.
            try
            {
                context.InputPlayer?.ReleaseAllHeldKeys();
            }
            catch (Exception ex)
            {
                context.WriteTrace(
                    "Input",
                    "Failed to release held inputs during shutdown.",
                    ExecutionTraceLevel.Error,
                    exception: ex);
            }
        }
    }

    private sealed class TimelineCursor
    {
        public TimeSpan LastTimestamp;
        public bool HasTimestamp;
        public bool RebaseNextTimestamp;
        public bool HasAnchor;
        public long AnchorTick;
        public TimeSpan TimestampOrigin;
        public TimeSpan PausedAtAnchor;
    }

    // Repeating blocks use the existing timing and input batching paths, with their own
    // timeline on each activation. This avoids adding a second input executor.
    internal static async Task ExecuteActionBlockAsync(
        IReadOnlyList<MacroStep> steps, int startIndex, int endIndex,
        MacroContext context, CancellationToken ct, Action<int>? countActions = null)
    {
        var timeline = new TimelineCursor { RebaseNextTimestamp = true };
        for (int i = startIndex; i < endIndex; i++)
        {
            if (context.IsAborted || ct.IsCancellationRequested)
                return;
            if (context.Control.IsPaused)
                await context.Control.WaitIfPausedAsync(ct).ConfigureAwait(false);
            if (context.IsAborted || ct.IsCancellationRequested)
                return;
            MacroStep step = steps[i];
            if (RepeatingImageDispatcher.StartsMonitoring(step))
            {
                await RepeatingImageDispatcher.ExecuteAsync(steps, i, context, ct).ConfigureAwait(false);
                return;
            }
            if (step is WaitUntilStep { AppearanceRedirect: not null })
                throw new InvalidOperationException("Appearance redirects must be declared at the top level.");
            if (!step.IsEnabled)
                continue;
            await WaitForRecordedGapAsync(step, context, ct, timeline).ConfigureAwait(false);
            // A bounded status sink keeps nested execution visible without creating a
            // second execution or capture path. Mouse move batches stay allocation-light.
            if (step is not MouseMoveStep && context.StatusSink is not null)
                context.PublishStatus(new ExecutionStatusSnapshot { ActionId = step.StepId, Phase = "Executing" });
            if (step is MouseMoveStep { IsRelative: true } && context.InputPlayer is not null)
            {
                int runLength = 1;
                while (i + runLength < endIndex && steps[i + runLength] is MouseMoveStep { IsRelative: true, IsEnabled: true })
                    runLength++;
                countActions?.Invoke(runLength);
                i = await PlayRelativeMoveRunAsync(steps, i, endIndex, context, ct, timeline).ConfigureAwait(false);
            }
            else
            {
                countActions?.Invoke(1);
                await ActionDispatcher.ExecuteAsync(step, context, ct).ConfigureAwait(false);
            }
            if (context.PendingControlFlow != ControlFlowSignal.None)
                return;
            if (context.PendingNavigation is { } navigation)
            {
                if (navigation.Kind != NavigationKind.NextConditional) return;
                int destination = BlockRouting.FindNextConditional(steps, i, endIndex);
                if (destination < 0) return; // Let the containing block unwind to its parent scope.
                context.ConsumeNavigation();
                context.InputPlayer?.ReleaseAllHeldKeys();
                context.WriteTrace("ControlFlow", $"Continuing at sibling conditional {steps[destination].StepId}.",
                    actionId: navigation.SourceStepId, data: navigation);
                timeline = new TimelineCursor { RebaseNextTimestamp = true };
                i = destination - 1;
            }
        }
    }

    private static async Task WaitForRecordedGapAsync(
        MacroStep step,
        MacroContext context,
        CancellationToken ct,
        TimelineCursor timeline)
    {
        bool recorded = StepTimeline.TryGetRelativeTimestamp(step, out TimeSpan timestamp);
        // Zero is also the first instant of a recording. These input primitives have
        // no separate 'recorded' flag; their own manual delays still execute normally.
        if (!recorded && step is not (ClickStep or MouseMoveStep or KeyPressStep or MouseWheelStep or MouseSweepStep))
        {
            // A manual delay, wait, or conditional is an intentional barrier. Preserve
            // the following recorded gap, but never charge its duration as drift.
            timeline.HasAnchor = false;
            return;
        }

        if (!timeline.HasAnchor)
        {
            timeline.TimestampOrigin = timeline.RebaseNextTimestamp
                ? timestamp : timeline.HasTimestamp ? timeline.LastTimestamp : TimeSpan.Zero;
            timeline.AnchorTick = Stopwatch.GetTimestamp();
            timeline.PausedAtAnchor = context.Control.TotalPausedDuration;
            timeline.HasAnchor = true;
            timeline.RebaseNextTimestamp = false;
        }

        TimeSpan offset = StepTimeline.Scale(timestamp - timeline.TimestampOrigin, context.PlaybackSpeed);
        timeline.LastTimestamp = StepTimeline.GetEndTimestamp(step, timestamp);
        timeline.HasTimestamp = true;

        while (!ct.IsCancellationRequested && !context.IsAborted)
        {
            if (context.Control.IsPaused)
            {
                await context.Control.WaitIfPausedAsync(ct).ConfigureAwait(false);
                continue;
            }

            TimeSpan paused = context.Control.TotalPausedDuration - timeline.PausedAtAnchor;
            TimeSpan activeElapsed = Stopwatch.GetElapsedTime(timeline.AnchorTick) - paused;
            TimeSpan remaining = offset - activeElapsed;
            if (remaining <= TimeSpan.Zero) return;
            if (remaining > TimeSpan.FromMilliseconds(20))
            {
                TimeSpan slice = remaining - TimeSpan.FromMilliseconds(8);
                await Task.Delay(slice > TimeSpan.FromMilliseconds(40) ? TimeSpan.FromMilliseconds(40) : slice, ct).ConfigureAwait(false);
            }
            else
            {
                PrecisionWait(remaining, context, ct);
            }
        }
    }

    /// <summary>
    /// Plays consecutive relative look/drag moves as one timed stream so the
    /// camera is not broken up by per-step Task.Delay and UI selection.
    /// </summary>
    private static async Task<int> PlayRelativeMoveRunAsync(
        IReadOnlyList<MacroStep> steps,
        int startIndex,
        int endIndex,
        MacroContext context,
        CancellationToken ct,
        TimelineCursor timeline)
    {
        var events = new List<InputEvent>(32);
        int lastIndex = startIndex;

        for (int i = startIndex; i < endIndex; i++)
        {
            if (steps[i] is not MouseMoveStep move || !move.IsRelative || !move.IsEnabled)
                break;

            lastIndex = i;
            // Zero movement still marks time. Dropping the first or final row
            // would make the player rebase the batch and shorten recorded pauses.
            events.Add(new InputEvent(
                InputEventType.MouseMoveRelative,
                ToPlaybackTicks(move.RelativeTimestamp),
                move.Position.X,
                move.Position.Y,
                0,
                0));
        }

        if (events.Count > 0 && context.InputPlayer is not null)
            await context.InputPlayer.PlayAsync(events, context.ToPlaybackOptions(), ct).ConfigureAwait(false);

        if (StepTimeline.TryGetRelativeTimestamp(steps[lastIndex], out TimeSpan lastTs))
        {
            timeline.LastTimestamp = lastTs;
            timeline.HasTimestamp = true;
        }

        return lastIndex;
    }

    private static long ToPlaybackTicks(TimeSpan timestamp)
        => timestamp <= TimeSpan.Zero
            ? 0
            : (long)(timestamp.TotalSeconds * Stopwatch.Frequency);

    private static int FindRootDestination(IReadOnlyList<MacroStep> steps, Guid? target)
    {
        for (int i = 0; i < steps.Count; i++)
            if (steps[i].StepId == target && steps[i].IsEnabled
                && steps[i] is not WaitUntilStep { AppearanceRedirect: not null }) return i;
        throw new InvalidOperationException("Jump target is missing, disabled, or not a supported top-level action.");
    }

    private static void PrecisionWait(TimeSpan wait, MacroContext context, CancellationToken ct)
    {
        long target = Stopwatch.GetTimestamp() + (long)(wait.TotalSeconds * Stopwatch.Frequency);
        long sleepFloor = Stopwatch.Frequency * 8 / 1000;
        while (Stopwatch.GetTimestamp() < target)
        {
            if (ct.IsCancellationRequested || context.IsAborted || context.Control.IsPaused)
                return;
            if (target - Stopwatch.GetTimestamp() > sleepFloor)
                Thread.Sleep(1);
            else
                Thread.SpinWait(8);
        }
    }

    private static bool ShouldHaltRun(
        MacroContext context,
        CancellationToken ct,
        bool repeatUntilStopped,
        out bool timedOut)
    {
        if (context.Control.PanicToken.IsCancellationRequested
            || context.Control.StopToken.IsCancellationRequested
            || ct.IsCancellationRequested)
        {
            timedOut = false;
            return true;
        }

        if (!repeatUntilStopped && context.HasExceededMaxRuntime)
        {
            timedOut = true;
            return true;
        }

        timedOut = false;
        return false;
    }

    private void SetState(ExecutionState state)
    {
        if (State != state)
        {
            State = state;
            StateChanged?.Invoke(this, state);
        }
    }
}
