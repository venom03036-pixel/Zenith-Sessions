using System.Diagnostics;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;

namespace MacroSuite.Core.Execution;

/// <summary>
/// Moves the cursor to the last image/color detection after a find succeeds (Rule 4).
/// </summary>
public static class DetectionCursorMover
{
    public static bool ShouldMoveAfterWait(ICondition condition, bool waitWhile)
        => !waitWhile && condition is ImageCondition or ColorCondition;

    public static async Task MoveToLastDetectionAsync(
        MacroContext context,
        CancellationToken ct,
        bool requireCoordinates)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.InputPlayer is null || context.IsAborted || ct.IsCancellationRequested)
            return;

        if (!TryGetPoint(context, out int x, out int y))
        {
            if (requireCoordinates)
            {
                throw new InvalidOperationException(
                    "Find succeeded but no match coordinates were recorded. Expected $LastDetectedX / $LastDetectedY from the detection step.");
            }

            return;
        }

        var ev = new InputEvent(InputEventType.MouseMove, Stopwatch.GetTimestamp(), x, y, 0, 0);
        await context.InputPlayer.PlayAsync([ev], context.ToPlaybackOptions(), ct).ConfigureAwait(false);
    }

    public static bool TryGetPoint(MacroContext context, out int x, out int y)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (TryGetInt(context, MacroVariableNames.LastDetectedXAlias, out x)
            && TryGetInt(context, MacroVariableNames.LastDetectedYAlias, out y))
        {
            return true;
        }

        if (TryGetInt(context, MacroVariableNames.LastDetectedX, out x)
            && TryGetInt(context, MacroVariableNames.LastDetectedY, out y))
        {
            return true;
        }

        if (TryGetInt(context, ImageCondition.LastMatchXVariable, out x)
            && TryGetInt(context, ImageCondition.LastMatchYVariable, out y))
        {
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    private static bool TryGetInt(MacroContext context, string name, out int value)
        => context.Variables.TryGet(name, out value);
}
