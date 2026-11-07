using System.ComponentModel;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;

namespace MacroSuite.Core.Execution;

public enum NavigationKind { NextConditional, Jump, Stop }

public sealed record NavigationRequest(
    NavigationKind Kind, Guid SourceStepId, Guid? TargetStepId = null, string? Reason = null);

/// <summary>A recoverable operation failed inside a block; its owner decides the continuation.</summary>
internal sealed class RecoverableBlockFailureException : Exception
{
    public bool IsBlockCondition { get; }
    public RecoverableBlockFailureException(string message, Exception? inner = null, bool isBlockCondition = false) : base(message, inner)
        => IsBlockCondition = isBlockCondition;
}

/// <summary>Routes only known recoverable outcomes. Cancellation and invalid configuration never become misses.</summary>
public static class BlockRouting
{
    public static bool IsConditional(MacroStep step)
        => step.IsEnabled && (step is IfThenElseStep || step is LoopStep { WhileCondition: not null });

    public static int FindNextConditional(IReadOnlyList<MacroStep> steps, int afterIndex, int endIndex)
    {
        for (int i = afterIndex + 1; i < endIndex; i++)
            if (IsConditional(steps[i])) return i;
        return -1;
    }

    public static ConditionFailurePolicy ConditionPolicy(ICondition? condition) => condition switch
    {
        ColorCondition color => color.FailurePolicy,
        VisualPresenceCondition visual => visual.FailurePolicy,
        _ => ConditionFailurePolicy.Default
    };

    public static FlowRoute ResolveFailureRoute(BlockRoutingOptions options, ICondition? condition)
        => options.OnFailure != FlowRoute.Default ? options.OnFailure : ConditionPolicy(condition) switch
        {
            ConditionFailurePolicy.Stop => FlowRoute.Stop,
            ConditionFailurePolicy.Skip => FlowRoute.Continue,
            ConditionFailurePolicy.Retry => FlowRoute.Retry,
            _ => FlowRoute.Default
        };

    public static bool IsRecoverable(ConditionEvaluation evaluation)
    {
        if (!evaluation.IsFailed) return false;
        // A missing/invalid template, unavailable matcher, or unknown error is configuration
        // or programming failure. Do not hide it behind the default block continuation.
        if (evaluation.ErrorCode is "ConditionIndeterminate" or "CaptureFrameUnavailable"
            or "ImageMatchFailed" or "MatchFailed") return true;
        return (evaluation.ErrorCode is "ColorDetectionFailed" or "VisualConditionFailed" or "ImageDetectionException")
            && (evaluation.Exception is IOException and not FileNotFoundException and not DirectoryNotFoundException
                or Win32Exception or TimeoutException);
    }

    internal static async Task<bool> ApplyAsync(
        MacroStep owner, BlockRoutingOptions options, FlowRoute route, int retriesUsed,
        MacroContext context, CancellationToken ct, string reason)
    {
        ConditionWaitEvaluator.ThrowIfStopped(context, ct);
        if (!Enum.IsDefined(route)) throw new InvalidOperationException("Unknown block routing option.");
        if (route == FlowRoute.Retry && retriesUsed < Math.Clamp(options.RetryCount, 0, 20))
        {
            context.WriteTrace("ControlFlow", $"Applying Retry; entering this block again: {reason}", actionId: owner.StepId,
                data: new { Rule = route, Destination = owner.StepId, Attempt = retriesUsed + 1, Reason = reason });
            await Task.Delay(Math.Clamp(options.RetryDelayMs, 10, 10_000), ct).ConfigureAwait(false);
            ConditionWaitEvaluator.ThrowIfStopped(context, ct);
            return true;
        }
        if (route == FlowRoute.Continue)
        {
            context.WriteTrace("ControlFlow", $"Applying Continue; returning to the next action in the containing scope: {reason}",
                actionId: owner.StepId, data: new { Rule = route, Destination = "Next action in containing scope", Reason = reason });
            return false;
        }
        if (route == FlowRoute.Stop)
        {
            context.WriteTrace("ControlFlow", $"Applying Stop; ending the macro: {reason}", actionId: owner.StepId,
                data: new { Rule = route, Destination = "End macro", Reason = reason });
            context.RequestNavigation(new(NavigationKind.Stop, owner.StepId, Reason: reason));
            await context.Control.StopAsync().ConfigureAwait(false);
            return false;
        }
        if (route == FlowRoute.Jump && options.TargetStepId is null)
            throw new InvalidOperationException("A Jump route requires an existing top-level target action.");
        if (route == FlowRoute.Retry)
            context.WriteTrace("ControlFlow", $"Retry limit reached; applying default continuation to the next eligible conditional: {reason}",
                actionId: owner.StepId, data: new { Rule = route, Destination = "Next eligible conditional", Retries = retriesUsed, Reason = reason });
        context.RequestNavigation(new(route == FlowRoute.Jump ? NavigationKind.Jump : NavigationKind.NextConditional,
            owner.StepId, route == FlowRoute.Jump ? options.TargetStepId : null, reason));
        return false;
    }

    public static void Validate(IReadOnlyList<MacroStep> root)
    {
        // Legacy action lists may reuse an instance/ID. Positional continuation is
        // unambiguous; require unique identities only when a saved target is used.
        bool requiresUniqueIds = UsesIdentityNavigation(root);
        var rootIds = new Dictionary<Guid, MacroStep>();
        var allIds = new HashSet<Guid>();
        foreach (MacroStep step in root)
            if (!rootIds.TryAdd(step.StepId, step) && requiresUniqueIds)
                throw new InvalidOperationException($"Duplicate root action ID '{step.StepId}'.");
        Visit(root);

        void Visit(IReadOnlyList<MacroStep> steps)
        {
            foreach (MacroStep step in steps)
            {
                if (!allIds.Add(step.StepId) && requiresUniqueIds)
                    throw new InvalidOperationException($"Duplicate action ID '{step.StepId}'.");
                BlockRoutingOptions? options = step switch
                {
                    IfThenElseStep branch => branch.Routing,
                    LoopStep loop => loop.Routing,
                    _ => null
                };
                if (options is not null)
                {
                    if (!Enum.IsDefined(options.OnFailure) || !Enum.IsDefined(options.OnNotSatisfied))
                        throw new InvalidOperationException("Unknown block routing option.");
                    if (options.OnFailure == FlowRoute.Jump || options.OnNotSatisfied == FlowRoute.Jump)
                    {
                        if (options.TargetStepId is not { } target
                            || !rootIds.TryGetValue(target, out MacroStep? destination) || !destination.IsEnabled
                            || destination is WaitUntilStep { AppearanceRedirect: not null })
                            throw new InvalidOperationException("Jump target is missing, disabled, or nested. Choose an enabled top-level action or block.");
                    }
                }
                if (step is IfThenElseStep conditional)
                {
                    Visit(conditional.ThenSteps);
                    Visit(conditional.ElseSteps);
                }
                else if (step is LoopStep loop) Visit(loop.BodySteps);
            }
        }
    }

    private static bool UsesIdentityNavigation(IReadOnlyList<MacroStep> steps)
    {
        foreach (MacroStep step in steps)
        {
            if (step is WaitUntilStep { AppearanceRedirect: not null }) return true;
            if (step is IfThenElseStep branch)
            {
                if (HasJump(branch.Routing) || UsesIdentityNavigation(branch.ThenSteps)
                    || UsesIdentityNavigation(branch.ElseSteps)) return true;
            }
            else if (step is LoopStep loop && (HasJump(loop.Routing) || UsesIdentityNavigation(loop.BodySteps))) return true;
        }
        return false;
    }

    private static bool HasJump(BlockRoutingOptions? options)
        => options?.OnFailure == FlowRoute.Jump || options?.OnNotSatisfied == FlowRoute.Jump;
}
