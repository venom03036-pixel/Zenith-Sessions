using System.Diagnostics;
using System.ComponentModel;
using MacroSuite.App.Common;
using MacroSuite.App.Themes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using MacroSuite.App.ViewModels;
using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.App.Overlays;

/// <summary>
/// Small floating transport strip. Native window options are opt-in and reversible;
/// the independent tray menu remains available when the strip passes clicks through.
/// </summary>
public partial class MiniModeOverlay : Window
{
    private MainViewModel? _mainVm;
    private Window? _mainWindow;
    private MiniModeViewModel? _miniVm;
    private OverlayWindowEffects? _windowEffects;
    private bool _trayRecoveryAvailable;

    public MiniModeOverlay()
    {
        InitializeComponent();
        InitializeDragInput();
        PositionCenter();
        // The panel grows when the live feed appears; keep it centered unless the user dragged it.
        SizeChanged += OnSizeChanged;
        Closed += OnClosed;
        DpiChanged += OnDpiChanged;
        SourceInitialized += OnOverlaySourceInitialized;
        Closing += OnOverlayClosing;
    }

    public void Attach(MainViewModel vm, Window mainWindow)
    {
        if (_mainVm is not null) _mainVm.SettingsVM.PropertyChanged -= OnOverlaySettingsChanged;
        if (_miniVm is not null) _miniVm.PropertyChanged -= OnGestureStateChanged;
        _miniVm?.Dispose();
        _mainVm = vm;
        _mainWindow = mainWindow;
        _miniVm = new MiniModeViewModel(vm, mainWindow, () => Hide());
        DataContext = _miniVm;
        _miniVm.PropertyChanged += OnGestureStateChanged;
        vm.SettingsVM.PropertyChanged += OnOverlaySettingsChanged;
        FitToWorkArea();
        ApplyWindowOptions();
    }

    public bool IsClickThrough => _windowEffects?.ClickThroughApplied ?? false;
    public bool IsHiddenFromCapture => _windowEffects?.CaptureExclusionApplied ?? false;

    public void SetTrayRecoveryAvailable(bool available)
    {
        _trayRecoveryAvailable = available;
        if (!available && _mainVm?.SettingsVM.MiniToolbarClickThrough == true)
            _mainVm.StatusMessage = "The system tray is unavailable. Mini toolbar click-through is paused so its buttons remain usable.";
        ApplyWindowOptions();
    }

    private void OnOverlaySourceInitialized(object? sender, EventArgs e) => ApplyWindowOptions();
    private void OnOverlayClosing(object? sender, CancelEventArgs e) => _windowEffects?.Dispose();
    private void OnOverlaySettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SettingsViewModel.MiniToolbarClickThrough) or nameof(SettingsViewModel.MiniToolbarHideFromCapture))) return;
        if (Dispatcher.CheckAccess()) ApplyWindowOptions();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(ApplyWindowOptions);
    }

    private void ApplyWindowOptions()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (_mainVm is null || handle == 0) return;
        try
        {
            _windowEffects ??= new OverlayWindowEffects(handle);
            var settings = _mainVm.SettingsVM;
            bool clickThrough = settings.MiniToolbarClickThrough && _trayRecoveryAvailable;
            if (IsClickThrough != clickThrough) { InvalidatePointerGesture(); _windowEffects.SetClickThrough(clickThrough); }
            if (IsHiddenFromCapture != settings.MiniToolbarHideFromCapture &&
                !_windowEffects.TrySetCaptureExcluded(settings.MiniToolbarHideFromCapture, out string? error))
            {
                _mainVm.StatusMessage = error ?? "The mini toolbar screenshot setting could not be applied.";
                Trace.TraceWarning(_mainVm.StatusMessage);
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _mainVm.StatusMessage = "Mini toolbar options could not be applied: " + ex.Message;
            Trace.TraceWarning(_mainVm.StatusMessage);
        }
    }

    // ── Positioning ───────────────────────────────────────────────────────────

    private bool _userMoved;
    private Rect _placementWorkArea = SystemParameters.WorkArea;
    private System.Drawing.Rectangle? _physicalWorkArea;
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => PositionCenter();
    private void OnToolbarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep the same four transport controls at narrow widths; only their branding
        // moves to a centered row so the word labels and Menu never overlap them.
        if (_miniVm is not null) _miniVm.ControlColumns = e.NewSize.Width < 420 ? 2 : 4;
    }
    private void OnClosed(object? sender, EventArgs e)
    {
        SizeChanged -= OnSizeChanged; Closed -= OnClosed; DpiChanged -= OnDpiChanged;
        SourceInitialized -= OnOverlaySourceInitialized; Closing -= OnOverlayClosing;
        if (_mainVm is not null) _mainVm.SettingsVM.PropertyChanged -= OnOverlaySettingsChanged;
        if (_miniVm is not null) _miniVm.PropertyChanged -= OnGestureStateChanged;
        _miniVm?.Dispose();
    }
    private void OnDpiChanged(object sender, DpiChangedEventArgs e) { FitToWorkArea(); PositionCenter(); }
    private void FitToWorkArea()
    {
        try
        {
            var desktop = Win32VirtualDesktopGeometry.Query(); var bounds = desktop.GetCurrentDisplayBounds();
            var monitor = desktop.Monitors.First(m => m.Bounds == bounds);
            _physicalWorkArea = monitor.WorkArea;
            _placementWorkArea = new Rect(monitor.WorkArea.X / monitor.DpiScaleX, monitor.WorkArea.Y / monitor.DpiScaleY,
                monitor.WorkArea.Width / monitor.DpiScaleX, monitor.WorkArea.Height / monitor.DpiScaleY);
        }
        catch (Exception ex) { Trace.TraceWarning($"Mini toolbar display placement used the primary work area: {ex.Message}"); _placementWorkArea = SystemParameters.WorkArea; }
        Width = Math.Min(480, Math.Max(280, _placementWorkArea.Width - 24));
        if (_miniVm is not null) _miniVm.ControlColumns = Width < 420 ? 2 : 4;
    }

    private void PositionCenter()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (_userMoved) { Win32WindowPlacement.ClampToNearestWorkArea(handle); return; }
        if (handle != 0 && _physicalWorkArea is { } physical) { Win32WindowPlacement.CenterInPhysicalWorkArea(handle, physical); return; }
        var screen = _placementWorkArea;
        double width = ActualWidth > 0 ? ActualWidth : Width;
        double height = ActualHeight > 0 ? ActualHeight : 70;
        if (width <= 0) width = 360;
        Left = screen.Left + (screen.Width - width) / 2;
        Top = screen.Top + (screen.Height - height) / 2;
    }

    // ── Drag & Double-Click ───────────────────────────────────────────────────

    private void OnPillMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _miniVm?.RestoreMainWindow();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _userMoved = true;
            DragMove();
        }
    }

    // ── 6-Button Toolbar Handlers ─────────────────────────────────────────────

    private void MoreBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ContextMenu: { } menu } button) { menu.PlacementTarget = button; menu.IsOpen = true; }
    }

    private void OpenBtn_Click(object sender, RoutedEventArgs e)
    {
        _miniVm?.Open();
    }

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        _miniVm?.Save();
    }

    private void RecBtn_Click(object sender, RoutedEventArgs e)
    {
        _miniVm?.ToggleRec();
    }

    private void PauseBtn_Click(object sender, RoutedEventArgs e)
    {
        _miniVm?.TogglePause();
    }

    private void PlayBtn_Click(object sender, RoutedEventArgs e)
    {
        _miniVm?.TogglePlay();
    }

    private void ExportBtn_Click(object sender, RoutedEventArgs e)
    {
        _miniVm?.ExportExe();
    }

    private void PrefsBtn_Click(object sender, RoutedEventArgs e)
    {
        _miniVm?.Prefs();
    }

    private void RestoreBtn_Click(object sender, RoutedEventArgs e)
    {
        _miniVm?.RestoreMainWindow();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        if (_mainWindow != null)
        {
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }
    }

    // ── Fade-in / Fade-out ───────────────────────────────────────────────────

    public new void Show()
    {
        FitToWorkArea();
        PositionCenter();
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        base.Show();
        PositionCenter();
        ApplyWindowOptions();
        _miniVm?.RefreshState();
    }

    public new void Hide()
    {
        InvalidatePointerGesture();
        BeginAnimation(OpacityProperty, null);
        base.Hide();
    }
}

/// <summary>
/// View model governing the compact 6-button toolbar state, bindings, and actions.
/// </summary>
public sealed class MiniModeViewModel : Common.ObservableObject, IDisposable
{
    private readonly MainViewModel _vm;
    private readonly Window _mainWindow;
    private readonly Action _hideAction;

    private static readonly RadialGradientBrush IdleRecBrush;
    private static readonly RadialGradientBrush ActiveRecBrush;
    private static readonly SolidColorBrush MintBrush;
    private static readonly SolidColorBrush RoseBrush;
    private static readonly SolidColorBrush RecRedBrush;
    private static readonly SolidColorBrush SkyBrush;
    private static readonly SolidColorBrush AmberBrush;
    private static readonly SolidColorBrush SlateBrush;
    private static readonly SolidColorBrush StatusRecBrush;
    private static readonly SolidColorBrush AccentRecBrush;
    private static readonly SolidColorBrush AccentRunBrush;
    private static readonly SolidColorBrush AccentIdleBrush;


    private int _refreshPosted;
    private int _liveFeedPosted;

    private long _lastLiveFeedTicks;




    static MiniModeViewModel()
    {
        IdleRecBrush = new RadialGradientBrush(
            Color.FromRgb(0x60, 0xA5, 0xFA),
            Color.FromRgb(0x1D, 0x4E, 0xD8))
        {
            Center = new Point(0.35, 0.35),
            GradientOrigin = new Point(0.3, 0.3),
            RadiusX = 0.6,
            RadiusY = 0.6
        };
        IdleRecBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x93, 0xC5, 0xFD), 0));
        IdleRecBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x25, 0x63, 0xEB), 0.5));
        IdleRecBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0x1D, 0x4E, 0xD8), 1));
        IdleRecBrush.Freeze();

        ActiveRecBrush = new RadialGradientBrush(
            Color.FromRgb(0xFC, 0xA5, 0xA5),
            Color.FromRgb(0xB9, 0x1C, 0x1C))
        {
            Center = new Point(0.35, 0.35),
            GradientOrigin = new Point(0.3, 0.3),
            RadiusX = 0.6,
            RadiusY = 0.6
        };
        ActiveRecBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFE, 0xCA, 0xCA), 0));
        ActiveRecBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0xEF, 0x44, 0x44), 0.5));
        ActiveRecBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0xB9, 0x1C, 0x1C), 1));
        ActiveRecBrush.Freeze();

        MintBrush = FreezeRgb(0x00, 0xF5, 0xA0);
        RoseBrush = FreezeRgb(0xF4, 0x3F, 0x5E);
        RecRedBrush = FreezeRgb(0xEF, 0x44, 0x44);
        SkyBrush = FreezeRgb(0x38, 0xBD, 0xF8);
        AmberBrush = FreezeRgb(0xFB, 0xBF, 0x24);
        SlateBrush = FreezeRgb(0x94, 0xA3, 0xB8);
        StatusRecBrush = FreezeRgb(0xF8, 0x71, 0x71);
        AccentRecBrush = FreezeRgb(0x7F, 0x1D, 0x1D);
        AccentRunBrush = FreezeRgb(0x0F, 0x76, 0x6E);
        AccentIdleBrush = FreezeRgb(0x2A, 0x35, 0x48);
    }

    private static SolidColorBrush FreezeRgb(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private const int LiveFeedRows = 9;

    public MiniModeViewModel(MainViewModel vm, Window mainWindow, Action hideAction)
    {
        _vm = vm;
        _mainWindow = mainWindow;
        _hideAction = hideAction;

        ImportCommand = new RelayCommand(Open, () => _vm.EditorVM.CanImportMacro && _vm.EditorVM.LoadMacroCommand.CanExecute(null));
        SaveCommand = new RelayCommand(Save, () => _vm.EditorVM.SaveMacroCommand.CanExecute(null) && !Busy && _vm.CanUseWorkspace);
        ExportCommand = new RelayCommand(ExportExe, () => _vm.EditorVM.ExportMacroCommand.CanExecute(null) && !Busy && _vm.CanUseWorkspace);
        SettingsCommand = new RelayCommand(Prefs, () => _vm.CanUseWorkspace);
        RestoreCommand = new RelayCommand(RestoreMainWindow);
        RunCommand = new RelayCommand(TogglePlay, () => IsRunning || (_vm.CanUseWorkspace && _vm.EditorVM.RunCommand.CanExecute(null)));
        RecordCommand = new RelayCommand(ToggleRec, () => !_vm.EditorVM.IsRecordingFinishing && (IsRecording || (_vm.CanUseWorkspace && _vm.EditorVM.RecordCommand.CanExecute(null))));
        PauseCommand = new RelayCommand(TogglePause, () => _vm.AutoClickerVM.IsRunning || _vm.EditorVM.PauseCommand.CanExecute(null));
        _vm.EditorVM.PropertyChanged += OnEditorChanged;
        _vm.AutoClickerVM.PropertyChanged += OnAutoClickerChanged;
        _vm.SettingsVM.PropertyChanged += OnSettingsChanged;
        _vm.EditorVM.RecordingBatchApplied += RefreshLiveFeed;
        _vm.PropertyChanged += OnMainChanged;
    }

    private bool _disposed;
    private int _controlColumns = 4;
    private bool Busy => _vm.EditorVM.IsRunning || _vm.EditorVM.IsRecordingBusy || _vm.EditorVM.IsDetectionTesting;
    public int ControlColumns { get => _controlColumns; set { if (SetProperty(ref _controlColumns, value)) { OnPropertyChanged(nameof(HeaderCommandsVisibility)); OnPropertyChanged(nameof(CompactCommandsVisibility)); OnPropertyChanged(nameof(ControlMinimumHeight)); } } }
    public Visibility HeaderCommandsVisibility => ControlColumns == 4 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CompactCommandsVisibility => ControlColumns == 2 ? Visibility.Visible : Visibility.Collapsed;
    public double ControlMinimumHeight => ControlColumns == 4 ? 76 : 64;
    public LogoColorMode LogoColorMode => _vm.SettingsVM.LogoColorMode;
    public string RecordHotkey => IsRecording ? _vm.SettingsVM.StopHotkey : _vm.SettingsVM.RecordHotkey;
    public string RunHotkey => _vm.AutoClickerVM.IsRunning ? _vm.AutoClickerVM.StartHotkey : IsRunning ? _vm.SettingsVM.StopHotkey : _vm.SettingsVM.PlayHotkey;
    public string PauseHotkey => _vm.SettingsVM.PauseHotkey;
    public string PanicHotkey => _vm.SettingsVM.PanicHotkey;
    public string RunTooltip => $"{PlayText} ({RunHotkey})";
    public string RecordTooltip => $"{RecText} ({RecordHotkey})";
    public string CompactStatusLabel => _vm.ExecutionState == "PANIC TERMINATED" ? (_vm.EditorVM.IsRecordingFinishing ? "Emergency · Finishing" : "Emergency stopped")
        : _vm.EditorVM.IsDetectionTesting ? "Testing"
        : _vm.EditorVM.IsRecordingBusy ? _vm.EditorVM.RecordingStateText
        : IsPaused ? "Paused"
        : IsRunning ? "Running"
        : _vm.AutoClickerVM.IsStopping ? "Stopping"
        : _vm.AutoClickerVM.IsStarting ? "Starting"
        : _vm.EditorVM.HasExecutionError ? "Stopped"
        : !_vm.CanUseWorkspace ? "Activation required"
        : string.IsNullOrWhiteSpace(_vm.EditorVM.RecordingStateText) || _vm.EditorVM.RecordingStateText == "Idle" ? "Ready" : _vm.EditorVM.RecordingStateText;
    public string ActiveMacroText
    {
        get
        {
            if (_vm.AutoClickerVM.IsRunning) return $"Active: Auto clicker ({_vm.AutoClickerVM.StartHotkey})";
            string name = _vm.EditorVM.IsRunning ? _vm.EditorVM.RunningMacroName : _vm.EditorVM.MacroName;
            string hotkey = _vm.EditorVM.IsRunning ? _vm.EditorVM.RunningMacroHotkey : _vm.EditorVM.CurrentHotkey;
            return string.IsNullOrWhiteSpace(name) ? "No macro selected" : $"Active: {name}" + (string.IsNullOrWhiteSpace(hotkey) ? "" : $" ({hotkey})");
        }
    }
    public RelayCommand ImportCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public RelayCommand RestoreCommand { get; }
    public RelayCommand RunCommand { get; }
    public RelayCommand RecordCommand { get; }
    public RelayCommand PauseCommand { get; }
    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MacroEditorViewModel.IsRunning) or nameof(MacroEditorViewModel.IsPaused) or nameof(MacroEditorViewModel.IsRecordingBusy) or nameof(MacroEditorViewModel.IsRecording) or nameof(MacroEditorViewModel.RunningMacroName) or nameof(MacroEditorViewModel.RunningMacroHotkey) or nameof(MacroEditorViewModel.MacroName) or nameof(MacroEditorViewModel.CurrentHotkey) or nameof(MacroEditorViewModel.IsDetectionTesting) or nameof(MacroEditorViewModel.CanImportMacro) or nameof(MacroEditorViewModel.HasExecutionError) or nameof(MacroEditorViewModel.RecordingStateText) or nameof(MacroEditorViewModel.IsRecordingPaused) or nameof(MacroEditorViewModel.IsRecordingFinishing)) RequestRefresh();
    }
    private void OnAutoClickerChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is nameof(AutoClickerViewModel.IsRunning) or nameof(AutoClickerViewModel.StartHotkey) or nameof(AutoClickerViewModel.IsStarting) or nameof(AutoClickerViewModel.IsStopping) or nameof(AutoClickerViewModel.IsPaused)) RequestRefresh(); }
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => RequestRefresh();
    private string _lastCompactStatus = string.Empty;
    private void OnMainChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(MainViewModel.CanUseWorkspace) || (e.PropertyName == nameof(MainViewModel.ExecutionState) && CompactStatusLabel != _lastCompactStatus)) RequestRefresh(); }
    private void RequestRefresh()
    {
        if (_disposed) return;
        var dispatcher = _mainWindow.Dispatcher;
        if (dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _refreshPosted, 1) != 0) return;
        dispatcher.BeginInvoke(() => { Interlocked.Exchange(ref _refreshPosted, 0); RefreshState(true); });
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _vm.EditorVM.PropertyChanged -= OnEditorChanged;
        _vm.AutoClickerVM.PropertyChanged -= OnAutoClickerChanged;
        _vm.SettingsVM.PropertyChanged -= OnSettingsChanged;
        _vm.EditorVM.RecordingBatchApplied -= RefreshLiveFeed;
        _vm.PropertyChanged -= OnMainChanged;
        RecentActions.Clear();
    }

    /// <summary>Newest recorded actions, so the user can verify the capture without the studio window.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> RecentActions { get; } = new();

    public Visibility LiveFeedVisibility => IsRecording ? Visibility.Visible : Visibility.Collapsed;

    public string LiveHeaderText => $"RECORDING — {_vm.EditorVM.Steps.Count} actions";

    public void RefreshState() => RefreshState(false);
    private void RefreshState(bool force)
    {
        if (_disposed) return;
        // Recording telemetry can arrive off the UI thread; touching a bound collection or
        // raising PropertyChanged from there desynchronizes the item generator.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            if (Interlocked.Exchange(ref _refreshPosted, 1) != 0)
                return;
            dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _refreshPosted, 0);
                RefreshState(true);
            });
            return;
        }

        _lastCompactStatus = CompactStatusLabel;
        OnPropertyChanged(nameof(InteractionState));

        OnPropertyChanged(nameof(LogoColorMode)); OnPropertyChanged(nameof(RecordHotkey)); OnPropertyChanged(nameof(RunHotkey));
        OnPropertyChanged(nameof(PanicButtonText));
        OnPropertyChanged(nameof(PauseHotkey)); OnPropertyChanged(nameof(PanicHotkey)); OnPropertyChanged(nameof(RunTooltip));
        OnPropertyChanged(nameof(RecordTooltip)); OnPropertyChanged(nameof(CompactStatusLabel)); OnPropertyChanged(nameof(ActiveMacroText));
        RunCommand.RaiseCanExecuteChanged(); RecordCommand.RaiseCanExecuteChanged(); PauseCommand.RaiseCanExecuteChanged();
        ImportCommand.RaiseCanExecuteChanged(); SaveCommand.RaiseCanExecuteChanged(); ExportCommand.RaiseCanExecuteChanged(); SettingsCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(RecText));
        OnPropertyChanged(nameof(PlayText));
        OnPropertyChanged(nameof(PlaySymbol)); OnPropertyChanged(nameof(PauseSymbol));
        OnPropertyChanged(nameof(PauseText));
        OnPropertyChanged(nameof(PauseTooltip));
        OnPropertyChanged(nameof(PauseIconData));
        OnPropertyChanged(nameof(RecTextColor));
        OnPropertyChanged(nameof(PlayTextColor));
        OnPropertyChanged(nameof(PauseTextColor));
        OnPropertyChanged(nameof(RecOrbBrush));
        OnPropertyChanged(nameof(PlayArrowsBrush));
        OnPropertyChanged(nameof(PauseIconBrush));
        OnPropertyChanged(nameof(RecShadowColor));
        OnPropertyChanged(nameof(PlayShadowColor));
        OnPropertyChanged(nameof(MacroName));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(AccentBorderBrush));
        OnPropertyChanged(nameof(LiveFeedVisibility));
        OnPropertyChanged(nameof(LiveHeaderText));
        if (IsRecording) RefreshLiveFeed();
        else RecentActions.Clear();
    }

    private void RefreshLiveFeed()
    {
        if (_disposed) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            if (Interlocked.Exchange(ref _liveFeedPosted, 1) != 0)
                return;
            dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _liveFeedPosted, 0);
                RefreshLiveFeed();
            });
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (now - _lastLiveFeedTicks < Stopwatch.Frequency / 10)
            return;
        _lastLiveFeedTicks = now;

        var rows = _vm.EditorVM.GetRecentActionSummaries(LiveFeedRows);

        // Rewrite in place: this list is tiny and rebuilding it avoids index bookkeeping.
        while (RecentActions.Count > rows.Count)
            RecentActions.RemoveAt(RecentActions.Count - 1);
        for (int i = 0; i < rows.Count; i++)
        {
            if (i < RecentActions.Count) RecentActions[i] = rows[i];
            else RecentActions.Add(rows[i]);
        }

        OnPropertyChanged(nameof(LiveHeaderText));
    }

    public bool IsRecording => _vm.EditorVM.IsRecording;
    public string InteractionState => $"{CompactStatusLabel}|{IsRecording}|{IsRunning}|{IsPaused}|{RecordHotkey}|{RunHotkey}|{PauseHotkey}|{PanicHotkey}";
    public bool IsRunning => _vm.EditorVM.IsRunning || _vm.AutoClickerVM.IsRunning;
    public bool IsPaused => _vm.EditorVM.IsPaused || _vm.EditorVM.IsRecordingPaused || _vm.AutoClickerVM.IsPaused;

    public string RecText => _vm.EditorVM.IsRecordingFinishing ? "Finishing recording" : IsRecording ? "Stop recording" : "Record";
    public string PlayText => IsRunning ? "Stop" : "Run";
    public string PlaySymbol => IsRunning ? "■" : "▶";
    public string PauseSymbol => IsPaused ? "▶" : "Ⅱ";
    public string PauseText => IsPaused ? "Resume" : "Pause";
    public string PauseTooltip =>
        IsPaused
            ? $"Resume ({_vm.EditorVM.PauseHotkey})"
            : $"Pause or resume ({_vm.EditorVM.PauseHotkey})";
    public string PauseIconData => IsPaused
        ? "M 8,5 L 19,12 L 8,19 Z"
        : "M 7,5 L 10,5 L 10,19 L 7,19 Z M 14,5 L 17,5 L 17,19 L 14,19 Z";

    public Brush RecOrbBrush => IsRecording ? ActiveRecBrush : IdleRecBrush;

    public Brush PlayArrowsBrush => IsRunning ? MintBrush : RoseBrush;

    public Brush RecTextColor => IsRecording ? RecRedBrush : SkyBrush;

    public Brush PlayTextColor => IsRunning ? MintBrush : RoseBrush;

    public Brush PauseTextColor => IsPaused
        ? MintBrush
        : IsRunning
            ? AmberBrush
            : SlateBrush;

    public Brush PauseIconBrush => PauseTextColor;

    public Color RecShadowColor => IsRecording
        ? Color.FromRgb(0xEF, 0x44, 0x44)
        : Color.FromRgb(0x38, 0xBD, 0xF8);

    public Color PlayShadowColor => IsRunning
        ? Color.FromRgb(0x00, 0xF5, 0xA0)
        : Color.FromRgb(0xF4, 0x3F, 0x5E);

    public string MacroName => _vm.EditorVM.IsRunning ? _vm.EditorVM.RunningMacroName : _vm.EditorVM.MacroName;

    public string StatusLabel => _vm.TopStatusText;
    public string PanicButtonText => _vm.PanicButtonText;
    public System.Windows.Input.ICommand PanicCommand => _vm.PanicCommand;

    public Brush StatusBrush => IsRecording
        ? StatusRecBrush
        : IsPaused
            ? AmberBrush
            : IsRunning
                ? MintBrush
                : SlateBrush;

    public Brush AccentBorderBrush => IsRecording
        ? AccentRecBrush
        : IsRunning
            ? AccentRunBrush
            : AccentIdleBrush;

    // ── Command Actions ───────────────────────────────────────────────────────

    public void Open()
    {
        if (_vm.EditorVM.CanImportMacro && _vm.EditorVM.LoadMacroCommand.CanExecute(null))
            _vm.EditorVM.LoadMacroCommand.Execute(null);
    }

    public void Save()
    {
        if (_vm.CanUseWorkspace && !Busy && _vm.EditorVM.SaveMacroCommand.CanExecute(null))
            _vm.EditorVM.SaveMacroCommand.Execute(null);
    }

    public void ToggleRec()
    {
        if (_vm.EditorVM.IsRecordingBusy)
        {
            if (_vm.EditorVM.StopCommand.CanExecute(null))
                _vm.EditorVM.StopCommand.Execute(null);
            return;
        }

        if (_vm.EditorVM.RecordCommand.CanExecute(null))
            _vm.EditorVM.RecordCommand.Execute(null);
    }

    public void TogglePause()
    {
        if (_mainWindow is MainWindow studio) studio.ToggleTransportPause();
    }

    public void TogglePlay()
    {
        if (_vm.EditorVM.IsRunning)
        {
            if (_vm.EditorVM.StopCommand.CanExecute(null))
                _vm.EditorVM.StopCommand.Execute(null);
        }
        else if (_vm.AutoClickerVM.IsRunning)
        {
            _vm.AutoClickerVM.StopClicker();
        }
        else
        {
            if (_vm.CanUseWorkspace && _vm.EditorVM.RunCommand.CanExecute(null))
                _vm.EditorVM.RunCommand.Execute(null);
        }
    }

    public void ExportExe()
    {
        if (_vm.CanUseWorkspace && !Busy && _vm.EditorVM.ExportMacroCommand.CanExecute(null))
            _vm.EditorVM.ExportMacroCommand.Execute(null);
    }

    public void Prefs()
    {
        if (!_vm.CanUseWorkspace) return;
        _vm.CurrentViewModel = _vm.SettingsVM;
        RestoreMainWindow();
    }

    public void RestoreMainWindow()
    {
        if (_mainWindow is MainWindow studio) { studio.RestoreFromMiniMode(); return; }
        _hideAction();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Show();
        _mainWindow.Activate();
    }
}
