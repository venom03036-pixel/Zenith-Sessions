namespace MacroSuite.App.ViewModels;

public enum HotkeyConflictChoice
{
    Cancel = 0,
    UnbindPrevious = 1,
    UseSuggested = 2
}

public readonly record struct HotkeyConflictPrompt(
    string Hotkey,
    string PreviousOwner,
    string NewOwner,
    string SuggestedHotkey,
    string OriginalHotkey = "");
