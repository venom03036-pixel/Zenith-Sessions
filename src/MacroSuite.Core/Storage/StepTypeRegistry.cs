using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;

namespace MacroSuite.Core.Storage;

/// <summary>
/// Canonical JSON <c>$type</c> discriminators that the action dispatcher can execute.
/// Unknown discriminators still round-trip as <see cref="UnknownStep"/> but fail at runtime.
/// </summary>
public static class StepTypeRegistry
{
    public static readonly string[] KnownDiscriminators =
    [
        "click",
        "mousemove",
        "mousewheel",
        "mousesweep",
        "mouse_hold",
        "mousetrajectory",
        "camera_trajectory",
        "keypress",
        "delay",
        "waituntil",
        "visualwait",
        "ifthenelse",
        "loop",
        "break",
        "continue",
        "ai",
        "plugin"
    ];

    public static Type? ResolveClrType(string discriminator)
        => discriminator.ToLowerInvariant() switch
        {
            "click" => typeof(ClickStep),
            "mousemove" => typeof(MouseMoveStep),
            "mousewheel" => typeof(MouseWheelStep),
            "mousesweep" or "mouse_hold" => typeof(MouseSweepStep),
            "mousetrajectory" or "camera_trajectory" => typeof(MouseTrajectoryStep),
            "keypress" => typeof(KeyPressStep),
            "delay" => typeof(DelayStep),
            "waituntil" => typeof(WaitUntilStep),
            "visualwait" => typeof(VisualWaitStep),
            "ifthenelse" => typeof(IfThenElseStep),
            "loop" => typeof(LoopStep),
            "break" => typeof(BreakStep),
            "continue" => typeof(ContinueStep),
            "ai" => typeof(AIStep),
            "plugin" => typeof(PluginStep),
            _ => null
        };

    public static string? GetCanonicalDiscriminator(Type stepType)
    {
        ArgumentNullException.ThrowIfNull(stepType);
        if (stepType == typeof(ClickStep)) return "click";
        if (stepType == typeof(MouseMoveStep)) return "mousemove";
        if (stepType == typeof(MouseWheelStep)) return "mousewheel";
        if (stepType == typeof(MouseSweepStep)) return "mousesweep";
        if (stepType == typeof(MouseTrajectoryStep)) return "mousetrajectory";
        if (stepType == typeof(KeyPressStep)) return "keypress";
        if (stepType == typeof(DelayStep)) return "delay";
        if (stepType == typeof(WaitUntilStep)) return "waituntil";
        if (stepType == typeof(VisualWaitStep)) return "visualwait";
        if (stepType == typeof(IfThenElseStep)) return "ifthenelse";
        if (stepType == typeof(LoopStep)) return "loop";
        if (stepType == typeof(BreakStep)) return "break";
        if (stepType == typeof(ContinueStep)) return "continue";
        if (stepType == typeof(AIStep)) return "ai";
        if (stepType == typeof(PluginStep)) return "plugin";
        return null;
    }

    public static bool IsRegistered(string? discriminator)
        => !string.IsNullOrWhiteSpace(discriminator)
           && ResolveClrType(discriminator) is not null;

    public static void ValidateKnownDiscriminators()
    {
        foreach (string tag in KnownDiscriminators)
        {
            Type? type = ResolveClrType(tag);
            if (type is null || !typeof(MacroStep).IsAssignableFrom(type))
            {
                throw new InvalidOperationException(
                    $"JSON discriminator '{tag}' is registered but does not resolve to a MacroStep type.");
            }

            string? canonical = GetCanonicalDiscriminator(type);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                throw new InvalidOperationException(
                    $"Step type '{type.Name}' resolved from '{tag}' has no canonical discriminator.");
            }
        }
    }
}
