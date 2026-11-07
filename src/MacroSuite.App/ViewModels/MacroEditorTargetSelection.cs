using System.Collections.ObjectModel;
using MacroSuite.App.Common;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Models;

namespace MacroSuite.App.ViewModels;

public sealed partial class MacroEditorViewModel
{
    public IReadOnlyList<string> TargetModes { get; } = ["Screen", "Active external window", "Application", "Specific window"];
    public ObservableCollection<TargetWindowInfo> AvailableTargetWindows { get; } = [];
    public void RegisterOwnedTargetWindow(IntPtr window) => Targets.RegisterOwnWindow(window);
    public Task PauseFromTransportAsync() => PauseMacroAsync();
    private TargetWindowInfo? _selectedTargetWindow;
    private CancellationTokenSource? _targetEnumerationCancellation;
    private Task _targetEnumerationTask = Task.CompletedTask;
    private RelayCommand? _refreshTargetWindowsCommand;
    public RelayCommand RefreshTargetWindowsCommand => _refreshTargetWindowsCommand ??= new RelayCommand(() =>
    { if (_targetEnumerationTask.IsCompleted) _targetEnumerationTask = RefreshTargetWindowsAsync(); }, () => CanEditMacro);
    public string TargetWindowDisplay => SelectedScript?.Target is { Mode: MacroTargetMode.Application or MacroTargetMode.SpecificWindow } target
        ? (target.ApplicationId + (target.Mode == MacroTargetMode.SpecificWindow ? " · " + target.WindowTitle : ""))
        : "Choose a window below before selecting Application or Specific window.";
    public TargetWindowInfo? SelectedTargetWindow
    {
        get => _selectedTargetWindow;
        set
        {
            if (!SetProperty(ref _selectedTargetWindow, value) || IsEditingLocked || value is null || value.IsOwnWindow) return;
            if (SelectedScript?.Target?.Mode is MacroTargetMode.Application or MacroTargetMode.SpecificWindow)
                ApplyTargetRule(new() { Mode = SelectedScript.Target.Mode, ApplicationId = value.ApplicationId, WindowTitle = value.Title });
        }
    }
    public int TargetModeIndex
    {
        get => (int)(SelectedScript?.Target?.Mode ?? MacroTargetMode.Screen);
        set
        {
            if (IsEditingLocked || SelectedScript is null || value is < 0 or > 3 || (SelectedScript.Target is not null && value == TargetModeIndex)) return;
            var mode = (MacroTargetMode)value;
            if (mode is MacroTargetMode.Application or MacroTargetMode.SpecificWindow)
            {
                var window = SelectedTargetWindow;
                if (window is null || window.IsOwnWindow)
                { NotifyStatus("Refresh windows and choose the target application first, then select its target mode."); OnPropertyChanged(nameof(TargetModeIndex)); return; }
                ApplyTargetRule(new() { Mode = mode, ApplicationId = window.ApplicationId, WindowTitle = window.Title });
            }
            else ApplyTargetRule(new() { Mode = mode });
        }
    }
    public string TargetSummary => SelectedScript?.Target switch
    {
        { Mode: MacroTargetMode.ActiveExternalWindow } => "Target: active external window (pinned when starting)",
        { Mode: MacroTargetMode.Application } rule => "Target: " + rule.ApplicationId,
        { Mode: MacroTargetMode.SpecificWindow } rule => "Target: " + rule.WindowTitle,
        { Mode: MacroTargetMode.Screen } => "Target: screen",
        _ => "Target: screen (legacy macro)"
    };
    private void ApplyTargetRule(MacroTargetRule target)
    {
        target.Validate();
        if (SelectedScript is null) return;
        SelectedScript.Target = target;
        RefreshTargetPresentation(); RefreshSaveState();
    }
    private void RefreshTargetPresentation()
    {
        OnPropertyChanged(nameof(TargetModeIndex)); OnPropertyChanged(nameof(TargetSummary));
        OnPropertyChanged(nameof(InputTargetText)); OnPropertyChanged(nameof(TargetWindowDisplay));
        RefreshTargetWindowsCommand.RaiseCanExecuteChanged();
    }
    private void TargetScriptChanged()
    {
        var target = SelectedScript?.Target;
        _selectedTargetWindow = AvailableTargetWindows.FirstOrDefault(window =>
            string.Equals(window.ApplicationId, target?.ApplicationId, StringComparison.OrdinalIgnoreCase)
            && (target?.Mode != MacroTargetMode.SpecificWindow || window.Title == target.WindowTitle));
        OnPropertyChanged(nameof(SelectedTargetWindow));
        RefreshTargetPresentation();
    }
    private async Task RefreshTargetWindowsAsync()
    {
        _targetEnumerationCancellation?.Dispose();
        using var cancellation = new CancellationTokenSource();
        _targetEnumerationCancellation = cancellation;
        try
        {
            var windows = await Targets.EnumerateAsync(cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested) return;
            AvailableTargetWindows.Clear();
            foreach (var window in windows.Where(window => !window.IsOwnWindow && !string.IsNullOrWhiteSpace(window.Title)).Take(512)) AvailableTargetWindows.Add(window);
            var target = SelectedScript?.Target;
            _selectedTargetWindow = AvailableTargetWindows.FirstOrDefault(window => string.Equals(window.ApplicationId, target?.ApplicationId, StringComparison.OrdinalIgnoreCase) &&
                (target?.Mode != MacroTargetMode.SpecificWindow || window.Title == target.WindowTitle));
            OnPropertyChanged(nameof(SelectedTargetWindow));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { NotifyStatus("Could not list target windows: " + ex.Message); }
        finally { if (ReferenceEquals(_targetEnumerationCancellation, cancellation)) _targetEnumerationCancellation = null; }
    }
    public async Task ShutdownTargetSelectionAsync()
    {
        _targetEnumerationCancellation?.Cancel();
        await _targetEnumerationTask;
        var targets = _targets;
        if (targets is IAsyncDisposable asyncTargets) await asyncTargets.DisposeAsync();
        else targets?.Dispose();
        if (ReferenceEquals(_targets, targets)) _targets = null;
    }
    private void PlaybackTargetPaused(object? sender, EventArgs args)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        void Update() { if (ReferenceEquals(sender, _playbackSession)) { IsPaused = true; NotifyStatus(_playbackSession?.PauseReason ?? "Focus the selected target, then resume manually."); } }
        if (dispatcher is null || dispatcher.CheckAccess()) Update(); else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(Update);
    }
}
