using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MacroSuite.App.Common;
using MacroSuite.App.Overlays;
using MacroSuite.App.Services;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.Input;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Infrastructure.Input;

namespace MacroSuite.App;

public partial class MainWindow : Window
{
    private GlobalHotkeyService? _hotkeyService;
    private SystemTrayService? _trayService;
    private RuntimeThemeIconService? _themeIcons;
    private MiniModeOverlay? _miniMode;
    private OwnedEmergencyHotkeyListener? _emergencyHotkeyListener;
    private string? _activeEmergencyHotkey;
    private readonly QueuedTransportDispatcher _transportDispatcher;
    private AIMacroAuthoringWindow? _authoringWindow;
    private AIMacroAuthoringViewModel? _authoringViewModel;
    private bool _closeAfterAuthoring;
    private bool _shutdownPending;
    private bool _shutdownComplete;
    private WindowState _windowStateBeforeMini = WindowState.Normal;
    private bool _restoringMainWindow;
    public Func<AIMacroAuthoringViewModel>? CreateMacroAuthoringViewModel { get; set; }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _transportDispatcher = new QueuedTransportDispatcher(Dispatcher);
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        StateChanged += OnWindowStateChanged;
        viewModel.MacroAuthoringRequested += OpenMacroAuthoring;
        viewModel.DiagnosticsRequested += ShowDiagnostics;
#if DEVELOPER_MODE
        viewModel.LicenseManagerRequested += () =>
        {
            using var trust = typeof(MainWindow).Assembly.GetManifestResourceStream("Zenith.ActivationTrust.json");
            using var reader = trust is null ? null : new StreamReader(trust);
            new MacroSuite.LicenseIssuer.LicenseManagerWindow(expectedTrustManifestJson: reader?.ReadToEnd()) { Owner = this }.ShowDialog();
        };
#endif
        viewModel.EmergencyStopped += _transportDispatcher.Invalidate;
        viewModel.EmergencyStopped += () => _authoringViewModel?.Cancel();
        viewModel.PropertyChanged += OnAppearanceExecutionChanged;
        viewModel.SettingsVM.Appearance.SetExecutionActive(viewModel.IsAnythingRunning);
    }

    private void OnAppearanceExecutionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if ((e.PropertyName == nameof(MainViewModel.IsAnythingRunning) || e.PropertyName == nameof(MainViewModel.CanUseWorkspace)) && DataContext is MainViewModel vm)
        {
            vm.SettingsVM.Appearance.SetExecutionActive(vm.IsAnythingRunning);
            if (!vm.IsAnythingRunning && (_refreshHotkeysPending || e.PropertyName == nameof(MainViewModel.CanUseWorkspace))) RefreshGlobalHotkeys(vm);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        var hWnd = helper.Handle;

        _hotkeyService = new GlobalHotkeyService(hWnd);
        _hotkeyService.SuppressDispatch = () => _shutdownPending || _authoringWindow is not null || (IsVisible && HotkeyFocusGuard.ShouldSuppressTransportHotkeys());
        _hotkeyService.CaptureInterceptor = (modifiers, vk) =>
        {
            if (_authoringWindow is not null) return true;
            if (DataContext is not MainViewModel captureVm || !IsCapturingAnyBind(captureVm))
                return false;

            // Dialog / location pick own Tab and Esc. Do not run macros or panic.
            if (_bindDialogOpen
                || captureVm.AutoClickerVM.IsPickingLocation
                || captureVm.EditorVM.ActionEditor.IsPickingLocation)
                return true;

            string formatted = HotkeyCodec.Format(modifiers, vk);
            Dispatcher.BeginInvoke(() => TryConsumeCapturedHotkey(formatted), DispatcherPriority.Input);
            return true;
        };
        if (DataContext is MainViewModel vm)
        {
            _hotkeyService.HotkeyConflictDetected += (res) =>
            {
                Dispatcher.Invoke(() =>
                {
                    vm.StatusMessage = $"Hotkey Conflict: {res.ErrorMessage}";
                });
            };

            Topmost = vm.SettingsVM.AlwaysOnTop;
            vm.SettingsVM.AlwaysOnTopChanged += (onTop) => Dispatcher.Invoke(() => { Topmost = onTop; });

            if (vm.CanUseWorkspace) vm.EditorVM.RegisterOwnedTargetWindow(hWnd);
            vm.EditorVM.CheckForRecordingRecovery();
            // Pull the saved transport binds in before the first registration pass, otherwise
            // the defaults are briefly treated as reserved and scripts get moved off them.
            vm.EditorVM.RecordHotkey = vm.SettingsVM.RecordHotkey;
            vm.EditorVM.StopHotkey = vm.SettingsVM.StopHotkey;
            vm.EditorVM.PlayHotkey = vm.SettingsVM.PlayHotkey;
            vm.EditorVM.PauseHotkey = vm.SettingsVM.PauseHotkey;
            vm.EditorVM.PanicHotkey = vm.SettingsVM.PanicHotkey;
            vm.EditorVM.MiniToolbarClickThroughHotkey = vm.SettingsVM.MiniToolbarClickThroughHotkey;
            vm.EditorVM.ConfirmHotkeyConflictPrompter = prompt =>
            {
                _bindDialogOpen = true;
                try
                {
                    return HotkeyConflictDialog.Ask(this, prompt);
                }
                finally
                {
                    _bindDialogOpen = false;
                }
            };

            vm.EditorVM.TryApplyTransportHotkey = (target, key) => ApplyHotkeyBindings(vm, new Dictionary<string,string> { [target] = key }, true);
            vm.AutoClickerVM.TryApplyStartHotkey = key => ApplyHotkeyBindings(vm, new Dictionary<string,string> { ["autoclicker"] = key }, true);
            vm.EditorVM.EmergencyStopIsReady = () => _emergencyHotkeyListener?.IsRunning == true;
            vm.AutoClickerVM.EmergencyStopIsReady = () => _emergencyHotkeyListener?.IsRunning == true;
            vm.SettingsVM.CanChangeHotkeys = () => !vm.IsAnythingRunning;
            vm.SettingsVM.TryApplyHotkeys = proposed => ApplyHotkeyBindings(vm, proposed, true);
            RefreshGlobalHotkeys(vm);
            vm.EditorVM.HotkeysUpdated += () => Dispatcher.Invoke(() => RefreshGlobalHotkeys(vm));
            vm.AutoClickerVM.HotkeyChanged += () => Dispatcher.Invoke(() => RefreshGlobalHotkeys(vm));
            vm.SettingsVM.RecordHotkeyChanged += (h) => Dispatcher.Invoke(() => { vm.EditorVM.RecordHotkey = h; RefreshGlobalHotkeys(vm); });
            vm.SettingsVM.StopHotkeyChanged += (h) => Dispatcher.Invoke(() => { vm.EditorVM.StopHotkey = h; RefreshGlobalHotkeys(vm); });
            vm.SettingsVM.PlayHotkeyChanged += (h) => Dispatcher.Invoke(() => { vm.EditorVM.PlayHotkey = h; RefreshGlobalHotkeys(vm); });
            vm.SettingsVM.PauseHotkeyChanged += (h) => Dispatcher.Invoke(() => { vm.EditorVM.PauseHotkey = h; RefreshGlobalHotkeys(vm); });
            vm.SettingsVM.PanicHotkeyChanged += (h) => Dispatcher.Invoke(() => { vm.EditorVM.PanicHotkey = h; RefreshGlobalHotkeys(vm); });
            vm.SettingsVM.MiniToolbarClickThroughHotkeyChanged += h => Dispatcher.Invoke(() => { vm.EditorVM.MiniToolbarClickThroughHotkey = h; RefreshGlobalHotkeys(vm); });
            vm.SettingsVM.RequestMiniMode += () => Dispatcher.Invoke(SwitchToMiniMode);

            vm.EditorVM.ShowActionEditor = vm.SettingsVM.ShowActionEditor;
            vm.EditorVM.ActionEditorWidth = vm.SettingsVM.ActionEditorWidth;
            vm.EditorVM.LibraryPaneWidth = vm.SettingsVM.LibraryPaneWidth;
            vm.EditorVM.AutoSizeActionEditor = vm.SettingsVM.AutoSizeActionEditor;
            vm.EditorVM.LayoutPrefsChanged += () =>
            {
                vm.SettingsVM.ShowActionEditor = vm.EditorVM.ShowActionEditor;
                vm.SettingsVM.ActionEditorWidth = vm.EditorVM.ActionEditorWidth;
                vm.SettingsVM.LibraryPaneWidth = vm.EditorVM.LibraryPaneWidth;
                vm.SettingsVM.AutoSizeActionEditor = vm.EditorVM.AutoSizeActionEditor;
            };

            // Transport binds rebound from the editor's quick-bind pills persist to settings.
            vm.EditorVM.QuickBindChanged += (target, hotkey) => Dispatcher.Invoke(() =>
            {
                switch (target)
                {
                    case "record": vm.SettingsVM.RecordHotkey = hotkey; break;
                    case "stop": vm.SettingsVM.StopHotkey = hotkey; break;
                    case "play": vm.SettingsVM.PlayHotkey = hotkey; break;
                    case "pause": vm.SettingsVM.PauseHotkey = hotkey; break;
                    case "panic": vm.SettingsVM.PanicHotkey = hotkey; break;
                    case "mini-click-through": vm.SettingsVM.MiniToolbarClickThroughHotkey = hotkey; break;
                }
                RefreshGlobalHotkeys(vm);
            });

            vm.EditorVM.EnterCompactOverlay = () =>
            {
                if (!vm.SettingsVM.MiniModeWhileRecording) return;
                if (Dispatcher.CheckAccess())
                    SwitchToMiniMode();
                else
                    Dispatcher.Invoke(SwitchToMiniMode);
            };
            vm.EditorVM.LeaveCompactOverlay = () =>
            {
                if (Dispatcher.CheckAccess())
                    RestoreFromMiniMode();
                else
                    Dispatcher.Invoke(RestoreFromMiniMode);
            };

            // Create mini-mode overlay, attach VM
            _miniMode = new MiniModeOverlay();
            _miniMode.Attach(vm, this);
            _miniMode.IsVisibleChanged += (_, _) =>
            {
                vm.EditorVM.IsMiniToolbarVisible = _miniMode.IsVisible;
                if (!_shutdownPending) RefreshMiniClickThroughHotkey(vm);
            };
        }

        _trayService = new SystemTrayService(this);
        _trayService.AvailabilityChanged += available => _miniMode?.SetTrayRecoveryAvailable(available);
        _miniMode?.SetTrayRecoveryAvailable(_trayService.IsAvailable);
        _themeIcons = new RuntimeThemeIconService(this, _trayService);
        if (DataContext is MainViewModel iconVm)
        {
            iconVm.SettingsVM.Appearance.Changed += RefreshThemeIcons;
            iconVm.SettingsVM.PropertyChanged += OnIconSettingsChanged;
        }
        RefreshThemeIcons();
    }

    private void RefreshThemeIcons() => _themeIcons?.Refresh();
    private void OnIconSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.LogoColorMode)) RefreshThemeIcons();
    }

    public void SwitchToMiniMode()
    {
        if (DataContext is MainViewModel workspaceVm && !workspaceVm.CanUseWorkspace)
        {
            workspaceVm.NavigateLicensingCommand.Execute(null);
            return;
        }
        CancelPendingRestoreFocus();
        if (WindowState != WindowState.Minimized) _windowStateBeforeMini = WindowState;
        WindowState = WindowState.Minimized;
        Hide();
        _miniMode?.Show();
    }

    public bool ToggleMiniToolbarClickThrough()
    {
        if (_shutdownPending || _miniMode is not { IsVisible: true } || DataContext is not MainViewModel vm) return false;
        vm.SettingsVM.MiniToolbarClickThrough = !vm.SettingsVM.MiniToolbarClickThrough;
        if (_miniMode.IsClickThrough != vm.SettingsVM.MiniToolbarClickThrough)
        {
            vm.StatusMessage = "Windows could not apply the click-through setting. Use the toolbar or system tray to review its settings.";
            return false;
        }
        vm.StatusMessage = $"Mini toolbar click-through {(vm.SettingsVM.MiniToolbarClickThrough ? "on" : "off")} · {vm.SettingsVM.MiniToolbarClickThroughHotkey}";
        return true;
    }

    public void RestoreFromMiniMode()
    {
        if (_shutdownPending || _restoringMainWindow) return;
        _restoreTask = RestoreExistingWindowAsync();
    }

    private void RefreshMiniClickThroughHotkey(MainViewModel vm) { /* Recovery remains registered for the lifetime of the window. */ }

    private void OpenMacroAuthoring()
    {
        if (_shutdownPending || DataContext is not MainViewModel vm || vm.IsAnythingRunning || _authoringWindow is not null) return;
        if (CreateMacroAuthoringViewModel is null)
        {
            vm.StatusMessage = "AI macro authoring is unavailable. Check the AI provider settings.";
            return;
        }
        _transportDispatcher.Invalidate();
        vm.CancelShortcutCapture();
        Keyboard.ClearFocus();
        try
        {
            _authoringViewModel = CreateMacroAuthoringViewModel();
            _authoringWindow = new AIMacroAuthoringWindow(_authoringViewModel)
            {
                Owner = this,
                ChooseRegion = () =>
                {
                    // Remain hidden through capture, not just the rubber-band picker.
                    // This keeps the app out of the explicitly selected snapshot.
                    Hide();
                    return vm.EditorVM.PromptOwnedRegion();
                }
            };
            _authoringWindow.Closed += (_, _) =>
            {
                _authoringWindow = null;
                _authoringViewModel = null;
                IsEnabled = true;
                if (_closeAfterAuthoring) Dispatcher.BeginInvoke(new Action(Close));
                else { Show(); Activate(); }
            };
            // A modeless owned window can hide for screen selection without completing
            // a modal loop. Keep the studio disabled until the dialog has drained work.
            IsEnabled = false;
            _authoringWindow.Show();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"AI authoring dialog failed: {ex.GetType().Name}");
            vm.StatusMessage = "Could not open AI macro authoring: " + ex.Message;
            _authoringWindow?.Close();
            _authoringWindow = null;
            _authoringViewModel = null;
            IsEnabled = true;
            Show();
            Activate();
        }
    }

    private void ShowDiagnostics()
    {
        if (DataContext is MainViewModel vm)
            new DiagnosticsWindow(vm) { Owner = this }.ShowDialog();
    }

    /// <summary>
    /// Registers every global hotkey in strict priority order. Transport controls claim their
    /// combination first; a script bound to the same key is reassigned rather than silently
    /// shadowed, so macro hotkeys can never interfere with Record / Stop / Play / Pause.
    /// </summary>
    private void RefreshGlobalHotkeys(MainViewModel vm) => ApplyHotkeyBindings(vm, null, false);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (DataContext is not MainViewModel vm) return;
        if (_shutdownPending || _authoringWindow is not null) return;

        // Bind capture consumes the next keystroke so macros / transport binds do not fire.
        if (IsCapturingAnyBind(vm))
        {
            var captured = HotkeyCodec.FromKeyEvent(e);
            if (captured is null) return;
            if (TryConsumeCapturedHotkey(captured))
            {
                e.Handled = true;
                return;
            }
        }

        // Physical emergency presses are consumed by the global listener before WPF.
        // In-window injected keys must not turn a recorded action into a panic stop.
        if (HotkeyCodec.AreEqual(HotkeyCodec.FromKeyEvent(e), vm.EditorVM.PanicHotkey))
        {
            if (InputModeGate.IsResourceCapture) return;
            e.Handled = true;
            return;
        }

        // Do not intercept other hotkeys if user is actively binding a hotkey in HotkeyInputBox
        if (Keyboard.FocusedElement is TextBox tb && tb.Name == "HotkeyInputBox") return;
        if (HotkeyFocusGuard.ShouldSuppressTransportHotkeys()) return;
        if (vm.EditorVM.ActionEditor.IsCapturingKey) return;
        if (vm.EditorVM.ActionEditor.IsPickingLocation) return;

        if (IsCapturingAnyBind(vm) || WasCaptureJustConsumed())
            return;

        var hotkey = GetHotkeyString(e);
        if (string.IsNullOrEmpty(hotkey)) return;

        bool recordingTransport = HotkeyCodec.AreEqual(hotkey, vm.EditorVM.RecordHotkey)
            || HotkeyCodec.AreEqual(hotkey, vm.EditorVM.StopHotkey);
        if (recordingTransport && e.IsRepeat)
        {
            e.Handled = true;
            return;
        }

        // Dispatch registered hotkey actions immediately inside the window
        if (TryParseHotkey(hotkey, out uint modifiers, out uint vk))
        {
            // Registered global combinations execute through WM_HOTKEY once. A direct
            // Record call here would start and immediately stop from the same physical press.
            bool localDispatched = _hotkeyService?.Trigger(modifiers, vk, localOnly: true) == true;
            if (localDispatched || recordingTransport)
            {
                e.Handled = true;
                return;
            }
        }
    }

    private long _captureConsumedTick;
    private bool _bindDialogOpen;

    private bool IsCapturingAnyBind(MainViewModel vm)
        => _bindDialogOpen
           || vm.SettingsVM.HotkeyCaptureTarget.Length > 0
           || vm.EditorVM.QuickBindCaptureTarget.Length > 0
           || vm.EditorVM.IsScriptHotkeyBoxFocused
           || vm.EditorVM.ActionEditor.IsCapturingKey
           || vm.EditorVM.ActionEditor.IsPickingLocation
           || vm.AutoClickerVM.IsPickingLocation
           || Keyboard.FocusedElement is TextBox hotkeyBox && hotkeyBox.Name == "HotkeyInputBox";

    private bool WasCaptureJustConsumed()
        => Environment.TickCount64 - _captureConsumedTick < 400;

    /// <summary>
    /// Routes a keystroke into the active bind picker. Returns true when macros must not run.
    /// </summary>
    private bool TryConsumeCapturedHotkey(string? hotkey)
    {
        if (_authoringWindow is not null) return true;
        if (DataContext is not MainViewModel vm) return false;
        if (_bindDialogOpen) return true;
        if (!IsCapturingAnyBind(vm)) return false;
        if (string.IsNullOrWhiteSpace(hotkey)) return true;

        if (HotkeyCodec.AreEqual(hotkey, "ESC")
            && vm.SettingsVM.HotkeyCaptureTarget != "panic"
            && vm.EditorVM.QuickBindCaptureTarget != "panic")
        {
            vm.SettingsVM.CompleteHotkeyCapture("ESC");
            vm.EditorVM.CompleteQuickBindCapture("ESC");
            if (vm.EditorVM.ActionEditor.IsCapturingKey) vm.EditorVM.ActionEditor.IsCapturingKey = false;
            _captureConsumedTick = Environment.TickCount64;
            return true;
        }

        if (vm.SettingsVM.HotkeyCaptureTarget.Length > 0)
        {
            string target = vm.SettingsVM.ConsumeCaptureTarget();
            vm.EditorVM.TryAssignBind(target, hotkey);
        }
        else if (vm.EditorVM.QuickBindCaptureTarget.Length > 0)
        {
            vm.EditorVM.CompleteQuickBindCapture(hotkey);
        }
        else if (vm.EditorVM.IsScriptHotkeyBoxFocused
                 || Keyboard.FocusedElement is TextBox tb && tb.Name == "HotkeyInputBox")
        {
            vm.EditorVM.TryChangeHotkey(hotkey);
        }
        else if (vm.EditorVM.ActionEditor.IsCapturingKey
                 && HotkeyCodec.TryParse(hotkey, out _, out uint vk))
        {
            vm.EditorVM.ActionEditor.TryCaptureKey((int)vk);
        }

        _captureConsumedTick = Environment.TickCount64;
        return true;
    }

    public static string? GetHotkeyString(KeyEventArgs e) => HotkeyCodec.FromKeyEvent(e);

    public static bool TryParseHotkey(string hotkeyStr, out uint modifiers, out uint vk)
        => HotkeyCodec.TryParse(hotkeyStr, out modifiers, out vk);

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        // Restoring the cached full-window state is part of our own handoff. It
        // must not cancel its retry token or hide Mini before Show has completed.
        // A new minimize still supersedes that handoff and cancels it below.
        if (_restoringMainWindow && WindowState != WindowState.Minimized) return;
        CancelPendingRestoreFocus();
        if (WindowState != WindowState.Minimized) _windowStateBeforeMini = WindowState;
        if (MaximizeButton != null)
        {
            MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore Down" : "Enlarge / Maximize";
        }

        if (DataContext is not MainViewModel vm) return;

        // Show mini-mode pill when minimized while anything is running
        if (WindowState == WindowState.Minimized && vm.IsAnythingRunning)
        {
            _miniMode?.Show();
        }
        else if (WindowState != WindowState.Minimized)
        {
            _miniMode?.Hide();
        }
    }

    private void TriggerGlobalScreenSnip()
    {
        if (DataContext is not MainViewModel vm) return;
        if (_shutdownPending || _authoringWindow is not null || vm.IsAnythingRunning
            || InputModeGate.IsResourceCapture || !vm.EditorVM.CanEditMacro) return;
        var selectionStamp = vm.EditorVM.CaptureAuthoringSelection();
        ImageTemplateSelection? selection;
        try
        {
            // The application owner returns encoded pixels only after the actual
            // shared capture, frame disposal and privacy cleanup have completed.
            selection = vm.EditorVM.PromptOwnedScreenSnipTemplate();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to capture screen: {ex.Message}", "Screen Snip Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (selection?.TemplateBytes is not { Length: > 0 } bytes)
            return;
        if (_shutdownPending || vm.IsAnythingRunning || !vm.EditorVM.CanEditMacro
            || !vm.EditorVM.IsCurrentAuthoringSelection(selectionStamp)) return;

        try
        {
            var condition = new ImageCondition(bytes, minConfidence: selection.Confidence, searchRegion: selection.SourceRegion);
            var step = new WaitUntilStep(condition)
            {
                Timeout = TimeSpan.FromSeconds(15),
                ThrowOnTimeout = true
            };
            if (vm.EditorVM.TryInsertStepAfterSelection(step))
                vm.EditorVM.LogActivity("Captured an embedded image template using F10.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save screen snip: {ex.Message}", "Screen Snip Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_authoringWindow is not null)
        {
            e.Cancel = true;
            _closeAfterAuthoring = true;
            _authoringWindow.Close();
            return;
        }
        _transportDispatcher.Invalidate();
        if (!_shutdownComplete && DataContext is MainViewModel vm)
        {
            e.Cancel = true;
            if (_shutdownPending) return;
            _shutdownPending = true;
            CancelPendingRestoreFocus();
            IsEnabled = false;
            try
            {
                Task inputShutdown = vm.ShutdownInputOperationsAsync(); // Close admission before reentrant presentation callbacks.
                vm.TriggerPanic();
                // Keep the dispatcher alive until all owners have released the shared
                // capture/input services. Their final continuations may run on this UI.
                await Task.WhenAll(inputShutdown, vm.SettingsVM.Appearance.ShutdownAsync(), _restoreTask);
                vm.SettingsVM.DetachRuntimeSettings();
                vm.PropertyChanged -= OnAppearanceExecutionChanged;
                _shutdownComplete = true;
                await Dispatcher.InvokeAsync(new Action(Close));
            }
            catch (Exception ex)
            {
                _shutdownPending = false;
                IsEnabled = true;
                vm.StatusMessage = $"Could not finish closing: {ex.Message}. Try closing again.";
            }
            return;
        }
        _miniMode?.Close();
        _emergencyHotkeyListener?.Dispose();
        _hotkeyService?.Dispose();
        if (DataContext is MainViewModel iconVm)
        {
            iconVm.SettingsVM.Appearance.Changed -= RefreshThemeIcons;
            iconVm.SettingsVM.PropertyChanged -= OnIconSettingsChanged;
        }
        _themeIcons?.Dispose();
        _trayService?.Dispose();
    }
}
