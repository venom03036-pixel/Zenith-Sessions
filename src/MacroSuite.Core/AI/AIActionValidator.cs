using System.Drawing;
using MacroSuite.SDK.AI;
using MacroSuite.SDK.Contracts;

namespace MacroSuite.Core.AI;

/// <summary>
/// Enforces Rule 8: AI output is untrusted data and must pass strict validation before simulation.
/// Validates coordinate boundaries, action whitelists, text payload sizes, and confidence levels.
/// </summary>
public sealed class AIActionValidator
{
    private static readonly HashSet<string> s_allowedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "click",
        "double_click",
        "keypress",
        "text",
        "delay",
        "none"
    };

    public static IReadOnlyCollection<string> AllowedActions => s_allowedActions;

    public static bool IsAllowedAction(string? actionType)
        => !string.IsNullOrWhiteSpace(actionType) && s_allowedActions.Contains(actionType);

    public static bool IsInputProducing(string? actionType)
        => actionType is not null
           && (actionType.Equals("click", StringComparison.OrdinalIgnoreCase)
               || actionType.Equals("double_click", StringComparison.OrdinalIgnoreCase)
               || actionType.Equals("keypress", StringComparison.OrdinalIgnoreCase)
               || actionType.Equals("text", StringComparison.OrdinalIgnoreCase));

    /// <summary>Maximum allowable text length for simulated text entry.</summary>
    public int MaxTextLength { get; init; } = 200;

    /// <summary>Minimum acceptable confidence score (0.0 to 1.0).</summary>
    public double MinConfidenceThreshold { get; init; } = 0.50;

    public const int MinCoordinate = -16_384;
    public const int MaxCoordinate = 16_384;

    /// <summary>
    /// Validates an <see cref="AIAction"/> against safety bounds and constraints.
    /// Returns <see cref="ValidationResult.Success"/> or informative <see cref="ValidationResult.Failure"/>.
    /// </summary>
    public ValidationResult Validate(
        AIAction action,
        Rectangle? allowedRegion = null,
        string? expectedWindow = null,
        string? currentWindow = null)
    {
        if (action is null)
        {
            return ValidationResult.Failure("AI action cannot be null.");
        }

        // 1. Action type whitelist check
        if (string.IsNullOrWhiteSpace(action.ActionType) || !s_allowedActions.Contains(action.ActionType))
        {
            return ValidationResult.Failure($"Action type '{action.ActionType}' is not permitted by safety policy.");
        }

        // 2. Target window: required for input-producing actions (empty is not "any window").
        if (IsInputProducing(action.ActionType))
        {
            if (string.IsNullOrWhiteSpace(expectedWindow))
            {
                return ValidationResult.Failure(
                    "Expected window is required for input-producing AI actions. Set Expected window on the AI step.");
            }

            if (string.IsNullOrWhiteSpace(currentWindow))
                return ValidationResult.Failure("The foreground window could not be verified.");
            if (!currentWindow.Contains(expectedWindow, StringComparison.OrdinalIgnoreCase))
            {
                return ValidationResult.Failure($"Foreground window '{currentWindow}' does not match required target window '{expectedWindow}'.");
            }
        }

        // 3. Coordinate bounds check for click actions
        if (action.ActionType.Equals("click", StringComparison.OrdinalIgnoreCase) ||
            action.ActionType.Equals("double_click", StringComparison.OrdinalIgnoreCase))
        {
            if (!action.X.HasValue || !action.Y.HasValue)
            {
                return ValidationResult.Failure($"Action '{action.ActionType}' requires valid X and Y coordinates.");
            }

            int x = action.X.Value;
            int y = action.Y.Value;

            if (allowedRegion.HasValue)
            {
                var r = allowedRegion.Value;
                if (x < r.Left || x >= r.Right || y < r.Top || y >= r.Bottom)
                {
                    return ValidationResult.Failure($"Target coordinates ({x}, {y}) fall outside the allowed bounding region [{r.Left},{r.Top},{r.Width},{r.Height}].");
                }
            }
            else
            {
                // Basic virtual screen sanity checks (avoid extreme out-of-bounds coordinates)
                if (x < MinCoordinate || x > MaxCoordinate || y < MinCoordinate || y > MaxCoordinate)
                {
                    return ValidationResult.Failure($"Target coordinates ({x}, {y}) are outside realistic virtual screen boundaries.");
                }
            }
        }

        // 4. Keyboard action validation
        if (action.ActionType.Equals("keypress", StringComparison.OrdinalIgnoreCase))
        {
            if (!action.KeyCode.HasValue || action.KeyCode.Value <= 0 || action.KeyCode.Value > 255)
            {
                return ValidationResult.Failure($"Invalid virtual key code: {action.KeyCode}. Must be between 1 and 255.");
            }
        }

        // 5. Text payload validation. Dispatch uses Unicode SendInput via IInputPlayer.
        if (action.ActionType.Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(action.Text))
                return ValidationResult.Failure("Text action payload is empty.");
            if (action.Text.Length > MaxTextLength)
                return ValidationResult.Failure(
                    $"Text length ({action.Text.Length}) exceeds safety maximum of {MaxTextLength} characters.");
        }

        // 6. Confidence: missing/non-finite fails closed for input-producing actions.
        if (IsInputProducing(action.ActionType))
        {
            if (action.Confidence is not double confidence || double.IsNaN(confidence) || double.IsInfinity(confidence))
            {
                return ValidationResult.Failure("Model confidence is missing or invalid.");
            }

            if (confidence < MinConfidenceThreshold)
            {
                return ValidationResult.Failure($"Model confidence ({confidence:P0}) is below minimum safety threshold ({MinConfidenceThreshold:P0}).");
            }
        }

        return ValidationResult.Success();
    }
}
