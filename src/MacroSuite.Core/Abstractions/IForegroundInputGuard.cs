namespace MacroSuite.Core.Abstractions;

/// <summary>
/// Decides whether synthesized input may be sent to the current foreground window (Rule 3, Rule 4).
/// Panic release must not consult this guard.
/// </summary>
public interface IForegroundInputGuard
{
    bool TryAllowSynthesizedInput(out string? refusalReason);
}
