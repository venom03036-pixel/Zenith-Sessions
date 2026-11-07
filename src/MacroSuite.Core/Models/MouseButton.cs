namespace MacroSuite.Core.Models;

/// <summary>
/// Identifies which mouse button was pressed or released in a <see cref="InputEvent"/>.
/// Stored in <see cref="InputEvent.Data"/> for <see cref="InputEventType.MouseDown"/> and
/// <see cref="InputEventType.MouseUp"/> events.
/// </summary>
public enum MouseButton : int
{
    None   = 0,
    Left   = 1,
    Right  = 2,
    Middle = 3,
    X1     = 4,
    X2     = 5,
}
