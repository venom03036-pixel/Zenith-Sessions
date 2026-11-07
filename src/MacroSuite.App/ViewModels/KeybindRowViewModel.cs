using MacroSuite.App.Common;

namespace MacroSuite.App.ViewModels;

/// <summary>One named row on the HOTKEYS tab (app control or macro).</summary>
public sealed class KeybindRowViewModel : ObservableObject
{
    private string _hotkey = string.Empty;
    private string _hotkeyDisplay = "—";
    private bool _isCapturing;

    public KeybindRowViewModel(string id, string name, string hotkey, bool isEditable, RelayCommand captureCommand)
    {
        Id = id;
        Name = name;
        IsEditable = isEditable;
        CaptureCommand = captureCommand;
        _hotkey = hotkey;
        _hotkeyDisplay = string.IsNullOrWhiteSpace(hotkey) ? "—" : hotkey;
    }

    public string Id { get; }
    public string Name { get; }
    public bool IsEditable { get; }
    public RelayCommand CaptureCommand { get; }

    public string Hotkey
    {
        get => _hotkey;
        set => SetProperty(ref _hotkey, value);
    }

    public string HotkeyDisplay
    {
        get => _hotkeyDisplay;
        set => SetProperty(ref _hotkeyDisplay, value);
    }

    public bool IsCapturing
    {
        get => _isCapturing;
        set => SetProperty(ref _isCapturing, value);
    }
}
