using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;

namespace MacroSuite.Core.Steps;

public sealed class BreakStep : MacroStep
{
    public BreakStep() => Label = "Break";

    public override Task ExecuteAsync(MacroContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        if (!IsEnabled || context.IsAborted)
            return Task.CompletedTask;
        if (context.CurrentLoopDepth <= 0)
            throw new InvalidOperationException("BREAK can only execute inside a loop.");
        context.SignalControlFlow(ControlFlowSignal.Break);
        return Task.CompletedTask;
    }
}

public sealed class ContinueStep : MacroStep
{
    public ContinueStep() => Label = "Continue";

    public override Task ExecuteAsync(MacroContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        if (!IsEnabled || context.IsAborted)
            return Task.CompletedTask;
        if (context.CurrentLoopDepth <= 0)
            throw new InvalidOperationException("CONTINUE can only execute inside a loop.");
        context.SignalControlFlow(ControlFlowSignal.Continue);
        return Task.CompletedTask;
    }
}
