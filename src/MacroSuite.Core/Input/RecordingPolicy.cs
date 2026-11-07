namespace MacroSuite.Core.Input;

/// <summary>
/// Central rules for whether a captured OS event may become a macro action.
/// Evaluated on the recording consumer (not inside hook callbacks).
/// </summary>
public static class RecordingPolicy
{
    /// <summary>
    /// The user's configured record and stop keys. The current emergency shortcut
    /// is supplied through ReservedHotkeyRegistry, so Esc is available after rebinding it.
    /// </summary>
    public static bool IsTransportVirtualKey(int vk, uint recordVk, uint stopVk)
        => (recordVk != 0 && vk == recordVk)
           || (stopVk != 0 && vk == stopVk);

    public static bool ShouldRecordKeyboard(
        InputMode mode,
        int virtualKey,
        ReservedHotkeyRegistry reserved,
        bool foregroundIsMacroEngine,
        uint recordVk,
        uint stopVk)
    {
        if (mode != InputMode.Recording)
            return false;
        if (foregroundIsMacroEngine)
            return false;
        if (IsTransportVirtualKey(virtualKey, recordVk, stopVk))
            return false;
        if (reserved.Contains(virtualKey))
            return false;
        return true;
    }

    public static bool ShouldRecordMouse(InputMode mode, bool hitIsMacroEngineWindow, long timestampTicks)
    {
        if (mode != InputMode.Recording)
            return false;
        if (hitIsMacroEngineWindow)
            return false;
        if (InputModeGate.InPostCaptureGrace(timestampTicks))
            return false;
        return true;
    }
}
