namespace MacroSuite.Core.Input;

/// <summary>
/// Exclusive input consumer. Only the active mode may treat OS input as belonging to that subsystem.
/// </summary>
public enum InputMode
{
    Normal = 0,
    Recording,
    Editing,
    PickingLocation,
    PickingColor,
    CapturingImage,
    SelectingRegion,
    TestingVisualMatch,
    TestingAIVision,
    MacroExecuting,
    PanicStopping
}
