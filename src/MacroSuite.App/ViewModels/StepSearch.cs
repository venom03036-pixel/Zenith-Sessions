using System;
using System.IO;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;

namespace MacroSuite.App.ViewModels;

/// <summary>
/// Allocation-light matcher for the action find bar. Reads domain steps directly
/// so a 50k+ macro can be searched without materializing row view-models.
/// </summary>
internal static class StepSearch
{
    public static bool Matches(MacroStep step, int index1Based, string query)
    {
        if (step is null || string.IsNullOrEmpty(query))
            return false;

        if (ContainsInt(index1Based, query))
            return true;

        switch (step)
        {
            case ClickStep click:
                return MatchesClick(click, query);
            case MouseSweepStep sweep:
                return TokenContains("SWEEP", query)
                    || TokenContains("camera", query)
                    || TokenContains("hold", query)
                    || ContainsInt(sweep.DeltaX, query)
                    || ContainsInt(sweep.DeltaY, query)
                    || ContainsInt(sweep.DurationMs, query);
            case MouseTrajectoryStep path:
                MouseTrajectoryPlayback.NetDisplacement(path.Samples, out int pdx, out int pdy);
                return TokenContains("PATH", query)
                    || TokenContains("camera", query)
                    || TokenContains("trajectory", query)
                    || ContainsInt(pdx, query)
                    || ContainsInt(pdy, query)
                    || ContainsInt(path.DurationMs, query)
                    || ContainsInt(path.Samples.Length, query);
            case MouseWheelStep wheel:
                return TokenContains("SCROLL", query)
                    || TokenContains("wheel", query)
                    || ContainsInt(wheel.DeltaY, query)
                    || ContainsInt(wheel.DeltaX, query);
            case MouseMoveStep move:
                return TokenContains("MOVE", query)
                    || TokenContains("MOVE_REL", query)
                    || TokenContains("mouse", query)
                    || (move.IsRelative && (TokenContains("relative", query) || TokenContains("dx", query)))
                    || MatchesPoint(move.Position.X, move.Position.Y, query);
            case KeyPressStep key:
                return MatchesKey(key, query);
            case DelayStep delay:
                return TokenContains("WAIT", query)
                    || TokenContains("delay", query)
                    || ContainsInt((int)delay.Duration.TotalMilliseconds, query);
            case AIStep ai:
                return TokenContains("AI_VISION", query)
                    || TokenContains("ai", query)
                    || (!string.IsNullOrEmpty(ai.Prompt) && TokenContains(ai.Prompt, query));
            case WaitUntilStep until:
                return MatchesWaitUntil(until, query);
            case VisualWaitStep visual:
                return MatchesVisual(visual, query);
            case IfThenElseStep iff:
                return TokenContains("IF", query)
                    || (!string.IsNullOrEmpty(iff.Condition.Description) && TokenContains(iff.Condition.Description, query));
            case LoopStep loop:
                return MatchesLoop(loop, query);
            default:
                return TokenContains(step.GetType().Name, query)
                    || (!string.IsNullOrEmpty(step.Label) && TokenContains(step.Label, query));
        }
    }

    private static bool MatchesClick(ClickStep click, string query)
    {
        if (TokenContains("click", query) || TokenContains("drag", query))
            return true;

        string prefix = click.Button switch
        {
            MouseButton.Right => "RIGHT",
            MouseButton.Middle => "MIDDLE",
            MouseButton.X1 => "X1",
            MouseButton.X2 => "X2",
            _ => "LEFT"
        };
        string stroke = click.Stroke ?? "Press";
        string keyword = stroke.Equals("Down", StringComparison.OrdinalIgnoreCase) ? prefix + "_DOWN"
            : stroke.Equals("Up", StringComparison.OrdinalIgnoreCase) ? prefix + "_UP"
            : prefix + "_CLICK";

        return TokenContains(keyword, query)
            || TokenContains(stroke, query)
            || TokenContains(click.Button.ToString(), query)
            || MatchesPoint(click.Position.X, click.Position.Y, query);
    }

    private static bool MatchesKey(KeyPressStep key, string query)
    {
        var name = MacroStepItemViewModel.VirtualKeyName(key.KeyCode);
        if (HasPrefix(query, "key=", out var keyRest)
            && keyRest.Equals(name, StringComparison.OrdinalIgnoreCase))
            return true;

        var kind = key.Stroke switch
        {
            "up" or "Up" or "UP" => "KEY_UP",
            "down" or "Down" or "DOWN" => "KEY_DOWN",
            _ => "KEY_PRESS"
        };

        return TokenContains(kind, query)
            || TokenContains("key", query)
            || TokenContains(name, query)
            || ContainsInt(key.KeyCode, query);
    }

    private static bool MatchesWaitUntil(WaitUntilStep until, string query)
    {
        if (until.Condition is ImageCondition img)
        {
            if (TokenContains("WAIT_IMAGE", query) || TokenContains("image", query) || TokenContains("template", query))
                return true;
            return !string.IsNullOrEmpty(img.TemplatePath) && TokenContains(Path.GetFileName(img.TemplatePath), query);
        }

        return TokenContains("WAIT_COLOR", query) || TokenContains("color", query) || TokenContains("pixel", query);
    }

    private static bool MatchesVisual(VisualWaitStep visual, string query)
    {
        if (TokenContains("WAIT_VISUAL", query) || TokenContains("visual", query))
            return true;
        if (!string.IsNullOrEmpty(visual.Hex) && TokenContains(visual.Hex, query))
            return true;
        return MatchesPoint(visual.X, visual.Y, query);
    }

    private static bool MatchesLoop(LoopStep loop, string query)
    {
        if (loop.RepeatUntil && TokenContains("UNTIL", query))
            return true;
        if (loop.WhileCondition != null && TokenContains("WHILE", query))
            return true;
        if (TokenContains("REPEAT", query) || TokenContains("loop", query))
            return true;
        return loop.WhileCondition != null
            && !string.IsNullOrEmpty(loop.WhileCondition.Description)
            && TokenContains(loop.WhileCondition.Description, query);
    }

    private static bool MatchesPoint(int x, int y, string query)
    {
        if (HasPrefix(query, "x=", out var xRest) && int.TryParse(xRest, out int qx) && qx == x)
            return true;
        if (HasPrefix(query, "y=", out var yRest) && int.TryParse(yRest, out int qy) && qy == y)
            return true;
        return ContainsInt(x, query) || ContainsInt(y, query);
    }

    private static bool HasPrefix(string query, string prefix, out ReadOnlySpan<char> rest)
    {
        if (query.AsSpan().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            rest = query.AsSpan(prefix.Length);
            return true;
        }

        rest = default;
        return false;
    }

    private static bool TokenContains(string haystack, string query) =>
        !string.IsNullOrEmpty(haystack)
        && MemoryExtensions.Contains(haystack.AsSpan(), query.AsSpan(), StringComparison.OrdinalIgnoreCase);

    private static bool ContainsInt(int value, string query)
    {
        if (query.Length == 0 || query.Length > 11)
            return false;
        Span<char> buf = stackalloc char[16];
        return value.TryFormat(buf, out int written)
            && MemoryExtensions.Contains(buf[..written], query.AsSpan(), StringComparison.Ordinal);
    }
}
