using System.Drawing;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;

namespace MacroSuite.Core.Execution;

/// <summary>
/// One run-owned observer for all appearance redirects. All due templates share one
/// frame from the application's capture service. A redirect cancels and joins the old
/// action before returning a destination; there can never be two active input paths.
/// </summary>
internal sealed class AppearanceRedirectCoordinator : IAsyncDisposable
{
    private readonly MacroContext _context;
    private readonly MacroContext _detectionContext;
    private readonly List<Watch> _watches = new();
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _runToken;
    private Task<AppearanceRedirectMatch>? _pending;
    private readonly TaskCompletionSource _firstScan = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static bool IsDeclaration(MacroStep step)
        => step is WaitUntilStep { AppearanceRedirect: not null };

    internal static void Validate(IReadOnlyList<MacroStep> steps)
    {
        // Identity validation is needed only when declarations select a destination.
        // Preserve positional playback of older macros that reused an action ID.
        bool hasDeclarations = steps.Any(IsDeclaration);
        var ids = new HashSet<Guid>();
        int count = 0;
        foreach (MacroStep step in steps)
        {
            if (hasDeclarations && !ids.Add(step.StepId))
                throw new InvalidOperationException("Root action IDs must be unique for navigation.");
            if (step is WaitUntilStep { AppearanceRedirect: { } redirect } wait)
            {
                if (wait.Condition is not ImageCondition image || wait.WaitWhile || wait.RepeatWhileVisible)
                    throw new InvalidOperationException("An appearance redirect requires a positive Wait Image with Repeat while visible turned off.");
                if (image.TemplateBytes is not { Length: > 0 } && string.IsNullOrWhiteSpace(image.TemplatePath))
                    throw new InvalidOperationException("Capture or choose an image before enabling an appearance redirect.");
                if (redirect.CooldownMs is < 50 or > 60_000)
                    throw new InvalidOperationException("Appearance redirect delay must be between 50 and 60000 ms.");
                MacroStep? target = steps.FirstOrDefault(s => s.StepId == redirect.TargetStepId);
                if (target is null || !target.IsEnabled || IsDeclaration(target))
                    throw new InvalidOperationException("The appearance redirect destination is missing, disabled, or not a root action. Choose an enabled action outside nested blocks.");
                if (++count > 128)
                    throw new InvalidOperationException("A macro can have at most 128 appearance redirects.");
            }
            ValidateChildren(step);
        }
    }

    private static void ValidateChildren(MacroStep parent)
    {
        IEnumerable<MacroStep> children = parent switch
        {
            IfThenElseStep branch => branch.ThenSteps.Concat(branch.ElseSteps),
            LoopStep loop => loop.BodySteps,
            _ => Array.Empty<MacroStep>()
        };
        foreach (MacroStep child in children)
        {
            if (IsDeclaration(child))
                throw new InvalidOperationException("Appearance redirects must be placed in the main action list, outside IF and WHILE blocks.");
            ValidateChildren(child);
        }
    }

    internal AppearanceRedirectCoordinator(IReadOnlyList<MacroStep> steps, MacroContext context, CancellationToken ct)
    {
        _context = context;
        _runToken = ct;
        // Detection coordinates cannot overwrite the active action's variables.
        _detectionContext = new MacroContext(context.CaptureService, context.Control)
        {
            RunId = context.RunId,
            ImageMatcher = context.ImageMatcher,
            WindowCoordinates = context.WindowCoordinates,
            Trace = context.Trace,
            StartedAtUtc = context.StartedAtUtc,
            MaxRuntime = context.MaxRuntime,
            EnforceRuntimeCap = context.EnforceRuntimeCap
        };
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, context.StopToken, context.PanicToken);
        foreach (var step in steps)
            if (step is WaitUntilStep { IsEnabled: true, AppearanceRedirect: not null } wait)
                _watches.Add(new Watch(wait));
        if (_watches.Count > 0)
            _pending = Task.Run(() => WaitForMatchAsync(_lifetime.Token));
    }

    /// <summary>Runs one root action (including its children), while retaining observer state across actions.</summary>
    internal async Task<AppearanceRedirectMatch?> ExecuteActionAsync(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_pending is null)
        {
            await action(ct).ConfigureAwait(false);
            return null;
        }

        // An image already visible at startup selects its destination before the
        // first input action. A failed initial scan also fails before input.
        await Task.WhenAny(_firstScan.Task, _pending).WaitAsync(ct).ConfigureAwait(false);

        if (_pending.IsCompleted)
            return await TakeMatchAsync(ct).ConfigureAwait(false);

        using var actionLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        // A synchronous precision wait in an input action must not block the observer's
        // cancellation handoff. Only this one action is scheduled and always joined.
        Task running = Task.Run(() => action(actionLifetime.Token), CancellationToken.None);
        await Task.WhenAny(running, _pending).ConfigureAwait(false);
        if (_pending.IsCompleted)
        {
            actionLifetime.Cancel();
            try { await running.ConfigureAwait(false); }
            catch (OperationCanceledException) when (actionLifetime.IsCancellationRequested) { }
            ct.ThrowIfCancellationRequested();
            if (_context.IsAborted)
                throw new OperationCanceledException("Playback stopped before redirect.", ct);
            return await TakeMatchAsync(ct).ConfigureAwait(false);
        }
        await running.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        // Resolve a detection that completed at the final action boundary as well.
        return _pending.IsCompleted ? await TakeMatchAsync(ct).ConfigureAwait(false) : null;
    }

    private async Task<AppearanceRedirectMatch> TakeMatchAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_context.IsAborted)
            throw new OperationCanceledException("Playback stopped before redirect.", ct);
        AppearanceRedirectMatch match = await _pending!.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (_context.IsAborted)
            throw new OperationCanceledException("Playback stopped before redirect.", ct);
        _context.WriteTrace("Navigation", "Image appeared; canceling the old path and selecting the configured root destination.",
            actionId: match.SourceStepId, data: new { Rule = "Appearance", Destination = match.TargetStepId });
        // Publish only the chosen match, after the abandoned action has joined.
        // The running action never observes coordinates from an unrelated scan.
        ImageCondition.StoreMatch(_context, match.X, match.Y, match.Confidence);
        _pending = Task.Run(() => WaitForMatchAsync(_lifetime.Token));
        return match;
    }

    private async Task<AppearanceRedirectMatch> WaitForMatchAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_context.IsAborted)
                throw new OperationCanceledException("Playback ended while observing images.", ct);
            if (_context.Control.IsPaused)
            {
                await _context.Control.WaitIfPausedAsync(ct).ConfigureAwait(false);
                foreach (var watch in _watches) watch.ResetStreaks();
            }
            ct.ThrowIfCancellationRequested();
            long now = Environment.TickCount64;
            Rectangle union = Rectangle.Empty;
            bool fullScreen = false;
            bool anyDue = false;
            foreach (Watch watch in _watches)
            {
                watch.Due = now >= watch.NextCheck;
                if (!watch.Due) continue;
                anyDue = true;
                watch.Region = watch.Image.GetCaptureRegion(_detectionContext);
                if (watch.Region is { } region)
                    union = union.IsEmpty ? region : Rectangle.Union(union, region);
                else fullScreen = true;
            }
            if (anyDue)
            {
                AppearanceRedirectMatch? chosen = null;
                using (CaptureFrame frame = fullScreen
                    ? await _context.CaptureService.CaptureScreenAsync(ct).ConfigureAwait(false)
                    : await _context.CaptureService.CaptureRegionAsync(union, ct).ConfigureAwait(false))
                {
                    foreach (Watch watch in _watches)
                    {
                        if (!watch.Due) continue;
                        ct.ThrowIfCancellationRequested();
                        if (_context.Control.IsPaused) break;
                        Rectangle? relative = null;
                        if (watch.Region is { } region)
                        {
                            Rectangle clipped = Rectangle.Intersect(region, new Rectangle(frame.OriginX, frame.OriginY, frame.Width, frame.Height));
                            if (clipped.Width <= 0 || clipped.Height <= 0)
                                throw new InvalidOperationException("An appearance image search area is outside the captured screen.");
                            relative = new Rectangle(clipped.X - frame.OriginX, clipped.Y - frame.OriginY, clipped.Width, clipped.Height);
                        }
                        ConditionEvaluation result = await watch.Image.EvaluateFrameDetailedAsync(_detectionContext, frame, relative, ct).ConfigureAwait(false);
                        result.ThrowIfFailed($"Appearance image '{watch.Step.Label}'");
                        now = Environment.TickCount64;
                        watch.NextCheck = now + watch.PollMs;
                        if (!result.IsSatisfied)
                        {
                            watch.TrueCount = 0;
                            watch.FalseCount = Math.Min(watch.FalseCount + 1, watch.FalseRequired);
                            if (watch.FalseCount >= watch.FalseRequired) watch.Armed = true;
                            continue;
                        }
                        watch.FalseCount = 0;
                        if (watch.TrueCount == 0) watch.StreakStart = now;
                        watch.TrueCount = Math.Min(watch.TrueCount + 1, watch.TrueRequired);
                        if (watch.Armed && now >= watch.NextActivation
                            && watch.TrueCount >= watch.TrueRequired && now - watch.StreakStart >= watch.StabilityMs)
                        {
                            watch.Armed = false;
                            watch.NextActivation = now + watch.Step.AppearanceRedirect!.CooldownMs;
                            // Simultaneous matches form one decision. First in list wins;
                            // all matched edges are consumed so a loser cannot immediately
                            // cancel the winning destination while the same images stay up.
                            chosen ??= new AppearanceRedirectMatch(watch.Step.StepId, watch.Step.AppearanceRedirect.TargetStepId,
                                _detectionContext.Variables.Get<int>(ImageCondition.LastMatchXVariable),
                                _detectionContext.Variables.Get<int>(ImageCondition.LastMatchYVariable),
                                _detectionContext.Variables.Get<double>(MacroVariableNames.LastDetectedConfidence));
                        }
                    }
                }
                if (chosen is not null) return chosen;
                _firstScan.TrySetResult();
            }
            long due = _watches.Min(w => w.NextCheck);
            await Task.Delay((int)Math.Clamp(due - Environment.TickCount64, 1, 50), ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try
        {
            if (_pending is not null) await _pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (_runToken.IsCancellationRequested || _context.IsAborted)
        {
            // Joining a failing observer must not replace an already requested Stop/Panic.
            _context.WriteTrace("ImageDetection", "Image observer failed while playback was stopping.",
                ExecutionTraceLevel.Warning, exception: ex);
        }
        finally { _lifetime.Dispose(); }
    }

    private sealed class Watch
    {
        public readonly WaitUntilStep Step;
        public ImageCondition Image => (ImageCondition)Step.Condition;
        public readonly int PollMs, TrueRequired, FalseRequired, StabilityMs;
        public int TrueCount, FalseCount;
        public long NextCheck, NextActivation, StreakStart;
        public bool Armed = true, Due;
        public Rectangle? Region;
        public Watch(WaitUntilStep step)
        {
            Step = step;
            PollMs = ConditionReliability.ClampPollIntervalMs((int)step.PollInterval.TotalMilliseconds);
            TrueRequired = ConditionReliability.ClampConfirmations(step.RequiredConfirmations);
            FalseRequired = ConditionReliability.ClampConfirmations(step.RequiredFalseConfirmations);
            StabilityMs = Math.Max(0, step.StabilityDurationMs);
        }
        public void ResetStreaks() { TrueCount = FalseCount = 0; NextCheck = 0; }
    }
}

internal sealed record AppearanceRedirectMatch(Guid SourceStepId, Guid TargetStepId, int X, int Y, double Confidence);
