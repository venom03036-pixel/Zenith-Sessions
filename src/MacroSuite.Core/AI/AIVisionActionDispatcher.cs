using System.Diagnostics;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.SDK.AI;

namespace MacroSuite.Core.AI;

/// <summary>
/// Single input-dispatch path for validated, approved AI actions (Rule 4, Rule 8).
/// Semantic vision matching must not call this; it may only return a condition result.
/// </summary>
public static class AIVisionActionDispatcher
{
    public static async Task DispatchAsync(Abstractions.IInputPlayer inputPlayer, AIAction action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inputPlayer);
        ArgumentNullException.ThrowIfNull(action);

        string type = action.ActionType.ToLowerInvariant();
        long now = Stopwatch.GetTimestamp();

        switch (type)
        {
            case "click" when action.X.HasValue && action.Y.HasValue:
                int x = action.X.Value;
                int y = action.Y.Value;
                await inputPlayer.PlayAsync(new[]
                {
                    new InputEvent(InputEventType.MouseMove, now, x, y, 0),
                    new InputEvent(InputEventType.MouseDown, now + 1, x, y, (int)MouseButton.Left),
                    new InputEvent(InputEventType.MouseUp, now + 2, x, y, (int)MouseButton.Left)
                }, options: null, ct: ct).ConfigureAwait(false);
                break;

            case "double_click" when action.X.HasValue && action.Y.HasValue:
                int dx = action.X.Value;
                int dy = action.Y.Value;
                await inputPlayer.PlayAsync(new[]
                {
                    new InputEvent(InputEventType.MouseMove, now, dx, dy, 0),
                    new InputEvent(InputEventType.MouseDown, now + 1, dx, dy, (int)MouseButton.Left),
                    new InputEvent(InputEventType.MouseUp, now + 2, dx, dy, (int)MouseButton.Left),
                    new InputEvent(InputEventType.MouseDown, now + 50, dx, dy, (int)MouseButton.Left),
                    new InputEvent(InputEventType.MouseUp, now + 51, dx, dy, (int)MouseButton.Left)
                }, options: null, ct: ct).ConfigureAwait(false);
                break;

            case "keypress" when action.KeyCode.HasValue:
                int vk = action.KeyCode.Value;
                await inputPlayer.PlayAsync(new[]
                {
                    new InputEvent(InputEventType.KeyDown, now, 0, 0, vk),
                    new InputEvent(InputEventType.KeyUp, now + 1, 0, 0, vk)
                }, options: null, ct: ct).ConfigureAwait(false);
                break;

            case "text" when !string.IsNullOrEmpty(action.Text):
                await inputPlayer.PlayAsync(
                    TextInputEvents.FromText(action.Text, now),
                    options: null,
                    ct: ct).ConfigureAwait(false);
                break;

            case "delay":
                await Task.Delay(100, ct).ConfigureAwait(false);
                break;

            case "none":
                break;

            default:
                throw new InvalidOperationException(
                    $"No AI action dispatcher is registered for action type '{action.ActionType}'.");
        }
    }

    public static bool CanDispatch(string? actionType)
    {
        if (string.IsNullOrWhiteSpace(actionType))
            return false;

        return actionType.ToLowerInvariant() switch
        {
            "click" or "double_click" or "keypress" or "text" or "delay" or "none" => true,
            _ => false
        };
    }
}
