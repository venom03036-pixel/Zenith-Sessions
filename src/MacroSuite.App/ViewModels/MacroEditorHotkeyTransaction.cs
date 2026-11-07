namespace MacroSuite.App.ViewModels;

public sealed partial class MacroEditorViewModel
{
    /// <summary>Host validates and acquires a replacement before this editor changes its effective binding.</summary>
    public Func<string, string, bool>? TryApplyTransportHotkey { get; set; }
    public Func<bool>? CommitVisibleRecordingFields { get; set; }
    public Func<bool>? EmergencyStopIsReady { get; set; }
    private bool EnsureEmergencyStopReady()
    {
        if (EmergencyStopIsReady?.Invoke() != false) return true;
        NotifyStatus("The emergency shortcut is unavailable. Reconfigure it in Settings or restart Zenith before starting automation. Stop and Panic buttons remain available.");
        return false;
    }
    public void ReportRecordingFieldError(string field, string problem)
        => NotifyStatus($"Fix {field} before recording: {problem}");
}
