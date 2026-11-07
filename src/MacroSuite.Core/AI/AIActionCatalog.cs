using MacroSuite.SDK.AI;

namespace MacroSuite.Core.AI;

/// <summary>
/// Canonical AI action types that both the validator whitelist and the dispatcher must honor.
/// </summary>
public static class AIActionCatalog
{
    public static readonly string[] Types =
    [
        "click",
        "double_click",
        "keypress",
        "text",
        "delay",
        "none"
    ];

    public static void ThrowIfInconsistent()
    {
        foreach (string type in Types)
        {
            if (!AIActionValidator.IsAllowedAction(type))
            {
                throw new InvalidOperationException(
                    $"AI action '{type}' is catalogued but not allowed by the safety validator.");
            }

            if (!AIVisionActionDispatcher.CanDispatch(type))
            {
                throw new InvalidOperationException(
                    $"AI action '{type}' is catalogued but has no dispatcher.");
            }
        }

        foreach (string type in AIActionValidator.AllowedActions)
        {
            if (!Types.Contains(type, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"AI action '{type}' is allowed by the validator but missing from the catalog.");
            }
        }
    }
}
