using System.Diagnostics;
using MacroSuite.App.Common;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Input;

namespace MacroSuite.App.ViewModels;

/// <summary>
/// Top-level shell view model governing navigation, global panic stop, and status bar telemetry.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ILicenseService _licenseService;
    private object _currentViewModel;
    private string _executionState = "Ready";
    private string _statusMessage = "Ready";
    private bool _statusIsRunningTelemetry;
    private bool _emergencyStopped;
    private bool _wasAnythingRunning;
    private int _currentFps = 30;
    private readonly object _inputLifecycleGate = new();
    private Task _inputRecovery = Task.CompletedTask;
    private Task? _inputShutdown;

    public HostInputOperationArbiter InputOperations { get; }
    public Task InputRecovery { get { lock (_inputLifecycleGate) return _inputRecovery; } }

    public MacroEditorViewModel EditorVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public LicensingViewModel LicensingVM { get; }
    public UsageDashboardViewModel UsageVM { get; }
    public AutoClickerViewModel AutoClickerVM { get; }

    public object CurrentViewModel
    {
        get => _currentViewModel;
        set
        {
            if (!SetProperty(ref _currentViewModel, value)) return;
            OnPropertyChanged(nameof(IsEditorSelected));
            OnPropertyChanged(nameof(IsSettingsSelected));
            OnPropertyChanged(nameof(IsAccountSelected));
            OnPropertyChanged(nameof(IsUsageSelected));
            OnPropertyChanged(nameof(IsAutoClickerSelected));
        }
    }

    public bool IsEditorSelected => ReferenceEquals(CurrentViewModel, EditorVM);
    public bool IsSettingsSelected => ReferenceEquals(CurrentViewModel, SettingsVM);
    public bool IsAccountSelected => ReferenceEquals(CurrentViewModel, LicensingVM);
    public bool IsUsageSelected => ReferenceEquals(CurrentViewModel, UsageVM);
    public bool IsAutoClickerSelected => ReferenceEquals(CurrentViewModel, AutoClickerVM);

    public string ExecutionState
    {
        get => _executionState;
        set => SetProperty(ref _executionState, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            _statusIsRunningTelemetry = false;
            SetProperty(ref _statusMessage, value);
        }
    }

    public int CurrentFps
    {
        get => _currentFps;
        set => SetProperty(ref _currentFps, value);
    }

    public string ActiveProvider => SettingsVM.SelectedProvider.ToUpperInvariant();
    public string PanicHotkeyDisplay => SettingsVM.PanicHotkey;
    public string PanicButtonText => $"Panic stop · {PanicHotkeyDisplay}";

    public string LicenseTier => _licenseService.GetCurrentLicense().Type.ToString().ToUpperInvariant();

    /// <summary>True when either the macro engine or the auto-clicker is actively running.</summary>
    public bool IsAnythingRunning => EditorVM.IsRunning || EditorVM.IsPaused || EditorVM.IsRecordingBusy
        || EditorVM.IsDetectionTesting || AutoClickerVM.IsRunning || AutoClickerVM.IsStarting;

    public string TopStatusText
    {
        get
        {
            if (_emergencyStopped) return "Emergency stopped";
            if (!EmergencyStopAvailable) return "Emergency shortcut unavailable";
            if (EditorVM.IsDetectionTesting) return "Testing detection";
            if (EditorVM.IsRecordingBusy) return EditorVM.RecordingStateText;
            if (EditorVM.IsRunning || EditorVM.IsPaused) return EditorVM.TransportStateText;
            if (AutoClickerVM.IsPaused) return "Paused · Auto clicker";
            if (AutoClickerVM.IsRunning) return $"Running · Auto clicker · {AutoClickerVM.ClicksPerSecond:0.##}/s";
            return EditorVM.TransportStateText;
        }
    }

    public string TopStatusDotFill
    {
        get
        {
            if (ExecutionState == "PANIC TERMINATED") return "#EF4444";
            if (EditorVM.IsPaused || EditorVM.IsRecordingPaused || AutoClickerVM.IsPaused) return "#FBBF24";
            if (IsAnythingRunning) return "#4ADEA3";
            return "#93939F";
        }
    }

    public string TopStatusBorderBrush
    {
        get
        {
            if (ExecutionState == "PANIC TERMINATED") return "#EF4444";
            if (IsAnythingRunning) return "#4ADEA3";
            return "#44444E";
        }
    }

    public string TopStatusBackground
    {
        get
        {
            if (ExecutionState == "PANIC TERMINATED") return "#26EF4444";
            if (IsAnythingRunning) return "#184ADEA3";
            return "#18181C";
        }
    }

    public string TopStatusForeground
    {
        get
        {
            if (ExecutionState == "PANIC TERMINATED") return "#EF4444";
            if (EditorVM.IsPaused || EditorVM.IsRecordingPaused || AutoClickerVM.IsPaused) return "#FBBF24";
            if (IsAnythingRunning) return "#4ADEA3";
            return "#B0B0BB";
        }
    }

    public string StateBadgeBackground
    {
        get
        {
            if (ExecutionState == "PANIC TERMINATED") return "#26EF4444";
            if (IsAnythingRunning) return "#1A00F5A0";
            return "#1A2333";
        }
    }

    public string StateBadgeForeground
    {
        get
        {
            if (ExecutionState == "PANIC TERMINATED") return "#EF4444";
            if (IsAnythingRunning) return "#00F5A0";
            return "#EAB308";
        }
    }

    public RelayCommand NavigateEditorCommand { get; }
    public RelayCommand NavigateSettingsCommand { get; }
    public RelayCommand NavigateLicensingCommand { get; }
    public RelayCommand NavigateUsageCommand { get; }
    public RelayCommand NavigateAutoClickerCommand { get; }
    public RelayCommand PanicCommand { get; }
    public RelayCommand GenerateMacroWithAICommand { get; }
    public RelayCommand ShowDiagnosticsCommand { get; }
    public event Action? MacroAuthoringRequested;
    public event Action? DiagnosticsRequested;
    public event Action? LicenseManagerRequested;
    public RelayCommand OpenLicenseManagerCommand { get; }
    public bool CanUseWorkspace => true;
    public bool IsOwnerBuild => BuildEnvironment.IsDeveloperBuild;
    public event Action? EmergencyStopped;

    public MainViewModel(
        MacroEditorViewModel editorVM,
        SettingsViewModel settingsVM,
        LicensingViewModel licensingVM,
        UsageDashboardViewModel usageVM,
        AutoClickerViewModel autoClickerVM,
        ILicenseService licenseService,
        HostInputOperationArbiter? inputOperations = null)
    {
        EditorVM = editorVM ?? throw new ArgumentNullException(nameof(editorVM));
        SettingsVM = settingsVM ?? throw new ArgumentNullException(nameof(settingsVM));
        LicensingVM = licensingVM ?? throw new ArgumentNullException(nameof(licensingVM));
        UsageVM = usageVM ?? throw new ArgumentNullException(nameof(usageVM));
        AutoClickerVM = autoClickerVM ?? throw new ArgumentNullException(nameof(autoClickerVM));
        _licenseService = licenseService ?? throw new ArgumentNullException(nameof(licenseService));
        InputOperations = inputOperations ?? EditorVM.InputOperations;
        if (inputOperations is not null && (!ReferenceEquals(inputOperations, EditorVM.InputOperations)
            || !ReferenceEquals(inputOperations, AutoClickerVM.InputOperations)))
            throw new ArgumentException("The application input producers must share one operation owner.", nameof(inputOperations));
        EditorVM.AppSettings = SettingsVM;

        _currentViewModel = EditorVM;
        CurrentFps = SettingsVM.TargetFps;

        NavigateEditorCommand = new RelayCommand(() => CurrentViewModel = EditorVM);
        NavigateSettingsCommand = new RelayCommand(() => CurrentViewModel = SettingsVM);
        NavigateLicensingCommand = new RelayCommand(() => CurrentViewModel = LicensingVM);
        NavigateUsageCommand = new RelayCommand(() =>
        {
            CurrentViewModel = UsageVM;
            _ = UsageVM.RefreshDataAsync();
        });
        NavigateAutoClickerCommand = new RelayCommand(() => CurrentViewModel = AutoClickerVM);
        PanicCommand = new RelayCommand(TriggerPanic);
        GenerateMacroWithAICommand = new RelayCommand(() =>
        {
            if (!IsAnythingRunning) MacroAuthoringRequested?.Invoke();
        }, () => !IsAnythingRunning);
        ShowDiagnosticsCommand = new RelayCommand(() => DiagnosticsRequested?.Invoke());
        OpenLicenseManagerCommand = new RelayCommand(() => LicenseManagerRequested?.Invoke(), () => IsOwnerBuild && !IsAnythingRunning);
        EditorVM.ExternalInputIsActive = () => AutoClickerVM.IsRunning || AutoClickerVM.IsStarting;
        AutoClickerVM.HotkeyCaptureRequested += () => EditorVM.BeginQuickBindCapture("autoclicker");
        AutoClickerVM.StartHotkey = SettingsVM.AutoClickerHotkey;
        EditorVM.AutoClickerHotkey = AutoClickerVM.StartHotkey;
        AutoClickerVM.HotkeyChanged += () =>
        {
            EditorVM.AutoClickerHotkey = AutoClickerVM.StartHotkey;
            SettingsVM.AutoClickerHotkey = AutoClickerVM.StartHotkey;
        };
        EditorVM.QuickBindChanged += (target, key) =>
        {
            if (target == "autoclicker") AutoClickerVM.StartHotkey = key;
        };

        EditorVM.StatusChanged += msg => StatusMessage = msg;
        _licenseService.LicenseChanged += (s, e) => OnPropertyChanged(nameof(LicenseTier));
        SettingsVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.SelectedProvider))
            {
                OnPropertyChanged(nameof(ActiveProvider));
            }
            else if (e.PropertyName == nameof(SettingsViewModel.TargetFps))
            {
                CurrentFps = SettingsVM.TargetFps;
            }
            else if (e.PropertyName == nameof(SettingsViewModel.PanicHotkey))
            {
                OnPropertyChanged(nameof(PanicHotkeyDisplay));
                OnPropertyChanged(nameof(PanicButtonText));
                UpdateRunningTelemetry();
            }
        };
        EditorVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EditorVM.IsRunning) or nameof(EditorVM.IsPaused)
                or nameof(EditorVM.IsRecordingBusy) or nameof(EditorVM.IsRecording) or nameof(EditorVM.IsDetectionTesting)
                or nameof(EditorVM.TransportStateText) or nameof(EditorVM.RecordingStateText) or nameof(EditorVM.IsRecordingPaused) or nameof(EditorVM.IsRecordingFinishing))
            {
                UpdateRunningTelemetry();
            }
            if (e.PropertyName == nameof(EditorVM.QuickBindCaptureTarget))
                AutoClickerVM.IsCapturingHotkey = EditorVM.QuickBindCaptureTarget == "autoclicker";
        };
        AutoClickerVM.RunningStateChanged += () =>
        {
            UpdateRunningTelemetry();
        };
    }

    private void UpdateRunningTelemetry()
    {
        EditorVM.RefreshImportAvailability();
        bool active = IsAnythingRunning;
        if (active && !_wasAnythingRunning) _emergencyStopped = false;
        _wasAnythingRunning = active;
        AutoClickerVM.IsMacroActive = EditorVM.IsRunning || EditorVM.IsPaused || EditorVM.IsRecordingBusy || EditorVM.IsDetectionTesting;
        GenerateMacroWithAICommand.RaiseCanExecuteChanged();

        if (_emergencyStopped)
        {
            ExecutionState = "PANIC TERMINATED";
        }
        else
        {
            if (EditorVM.IsDetectionTesting)
            {
                ExecutionState = "Testing detection";
                SetRunningStatusMessage("Testing detection · No actions will run");
            }
            else if (EditorVM.IsRunning || EditorVM.IsPaused || EditorVM.IsRecordingBusy)
            {
                ExecutionState = EditorVM.IsRecordingBusy ? EditorVM.RecordingStateText : EditorVM.TransportStateText;
                SetRunningStatusMessage($"{EditorVM.TransportStateText} · Stop: {SettingsVM.StopHotkey} · Emergency stop: {PanicHotkeyDisplay}");
            }
            else if (AutoClickerVM.IsRunning)
            {
                ExecutionState = AutoClickerVM.IsPaused ? "Paused · Auto clicker" : $"Running · Auto clicker · {AutoClickerVM.ClicksPerSecond:0.##}/s";
                SetRunningStatusMessage($"Auto-clicker active ({AutoClickerVM.ClicksPerSecond:F0} CPS) - Press {AutoClickerVM.StartHotkey} to stop, {PanicHotkeyDisplay} for panic.");
            }
            else
            {
                ExecutionState = EditorVM.TransportStateText;
                // The editor publishes its final result before IsRunning becomes false.
                // Keep that explanation visible; only clear our own transient running banner.
                if (_statusIsRunningTelemetry)
                    StatusMessage = EditorVM.TransportStateText;
            }
        }
        OnPropertyChanged(nameof(IsAnythingRunning));
        OnPropertyChanged(nameof(TopStatusText));
        OnPropertyChanged(nameof(TopStatusDotFill));
        OnPropertyChanged(nameof(TopStatusBorderBrush));
        OnPropertyChanged(nameof(TopStatusBackground));
        OnPropertyChanged(nameof(TopStatusForeground));
        OnPropertyChanged(nameof(StateBadgeBackground));
        OnPropertyChanged(nameof(StateBadgeForeground));
    }

    private void SetRunningStatusMessage(string message)
    {
        StatusMessage = message;
        _statusIsRunningTelemetry = true;
    }

    public void CancelShortcutCapture()
    {
        SettingsVM.ConsumeCaptureTarget();
        EditorVM.CompleteQuickBindCapture(null);
        EditorVM.ActionEditor.IsCapturingKey = false;
        EditorVM.IsScriptHotkeyBoxFocused = false;
    }

    public void TriggerPanic()
    {
        SignalEmergencyStop();
        var recoveryPublication = ReserveInputRecovery(InputOperations.Snapshot);
        try
        {
            if (Interlocked.Exchange(ref _emergencySignalFailure, null) is { } signalFailure) Trace.TraceError($"Emergency signal failure: {signalFailure.GetType().Name}");
            _emergencyStopped = true;
            _wasAnythingRunning = IsAnythingRunning;
            try
            {
                EditorVM.PanicMacro();
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Shell panic failed to stop the editor: {ex}");
            }

            try
            {
                AutoClickerVM.StopClicker();
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Shell panic failed to stop the auto-clicker: {ex}");
            }
            // Auxiliary cancellation must never prevent either input producer from stopping.
            if (EmergencyStopped is { } handlers)
                foreach (Action handler in handlers.GetInvocationList())
                    try { handler(); }
                    catch (Exception ex) { Trace.TraceError($"Shell panic auxiliary cancellation failed: {ex.GetType().Name}"); }
            ExecutionState = "PANIC TERMINATED";
            StatusMessage = "Emergency stop initiated. Releasing owned input and preserving the recording.";
            UpdateRunningTelemetry();

            // Release any mouse capture currently held by WPF controls
            try
            {
                System.Windows.Input.Mouse.Capture(null);
            }
            catch { }
        }
        finally { recoveryPublication?.TrySetResult(InputOperations.Snapshot); }
    }

    private TaskCompletionSource<HostInputOperationSnapshot>? ReserveInputRecovery(HostInputOperationSnapshot captured)
    {
        if (captured.OperationId is null) return null;
        TaskCompletionSource<HostInputOperationSnapshot> published;
        lock (_inputLifecycleGate)
        {
            if (!_inputRecovery.IsCompleted || InputOperations.Snapshot.IsClosed) return null;
            published = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _inputRecovery = RecoverStoppedInputAsync(captured, published.Task);
        }
        return published;
    }

    private async Task RecoverStoppedInputAsync(HostInputOperationSnapshot original, Task<HostInputOperationSnapshot> published)
    {
        var captured = await published;
        if (captured.OperationId != original.OperationId) return;
        try
        {
            await InputOperations.WaitForRetirementAsync(captured.OperationId!.Value);
            var current = InputOperations.Snapshot;
            if (current.OperationId != captured.OperationId || current.Kind != captured.Kind
                || current.StopGeneration != captured.StopGeneration || current.IsClosed || !current.IsQuarantined) return;
            bool recovered = current.Kind switch
            {
                HostInputOperationKind.Playback or HostInputOperationKind.Recording => await EditorVM.RecoverInputOwnershipAsync(),
                HostInputOperationKind.AutoClicker => await AutoClickerVM.RecoverInputOwnershipAsync(),
                _ => false // A backend key release cannot prove that a picker/capture owner exited.
            };
            current = InputOperations.Snapshot;
            if (current.StopGeneration == captured.StopGeneration && !current.IsClosed
                && (current.OperationId is null || current.OperationId == captured.OperationId))
                StatusMessage = recovered ? "Emergency stop cleanup completed. Start another activity deliberately."
                    : "Emergency stop cleanup remains incomplete. New input activities are blocked.";
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Emergency input cleanup failed: {ex.GetType().Name}");
            var current = InputOperations.Snapshot;
            if (!current.IsClosed && current.OperationId == captured.OperationId && current.StopGeneration == captured.StopGeneration)
                StatusMessage = "Emergency stop cleanup remains incomplete. New input activities are blocked.";
        }
    }

    /// <summary>Closes admission synchronously; actual producers and recovery are joined before shared services may be disposed.</summary>
    public Task ShutdownInputOperationsAsync()
    {
        InputOperations.Close();
        // Older isolated callers may have constructed the view models independently. Production
        // explicitly injects and validates the one shared owner above.
        if (!ReferenceEquals(EditorVM.InputOperations, InputOperations)) EditorVM.InputOperations.Close();
        if (!ReferenceEquals(AutoClickerVM.InputOperations, InputOperations)) AutoClickerVM.InputOperations.Close();
        TaskCompletionSource published;
        Task result;
        lock (_inputLifecycleGate)
        {
            if (_inputShutdown is { IsCompleted: false } || _inputShutdown?.IsCompletedSuccessfully == true) return _inputShutdown!;
            published = new(TaskCreationOptions.RunContinuationsAsynchronously);
            result = _inputShutdown = ShutdownInputCoreAsync(published.Task, _inputRecovery);
        }
        published.SetResult();
        return result;
    }

    private async Task ShutdownInputCoreAsync(Task published, Task recovery)
    {
        await published;
        await Task.WhenAll(EditorVM.ShutdownAsync(), AutoClickerVM.ShutdownAsync(), recovery);
        await InputOperations.DrainAsync();
        if (!ReferenceEquals(EditorVM.InputOperations, InputOperations)) await EditorVM.InputOperations.DrainAsync();
        if (!ReferenceEquals(AutoClickerVM.InputOperations, InputOperations)) await AutoClickerVM.InputOperations.DrainAsync();
    }
}
