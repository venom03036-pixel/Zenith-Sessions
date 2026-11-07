namespace MacroSuite.SDK.AI;

/// <summary>
/// Structured action proposed by an AI vision provider (Rule 8: Untrusted data requiring validation).
/// </summary>
public sealed class AIAction
{
    /// <summary>Action verb: 'click', 'double_click', 'keypress', 'text', 'delay', or 'none'.</summary>
    public string ActionType { get; init; } = "none";

    /// <summary>Screen X coordinate in virtual screen space.</summary>
    public int? X { get; init; }

    /// <summary>Screen Y coordinate in virtual screen space.</summary>
    public int? Y { get; init; }

    /// <summary>Virtual key code for keyboard actions.</summary>
    public int? KeyCode { get; init; }

    /// <summary>Text to type or payload content.</summary>
    public string? Text { get; init; }

    /// <summary>AI model's self-reported confidence (0.0 to 1.0). Null means the model omitted it.</summary>
    public double? Confidence { get; init; }

    /// <summary>Model's reasoning or explanation for this proposed action.</summary>
    public string Reasoning { get; init; } = string.Empty;
}
