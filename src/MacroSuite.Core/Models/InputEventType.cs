namespace MacroSuite.Core.Models;

/// <summary>
/// Classifies a low-level input event captured by a Windows hook.
/// </summary>
public enum InputEventType : byte
{
    MouseMove    = 0,
    MouseDown    = 1,
    MouseUp      = 2,
    KeyDown      = 3,
    KeyUp        = 4,
    MouseWheel   = 5,
    /// <summary>X/Y are pixel deltas, not screen coordinates.</summary>
    MouseMoveRelative = 6,
    /// <summary>Horizontal wheel / tilt. Data is the signed delta.</summary>
    MouseHWheel = 7,
    /// <summary>Unicode key down. Data is a UTF-16 code unit.</summary>
    Unicode = 8,
    /// <summary>Unicode key up. Data is a UTF-16 code unit.</summary>
    UnicodeUp = 9,
}
