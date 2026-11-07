namespace MacroSuite.Core.Models;

/// <summary>
/// How a mouse-move step is interpreted at playback.
/// Absolute is desktop targeting; Relative is camera look / locked-cursor motion.
/// </summary>
public enum MouseMoveMode
{
    Absolute,
    Relative
}
