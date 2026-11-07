using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using MacroSuite.App.Common;
using MacroSuite.App.Themes;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.AI;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Security;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Vision;
using MacroSuite.Infrastructure.AIProviders;
using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.App.ViewModels;

/// <summary>
/// View model governing AI providers, concurrent API key management, API key testing, and safety policies.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public event Action<ApprovalPolicy>? ApprovalPolicyChanged;
    public event Action<string>? ThemeChanged;
    private readonly ISecretVault _secretVault;
    private readonly AIVisionRouter _visionRouter;
    private readonly IDisplayInfoService _displayInfoService;
    private readonly AppDataPathResolver _paths;

    private ThemePreset _selectedTheme = ThemeCatalog.Resolve(ThemeCatalog.DefaultId);
    private bool _imageScanBudgetEnabled = true;
    private readonly ImageScanBudget _imageScanBudget;
    private readonly SynchronizationContext? _settingsContext = SynchronizationContext.Current;
    private bool _runtimeSettingsDetached;

    public AppearanceViewModel Appearance { get; }
    public ThemePreset? SelectedPreset
    {
        get => Appearance is not null && !ThemeCatalog.Presets.Any(p => p.Id == Appearance.CurrentPalette.Id) ? null : SelectedTheme;
        set { if (value is not null) { if (SelectedTheme.Id == value.Id) Appearance.UsePreset(value.Id); else SelectedTheme = value; } }
    }
    private void OnAppearanceSelectionChanged() => OnPropertyChanged(nameof(SelectedPreset));
    public bool ImageScanBudgetEnabled
    {
        get => _imageScanBudget.Enabled;
        set => _imageScanBudget.Enabled = value;
    }

    private void OnImageScanBudgetEnabledChanged(bool enabled)
    {
        if (_runtimeSettingsDetached) return;
        if (_settingsContext is not null && SynchronizationContext.Current != _settingsContext)
        {
            _settingsContext.Post(_ => OnImageScanBudgetEnabledChanged(enabled), null);
            return;
        }
        _imageScanBudgetEnabled = enabled;
        OnPropertyChanged(nameof(ImageScanBudgetEnabled));
        SaveConfig();
    }

    public void DetachRuntimeSettings()
    {
        _runtimeSettingsDetached = true;
        _imageScanBudget.EnabledChanged -= OnImageScanBudgetEnabledChanged;
        Appearance.Changed -= OnAppearanceSelectionChanged;
    }

    public IReadOnlyList<ThemePreset> ThemePresets => ThemeCatalog.Presets;

    public ThemePreset SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            ThemePreset theme = ThemeCatalog.Resolve(value?.Id);
            if (!SetProperty(ref _selectedTheme, theme)) return;
            SaveConfig();
            Appearance?.UsePreset(theme.Id);
            ThemeChanged?.Invoke(theme.Id);
        }
    }

    private bool _alwaysOnTop;
    private string _recordHotkey = "F6";
    private string _stopHotkey = "F8";
    private string _playHotkey = "F4";
    private string _pauseHotkey = "F5";
    private string _panicHotkey = "F12";
    private string _autoClickerHotkey = "F1";
    private string _miniToolbarClickThroughHotkey = "Ctrl+Alt+F10";
    private bool _autoSizeActionEditor = true;
    private bool _miniModeWhileRecording = true;
    private bool _miniToolbarClickThrough;
    private bool _miniToolbarHideFromCapture;
    private bool _useRawMouseRecording;
    private double _cameraLookGainX = 1.0;
    private double _cameraLookGainY = 1.0;
    private int _cameraHzIndex = 1;
    private int _cameraModeIndex;
    private bool _detailedExecutionTrace;
    private bool _showDetectionOverlay;
    private string _preferredInputBackendId = "win32";
    private string _inputBackendStatus = "Win32 SendInput is the active input pipeline.";
    private decimal _dailySpendCapUsd = 5.00m;
    private decimal _monthlySpendCapUsd = 50.00m;

    private string _selectedProvider = "openai";
    private string _selectedModel = "gpt-4o";
    private string? _loadedSelectedModel;
    private string? _pendingGeminiModel;
    private CancellationTokenSource? _geminiCatalogCts;
    private bool _isInitializing = true;
    private string _apiKeyInput = string.Empty;
    private bool _hasStoredKey;
    private bool _isTesting;
    private string _testStatusMessage = string.Empty;
    private string _persistenceStatus = string.Empty;
    private AutoClickerViewModel? _autoClickerPreferences;
    private LogoColorMode _logoColorMode = LogoColorMode.FollowTheme;
    public LogoColorMode LogoColorMode { get => _logoColorMode; set { if (Enum.IsDefined(value) && SetProperty(ref _logoColorMode, value)) { OnPropertyChanged(nameof(LogoColorModeIndex)); SaveConfig(); } } }
    public int LogoColorModeIndex { get => (int)_logoColorMode; set { if (value is >= 0 and <= 1) LogoColorMode = (LogoColorMode)value; } }
    public bool AutomaticSettingsSaving { get; set; } = true;
    public string SettingsFolderPath => _paths.ConfigDirectory;
    public string SettingsStatus => PersistenceStatus;
    public bool HasSettingsError => PersistenceStatus.Length > 0 && PersistenceStatus != "Settings saved.";
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand OpenSettingsFolderCommand { get; }
    public void AttachAutoClickerPreferences(AutoClickerViewModel autoClicker) => _autoClickerPreferences = autoClicker;
    public bool SaveSettings()
    {
        var errors = new List<string>();
        bool saved = SettingsSaveCoordinator.Save(new Func<bool>[] {
            () => { bool ok = TrySaveConfig(true); if (!ok) errors.Add(PersistenceStatus); return ok; },
            () => { bool ok = Appearance.TrySaveDraft(); if (!ok) errors.Add(Appearance.Status); return ok; },
            () => { bool ok = _autoClickerPreferences?.SavePreferences() ?? true; if (!ok) errors.Add(_autoClickerPreferences!.PreferencesStatus); return ok; }
        });
        PersistenceStatus = saved ? "Settings saved." : string.Join(" ", errors);
        return saved;
    }
    private void OpenSettingsFolder()
    {
        try { Directory.CreateDirectory(SettingsFolderPath); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SettingsFolderPath) { UseShellExecute = true }); }
        catch (Exception ex) { PersistenceStatus = $"Could not open the settings folder: {ex.Message}"; }
    }
    private string _testLatencyText = string.Empty;

    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set
        {
            if (SetProperty(ref _alwaysOnTop, value))
            {
                SaveConfig();
                AlwaysOnTopChanged?.Invoke(value);
            }
        }
    }

    public string RecordHotkey
    {
        get => _recordHotkey;
        set
        {
            string next = value?.Trim() ?? string.Empty;
            if (!PrepareShortcut("record", next, _recordHotkey)) return;
            if (SetProperty(ref _recordHotkey, next))
            {
                OnPropertyChanged(nameof(RecordHotkeyDisplay));
                SaveConfig();
                RecordHotkeyChanged?.Invoke(_recordHotkey);
            }
        }
    }

    public string StopHotkey
    {
        get => _stopHotkey;
        set
        {
            string next = value?.Trim() ?? string.Empty;
            if (!PrepareShortcut("stop", next, _stopHotkey)) return;
            if (SetProperty(ref _stopHotkey, next))
            {
                OnPropertyChanged(nameof(StopHotkeyDisplay));
                SaveConfig();
                StopHotkeyChanged?.Invoke(_stopHotkey);
            }
        }
    }

    public string PlayHotkey
    {
        get => _playHotkey;
        set
        {
            string next = value?.Trim() ?? string.Empty;
            if (!PrepareShortcut("play", next, _playHotkey)) return;
            if (SetProperty(ref _playHotkey, next))
            {
                OnPropertyChanged(nameof(PlayHotkeyDisplay));
                SaveConfig();
                PlayHotkeyChanged?.Invoke(_playHotkey);
            }
        }
    }

    public string PauseHotkey
    {
        get => _pauseHotkey;
        set
        {
            string next = value?.Trim() ?? string.Empty;
            if (!PrepareShortcut("pause", next, _pauseHotkey)) return;
            if (SetProperty(ref _pauseHotkey, next))
            {
                OnPropertyChanged(nameof(PauseHotkeyDisplay));
                SaveConfig();
                PauseHotkeyChanged?.Invoke(_pauseHotkey);
            }
        }
    }

    /// <summary>The emergency bind is always a valid, non-empty key or combination.</summary>
    public string PanicHotkey
    {
        get => _panicHotkey;
        set
        {
            string next = value?.Trim() ?? string.Empty;
            if (!HotkeyCodec.TryParse(next, out _, out _))
            {
                PersistenceStatus = "Panic Stop requires a valid key or combination; the current bind was kept.";
                return;
            }
            if (!PrepareShortcut("panic", next, _panicHotkey)) return;
            if (SetProperty(ref _panicHotkey, next))
            {
                OnPropertyChanged(nameof(PanicHotkeyDisplay));
                SaveConfig();
                PanicHotkeyChanged?.Invoke(_panicHotkey);
            }
        }
    }

    public string AutoClickerHotkey
    {
        get => _autoClickerHotkey;
        set
        {
            string normalized = value?.Trim() ?? string.Empty;
            if (normalized.Length > 0 && !HotkeyCodec.TryParse(normalized, out _, out _)) return;
            if (normalized.Length > 0 && !PrepareShortcut("autoclicker", normalized, _autoClickerHotkey)) return;
            if (SetProperty(ref _autoClickerHotkey, normalized)) SaveConfig();
        }
    }

    /// <summary>Allows the user to recover or enable mini toolbar interaction from the keyboard.</summary>
    public string MiniToolbarClickThroughHotkey
    {
        get => _miniToolbarClickThroughHotkey;
        set
        {
            string next = value?.Trim() ?? string.Empty;
            if (!HotkeyCodec.TryParse(next, out _, out _))
            {
                PersistenceStatus = "Mini toolbar click-through requires a valid key or combination; the current bind was kept.";
                return;
            }
            if (!PrepareShortcut("mini-click-through", next, _miniToolbarClickThroughHotkey)) return;
            if (SetProperty(ref _miniToolbarClickThroughHotkey, next))
            {
                OnPropertyChanged(nameof(MiniToolbarClickThroughHotkeyDisplay));
                SaveConfig();
                MiniToolbarClickThroughHotkeyChanged?.Invoke(next);
            }
        }
    }

    private string _hotkeyCaptureTarget = string.Empty;

    public string HotkeyCaptureTarget
    {
        get => _hotkeyCaptureTarget;
        private set
        {
            if (SetProperty(ref _hotkeyCaptureTarget, value))
            {
                OnPropertyChanged(nameof(RecordHotkeyDisplay));
                OnPropertyChanged(nameof(StopHotkeyDisplay));
                OnPropertyChanged(nameof(PlayHotkeyDisplay));
                OnPropertyChanged(nameof(PauseHotkeyDisplay));
                OnPropertyChanged(nameof(PanicHotkeyDisplay));
                OnPropertyChanged(nameof(MiniToolbarClickThroughHotkeyDisplay));
            }
        }
    }

    public string RecordHotkeyDisplay => DisplayBind("record", RecordHotkey);
    public string StopHotkeyDisplay => DisplayBind("stop", StopHotkey);
    public string PlayHotkeyDisplay => DisplayBind("play", PlayHotkey);
    public string PauseHotkeyDisplay => DisplayBind("pause", PauseHotkey);
    public string PanicHotkeyDisplay => DisplayBind("panic", PanicHotkey);
    public string MiniToolbarClickThroughHotkeyDisplay => DisplayBind("mini-click-through", MiniToolbarClickThroughHotkey);

    public RelayCommand BeginHotkeyCaptureCommand { get; }

    private string DisplayBind(string target, string hotkey)
        => string.Equals(HotkeyCaptureTarget, target, StringComparison.OrdinalIgnoreCase)
            ? "Press a key…"
            : string.IsNullOrWhiteSpace(hotkey) ? "—" : hotkey;

    private static string NormalizeBind(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    public void BeginHotkeyCapture(object? parameter)
    {
        string target = parameter as string ?? string.Empty;
        HotkeyCaptureTarget = string.Equals(HotkeyCaptureTarget, target, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : target;
    }

    public string ConsumeCaptureTarget()
    {
        string target = HotkeyCaptureTarget;
        HotkeyCaptureTarget = string.Empty;
        return target;
    }

    public bool CompleteHotkeyCapture(string? hotkey)
    {
        string target = HotkeyCaptureTarget;
        if (target.Length == 0) return false;
        HotkeyCaptureTarget = string.Empty;
        if (string.IsNullOrWhiteSpace(hotkey)
            || (!string.Equals(target, "panic", StringComparison.OrdinalIgnoreCase)
                && HotkeyCodec.AreEqual(hotkey, "ESC")))
            return true;

        if (!HotkeyCodec.TryParse(hotkey, out _, out _))
        {
            PersistenceStatus = "Enter a valid key or combination; the current bind was kept.";
            return true;
        }

        var others = new[] { ("record", RecordHotkey), ("stop", StopHotkey), ("play", PlayHotkey), ("pause", PauseHotkey), ("panic", PanicHotkey), ("mini-click-through", MiniToolbarClickThroughHotkey), ("autoclicker", AutoClickerHotkey) };
        if (others.Any(pair => !string.Equals(pair.Item1, target, StringComparison.OrdinalIgnoreCase)
                               && HotkeyCodec.AreEqual(pair.Item2, hotkey)))
            return true;

        switch (target.ToLowerInvariant())
        {
            case "record": RecordHotkey = hotkey.Trim(); break;
            case "stop": StopHotkey = hotkey.Trim(); break;
            case "play": PlayHotkey = hotkey.Trim(); break;
            case "pause": PauseHotkey = hotkey.Trim(); break;
            case "panic": PanicHotkey = hotkey.Trim(); break;
            case "mini-click-through": MiniToolbarClickThroughHotkey = hotkey.Trim(); break;
        }

        if (HotkeyCodec.TryParse(hotkey, out uint modifiers, out _) && modifiers == 0)
            PersistenceStatus = "Warning: single-key global binds can fire while other apps are focused. Prefer Ctrl/Alt/Win combinations.";

        return true;
    }

    public event Action? RequestMiniMode;
    public event Action<bool>? AlwaysOnTopChanged;
    public event Action<bool>? DetectionOverlayEnabledChanged;
    public event Action<string>? RecordHotkeyChanged;
    public event Action<string>? StopHotkeyChanged;
    public event Action<string>? PlayHotkeyChanged;
    public event Action<string>? PauseHotkeyChanged;
    public event Action<string>? PanicHotkeyChanged;
    public event Action<string>? MiniToolbarClickThroughHotkeyChanged;
    private bool? _testIsValid;

    private bool _allowGdiFallback = true;
    private int _maxTargetFps = 165;
    private int _targetFps = 30;
    private bool _showActionEditor = true;
    private double _actionEditorWidth = 300;
    private double _libraryPaneWidth = 240;
    private ApprovalPolicy _selectedPolicy = ApprovalPolicy.ManualApproval;
    private bool _autonomousModeActive;
    private int _approvalTimeoutSeconds = 10;
    private int _maxAiTextLength = 200;
    private double _minAiConfidence = 0.50;

    public ObservableCollection<string> AvailableProviders { get; } = new(["openai", "anthropic", "gemini"]);
    public ObservableCollection<string> AvailableModels { get; } = new();

    public string SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetProperty(ref _selectedProvider, value))
            {
                OnProviderChanged();
                PersistProviderSelection();
            }
        }
    }

    public string SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (SetProperty(ref _selectedModel, value))
            {
                ApplySelectedProviderToRouter();
                PersistProviderSelection();
            }
        }
    }

    public string ApiKeyInput
    {
        get => _apiKeyInput;
        set => SetProperty(ref _apiKeyInput, value);
    }

    public bool HasStoredKey
    {
        get => _hasStoredKey;
        private set
        {
            if (SetProperty(ref _hasStoredKey, value)) OnPropertyChanged(nameof(CredentialStatus));
        }
    }

    public string CredentialStatus => HasStoredKey ? "Encrypted API key saved for this provider." : "No API key saved for this provider.";

    public bool IsTesting
    {
        get => _isTesting;
        private set => SetProperty(ref _isTesting, value);
    }

    public string TestStatusMessage
    {
        get => _testStatusMessage;
        private set => SetProperty(ref _testStatusMessage, value);
    }

    public string PersistenceStatus
    {
        get => _persistenceStatus;
        private set { if (SetProperty(ref _persistenceStatus, value)) { OnPropertyChanged(nameof(SettingsStatus)); OnPropertyChanged(nameof(HasSettingsError)); } }
    }

    public string TestLatencyText
    {
        get => _testLatencyText;
        private set => SetProperty(ref _testLatencyText, value);
    }

    public bool? TestIsValid
    {
        get => _testIsValid;
        private set => SetProperty(ref _testIsValid, value);
    }

    /// <summary>
    /// Daily burst limit in USD (UTC midnight reset). Stops a runaway macro from consuming
    /// the monthly budget in one session. This is not $5 times 30 days.
    /// </summary>
    public decimal DailySpendCapUsd
    {
        get => _dailySpendCapUsd;
        set
        {
            if (SetProperty(ref _dailySpendCapUsd, Math.Max(0m, value)))
                SaveConfig();
        }
    }

    /// <summary>
    /// Monthly budget in USD (UTC calendar month). This is the real ceiling for BYOK safety.
    /// Defaults are placeholders, not a priced paid-tier quota.
    /// </summary>
    public decimal MonthlySpendCapUsd
    {
        get => _monthlySpendCapUsd;
        set
        {
            if (SetProperty(ref _monthlySpendCapUsd, Math.Max(0m, value)))
                SaveConfig();
        }
    }

    public bool AllowGdiFallback
    {
        get => _allowGdiFallback;
        set
        {
            if (SetProperty(ref _allowGdiFallback, value))
                SaveConfig();
        }
    }

    public IReadOnlyList<string> InputBackendOptions { get; } =
    [
        "Win32 SendInput (default)",
        "Virtual HID (reserved — falls back to Win32 until a signed adapter exists)"
    ];

    public string PreferredInputBackendId => _preferredInputBackendId;

    public int PreferredInputBackendIndex
    {
        get => string.Equals(_preferredInputBackendId, "virtual-hid", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        set
        {
            string next = value == 1 ? "virtual-hid" : "win32";
            if (!SetProperty(ref _preferredInputBackendId, next))
                return;
            OnPropertyChanged(nameof(PreferredInputBackendIndex));
            OnPropertyChanged(nameof(PreferredInputBackendId));
            SaveConfig();
        }
    }

    public string InputBackendStatus
    {
        get => _inputBackendStatus;
        set => SetProperty(ref _inputBackendStatus, value ?? string.Empty);
    }

    public int MaxTargetFps
    {
        get => _maxTargetFps;
        private set => SetProperty(ref _maxTargetFps, value);
    }

    public int TargetFps
    {
        get => _targetFps;
        set
        {
            if (SetProperty(ref _targetFps, Math.Clamp(value, 5, MaxTargetFps)))
            {
                OnPropertyChanged(nameof(TargetFpsInputText));
                OnPropertyChanged(nameof(TargetFpsText));
                SaveConfig();
            }
        }
    }

    public bool ShowActionEditor
    {
        get => _showActionEditor;
        set
        {
            if (SetProperty(ref _showActionEditor, value))
                SaveConfig();
        }
    }

    public double ActionEditorWidth
    {
        get => _actionEditorWidth;
        set
        {
            if (!double.IsFinite(value)) return;
            double clamped = Math.Clamp(value, 220, 720);
            if (SetProperty(ref _actionEditorWidth, clamped))
                SaveConfig();
        }
    }

    public double LibraryPaneWidth
    {
        get => _libraryPaneWidth;
        set
        {
            if (!double.IsFinite(value)) return;
            if (SetProperty(ref _libraryPaneWidth, Math.Clamp(value, 220, 360)))
                SaveConfig();
        }
    }

    public bool AutoSizeActionEditor
    {
        get => _autoSizeActionEditor;
        set
        {
            if (SetProperty(ref _autoSizeActionEditor, value))
                SaveConfig();
        }
    }

    public bool UseRawMouseRecording
    {
        get => _useRawMouseRecording;
        set
        {
            if (SetProperty(ref _useRawMouseRecording, value))
                SaveConfig();
        }
    }

    public double CameraLookGainX
    {
        get => _cameraLookGainX;
        set
        {
            if (SetProperty(ref _cameraLookGainX, MouseTrajectoryStep.ClampLookGain(value)))
                SaveConfig();
        }
    }

    public double CameraLookGainY
    {
        get => _cameraLookGainY;
        set
        {
            if (SetProperty(ref _cameraLookGainY, MouseTrajectoryStep.ClampLookGain(value)))
                SaveConfig();
        }
    }

    public int CameraHzIndex
    {
        get => _cameraHzIndex;
        set
        {
            if (SetProperty(ref _cameraHzIndex, Math.Clamp(value, 0, 2)))
                SaveConfig();
        }
    }

    public int CameraModeIndex
    {
        get => _cameraModeIndex;
        set
        {
            if (SetProperty(ref _cameraModeIndex, Math.Clamp(value, 0, 1)))
                SaveConfig();
        }
    }

    public int CameraPlaybackHz => _cameraHzIndex switch { 0 => 60, 2 => 250, _ => 125 };

    public TrajectoryPlaybackMode CameraPlaybackMode
        => _cameraModeIndex == 1
            ? TrajectoryPlaybackMode.PacketReference
            : TrajectoryPlaybackMode.Reconstructed;

    /// <summary>Show the floating mini recorder (and hide the studio window) while recording.</summary>
    public bool MiniModeWhileRecording
    {
        get => _miniModeWhileRecording;
        set
        {
            if (SetProperty(ref _miniModeWhileRecording, value))
                SaveConfig();
        }
    }

    /// <summary>Let pointer input pass through the Mini toolbar; the tray menu remains interactive.</summary>
    public bool MiniToolbarClickThrough
    {
        get => _miniToolbarClickThrough;
        set
        {
            if (SetProperty(ref _miniToolbarClickThrough, value))
                SaveConfig();
        }
    }

    /// <summary>Request exclusion of the Mini toolbar from supported Windows screen captures.</summary>
    public bool MiniToolbarHideFromCapture
    {
        get => _miniToolbarHideFromCapture;
        set
        {
            if (SetProperty(ref _miniToolbarHideFromCapture, value))
                SaveConfig();
        }
    }

    public bool DetailedExecutionTrace
    {
        get => _detailedExecutionTrace;
        set
        {
            if (SetProperty(ref _detailedExecutionTrace, value))
                SaveConfig();
        }
    }

    public bool ShowDetectionOverlay
    {
        get => _showDetectionOverlay;
        set
        {
            if (SetProperty(ref _showDetectionOverlay, value))
            {
                SaveConfig();
                DetectionOverlayEnabledChanged?.Invoke(value);
            }
        }
    }

    public string TargetFpsText => $"{TargetFps} FPS";

    public string TargetFpsInputText
    {
        get => $"{TargetFps}";
        set
        {
            if (int.TryParse(value, out int parsed))
            {
                TargetFps = parsed;
            }
            OnPropertyChanged(nameof(TargetFpsInputText));
        }
    }

    public string MonitorRefreshRateText => $"(Monitor: {MaxTargetFps} Hz)";

    public ApprovalPolicy SelectedPolicy
    {
        get => _selectedPolicy;
        set
        {
            if (SetProperty(ref _selectedPolicy, value))
            {
                SaveConfig();
                ApprovalPolicyChanged?.Invoke(value);
            }
        }
    }

    public bool AutonomousModeActive
    {
        get => _autonomousModeActive;
        set
        {
            if (SetProperty(ref _autonomousModeActive, value))
                SaveConfig();
        }
    }

    public int ApprovalTimeoutSeconds
    {
        get => _approvalTimeoutSeconds;
        set
        {
            if (SetProperty(ref _approvalTimeoutSeconds, Math.Clamp(value, 8, 60)))
                SaveConfig();
        }
    }

    public int MaxAiTextLength
    {
        get => _maxAiTextLength;
        set
        {
            if (SetProperty(ref _maxAiTextLength, Math.Clamp(value, 1, 10_000)))
                SaveConfig();
        }
    }

    public double MinAiConfidence
    {
        get => _minAiConfidence;
        set
        {
            if (SetProperty(ref _minAiConfidence, Math.Clamp(value, 0.0, 1.0)))
                SaveConfig();
        }
    }

    public RelayCommand SaveKeyCommand { get; }
    public RelayCommand RemoveKeyCommand { get; }
    public RelayCommand TestKeyCommand { get; }
    public RelayCommand SwitchToMiniModeCommand { get; }

    public SettingsViewModel(
        ISecretVault secretVault,
        AIVisionRouter visionRouter,
        IDisplayInfoService? displayInfoService = null,
        AppDataPathResolver? paths = null,
        ImageScanBudget? imageScanBudget = null)
    {
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _visionRouter = visionRouter ?? throw new ArgumentNullException(nameof(visionRouter));
        _displayInfoService = displayInfoService ?? new WindowsDisplayInfoService();
        _paths = paths ?? AppDataPathResolver.Default;
        _imageScanBudget = imageScanBudget ?? ImageScanBudget.Shared;

        try { SettingsBootstrapDefaults.Ensure(_paths.ConfigDirectory); }
        catch (Exception ex) { PersistenceStatus = $"Could not create settings defaults: {ex.Message}"; }
        LoadConfig();
        Appearance = new AppearanceViewModel(_paths.RootDirectory, _selectedTheme.Id);
        Appearance.Changed += OnAppearanceSelectionChanged;
        _imageScanBudget.Enabled = _imageScanBudgetEnabled;
        _imageScanBudget.EnabledChanged += OnImageScanBudgetEnabledChanged;

        int detectedMax = _displayInfoService.GetMaxMonitorRefreshRate();
        _maxTargetFps = Math.Max(165, detectedMax);

        string rec = NormalizeBind(_recordHotkey, "F6");
        string stop = NormalizeBind(_stopHotkey, "F8");
        string play = NormalizeBind(_playHotkey, "F4");
        string pause = NormalizeBind(_pauseHotkey, "F5");
        bool repaired = rec != _recordHotkey || stop != _stopHotkey || play != _playHotkey || pause != _pauseHotkey;
        _recordHotkey = rec;
        _stopHotkey = stop;
        _playHotkey = play;
        _pauseHotkey = pause;
        if (repaired)
            SaveConfig();

        SaveKeyCommand = new RelayCommand(SaveApiKey, () => !string.IsNullOrWhiteSpace(ApiKeyInput));
        RemoveKeyCommand = new RelayCommand(RemoveApiKey, () => HasStoredKey);
        SaveSettingsCommand = new RelayCommand(() => SaveSettings());
        OpenSettingsFolderCommand = new RelayCommand(OpenSettingsFolder);
        if (!File.Exists(Path.Combine(_paths.ConfigDirectory, "settings.json"))) TrySaveConfig(true);
        BeginHotkeyCaptureCommand = new RelayCommand(BeginHotkeyCapture);
        TestKeyCommand = new RelayCommand(async () => await TestApiKeyAsync(), () => !IsTesting);
        SwitchToMiniModeCommand = new RelayCommand(() => RequestMiniMode?.Invoke());

        OnProviderChanged();
        _isInitializing = false;
        ApplySelectedProviderToRouter();
    }

    private void OnProviderChanged()
    {
        HasStoredKey = _secretVault.HasSecret(SelectedProvider);
        ApiKeyInput = string.Empty;
        TestStatusMessage = string.Empty;
        TestLatencyText = string.Empty;
        TestIsValid = null;

        var provider = _visionRouter.TryGetProvider(SelectedProvider);
        AvailableModels.Clear();

        if (provider is GoogleGeminiVisionProvider gemini)
        {
            AvailableModels.Add(gemini.DefaultModel);
            _pendingGeminiModel = _isInitializing ? _loadedSelectedModel : null;
            string prefer = _pendingGeminiModel ?? gemini.DefaultModel;
            string resolved = GoogleGeminiVisionProvider.ResolveModelId(prefer);
            SelectedModel = TryFindModel(AvailableModels, resolved, out string exact)
                ? exact
                : gemini.DefaultModel;
            ApplySelectedProviderToRouter();
            ScheduleGeminiServedModelsRefresh();
            return;
        }

        CancelGeminiCatalogRefresh();

        if (provider != null)
        {
            foreach (var m in provider.SupportedModels)
            {
                AvailableModels.Add(m);
            }
        }
        else
        {
            AvailableModels.Add("default");
        }

        if (_isInitializing && !string.IsNullOrWhiteSpace(_loadedSelectedModel))
        {
            if (!AvailableModels.Contains(_loadedSelectedModel))
                AvailableModels.Add(_loadedSelectedModel);
            SelectedModel = _loadedSelectedModel;
        }
        else if (provider != null)
        {
            SelectedModel = provider.DefaultModel;
        }
        else
        {
            SelectedModel = "default";
        }

        ApplySelectedProviderToRouter();
    }

    private void ApplySelectedProviderToRouter()
    {
        if (!string.IsNullOrWhiteSpace(SelectedProvider))
            _visionRouter.DefaultProviderId = SelectedProvider;

        string model = SelectedModel;
        if (_visionRouter.TryGetProvider(SelectedProvider) is GoogleGeminiVisionProvider)
        {
            string resolved = GoogleGeminiVisionProvider.ResolveModelId(model);
            if (!string.Equals(resolved, model, StringComparison.OrdinalIgnoreCase))
            {
                if (SetProperty(ref _selectedModel, resolved, nameof(SelectedModel)))
                    PersistProviderSelection();
                model = resolved;
            }
        }

        _visionRouter.DefaultModelId = string.IsNullOrWhiteSpace(model) ? null : model;
    }

    private void ScheduleGeminiServedModelsRefresh()
    {
        if (_visionRouter.TryGetProvider(SelectedProvider) is not GoogleGeminiVisionProvider)
            return;

        CancelGeminiCatalogRefresh();
        _geminiCatalogCts = new CancellationTokenSource();
        CancellationToken ct = _geminiCatalogCts.Token;
        _ = RefreshGeminiServedModelsAsync(ct);
    }

    private void CancelGeminiCatalogRefresh()
    {
        try
        {
            _geminiCatalogCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RefreshGeminiServedModelsAsync(CancellationToken ct)
    {
        if (_visionRouter.TryGetProvider(SelectedProvider) is not GoogleGeminiVisionProvider gemini)
            return;

        string? key = !string.IsNullOrWhiteSpace(ApiKeyInput)
            ? ApiKeyInput.Trim()
            : _secretVault.GetSecret(SelectedProvider);

        if (string.IsNullOrWhiteSpace(key))
            return;

        try
        {
            var result = await gemini.ValidateApiKeyAsync(key, gemini.DefaultModel, ct);
            if (ct.IsCancellationRequested)
                return;
            if (!result.IsValid || result.AvailableModels.Count == 0)
                return;

            ApplyServedGeminiModels(result.AvailableModels);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Keep the placeholder Flash-Lite entry. Do not overwrite Test API Key status.
        }
    }

    private void ApplyServedGeminiModels(IReadOnlyList<string> served)
    {
        AvailableModels.Clear();
        foreach (string model in served)
        {
            if (string.IsNullOrWhiteSpace(model))
                continue;
            if (GoogleGeminiVisionProvider.IsRetiredGeminiId(model))
                continue;
            if (!TryFindModel(AvailableModels, model, out _))
                AvailableModels.Add(model);
        }

        if (AvailableModels.Count == 0)
            AvailableModels.Add(GoogleGeminiVisionProvider.CurrentFlashLiteModel);

        string prefer = _pendingGeminiModel ?? SelectedModel;
        string resolved = GoogleGeminiVisionProvider.ResolveModelId(prefer);
        string next;
        if (!GoogleGeminiVisionProvider.IsRetiredGeminiId(prefer)
            && TryFindModel(AvailableModels, prefer, out string preferExact))
        {
            next = preferExact;
        }
        else if (TryFindModel(AvailableModels, resolved, out string resolvedExact))
        {
            next = resolvedExact;
        }
        else if (TryFindModel(AvailableModels, GoogleGeminiVisionProvider.CurrentFlashLiteModel, out string liteExact))
        {
            next = liteExact;
        }
        else
        {
            next = AvailableModels[0];
        }

        _pendingGeminiModel = null;
        if (!string.Equals(_selectedModel, next, StringComparison.OrdinalIgnoreCase))
        {
            _selectedModel = next;
            OnPropertyChanged(nameof(SelectedModel));
            PersistProviderSelection();
        }

        ApplySelectedProviderToRouter();
    }

    private static bool TryFindModel(IEnumerable<string> models, string? id, out string exact)
    {
        exact = string.Empty;
        if (string.IsNullOrWhiteSpace(id))
            return false;

        foreach (string model in models)
        {
            if (string.Equals(model, id, StringComparison.OrdinalIgnoreCase))
            {
                exact = model;
                return true;
            }
        }

        return false;
    }

    private void PersistProviderSelection()
    {
        if (!_isInitializing)
            SaveConfig();
    }

    private void SaveApiKey()
    {
        if (string.IsNullOrWhiteSpace(ApiKeyInput)) return;

        // DPAPI encryption via vault
        _secretVault.SetSecret(SelectedProvider, ApiKeyInput.Trim());
        HasStoredKey = true;
        ApiKeyInput = string.Empty;
        TestStatusMessage = "API key securely stored in DPAPI vault.";
        TestIsValid = true;
        ScheduleGeminiServedModelsRefresh();
    }

    private void RemoveApiKey()
    {
        _secretVault.RemoveSecret(SelectedProvider);
        HasStoredKey = false;
        TestStatusMessage = "API key removed from vault.";
        TestIsValid = null;
        if (_visionRouter.TryGetProvider(SelectedProvider) is GoogleGeminiVisionProvider gemini)
        {
            CancelGeminiCatalogRefresh();
            ApplyServedGeminiModels([gemini.DefaultModel]);
        }
    }

    public async Task TestApiKeyAsync()
    {
        IsTesting = true;
        TestStatusMessage = "Connecting to provider API...";
        TestLatencyText = string.Empty;
        TestIsValid = null;

        try
        {
            string? keyToTest = !string.IsNullOrWhiteSpace(ApiKeyInput) ? ApiKeyInput.Trim() : null;
            var result = await _visionRouter.TestApiKeyAsync(SelectedProvider, keyToTest, SelectedModel);

            TestIsValid = result.IsValid;
            TestLatencyText = $"{Math.Round(result.Latency.TotalMilliseconds)} ms";

            if (result.IsValid)
            {
                TestStatusMessage = $"✓ Connected! {result.Message}";
                if (result.AvailableModels.Count > 0)
                {
                    if (_visionRouter.TryGetProvider(SelectedProvider) is GoogleGeminiVisionProvider)
                    {
                        ApplyServedGeminiModels(result.AvailableModels);
                    }
                    else
                    {
                        AvailableModels.Clear();
                        foreach (var m in result.AvailableModels)
                        {
                            AvailableModels.Add(m);
                        }
                        if (!AvailableModels.Contains(SelectedModel))
                        {
                            SelectedModel = AvailableModels[0];
                        }
                    }
                }
            }
            else
            {
                TestStatusMessage = $"✗ Verification Failed: {result.Message}";
            }
        }
        catch (Exception ex)
        {
            TestIsValid = false;
            TestStatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    public void LoadConfig()
    {
        string configDir = _paths.ConfigDirectory;
        string configPath = Path.Combine(configDir, "settings.json");
        var loaded = SettingsJsonStore.Load(configPath);
        PersistenceStatus = loaded.Message ?? string.Empty;

        if (loaded.Document is null)
        {
            if (!loaded.TrustSafetyCritical)
            {
                _autonomousModeActive = false;
                _selectedPolicy = ApprovalPolicy.ManualApproval;
            }
            return;
        }

        try
        {
            using var doc = loaded.Document;
            var root = doc.RootElement;
            _shortcutDefaultsVersion = root.TryGetProperty("shortcutDefaultsVersion", out var defaultsVersion) && defaultsVersion.TryGetInt32(out int version) ? version : 0;
            if (root.TryGetProperty("logoColorMode", out var logo) && logo.ValueKind == JsonValueKind.String && Enum.TryParse<LogoColorMode>(logo.GetString(), out var logoMode) && Enum.IsDefined(logoMode)) _logoColorMode = logoMode;
            _selectedTheme = root.TryGetProperty("themeId", out var themeId) && themeId.ValueKind == JsonValueKind.String
                ? ThemeCatalog.Resolve(themeId.GetString())
                : ThemeCatalog.Resolve(ThemeCatalog.DefaultId);
            _imageScanBudgetEnabled = !root.TryGetProperty("imageScanBudgetEnabled", out var scanBudget)
                || scanBudget.ValueKind != JsonValueKind.False;
            if (root.TryGetProperty("alwaysOnTop", out var aot)) _alwaysOnTop = aot.GetBoolean();
            if (root.TryGetProperty("recordHotkey", out var rh)) _recordHotkey = rh.GetString() ?? "F6";
            if (root.TryGetProperty("stopHotkey", out var sh)) _stopHotkey = sh.GetString() ?? "F8";
            if (root.TryGetProperty("allowGdiFallback", out var gdi)) _allowGdiFallback = gdi.GetBoolean();
            if (root.TryGetProperty("targetFps", out var fps)) _targetFps = fps.GetInt32();
            if (root.TryGetProperty("playHotkey", out var ph)) _playHotkey = ph.GetString() ?? "F4";
            if (root.TryGetProperty("pauseHotkey", out var pah)) _pauseHotkey = pah.GetString() ?? "F5";
            if (root.TryGetProperty("autoClickerHotkey", out var ach) && ach.ValueKind == JsonValueKind.String)
            {
                string bind = ach.GetString()?.Trim() ?? string.Empty;
                if (bind.Length == 0 || HotkeyCodec.TryParse(bind, out _, out _)) _autoClickerHotkey = bind;
            }
            _miniToolbarClickThroughHotkey = "Ctrl+Alt+F10";
            if (root.TryGetProperty("miniToolbarClickThroughHotkey", out var miniClickThroughHotkey)
                && miniClickThroughHotkey.ValueKind == JsonValueKind.String
                && HotkeyCodec.TryParse(miniClickThroughHotkey.GetString(), out _, out _))
                _miniToolbarClickThroughHotkey = miniClickThroughHotkey.GetString()!.Trim();
            bool hasPanicHotkey = root.TryGetProperty("panicHotkey", out var panic)
                && panic.ValueKind == JsonValueKind.String
                && HotkeyCodec.TryParse(panic.GetString(), out _, out _);
            if (hasPanicHotkey)
                _panicHotkey = panic.GetString()!.Trim();
            if (root.TryGetProperty("showActionEditor", out var sae)) _showActionEditor = sae.GetBoolean();
            if (root.TryGetProperty("actionEditorWidth", out var aew)) _actionEditorWidth = aew.GetDouble();
            if (root.TryGetProperty("libraryPaneWidth", out var lpw) && lpw.TryGetDouble(out double width) && double.IsFinite(width))
                _libraryPaneWidth = Math.Clamp(width, 220, 360);
            if (root.TryGetProperty("autoSizeActionEditor", out var asae)) _autoSizeActionEditor = asae.GetBoolean();
            if (root.TryGetProperty("miniModeWhileRecording", out var mmr)) _miniModeWhileRecording = mmr.GetBoolean();
            // These modes are opt-in, including when older or invalid values are loaded.
            _miniToolbarClickThrough = root.TryGetProperty("miniToolbarClickThrough", out var clickThrough)
                && clickThrough.ValueKind == JsonValueKind.True;
            _miniToolbarHideFromCapture = root.TryGetProperty("miniToolbarHideFromCapture", out var hideFromCapture)
                && hideFromCapture.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("useRawMouseRecording", out var raw)) _useRawMouseRecording = raw.GetBoolean();
            if (root.TryGetProperty("cameraLookGainX", out var cgx) && cgx.TryGetDouble(out double gx))
                _cameraLookGainX = MouseTrajectoryStep.ClampLookGain(gx);
            if (root.TryGetProperty("cameraLookGainY", out var cgy) && cgy.TryGetDouble(out double gy))
                _cameraLookGainY = MouseTrajectoryStep.ClampLookGain(gy);
            if (root.TryGetProperty("cameraHzIndex", out var chz) && chz.TryGetInt32(out int hz))
                _cameraHzIndex = Math.Clamp(hz, 0, 2);
            if (root.TryGetProperty("cameraModeIndex", out var cmi) && cmi.TryGetInt32(out int mode))
                _cameraModeIndex = Math.Clamp(mode, 0, 1);
            if (loaded.TrustSafetyCritical
                && root.TryGetProperty("approvalPolicy", out var policy)
                && Enum.TryParse(policy.GetString(), true, out ApprovalPolicy parsedPolicy))
                _selectedPolicy = parsedPolicy;
            else
                _selectedPolicy = ApprovalPolicy.ManualApproval;
            if (loaded.TrustSafetyCritical && root.TryGetProperty("autonomousModeActive", out var autonomous))
                _autonomousModeActive = autonomous.GetBoolean();
            else
                _autonomousModeActive = false;
            if (root.TryGetProperty("approvalTimeoutSeconds", out var approvalTimeout)
                && approvalTimeout.TryGetInt32(out int seconds))
                _approvalTimeoutSeconds = Math.Clamp(seconds, 8, 60);
            if (root.TryGetProperty("maxAiTextLength", out var maxText)
                && maxText.TryGetInt32(out int textLen))
                _maxAiTextLength = Math.Clamp(textLen, 1, 10_000);
            if (root.TryGetProperty("minAiConfidence", out var minConf)
                && minConf.TryGetDouble(out double confidence))
                _minAiConfidence = Math.Clamp(confidence, 0.0, 1.0);
            if (root.TryGetProperty("detailedExecutionTrace", out var trace))
                _detailedExecutionTrace = trace.GetBoolean();
            if (root.TryGetProperty("showDetectionOverlay", out var overlay))
                _showDetectionOverlay = overlay.GetBoolean();
            if (root.TryGetProperty("preferredInputBackendId", out var backend)
                && backend.GetString() is { } backendId
                && backendId.Equals("virtual-hid", StringComparison.OrdinalIgnoreCase))
                _preferredInputBackendId = "virtual-hid";
            if (root.TryGetProperty("selectedProvider", out var selectedProvider)
                && selectedProvider.GetString() is { } providerId
                && !string.IsNullOrWhiteSpace(providerId)
                && AvailableProviders.Contains(providerId))
                _selectedProvider = providerId;
            if (root.TryGetProperty("selectedModel", out var selectedModel)
                && selectedModel.GetString() is { } modelId
                && !string.IsNullOrWhiteSpace(modelId))
                _loadedSelectedModel = modelId;
            if (loaded.TrustSafetyCritical
                && root.TryGetProperty("dailySpendCapUsd", out var dailyCap)
                && dailyCap.TryGetDecimal(out decimal daily))
                _dailySpendCapUsd = Math.Max(0m, daily);
            if (loaded.TrustSafetyCritical
                && root.TryGetProperty("monthlySpendCapUsd", out var monthlyCap)
                && monthlyCap.TryGetDecimal(out decimal monthly))
                _monthlySpendCapUsd = Math.Max(0m, monthly);
            if (!hasPanicHotkey)
            {
                // Six choices ensure one is free of the five application transport binds.
                string[] transports = [_recordHotkey, _stopHotkey, _playHotkey, _pauseHotkey, _autoClickerHotkey];
                string[] candidates = ["F12", "Ctrl+Shift+F12", "Ctrl+Alt+Shift+F12", "Alt+Shift+F11", "Ctrl+Alt+Shift+F11", "Ctrl+Shift+F11"];
                _panicHotkey = candidates.First(candidate => transports.All(bind => !HotkeyCodec.AreEqual(bind, candidate)));
            }
        }
        catch (Exception ex)
        {
            _autonomousModeActive = false;
            _selectedPolicy = ApprovalPolicy.ManualApproval;
            PersistenceStatus = $"Settings could not be applied; using conservative safety values. {ex.Message}";
        }
    }

    public bool TrySaveSpendCaps(decimal daily, decimal monthly)
    {
        decimal previousDaily = _dailySpendCapUsd;
        decimal previousMonthly = _monthlySpendCapUsd;
        _dailySpendCapUsd = Math.Max(0m, daily);
        _monthlySpendCapUsd = Math.Max(0m, monthly);
        SaveConfig();
        bool saved = !PersistenceStatus.StartsWith("Could not save settings:", StringComparison.Ordinal);
        if (!saved)
        {
            _dailySpendCapUsd = previousDaily;
            _monthlySpendCapUsd = previousMonthly;
        }
        OnPropertyChanged(nameof(DailySpendCapUsd));
        OnPropertyChanged(nameof(MonthlySpendCapUsd));
        return saved;
    }

    public void SaveConfig() => TrySaveConfig(false);

    private bool TrySaveConfig(bool force)
    {
        if (!force && !AutomaticSettingsSaving) return true;
        string configDir = _paths.ConfigDirectory;
        string configPath = Path.Combine(configDir, "settings.json");

        var data = new
        {
            themeId = _selectedTheme.Id,
            logoColorMode = _logoColorMode.ToString(),
            imageScanBudgetEnabled = _imageScanBudgetEnabled,
            alwaysOnTop = _alwaysOnTop,
            shortcutDefaultsVersion = _shortcutDefaultsVersion,
            recordHotkey = _recordHotkey,
            stopHotkey = _stopHotkey,
            playHotkey = _playHotkey,
            pauseHotkey = _pauseHotkey,
            panicHotkey = _panicHotkey,
            autoClickerHotkey = _autoClickerHotkey,
            miniToolbarClickThroughHotkey = _miniToolbarClickThroughHotkey,
            allowGdiFallback = _allowGdiFallback,
            targetFps = _targetFps,
            showActionEditor = _showActionEditor,
            actionEditorWidth = _actionEditorWidth,
            libraryPaneWidth = _libraryPaneWidth,
            autoSizeActionEditor = _autoSizeActionEditor,
            miniModeWhileRecording = _miniModeWhileRecording,
            miniToolbarClickThrough = _miniToolbarClickThrough,
            miniToolbarHideFromCapture = _miniToolbarHideFromCapture,
            useRawMouseRecording = _useRawMouseRecording,
            cameraLookGainX = _cameraLookGainX,
            cameraLookGainY = _cameraLookGainY,
            cameraHzIndex = _cameraHzIndex,
            cameraModeIndex = _cameraModeIndex,
            approvalPolicy = _selectedPolicy.ToString(),
            autonomousModeActive = _autonomousModeActive,
            approvalTimeoutSeconds = _approvalTimeoutSeconds,
            maxAiTextLength = _maxAiTextLength,
            minAiConfidence = _minAiConfidence,
            detailedExecutionTrace = _detailedExecutionTrace,
            showDetectionOverlay = _showDetectionOverlay,
            preferredInputBackendId = _preferredInputBackendId,
            selectedProvider = _selectedProvider,
            selectedModel = _selectedModel,
            dailySpendCapUsd = _dailySpendCapUsd,
            monthlySpendCapUsd = _monthlySpendCapUsd
        };
        string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        try
        {
            SettingsJsonStore.SaveAtomic(configPath, json);
            if (string.IsNullOrEmpty(PersistenceStatus) || PersistenceStatus.StartsWith("Could not save", StringComparison.Ordinal))
                PersistenceStatus = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            PersistenceStatus = $"Could not save settings: {ex.Message}";
            return false;
        }
    }
}
