using MacroSuite.App.Common;

namespace MacroSuite.App.ViewModels;

public sealed partial class SettingsViewModel
{
    private int _shortcutDefaultsVersion = 2;
    public Func<bool>? CanChangeHotkeys { get; set; }
    public Func<IReadOnlyDictionary<string, string>, bool>? TryApplyHotkeys { get; set; }
    private string _shortcutAvailability = "Shortcuts are checked when the main window opens.";
    public string ShortcutAvailability { get => _shortcutAvailability; set => SetProperty(ref _shortcutAvailability, value); }
    private RelayCommand? _resetTransportDefaultsCommand;
    public RelayCommand ResetTransportDefaultsCommand => _resetTransportDefaultsCommand ??= new RelayCommand(ResetTransportDefaults);

    private bool PrepareShortcut(string id, string next, string current)
    {
        if (HotkeyCodec.AreEqual(next, current)) return true;
        if (!HotkeyCodec.TryParse(next, out _, out _))
        {
            PersistenceStatus = "Choose a valid shortcut. The previous shortcut was kept.";
            return false;
        }
        if (CanChangeHotkeys?.Invoke() == false)
        {
            PersistenceStatus = "Finish the current recording, save, playback, or detection test before changing shortcuts.";
            return false;
        }
        return TryApplyHotkeys?.Invoke(new Dictionary<string, string> { [id] = next }) != false;
    }

    public void ResetTransportDefaults()
    {
        if (CanChangeHotkeys?.Invoke() == false)
        {
            PersistenceStatus = "Finish the current operation before resetting shortcuts.";
            return;
        }
        var defaults = new Dictionary<string, string> { ["record"] = "F6", ["stop"] = "F8", ["play"] = "F4", ["pause"] = "F5" };
        if (TryApplyHotkeys?.Invoke(defaults) == false) return;
        _shortcutDefaultsVersion = 2;
        _recordHotkey = "F6"; _stopHotkey = "F8"; _playHotkey = "F4"; _pauseHotkey = "F5";
        foreach (string property in new[] { nameof(RecordHotkey), nameof(StopHotkey), nameof(PlayHotkey), nameof(PauseHotkey),
            nameof(RecordHotkeyDisplay), nameof(StopHotkeyDisplay), nameof(PlayHotkeyDisplay), nameof(PauseHotkeyDisplay) }) OnPropertyChanged(property);
        // Publish after all values have changed, so observers never see a half-applied reset.
        RecordHotkeyChanged?.Invoke(_recordHotkey); StopHotkeyChanged?.Invoke(_stopHotkey);
        PlayHotkeyChanged?.Invoke(_playHotkey); PauseHotkeyChanged?.Invoke(_pauseHotkey);
        SaveConfig();
    }
}
