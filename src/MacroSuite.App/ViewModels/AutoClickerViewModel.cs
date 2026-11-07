using System.Collections.ObjectModel;
using System.Diagnostics;
using MacroSuite.App.Common;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Licensing;
using System.IO;
using System.Text.Json;

namespace MacroSuite.App.ViewModels;

/// <summary>
/// ViewModel for the built-in Auto Clicker.
/// All click simulation goes through IInputPlayer (Rule 4 — single input pipeline).
/// All loops carry CancellationToken (Rule 12 — explicit cancellation).
/// </summary>
public sealed partial class AutoClickerViewModel : ObservableObject, IDisposable
{
    private readonly IInputPlayer _inputPlayer;
    public HostInputOperationArbiter InputOperations { get; }
    private sealed class InputOwner(HostInputOperationLease lease)
    {
        public HostInputOperationLease Lease { get; } = lease;
        public object Transitions { get; } = new();
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(lease.StopToken);
        public Task Pause { get; set; } = Task.CompletedTask;
        public Task Run { get; set; } = Task.CompletedTask;
        public Task Resume { get; set; } = Task.CompletedTask;
        public long PauseGeneration { get; set; }
        public bool CleanupFailed { get; set; }
        public bool InputStarted { get; set; }
        public bool Finalizing { get; set; }
        public IDisposable? Attachment { get; set; }
    }
    private InputOwner? _runOwner;
    private InputOwner? _pickerOwner;
    private InputOwner? _failedInputOwner;
    private readonly ILocationPicker? _locationPicker;
    private bool _isPickingLocation;
    private readonly AppDataPathResolver? _preferencesPaths;
    private readonly IFeatureGate? _featureGate;
    private readonly IActivationService? _activationService;
    private bool _isStarting;
    public bool IsStarting
    {
        get => _isStarting;
        private set
        {
            if (!SetProperty(ref _isStarting, value)) return;
            OnPropertyChanged(nameof(StartStopButtonText)); OnPropertyChanged(nameof(CanEditConfiguration));
            ToggleRunCommand.RaiseCanExecuteChanged(); RunningStateChanged?.Invoke();
        }
    }
    public bool CanStart => _activationService?.IsActive ?? true;
    private void OnActivationStateChanged(object? sender, EventArgs e) { OnPropertyChanged(nameof(CanStart)); ToggleRunCommand.RaiseCanExecuteChanged(); }
    private bool _preferencesReady;
    private string _preferencesStatus = string.Empty;
    public string PreferencesStatus { get => _preferencesStatus; private set => SetProperty(ref _preferencesStatus, value); }

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        if (_preferencesReady && propertyName is nameof(ClicksPerSecond) or nameof(RandomOffsetEnabled) or nameof(RandomOffsetCps)
            or nameof(SelectedMouseButton) or nameof(SelectedClickType) or nameof(UseCurrentPosition) or nameof(FixedX) or nameof(FixedY)
            or nameof(RepeatCount) or nameof(RepeatUntilStopped)) SavePreferences();
    }

    public bool SavePreferences()
    {
        if (_preferencesPaths is null) return true;
        try
        {
            string json = JsonSerializer.Serialize(new { version = 1, clicksPerSecond = ClicksPerSecond, mouseButton = SelectedMouseButton,
                clickType = SelectedClickType, useCurrentPosition = UseCurrentPosition, fixedX = FixedX, fixedY = FixedY,
                repeatCount = RepeatCount, repeatMode = RepeatUntilStopped ? "UntilStopped" : RepeatOnce ? "Once" : "Count",
                varyTiming = RandomOffsetEnabled, variationCps = RandomOffsetCps }, new JsonSerializerOptions { WriteIndented = true });
            SettingsJsonStore.SaveAtomic(Path.Combine(_preferencesPaths.ConfigDirectory, "autoclicker.json"), json);
            PreferencesStatus = string.Empty; return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { PreferencesStatus = $"Could not save Auto Clicker settings: {ex.Message}"; return false; }
    }

    private void LoadPreferences()
    {
        if (_preferencesPaths is null) return;
        var loaded = SettingsJsonStore.Load(Path.Combine(_preferencesPaths.ConfigDirectory, "autoclicker.json"));
        if (loaded.Document is null) { PreferencesStatus = loaded.Kind == SettingsLoadKind.Missing ? string.Empty : loaded.Message ?? string.Empty; return; }
        PreferencesStatus = loaded.Message ?? string.Empty;
        using var document = loaded.Document;
        try
        {
            var data = document.RootElement;
            if (data.TryGetProperty("version", out var version) && version.GetInt32() != 1) throw new InvalidDataException("This Auto Clicker preferences version is not supported.");
            if (data.TryGetProperty("clicksPerSecond", out var cps) && cps.TryGetDouble(out double rate) && double.IsFinite(rate)) _clicksPerSecond = Math.Clamp(rate, .1, 500);
            if (data.TryGetProperty("mouseButton", out var button) && MouseButtons.Contains(button.GetString())) _selectedMouseButton = button.GetString()!;
            if (data.TryGetProperty("clickType", out var click) && ClickTypes.Contains(click.GetString())) _selectedClickType = click.GetString()!;
            if (data.TryGetProperty("useCurrentPosition", out var current)) _useCurrentPosition = current.GetBoolean();
            if (data.TryGetProperty("fixedX", out var x) && x.TryGetInt32(out int px)) _fixedX = px;
            if (data.TryGetProperty("fixedY", out var y) && y.TryGetInt32(out int py)) _fixedY = py;
            if (data.TryGetProperty("repeatCount", out var count) && count.TryGetInt32(out int repeats)) _repeatCount = Math.Max(1, repeats);
            if (data.TryGetProperty("repeatMode", out var mode)) { string? text = mode.GetString(); _repeatUntilStopped = text is not ("Once" or "Count"); _repeatOnce = text == "Once"; }
            if (data.TryGetProperty("varyTiming", out var vary)) _randomOffsetEnabled = vary.GetBoolean();
            if (data.TryGetProperty("variationCps", out var variation) && variation.TryGetDouble(out double delta) && double.IsFinite(delta)) _randomOffsetCps = Math.Clamp(delta, 0, 50);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or FormatException) { PreferencesStatus = $"Some Auto Clicker settings could not be loaded: {ex.Message}"; }
    }

    // ── Click Speed ──────────────────────────────────────────────────────────
    private double _clicksPerSecond = 10.0;
    private bool _randomOffsetEnabled;
    private double _randomOffsetCps = 2.0;

    // ── Click Options ─────────────────────────────────────────────────────────
    private string _selectedMouseButton = "Left";
    private string _selectedClickType = "Single";

    // ── Cursor Position ───────────────────────────────────────────────────────
    private bool _useCurrentPosition = true;
    private int _fixedX;
    private int _fixedY;

    // ── Repeat ────────────────────────────────────────────────────────────────
    private int _repeatCount = 1;
    private bool _repeatUntilStopped = true;
    private bool _repeatOnce;
    private bool _isMacroActive;
    private bool _isCapturingHotkey;

    // ── Runtime State ─────────────────────────────────────────────────────────
    private bool _isRunning;
    private bool _isStopping;
    private Task _runTask = Task.CompletedTask;
    private Task _pickTask = Task.CompletedTask;
    private Task _testTask = Task.CompletedTask;
    private Task? _shutdownTask;
    private bool _disposed;
    private long _totalClicksCompleted;
    private double _liveCpsRunning;

    // ── Click Tester ──────────────────────────────────────────────────────────
    private bool _isTestRunning;
    private double _liveCpsTester;
    private long _totalTestClicks;
    private string _testElapsedText = "0:00";
    private bool _testPanelExpanded;
    private readonly List<DateTime> _testClickTimestamps = new();
    private CancellationTokenSource? _testCts;
    private DateTime _testStartTime;

    // ── Hotkeys ───────────────────────────────────────────────────────────────
    private string _startHotkey = TransportHotkeyDefaults.AutoClicker;

    public static IReadOnlyList<string> MouseButtons { get; } = ["Left", "Right", "Middle"];
    public static IReadOnlyList<string> ClickTypes { get; } = ["Single", "Double"];

    public ObservableCollection<string> RunLog { get; } = new();

    /// <summary>Fired whenever IsRunning changes so MainWindow can show/hide mini-mode.</summary>
    public event Action? RunningStateChanged;

    /// <summary>Fired whenever StartHotkey changes so global bindings can be refreshed.</summary>
    public event Action? HotkeyChanged;
    public event Action? HotkeyCaptureRequested;

    // ── Properties ───────────────────────────────────────────────────────────

    public double ClicksPerSecond
    {
        get => _clicksPerSecond;
        set
        {
            if (!double.IsFinite(value)) return;
            if (SetProperty(ref _clicksPerSecond, Math.Clamp(value, 0.1, 500.0)))
            {
                OnPropertyChanged(nameof(IntervalPreviewText));
                OnPropertyChanged(nameof(SliderValue));
                OnPropertyChanged(nameof(CpsDisplayText));
                OnPropertyChanged(nameof(CpsInputText));
                OnPropertyChanged(nameof(RunSummary));
            }
        }
    }

    public string CpsInputText
    {
        get => $"{ClicksPerSecond:0.##}";
        set
        {
            if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed) ||
                double.TryParse(value, out parsed))
            {
                ClicksPerSecond = parsed;
            }
            OnPropertyChanged(nameof(CpsInputText));
        }
    }

    public double SliderValue
    {
        get => _clicksPerSecond;
        set => ClicksPerSecond = value;
    }

    public string CpsDisplayText => $"{ClicksPerSecond:F1}";

    public bool RandomOffsetEnabled
    {
        get => _randomOffsetEnabled;
        set => SetProperty(ref _randomOffsetEnabled, value);
    }

    public double RandomOffsetCps
    {
        get => _randomOffsetCps;
        set { if (double.IsFinite(value)) SetProperty(ref _randomOffsetCps, Math.Clamp(value, 0, 50.0)); }
    }

    public string SelectedMouseButton
    {
        get => _selectedMouseButton;
        set { if (SetProperty(ref _selectedMouseButton, value)) OnPropertyChanged(nameof(RunSummary)); }
    }

    public string SelectedClickType
    {
        get => _selectedClickType;
        set
        {
            if (!SetProperty(ref _selectedClickType, value)) return;
            OnPropertyChanged(nameof(RateUnit));
            OnPropertyChanged(nameof(IntervalPreviewText));
            OnPropertyChanged(nameof(RunSummary));
            OnPropertyChanged(nameof(CompletedCountText));
        }
    }

    public bool UseCurrentPosition
    {
        get => _useCurrentPosition;
        set
        {
            if (SetProperty(ref _useCurrentPosition, value))
            {
                OnPropertyChanged(nameof(UseFixedPosition));
                OnPropertyChanged(nameof(RunSummary));
                PickLocationCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool UseFixedPosition
    {
        get => !_useCurrentPosition;
        set => UseCurrentPosition = !value;
    }

    public int FixedX
    {
        get => _fixedX;
        set
        {
            if (!SetProperty(ref _fixedX, value)) return;
            OnPropertyChanged(nameof(RunSummary));
            OnPropertyChanged(nameof(FixedPositionMonitorText));
        }
    }

    public int FixedY
    {
        get => _fixedY;
        set
        {
            if (!SetProperty(ref _fixedY, value)) return;
            OnPropertyChanged(nameof(RunSummary));
            OnPropertyChanged(nameof(FixedPositionMonitorText));
        }
    }

    public bool IsPickingLocation
    {
        get => _isPickingLocation;
        private set
        {
            if (SetProperty(ref _isPickingLocation, value))
            {
                OnPropertyChanged(nameof(PickLocationButtonText));
                PickLocationCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string PickLocationButtonText
        => IsPickingLocation ? "Move mouse · Tab to confirm" : "Pick position";

    /// <summary>Optional UI overlay (coordinates under the cursor). Tests omit this.</summary>
    public Func<ILocationPicker, Task<ScreenPoint?>>? ShowPickOverlay { get; set; }
    public Func<ILocationPicker, HostInputOperationLease, CancellationToken, Task<ScreenPoint?>>? ShowOwnedPickOverlay { get; set; }

    public Func<IVirtualDesktopGeometry>? DisplayGeometryProvider { get; set; }
    public string FixedPositionMonitorText
    {
        get
        {
            if (DisplayGeometryProvider is null) return "Monitor information is unavailable.";
            try
            {
                foreach (var monitor in DisplayGeometryProvider().Monitors)
                {
                    if (!monitor.Bounds.Contains(FixedX, FixedY)) continue;
                    string location = monitor.IsPrimary ? "Primary display" : $"Display at X {monitor.Bounds.X}, Y {monitor.Bounds.Y}";
                    return $"{location} · {monitor.Bounds.Width} × {monitor.Bounds.Height} pixels";
                }
                return "This position is outside the connected displays.";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Auto Clicker monitor lookup failed: {ex.Message}");
                return "Monitor information could not be read. Check the position before starting.";
            }
        }
    }

    public int RepeatCount
    {
        get => _repeatCount;
        set { if (SetProperty(ref _repeatCount, Math.Max(1, value))) OnPropertyChanged(nameof(RunSummary)); }
    }

    public bool RepeatUntilStopped
    {
        get => _repeatUntilStopped;
        set
        {
            if (value) SetRepeatMode(untilStopped: true, once: false);
        }
    }

    public bool RepeatNTimes
    {
        get => !_repeatUntilStopped && !_repeatOnce;
        set
        {
            if (value) SetRepeatMode(untilStopped: false, once: false);
        }
    }

    public bool RepeatOnce
    {
        get => _repeatOnce;
        set { if (value) SetRepeatMode(untilStopped: false, once: true); }
    }

    private void SetRepeatMode(bool untilStopped, bool once)
    {
        _repeatUntilStopped = untilStopped;
        _repeatOnce = once;
        OnPropertyChanged(nameof(RepeatUntilStopped));
        OnPropertyChanged(nameof(RepeatNTimes));
        OnPropertyChanged(nameof(RepeatOnce));
        OnPropertyChanged(nameof(RunSummary));
    }

    public bool IsMacroActive
    {
        get => _isMacroActive;
        set
        {
            if (!SetProperty(ref _isMacroActive, value)) return;
            OnPropertyChanged(nameof(CanEditConfiguration));
            OnPropertyChanged(nameof(ConcurrencyMessage));
            ToggleRunCommand.RaiseCanExecuteChanged();
            PickLocationCommand.RaiseCanExecuteChanged();
            BeginHotkeyCaptureCommand.RaiseCanExecuteChanged();
        }
    }

    public bool CanEditConfiguration => !_disposed && !IsRunning && !IsStarting && !IsMacroActive;
    public Task RunCompletion => _runTask;
    public string ConcurrencyMessage => IsMacroActive
        ? "Stop the running macro or recording before starting Auto Clicker."
        : "Auto Clicker runs separately from macros. Stop it before recording or running a macro. Panic Stop stops all input.";
    public string RateUnit => SelectedClickType == "Double" ? "double-clicks/s" : "clicks/s";
    public string CompletedCountText => SelectedClickType == "Double"
        ? $"{TotalClicksCompleted:N0} double-click pairs completed"
        : $"{TotalClicksCompleted:N0} clicks completed";
    public string RunSummary => $"{SelectedMouseButton} {(SelectedClickType == "Double" ? "double click" : "click")} · {ClicksPerSecond:0.##} {RateUnit} · "
        + (UseCurrentPosition ? "Current cursor" : $"X {FixedX}, Y {FixedY}") + " · "
        + (RepeatUntilStopped ? "Until stopped" : RepeatOnce ? "Once" : $"{RepeatCount:N0} times")
        + (string.IsNullOrWhiteSpace(StartHotkey) ? " · No shortcut" : $" · {StartHotkey} to toggle");

    public bool IsCapturingHotkey
    {
        get => _isCapturingHotkey;
        set { if (SetProperty(ref _isCapturingHotkey, value)) OnPropertyChanged(nameof(StartHotkeyDisplay)); }
    }
    public string StartHotkeyDisplay => IsCapturingHotkey ? "Press shortcut…" : string.IsNullOrWhiteSpace(StartHotkey) ? "Assign shortcut" : StartHotkey;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(StartStopButtonText));
                OnPropertyChanged(nameof(CanEditConfiguration));
                ToggleRunCommand.RaiseCanExecuteChanged();
                PickLocationCommand.RaiseCanExecuteChanged();
                BeginHotkeyCaptureCommand.RaiseCanExecuteChanged();
                RunningStateChanged?.Invoke();
            }
        }
    }

    public bool IsStopping
    {
        get => _isStopping;
        private set
        {
            if (!SetProperty(ref _isStopping, value)) return;
            OnPropertyChanged(nameof(StartStopButtonText));
            ToggleRunCommand.RaiseCanExecuteChanged();
        }
    }
    public string StartStopButtonText => IsStopping ? "Stopping…" : IsStarting ? "Checking license…" : IsRunning ? IsPaused ? "Paused · Stop" : "Stop" : "Start";

    public long TotalClicksCompleted
    {
        get => _totalClicksCompleted;
        private set { if (SetProperty(ref _totalClicksCompleted, value)) OnPropertyChanged(nameof(CompletedCountText)); }
    }

    public double LiveCpsRunning
    {
        get => _liveCpsRunning;
        private set => SetProperty(ref _liveCpsRunning, value);
    }

    public string IntervalPreviewText
    {
        get
        {
            double ms = 1000.0 / _clicksPerSecond;
            string units = SelectedClickType == "Double" ? "double-click pairs" : "clicks";
            return $"{ClicksPerSecond:0.##} {RateUnit} = {ms:0.##} ms between {units}.";
        }
    }

    public string StartHotkey
    {
        get => _startHotkey;
        set
        {
            if (value != _startHotkey && TryApplyStartHotkey?.Invoke(value) == false) return;
            if (SetProperty(ref _startHotkey, value))
            {
                OnPropertyChanged(nameof(StartHotkeyDisplay));
                OnPropertyChanged(nameof(RunSummary));
                HotkeyChanged?.Invoke();
            }
        }
    }

    // ── Click Tester Properties ───────────────────────────────────────────────

    public bool TestPanelExpanded
    {
        get => _testPanelExpanded;
        set => SetProperty(ref _testPanelExpanded, value);
    }

    public bool IsTestRunning
    {
        get => _isTestRunning;
        private set
        {
            if (!SetProperty(ref _isTestRunning, value)) return;
            StartTestCommand.RaiseCanExecuteChanged();
            StopTestCommand.RaiseCanExecuteChanged();
        }
    }

    public double LiveCpsTester
    {
        get => _liveCpsTester;
        private set => SetProperty(ref _liveCpsTester, value);
    }

    public long TotalTestClicks
    {
        get => _totalTestClicks;
        private set => SetProperty(ref _totalTestClicks, value);
    }

    public string TestElapsedText
    {
        get => _testElapsedText;
        private set => SetProperty(ref _testElapsedText, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public RelayCommand ToggleRunCommand { get; }
    public RelayCommand PickLocationCommand { get; }
    public RelayCommand IncreaseCpsCommand { get; }
    public RelayCommand DecreaseCpsCommand { get; }
    public RelayCommand IncreaseRepeatCommand { get; }
    public RelayCommand DecreaseRepeatCommand { get; }
    public RelayCommand ToggleTestPanelCommand { get; }
    public RelayCommand StartTestCommand { get; }
    public RelayCommand StopTestCommand { get; }
    public RelayCommand ResetTestCommand { get; }
    public RelayCommand RegisterTestClickCommand { get; }
    public RelayCommand BeginHotkeyCaptureCommand { get; }

    public AutoClickerViewModel(IInputPlayer inputPlayer, ILocationPicker? locationPicker = null, AppDataPathResolver? paths = null, IFeatureGate? featureGate = null, IActivationService? activationService = null, HostInputOperationArbiter? inputOperations = null)
    {
        _inputPlayer = inputPlayer ?? throw new ArgumentNullException(nameof(inputPlayer));
        InputOperations = inputOperations ?? new HostInputOperationArbiter();
        _locationPicker = locationPicker;
        _preferencesPaths = paths;
        _featureGate = featureGate;
        _activationService = activationService;
        if (_activationService is not null) _activationService.StateChanged += OnActivationStateChanged;

        ToggleRunCommand = new RelayCommand(ToggleRun, () => !_disposed && !IsStopping && (IsRunning || !IsMacroActive));
        PickLocationCommand = new RelayCommand(() => _ = PickFixedLocationAsync(), () => CanEditConfiguration && UseFixedPosition && !IsPickingLocation);
        IncreaseCpsCommand = new RelayCommand(() => ClicksPerSecond = Math.Min(500, ClicksPerSecond + 1));
        DecreaseCpsCommand = new RelayCommand(() => ClicksPerSecond = Math.Max(0.1, ClicksPerSecond - 1));
        IncreaseRepeatCommand = new RelayCommand(() => RepeatCount++);
        DecreaseRepeatCommand = new RelayCommand(() => RepeatCount = Math.Max(1, RepeatCount - 1));
        ToggleTestPanelCommand = new RelayCommand(() => TestPanelExpanded = !TestPanelExpanded);
        StartTestCommand = new RelayCommand(StartTest, () => !_disposed && !IsTestRunning);
        StopTestCommand = new RelayCommand(StopTest, () => !_disposed && IsTestRunning);
        ResetTestCommand = new RelayCommand(ResetTest, () => !_disposed);
        RegisterTestClickCommand = new RelayCommand(RegisterTestClick, () => !_disposed);
        BeginHotkeyCaptureCommand = new RelayCommand(() => HotkeyCaptureRequested?.Invoke(), () => CanEditConfiguration);
        if (paths is not null)
        {
            try { SettingsBootstrapDefaults.Ensure(paths.ConfigDirectory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { PreferencesStatus = $"Could not create preference defaults: {ex.Message}"; }
            LoadPreferences();
            if (!File.Exists(Path.Combine(paths.ConfigDirectory, "autoclicker.json"))) SavePreferences();
        }
        _preferencesReady = true;
    }

    // ── Run Logic ─────────────────────────────────────────────────────────────

    public void ToggleRun()
    {
        if (_disposed || IsStopping) return;
        if (_runOwner is not null)
            StopClicker();
        else if (!IsMacroActive)
        {
            if (!InputOperations.TryAcquire(HostInputOperationKind.AutoClicker, out var lease, out string reason))
            { PreferencesStatus = reason; return; }
            var owner = new InputOwner(lease!);
            _runOwner = owner;
            // The first continuation is deliberately inline: publish its actual Task before any
            // property/activation callback, while preserving synchronous standalone startup.
            var published = new TaskCompletionSource();
            _runTask = owner.Run = StartClickerAsync(owner, published.Task);
            published.SetResult();
        }
    }

    private async Task StartClickerAsync(InputOwner owner, Task published)
    {
        await published;
        var ct = owner.Stop.Token;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_disposed || IsMacroActive || !owner.Lease.CanContinue) return;
            if (EmergencyStopIsReady?.Invoke() == false)
            { RunLog.Insert(0, "The emergency shortcut is unavailable. Reconfigure it or restart Zenith before starting Auto Clicker."); return; }
            ct.ThrowIfCancellationRequested();
            if (!owner.Lease.CanContinue || _disposed) return;
            IsStarting = true;
            if (_activationService is not null)
            {
                await _activationService.RefreshAsync(ct);
                ct.ThrowIfCancellationRequested();
                _activationService.RequireActive();
            }
            _featureGate?.Require(FeatureId.BasicPlayback);
            ct.ThrowIfCancellationRequested();
            if (_disposed || IsMacroActive || !owner.Lease.CanContinue) return;
            if (_inputPlayer is IHostInputOperationAwarePlayer aware) owner.Attachment = aware.AttachHostOperation(owner.Lease);
            owner.InputStarted = true;
        IsRunning = true;
        IsStarting = false;
        TotalClicksCompleted = 0;

        var button = SelectedMouseButton switch
        {
            "Right" => MouseButton.Right,
            "Middle" => MouseButton.Middle,
            _ => MouseButton.Left
        };
        bool isDouble = SelectedClickType == "Double";

        bool useCurrent = UseCurrentPosition;
        int fixedX = FixedX;
        int fixedY = FixedY;
        bool repeatUntilStopped = RepeatUntilStopped;
        int maxRepeats = RepeatOnce ? 1 : RepeatCount;

            await Task.Run(() =>
            {
                var thread = Thread.CurrentThread;
                var originalPriority = thread.Priority;
                try
                {
                thread.Priority = ThreadPriority.AboveNormal;
                var sw = Stopwatch.StartNew();
                var rng = new Random();

                long totalClicks = 0;
                long lastSampleCount = 0;
                long lastSampleTicks = sw.ElapsedTicks;
                long targetTick = sw.ElapsedTicks;
                WaitHandle[] scheduleWaiters = [ct.WaitHandle, _scheduleChanged];

                while (!ct.IsCancellationRequested)
                {
                    if (IsPaused) { _pauseWake.Wait(ct); targetTick = sw.ElapsedTicks; lastSampleTicks=targetTick;lastSampleCount=totalClicks; }
                    // Execute click through single input pipeline (Rule 4)
                    int clickX = useCurrent ? -1 : fixedX;
                    int clickY = useCurrent ? -1 : fixedY;
                    lock (_clickGate) { if (!owner.Lease.CanContinue) throw new OperationCanceledException(ct); if (IsPaused || ct.IsCancellationRequested) continue; _inputPlayer.SendClick(button, clickX, clickY, isDouble); }

                    totalClicks++;

                    if (!repeatUntilStopped && totalClicks >= maxRepeats)
                        break;

                    // Compute interval in stopwatch ticks
                    double currentCps = ClicksPerSecond;
                    if (RandomOffsetEnabled && RandomOffsetCps > 0)
                    {
                        double jitter = (rng.NextDouble() * 2.0 - 1.0) * RandomOffsetCps;
                        currentCps = Math.Clamp(currentCps + jitter, 0.1, 500.0);
                    }

                    long intervalTicks = (long)(Stopwatch.Frequency / currentCps);
                    targetTick += intervalTicks;

                    // Periodic live CPS and clicks update (~200ms)
                    long nowTicks = sw.ElapsedTicks;
                    if (nowTicks - lastSampleTicks >= (Stopwatch.Frequency / 5))
                    {
                        double elapsedSec = (double)(nowTicks - lastSampleTicks) / Stopwatch.Frequency;
                        LiveCpsRunning = (totalClicks - lastSampleCount) / elapsedSec;
                        TotalClicksCompleted = totalClicks;
                        lastSampleCount = totalClicks;
                        lastSampleTicks = nowTicks;
                    }

                    // Precision timing wait
                    while (sw.ElapsedTicks < targetTick)
                    {
                        if (ct.IsCancellationRequested || IsPaused) break;
                        long remainingTicks = targetTick - sw.ElapsedTicks;
                        long remainingMs = remainingTicks * 1000 / Stopwatch.Frequency;

                        if (remainingMs > 20)
                        {
                            if (WaitHandle.WaitAny(scheduleWaiters, (int)(remainingMs - 15)) != WaitHandle.WaitTimeout) break;
                        }
                        else if (remainingMs > 2)
                        {
                            Thread.Sleep(1);
                        }
                        else
                        {
                            Thread.SpinWait(10);
                        }
                    }

                    // Prevent runaway drift if system lagged
                    if (sw.ElapsedTicks > targetTick + intervalTicks)
                    {
                        targetTick = sw.ElapsedTicks;
                    }
                }

                TotalClicksCompleted = totalClicks;
                }
                finally { thread.Priority = originalPriority; }
            }, ct);
        }
        catch (OperationCanceledException) { /* normal stop */ }
        catch (Exception ex) when (ex is FeatureNotLicensedException or ActivationRequiredException)
        { PreferencesStatus = ex.Message; LogFailure("Starting Auto Clicker", ex); }
        catch (Exception ex)
        {
            if (!owner.InputStarted) PreferencesStatus = $"Could not verify activation: {ex.Message}";
            LogFailure("Running Auto Clicker", ex);
        }
        finally
        {
            Task pause, resume;
            lock (owner.Transitions) { owner.Finalizing = true; pause = owner.Pause; resume = owner.Resume; }
            // A pause acknowledgement is part of this run, even after its producer has stopped.
            try { await Task.WhenAll(pause, resume); }
            catch (Exception ex) { owner.CleanupFailed = true; LogFailure("Joining Auto Clicker pause", ex); }
            // Independent final cleanup must still run after a transition/release fault. A later
            // successful release does not erase that fault or automatically clear quarantine.
            if (owner.InputStarted)
            {
                try { lock (_clickGate) ReleaseInput(owner.Lease); }
                catch (Exception ex) { owner.CleanupFailed = true; LogFailure("Releasing Auto Clicker input", ex); }
            }
            try { owner.Attachment?.Dispose(); owner.Attachment = null; }
            catch (Exception ex) { owner.CleanupFailed = true; LogFailure("Detaching Auto Clicker input", ex); }
            if (owner.CleanupFailed) _failedInputOwner = owner;
            try { await owner.Lease.CompleteAsync(!owner.CleanupFailed); }
            catch (Exception ex) { _failedInputOwner = owner; LogFailure("Finalizing Auto Clicker input", ex); }
            finally { owner.Stop.Dispose(); }
            if (ReferenceEquals(_runOwner, owner))
            {
                _runOwner = null;
                _isStarting = false; _isRunning = false; _isStopping = false; _clickerPaused = false; _liveCpsRunning = 0;
                WakeClicker();
                // Commit every final field before allowing observers to start a replacement.
                OnPropertyChanged(nameof(IsStarting)); OnPropertyChanged(nameof(IsRunning)); OnPropertyChanged(nameof(IsStopping));
                OnPropertyChanged(nameof(IsPaused)); OnPropertyChanged(nameof(LiveCpsRunning));
                OnPropertyChanged(nameof(StartStopButtonText)); OnPropertyChanged(nameof(CanEditConfiguration));
                ToggleRunCommand.RaiseCanExecuteChanged(); PickLocationCommand.RaiseCanExecuteChanged();
                BeginHotkeyCaptureCommand.RaiseCanExecuteChanged(); RunningStateChanged?.Invoke();
            }
        }
    }

    public void StopClicker()
    {
        if (_runOwner is null) return;
        if (IsRunning || IsStarting) IsStopping = true;
        SignalEmergencyStop();
    }

    public Task PickFixedLocationAsync()
    {
        if (!CanEditConfiguration || !UseFixedPosition || IsPickingLocation || _locationPicker is null)
            return Task.CompletedTask;

        if (_pickerOwner is not null) return _pickTask;
        if (!InputOperations.TryAcquire(HostInputOperationKind.Picker, out var lease, out string reason))
        { PreferencesStatus = reason; return Task.CompletedTask; }
        var owner = new InputOwner(lease!);
        _pickerOwner = owner;
        var published = new TaskCompletionSource();
        _pickTask = PickFixedLocationCoreAsync(owner, published.Task);
        published.SetResult();
        return _pickTask;
    }

    private async Task PickFixedLocationCoreAsync(InputOwner owner, Task published)
    {
        await published;
        CancellationTokenRegistration stopped = default;
        try
        {
            stopped = owner.Stop.Token.Register(() =>
            {
                if (_locationPicker is IHostInputOperationBoundPicker bound) bound.Cancel(owner.Lease);
                else _locationPicker?.Cancel();
            });
            owner.Stop.Token.ThrowIfCancellationRequested();
            IsPickingLocation = true;
            owner.Stop.Token.ThrowIfCancellationRequested();
            ScreenPoint? point = ShowOwnedPickOverlay is not null
                ? await ShowOwnedPickOverlay(_locationPicker!, owner.Lease, owner.Stop.Token)
                : ShowPickOverlay is not null ? await ShowPickOverlay(_locationPicker!)
                : _locationPicker is IHostInputOperationBoundPicker bound ? await bound.ArmAndWaitAsync(owner.Lease, owner.Stop.Token)
                : await _locationPicker!.ArmAndWaitAsync(owner.Stop.Token);
            if (_disposed || point is null || !owner.Lease.CanContinue || owner.Stop.IsCancellationRequested)
                return;

            FixedX = point.Value.X;
            FixedY = point.Value.Y;
        }
        catch (OperationCanceledException) { /* normal picker cancellation */ }
        catch (Exception ex)
        {
            owner.CleanupFailed = ex is HostInputOperationCleanupException;
            if (owner.CleanupFailed) PreferencesStatus = ex.Message;
            LogFailure("Choosing the cursor position", ex);
        }
        finally
        {
            // Unregister/join the captured cancellation callback before publishing retirement.
            // No old picker callback may reach a replacement operation after the lease is freed.
            try { await stopped.DisposeAsync(); }
            catch (Exception ex) { owner.CleanupFailed = true; LogFailure("Joining cursor picker cancellation", ex); }
            try { owner.Stop.Dispose(); }
            catch (Exception ex) { owner.CleanupFailed = true; LogFailure("Disposing cursor picker cancellation", ex); }
            try { await owner.Lease.CompleteAsync(!owner.CleanupFailed); }
            catch (Exception ex) { LogFailure("Finalizing the cursor picker", ex); }
            if (ReferenceEquals(_pickerOwner, owner)) { _pickerOwner = null; IsPickingLocation = false; }
        }
    }

    // ── Click Tester Logic ────────────────────────────────────────────────────

    private void StartTest()
    {
        if (_disposed || IsTestRunning || !_testTask.IsCompleted) return;
        var testCts = new CancellationTokenSource();
        _testCts = testCts;
        var published = new TaskCompletionSource();
        _testTask = UpdateTestTimerAsync(testCts, published.Task);
        _testClickTimestamps.Clear();
        _totalTestClicks = 0;
        _liveCpsTester = 0;
        _testStartTime = DateTime.UtcNow;
        _isTestRunning = true;
        published.SetResult();
        OnPropertyChanged(nameof(TotalTestClicks)); OnPropertyChanged(nameof(LiveCpsTester));
        OnPropertyChanged(nameof(IsTestRunning));
        StartTestCommand.RaiseCanExecuteChanged(); StopTestCommand.RaiseCanExecuteChanged();
    }

    private void StopTest()
    {
        CancelOperation(_testCts, "the click speed test");
    }

    private void ResetTest()
    {
        if (_disposed) return;
        StopTest();
        _testClickTimestamps.Clear();
        TotalTestClicks = 0;
        LiveCpsTester = 0;
        TestElapsedText = "0:00";
    }

    public void RegisterTestClick()
    {
        if (_disposed || _testCts?.IsCancellationRequested == true) return;
        if (!IsTestRunning) StartTest();
        if (_disposed || _testCts?.IsCancellationRequested == true) return;

        var now = DateTime.UtcNow;
        _testClickTimestamps.Add(now);
        TotalTestClicks++;

        // Live CPS = clicks registered in the last 1 second window
        var cutoff = now.AddSeconds(-1);
        int recentClicks = _testClickTimestamps.Count(t => t >= cutoff);
        LiveCpsTester = recentClicks;

        // Trim entries older than 2 seconds to keep list small
        _testClickTimestamps.RemoveAll(t => t < now.AddSeconds(-2));
    }

    private async Task UpdateTestTimerAsync(CancellationTokenSource testCts, Task published)
    {
        await published;
        var ct = testCts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(200, ct);
                var elapsed = DateTime.UtcNow - _testStartTime;
                TestElapsedText = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_testCts, testCts))
            {
                _testCts = null;
                IsTestRunning = false;
            }
            testCts.Dispose();
        }
    }

    /// <summary>
    /// Prevents new work, cancels every owned operation, and waits for cleanup.
    /// The application must await this before disposing the shared input player or picker.
    /// </summary>
    public Task ShutdownAsync()
    {
        if (_shutdownTask is not null) return _shutdownTask;
        var published = new TaskCompletionSource();
        _shutdownTask = ShutdownCoreAsync(published.Task);
        published.SetResult();
        return _shutdownTask;
    }

    private async Task ShutdownCoreAsync(Task published)
    {
        await published;
        _disposed = true;
        if (_activationService is not null) _activationService.StateChanged -= OnActivationStateChanged;
        IsCapturingHotkey = false;
        OnPropertyChanged(nameof(CanEditConfiguration));
        ToggleRunCommand.RaiseCanExecuteChanged();
        PickLocationCommand.RaiseCanExecuteChanged();
        BeginHotkeyCaptureCommand.RaiseCanExecuteChanged();
        StartTestCommand.RaiseCanExecuteChanged();
        StopTestCommand.RaiseCanExecuteChanged();
        ResetTestCommand.RaiseCanExecuteChanged();
        RegisterTestClickCommand.RaiseCanExecuteChanged();

        StopClicker();
        StopTest();
        _pickerOwner?.Lease.SignalStop();

        try { await Task.WhenAll(_runTask, _pickTask, _testTask, _pauseAcknowledgement); }
        catch (OperationCanceledException) { /* all operations have reached their cleanup */ }
        catch (Exception ex) { LogFailure("Stopping Auto Clicker", ex); }
        finally { Interlocked.Exchange(ref _pauseSignalsDisposed, 1); _scheduleChanged.Dispose(); _pauseWake.Dispose(); }
    }

    private void CancelOperation(CancellationTokenSource? cts, string operation)
    {
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* the operation already finished */ }
        catch (Exception ex) { LogFailure($"Cancelling {operation}", ex); }
    }

    private void ReleaseInput(HostInputOperationLease lease)
    {
        if (!lease.OwnsCleanup) throw new InvalidOperationException("Auto Clicker no longer owns input cleanup.");
        if (_inputPlayer is IHostInputOperationAwarePlayer aware) aware.ReleaseHostOperationInputs(lease);
        else _inputPlayer.ReleaseAllHeldKeys();
    }

    public async Task<bool> RecoverInputOwnershipAsync(CancellationToken ct = default)
    {
        var owner = _failedInputOwner;
        if (owner is null) return false;
        // Completion is the arbiter's retirement receipt, not the VM's final publication receipt.
        // Join this owner's actual tail, never whichever replacement later occupies _runTask.
        var run = owner.Run;
        await run;
        ct.ThrowIfCancellationRequested();
        var lease = owner.Lease;
        var snapshot = InputOperations.Snapshot;
        if (!ReferenceEquals(_failedInputOwner, owner) || !lease.OwnsCleanup || !lease.IsRetired
            || !snapshot.IsQuarantined || snapshot.OperationId != lease.OperationId) return false;
        bool recovered = await InputOperations.TryRecoverAsync(_ =>
        {
            using var attachment = (_inputPlayer as IHostInputOperationAwarePlayer)?.AttachHostOperation(lease);
            lock (_clickGate) ReleaseInput(lease);
            owner.Attachment?.Dispose();
            owner.Attachment = null;
            return Task.CompletedTask;
        }, ct);
        if (recovered && ReferenceEquals(_failedInputOwner, owner)) _failedInputOwner = null;
        return recovered;
    }

    private void LogFailure(string operation, Exception ex)
        => RunLog.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {operation} failed: {ex.Message}");

    /// <summary>Starts the tracked shutdown. Await ShutdownAsync before disposing shared dependencies.</summary>
    public void Dispose() => _ = ShutdownAsync();
}
