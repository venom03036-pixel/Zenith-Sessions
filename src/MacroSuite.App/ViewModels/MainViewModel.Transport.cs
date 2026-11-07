namespace MacroSuite.App.ViewModels;

public sealed partial class MainViewModel
{
    private Exception? _emergencySignalFailure;
    public bool EmergencyStopAvailable => EditorVM.EmergencyStopIsReady?.Invoke() != false;
    public void RefreshEmergencyStopReadiness()
    {
        OnPropertyChanged(nameof(EmergencyStopAvailable)); OnPropertyChanged(nameof(TopStatusText));
        EditorVM.RunCommand.RaiseCanExecuteChanged(); EditorVM.RecordCommand.RaiseCanExecuteChanged();
        AutoClickerVM.ToggleRunCommand.RaiseCanExecuteChanged();
    }
    /// <summary>The physical emergency listener calls this before posting presentation work.</summary>
    public void SignalEmergencyStop()
    {
        InputOperations.SignalStop();
        try { EditorVM.SignalEmergencyStop(); }
        catch (Exception ex) { Interlocked.Exchange(ref _emergencySignalFailure, ex); }
        try { AutoClickerVM.SignalEmergencyStop(); }
        catch (Exception ex) { Interlocked.Exchange(ref _emergencySignalFailure, ex); }
    }
    public async Task PauseForEditorFocusAsync()
    {
        await EditorVM.PauseForEditorFocusAsync();
        await AutoClickerVM.PauseForEditorFocusAsync();
    }
    public Task ToggleTransportPauseAsync() => AutoClickerVM.IsRunning ? AutoClickerVM.TogglePauseAsync() : ToggleEditorPauseAsync();
    private Task ToggleEditorPauseAsync() => EditorVM.PauseCommand.CanExecute(null) ? EditorVM.PauseFromTransportAsync() : Task.CompletedTask;
}
