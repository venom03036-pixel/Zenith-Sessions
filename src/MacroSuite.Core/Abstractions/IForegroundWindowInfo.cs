namespace MacroSuite.Core.Abstractions;

/// <summary>
/// Platform-isolated foreground window title query (Rule 3). Used by AI safety checks.
/// </summary>
public interface IForegroundWindowInfo
{
    string? GetForegroundWindowTitle();
}
