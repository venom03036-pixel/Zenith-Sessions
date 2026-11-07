using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Data;
using GongSolutions.Wpf.DragDrop;
using MacroSuite.App.Common;
using MacroSuite.App.Overlays;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.AI;
using MacroSuite.Core.Audit;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Usage;
using MacroSuite.Core.Vision;
using MacroSuite.Infrastructure.Input;
using MacroSuite.Infrastructure.OpenCV;
using MacroSuite.Infrastructure.Security;
using MacroSuite.Infrastructure.Windows;
using DrawingPoint = System.Drawing.Point;
using DrawingRect = System.Drawing.Rectangle;

namespace MacroSuite.App.ViewModels;

public sealed record ActionRouteTarget(Guid StepId, string DisplayName);

public sealed class MacroScriptItemViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isActive = true;
    private bool _isRunning;
    private bool _isFavorite;
    private int _actionCount;
    private string _name = string.Empty;
    private string _hotkey = string.Empty;
    private string _lastRunText = "Never";

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public int ActionCount
    {
        get => _actionCount;
        set => SetProperty(ref _actionCount, value);
    }

    public string Hotkey
    {
        get => _hotkey;
        set => SetProperty(ref _hotkey, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }

    public string LastRunText
    {
        get => _lastRunText;
        set => SetProperty(ref _lastRunText, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (SetProperty(ref _isFavorite, value))
                OnPropertyChanged(nameof(FavoriteGlyph));
        }
    }

    /// <summary>Star glyph shown in the script card — filled when favorited, outline when not.</summary>
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";

    public List<MacroStep> Actions { get; } = new();
    public List<MacroStepItemViewModel> Steps { get; } = new();
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FilePath { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public TimeSpan MaxRuntime { get; set; } = TimeSpan.FromHours(1);
    public int MaxIterations { get; set; } = 100_000;
    public int MaxLoopDepth { get; set; } = 8;
    public int RepeatCount { get; set; } = 1;
    public bool RepeatUntilStopped { get; set; }
    public double PlaybackSpeed { get; set; } = 1.0;
    public string Description { get; set; } = string.Empty;
    public MacroTargetRule? Target { get; set; }
    public RecordingCommitMetadata? RecordingCommit { get; set; }
    public MouseSamplingMode MouseSamplingMode { get; set; } = MouseSamplingMode.DistanceAndTime;

    public MacroScriptItemViewModel(
        string name,
        int actionCount,
        string hotkey,
        bool isActive = true,
        string lastRun = "Last run: 3 hr ago",
        Guid? id = null,
        string? filePath = null,
        DateTimeOffset? createdAt = null,
        bool isFavorite = false)
    {
        _name = name;
        _actionCount = actionCount;
        _hotkey = hotkey;
        _isActive = isActive;
        _lastRunText = lastRun;
        _isFavorite = isFavorite;
        if (id.HasValue) Id = id.Value;
        if (!string.IsNullOrEmpty(filePath)) FilePath = filePath;
        if (createdAt.HasValue) CreatedAt = createdAt.Value;
    }

    public IReadOnlyList<MacroStep> GetActions()
    {
        if (Actions.Count > 0)
            return Actions;
        if (Steps.Count == 0)
            return Actions;
        return Steps.ConvertAll(s => s.Step);
    }
}

public sealed partial class MacroStepItemViewModel : ObservableObject
{
    private bool _isEnabled = true;
    private int _stepIndex = 1;

    public MacroStep Step { get; }
    public string Title { get; }
    public string Description { get; }
    public string TypeLabel { get; }

    public int StepIndex
    {
        get => _stepIndex;
        set => SetProperty(ref _stepIndex, value);
    }

    public string ActionKeyword { get; set; } = "ACTION";
    public string ParameterTokens { get; set; } = "";
    private string _timingOffset = "—";
    public string TimingOffset
    {
        get => _timingOffset;
        set => SetProperty(ref _timingOffset, value);
    }
    public string KeywordBrushKey { get; set; } = "AccentMint";

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                var prop = Step.GetType().GetProperty(nameof(MacroStep.IsEnabled));
                prop?.SetValue(Step, value);
            }
        }
    }

    public MacroStepItemViewModel(MacroStep step, string title, string description, string typeLabel)
    {
        Step = step ?? throw new ArgumentNullException(nameof(step));
        Title = title;
        Description = description;
        TypeLabel = typeLabel;
        _isEnabled = step.IsEnabled;

        ConfigureTokens();
        if (step is ClickStep click && (!string.IsNullOrWhiteSpace(click.XVariable)
            || !string.IsNullOrWhiteSpace(click.YVariable) || click.OffsetX != 0 || click.OffsetY != 0))
        {
            Title = $"{click.Button} click: {ParameterTokens}";
            Description = $"Click target: {ParameterTokens}. Offsets move the click from the target point.";
        }
        ApplyTiming(null);
    }

    public static MacroStepItemViewModel FromStep(MacroStep step, int index, MacroStep? previous = null)
    {
        string type = step switch
        {
            ClickStep => "MOUSE",
            MouseMoveStep => "MOUSE",
            MouseWheelStep => "MOUSE",
            MouseSweepStep => "MOUSE",
            MouseTrajectoryStep => "MOUSE",
            KeyPressStep => "KEYBOARD",
            DelayStep => "FLOW",
            AIStep => "AI VISION",
            WaitUntilStep => "RULE",
            VisualWaitStep => "VISION",
            LoopStep => "FLOW",
            IfThenElseStep => "FLOW",
            _ => "STEP"
        };
        var vm = new MacroStepItemViewModel(step, step.Label ?? step.GetType().Name, "", type)
        {
            StepIndex = index + 1
        };
        vm.ApplyTiming(previous);
        return vm;
    }

    public void ApplyTiming(MacroStep? previous)
    {
        if (Step is DelayStep delay)
        {
            TimingOffset = DurationDisplay.Format(delay.Duration);
            return;
        }

        if (Step is MouseSweepStep sweep)
        {
            TimingOffset = DurationDisplay.Format(sweep.DurationMs);
            return;
        }

        if (Step is MouseTrajectoryStep path)
        {
            TimingOffset = DurationDisplay.Format(path.DurationMs);
            return;
        }

        bool haveCurrent = StepTimeline.TryGetRelativeTimestamp(Step, out TimeSpan current);
        if (haveCurrent
            && previous is not null
            && StepTimeline.TryGetRelativeTimestamp(previous, out TimeSpan previousTime)
            && current >= previousTime)
        {
            TimeSpan gap = current - previousTime;
            TimingOffset = gap > TimeSpan.Zero ? DurationDisplay.Format(gap) : "—";
            return;
        }

        TimingOffset = haveCurrent ? DurationDisplay.Format(current) : "—";
    }

    public void ConfigureTokens()
    {
        switch (Step)
        {
            case ClickStep click:
                ActionKeyword = ClickKeyword(click);
                ParameterTokens = ClickTargetTokens(click);
                KeywordBrushKey = "AccentMint";
                break;
            case MouseSweepStep sweep:
                ActionKeyword = "SWEEP";
                ParameterTokens = $"dx={sweep.DeltaX} dy={sweep.DeltaY} {sweep.DurationMs}ms {MouseSweepMotion.ClampHz(sweep.PlaybackHz)}Hz";
                KeywordBrushKey = "AccentSky";
                break;
            case MouseTrajectoryStep path:
                ActionKeyword = "PATH";
                MouseTrajectoryPlayback.MeasureExtents(path.Samples, out float minX, out float maxX, out float minY, out float maxY);
                ParameterTokens = $"{path.Samples.Length}pt x={MouseTrajectoryPlayback.FormatExtent(minX, maxX)} y={MouseTrajectoryPlayback.FormatExtent(minY, maxY)} {path.DurationMs}ms ×{FormatLookGain(path.Sensitivity)}/{FormatLookGain(path.SensitivityY)}";
                KeywordBrushKey = "AccentSky";
                break;
            case MouseWheelStep wheel:
                ActionKeyword = "SCROLL";
                ParameterTokens = wheel.DeltaX != 0 && wheel.DeltaY == 0
                    ? $"deltaX={wheel.DeltaX}"
                    : wheel.DeltaX != 0
                        ? $"deltaY={wheel.DeltaY} deltaX={wheel.DeltaX}"
                        : $"delta={wheel.DeltaY}";
                KeywordBrushKey = "AccentSky";
                break;
            case MouseMoveStep move:
                if (move.IsRelative)
                {
                    ActionKeyword = "MOVE_REL";
                    ParameterTokens = $"dx={move.Position.X} dy={move.Position.Y}";
                }
                else
                {
                    ActionKeyword = "MOVE";
                    ParameterTokens = move.IsClientRelative
                        ? $"client=({move.Position.X}, {move.Position.Y})"
                        : $"x={move.Position.X} y={move.Position.Y}";
                }
                KeywordBrushKey = "AccentSky";
                break;
            case KeyPressStep key:
                ActionKeyword = (key.Stroke ?? "Press").ToLowerInvariant() switch
                {
                    "up" => "KEY_UP",
                    "down" => "KEY_DOWN",
                    _ => "KEY_PRESS"
                };
                ParameterTokens = $"key={VirtualKeyName(key.KeyCode)}";
                KeywordBrushKey = "AccentWarning";
                break;
            case DelayStep delay:
                ActionKeyword = "WAIT";
                ParameterTokens = DurationDisplay.Format(delay.Duration);
                KeywordBrushKey = "AccentMauve";
                break;
            case AIStep ai:
                ActionKeyword = "AI_VISION";
                ParameterTokens = $"prompt=\"{ai.Prompt}\"";
                KeywordBrushKey = "AccentCyan";
                break;
            case WaitUntilStep until:
                if (until.Condition is ImageCondition img)
                {
                    ActionKeyword = until.AppearanceRedirect is not null ? "ON_IMAGE_APPEARS"
                        : until.RepeatWhileVisible ? "REPEAT_IMAGE" : until.WaitWhile ? "WAIT_WHILE_IMAGE" : "WAIT_IMAGE";
                    ParameterTokens = !string.IsNullOrEmpty(img.TemplatePath)
                        ? $"template=\"{Path.GetFileName(img.TemplatePath)}\" conf={img.MinConfidence:P0}"
                        : $"template=photo conf={img.MinConfidence:P0}";
                    if (until.RepeatWhileVisible)
                        ParameterTokens += $" · repeat every {Math.Clamp(until.RepeatDelayMs, 50, 60_000)} ms · actions to next image";
                    if (until.AppearanceRedirect is not null)
                        ParameterTokens += " · jump to chosen action once per appearance";
                    KeywordBrushKey = until.WaitWhile ? "AccentWarning" : "AccentMint";
                }
                else if (until.Condition is ColorCondition color)
                {
                    ActionKeyword = until.WaitWhile ? "WAIT_WHILE_COLOR" : "WAIT_COLOR";
                    string where = color.Region is { } region
                        ? $"region=({region.X},{region.Y},{region.Width}x{region.Height})"
                        : color.Location is { } loc
                            ? $"pixel=({loc.X},{loc.Y})"
                            : "pixel";
                    string extras = color.AdditionalTargets.Count > 0 ? $" +{color.AdditionalTargets.Count}" : "";
                    ParameterTokens = $"{where} RGB({color.R},{color.G},{color.B}){extras} {color.ComparisonMode} {color.MinMatchPercentage:P0} ±{color.Tolerance}";
                    KeywordBrushKey = "AccentCyan";
                }
                else
                {
                    ActionKeyword = "WAIT_COLOR";
                    ParameterTokens = until.Condition.Description;
                    KeywordBrushKey = "AccentCyan";
                }
                break;
            case VisualWaitStep visual:
                ActionKeyword = visual.Mode.Equals("Region", StringComparison.OrdinalIgnoreCase)
                    ? "WAIT_VISUAL_REGION"
                    : "WAIT_VISUAL";
                ParameterTokens = visual.Mode.Equals("Region", StringComparison.OrdinalIgnoreCase)
                    ? $"region=({visual.X},{visual.Y},{visual.Width}x{visual.Height}) {visual.Hex} {visual.ComparisonMode} sim={visual.SimilarityPercent}%"
                    : $"pixel=({visual.X},{visual.Y}) {visual.Hex} {visual.ComparisonMode} sim={visual.SimilarityPercent}%";
                KeywordBrushKey = "AccentCyan";
                break;
            case IfThenElseStep iff:
                ActionKeyword = "IF";
                ParameterTokens = $"{iff.Condition.Description}  then={iff.ThenSteps.Count} else={iff.ElseSteps.Count}";
                KeywordBrushKey = "AccentMauve";
                break;
            case LoopStep loop:
                ActionKeyword = loop.RepeatUntil ? "UNTIL" : loop.WhileCondition != null ? "WHILE" : "REPEAT";
                ParameterTokens = loop.WhileCondition != null
                    ? $"{loop.WhileCondition.Description}  body={loop.BodySteps.Count} max={loop.MaxDurationMs}ms"
                    : $"count={loop.MaxIterations} body={loop.BodySteps.Count}";
                KeywordBrushKey = "AccentMauve";
                break;
            default:
                ActionKeyword = TypeLabel.ToUpperInvariant().Replace(" ", "_");
                ParameterTokens = Description;
                KeywordBrushKey = "AccentMint";
                break;
        }
    }

    private static string FormatLookGain(double gain)
    {
        if (Math.Abs(gain - Math.Round(gain)) < 0.05)
            return ((int)Math.Round(gain)).ToString();
        return gain.ToString("0.##");
    }

    private static string ClickTargetTokens(ClickStep click)
    {
        static string Coordinate(string? variable, int position)
            => string.IsNullOrWhiteSpace(variable) ? position.ToString()
                : variable.Trim().StartsWith('$') ? variable.Trim() : "$" + variable.Trim();
        bool detectedImage = string.Equals(Coordinate(click.XVariable, click.Position.X),
                "$" + ImageCondition.LastMatchXVariable, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Coordinate(click.YVariable, click.Position.Y),
                "$" + ImageCondition.LastMatchYVariable, StringComparison.OrdinalIgnoreCase);
        string target = detectedImage ? "detected image"
            : $"x={Coordinate(click.XVariable, click.Position.X)} y={Coordinate(click.YVariable, click.Position.Y)}";
        if (click.OffsetX != 0 || click.OffsetY != 0)
            target += $" offset=({click.OffsetX:+0;-0;0},{click.OffsetY:+0;-0;0})";
        if (click.IsClientRelative) target += " client";
        return target;
    }

    private static string ClickKeyword(ClickStep click)
    {
        string prefix = click.Button switch
        {
            MacroSuite.Core.Models.MouseButton.Right => "RIGHT",
            MacroSuite.Core.Models.MouseButton.Middle => "MIDDLE",
            MacroSuite.Core.Models.MouseButton.X1 => "X1",
            MacroSuite.Core.Models.MouseButton.X2 => "X2",
            _ => "LEFT"
        };
        string stroke = click.Stroke ?? "Press";
        if (stroke.Equals("Down", StringComparison.OrdinalIgnoreCase))
            return prefix + "_DOWN";
        if (stroke.Equals("Up", StringComparison.OrdinalIgnoreCase))
            return prefix + "_UP";
        return prefix + "_CLICK";
    }

    public static string VirtualKeyName(int vk)
    {
        if (vk >= 0x70 && vk <= 0x87) return $"F{vk - 0x70 + 1}";
        if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
        if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
        if (vk >= 0x60 && vk <= 0x69) return $"Num{vk - 0x60}";
        return vk switch
        {
            13 => "ENTER",
            17 => "CTRL",
            18 => "ALT",
            16 => "SHIFT",
            32 => "SPACE",
            27 => "ESC",
            9 => "TAB",
            8 => "BACKSPACE",
            0xBE => ".",
            0xBC => ",",
            0xBD => "-",
            0xBB => "=",
            0xBF => "/",
            0xBA => ";",
            0xDE => "'",
            0xDB => "[",
            0xDD => "]",
            0xDC => "\\",
            0xC0 => "`",
            _ => $"0x{vk:X2}"
        };
    }
}

public sealed partial class MacroEditorViewModel : ObservableObject, IDropTarget, IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out WinPoint lpPoint);

    private readonly IInputRecorder _inputRecorder;
    private readonly IRecordingDesktopState _recordingDesktop;
    private readonly ILocationPicker? _locationPicker;
    private readonly System.Windows.Threading.Dispatcher? _uiDispatcher;
    private readonly MacroActionStore _actionStore = new();
    private readonly EditorHistory _editHistory = new();
    private CancellationTokenSource? _recordingCts;
    private Task? _recordingConsumerTask;
    private Task? _recordingStopTask;
    private Task? _recordingCaptureStopTask;
    private TaskCompletionSource? _recordingStartCompletion;
    private readonly object _recordingLifecycleGate = new();
    private RecordingInputBuffer? _recordingInputBuffer;
    private string? _recordingFailure;
    private long _recordingStartTick;
    private TimeSpan _recordingTimelineOrigin;
    private readonly System.Collections.Concurrent.ConcurrentQueue<MacroStep> _recordedStagingQueue = new();
    private int _totalEventsCaptured;
    private long _focusWarningTimestamp;
    private System.Windows.Threading.DispatcherTimer? _uiUpdateTimer;
    private MouseSamplingMode _mouseSamplingMode = MouseSamplingMode.DistanceAndTime;
    private int _mouseMinDistancePx = 8;
    private double _mouseMinTimeMs = 32.0;
    private readonly IScreenCaptureService _captureService;
    private readonly IInputPlayer _inputPlayer;
    private readonly AIVisionRouter _visionRouter;
    private readonly IApprovalGate _approvalGate;
    private readonly IFeatureGate _featureGate;
    private readonly UsageTracker _usageTracker;
    private readonly IAuditLogger _auditLogger;
    private readonly IImageMatcher _imageMatcher;
    private readonly IImageEncoder _imageEncoder;
    private readonly IClickRefiner _clickRefiner;
    private readonly MacroJsonSerializer _serializer = new();
    private readonly string _macrosDirectory;
    private readonly string? _customMacrosDirectory;
    private readonly bool _isPersistenceEnabled;

    private string _macroName = "Skill Rotation B";
    private string _description = "High DPS combo loop with burst ability rotation.";
    private string _currentHotkey = "Ctrl+2";
    private string _currentLastRun = "Last run: 3 hr ago";
    private MacroStepItemViewModel? _selectedStep;
    private bool _committingSelection;
    private MacroScriptItemViewModel? _selectedScript;
    private bool _isRunning;
    private bool _isPaused;
    private double _playbackSpeed = 1.0;
    private int _repeatCount = 1;
    private MacroController? _activeController;
    private readonly object _playbackSync = new();
    private Task? _playbackExecutionTask;
    private BoundedExecutionStatusSink? _activeStatusSink;
    private volatile bool _disposed;

    private bool _isRecording;
    private bool _isStartingRecording;
    private bool _isRecordingFollowEnabled = true;
    private bool _showResumeFollowBanner;
    private bool _showActionEditor = true;
    private double _actionEditorWidth = 300;
    private bool _autoSizeActionEditor = true;
    private bool _isScriptEnabled = true;
    private bool _isEditingMacro;
    private bool _isScriptHotkeyBoxFocused;
    private string _selectedCenterTab = "SCRIPTS";
    private int _totalRunsCount = 0;
    private string _recordHotkey = TransportHotkeyDefaults.Record;
    private string _stopHotkey = TransportHotkeyDefaults.Stop;
    private string _playHotkey = TransportHotkeyDefaults.Play;
    private string _pauseHotkey = TransportHotkeyDefaults.Pause;
    private string _panicHotkey = TransportHotkeyDefaults.Panic;
    private string _miniToolbarClickThroughHotkey = TransportHotkeyDefaults.MiniClickThrough;
    private bool _isMiniToolbarVisible;
    public bool IsMiniToolbarVisible
    {
        get => Volatile.Read(ref _isMiniToolbarVisible);
        set => Volatile.Write(ref _isMiniToolbarVisible, value);
    }
    private string _quickBindCaptureTarget = string.Empty;
    private List<string> _reservedTransportHotkeys = new();
    private string _scriptSearchText = string.Empty;
    private string _actionSearchText = string.Empty;
    private int _actionSearchMatchCount;
    private int _actionSearchOrdinal;
    private bool _actionSearchNavigating;
    private ICollectionView? _scriptsView;
    private ICollectionView? _favoritesView;

    public string RecordHotkey
    {
        get => _recordHotkey;
        set
        {
            if (SetProperty(ref _recordHotkey, value?.Trim() ?? string.Empty))
            {
                OnPropertyChanged(nameof(RecordHotkeyDisplay));
                OnPropertyChanged(nameof(EmptyRecordButtonText));
                HotkeysUpdated?.Invoke();
                RefreshKeybindRows();
            }
        }
    }

    public string StopHotkey
    {
        get => _stopHotkey;
        set
        {
            if (SetProperty(ref _stopHotkey, value?.Trim() ?? string.Empty))
            {
                OnPropertyChanged(nameof(StopHotkeyDisplay));
                OnPropertyChanged(nameof(EmptyRecordButtonText));
                OnPropertyChanged(nameof(RecordingBannerText));
                HotkeysUpdated?.Invoke();
                RefreshKeybindRows();
            }
        }
    }

    public string PlayHotkey
    {
        get => _playHotkey;
        set
        {
            if (SetProperty(ref _playHotkey, value?.Trim() ?? string.Empty))
            {
                OnPropertyChanged(nameof(PlayHotkeyDisplay));
                HotkeysUpdated?.Invoke();
                RefreshKeybindRows();
            }
        }
    }

    public string PauseHotkey
    {
        get => _pauseHotkey;
        set
        {
            if (SetProperty(ref _pauseHotkey, value?.Trim() ?? string.Empty))
            {
                OnPropertyChanged(nameof(PauseHotkeyDisplay));
                HotkeysUpdated?.Invoke();
                RefreshKeybindRows();
            }
        }
    }

    public string MiniToolbarClickThroughHotkey
    {
        get => _miniToolbarClickThroughHotkey;
        set
        {
            string next = value?.Trim() ?? string.Empty;
            if (!HotkeyCodec.TryParse(next, out _, out _))
            {
                StatusChanged?.Invoke("Click-through requires a valid key or combination; the current bind was kept.");
                return;
            }
            if (SetProperty(ref _miniToolbarClickThroughHotkey, next))
            {
                HotkeysUpdated?.Invoke();
                RefreshKeybindRows();
            }
        }
    }

    public string PanicHotkey
    {
        get => _panicHotkey;
        set
        {
            string next = value?.Trim() ?? string.Empty;
            if (!HotkeyCodec.TryParse(next, out _, out _))
            {
                StatusChanged?.Invoke("Panic Stop requires a valid key or combination; the current bind was kept.");
                return;
            }
            if (SetProperty(ref _panicHotkey, next))
            {
                OnPropertyChanged(nameof(PanicHotkeyDisplay));
                PanicHotkeyChanged?.Invoke(next);
                HotkeysUpdated?.Invoke();
                RefreshKeybindRows();
            }
        }
    }

    /// <summary>
    /// Which named bind is waiting for a keystroke, including "panic",
    /// or empty when not capturing.
    /// </summary>
    public string QuickBindCaptureTarget
    {
        get => _quickBindCaptureTarget;
        private set
        {
            if (SetProperty(ref _quickBindCaptureTarget, value))
            {
                OnPropertyChanged(nameof(RecordHotkeyDisplay));
                OnPropertyChanged(nameof(StopHotkeyDisplay));
                OnPropertyChanged(nameof(PlayHotkeyDisplay));
                OnPropertyChanged(nameof(PauseHotkeyDisplay));
                OnPropertyChanged(nameof(PanicHotkeyDisplay));
                RefreshKeybindRows();
            }
        }
    }

    /// <summary>
    /// True while the editor hotkey box has focus so global macros must not steal the next key.
    /// </summary>
    public bool IsScriptHotkeyBoxFocused
    {
        get => _isScriptHotkeyBoxFocused;
        set => SetProperty(ref _isScriptHotkeyBoxFocused, value);
    }

    public string RecordHotkeyDisplay => DisplayFor("record", RecordHotkey);
    public string StopHotkeyDisplay => DisplayFor("stop", StopHotkey);
    public string PlayHotkeyDisplay => DisplayFor("play", PlayHotkey);
    public string PauseHotkeyDisplay => DisplayFor("pause", PauseHotkey);
    public string PanicHotkeyDisplay => DisplayFor("panic", PanicHotkey);
    public string EmptyRecordButtonText => IsRecording
        ? $"● Recording… ({StopHotkeyDisplay} to Stop)"
        : $"Record ({RecordHotkeyDisplay})";
    public string RecordingBannerText => $"RECORDING ACTIVE ({StopHotkeyDisplay} to Stop)";

    private string DisplayFor(string target, string hotkey)
        => string.Equals(QuickBindCaptureTarget, target, StringComparison.OrdinalIgnoreCase)
            ? "Press a key…"
            : string.IsNullOrWhiteSpace(hotkey) ? "—" : hotkey;

    /// <summary>Raised when the user rebinds a transport key so settings can persist it.</summary>
    public event Action<string, string>? QuickBindChanged;
    public event Action<string>? PanicHotkeyChanged;

    /// <summary>
    /// Enter the floating mini overlay immediately (before the first recorded or played
    /// input) so clicks land on the target app instead of this window.
    /// </summary>
    public Action? EnterCompactOverlay { get; set; }
    public Action<ColorDetectionResult>? DetectionOverlayUpdated { get; set; }
    public Action<ExecutionStatusSnapshot>? ExecutionStatusPublished { get; set; }
    public Action? DetectionOverlayStopped { get; set; }

    /// <summary>Restore the studio window after recording or playback ends.</summary>
    public Action? LeaveCompactOverlay { get; set; }

    public void BeginQuickBindCapture(object? parameter)
    {
        string target = parameter as string ?? string.Empty;
        QuickBindCaptureTarget = string.Equals(QuickBindCaptureTarget, target, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : target;

        if (QuickBindCaptureTarget.Length > 0)
            StatusChanged?.Invoke(string.Equals(QuickBindCaptureTarget, "panic", StringComparison.OrdinalIgnoreCase)
                ? "Press any key or combo to rebind Panic Stop (ESC binds Escape; click the bind again to cancel)."
                : $"Press any key to rebind {BindDisplayName(QuickBindCaptureTarget)} (ESC cancels).");
    }

    /// <summary>
    /// Completes an in-progress quick bind. Returns true when the keystroke was consumed.
    /// </summary>
    public bool CompleteQuickBindCapture(string? hotkey)
    {
        string target = QuickBindCaptureTarget;
        if (target.Length == 0) return false;

        if (string.IsNullOrWhiteSpace(hotkey)
            || (!string.Equals(target, "panic", StringComparison.OrdinalIgnoreCase)
                && HotkeyCodec.AreEqual(hotkey, "ESC")))
        {
            QuickBindCaptureTarget = string.Empty;
            if (HotkeyCodec.AreEqual(hotkey, "ESC"))
                StatusChanged?.Invoke("Bind cancelled. The current key was kept.");
            return true;
        }

        // Keep capture armed until after the conflict dialog so the other macro
        // cannot steal the same key while the popup is open.
        TryAssignBind(target, hotkey.Trim());
        QuickBindCaptureTarget = string.Empty;
        return true;
    }

    /// <summary>
    /// Assigns <paramref name="hotkey"/> to a named bind. If another named bind already
    /// owns it, asks before unbinding that owner (Fortnite-style).
    /// </summary>
    public bool TryAssignBind(string targetId, string hotkey)
    {
        if (string.IsNullOrWhiteSpace(targetId)) return false;
        if (targetId.ToLowerInvariant() is not ("record" or "stop" or "play" or "pause" or "panic" or "autoclicker" or "mini-click-through")
            && FindScriptByBindId(targetId) is null) return false;
        hotkey = hotkey.Trim();
        if (hotkey.Length == 0) return false;
        if (!HotkeyCodec.TryParse(hotkey, out _, out _))
        {
            StatusChanged?.Invoke("Enter a valid key or combination; the current bind was kept.");
            return false;
        }

        if (TryApplyTransportHotkey is not null && targetId.StartsWith("script:", StringComparison.OrdinalIgnoreCase) && TransportBinds().Any(bind => HotkeyCodec.AreEqual(bind.Hotkey, hotkey)))
        { NotifyStatus("That shortcut controls Zenith. Change the transport shortcut in Settings before assigning it to a macro."); return false; }

        if (!targetId.StartsWith("script:", StringComparison.OrdinalIgnoreCase) && TryApplyTransportHotkey?.Invoke(targetId.ToLowerInvariant(), hotkey) == false) return false;

        string original = ReadNamedBind(targetId);
        string newOwner = OwnerDisplayName(targetId);
        string suggested = FindFreeHotkey();
        var currentOwner = FindBindOwner(hotkey);
        if (currentOwner != null && SameBindId(currentOwner.Id, targetId))
        {
            ApplyNamedBind(targetId, original.Length > 0 ? original : hotkey);
            RefreshKeybindRows();
            return true;
        }

        if (currentOwner != null && !SameBindId(currentOwner.Id, targetId))
        {
            if (SameBindId(targetId, "mini-click-through") && !currentOwner.Id.StartsWith("script:", StringComparison.OrdinalIgnoreCase))
            {
                StatusChanged?.Invoke($"{hotkey} already controls {currentOwner.DisplayName}. Choose an unused click-through shortcut or change that control first.");
                return false;
            }
            if (currentOwner.Id == "mini-click-through")
            {
                StatusChanged?.Invoke($"{MiniToolbarClickThroughHotkey} is reserved for mini toolbar click-through. Change that shortcut before reusing this bind.");
                return false;
            }
            if (currentOwner.Id == "panic")
            {
                StatusChanged?.Invoke($"{PanicHotkey} is reserved for Panic Stop. Change Panic Stop before reusing this bind.");
                return false;
            }

            var prompt = new HotkeyConflictPrompt(hotkey, currentOwner.DisplayName, newOwner, suggested, original);
            var choice = ConfirmHotkeyConflictPrompter?.Invoke(prompt) ?? HotkeyConflictChoice.Cancel;
            if (choice == HotkeyConflictChoice.Cancel)
            {
                RestoreNamedBind(targetId, original);
                StatusChanged?.Invoke($"Kept {newOwner} on {(original.Length > 0 ? original : "its original key")}.");
                return false;
            }

            if (choice == HotkeyConflictChoice.UseSuggested)
            {
                ApplyNamedBind(targetId, suggested);
                StatusChanged?.Invoke($"{newOwner} bound to {suggested}. {currentOwner.DisplayName} kept {hotkey}.");
                LogActivity($"'{newOwner}' bound to suggested '{suggested}' instead of '{hotkey}'.");
                RefreshKeybindRows();
                return true;
            }

            currentOwner.Unbind();
        }
        else if (currentOwner == null && IsReservedTransportHotkey(hotkey))
        {
            var prompt = new HotkeyConflictPrompt(hotkey, "a reserved app bind", newOwner, suggested, original);
            var choice = ConfirmHotkeyConflictPrompter?.Invoke(prompt) ?? HotkeyConflictChoice.Cancel;
            if (choice == HotkeyConflictChoice.Cancel)
            {
                RestoreNamedBind(targetId, original);
                StatusChanged?.Invoke($"'{hotkey}' is reserved by a transport control.");
                return false;
            }

            if (choice == HotkeyConflictChoice.UseSuggested)
            {
                ApplyNamedBind(targetId, suggested);
                StatusChanged?.Invoke($"{newOwner} bound to {suggested}.");
                RefreshKeybindRows();
                return true;
            }
        }

        ApplyNamedBind(targetId, hotkey);
        StatusChanged?.Invoke($"{newOwner} bound to {hotkey}.");
        LogActivity($"'{newOwner}' bound to '{hotkey}'.");
        RefreshKeybindRows();
        return true;
    }

    private IEnumerable<(string Name, string Hotkey)> TransportBinds()
    {
        yield return ("record", RecordHotkey);
        yield return ("stop", StopHotkey);
        yield return ("play", PlayHotkey);
        yield return ("pause", PauseHotkey);
        yield return ("panic", PanicHotkey);
        yield return ("autoclicker", AutoClickerHotkey);
        yield return ("mini-click-through", MiniToolbarClickThroughHotkey);
    }

    private string OwnerDisplayName(string targetId)
    {
        if (targetId.StartsWith("script:", StringComparison.OrdinalIgnoreCase))
            return FindScriptByBindId(targetId)?.Name ?? "this macro";

        return BindDisplayName(targetId);
    }

    private string ReadNamedBind(string targetId)
    {
        if (targetId.StartsWith("script:", StringComparison.OrdinalIgnoreCase))
            return FindScriptByBindId(targetId)?.Hotkey ?? string.Empty;

        return targetId.ToLowerInvariant() switch
        {
            "record" => RecordHotkey,
            "stop" => StopHotkey,
            "play" => PlayHotkey,
            "pause" => PauseHotkey,
            "panic" => PanicHotkey,
            "autoclicker" => AutoClickerHotkey,
            "mini-click-through" => MiniToolbarClickThroughHotkey,
            _ => string.Empty
        };
    }

    private void RestoreNamedBind(string targetId, string original)
    {
        ApplyNamedBind(targetId, original);
        RefreshKeybindRows();
    }

    private MacroScriptItemViewModel? FindScriptByBindId(string targetId)
    {
        if (!targetId.StartsWith("script:", StringComparison.OrdinalIgnoreCase))
            return null;

        string idText = targetId["script:".Length..];
        return MacroScripts.FirstOrDefault(s =>
            s.Id.ToString("N").Equals(idText, StringComparison.OrdinalIgnoreCase)
            || s.Id.ToString().Equals(idText, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SameBindId(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!a.StartsWith("script:", StringComparison.OrdinalIgnoreCase)
            || !b.StartsWith("script:", StringComparison.OrdinalIgnoreCase))
            return false;

        return Guid.TryParse(a["script:".Length..], out Guid left)
               && Guid.TryParse(b["script:".Length..], out Guid right)
               && left == right;
    }

    public static string BindDisplayName(string targetId)
    {
        if (targetId.StartsWith("script:", StringComparison.OrdinalIgnoreCase))
            return "this macro";

        return targetId.ToLowerInvariant() switch
        {
            "record" => "Toggle Record",
            "stop" => "Stop Playback / Recording",
            "play" => "Play Macro",
            "pause" => "Pause Macro",
            "panic" => "Panic Stop",
            "autoclicker" => "Toggle Auto Clicker",
            "mini-click-through" => "Toggle Mini Toolbar Click-through",
            _ => targetId
        };
    }

    private sealed class BindOwner
    {
        public required string Id { get; init; }
        public required string DisplayName { get; init; }
        public required Action Unbind { get; init; }
    }

    private BindOwner? FindBindOwner(string hotkey)
    {
        if (HotkeyCodec.AreEqual(hotkey, PanicHotkey))
            return new BindOwner { Id = "panic", DisplayName = "Panic Stop", Unbind = () => { } };

        foreach (var (name, value) in TransportBinds())
        {
            if (!HotkeyCodec.AreEqual(value, hotkey)) continue;
            string id = name;
            return new BindOwner
            {
                Id = id,
                DisplayName = BindDisplayName(id),
                Unbind = () => ClearNamedBind(id)
            };
        }

        foreach (var script in MacroScripts)
        {
            if (!HotkeyCodec.AreEqual(script.Hotkey, hotkey)) continue;
            var captured = script;
            return new BindOwner
            {
                Id = ScriptBindId(captured),
                DisplayName = captured.Name,
                Unbind = () =>
                {
                    captured.Hotkey = string.Empty;
                    if (captured == SelectedScript)
                        CurrentHotkey = string.Empty;
                    SaveMacroScript(captured);
                }
            };
        }

        return null;
    }

    private static string ScriptBindId(MacroScriptItemViewModel script) => $"script:{script.Id:N}";

    private void ClearNamedBind(string targetId)
    {
        ApplyNamedBind(targetId, string.Empty);
    }

    private void ApplyNamedBind(string targetId, string hotkey)
    {
        if (targetId.StartsWith("script:", StringComparison.OrdinalIgnoreCase))
        {
            var script = FindScriptByBindId(targetId);
            if (script == null) return;
            script.Hotkey = hotkey;
            if (script == SelectedScript)
                CurrentHotkey = hotkey;
            SaveMacroScript(script);
            HotkeysUpdated?.Invoke();
            return;
        }

        switch (targetId.ToLowerInvariant())
        {
            case "record": RecordHotkey = hotkey; break;
            case "stop": StopHotkey = hotkey; break;
            case "play": PlayHotkey = hotkey; break;
            case "pause": PauseHotkey = hotkey; break;
            case "panic": PanicHotkey = hotkey; break;
            case "autoclicker": AutoClickerHotkey = hotkey; break;
            case "mini-click-through": MiniToolbarClickThroughHotkey = hotkey; break;
            default: return;
        }

        QuickBindChanged?.Invoke(targetId.ToLowerInvariant(), hotkey);
        OnPropertyChanged(nameof(RecordHotkeyDisplay));
        OnPropertyChanged(nameof(StopHotkeyDisplay));
        OnPropertyChanged(nameof(PlayHotkeyDisplay));
        OnPropertyChanged(nameof(PauseHotkeyDisplay));
        OnPropertyChanged(nameof(PanicHotkeyDisplay));
        OnPropertyChanged(nameof(EmptyRecordButtonText));
        OnPropertyChanged(nameof(RecordingBannerText));
    }

    public void RefreshKeybindRows()
    {
        if (BeginNamedBindCaptureCommand == null) return;

        ReplaceKeybindRows(AppKeybinds,
        [
            CreateBindRow("panic", "Panic Stop", PanicHotkey),
            CreateBindRow("record", "Toggle Record", RecordHotkey),
            CreateBindRow("stop", "Stop Playback / Recording", StopHotkey),
            CreateBindRow("play", "Play Macro", PlayHotkey),
            CreateBindRow("pause", "Pause Macro", PauseHotkey),
            CreateBindRow("autoclicker", "Toggle Auto Clicker", AutoClickerHotkey),
            CreateBindRow("mini-click-through", "Toggle Mini Toolbar Click-through", MiniToolbarClickThroughHotkey)
        ]);

        var macroRows = new List<KeybindRowViewModel>(MacroScripts.Count);
        foreach (var script in MacroScripts)
        {
            string id = ScriptBindId(script);
            bool capturing = string.Equals(QuickBindCaptureTarget, id, StringComparison.OrdinalIgnoreCase);
            macroRows.Add(new KeybindRowViewModel(id, script.Name, script.Hotkey, true, BeginNamedBindCaptureCommand)
            {
                HotkeyDisplay = capturing ? "Press a key…" : (string.IsNullOrWhiteSpace(script.Hotkey) ? "—" : script.Hotkey),
                IsCapturing = capturing
            });
        }

        ReplaceKeybindRows(MacroKeybinds, macroRows);
    }

    private KeybindRowViewModel CreateBindRow(string id, string name, string hotkey)
    {
        bool capturing = string.Equals(QuickBindCaptureTarget, id, StringComparison.OrdinalIgnoreCase);
        return new KeybindRowViewModel(id, name, hotkey, true, BeginNamedBindCaptureCommand)
        {
            HotkeyDisplay = capturing ? "Press a key…" : (string.IsNullOrWhiteSpace(hotkey) ? "—" : hotkey),
            IsCapturing = capturing
        };
    }

    private static void ReplaceKeybindRows(ObservableCollection<KeybindRowViewModel> target, IReadOnlyList<KeybindRowViewModel> next)
    {
        target.Clear();
        foreach (var row in next)
            target.Add(row);
    }

    /// <summary>Hotkey strings owned by app-level controls; scripts may not use them.</summary>
    public IReadOnlyList<string> ReservedTransportHotkeys => _reservedTransportHotkeys;

    public void SetReservedTransportHotkeys(IEnumerable<string> hotkeys)
    {
        _reservedTransportHotkeys = hotkeys
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool IsReservedTransportHotkey(string? hotkey)
        => !string.IsNullOrWhiteSpace(hotkey)
           && (HotkeyCodec.AreEqual(hotkey, PanicHotkey)
               || _reservedTransportHotkeys.Any(h => HotkeyCodec.AreEqual(h, hotkey)));

    /// <summary>
    /// Moves any script hotkey that collides with a transport control onto a free key,
    /// so pressing it runs the macro instead of starting a recording.
    /// </summary>
    public void ResolveTransportHotkeyConflicts()
    {
        foreach (var script in MacroScripts)
        {
            if (!IsReservedTransportHotkey(script.Hotkey)) continue;

            string previous = script.Hotkey.Trim();
            string replacement = FindFreeHotkey();
            script.Hotkey = replacement;
            if (script == SelectedScript)
                CurrentHotkey = replacement;
            SaveMacroScript(script);

            StatusChanged?.Invoke($"'{script.Name}' used {previous}, which is a transport bind — moved to {replacement}.");
            LogActivity($"'{script.Name}' used {previous}, which is a transport bind — moved to {replacement}.");
        }

        ResolveDuplicateScriptHotkeys();
        AssignMissingScriptHotkeys();
    }

    /// <summary>Two scripts must never share a combo — the later one is moved to a free key.</summary>
    public void ResolveDuplicateScriptHotkeys()
    {
        var seen = new HashSet<(uint Mod, uint Vk)>();
        foreach (var script in MacroScripts)
        {
            if (!HotkeyCodec.TryParse(script.Hotkey, out uint mod, out uint vk))
                continue;
            if (seen.Add((mod, vk)))
                continue;

            string previous = script.Hotkey.Trim();
            string replacement = FindFreeHotkey();
            script.Hotkey = replacement;
            if (script == SelectedScript)
                CurrentHotkey = replacement;
            SaveMacroScript(script);
            StatusChanged?.Invoke($"'{script.Name}' shared {previous} with another script — moved to {replacement}.");
            LogActivity($"'{script.Name}' shared {previous} with another script — moved to {replacement}.");
        }
    }

    /// <summary>
    /// Script binds that collide with Record / Stop / Play / Pause (or each other) become
    /// Ctrl+letter so they can never steal a transport key. Empty binds are filled the same way.
    /// </summary>
    private string FindFreeHotkey()
    {
        bool Taken(string candidate)
            => IsReservedTransportHotkey(candidate)
               || TransportBinds().Any(bind => HotkeyCodec.AreEqual(bind.Hotkey, candidate))
               || MacroScripts.Any(s => !string.IsNullOrWhiteSpace(s.Hotkey) && HotkeyCodec.AreEqual(s.Hotkey, candidate));

        foreach (char c in "1234567890QWERTYUIOPASDFGHJKLZXCVBNM")
        {
            string candidate = $"Ctrl+{c}";
            if (!Taken(candidate)) return candidate;
        }

        foreach (char c in "1234567890QWERTYUIOPASDFGHJKLZXCVBNM")
        {
            string candidate = $"Ctrl+Shift+{c}";
            if (!Taken(candidate)) return candidate;
        }

        for (int i = 1; i <= 24; i++)
        {
            string candidate = $"Ctrl+F{i}";
            if (!Taken(candidate)) return candidate;
        }

        return "Ctrl+Alt+Z";
    }

    /// <summary>Scripts with a blank hotkey get a free Ctrl+ bind so they stay launchable.</summary>
    public void AssignMissingScriptHotkeys()
    {
        foreach (var script in MacroScripts)
        {
            if (!string.IsNullOrWhiteSpace(script.Hotkey) && HotkeyCodec.TryParse(script.Hotkey, out _, out _))
                continue;

            string replacement = FindFreeHotkey();
            script.Hotkey = replacement;
            if (script == SelectedScript)
                CurrentHotkey = replacement;
            SaveMacroScript(script);
            LogActivity($"Assigned '{script.Name}' the free bind {replacement}.");
        }
    }

    /// <summary>
    /// Current mouse movement sampling strategy during recording.
    /// </summary>
    public MouseSamplingMode MouseSamplingMode
    {
        get => _mouseSamplingMode;
        set => SetProperty(ref _mouseSamplingMode, value);
    }

    /// <summary>
    /// Minimum pixel movement threshold for DistanceAndTime sampling mode.
    /// </summary>
    public int MouseMinDistancePx
    {
        get => _mouseMinDistancePx;
        set => SetProperty(ref _mouseMinDistancePx, value);
    }

    /// <summary>
    /// Minimum elapsed time in milliseconds for DistanceAndTime sampling mode.
    /// </summary>
    public double MouseMinTimeMs
    {
        get => _mouseMinTimeMs;
        set => SetProperty(ref _mouseMinTimeMs, value);
    }

    public VirtualizingStepCollection Steps { get; }
    public ActionPropertiesViewModel ActionEditor { get; }
    public ObservableCollection<MacroScriptItemViewModel> MacroScripts { get; } = new();
    public ObservableCollection<MacroScriptItemViewModel> FavoriteScripts { get; } = new();
    public ObservableCollection<string> ActivityLogs { get; } = new();

    public bool HasFavorites => FavoriteScripts.Count > 0;

    public string ScriptSearchText
    {
        get => _scriptSearchText;
        set
        {
            var next = value ?? string.Empty;
            if (SetProperty(ref _scriptSearchText, next))
                ApplyScriptSearch();
        }
    }

    public bool HasScriptSearch => !string.IsNullOrWhiteSpace(_scriptSearchText);

    public int VisibleScriptCount
    {
        get
        {
            if (!HasScriptSearch)
                return MacroScripts.Count;
            int n = 0;
            foreach (var script in MacroScripts)
            {
                if (ScriptMatches(script))
                    n++;
            }

            return n;
        }
    }

    public string ScriptListCountText =>
        HasScriptSearch
            ? $"{VisibleScriptCount} of {MacroScripts.Count} scripts"
            : $"{MacroScripts.Count} scripts";

    public bool HasNoMatchingScripts => HasScriptSearch && VisibleScriptCount == 0;

    public bool HasVisibleFavorites
    {
        get
        {
            if (!HasFavorites)
                return false;
            if (!HasScriptSearch)
                return true;
            foreach (var script in FavoriteScripts)
            {
                if (ScriptMatches(script))
                    return true;
            }

            return false;
        }
    }

    public string ActionSearchText
    {
        get => _actionSearchText;
        set
        {
            var next = value ?? string.Empty;
            if (SetProperty(ref _actionSearchText, next))
                RunActionSearch(jump: true);
        }
    }

    public bool HasActionSearch => !string.IsNullOrWhiteSpace(_actionSearchText);

    public string ActionSearchStatus
    {
        get
        {
            if (!HasActionSearch)
                return string.Empty;
            if (_actionSearchMatchCount == 0)
                return "0 / 0";
            return $"{_actionSearchOrdinal} / {_actionSearchMatchCount}";
        }
    }

    public bool HasActionSearchMatches => HasActionSearch && _actionSearchMatchCount > 0;


    public string SelectedCenterTab
    {
        get => _selectedCenterTab;
        set
        {
            if (SetProperty(ref _selectedCenterTab, value))
            {
                OnPropertyChanged(nameof(IsScriptsTabVisible));
                OnPropertyChanged(nameof(IsHotkeysTabVisible));
                OnPropertyChanged(nameof(IsLogTabVisible));
            }
        }
    }

    public bool IsScriptsTabVisible => SelectedCenterTab == "SCRIPTS";
    public bool IsHotkeysTabVisible => SelectedCenterTab == "HOTKEYS";
    public bool IsLogTabVisible => SelectedCenterTab == "LOG";

    public enum ScriptSortMode
    {
        AlphaNumeric = 0,
        NewestFirst = 1,
        OldestFirst = 2
    }

    private ScriptSortMode _sortMode = ScriptSortMode.AlphaNumeric;

    public ScriptSortMode SortMode
    {
        get => _sortMode;
        set
        {
            if (SetProperty(ref _sortMode, value))
            {
                ApplyScriptSorting();
                OnPropertyChanged(nameof(IsSortNewestFirst));
                OnPropertyChanged(nameof(SortOrderButtonText));
                OnPropertyChanged(nameof(SortOrderTooltip));
            }
        }
    }

    public bool IsSortNewestFirst
    {
        get => _sortMode == ScriptSortMode.NewestFirst;
        set => SortMode = value ? ScriptSortMode.NewestFirst : ScriptSortMode.AlphaNumeric;
    }

    public string SortOrderButtonText => _sortMode switch
    {
        ScriptSortMode.AlphaNumeric => "⇅ A→Z / 1→9",
        ScriptSortMode.NewestFirst => "⇅ Newest First",
        ScriptSortMode.OldestFirst => "⇅ Oldest First",
        _ => "⇅ A→Z / 1→9"
    };

    public string SortOrderTooltip => _sortMode switch
    {
        ScriptSortMode.AlphaNumeric => "Currently sorted: Words (A→Z), Letters (A→Z), Numbers (1→9 at bottom). Click for Newest First.",
        ScriptSortMode.NewestFirst => "Currently sorted: Newest first (Numbered scripts at bottom). Click for Oldest First.",
        ScriptSortMode.OldestFirst => "Currently sorted: Oldest first (Numbered scripts at bottom). Click for Alphabetical & Numeric.",
        _ => "Click to toggle sort order."
    };

    public void ToggleSortOrder()
    {
        SortMode = _sortMode switch
        {
            ScriptSortMode.AlphaNumeric => ScriptSortMode.NewestFirst,
            ScriptSortMode.NewestFirst => ScriptSortMode.OldestFirst,
            ScriptSortMode.OldestFirst => ScriptSortMode.AlphaNumeric,
            _ => ScriptSortMode.AlphaNumeric
        };
    }

    /// <summary>
    /// Checks if a script name ends in a standalone letter suffix (e.g. "Skill Rotation A" -> 'A').
    /// Requires a space before the trailing letter so words ending in that letter are not matched.
    /// </summary>
    private static bool TryExtractTrailingLetter(string? name, out char letter, out string baseName)
    {
        letter = '\0';
        baseName = name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) return false;

        var match = Regex.Match(name.Trim(), @"^(.*?)\s+([a-zA-Z])$");
        if (match.Success)
        {
            baseName = match.Groups[1].Value.Trim();
            letter = char.ToUpperInvariant(match.Groups[2].Value[0]);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Checks if a script name ends in a number (e.g. "Script #1" -> 1, "Macro 12" -> 12).
    /// </summary>
    private static bool TryExtractTrailingNumber(string? name, out int number, out string baseName)
    {
        number = int.MaxValue;
        baseName = name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) return false;

        var match = Regex.Match(name.Trim(), @"^(.*?)(?:#|\s)*(\d+)$");
        if (match.Success && int.TryParse(match.Groups[2].Value, out number))
        {
            baseName = match.Groups[1].Value.TrimEnd('#', ' ');
            return true;
        }
        return false;
    }

    /// <summary>
    /// Categorizes a script into:
    /// 0 = Words (pure words at top)
    /// 1 = Standalone letter suffix ("Skill Rotation A", right above numbered scripts)
    /// 2 = Numbered scripts ("Script #1", always at bottom in order)
    /// </summary>
    private static int GetScriptCategory(string? name)
    {
        if (TryExtractTrailingNumber(name, out _, out _))
            return 2;

        if (TryExtractTrailingLetter(name, out _, out _))
            return 1;

        return 0;
    }

    private class MacroScriptComparer : IComparer<MacroScriptItemViewModel>
    {
        private readonly ScriptSortMode _mode;

        public MacroScriptComparer(ScriptSortMode mode)
        {
            _mode = mode;
        }

        public int Compare(MacroScriptItemViewModel? x, MacroScriptItemViewModel? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return 1;
            if (y is null) return -1;

            int catX = GetScriptCategory(x.Name);
            int catY = GetScriptCategory(y.Name);

            // Group order: Words (0) -> Letter suffixes (1) -> Numbered scripts (2)
            if (catX != catY)
            {
                return catX.CompareTo(catY);
            }

            // Within Group 0 (Words):
            if (catX == 0)
            {
                if (_mode == ScriptSortMode.NewestFirst)
                    return y.CreatedAt.CompareTo(x.CreatedAt);
                if (_mode == ScriptSortMode.OldestFirst)
                    return x.CreatedAt.CompareTo(y.CreatedAt);

                int nameCmp = string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
                return nameCmp != 0 ? nameCmp : y.CreatedAt.CompareTo(x.CreatedAt);
            }

            // Within Group 1 (Letter suffixes: "Skill Rotation A", "Skill Rotation B"):
            if (catX == 1)
            {
                if (_mode == ScriptSortMode.NewestFirst)
                    return y.CreatedAt.CompareTo(x.CreatedAt);
                if (_mode == ScriptSortMode.OldestFirst)
                    return x.CreatedAt.CompareTo(y.CreatedAt);

                TryExtractTrailingLetter(x.Name, out char letX, out string baseNameX);
                TryExtractTrailingLetter(y.Name, out char letY, out string baseNameY);

                int letCmp = letX.CompareTo(letY);
                if (letCmp != 0) return letCmp;

                int baseCmp = string.Compare(baseNameX, baseNameY, StringComparison.OrdinalIgnoreCase);
                return baseCmp != 0 ? baseCmp : y.CreatedAt.CompareTo(x.CreatedAt);
            }

            // Within Group 2 (Numbered: "Script #1", "Script #2"):
            // Numbered scripts are always in numeric order at the bottom
            TryExtractTrailingNumber(x.Name, out int numX, out string numBaseX);
            TryExtractTrailingNumber(y.Name, out int numY, out string numBaseY);

            int numCmp = numX.CompareTo(numY);
            if (numCmp != 0) return numCmp;

            int bCmp = string.Compare(numBaseX, numBaseY, StringComparison.OrdinalIgnoreCase);
            return bCmp != 0 ? bCmp : y.CreatedAt.CompareTo(x.CreatedAt);
        }
    }

    public void ApplyScriptSorting()
    {
        var currentSelected = SelectedScript;
        var sorted = new List<MacroScriptItemViewModel>(MacroScripts);
        sorted.Sort(new MacroScriptComparer(_sortMode));

        for (int i = 0; i < sorted.Count; i++)
        {
            int oldIndex = MacroScripts.IndexOf(sorted[i]);
            if (oldIndex >= 0 && oldIndex != i)
            {
                MacroScripts.Move(oldIndex, i);
            }
        }

        if (currentSelected != null && MacroScripts.Contains(currentSelected))
        {
            SelectedScript = currentSelected;
        }

        SyncFavorites();
    }

    public bool IsEditingMacro
    {
        get => _isEditingMacro;
        set => SetProperty(ref _isEditingMacro, value);
    }

    public string MacroStatusButtonText => IsScriptEnabled ? "Enabled" : "Disabled";

    public string MacroName
    {
        get => _macroName;
        set { if (SetProperty(ref _macroName, value)) MarkPendingEdits(); }
    }

    public string Description
    {
        get => _description;
        set { if (SetProperty(ref _description, value)) MarkPendingEdits(); }
    }

    public string CurrentHotkey
    {
        get => _currentHotkey;
        set => SetProperty(ref _currentHotkey, value);
    }

    public string CurrentLastRun
    {
        get => _currentLastRun;
        set => SetProperty(ref _currentLastRun, value);
    }

        public MacroStepItemViewModel? SelectedStep
    {
        get => _selectedStep;
        set
        {
            if (_rebuildingOutline && value is null) return;
            if (ReferenceEquals(_selectedStep, value))
                return;

            if (_selectedStep != null && ActionEditor != null && !_committingSelection && !IsRunning && !IsRecordingBusy)
            {
                _committingSelection = true;
                try
                {
                    if (!ActionEditor.TryCommit())
                    {
                        StatusChanged?.Invoke(ActionEditor.DraftError ?? "Fix the current action before changing selection.");
                        OnPropertyChanged(nameof(SelectedStep));
                        return;
                    }
                }
                finally
                {
                    _committingSelection = false;
                }
            }

            if (value != null)
            {
                int liveIndex = Steps.IndexOf(value);
                if (liveIndex >= 0)
                    value = Steps[liveIndex];
            }

            if (!ReferenceEquals(_selectedStep, value)) _authoringSelectionRevision++;
            if (SetProperty(ref _selectedStep, value))
            {
                if (value != null)
                {
                    int idx = Steps.IndexOf(value);
                    if (idx >= 0)
                    {
                        Steps.PinIndex(idx);
                        if (HasActionSearch && !_actionSearchNavigating)
                            SyncActionSearchOrdinal(idx);
                    }
                }

                ActionEditor?.LoadFrom(value?.Step);
                OnPropertyChanged(nameof(ShowMacroProperties));
                OnPropertyChanged(nameof(InspectorTitle));
                DuplicateStepCommand?.RaiseCanExecuteChanged();
                MoveToTopCommand?.RaiseCanExecuteChanged();
                MoveToBottomCommand?.RaiseCanExecuteChanged();
                UndoEditCommand?.RaiseCanExecuteChanged();
                RedoEditCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public MacroScriptItemViewModel? SelectedScript
    {
        get => _selectedScript;
        set
        {
            if (_selectedScript == value) return;
            if (IsRecordingBusy)
            {
                StatusChanged?.Invoke("Wait for recording to finish before changing scripts.");
                OnPropertyChanged(nameof(SelectedScript));
                return;
            }

            if (_selectedScript != null)
            {
                if (!TryCommitPendingActionEdits()) return;
                _selectedScript.Name = MacroName;
                _selectedScript.Description = Description;
                CapturePlaybackSettingsTo(_selectedScript);
                _selectedScript.Actions.Clear();
                _selectedScript.Actions.AddRange(_actionStore.Items);
                _selectedScript.Steps.Clear();
                _selectedScript.ActionCount = _selectedScript.Actions.Count;
                if (_isPersistenceEnabled && !TrySaveMacroScript(_selectedScript)) return;
                _editHistory.Clear();
            }

            _authoringSelectionRevision++;
            if (SetProperty(ref _selectedScript, value) && value != null)
            {
                foreach (var s in MacroScripts) s.IsSelected = (s == value);
                _macroName = value.Name;
                _description = value.Description;
                _currentHotkey = value.Hotkey;
                _currentLastRun = value.LastRunText;
                _isScriptEnabled = value.IsActive;
                ApplyPlaybackSettingsFrom(value);
                SaveStateFor(value);

                OnPropertyChanged(nameof(MacroName));
                OnPropertyChanged(nameof(Description));
                OnPropertyChanged(nameof(CurrentHotkey));
                OnPropertyChanged(nameof(CurrentLastRun));
                OnPropertyChanged(nameof(IsScriptEnabled));
                OnPropertyChanged(nameof(MacroStatusButtonText));

                _committingSelection = true;
                try { SelectedStep = null; Steps.ReplaceAll(value.GetActions()); }
                finally { _committingSelection = false; }
                OutlineRows.SetExecution(value.Id == _runningMacroId ? _executingStepId : null);
                OutlineRows.SetFailure(value.Id == _failedMacroId ? _failedStepId : null, LastExecutionError);
                RefreshStepOrder();
                ResetActionSearch();
                LogActivity($"Selected '{value.Name}' ({Steps.Count} actions)");
            }
            RefreshSaveState();
            TargetScriptChanged();
        }
    }

    public double PlaybackSpeed
    {
        get => _playbackSpeed;
        set
        {
            if (SetProperty(ref _playbackSpeed, value))
            {
                if (SelectedScript != null)
                    SelectedScript.PlaybackSpeed = _playbackSpeed;
                OnPropertyChanged(nameof(PlaybackSpeedText));
                MarkPendingEdits();
                LogActivity($"Playback speed set to {PlaybackSpeed:F1}x");
            }
        }
    }

    public string PlaybackSpeedText => $"{PlaybackSpeed:F1}x";

    private bool _repeatUntilStopped;

    public int RepeatCount
    {
        get => _repeatCount;
        set
        {
            int clamped = Math.Max(1, value);
            if (SetProperty(ref _repeatCount, clamped))
            {
                if (SelectedScript != null)
                    SelectedScript.RepeatCount = _repeatCount;
                OnPropertyChanged(nameof(RepeatCountText));
                OnPropertyChanged(nameof(RepeatCountInputText));
                MarkPendingEdits();
                LogActivity($"Repeat count set to {_repeatCount}");
            }
        }
    }

    public string RepeatCountInputText
    {
        get => $"{RepeatCount}";
        set
        {
            if (int.TryParse(value, out int count) && count >= 1)
            {
                RepeatCount = count;
            }
            OnPropertyChanged(nameof(RepeatCountInputText));
        }
    }

    public bool RepeatUntilStopped
    {
        get => _repeatUntilStopped;
        set
        {
            if (SetProperty(ref _repeatUntilStopped, value))
            {
                if (SelectedScript != null)
                    SelectedScript.RepeatUntilStopped = _repeatUntilStopped;
                OnPropertyChanged(nameof(CanEditRepeatCount));
                OnPropertyChanged(nameof(RepeatCountText));
                MarkPendingEdits();
                LogActivity($"Repeat until stopped set to {_repeatUntilStopped}");
            }
        }
    }

    public bool CanEditRepeatCount => !RepeatUntilStopped;

    public string RepeatCountText => RepeatUntilStopped ? "∞" : $"{RepeatCount}";

    public bool IsRecording
    {
        get => _isRecording;
        set
        {
            if (SetProperty(ref _isRecording, value))
            {
                StopCommand?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(EmptyRecordButtonText));
                OnPropertyChanged(nameof(IsRecordingBusy));
                RaiseTransportPresentation();
            }
        }
    }

    public bool IsRecordingBusy => IsRecording || _isStartingRecording
        || (_ownedRecording is { } pending && Volatile.Read(ref pending.NeedsFinalization) != 0 && _recordingPhase == "Faulted")
        || _recordingStopTask is { IsCompleted: false }
        || _recordingConsumerTask is { IsCompleted: false }
        || _recordingCaptureStopTask is { IsCompleted: false }
        || _inputRecorder.IsRecording;

    /// <summary>When true, the action list follows newly recorded steps.</summary>
    public bool IsRecordingFollowEnabled
    {
        get => _isRecordingFollowEnabled;
        set
        {
            if (SetProperty(ref _isRecordingFollowEnabled, value) && value)
                ShowResumeFollowBanner = false;
        }
    }

    public bool ShowResumeFollowBanner
    {
        get => _showResumeFollowBanner;
        set => SetProperty(ref _showResumeFollowBanner, value);
    }

    public ReservedHotkeyRegistry ReservedHotkeys { get; } = new();

    public SettingsViewModel? AppSettings { get; set; }

    public bool ShowActionEditor
    {
        get => _showActionEditor;
        set
        {
            if (SetProperty(ref _showActionEditor, value))
            {
                OnPropertyChanged(nameof(ToggleActionEditorLabel));
                LayoutPrefsChanged?.Invoke();
            }
        }
    }

    public double ActionEditorWidth
    {
        get => _actionEditorWidth;
        set
        {
            double clamped = Math.Clamp(value, 220, 720);
            if (SetProperty(ref _actionEditorWidth, clamped))
                LayoutPrefsChanged?.Invoke();
        }
    }

    /// <summary>When true the Action Editor width tracks the window instead of a pinned width.</summary>
    public bool AutoSizeActionEditor
    {
        get => _autoSizeActionEditor;
        set
        {
            if (SetProperty(ref _autoSizeActionEditor, value))
                LayoutPrefsChanged?.Invoke();
        }
    }

    public string ToggleActionEditorLabel => ShowActionEditor ? "Hide inspector" : "Show inspector";

    public event Action? LayoutPrefsChanged;

    /// <summary>Raised on the UI thread after a recording batch is appended to <see cref="Steps"/>.</summary>
    public event Action? RecordingBatchApplied;

    public bool IsScriptEnabled
    {
        get => _isScriptEnabled;
        set
        {
            if (SetProperty(ref _isScriptEnabled, value))
            {
                OnPropertyChanged(nameof(MacroStatusButtonText));
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(PauseButtonText));
                PauseCommand?.RaiseCanExecuteChanged();
                RunCommand?.RaiseCanExecuteChanged();
                StopCommand?.RaiseCanExecuteChanged();
                RaiseTransportPresentation();
            }
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (SetProperty(ref _isPaused, value))
            {
                OnPropertyChanged(nameof(PauseButtonText));
                StopCommand?.RaiseCanExecuteChanged();
                RaiseTransportPresentation();
            }
        }
    }

    public string PauseButtonText => IsPaused ? "Resume" : "Pause";

    // Telemetry stats
    public int TotalActionsCount => Steps.Count;
    public bool HasSteps => Steps.Count > 0;
    public bool HasNoSteps => Steps.Count == 0;

    /// <summary>Average delay across all DelayStep entries in the current script. Resets to 0 when no steps or no delays.</summary>
    public string AvgDelayMs
    {
        get
        {
            var delays = _actionStore.Items.OfType<MacroSuite.Core.Steps.DelayStep>().ToList();
            if (delays.Count == 0) return "—";
            double avgMs = delays.Average(d => d.Duration.TotalMilliseconds);
            return DurationDisplay.Format(avgMs);
        }
    }

    /// <summary>Estimated total duration: sum of all explicit delays + 50ms overhead per non-delay step.</summary>
    public string DurationSec
    {
        get
        {
            double totalMs = 0;
            foreach (var step in _actionStore.Items)
            {
                if (step is MacroSuite.Core.Steps.DelayStep d)
                    totalMs += d.Duration.TotalMilliseconds;
                else
                    totalMs += 50;
            }
            if (totalMs == 0) return "—";
            return DurationDisplay.Format(totalMs);
        }
    }

    public int TotalRunsCount
    {
        get => _totalRunsCount;
        set => SetProperty(ref _totalRunsCount, value);
    }

    // Commands
    public RelayCommand AddClickCommand { get; }
    public RelayCommand AddKeypressCommand { get; }
    public RelayCommand AddDelayCommand { get; }
    public RelayCommand AddAIStepCommand { get; }
    public RelayCommand AddColorConditionCommand { get; }
    public RelayCommand AddImageCommand { get; }
    public RelayCommand AddVisualWaitCommand { get; }
    public RelayCommand AddIfCommand { get; }
    public RelayCommand AddWhileCommand { get; }
    public RelayCommand AddRepeatUntilCommand { get; }
    public RelayCommand AddWaitWhileCommand { get; }
    public RelayCommand AddRepeatUntilGoneCommand { get; }
    public RelayCommand ToggleActionEditorCommand { get; }
    public RelayCommand BeginQuickBindCaptureCommand { get; }
    public RelayCommand BeginNamedBindCaptureCommand { get; }
    public RelayCommand InsertActionKindCommand { get; }
    public RelayCommand ChangeActionKindCommand { get; }
    public RelayCommand JumpToTopCommand { get; }
    public RelayCommand JumpToBottomCommand { get; }
    public RelayCommand ResumeRecordingFollowCommand { get; }
    public RelayCommand RemoveStepCommand { get; }
    public RelayCommand DuplicateStepCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand MoveToTopCommand { get; }
    public RelayCommand MoveToBottomCommand { get; }
    public RelayCommand UndoEditCommand { get; }
    public RelayCommand RedoEditCommand { get; }
    public RelayCommand SaveMacroCommand { get; }
    public RelayCommand LoadMacroCommand { get; }
    public RelayCommand RunCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand PanicCommand { get; }
    public RelayCommand NewMacroCommand { get; }
    public RelayCommand SelectScriptCommand { get; }
    public RelayCommand SelectPreviousMacroCommand { get; }
    public RelayCommand SelectNextMacroCommand { get; }
    public RelayCommand PlaySelectedMacroCommand { get; }
    public RelayCommand TogglePlayPauseMacroCommand { get; }
    public RelayCommand DeleteSelectedMacroCommand { get; }
    public RelayCommand CancelMacroCommand { get; }
    public RelayCommand RecordCommand { get; }
    public RelayCommand EmptyRecordCommand { get; }
    public RelayCommand DeleteMacroCommand { get; }
    public RelayCommand ToggleMacroStatusCommand { get; }
    public RelayCommand EditMacroCommand { get; }
    public RelayCommand SaveEditCommand { get; }
    public RelayCommand CancelEditCommand { get; }
    public RelayCommand ShowScriptsTabCommand { get; }
    public RelayCommand ShowHotkeysTabCommand { get; }
    public RelayCommand ShowLogTabCommand { get; }
    public RelayCommand ClearLogsCommand { get; }
    public RelayCommand DeleteScriptItemCommand { get; }
    public RelayCommand EditScriptItemCommand { get; }
    public RelayCommand RunScriptItemCommand { get; }
    public RelayCommand SelectHotkeyPresetCommand { get; }
    public RelayCommand ExportMacroCommand { get; }
    public RelayCommand ToggleSortOrderCommand { get; }
    public RelayCommand ClearScriptSearchCommand { get; }
    public RelayCommand ClearActionSearchCommand { get; }
    public RelayCommand FindNextActionCommand { get; }
    public RelayCommand FindPrevActionCommand { get; }
    public RelayCommand FavoriteScriptCommand { get; }
    public RelayCommand ToggleScriptItemActiveCommand { get; }

    /// <summary>Telemetry diagnostic: count of events captured in staging queue.</summary>
    public int EventsCapturedCount => _totalEventsCaptured;
    /// <summary>Telemetry diagnostic: current queue backlog length.</summary>
    public int QueueLength => _recordedStagingQueue.Count;

    public event Action<string>? StatusChanged;
    public event Action? HotkeysUpdated;

    /// <summary>
    /// hotkey, previous owner display name, new owner display name.
    /// Return true to unbind the previous owner and continue.
    /// </summary>
    public Func<HotkeyConflictPrompt, HotkeyConflictChoice>? ConfirmHotkeyConflictPrompter { get; set; }

    public ObservableCollection<KeybindRowViewModel> AppKeybinds { get; } = new();
    public ObservableCollection<KeybindRowViewModel> MacroKeybinds { get; } = new();
    public int RecordingGracePeriodMs { get; set; } = 150;

    public MacroEditorViewModel(
        IScreenCaptureService captureService,
        IInputPlayer inputPlayer,
        AIVisionRouter visionRouter,
        IApprovalGate approvalGate,
        IFeatureGate featureGate,
        UsageTracker usageTracker,
        IAuditLogger auditLogger,
        bool seedDemoData = false,
        IImageMatcher? imageMatcher = null,
        string? customMacrosDirectory = null,
        IInputRecorder? inputRecorder = null,
        ILocationPicker? locationPicker = null,
        IImageEncoder? imageEncoder = null,
        IClickRefiner? clickRefiner = null,
        IRecordingDesktopState? recordingDesktop = null,
        IMacroDocumentStore? documentStore = null,
        bool deferLibraryLoad = false,
        HostInputOperationArbiter? inputOperations = null)
    {
        InputOperations = inputOperations ?? new HostInputOperationArbiter();
        _ownsInputOperations = inputOperations is null;
        _captureService = captureService ?? throw new ArgumentNullException(nameof(captureService));
        _inputPlayer = inputPlayer ?? throw new ArgumentNullException(nameof(inputPlayer));
        _visionRouter = visionRouter ?? throw new ArgumentNullException(nameof(visionRouter));
        _approvalGate = approvalGate ?? throw new ArgumentNullException(nameof(approvalGate));
        _featureGate = featureGate ?? throw new ArgumentNullException(nameof(featureGate));
        _usageTracker = usageTracker ?? throw new ArgumentNullException(nameof(usageTracker));
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _imageMatcher = imageMatcher ?? new OpenCvMatcher();
        _imageEncoder = imageEncoder ?? new OpenCvImageEncoder();
        _clickRefiner = clickRefiner ?? new OpenCvClickRefiner();
        _inputRecorder = inputRecorder ?? new LowLevelHookService();
        _recordingDesktop = recordingDesktop ?? new RecordingDesktopState();
        _locationPicker = locationPicker;
        _customMacrosDirectory = customMacrosDirectory;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        _uiDispatcher = dispatcher != null && dispatcher.CheckAccess() ? dispatcher : null;
        _isPersistenceEnabled = !string.IsNullOrWhiteSpace(customMacrosDirectory) || seedDemoData;
        _macrosDirectory = !string.IsNullOrWhiteSpace(customMacrosDirectory)
            ? customMacrosDirectory
            : AppDataPathResolver.Default.MacrosDirectory;
        InitializeProtectedStorage(documentStore, deferLibraryLoad);

        Steps = new VirtualizingStepCollection(_actionStore);
        ActionEditor = new ActionPropertiesViewModel(this);
        InitializeEditorPresentation();

        AddClickCommand = new RelayCommand(AddClickStep);
        AddKeypressCommand = new RelayCommand(AddKeypressStep);
        AddDelayCommand = new RelayCommand(AddDelayStep);
        AddAIStepCommand = new RelayCommand(AddAIStep);
        AddColorConditionCommand = new RelayCommand(AddColorConditionStep);
        AddImageCommand = new RelayCommand(AddImageStep);
        AddVisualWaitCommand = new RelayCommand(AddVisualWaitStep);
        AddIfCommand = new RelayCommand(AddIfStep);
        AddWhileCommand = new RelayCommand(AddWhileStep);
        AddRepeatUntilCommand = new RelayCommand(AddRepeatUntilStep);
        AddWaitWhileCommand = new RelayCommand(AddWaitWhileStep);
        AddRepeatUntilGoneCommand = new RelayCommand(AddRepeatUntilGoneStep);
        ToggleActionEditorCommand = new RelayCommand(() => ShowActionEditor = !ShowActionEditor);
        BeginQuickBindCaptureCommand = new RelayCommand(BeginQuickBindCapture);
        BeginNamedBindCaptureCommand = new RelayCommand(BeginQuickBindCapture);
        InsertActionKindCommand = new RelayCommand(p =>
        {
            int kind = ParseKindParameter(p);
            if (kind >= 0)
                InsertDefaultAction((EditableActionKind)kind);
        }, _ => !IsEditingLocked);
        ChangeActionKindCommand = new RelayCommand(p =>
        {
            int kind = p switch
            {
                int i => i,
                string s when int.TryParse(s, out int n) => n,
                EditableActionKind e => (int)e,
                _ => -1
            };
            if (kind >= 0)
                ChangeSelectedActionKind((EditableActionKind)kind);
        }, _ => SelectedStep != null && !IsEditingLocked);
        JumpToTopCommand = new RelayCommand(JumpToTop, () => Steps.Count > 0);
        JumpToBottomCommand = new RelayCommand(JumpToBottom, () => Steps.Count > 0);
        ResumeRecordingFollowCommand = new RelayCommand(ResumeRecordingFollow);
        RemoveStepCommand = new RelayCommand(RemoveSelectedStep, () => SelectedStep != null && !IsEditingLocked);
        DuplicateStepCommand = new RelayCommand(DuplicateSelectedStep, () => SelectedStep != null && !IsEditingLocked);
        MoveUpCommand = new RelayCommand(MoveStepUp, () => CanMoveSelection(false));
        MoveDownCommand = new RelayCommand(MoveStepDown, () => CanMoveSelection(true));
        MoveToTopCommand = new RelayCommand(MoveStepToTop, () => CanMoveSelection(false));
        MoveToBottomCommand = new RelayCommand(MoveStepToBottom, () => CanMoveSelection(true));
        UndoEditCommand = new RelayCommand(UndoEdit, () => _editHistory.CanUndo && !IsEditingLocked);
        RedoEditCommand = new RelayCommand(RedoEdit, () => _editHistory.CanRedo && !IsEditingLocked);
        SaveMacroCommand = new RelayCommand(SaveMacro);
        LoadMacroCommand = new RelayCommand(LoadMacro, () => CanImportMacro);
        RunCommand = new RelayCommand(async () => await RunMacroAsync(), () => HasActivation && !_disposed && !IsRunning && !IsEditingLocked && Steps.Count > 0);
        PauseCommand = new RelayCommand(async () => await PauseMacroAsync(), () => IsRunning || IsRecording);
        StopCommand = new RelayCommand(async () => await StopMacroAsync(), () => IsRunning || IsRecordingBusy || IsPaused
            || _ownedEditorPlayback?.Lease.IsCurrent == true || _recordingInputOperation?.IsCurrent == true
            || _producerAuthoringRequest is not null);
        PanicCommand = new RelayCommand(PanicMacro);
        NewMacroCommand = new RelayCommand(CreateNewMacro);
        SelectScriptCommand = new RelayCommand(param =>
        {
            if (param is MacroScriptItemViewModel s)
            {
                SelectedScript = s;
            }
            else
            {
                SelectCurrentScript();
            }
        });
        SelectPreviousMacroCommand = new RelayCommand(SelectPreviousMacro);
        SelectNextMacroCommand = new RelayCommand(SelectNextMacro);
        PlaySelectedMacroCommand = new RelayCommand(async () =>
        {
            if (SelectedScript != null)
            {
                if (IsRunning) await StopMacroAsync();
                else if (RunCommand.CanExecute(null)) await RunMacroAsync();
            }
        });
        TogglePlayPauseMacroCommand = new RelayCommand(async () =>
        {
            if (IsRunning) await PauseMacroAsync();
            else if (SelectedScript != null && RunCommand.CanExecute(null)) await RunMacroAsync();
        });
        DeleteSelectedMacroCommand = new RelayCommand(() =>
        {
            if (SelectedScript != null) DeleteScript(SelectedScript);
        });
        CancelMacroCommand = new RelayCommand(async () =>
        {
            if (ActionEditor.IsPickingLocation)
            {
                CancelLocationPick();
                return;
            }
            await StopMacroAsync();
        });
        RecordCommand = new RelayCommand(ToggleRecording, () => IsRecordingBusy || CanEditMacro);
        EmptyRecordCommand = new RelayCommand(() =>
        {
            if (IsRecording || IsRunning || IsPaused)
                _ = StopMacroAsync();
            else
                ToggleRecording();
        });
        DeleteMacroCommand = new RelayCommand(DeleteCurrentMacro);
        ToggleMacroStatusCommand = new RelayCommand(ToggleMacroStatus);
        EditMacroCommand = new RelayCommand(EditCurrentMacro);
        SaveEditCommand = new RelayCommand(SaveEdit);
        CancelEditCommand = new RelayCommand(() => IsEditingMacro = false);
        ShowScriptsTabCommand = new RelayCommand(() => SelectedCenterTab = "SCRIPTS");
        ShowHotkeysTabCommand = new RelayCommand(() => SelectedCenterTab = "HOTKEYS");
        ShowLogTabCommand = new RelayCommand(() => SelectedCenterTab = "LOG");
        ClearLogsCommand = new RelayCommand(() => ActivityLogs.Clear());

        DeleteScriptItemCommand = new RelayCommand(param =>
        {
            if (param is MacroScriptItemViewModel s) DeleteScript(s);
            else DeleteCurrentMacro();
        });
        EditScriptItemCommand = new RelayCommand(param =>
        {
            if (param is MacroScriptItemViewModel s) EditScript(s);
            else EditCurrentMacro();
        });
        RunScriptItemCommand = new RelayCommand(async param =>
        {
            if (param is MacroScriptItemViewModel s)
            {
                await ToggleScriptAsync(s);
            }
            else
            {
                await ToggleScriptAsync(SelectedScript);
            }
        });
        SelectHotkeyPresetCommand = new RelayCommand(param =>
        {
            if (param is string preset && !string.IsNullOrWhiteSpace(preset))
            {
                TryChangeHotkey(preset);
            }
        });
        ExportMacroCommand = new RelayCommand(ExportMacro);
        ToggleSortOrderCommand = new RelayCommand(ToggleSortOrder);
        ClearScriptSearchCommand = new RelayCommand(() => ScriptSearchText = string.Empty);
        ClearActionSearchCommand = new RelayCommand(() => ActionSearchText = string.Empty);
        FindNextActionCommand = new RelayCommand(() => FindActionMatch(forward: true), () => HasActionSearchMatches);
        FindPrevActionCommand = new RelayCommand(() => FindActionMatch(forward: false), () => HasActionSearchMatches);
        FavoriteScriptCommand = new RelayCommand(param =>
        {
            var target = param as MacroScriptItemViewModel ?? SelectedScript;
            if (target == null) return;
            target.IsFavorite = !target.IsFavorite;
            SyncFavorites();
            SaveMacroScript(target);
            LogActivity($"'{target.Name}' {(target.IsFavorite ? "added to" : "removed from")} Favorites.");
        });
        ToggleScriptItemActiveCommand = new RelayCommand(param =>
        {
            var target = param as MacroScriptItemViewModel ?? SelectedScript;
            if (target == null) return;
            target.IsActive = !target.IsActive;
            if (target == SelectedScript)
            {
                _isScriptEnabled = target.IsActive;
                OnPropertyChanged(nameof(IsScriptEnabled));
                OnPropertyChanged(nameof(MacroStatusButtonText));
            }
            SaveMacroScript(target);
            LogActivity($"'{target.Name}' status toggled to {(target.IsActive ? "Active" : "Disabled")}.");
        });

        // Keep FavoriteScripts in sync whenever the main collection or any item's IsFavorite changes
        MacroScripts.CollectionChanged += (_, e) =>
        {
            CompanionScriptsChanged(e);
            if (e.NewItems != null)
            {
                foreach (MacroScriptItemViewModel item in e.NewItems)
                {
                    item.PropertyChanged += (s, pe) =>
                    {
                        if (pe.PropertyName == nameof(MacroScriptItemViewModel.IsFavorite))
                        {
                            SyncFavorites();
                            OnPropertyChanged(nameof(HasFavorites));
                        }
                        if (pe.PropertyName is nameof(MacroScriptItemViewModel.Hotkey)
                            or nameof(MacroScriptItemViewModel.Name))
                            RefreshKeybindRows();
                    };
                }
            }
            SyncFavorites();
            OnPropertyChanged(nameof(HasFavorites));
            OnPropertyChanged(nameof(HasVisibleFavorites));
            OnPropertyChanged(nameof(VisibleScriptCount));
            OnPropertyChanged(nameof(ScriptListCountText));
            OnPropertyChanged(nameof(HasNoMatchingScripts));
            RefreshKeybindRows();
        };

        _scriptsView = CollectionViewSource.GetDefaultView(MacroScripts);
        _favoritesView = CollectionViewSource.GetDefaultView(FavoriteScripts);

        LogActivity($"Macro Engine initialized. v{BuildEnvironment.Version} ready.");
        if (!deferLibraryLoad) InitializeScriptsLibrary(seedDemoData);
        RefreshKeybindRows();
    }

    public void LogActivity(string message)
    {
        // ActivityLogs is bound to a virtualizing ListBox; appending from the recording
        // consumer or a playback task desynchronizes the item generator and crashes render.
        if (TryPostToUi(() => LogActivity(message))) return;

        var time = DateTime.Now.ToString("HH:mm:ss");
        ActivityLogs.Insert(0, $"[{time}] {message}");
        if (ActivityLogs.Count > 120) ActivityLogs.RemoveAt(ActivityLogs.Count - 1);
    }

    /// <summary>Rebuilds FavoriteScripts to mirror the favorited subset of MacroScripts.</summary>
    private void SyncFavorites()
    {
        // Remove entries no longer favorite or no longer in MacroScripts
        for (int i = FavoriteScripts.Count - 1; i >= 0; i--)
        {
            var item = FavoriteScripts[i];
            if (!item.IsFavorite || !MacroScripts.Contains(item))
                FavoriteScripts.RemoveAt(i);
        }
        // Add new favorites in their MacroScripts order
        foreach (var item in MacroScripts)
        {
            if (item.IsFavorite && !FavoriteScripts.Contains(item))
                FavoriteScripts.Add(item);
        }
        OnPropertyChanged(nameof(HasFavorites));
        OnPropertyChanged(nameof(HasVisibleFavorites));
    }

    public void ToggleRecording()
    {
        _recordingCommandTask = ObserveRecordingCommandAsync();
    }

    private Task? _recordingCommandTask;
    public Task ToggleRecordingAsync() => IsRecordingBusy || _recordingInputOperation is { Completion.IsCompleted: false }
        || _producerAuthoringRequest?.Kind == HostInputOperationKind.Recording
        ? StopRecordingAsync() : StartRecordingAsync();
    private async Task ObserveRecordingCommandAsync()
    {
        try { await ToggleRecordingAsync(); }
        catch (Exception ex) { NotifyStatus($"Recording control failed: {ex.Message}"); LogActivity($"Recording control failed: {ex.Message}"); }
    }

    public Task StartRecordingAsync()
    {
        if (_disposed || _shutdownStarted) return Task.CompletedTask;
        if (_recordingInputOperation is { Completion.IsCompleted: false }) return _recordingInputStartup ?? Task.CompletedTask;
        if (_producerAuthoringRequest is { Kind: HostInputOperationKind.Recording } pending) return pending.Work;
        if (SelectedScript is not null && ActionEditor.RequiresImagePreparation)
            return StartProducerAfterImageDraft(HostInputOperationKind.Recording, SelectedScript);
        return StartRecordingWithGenerationAsync(InputOperations.StopGeneration);
    }

    private Task StartRecordingWithGenerationAsync(long generation, ProducerTargetPin? targetPin = null,
        MacroScriptItemViewModel? pinnedDocument = null)
    {
        if (_disposed || _shutdownStarted) return Task.CompletedTask;
        if (!InputOperations.TryAcquire(HostInputOperationKind.Recording, generation, out var lease, out string reason))
        { NotifyStatus(reason); return Task.CompletedTask; }
        _recordingInputOperation = lease;
        Task work = _recordingInputStartup = StartEditorRecordingOwnedAsync(lease!, targetPin, pinnedDocument);
        RefreshInputOwnershipPresentation();
        return work;
    }

    private async Task StartRecordingCoreAsync(HostInputOperationLease inputLease, ProducerTargetPin? targetPin,
        MacroScriptItemViewModel? pinnedDocument)
    {
        if (!EnsureEmergencyStopReady()) return;
        if (_ownedRecording is { DestinationRevisionConflict: true } conflict && ReferenceEquals(conflict.Destination, SelectedScript))
        { NotifyStatus("The destination changed outside this recording. Export the retained recording or restart and recover it before recording again."); return; }
        if (LibraryKeyUnavailable) { NotifyStatus("Restore an encrypted backup before recording into a library whose key is unavailable."); return; }
        if (ActionEditor.IsTestingDetection)
        {
            NotifyStatus("Finish or cancel the detection test before recording.");
            return;
        }
        if (ExternalInputIsActive?.Invoke() == true)
        {
            NotifyStatus("Stop Auto Clicker before recording a macro.");
            return;
        }
        if (_disposed || _shutdownStarted || IsRecording || _isStartingRecording || _recordingStopTask is { IsCompleted: false }
            || _recordingConsumerTask is { IsCompleted: false }
            || _recordingCaptureStopTask is { IsCompleted: false } || _inputRecorder.IsRecording) return;
        if (IsRunning)
        {
            StatusChanged?.Invoke("Cannot record while macro playback is active. Stop playback first.");
            LogActivity("Recording blocked: playback is active.");
            return;
        }

        // Always record into the script the user has selected. A new script is only
        // created when nothing is selected.
        if (SelectedScript != null)
        {
            if (!CommitOwnedInputDraft()) { NotifyStatus(ActionEditor.DraftError ?? "Fix the pending action before recording."); return; }
            if (CommitVisibleRecordingFields?.Invoke() == false) return;
        }
        OwnedRecording session;
        try
        {
            lock (_recordingLifecycleGate)
            {
                if (IsRecordingBusy || _disposed || _shutdownStarted || !inputLease.CanContinue
                    || targetPin is not null && (!ReferenceEquals(SelectedScript, pinnedDocument)
                        || !Equals(SelectedScript?.Target, targetPin.RequestedRule))) return;
                session = ClaimRecording(targetPin);
                session.InputLease = inputLease;
                session.InputStop = inputLease.StopToken.Register(static value =>
                {
                    var current = (OwnedRecording)value!;
                    Interlocked.Exchange(ref current.Closed, 1); current.StopSignal.TrySetResult();
                }, session);
                _recordingCts?.Dispose();
                _recordingCts = CancellationTokenSource.CreateLinkedTokenSource(inputLease.StopToken);
                _isStartingRecording = true;
                _recordingStartCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                SetRecordingPhase("Starting");
            }
        }
        catch (Exception ex) { NotifyStatus(ex.Message); return; }

        _editHistory.Clear();
        var ct = _recordingCts!.Token;
        var channel = new RecordingInputBuffer(admit: input => AdmitRecordingInput(session, input),
            shortcuts: session.Shortcuts.Take(5), miniShortcut: session.Shortcuts[5], miniActive: () => IsMiniToolbarVisible);
        _recordingInputBuffer = channel;
        _recordingFailure = null;
        _recordingStartTick = Stopwatch.GetTimestamp();
        _recordingTimelineOrigin = session.AppendOffset;
        NotifyRecordingBusy();

        session.SignalWatcher = ObserveRecordingStopSignalAsync(session);
        try
        {
            if (!await EnsureActivationForOperationAsync(ct)) throw new OperationCanceledException("Recording was not authorized.");
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(session, _ownedRecording) || Volatile.Read(ref session.Closed) != 0) throw new OperationCanceledException(ct);
            AttachAuthorizedRecordingDestination(session);
            if (_isPersistenceEnabled && !_libraryLoadDeferred)
                await CaptureRecordingFileRevisionAsync(session, ct);
            if (_isPersistenceEnabled && _documentStore is IRecordingRecoveryProvider provider)
                session.Journal = await provider.RecordingRecovery.BeginAsync(new(session.Id, session.Baseline, session.AppendOffset.Ticks,
                    SettingsSnapshot: System.Text.Json.JsonSerializer.Serialize(new {
                        CameraPlaybackHz = session.PlaybackHz, CameraLookGainX = session.GainX,
                        CameraLookGainY = session.GainY, CameraPlaybackMode = session.CameraMode,
                        MouseSamplingMode = session.SamplingMode, MouseMinDistancePx = session.MinDistancePx,
                        MouseMinTimeMs = session.MinTimeMs })), ct);
            ct.ThrowIfCancellationRequested();
            session.Desktop = new RecordingDesktopSnapshot(_recordingDesktop);
            session.StartTick = _recordingStartTick = Stopwatch.GetTimestamp(); session.LastTick = session.StartTick;
            _totalEventsCaptured = 0;
            // Start the consumer before native startup so accepted input survives startup cancellation.
            Interlocked.Exchange(ref session.NeedsFinalization, 1);
            _recordingConsumerTask = Task.Run(() => ProcessRecordedEventsAsync(channel.CapturedReader, CancellationToken.None));
            if (_inputRecorder is LowLevelHookService nativeRecorder)
                nativeRecorder.UseRawMouseInput = session.UseRawMouseInput;
            if (_inputRecorder is LowLevelHookService)
                await Task.Run(() => _inputRecorder.StartAsync(channel, ct), ct);
            else
                await _inputRecorder.StartAsync(channel, ct);
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);

            RecordingWindowFilter.BindToCurrentProcess();
            InputModeGate.Set(InputMode.Recording);
            IsRecording = true;
            // Start draining before UI callbacks: if overlay setup fails, accepted
            // capture still has a consumer and can be shut down without losing its tail.
            SetRecordingPhase("Recording");
            IsRecordingFollowEnabled = true;
            ShowResumeFollowBanner = false;
            EnterCompactOverlay?.Invoke();
            StatusChanged?.Invoke($"🔴 RECORDING '{MacroName}' — Move mouse, click, or type. Press {StopHotkey} to finish.");
            LogActivity($"Recording into '{MacroName}'. New actions will be added to this script.");

            // Batched UI: 10 Hz feels live while still coalescing layout work.
            _uiUpdateTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _uiUpdateTimer.Tick += OnRecordingUiTimerTick;
            _uiUpdateTimer.Start();

            _isStartingRecording = false;
            _recordingStartCompletion.TrySetResult();
            NotifyRecordingBusy();
        }
        catch (Exception ex)
        {
            _recordingFailure = ex is OperationCanceledException ? "Recording start cancelled." : $"Recording error: {ex.Message}";
            try
            {
                await StopInputCaptureAsync();
            }
            catch (Exception stopError)
            {
                _recordingFailure += $" Capture cleanup failed: {stopError.Message}";
            }
            finally { CloseRecordingAdmission(); }
            if (_uiUpdateTimer != null)
            {
                _uiUpdateTimer.Tick -= OnRecordingUiTimerTick;
                _uiUpdateTimer.Stop();
                _uiUpdateTimer = null;
            }
            IsRecording = false;
            SetRecordingPhase("Stopping");
            bool drained = false;
            try
            {
                if (_recordingConsumerTask != null)
                    await _recordingConsumerTask.WaitAsync(TimeSpan.FromSeconds(5));
                drained = true;
            }
            catch (Exception drainError)
            {
                _recordingFailure += $" Recording drain failed; retained data may be incomplete: {drainError.Message}";
            }
            try
            {
                if (inputLease.OwnsCleanup && InputModeGate.Current == InputMode.Recording) InputModeGate.Set(InputMode.Normal);
                if (!_disposed)
                {
                    // Restore before encryption or disk I/O, including failed overlay startup.
                    try { RequestRecordingEditorRestore(); }
                    catch (Exception restoreError) { _recordingFailure += $" Editor restoration failed: {restoreError.Message}"; }
                    if (drained)
                    {
                        RecordingSaveRequest? save = CompleteStopRecordingOnUi();
                        if (save != null && _recordingInputBuffer?.LastAcceptedSequence > 0)
                            await SaveRecordingSnapshotAsync(save);
                    }
                }
                if (session.Desktop != null) await session.Desktop.DisposeAsync();
                if (drained && session.Journal != null)
                {
                    try
                    {
                        if ((_recordingInputBuffer?.LastAcceptedSequence ?? 0) == 0)
                            await session.Journal.AbortIfEmptyAsync();
                        await session.Journal.DisposeAsync();
                    }
                    catch (Exception journalError) { _recordingFailure += " " + journalError.Message; }
                }
            }
            finally
            {
                if (drained) Interlocked.Exchange(ref session.NeedsFinalization, 0);
                _recordingTerminalReason = _recordingFailure;
                SetRecordingPhase(!drained || _recordingCaptureStopTask is { IsCompleted: false } || ex is not OperationCanceledException ? "Faulted" : "Idle");
                _isStartingRecording = false;
                _recordingStartCompletion.TrySetResult();
                NotifyRecordingBusy();
                if (!_disposed) { LogActivity(_recordingFailure); StatusChanged?.Invoke(_recordingFailure); }
            }
            if (session.SignalWatcher != null) await session.SignalWatcher;
        }
    }

    private void OnRecordingUiTimerTick(object? sender, EventArgs e)
    {
        // Keep the live counters moving even while every event is being filtered out,
        // so an active recorder never looks frozen.
        OnPropertyChanged(nameof(EventsCapturedCount));
        OnPropertyChanged(nameof(TransportStateText));
        OnPropertyChanged(nameof(QueueLength));
        OnPropertyChanged(nameof(RecordingCheckpointText));
        if (IsRecordingPaused && _recordingPhase == "Recording") SetRecordingPhase("Paused");

        if (_recordedStagingQueue.IsEmpty) return;

        int added = 0;
        const int maxAddsPerTick = 250;
        var batch = new List<MacroStep>(64);
        while (added < maxAddsPerTick && _recordedStagingQueue.TryDequeue(out var step))
        {
            batch.Add(step);
            added++;
        }

        if (added == 0) return;

        // Do not Reset the virtualizing list while recording — the studio is hidden and
        // a Reset storm is what freezes WPF after returning from Mini Mode.
        Steps.AddSteps(batch, notify: false);

        if (SelectedScript != null)
            SelectedScript.ActionCount = Steps.Count;

        string? warn = MacroSizePolicy.WarningForCount(Steps.Count);
        if (warn != null)
            StatusChanged?.Invoke(warn);

        OnPropertyChanged(nameof(TotalActionsCount));
        OnPropertyChanged(nameof(HasSteps));
        OnPropertyChanged(nameof(HasNoSteps));
        RecordingBatchApplied?.Invoke();
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread. Recording events arrive on a
    /// background consumer; mutating <see cref="Steps"/> or raising PropertyChanged from
    /// there corrupts the virtualizing list ("ItemsControl is inconsistent with its items source").
    /// </summary>
    private bool TryPostToUi(Action action)
    {
        try
        {
            var dispatcher = _uiDispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                return false;
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                return false;
            if (!dispatcher.Thread.IsAlive)
                return false;

            dispatcher.BeginInvoke(action);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private Task StopRecordingCoreAsync()
    {
        var inputLease = _recordingInputOperation;
        if (inputLease is not null && InputOperations.Owns(inputLease))
        {
            if (inputLease.Completion.IsCompleted) return Task.CompletedTask;
            inputLease.SignalStop();
            if (_ownedRecording?.InputLease != inputLease && _recordingInputStartup is { IsCompleted: false })
                return _recordingInputStartup;
        }
        lock (_recordingLifecycleGate)
        {
            if (_recordingStopTask is { IsCompleted: false }) return _recordingStopTask;
            CloseRecordingAdmission();
            if (_isStartingRecording)
            {
                _recordingCts?.Cancel();
                return _recordingStopTask = WaitForRecordingStartToStopAsync();
            }
            if (!IsRecording && (_ownedRecording is not { } pending || Volatile.Read(ref pending.NeedsFinalization) == 0)) return Task.CompletedTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _recordingStopTask = completion.Task;
            // The hook-owner signal may arrive on a worker. Close capture immediately,
            // but publish WPF notifications only on the editor dispatcher.
            Volatile.Write(ref _isRecording, false);
            SetRecordingPhase("Stopping");
            NotifyRecordingBusy();
            // Keep a task that callers can await even when shutdown has to cross the UI dispatcher.
            if (!TryPostToUi(() => CompleteRecordingStopAsync(completion)))
                CompleteRecordingStopAsync(completion);
            return _recordingStopTask;
        }
    }

    private async Task WaitForRecordingStartToStopAsync()
    {
        try
        {
            if (_recordingStartCompletion != null)
                await _recordingStartCompletion.Task.WaitAsync(TimeSpan.FromSeconds(11));
            // Startup observes the cancelled session before publishing Recording. If a
            // backend nevertheless completed concurrently, finalize directly rather than
            // awaiting this same shared stop task recursively.
            if (IsRecording)
            {
                Volatile.Write(ref _isRecording, false);
                SetRecordingPhase("Stopping"); NotifyRecordingBusy();
                if (_uiDispatcher != null && !_uiDispatcher.CheckAccess())
                    await (await _uiDispatcher.InvokeAsync(FinishStopRecordingAsync));
                else await FinishStopRecordingAsync();
            }
        }
        catch (TimeoutException)
        {
            const string message = "Recording startup is still stopping. Another recording cannot start until capture has stopped.";
            SetRecordingPhase("Faulted");
            void Publish() { NotifyStatus(message); LogActivity(message); }
            if (!TryPostToUi(Publish)) Publish();
            throw;
        }
    }

    private async Task StopInputCaptureAsync()
    {
        if (_recordingCaptureStopTask is not { IsCompleted: false })
            _recordingCaptureStopTask = _inputRecorder is LowLevelHookService
                ? Task.Run(_inputRecorder.StopAsync, CancellationToken.None)
                : _inputRecorder.StopAsync();
        await _recordingCaptureStopTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    private void CompleteRecordingStopAsync(TaskCompletionSource completion)
    {
        _ = FinishAndReportAsync();
        async Task FinishAndReportAsync()
        {
            try
            {
                await FinishStopRecordingAsync();
                await FinishRecordingInputOwnershipAsync();
                completion.TrySetResult();
                NotifyRecordingBusy();
            }
            catch (Exception ex)
            {
                _recordingFailure = ex.Message;
                _recordingTerminalReason = _recordingFailure;
                SetRecordingPhase("Faulted");
                if (!_disposed && !_shutdownStarted && !IsRunning)
                {
                    try { if (!TryPostToUi(() => RequestRecordingEditorRestore())) RequestRecordingEditorRestore(); }
                    catch (Exception restoreError) { _recordingFailure += $" Editor restoration failed: {restoreError.Message}"; }
                }
                StatusChanged?.Invoke($"Recording stop error: {ex.Message}");
                LogActivity($"Recording stop error: {ex.Message}");
                await FinishRecordingInputOwnershipAsync();
                completion.TrySetException(ex);
                NotifyRecordingBusy();
            }
        }
    }

    private void NotifyRecordingBusy()
    {
        void Notify()
        {
            if (_disposed) return;
            OnPropertyChanged(nameof(IsRecording));
            OnPropertyChanged(nameof(IsRecordingBusy));
            StopCommand?.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(EmptyRecordButtonText));
            RaiseTransportPresentation();
        }
        if (!TryPostToUi(Notify)) Notify();
    }

    private async Task FinishStopRecordingAsync()
    {
        var inputOwner = _ownedRecording?.InputLease ?? _recordingInputOperation;
        if (_uiUpdateTimer != null)
        {
            _uiUpdateTimer.Tick -= OnRecordingUiTimerTick;
            _uiUpdateTimer.Stop();
            _uiUpdateTimer = null;
        }

        // Reveal the already-existing editor immediately; cleanup and encryption stay off its dispatcher.
        if (!_shutdownStarted && !IsRunning) RequestRecordingEditorRestore();
        // Stop producers first and complete the queue. Normal stop drains every accepted
        // event; cancellation is only the bounded fallback for a failed consumer.
        try
        {
            await StopInputCaptureAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _recordingFailure = $"Input capture could not stop cleanly: {ex.Message}";
        }
        finally
        {
            _recordingInputBuffer?.TryComplete();
        }

        if (_recordingConsumerTask != null)
        {
            try
            {
                await _recordingConsumerTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _recordingFailure = "Recording processing exceeded the stop deadline; the recording may be incomplete.";
                SetRecordingPhase("Faulted");
                // Show the stable prefix already converted. Leave the worker and journal
                // owned and block a new session; another Stop can finish after it exits.
                void ShowPrefix()
                {
                    int available = _recordedStagingQueue.Count;
                    var prefix = new List<MacroStep>(Math.Min(available, 10000));
                    for (int i = 0; i < available && _recordedStagingQueue.TryDequeue(out var item); i++) prefix.Add(item);
                    if (prefix.Count > 0) { Steps.AddSteps(prefix, notify: false); Steps.RefreshFromStore(); RefreshStepOrder(persist: false); }
                    NotifyStatus(_recordingFailure + " Retained actions are visible. Retry Stop after cleanup completes, or recover the encrypted checkpoint after restarting.");
                }
                if (_uiDispatcher != null && !_uiDispatcher.CheckAccess()) await _uiDispatcher.InvokeAsync(ShowPrefix);
                else ShowPrefix();
                throw new TimeoutException(_recordingFailure);
            }
            if (_recordingConsumerTask.IsCompleted) _recordingConsumerTask = null;
        }
        _isStartingRecording = false;
        if (inputOwner?.OwnsCleanup == true && InputModeGate.Current == InputMode.Recording)
            InputModeGate.Set(InputMode.Normal);

        if (_disposed) return;
        RecordingSaveRequest? save = _uiDispatcher != null && !_uiDispatcher.CheckAccess()
            ? await _uiDispatcher.InvokeAsync(CompleteStopRecordingOnUi)
            : CompleteStopRecordingOnUi();
        if (save is not null) await SaveRecordingSnapshotAsync(save).ConfigureAwait(false);
        var session = _ownedRecording;
        if (session?.Desktop != null) await session.Desktop.DisposeAsync().ConfigureAwait(false);
        if (session?.Journal != null)
        {
            try { await session.Journal.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _recordingFailure ??= "Recovery journal retained: " + ex.Message; }
        }
        if (session != null) Interlocked.Exchange(ref session.NeedsFinalization, 0);
        _recordingTerminalReason = _recordingFailure ?? (session?.Emergency != 0 ? "Stopped by emergency stop" : "Recording stopped");
        SetRecordingPhase(_recordingFailure != null || _recordingCaptureStopTask is { IsCompleted: false } ? "Faulted" : "Idle");
        // Recovery verification and disposal happen after the actions are saved.
        // Publish their failures too, so the final status never hides the cause.
        if (_recordingFailure is { } finalFailure)
        {
            void ReportFailure() { NotifyStatus(finalFailure); LogActivity(finalFailure); }
            if (_uiDispatcher != null && !_uiDispatcher.CheckAccess()) await _uiDispatcher.InvokeAsync(ReportFailure);
            else ReportFailure();
        }
    }
    private RecordingSaveRequest? CompleteStopRecordingOnUi()
    {
        if (_disposed) return null;
        try
        {
        var leftover = new List<MacroStep>(64);
        while (_recordedStagingQueue.TryDequeue(out var step))
            leftover.Add(step);
        if (leftover.Count > 0)
            Steps.AddSteps(leftover, notify: false);

        Steps.RefreshFromStore();
        _editHistory.Clear();
        RefreshStepOrder(persist: false);
        RecordingSaveRequest? save = PrepareRecordingSave();
        if (_recordingFailure != null)
        {
            StatusChanged?.Invoke(_recordingFailure);
            LogActivity(_recordingFailure);
        }
        else
        {
            StatusChanged?.Invoke($"Recording stopped. Total actions: {Steps.Count}.");
            LogActivity($"Recording stopped for '{MacroName}'. Retained {Steps.Count} actions.");
        }
        return save;
        }
        finally
        {
            // Native capture and its tail have stopped; disk encryption must not hold the window hidden.
            if (!_shutdownStarted && !IsRunning) RequestRecordingEditorRestore();
        }
    }

    /// <summary>
    /// Keystrokes typed into Macro Engine itself are never recorded (that is deliberate),
    /// but silence looks like a broken recorder — tell the user, at most once every 3s.
    /// </summary>
    private void WarnFocusBlockedRecording(long nowTimestamp)
    {
        long last = Interlocked.Read(ref _focusWarningTimestamp);
        if (last != 0 && nowTimestamp - last < Stopwatch.Frequency * 3) return;
        Interlocked.Exchange(ref _focusWarningTimestamp, nowTimestamp);

        const string message = "Recording: switch to the app you want to record — input inside Macro Engine is not captured.";
        if (!TryPostToUi(() => StatusChanged?.Invoke(message)))
            StatusChanged?.Invoke(message);
    }

    private static uint GetVirtualKeyCode(string? hotkey) => HotkeyCodec.VirtualKeyOf(hotkey);

    private TimeSpan GetLatestRelativeTimestamp()
    {
        TimeSpan latest = TimeSpan.Zero;
        foreach (var step in _actionStore.Items)
        {
            if (MacroSuite.Core.Execution.StepTimeline.TryGetRelativeTimestamp(step, out TimeSpan ts))
            {
                TimeSpan end = StepTimeline.GetEndTimestamp(step, ts);
                if (end > latest) latest = end;
            }
        }

        return latest;
    }

    private async Task ProcessRecordedEventsAsync(ChannelReader<RecordedInput> reader, CancellationToken ct)
    {
        var session = _ownedRecording!;
        long epoch = -1;
        TimeSpan timelineOrigin = _recordingTimelineOrigin;
        long recordingStartTick = _recordingStartTick;
        if (timelineOrigin > TimeSpan.Zero)
            recordingStartTick -= (long)(timelineOrigin.TotalSeconds * Stopwatch.Frequency);
        long gracePeriodEndTick = _recordingStartTick + (long)(Stopwatch.Frequency * (RecordingGracePeriodMs / 1000.0));
        uint recVk = GetVirtualKeyCode(RecordHotkey);
        uint stopVk = GetVirtualKeyCode(StopHotkey);
        HotkeyCodec.TryParse(RecordHotkey, out uint recordModifiers, out uint recordKey);
        HotkeyCodec.TryParse(StopHotkey, out uint stopModifiers, out uint stopKey);
        var recordShortcutFilter = new RecordingHotkeyFilter(recordModifiers, recordKey);
        var stopShortcutFilter = new RecordingHotkeyFilter(stopModifiers, stopKey);
        HotkeyCodec.TryParse(MiniToolbarClickThroughHotkey, out uint miniModifiers, out uint miniKey);
        var miniShortcutFilter = new RecordingHotkeyFilter(miniModifiers, miniKey);

        var compressionOptions = new CompressionOptions
        {
            SamplingMode = session.SamplingMode,
            MinDistancePx = session.MinDistancePx,
            MinTimeMs = session.MinTimeMs,
        PreserveDirectionChanges = true
        };
        var compressionEngine = new MouseCompressionEngine(compressionOptions);
        var emittedMoves = new List<InputEvent>(16);
        var heldMouseButtons = new HashSet<int>(4);
        var heldKeys = new HashSet<int>(8);
        int lastMouseX = 0;
        int lastMouseY = 0;
        int lockMouseX = 0;
        int lockMouseY = 0;
        bool haveLastMouse = false;
        int accRelDx = 0;
        int accRelDy = 0;
        var pathSamples = new List<MouseTrajectorySample>(256);
        var pathPackets = new List<MouseTrajectoryPacket>(256);
        int pathX = 0;
        int pathY = 0;
        long pathStartTick = 0;
        long lastAcceptedTick = _recordingStartTick;
        bool hasRawMotion = false;

        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var packet))
                {
                    var ev = packet.Event;
                    _convertingPacket = packet;
                    Interlocked.Increment(ref _totalEventsCaptured);
                    if (ct.IsCancellationRequested) return;
                    if (_recordedStagingQueue.Count >= 10_000)
                    {
                        Interlocked.Exchange(ref session.Paused, 1);
                        SetRecordingPhase("Paused");
                        // Close new admission while draining the already accepted bounded prefix.
                    }

                    // Inclusion, target, held-button and timestamp information was frozen at admission.
                    if (epoch >= 0 && epoch != packet.Epoch)
                    {
                        var boundary = new InputEvent(InputEventType.MouseMove, lastAcceptedTick, lastMouseX, lastMouseY, 0);
                        FlushPendingMotion(compressionEngine, emittedMoves, recordingStartTick, ref accRelDx, ref accRelDy, boundary);
                        FlushHeldLookPath(pathSamples, pathPackets, ref pathX, ref pathY, ref pathStartTick, lastAcceptedTick, recordingStartTick);
                        foreach (int button in heldMouseButtons) EnqueueRecordedClick(new(InputEventType.MouseUp, lastAcceptedTick, lastMouseX, lastMouseY, button), recordingStartTick, "Up");
                        foreach (int key in heldKeys) EnqueueRecordedKey(key, lastAcceptedTick, recordingStartTick, "Up");
                        heldMouseButtons.Clear(); heldKeys.Clear(); haveLastMouse = false; hasRawMotion = false;
                    }
                    epoch = packet.Epoch;
                    await JournalRecordingInputAsync(session, packet).ConfigureAwait(false);
                    lastAcceptedTick = Math.Max(lastAcceptedTick, ev.TimestampTicks);

                    if (ev.Type == InputEventType.MouseMoveRelative)
                    {
                        // Seed already-held camera buttons without repositioning the cursor.
                        if (!hasRawMotion)
                        {
                            var rawPosition = new InputEvent(InputEventType.MouseMove, ev.TimestampTicks, -1, -1, 0);
                            SeedHeldButtonsFromCapture(heldMouseButtons, rawPosition, recordingStartTick, packet.HeldButtons);
                            hasRawMotion = true;
                        }
                        AppendHeldLookSample(pathSamples, pathPackets, ref pathX, ref pathY, ref pathStartTick, ev.X, ev.Y, ev.TimestampTicks);
                        if (pathPackets.Count >= 10_000)
                        {
                            FlushHeldLookPath(pathSamples, pathPackets, ref pathX, ref pathY, ref pathStartTick, ev.TimestampTicks, recordingStartTick);
                            pathStartTick = ev.TimestampTicks;
                        }
                        continue;
                    }

                    if (ev.Type == InputEventType.MouseMove)
                        SeedHeldButtonsFromCapture(heldMouseButtons, ev, recordingStartTick, packet.HeldButtons);

                    // Any held button (left, right, middle, X) drives relative look/drag.
                    bool useRelativeMotion = heldMouseButtons.Count > 0
                        || packet.HeldButtons != 0
                        || packet.PointerConstrained;

                    if (ev.Type == InputEventType.MouseMove)
                    {
                        if (!haveLastMouse)
                        {
                            lastMouseX = ev.X;
                            lastMouseY = ev.Y;
                            lockMouseX = ev.X;
                            lockMouseY = ev.Y;
                            haveLastMouse = true;
                        }

                        int dx = ev.X - lastMouseX;
                        int dy = ev.Y - lastMouseY;

                        if (useRelativeMotion)
                        {
                            int distFromLock = Math.Abs(ev.X - lockMouseX) + Math.Abs(ev.Y - lockMouseY);
                            int lastFromLock = Math.Abs(lastMouseX - lockMouseX) + Math.Abs(lastMouseY - lockMouseY);
                            lastMouseX = ev.X;
                            lastMouseY = ev.Y;

                            // Game recentered the cursor — skip that warp only.
                            // The look deltas already recorded stay; including this
                            // jump cancels the path down to lock jitter.
                            if (packet.PointerConstrained
                                && MouseTrajectoryPlayback.IsCursorSnapBack(lastFromLock, distFromLock))
                                continue;

                            if (dx == 0 && dy == 0)
                                continue;

                            if (heldMouseButtons.Count > 0)
                            {
                                AppendHeldLookSample(pathSamples, pathPackets, ref pathX, ref pathY, ref pathStartTick, dx, dy, ev.TimestampTicks);
                                if (pathPackets.Count >= 10_000)
                                {
                                    FlushHeldLookPath(pathSamples, pathPackets, ref pathX, ref pathY, ref pathStartTick, ev.TimestampTicks, recordingStartTick);
                                    pathStartTick = ev.TimestampTicks;
                                }
                                continue;
                            }

                            // Preserve each inferred packet, including tiny corrections and
                            // reversals. Summing until a threshold cancels real motion.
                            EnqueueRecordedRelativeMove(dx, dy, ev.X, ev.Y, ev.TimestampTicks, recordingStartTick);
                        }
                        else
                        {
                            lastMouseX = ev.X;
                            lastMouseY = ev.Y;
                            emittedMoves.Clear();
                            compressionEngine.Process(in ev, emittedMoves);
                            foreach (var m in emittedMoves)
                                EnqueueRecordedMove(m, recordingStartTick);
                        }
                    }
                    else if (ev.Type == InputEventType.MouseDown)
                    {
                        FlushPendingMotion(compressionEngine, emittedMoves, recordingStartTick,
                            ref accRelDx, ref accRelDy, ev);
                        FlushHeldLookPath(pathSamples, pathPackets, ref pathX, ref pathY, ref pathStartTick, ev.TimestampTicks, recordingStartTick);
                        lockMouseX = ev.X;
                        lockMouseY = ev.Y;
                        lastMouseX = ev.X;
                        lastMouseY = ev.Y;
                        haveLastMouse = true;
                        heldMouseButtons.Add(ev.Data);
                        pathStartTick = ev.TimestampTicks;
                        pathSamples.Clear();
                        pathPackets.Clear();
                        pathSamples.Add(new MouseTrajectorySample(0, 0, 0));
                        pathX = 0;
                        pathY = 0;
                        EnqueueRecordedClick(ev, recordingStartTick, stroke: "Down");
                    }
                    else if (ev.Type == InputEventType.MouseUp)
                    {
                        if (accRelDx != 0 || accRelDy != 0)
                        {
                            AppendHeldLookSample(pathSamples, pathPackets, ref pathX, ref pathY, ref pathStartTick, accRelDx, accRelDy, ev.TimestampTicks);
                            accRelDx = 0;
                            accRelDy = 0;
                        }
                        DiscardPendingAbsoluteMoves(compressionEngine, emittedMoves, ref accRelDx, ref accRelDy);
                        FlushHeldLookPath(pathSamples, pathPackets, ref pathX, ref pathY, ref pathStartTick, ev.TimestampTicks, recordingStartTick);
                        lastMouseX = ev.X;
                        lastMouseY = ev.Y;
                        heldMouseButtons.Remove(ev.Data);
                        if (heldMouseButtons.Count > 0)
                        {
                            pathStartTick = ev.TimestampTicks;
                            pathSamples.Clear();
                            pathPackets.Clear();
                            pathSamples.Add(new MouseTrajectorySample(0, 0, 0));
                            pathX = 0;
                            pathY = 0;
                        }
                        EnqueueRecordedClick(ev, recordingStartTick, stroke: "Up");
                    }
                    else if (ev.Type is InputEventType.MouseWheel or InputEventType.MouseHWheel)
                    {
                        FlushPendingMotion(compressionEngine, emittedMoves, recordingStartTick, ref accRelDx, ref accRelDy, ev, emitRelative: heldMouseButtons.Count == 0);
                        if (heldMouseButtons.Count > 0 && (accRelDx != 0 || accRelDy != 0))
                        {
                            AppendHeldLookSample(pathSamples, pathPackets, ref pathX, ref pathY, ref pathStartTick, accRelDx, accRelDy, ev.TimestampTicks);
                            accRelDx = 0;
                            accRelDy = 0;
                        }
                        FlushHeldLookPath(pathSamples, pathPackets, ref pathX, ref pathY, ref pathStartTick, ev.TimestampTicks, recordingStartTick);
                        if (heldMouseButtons.Count > 0 || hasRawMotion) pathStartTick = ev.TimestampTicks;
                        EnqueueRecordedWheel(ev, recordingStartTick);
                    }
                    else if (ev.Type == InputEventType.KeyDown)
                    {
                        int vk = ev.Data;
                        heldKeys.Add(vk); // Repeated key-down events are part of the recorded trace.

                        FlushPendingMotion(compressionEngine, emittedMoves, recordingStartTick, ref accRelDx, ref accRelDy, ev, emitRelative: heldMouseButtons.Count == 0);
                        if (heldMouseButtons.Count > 0 && (accRelDx != 0 || accRelDy != 0))
                        {
                            AppendHeldLookSample(pathSamples, pathPackets, ref pathX, ref pathY, ref pathStartTick, accRelDx, accRelDy, ev.TimestampTicks);
                            accRelDx = 0;
                            accRelDy = 0;
                        }
                        FlushHeldLookPath(pathSamples, pathPackets, ref pathX, ref pathY, ref pathStartTick, ev.TimestampTicks, recordingStartTick);
                        if (heldMouseButtons.Count > 0 || hasRawMotion) pathStartTick = ev.TimestampTicks;
                        EnqueueRecordedKey(vk, ev.TimestampTicks, recordingStartTick, stroke: "Down");
                    }
                    else if (ev.Type == InputEventType.KeyUp)
                    {
                        int vk = ev.Data;
                        if (!heldKeys.Remove(vk))
                            continue;

                        FlushPendingMotion(compressionEngine, emittedMoves, recordingStartTick, ref accRelDx, ref accRelDy, ev, emitRelative: heldMouseButtons.Count == 0);
                        if (heldMouseButtons.Count > 0 && (accRelDx != 0 || accRelDy != 0))
                        {
                            AppendHeldLookSample(pathSamples, pathPackets, ref pathX, ref pathY, ref pathStartTick, accRelDx, accRelDy, ev.TimestampTicks);
                            accRelDx = 0;
                            accRelDy = 0;
                        }
                        FlushHeldLookPath(pathSamples, pathPackets, ref pathX, ref pathY, ref pathStartTick, ev.TimestampTicks, recordingStartTick);
                        if (heldMouseButtons.Count > 0 || hasRawMotion) pathStartTick = ev.TimestampTicks;
                        EnqueueRecordedKey(vk, ev.TimestampTicks, recordingStartTick, stroke: "Up");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown on cancellation
        }
        catch (Exception ex)
        {
            _recordingFailure = ex.Message;
            _ = StopRecordingAsync();
        }
        finally
        {
            var endEv = new InputEvent(InputEventType.MouseMove, lastAcceptedTick, lastMouseX, lastMouseY, 0, 0);
            FlushPendingMotion(compressionEngine, emittedMoves, recordingStartTick, ref accRelDx, ref accRelDy, endEv, emitRelative: false);
            if (accRelDx != 0 || accRelDy != 0)
                AppendHeldLookSample(pathSamples, pathPackets, ref pathX, ref pathY, ref pathStartTick, accRelDx, accRelDy, endEv.TimestampTicks);
            FlushHeldLookPath(pathSamples, pathPackets, ref pathX, ref pathY, ref pathStartTick, endEv.TimestampTicks, recordingStartTick);

            long now = endEv.TimestampTicks;
            foreach (int button in heldMouseButtons)
            {
                var release = new InputEvent(InputEventType.MouseUp, now, lastMouseX, lastMouseY, button, 0);
                EnqueueRecordedClick(release, recordingStartTick, stroke: "Up");
            }

            foreach (int vk in heldKeys)
                EnqueueRecordedKey(vk, now, recordingStartTick, stroke: "Up");
        }
    }

    private static readonly MouseButton[] LookButtons =
    [
        MouseButton.Left,
        MouseButton.Right,
        MouseButton.Middle,
        MouseButton.X1,
        MouseButton.X2
    ];

    /// <summary>
    /// If the user is already holding a button (common for right-click cameras) the
    /// matching MouseDown may have been dropped by the REC grace period. Seed it so
    /// replay still does hold → relative move → release.
    /// </summary>
    private void SeedHeldButtonsFromCapture(
        HashSet<int> heldMouseButtons,
        in InputEvent ev,
        long recordingStartTick, int capturedButtons)
    {
        for (int i = 0; i < LookButtons.Length; i++)
        {
            var button = LookButtons[i];
            if ((capturedButtons & (1 << (int)button)) == 0)
                continue;
            if (!heldMouseButtons.Add((int)button))
                continue;

            var down = new InputEvent(InputEventType.MouseDown, ev.TimestampTicks, ev.X, ev.Y, (int)button, 0);
            EnqueueRecordedClick(down, recordingStartTick, stroke: "Down");
        }
    }

    private static void AppendHeldLookSample(
        List<MouseTrajectorySample> path,
        List<MouseTrajectoryPacket> packets,
        ref int pathX,
        ref int pathY,
        ref long pathStartTick,
        int dx,
        int dy,
        long timestampTicks)
    {
        if (dx == 0 && dy == 0)
            return;

        if (pathStartTick == 0)
            pathStartTick = timestampTicks;
        if (path.Count == 0)
            path.Add(new MouseTrajectorySample(0, 0, 0));

        pathX += dx;
        pathY += dy;
        int tMs = (int)Math.Max(0, (timestampTicks - pathStartTick) * 1000.0 / Stopwatch.Frequency);
        packets.Add(new MouseTrajectoryPacket(tMs, dx, dy, packets.Count)
        {
            OffsetTicks = Stopwatch.GetElapsedTime(pathStartTick, timestampTicks).Ticks
        });
        var sample = new MouseTrajectorySample(tMs, pathX, pathY);
        if (path.Count > 0 && path[^1].T == tMs)
            path[^1] = sample;
        else
            path.Add(sample);
    }

    private void FlushHeldLookPath(
        List<MouseTrajectorySample> path,
        List<MouseTrajectoryPacket> packets,
        ref int pathX,
        ref int pathY,
        ref long pathStartTick,
        long endTick,
        long recordingStartTick)
    {
        // Packet order and exact timestamps are authoritative. Even a return to the
        // origin inside one millisecond is real motion and must remain replayable.
        long capturedStartTick = pathStartTick;
        pathX = 0;
        pathY = 0;
        pathStartTick = 0;
        if (packets.Count == 0)
        {
            path.Clear();
            packets.Clear();
            return;
        }

        var built = MouseTrajectoryPlayback.Normalize(path);
        var captured = packets.Count == 0 ? null : packets.ToArray();
        path.Clear();
        packets.Clear();
        if (built.Length < 2)
            built = [new MouseTrajectorySample(0, 0, 0), built.Length == 0
                ? new MouseTrajectorySample(0, 0, 0) : built[0]];

        var rel = Stopwatch.GetElapsedTime(recordingStartTick, capturedStartTick);
        _recordedStagingQueue.Enqueue(new MouseTrajectoryStep(built, rel)
        {
            TimestampIsStart = true,
            PlaybackHz = _ownedRecording?.PlaybackHz ?? MouseTrajectoryPlayback.DefaultHz,
            Sensitivity = _ownedRecording?.GainX ?? MouseTrajectoryStep.DefaultLookGainX,
            SensitivityY = _ownedRecording?.GainY ?? MouseTrajectoryStep.DefaultLookGainY,
            Packets = captured,
            Mode = _ownedRecording?.CameraMode ?? TrajectoryPlaybackMode.Reconstructed,
            GapPolicy = TrajectoryGapPolicy.PreservePause
        });
    }

    private static void DiscardPendingAbsoluteMoves(
        MouseCompressionEngine compressionEngine,
        List<InputEvent> emittedMoves,
        ref int accRelDx,
        ref int accRelDy)
    {
        emittedMoves.Clear();
        compressionEngine.Flush(emittedMoves);
        emittedMoves.Clear();
        accRelDx = 0;
        accRelDy = 0;
    }

    private void FlushPendingMotion(
        MouseCompressionEngine compressionEngine,
        List<InputEvent> emittedMoves,
        long recordingStartTick,
        ref int accRelDx,
        ref int accRelDy,
        in InputEvent ev,
        bool emitRelative = true)
    {
        emittedMoves.Clear();
        compressionEngine.Flush(emittedMoves);
        foreach (var m in emittedMoves)
            EnqueueRecordedMove(m, recordingStartTick);

        if (emitRelative && (accRelDx != 0 || accRelDy != 0))
        {
            EnqueueRecordedRelativeMove(accRelDx, accRelDy, ev.X, ev.Y, ev.TimestampTicks, recordingStartTick);
            accRelDx = 0;
            accRelDy = 0;
        }
    }

    private void EnqueueRecordedKey(int vk, long timestampTicks, long recordingStartTick, string stroke)
    {
        var rel = Stopwatch.GetElapsedTime(recordingStartTick, timestampTicks);
        _recordedStagingQueue.Enqueue(new KeyPressStep(vk)
        {
            RelativeTimestamp = rel,
            Stroke = stroke
        });
    }

    private void EnqueueRecordedWheel(in InputEvent ev, long recordingStartTick)
    {
        if (ev.Data == 0)
            return;

        var rel = Stopwatch.GetElapsedTime(recordingStartTick, ev.TimestampTicks);
        int deltaY = ev.Type == InputEventType.MouseWheel ? ev.Data : 0;
        int deltaX = ev.Type == InputEventType.MouseHWheel ? ev.Data : 0;
        _recordedStagingQueue.Enqueue(new MouseWheelStep(deltaY, deltaX, rel));
    }

    private void EnqueueRecordedRelativeMove(int dx, int dy, int absX, int absY, long timestampTicks, long recordingStartTick)
    {
        if (dx == 0 && dy == 0)
            return;

        var rel = Stopwatch.GetElapsedTime(recordingStartTick, timestampTicks);
        _recordedStagingQueue.Enqueue(new MouseMoveStep(new DrawingPoint(dx, dy), isClientRelative: false, relativeTimestamp: rel)
        {
            IsRelative = true,
            ScreenPosition = new DrawingPoint(absX, absY)
        });
    }

    private void EnqueueRecordedClick(in InputEvent ev, long recordingStartTick, string stroke)
    {
        var rel = Stopwatch.GetElapsedTime(recordingStartTick, ev.TimestampTicks);
        var clickStep = new ClickStep(new DrawingPoint(ev.X, ev.Y), (MouseButton)ev.Data)
        {
            IsClientRelative = _convertingPacket.IsClientRelative,
            RecordedClientWidth = _convertingPacket.ClientWidth,
            RecordedClientHeight = _convertingPacket.ClientHeight,
            RelativeTimestamp = rel,
            Stroke = stroke
        };

        _recordedStagingQueue.Enqueue(clickStep);
    }

    private void EnqueueRecordedMove(in InputEvent ev, long recordingStartTick)
    {
        var rel = Stopwatch.GetElapsedTime(recordingStartTick, ev.TimestampTicks);
        // Store virtual-screen coordinates. Avoid WindowFromPoint on the recording hot path.
        var moveStep = new MouseMoveStep(new DrawingPoint(ev.X, ev.Y), isClientRelative: _convertingPacket.IsClientRelative, relativeTimestamp: rel)
        {
            RecordedClientWidth = _convertingPacket.ClientWidth,
            RecordedClientHeight = _convertingPacket.ClientHeight,
            ScreenPosition = new DrawingPoint(ev.X, ev.Y)
        };

        _recordedStagingQueue.Enqueue(moveStep);
    }

    public void DeleteScript(MacroScriptItemViewModel? script)
    {
        if (IsEditingLocked || !TryCommitPendingActionEdits()) return;
        var target = script ?? SelectedScript;
        if (target != null && MacroScripts.Count > 1)
        {
            var nextIndex = Math.Max(0, MacroScripts.IndexOf(target) - 1);
            string? deletedPath = null;
            if (_isPersistenceEnabled && !string.IsNullOrEmpty(target.FilePath) && File.Exists(target.FilePath))
            {
                object? companionMutation = null;
                BeginCompanionMutation(target.Id, ref companionMutation);
                try
                {
                    string folder = Path.Combine(_macrosDirectory, ".deleted");
                    Directory.CreateDirectory(folder);
                    deletedPath = Path.Combine(folder, $"{target.Id:N}-{Guid.NewGuid():N}.json");
                    File.Move(target.FilePath, deletedPath);
                    CaptureCompanionDeletedScript(target.Id);
                }
                catch (Exception ex)
                {
                    NotifyStatus($"Could not delete '{target.Name}': {ex.Message}");
                    return;
                }
                finally { CompleteCompanionMutation(companionMutation); }
            }
            _deletedMacro = target;
            _deletedMacroPath = deletedPath;
            MacroScripts.Remove(target);
            if (ReferenceEquals(_selectedScript, target)) _selectedScript = null;
            SelectedScript = MacroScripts[Math.Min(nextIndex, MacroScripts.Count - 1)];
            OnPropertyChanged(nameof(HasDeletedMacro));
            OnPropertyChanged(nameof(DeletedMacroNotice));
            UndoDeleteMacroCommand.RaiseCanExecuteChanged();
            StatusChanged?.Invoke("Ready");
            LogActivity($"Deleted macro '{target.Name}'.");
            HotkeysUpdated?.Invoke();
        }
    }

    private void DeleteCurrentMacro()
    {
        DeleteScript(SelectedScript);
    }

    private void ToggleMacroStatus()
    {
        if (IsEditingLocked) return;
        IsScriptEnabled = !IsScriptEnabled;
        if (SelectedScript != null)
        {
            SelectedScript.IsActive = IsScriptEnabled;
            SaveMacroScript(SelectedScript);
        }
        OnPropertyChanged(nameof(MacroStatusButtonText));
        StatusChanged?.Invoke(IsScriptEnabled ? "Macro status: Active" : "Macro status: Disabled");
        LogActivity($"Macro '{MacroName}' status toggled to {(IsScriptEnabled ? "Active" : "Disabled")}.");
    }

    public void EditScript(MacroScriptItemViewModel? script)
    {
        if (IsEditingLocked) return;
        if (IsRecordingBusy) return;
        if (script != null && script != SelectedScript)
        {
            SelectedScript = script;
        }
        IsEditingMacro = true;
        StatusChanged?.Invoke($"Editing properties for '{MacroName}'");
    }

    private void EditCurrentMacro()
    {
        if (IsEditingLocked) return;
        if (IsRecordingBusy) return;
        IsEditingMacro = !IsEditingMacro;
        if (IsEditingMacro)
        {
            StatusChanged?.Invoke($"Editing properties for '{MacroName}'");
        }
    }

    private void SaveEdit()
    {
        if (!TryCommitPendingActionEdits()) return;
        if (SelectedScript != null)
        {
            SelectedScript.Name = MacroName;
            TryChangeHotkey(CurrentHotkey, SelectedScript);
            SelectedScript.LastRunText = CurrentLastRun;
            if (_isPersistenceEnabled && !TrySaveMacroScript(SelectedScript)) return;
        }
        IsEditingMacro = false;
        StatusChanged?.Invoke($"Saved macro '{MacroName}'");
        LogActivity($"Updated macro settings for '{MacroName}' [{CurrentHotkey}].");
        HotkeysUpdated?.Invoke();
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "macro" : sanitized;
    }

    private void CapturePlaybackSettingsTo(MacroScriptItemViewModel script)
    {
        script.RepeatCount = RepeatCount;
        script.RepeatUntilStopped = RepeatUntilStopped;
        script.PlaybackSpeed = PlaybackSpeed;
    }

    private void ApplyPlaybackSettingsFrom(MacroScriptItemViewModel script)
    {
        _repeatCount = Math.Max(1, script.RepeatCount);
        _repeatUntilStopped = script.RepeatUntilStopped;
        _playbackSpeed = script.PlaybackSpeed > 0.05 ? script.PlaybackSpeed : 1.0;
        OnPropertyChanged(nameof(RepeatCount));
        OnPropertyChanged(nameof(RepeatCountText));
        OnPropertyChanged(nameof(RepeatCountInputText));
        OnPropertyChanged(nameof(RepeatUntilStopped));
        OnPropertyChanged(nameof(CanEditRepeatCount));
        OnPropertyChanged(nameof(PlaybackSpeed));
        OnPropertyChanged(nameof(PlaybackSpeedText));
    }

    public void SaveMacroScript(MacroScriptItemViewModel script)
        => TrySaveMacroScript(script);

    private bool TrySaveMacroScript(MacroScriptItemViewModel script)
    {
        if (_ownedRecording is { DestinationRevisionConflict: true } conflict && ReferenceEquals(conflict.Destination, script))
        { NotifyStatus("The destination changed outside this recording and was preserved. Export the retained recording as a backup, or restart and use recording recovery."); return false; }
        if (!_isPersistenceEnabled || _libraryLoadDeferred) { RefreshSaveState(); return false; }

        object? companionAttempt = null;
        try
        {
            BeginScriptSave(script);
            if (!Directory.Exists(_macrosDirectory))
            {
                Directory.CreateDirectory(_macrosDirectory);
            }

            var model = new MacroModel
            {
                RecordingCommit = script.RecordingCommit,
                Id = script.Id,
                Name = script.Name,
                Description = script == SelectedScript ? Description : script.Description,
                CreatedAt = script.CreatedAt,
                IsFavorite = script.IsFavorite,
                Settings = new MacroSettings
                {
                    Target = script.Target,
                    Hotkey = script.Hotkey,
                    IsActive = script.IsActive,
                    RepeatCount = script == SelectedScript ? RepeatCount : Math.Max(1, script.RepeatCount),
                    RepeatUntilStopped = script == SelectedScript ? RepeatUntilStopped : script.RepeatUntilStopped,
                    PlaybackSpeed = script == SelectedScript ? PlaybackSpeed : (script.PlaybackSpeed > 0.05 ? script.PlaybackSpeed : 1.0),
                    MaxRuntime = script.MaxRuntime,
                    MaxIterations = script.MaxIterations,
                    MaxLoopDepth = script.MaxLoopDepth,
                    MouseSamplingMode = script.MouseSamplingMode
                },
                Steps = script.GetActions().ToList()
            };
            CaptureCompanionSave(model, ref companionAttempt);
            string path = ProtectedSavePath(script);
            SaveProtectedDocument(model, path, companionAttempt);
            script.FilePath = path;
            CompleteScriptSave(script, model, companionAttempt);
            return true;
        }
        catch (Exception ex)
        {
            CompleteScriptSave(script, null, companionAttempt);
            LogActivity($"Warning: Failed to save script '{script.Name}': {ex.Message}");
            StatusChanged?.Invoke($"Could not save '{script.Name}': {ex.Message}");
            return false;
        }
    }

    public bool TryChangeHotkey(string? newHotkey, MacroScriptItemViewModel? targetScript = null)
    {
        targetScript ??= SelectedScript;
        if (targetScript == null) return false;

        newHotkey = newHotkey?.Trim() ?? string.Empty;

        // If it's unchanged, nothing to do
        if (HotkeyCodec.AreEqual(targetScript.Hotkey, newHotkey)
            || string.Equals(targetScript.Hotkey, newHotkey, StringComparison.OrdinalIgnoreCase))
        {
            if (targetScript == SelectedScript)
                CurrentHotkey = targetScript.Hotkey;
            return true;
        }

        if (string.IsNullOrEmpty(newHotkey))
        {
            targetScript.Hotkey = string.Empty;
            if (targetScript == SelectedScript)
                CurrentHotkey = string.Empty;
            SaveMacroScript(targetScript);
            HotkeysUpdated?.Invoke();
            RefreshKeybindRows();
            return true;
        }

        bool assigned = TryAssignBind(ScriptBindId(targetScript), newHotkey);
        if (!assigned && targetScript == SelectedScript)
            CurrentHotkey = targetScript.Hotkey;
        return assigned;
    }

    public void SelectScriptByName(string name)
    {
        var script = MacroScripts.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (script != null)
        {
            SelectedScript = script;
        }
    }

    public void SelectMacroByNameOrHotkey(string identifier, bool autoRun = false)
    {
        var script = MacroScripts.FirstOrDefault(s =>
            string.Equals(s.Hotkey, identifier, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Name, identifier, StringComparison.OrdinalIgnoreCase));
        if (script != null)
        {
            SelectedScript = script;
            if (autoRun && !IsRunning)
            {
                _ = RunMacroAsync();
            }
        }
    }

    private void InitializeScriptsLibrary(bool seedDemoData)
    {
        if (_libraryLoadDeferred) return;
        BeginCompanionLibraryLoad();
        bool companionLoadCompleted = false;
        try
        {
            InitializeScriptsLibraryCore(seedDemoData);
            companionLoadCompleted = true;
        }
        finally { CompleteCompanionLibraryLoad(companionLoadCompleted && !_libraryLoadHadErrors); }
    }

    private void InitializeScriptsLibraryCore(bool seedDemoData)
    {
        BeginProtectedLibraryLoad();
        MacroScripts.Clear();

        bool loadedFromDisk = false;
        if (_isPersistenceEnabled && Directory.Exists(_macrosDirectory))
        {
            var files = _documentStore.EnumerateLibraryFiles().ToArray();
            if (files.Length > 0)
            {
                foreach (var file in files)
                {
                    try
                    {
                        var model = ReadProtectedLibraryDocument(file, out string protectedPath, out var loadedDocument);
                        ObserveCompanionLoadedModel(model, protectedPath, loadedDocument);
                        if (model != null && !string.IsNullOrWhiteSpace(model.Name))
                        {
                            DateTimeOffset createdAt = model.CreatedAt != default
                                ? model.CreatedAt
                                : (File.Exists(file) ? new DateTimeOffset(File.GetCreationTimeUtc(file), TimeSpan.Zero) : DateTimeOffset.UtcNow);

                            var script = new MacroScriptItemViewModel(
                                model.Name,
                                model.Steps?.Count ?? 0,
                                model.Settings?.Hotkey ?? string.Empty,
                                model.Settings?.IsActive ?? true,
                                "Ready",
                                model.Id,
                                protectedPath,
                                createdAt,
                                model.IsFavorite);

                            script.RecordingCommit = model.RecordingCommit;
                            script.Target = model.Settings?.Target;
                            script.MaxRuntime = model.Settings?.MaxRuntime > TimeSpan.Zero
                                ? model.Settings.MaxRuntime
                                : TimeSpan.FromHours(1);
                            script.MaxIterations = model.Settings?.MaxIterations > 0
                                ? model.Settings.MaxIterations
                                : 100_000;
                            script.MaxLoopDepth = model.Settings?.MaxLoopDepth > 0
                                ? model.Settings.MaxLoopDepth
                                : 8;
                            script.RepeatCount = model.Settings?.RepeatCount > 0
                                ? model.Settings.RepeatCount
                                : 1;
                            script.RepeatUntilStopped = model.Settings?.RepeatUntilStopped ?? false;
                            script.Description = model.Description;
                            script.MouseSamplingMode = model.Settings?.MouseSamplingMode ?? MouseSamplingMode.DistanceAndTime;
                            script.PlaybackSpeed = model.Settings?.PlaybackSpeed > 0.05
                                ? model.Settings.PlaybackSpeed
                                : 1.0;

                            if (model.Steps != null)
                            {
                                foreach (var step in model.Steps)
                                    script.Actions.Add(step);
                            }
                            script.ActionCount = script.Actions.Count;
                            MacroScripts.Add(script);
                            loadedFromDisk = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        SetLibraryAccessError(ex);
                        LogActivity($"Failed to load '{Path.GetFileName(file)}': {ex.Message}");
                    }
                }
            }
        }

        if (!loadedFromDisk && !_libraryLoadHadErrors)
        {
            if (_isPersistenceEnabled && !seedDemoData)
            {
                // A real, persistent library starts with one empty macro rather than a
                // shelf of placeholder names that look like scripts but contain no actions.
                MacroScripts.Add(new MacroScriptItemViewModel(
                    "New Macro", 0, FindFreeHotkey(), true, "Never", createdAt: DateTimeOffset.UtcNow) { IsSelected = true });
            }
            else
            {
                SeedDemoScripts(seedDemoData);
            }

            if (_isPersistenceEnabled)
            {
                foreach (var s in MacroScripts)
                {
                    SaveMacroScript(s);
                }
            }
        }

        ApplyScriptSorting();

        var initialScript = MacroScripts.FirstOrDefault(s => s.IsSelected) ?? MacroScripts.FirstOrDefault();
        if (initialScript != null)
        {
            _selectedScript = initialScript;
            initialScript.IsSelected = true;
            _macroName = initialScript.Name;
            _currentHotkey = initialScript.Hotkey;
            _currentLastRun = initialScript.LastRunText;
            _isScriptEnabled = initialScript.IsActive;
            ApplyPlaybackSettingsFrom(initialScript);

            // Always materialize the selected script's saved actions, otherwise the
            // editor shows an empty list and Play has nothing to run.
            Steps.ReplaceAll(initialScript.GetActions());
            RefreshStepOrder();
        }
    }

    private void SeedDemoScripts(bool seedDemoData)
    {
        var baseTime = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        var scriptAutoClick = new MacroScriptItemViewModel("Auto-Click Farm", 10, "Ctrl+6", true, "Last run: 12 min ago", createdAt: baseTime.AddMinutes(0));
        var scriptInventory = new MacroScriptItemViewModel("Inventory Sweep", 14, "Ctrl+I", false, "Last run: 1 hr ago", createdAt: baseTime.AddMinutes(1));
        var scriptQuickLoot = new MacroScriptItemViewModel("Quick Loot", 9, "Ctrl+7", true, "Last run: 45 min ago", createdAt: baseTime.AddMinutes(2));
        var scriptBossRush = new MacroScriptItemViewModel("Boss Rush Combo", 17, "Ctrl+8", true, "Last run: 2 hr ago", createdAt: baseTime.AddMinutes(3));
        var scriptRotationA = new MacroScriptItemViewModel("Skill Rotation A", 9, "Ctrl+1", false, "Last run: 5 hr ago", createdAt: baseTime.AddMinutes(4));
        var scriptRotationB = new MacroScriptItemViewModel("Skill Rotation B", 8, "Ctrl+2", true, "Last run: 3 hr ago", createdAt: baseTime.AddMinutes(5)) { IsSelected = true };
        var scriptAutoHeal = new MacroScriptItemViewModel("Auto Heal", 5, "Ctrl+9", true, "Last run: 10 min ago", createdAt: baseTime.AddMinutes(6));

        if (seedDemoData)
        {
            // 1. Auto-Click Farm (10 rapid click actions)
            scriptAutoClick.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(960, 540)), "Left Click (960, 540)", "Target action point", "MOUSE") { ActionKeyword = "LEFT_CLICK", ParameterTokens = "x=960 y=540", TimingOffset = "0.05s", KeywordBrushKey = "AccentMint" });
            scriptAutoClick.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(50)), "Wait 50ms", "Burst delay", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=50", TimingOffset = "0.10s", KeywordBrushKey = "AccentMauve" });
            scriptAutoClick.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(960, 540)), "Left Click (960, 540)", "Target action point", "MOUSE") { ActionKeyword = "LEFT_CLICK", ParameterTokens = "x=960 y=540", TimingOffset = "0.15s", KeywordBrushKey = "AccentMint" });
            scriptAutoClick.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(45)), "Wait 45ms", "Burst delay", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=45", TimingOffset = "0.20s", KeywordBrushKey = "AccentMauve" });
            scriptAutoClick.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(961, 539)), "Left Click (961, 539)", "Humanized jitter click", "MOUSE") { ActionKeyword = "LEFT_CLICK", ParameterTokens = "x=961 y=539", TimingOffset = "0.25s", KeywordBrushKey = "AccentMint" });
            scriptAutoClick.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(55)), "Wait 55ms", "Burst delay", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=55", TimingOffset = "0.30s", KeywordBrushKey = "AccentMauve" });
            scriptAutoClick.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(960, 541)), "Left Click (960, 541)", "Target action point", "MOUSE") { ActionKeyword = "LEFT_CLICK", ParameterTokens = "x=960 y=541", TimingOffset = "0.35s", KeywordBrushKey = "AccentMint" });
            scriptAutoClick.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(40)), "Wait 40ms", "Burst delay", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=40", TimingOffset = "0.40s", KeywordBrushKey = "AccentMauve" });
            scriptAutoClick.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(960, 540)), "Left Click (960, 540)", "Target action point", "MOUSE") { ActionKeyword = "LEFT_CLICK", ParameterTokens = "x=960 y=540", TimingOffset = "0.45s", KeywordBrushKey = "AccentMint" });
            scriptAutoClick.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(60)), "Wait 60ms", "Cycle delay", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=60", TimingOffset = "0.51s", KeywordBrushKey = "AccentMauve" });

            // 2. Inventory Sweep (Grid sweep across inventory coordinates)
            scriptInventory.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(800, 400)), "Move to Slot 1", "Grid item 1", "MOUSE") { ActionKeyword = "MOVE", ParameterTokens = "x=800 y=400", TimingOffset = "0.08s", KeywordBrushKey = "AccentSky" });
            scriptInventory.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(800, 400)), "Click Slot 1", "Select slot", "MOUSE") { ActionKeyword = "LEFT_CLICK", ParameterTokens = "x=800 y=400", TimingOffset = "0.15s", KeywordBrushKey = "AccentMint" });
            scriptInventory.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(80)), "Wait 80ms", "UI delay", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=80", TimingOffset = "0.23s", KeywordBrushKey = "AccentMauve" });
            scriptInventory.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(860, 400)), "Move to Slot 2", "Grid item 2", "MOUSE") { ActionKeyword = "MOVE", ParameterTokens = "x=860 y=400", TimingOffset = "0.31s", KeywordBrushKey = "AccentSky" });
            scriptInventory.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(860, 400)), "Click Slot 2", "Select slot", "MOUSE") { ActionKeyword = "LEFT_CLICK", ParameterTokens = "x=860 y=400", TimingOffset = "0.38s", KeywordBrushKey = "AccentMint" });
            scriptInventory.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(80)), "Wait 80ms", "UI delay", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=80", TimingOffset = "0.46s", KeywordBrushKey = "AccentMauve" });
            scriptInventory.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(920, 400)), "Move to Slot 3", "Grid item 3", "MOUSE") { ActionKeyword = "MOVE", ParameterTokens = "x=920 y=400", TimingOffset = "0.54s", KeywordBrushKey = "AccentSky" });
            scriptInventory.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(920, 400)), "Click Slot 3", "Select slot", "MOUSE") { ActionKeyword = "LEFT_CLICK", ParameterTokens = "x=920 y=400", TimingOffset = "0.62s", KeywordBrushKey = "AccentMint" });
            scriptInventory.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(80)), "Wait 80ms", "UI delay", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=80", TimingOffset = "0.70s", KeywordBrushKey = "AccentMauve" });
            scriptInventory.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(800, 460)), "Move to Slot 4", "Grid item 4", "MOUSE") { ActionKeyword = "MOVE", ParameterTokens = "x=800 y=460", TimingOffset = "0.78s", KeywordBrushKey = "AccentSky" });
            scriptInventory.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(800, 460)), "Click Slot 4", "Select slot", "MOUSE") { ActionKeyword = "LEFT_CLICK", ParameterTokens = "x=800 y=460", TimingOffset = "0.85s", KeywordBrushKey = "AccentMint" });
            scriptInventory.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(80)), "Wait 80ms", "UI delay", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=80", TimingOffset = "0.93s", KeywordBrushKey = "AccentMauve" });
            scriptInventory.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(860, 460)), "Move to Slot 5", "Grid item 5", "MOUSE") { ActionKeyword = "MOVE", ParameterTokens = "x=860 y=460", TimingOffset = "1.01s", KeywordBrushKey = "AccentSky" });
            scriptInventory.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(860, 460)), "Click Slot 5", "Select slot", "MOUSE") { ActionKeyword = "LEFT_CLICK", ParameterTokens = "x=860 y=460", TimingOffset = "1.08s", KeywordBrushKey = "AccentMint" });

            // 3. Quick Loot
            scriptQuickLoot.Steps.Add(new MacroStepItemViewModel(new KeyPressStep(69), "Key Down: E", "Interact loot key", "KEYBOARD") { ActionKeyword = "KEY_DOWN", ParameterTokens = "key=E", TimingOffset = "0.05s", KeywordBrushKey = "AccentWarning" });
            scriptQuickLoot.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(40)), "Wait 40ms", "Hold delay", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=40", TimingOffset = "0.09s", KeywordBrushKey = "AccentMauve" });
            scriptQuickLoot.Steps.Add(new MacroStepItemViewModel(new KeyPressStep(69), "Key Up: E", "Release interact", "KEYBOARD") { ActionKeyword = "KEY_UP", ParameterTokens = "key=E", TimingOffset = "0.13s", KeywordBrushKey = "AccentWarning" });
            scriptQuickLoot.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(60)), "Wait 60ms", "Inter-action wait", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=60", TimingOffset = "0.19s", KeywordBrushKey = "AccentMauve" });
            scriptQuickLoot.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(960, 560)), "Left Click Loot Prompt", "Click pickup button", "MOUSE") { ActionKeyword = "LEFT_CLICK", ParameterTokens = "x=960 y=560", TimingOffset = "0.25s", KeywordBrushKey = "AccentMint" });
            scriptQuickLoot.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(70)), "Wait 70ms", "Pickup animation delay", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=70", TimingOffset = "0.32s", KeywordBrushKey = "AccentMauve" });
            scriptQuickLoot.Steps.Add(new MacroStepItemViewModel(new KeyPressStep(32), "Key Down: SPACE", "Jump / advance loot", "KEYBOARD") { ActionKeyword = "KEY_DOWN", ParameterTokens = "key=SPACE", TimingOffset = "0.38s", KeywordBrushKey = "AccentWarning" });
            scriptQuickLoot.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(40)), "Wait 40ms", "Jump hold", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=40", TimingOffset = "0.42s", KeywordBrushKey = "AccentMauve" });
            scriptQuickLoot.Steps.Add(new MacroStepItemViewModel(new KeyPressStep(32), "Key Up: SPACE", "Release space", "KEYBOARD") { ActionKeyword = "KEY_UP", ParameterTokens = "key=SPACE", TimingOffset = "0.46s", KeywordBrushKey = "AccentWarning" });

            // 4. Boss Rush Combo
            scriptBossRush.Steps.Add(new MacroStepItemViewModel(new KeyPressStep(49), "Key Down: 1", "Buff ability 1", "KEYBOARD") { ActionKeyword = "KEY_DOWN", ParameterTokens = "key=1", TimingOffset = "0.08s", KeywordBrushKey = "AccentWarning" });
            scriptBossRush.Steps.Add(new MacroStepItemViewModel(new KeyPressStep(49), "Key Up: 1", "Release ability 1", "KEYBOARD") { ActionKeyword = "KEY_UP", ParameterTokens = "key=1", TimingOffset = "0.15s", KeywordBrushKey = "AccentWarning" });
            scriptBossRush.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(120)), "Wait 120ms", "Cast delay", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=120", TimingOffset = "0.27s", KeywordBrushKey = "AccentMauve" });
            scriptBossRush.Steps.Add(new MacroStepItemViewModel(new KeyPressStep(50), "Key Down: 2", "Attack skill 2", "KEYBOARD") { ActionKeyword = "KEY_DOWN", ParameterTokens = "key=2", TimingOffset = "0.35s", KeywordBrushKey = "AccentWarning" });
            scriptBossRush.Steps.Add(new MacroStepItemViewModel(new KeyPressStep(50), "Key Up: 2", "Release skill 2", "KEYBOARD") { ActionKeyword = "KEY_UP", ParameterTokens = "key=2", TimingOffset = "0.42s", KeywordBrushKey = "AccentWarning" });
            scriptBossRush.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(150)), "Wait 150ms", "Global cooldown", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=150", TimingOffset = "0.57s", KeywordBrushKey = "AccentMauve" });
            scriptBossRush.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(1120, 640), MacroSuite.Core.Models.MouseButton.Left), "Left Click Boss Target", "Main burst target", "MOUSE") { ActionKeyword = "LEFT_CLICK", ParameterTokens = "x=1120 y=640", TimingOffset = "0.65s", KeywordBrushKey = "AccentMint" });
            scriptBossRush.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(80)), "Wait 80ms", "Animation windup", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=80", TimingOffset = "0.73s", KeywordBrushKey = "AccentMauve" });
            scriptBossRush.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(1120, 640), MacroSuite.Core.Models.MouseButton.Right), "Right Click Heavy Attack", "Heavy attack finisher", "MOUSE") { ActionKeyword = "RIGHT_CLICK", ParameterTokens = "x=1120 y=640", TimingOffset = "0.81s", KeywordBrushKey = "AccentMint" });
            scriptBossRush.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(100)), "Wait 100ms", "Post-attack recovery", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=100", TimingOffset = "0.91s", KeywordBrushKey = "AccentMauve" });
            scriptBossRush.Steps.Add(new MacroStepItemViewModel(new KeyPressStep(32), "Key Down: SPACE", "Dodge roll", "KEYBOARD") { ActionKeyword = "KEY_DOWN", ParameterTokens = "key=SPACE", TimingOffset = "0.98s", KeywordBrushKey = "AccentWarning" });
            scriptBossRush.Steps.Add(new MacroStepItemViewModel(new KeyPressStep(32), "Key Up: SPACE", "Complete roll", "KEYBOARD") { ActionKeyword = "KEY_UP", ParameterTokens = "key=SPACE", TimingOffset = "1.05s", KeywordBrushKey = "AccentWarning" });
            scriptBossRush.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(200)), "Wait 200ms", "Roll animation delay", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=200", TimingOffset = "1.25s", KeywordBrushKey = "AccentMauve" });
            scriptBossRush.Steps.Add(new MacroStepItemViewModel(new KeyPressStep(51), "Key Down: 3", "Ultimate ability 3", "KEYBOARD") { ActionKeyword = "KEY_DOWN", ParameterTokens = "key=3", TimingOffset = "1.33s", KeywordBrushKey = "AccentWarning" });
            scriptBossRush.Steps.Add(new MacroStepItemViewModel(new KeyPressStep(51), "Key Up: 3", "Release skill 3", "KEYBOARD") { ActionKeyword = "KEY_UP", ParameterTokens = "key=3", TimingOffset = "1.40s", KeywordBrushKey = "AccentWarning" });
            scriptBossRush.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(180)), "Wait 180ms", "Ultimate duration", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=180", TimingOffset = "1.58s", KeywordBrushKey = "AccentMauve" });
            scriptBossRush.Steps.Add(new MacroStepItemViewModel(new KeyPressStep(52), "Key Down: 4", "Execute finisher 4", "KEYBOARD") { ActionKeyword = "KEY_DOWN", ParameterTokens = "key=4", TimingOffset = "1.66s", KeywordBrushKey = "AccentWarning" });

            // 5. Skill Rotation A
            scriptRotationA.Steps.Add(new MacroStepItemViewModel(new KeyPressStep(81), "Key Down: Q", "Primary skill Q", "KEYBOARD") { ActionKeyword = "KEY_DOWN", ParameterTokens = "key=Q", TimingOffset = "0.08s", KeywordBrushKey = "AccentWarning" });
            scriptRotationA.Steps.Add(new MacroStepItemViewModel(new KeyPressStep(81), "Key Up: Q", "Release skill Q", "KEYBOARD") { ActionKeyword = "KEY_UP", ParameterTokens = "key=Q", TimingOffset = "0.15s", KeywordBrushKey = "AccentWarning" });
            scriptRotationA.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(90)), "Wait 90ms", "Cast delay", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=90", TimingOffset = "0.24s", KeywordBrushKey = "AccentMauve" });
            scriptRotationA.Steps.Add(new MacroStepItemViewModel(new KeyPressStep(87), "Key Down: W", "Secondary skill W", "KEYBOARD") { ActionKeyword = "KEY_DOWN", ParameterTokens = "key=W", TimingOffset = "0.32s", KeywordBrushKey = "AccentWarning" });
            scriptRotationA.Steps.Add(new MacroStepItemViewModel(new KeyPressStep(87), "Key Up: W", "Release skill W", "KEYBOARD") { ActionKeyword = "KEY_UP", ParameterTokens = "key=W", TimingOffset = "0.39s", KeywordBrushKey = "AccentWarning" });
            scriptRotationA.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(110)), "Wait 110ms", "Cooldown wait", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=110", TimingOffset = "0.50s", KeywordBrushKey = "AccentMauve" });
            scriptRotationA.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(950, 520)), "Left Click Skill Aim", "Target focus point", "MOUSE") { ActionKeyword = "LEFT_CLICK", ParameterTokens = "x=950 y=520", TimingOffset = "0.58s", KeywordBrushKey = "AccentMint" });
            scriptRotationA.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(75)), "Wait 75ms", "Action reset", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=75", TimingOffset = "0.65s", KeywordBrushKey = "AccentMauve" });
            scriptRotationA.Steps.Add(new MacroStepItemViewModel(new KeyPressStep(69), "Key Down: E", "Third skill E", "KEYBOARD") { ActionKeyword = "KEY_DOWN", ParameterTokens = "key=E", TimingOffset = "0.73s", KeywordBrushKey = "AccentWarning" });

            // 6. Skill Rotation B (Exact 8 reference steps from screenshot)
            scriptRotationB.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(1383, 678), MacroSuite.Core.Models.MouseButton.Left), "Left Click (1383, 678)", "Target action point", "MOUSE") { ActionKeyword = "LEFT_CLICK", ParameterTokens = "x=1383 y=678", TimingOffset = "0.08s", KeywordBrushKey = "AccentMint" });
            scriptRotationB.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(123, 940), MacroSuite.Core.Models.MouseButton.Left), "Move Cursor", "Target action point", "MOUSE") { ActionKeyword = "MOVE", ParameterTokens = "x=123 y=940", TimingOffset = "0.23s", KeywordBrushKey = "AccentSky" });
            scriptRotationB.Steps.Add(new MacroStepItemViewModel(new KeyPressStep(17), "Key Down: CTRL", "Key modifier", "KEYBOARD") { ActionKeyword = "KEY_DOWN", ParameterTokens = "key=CTRL", TimingOffset = "0.38s", KeywordBrushKey = "AccentWarning" });
            scriptRotationB.Steps.Add(new MacroStepItemViewModel(new KeyPressStep(65), "Key Up: A", "Skill key trigger", "KEYBOARD") { ActionKeyword = "KEY_UP", ParameterTokens = "key=A", TimingOffset = "0.53s", KeywordBrushKey = "AccentWarning" });
            scriptRotationB.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(100)), "Wait 100ms", "Cooldown wait", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=100", TimingOffset = "0.68s", KeywordBrushKey = "AccentMauve" });
            scriptRotationB.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(50)), "Scroll Wheel", "Wheel scroll down", "FLOW") { ActionKeyword = "SCROLL", ParameterTokens = "delta=-120", TimingOffset = "0.83s", KeywordBrushKey = "AccentSky" });
            scriptRotationB.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(1631, 956), MacroSuite.Core.Models.MouseButton.Right), "Right Click (1631, 956)", "Secondary target", "MOUSE") { ActionKeyword = "RIGHT_CLICK", ParameterTokens = "x=1631 y=956", TimingOffset = "0.98s", KeywordBrushKey = "AccentMint" });
            scriptRotationB.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(1121, 1021), MacroSuite.Core.Models.MouseButton.Left), "Drag Action", "Drag item", "MOUSE") { ActionKeyword = "DRAG", ParameterTokens = "x=1121 y=1021", TimingOffset = "1.13s", KeywordBrushKey = "AccentMint" });

            // 7. Auto Heal
            scriptAutoHeal.Steps.Add(new MacroStepItemViewModel(new KeyPressStep(72), "Key Down: H", "Potion hotkey trigger", "KEYBOARD") { ActionKeyword = "KEY_DOWN", ParameterTokens = "key=H", TimingOffset = "0.08s", KeywordBrushKey = "AccentWarning" });
            scriptAutoHeal.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(150)), "Wait 150ms", "Cast delay", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=150", TimingOffset = "0.23s", KeywordBrushKey = "AccentMauve" });
            scriptAutoHeal.Steps.Add(new MacroStepItemViewModel(new ClickStep(new DrawingPoint(960, 540), MacroSuite.Core.Models.MouseButton.Right), "Right Click Self-Cast", "Cast healing aura", "MOUSE") { ActionKeyword = "RIGHT_CLICK", ParameterTokens = "x=960 y=540", TimingOffset = "0.31s", KeywordBrushKey = "AccentMint" });
            scriptAutoHeal.Steps.Add(new MacroStepItemViewModel(new DelayStep(TimeSpan.FromMilliseconds(200)), "Wait 200ms", "Cooldown wait", "FLOW") { ActionKeyword = "WAIT", ParameterTokens = "ms=200", TimingOffset = "0.51s", KeywordBrushKey = "AccentMauve" });
            scriptAutoHeal.Steps.Add(new MacroStepItemViewModel(new KeyPressStep(72), "Key Up: H", "Release hotkey", "KEYBOARD") { ActionKeyword = "KEY_UP", ParameterTokens = "key=H", TimingOffset = "0.59s", KeywordBrushKey = "AccentWarning" });
        }

        // Sync ActionCounts
        scriptAutoClick.ActionCount = scriptAutoClick.Steps.Count;
        scriptInventory.ActionCount = scriptInventory.Steps.Count;
        scriptQuickLoot.ActionCount = scriptQuickLoot.Steps.Count;
        scriptBossRush.ActionCount = scriptBossRush.Steps.Count;
        scriptRotationA.ActionCount = scriptRotationA.Steps.Count;
        scriptRotationB.ActionCount = scriptRotationB.Steps.Count;
        scriptAutoHeal.ActionCount = scriptAutoHeal.Steps.Count;

        MacroScripts.Add(scriptAutoClick);
        MacroScripts.Add(scriptInventory);
        MacroScripts.Add(scriptQuickLoot);
        MacroScripts.Add(scriptBossRush);
        MacroScripts.Add(scriptRotationA);
        MacroScripts.Add(scriptRotationB);
        MacroScripts.Add(scriptAutoHeal);
    }

    private void CreateNewMacro() => CreateNewMacro(openEditor: true);

    private void CreateNewMacro(bool openEditor)
    {
        if (IsEditingLocked) return;
        if (!TryCommitPendingActionEdits()) return;
        string hotkey = FindFreeHotkey();

        var newScript = new MacroScriptItemViewModel(
            $"Script #{MacroScripts.Count + 1}",
            0,
            hotkey,
            true,
            "Never",
            createdAt: DateTimeOffset.UtcNow);
        newScript.Target = new MacroTargetRule { Mode = MacroTargetMode.Screen };
        MacroScripts.Add(newScript);
        ApplyScriptSorting();
        SelectedScript = newScript;
        Steps.Clear();
        RefreshStepOrder();
        SelectedCenterTab = "SCRIPTS";
        IsEditingMacro = openEditor;
        SaveMacroScript(newScript);
        StatusChanged?.Invoke($"Created new script '{newScript.Name}' [{hotkey}].");
        LogActivity($"Created new script '{newScript.Name}' [{hotkey}].");
        HotkeysUpdated?.Invoke();
    }

    private void SelectCurrentScript()
    {
        if (SelectedScript != null)
        {
            MacroName = SelectedScript.Name;
            CurrentHotkey = SelectedScript.Hotkey;
            CurrentLastRun = SelectedScript.LastRunText;
        }
    }

    public void SelectPreviousMacro()
    {
        if (MacroScripts.Count == 0) return;
        if (SelectedScript == null)
        {
            SelectedScript = MacroScripts[0];
            return;
        }
        int idx = MacroScripts.IndexOf(SelectedScript);
        if (idx > 0)
        {
            SelectedScript = MacroScripts[idx - 1];
        }
    }

    public void SelectNextMacro()
    {
        if (MacroScripts.Count == 0) return;
        if (SelectedScript == null)
        {
            SelectedScript = MacroScripts[0];
            return;
        }
        int idx = MacroScripts.IndexOf(SelectedScript);
        if (idx >= 0 && idx < MacroScripts.Count - 1)
        {
            SelectedScript = MacroScripts[idx + 1];
        }
    }

    public void RefreshStepOrder(bool persist = true)
    {
        if (SelectedScript != null)
        {
            SelectedScript.Name = MacroName;
            SelectedScript.Description = Description;
            CapturePlaybackSettingsTo(SelectedScript);
            SelectedScript.ActionCount = _actionStore.Count;
            SelectedScript.Actions.Clear();
            SelectedScript.Actions.AddRange(_actionStore.Items);
            SelectedScript.Steps.Clear();
            if (persist) SaveMacroScript(SelectedScript);
        }
        OnPropertyChanged(nameof(TotalActionsCount));
        OnPropertyChanged(nameof(AvgDelayMs));
        OnPropertyChanged(nameof(DurationSec));
        OnPropertyChanged(nameof(RecordedDelaysText));
        OnPropertyChanged(nameof(HasSteps));
        OnPropertyChanged(nameof(HasNoSteps));
        ActionEditor.RefreshRouteTargets();
        if (HasActionSearch)
            RunActionSearch(jump: false);
    }

    /// <summary>
    /// Compact summaries of the most recently recorded actions, newest last. Used by the
    /// floating mini recorder; reads the action store directly and materializes nothing extra.
    /// </summary>
    public IReadOnlyList<string> GetRecentActionSummaries(int count)
    {
        int total = _actionStore.Count;
        if (total == 0 || count <= 0) return Array.Empty<string>();

        int take = Math.Min(count, total);
        var result = new List<string>(take);
        for (int i = total - take; i < total; i++)
        {
            var vm = MacroStepItemViewModel.FromStep(_actionStore[i], i, i > 0 ? _actionStore[i - 1] : null);
            result.Add($"{i + 1,5}  {vm.ActionKeyword,-11} {vm.ParameterTokens}");
        }
        return result;
    }

    public void NotifyUserStoppedRecordingFollow()
    {
        if (!IsRecording) return;
        IsRecordingFollowEnabled = false;
        ShowResumeFollowBanner = true;
    }

    public void JumpToTop()
    {
        if (Steps.Count == 0) return;
        SelectedStep = Steps[0];
        StepNavigationRequested?.Invoke(SelectedStep);
    }

    public void JumpToBottom()
    {
        if (Steps.Count == 0) return;
        SelectedStep = Steps[^1];
        StepNavigationRequested?.Invoke(SelectedStep);
    }

    private void ApplyScriptSearch()
    {
        Predicate<object>? filter = HasScriptSearch ? ScriptMatchesFilter : null;
        if (_scriptsView != null)
            _scriptsView.Filter = filter;
        if (_favoritesView != null)
            _favoritesView.Filter = filter;
        OnPropertyChanged(nameof(HasScriptSearch));
        OnPropertyChanged(nameof(VisibleScriptCount));
        OnPropertyChanged(nameof(ScriptListCountText));
        OnPropertyChanged(nameof(HasVisibleFavorites));
        OnPropertyChanged(nameof(HasNoMatchingScripts));
    }

    private bool ScriptMatchesFilter(object obj) => ScriptMatches(obj as MacroScriptItemViewModel);

    private bool ScriptMatches(MacroScriptItemViewModel? script)
    {
        if (script is null)
            return false;
        if (!HasScriptSearch)
            return true;

        var query = _scriptSearchText.AsSpan().Trim();
        if (query.IsEmpty)
            return true;
        if (MemoryExtensions.Contains(script.Name.AsSpan(), query, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrEmpty(script.Hotkey)
            && MemoryExtensions.Contains(script.Hotkey.AsSpan(), query, StringComparison.OrdinalIgnoreCase))
            return true;

        Span<char> buf = stackalloc char[16];
        return script.ActionCount.TryFormat(buf, out int written)
            && MemoryExtensions.Contains(buf[..written], query, StringComparison.OrdinalIgnoreCase);
    }

    private void ResetActionSearch()
    {
        if (_actionSearchText.Length == 0 && _actionSearchMatchCount == 0)
            return;

        _actionSearchText = string.Empty;
        _actionSearchMatchCount = 0;
        _actionSearchOrdinal = 0;
        OnPropertyChanged(nameof(ActionSearchText));
        NotifyActionSearchProperties();
    }

    private void RunActionSearch(bool jump)
    {
        var query = _actionSearchText.Trim();
        if (query.Length == 0)
        {
            _actionSearchMatchCount = 0;
            _actionSearchOrdinal = 0;
            NotifyActionSearchProperties();
            return;
        }

        int count = 0;
        int first = -1;
        int jumpIndex = -1;
        int jumpOrdinal = 0;
        int selected = SelectedStepIndex;
        int selectedOrdinal = 0;

        for (int i = 0; i < _actionStore.Count; i++)
        {
            if (!StepSearch.Matches(_actionStore[i], i + 1, query))
                continue;

            count++;
            if (first < 0)
                first = i;
            if (i == selected)
                selectedOrdinal = count;
            if (jumpIndex < 0 && i >= Math.Max(selected, 0))
            {
                jumpIndex = i;
                jumpOrdinal = count;
            }
        }

        _actionSearchMatchCount = count;
        if (count == 0)
        {
            _actionSearchOrdinal = 0;
            NotifyActionSearchProperties();
            return;
        }

        if (!jump)
        {
            _actionSearchOrdinal = selectedOrdinal > 0 ? selectedOrdinal : 1;
            NotifyActionSearchProperties();
            return;
        }

        if (jumpIndex < 0)
        {
            jumpIndex = first;
            jumpOrdinal = 1;
        }

        _actionSearchOrdinal = jumpOrdinal;
        NotifyActionSearchProperties();
        SelectActionSearchHit(jumpIndex);
    }

    public void FindActionMatch(bool forward)
    {
        var query = _actionSearchText.Trim();
        if (query.Length == 0 || _actionSearchMatchCount == 0)
            return;

        int count = _actionStore.Count;
        if (count == 0)
            return;

        int start = SelectedStepIndex;
        if (forward)
        {
            int from = start < 0 ? 0 : start + 1;
            for (int i = 0; i < count; i++)
            {
                int index = (from + i) % count;
                if (!StepSearch.Matches(_actionStore[index], index + 1, query))
                    continue;
                SyncActionSearchOrdinal(index);
                SelectActionSearchHit(index);
                return;
            }
        }
        else
        {
            int from = start < 0 ? count - 1 : start - 1;
            for (int i = 0; i < count; i++)
            {
                int index = from - i;
                if (index < 0)
                    index += count;
                if (!StepSearch.Matches(_actionStore[index], index + 1, query))
                    continue;
                SyncActionSearchOrdinal(index);
                SelectActionSearchHit(index);
                return;
            }
        }
    }

    private void SyncActionSearchOrdinal(int index)
    {
        var query = _actionSearchText.Trim();
        if (query.Length == 0)
            return;

        int ordinal = 0;
        for (int i = 0; i <= index && i < _actionStore.Count; i++)
        {
            if (StepSearch.Matches(_actionStore[i], i + 1, query))
                ordinal++;
        }

        if (_actionSearchOrdinal == ordinal)
            return;
        _actionSearchOrdinal = ordinal;
        OnPropertyChanged(nameof(ActionSearchStatus));
    }

    private void SelectActionSearchHit(int index)
    {
        if (index < 0 || index >= Steps.Count)
            return;

        _actionSearchNavigating = true;
        try
        {
            SelectedStep = Steps[index];
            StepNavigationRequested?.Invoke(SelectedStep);
        }
        finally
        {
            _actionSearchNavigating = false;
        }
    }

    private void NotifyActionSearchProperties()
    {
        OnPropertyChanged(nameof(HasActionSearch));
        OnPropertyChanged(nameof(ActionSearchStatus));
        OnPropertyChanged(nameof(HasActionSearchMatches));
        FindNextActionCommand?.RaiseCanExecuteChanged();
        FindPrevActionCommand?.RaiseCanExecuteChanged();
    }

    public void ResumeRecordingFollow()
    {
        IsRecordingFollowEnabled = true;
        ShowResumeFollowBanner = false;
        JumpToBottom();
    }

    /// <summary>Raised when the selected step should be scrolled into view.</summary>
    public event Action<MacroStepItemViewModel?>? StepNavigationRequested;

    private static readonly Func<DrawingRect?> DefaultRegionPrompter = static () =>
    {
        using (InputModeGate.Enter(InputMode.SelectingRegion))
            return RegionSelectorWindow.PromptSelection(Application.Current?.MainWindow);
    };
    public Func<DrawingRect?> RegionPrompter { get; set; } = DefaultRegionPrompter;
    public Func<System.Drawing.Color?>? ColorPrompter { get; set; }
    public Func<ColorPickResult?>? ColorSamplePrompter { get; set; }

    /// <summary>Preview the draft image search area without capturing frames or changing the macro.</summary>
    public Action<DrawingRect?, bool, string> ImageSearchAreaPreviewer { get; set; } = static (region, useFullScreenDefault, label) =>
    {
        using (InputModeGate.Enter(InputMode.SelectingRegion))
        using (ScreenCapturePrivacy.HideEngine())
        {
            // Resolve the automatic area after hiding the editor, but before the
            // preview takes focus. Match the one-shot ImageCondition fallback.
            DrawingRect area = region is { Width: > 0, Height: > 0 } explicitArea
                ? explicitArea : Win32VirtualDesktopGeometry.Query().VirtualDesktopBounds;
            if (region is not { Width: > 0, Height: > 0 } && !useFullScreenDefault)
            {
                var coordinates = WindowCoordinateService.Default;
                IntPtr foreground = coordinates.GetForegroundWindow();
                if (foreground != IntPtr.Zero && coordinates.IsWindowValid(foreground))
                {
                    (int width, int height) = coordinates.GetClientSize(foreground);
                    if (width > 32 && height > 32)
                    {
                        (int x, int y) = coordinates.ClientToScreen(foreground, 0, 0);
                        area = new DrawingRect(x, y, width, height);
                    }
                }
            }
            SearchRegionPreviewWindow.ShowPreview(area, label, Application.Current?.MainWindow);
        }
    };

    /// <summary>Shows the follow-cursor coordinate HUD and waits for Tab / Escape.</summary>
    public Func<Task<ScreenPoint?>>? LocationPickPrompter { get; set; }
    public Func<ImageTemplateSelection?>? ImagePrompter { get; set; }

    private static readonly Func<DrawingRect?, System.Drawing.Size, DrawingRect?> DefaultImageAreaPlacementPrompter = static (area, size) =>
    {
        using (InputModeGate.Enter(InputMode.SelectingRegion))
            return ImageSearchAreaPlacementWindow.Prompt(area, size);
    };

    public Func<DrawingRect?, System.Drawing.Size, DrawingRect?> ImageAreaPlacementPrompter { get; set; } = DefaultImageAreaPlacementPrompter;

    #region Step Addition Handlers

    private void AddClickStep()
    {
        if (IsEditingLocked) return;
        // Adding a click must never hijack the screen with a region selector; that overlay is
        // only for capturing colors or image templates. The step lands on the current cursor
        // position and can be re-targeted with "Pick Location" in the Action Editor.
        int x, y;
        if (GetCursorPos(out var pt))
        {
            x = pt.X;
            y = pt.Y;
        }
        else
        {
            x = 960;
            y = 540;
        }

        var step = new ClickStep(new DrawingPoint(x, y), MacroSuite.Core.Models.MouseButton.Left);
        InsertStepAfterSelection(step);
        StatusChanged?.Invoke($"Added Click step at ({x}, {y}) — use 'Pick Location' in the editor to re-target it.");
    }

    private void AddKeypressStep()
    {
        InsertStepAfterSelection(new KeyPressStep(13));
        StatusChanged?.Invoke("Added Keypress step");
    }

    private void AddDelayStep()
    {
        InsertStepAfterSelection(new DelayStep(TimeSpan.FromMilliseconds(500)));
        StatusChanged?.Invoke("Added Delay step");
    }

    private void InsertStepAfterSelection(MacroStep step)
    {
        TryInsertStepAfterSelection(step);
    }

    public bool TryInsertStepAfterSelection(MacroStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (_disposed || IsEditingLocked || !TryCommitPendingActionEdits()) return false;
        if (TryInsertNested(step)) return true;
        int idx = SelectedStepIndex;
        int insertAt = idx < 0 ? _actionStore.Count : idx + 1;
        _editHistory.Push(new InsertEdit(insertAt, step));
        Steps.Insert(insertAt, MacroStepItemViewModel.FromStep(step, insertAt, insertAt > 0 ? GetStepAt(insertAt - 1) : null));
        SelectedStep = Steps[insertAt];
        RefreshStepOrder();
        RaiseEditCommands();
        return true;
    }

    /// <summary>
    /// Delegate that shows the AI Vision pre-chat dialog and returns the finalized prompt,
    /// or null if the user cancelled. Inject a replacement in tests (Rule 1 — no WPF from engine).
    /// </summary>
    public Func<string?>? AIVisionChatPrompter { get; set; }

    private void AddAIStep()
    {
        if (IsEditingLocked) return;
        // 1. Open the pre-flight chat dialog via the injected prompter (or the real WPF window).
        string? prompt;
        if (AIVisionChatPrompter != null)
        {
            prompt = AIVisionChatPrompter.Invoke();
        }
        else
        {
            var dialog = new MacroSuite.App.Overlays.AIVisionChatWindow();
            OverlayWindow.Attach(dialog);
            bool? result = dialog.ShowDialog();
            prompt = (result == true) ? dialog.FinalPrompt : null;
        }

        // 2. User cancelled — do nothing.
        if (string.IsNullOrWhiteSpace(prompt))
        {
            StatusChanged?.Invoke("AI Vision step cancelled.");
            return;
        }

        // 3. Optionally pick a screen region.
        var region = PromptOwnedRegion();

        // 4. Build the step with the user's prompt (Rule 8 — AI output is untrusted, prompt is user-authored).
        // Stamp the Settings provider/model so playback does not fall back to the OpenAI default.
        string? providerId = string.IsNullOrWhiteSpace(AppSettings?.SelectedProvider) ? null : AppSettings.SelectedProvider;
        string? modelId = string.IsNullOrWhiteSpace(AppSettings?.SelectedModel) ? null : AppSettings.SelectedModel;
        var step = new AIStep(prompt, region, modelId, providerId);
        InsertStepAfterSelection(step);
        LogActivity($"Added AI Vision step: \"{(prompt.Length > 40 ? prompt[..40] + "…" : prompt)}\"");
        StatusChanged?.Invoke("Added AI Vision step.");
    }


    private void AddColorConditionStep()
    {
        if (IsEditingLocked) return;
        if (!TryPromptColorSample(out int px, out int py, out var sampledColor))
            return;

        var condition = new ColorCondition(new DrawingPoint(px, py), sampledColor.R, sampledColor.G, sampledColor.B);
        var step = new WaitUntilStep(condition)
        {
            Timeout = TimeSpan.FromSeconds(10),
            PollInterval = TimeSpan.FromMilliseconds(150),
            ThrowOnTimeout = true
        };
        InsertStepAfterSelection(step);
        StatusChanged?.Invoke($"Added Color Condition step");
    }

    private void AddWaitWhileStep()
    {
        if (IsEditingLocked) return;
        if (!TryPromptColorSample(out int px, out int py, out var sampledColor))
            return;

        var condition = new ColorCondition(new DrawingPoint(px, py), sampledColor.R, sampledColor.G, sampledColor.B);
        var step = new WaitUntilStep(condition)
        {
            Timeout = TimeSpan.FromSeconds(10),
            PollInterval = TimeSpan.FromMilliseconds(150),
            WaitWhile = true,
            Label = WaitUntilStep.FormatLabel(condition, waitWhile: true)
        };
        InsertStepAfterSelection(step);
        StatusChanged?.Invoke("Added Wait While color step");
    }

    private void AddVisualWaitStep()
    {
        if (IsEditingLocked) return;
        var step = PromptOwnedVisualWait();
        if (step is null) return;

        InsertStepAfterSelection(step);
        StatusChanged?.Invoke("Added Wait for Visual Match step.");
    }

    private void AddImageStep()
    {
        if (IsEditingLocked) return;
        if (!TryPromptImageTemplate(out ImageTemplateSelection? selection) || selection is null)
            return;

        var condition = new ImageCondition(selection.ImagePath, selection.TemplateBytes, minConfidence: selection.Confidence, searchRegion: selection.SourceRegion)
        { ScanOptions = selection.SourceRegion is null ? null : ImageScanOptions.ForPlacedArea() };
        var step = new WaitUntilStep(condition)
        {
            Timeout = TimeSpan.FromSeconds(15),
            PollInterval = TimeSpan.FromMilliseconds(100),
            ThrowOnTimeout = true
        };
        InsertStepAfterSelection(step);

        if (selection.AutoClick)
        {
            var pt = selection.InitialCenter ?? new DrawingPoint(500, 300);
            var clickStep = new ClickStep(pt, MacroSuite.Core.Models.MouseButton.Left)
            {
                XVariable = ImageCondition.LastMatchXVariable,
                YVariable = ImageCondition.LastMatchYVariable
            };
            InsertStepAfterSelection(clickStep);
        }

        string fileName = Path.GetFileName(selection.ImagePath);
        StatusChanged?.Invoke($"Added Image template step '{fileName}'");
    }

    private bool TryPromptColorSample(out int x, out int y, out System.Drawing.Color color)
    {
        ColorPickResult? sample = null;
        try
        {
            sample = PromptAuthoringColor();
            if (sample is null && !_disposed && !_shutdownStarted) NotifyStatus("Color pick cancelled.");
        }
        catch (Exception error)
        {
            if (!_disposed && !_shutdownStarted) NotifyStatus($"Could not sample the selected point: {error.Message}");
        }
        if (sample is not null)
        {
            x = sample.X;
            y = sample.Y;
            color = sample.Color;
            return true;
        }

        x = 0;
        y = 0;
        color = default;
        return false;
    }

    private void AddIfStep()
    {
        if (IsEditingLocked) return;
        var cond = PromptOwnedPresence(VisualPresenceKind.ColorPresent);
        if (cond is null) return;
        var step = new IfThenElseStep(cond, [], []);
        InsertStepAfterSelection(step);
        StatusChanged?.Invoke("Added IF. Set the visual condition and THEN/ELSE actions in the editor.");
    }

    private void AddWhileStep()
    {
        if (IsEditingLocked) return;
        var cond = PromptOwnedPresence(VisualPresenceKind.ColorPresent);
        if (cond is null) return;
        var step = new LoopStep(10_000, [], cond) { MaxDurationMs = 30_000 };
        InsertStepAfterSelection(step);
        StatusChanged?.Invoke("Added WHILE. Body actions are edited in the panel — not recorded.");
    }

    private void AddRepeatUntilStep()
    {
        if (IsEditingLocked) return;
        var cond = PromptOwnedPresence(VisualPresenceKind.ColorPresent);
        if (cond is null) return;
        var step = new LoopStep(10_000, [], cond)
        {
            MaxDurationMs = 30_000,
            RepeatUntil = true,
            Label = "Repeat until present (run while absent)"
        };
        InsertStepAfterSelection(step);
        StatusChanged?.Invoke("Added Repeat until present. The body runs while the color is absent and stops when it is found.");
    }

    private void AddRepeatUntilGoneStep()
    {
        if (IsEditingLocked) return;
        var cond = PromptOwnedPresence(VisualPresenceKind.ColorAbsent);
        if (cond is null) return;
        var step = new LoopStep(10_000, [], cond)
        {
            MaxDurationMs = 30_000,
            RepeatUntil = true,
            Label = "Repeat until gone (run while present)"
        };
        InsertStepAfterSelection(step);
        StatusChanged?.Invoke("Added Repeat until gone. The body runs while the color is present and stops when it disappears.");
    }

    public void AppendToSelectedBlock(string slot, MacroStep child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (IsRecordingBusy || IsRunning || !ActionEditor.TryCommit()) return;
        int idx = SelectedStepIndex;
        var current = GetStepAt(idx);
        if (current is IfThenElseStep iff)
        {
            var thenSteps = iff.ThenSteps.ToList();
            var elseSteps = iff.ElseSteps.ToList();
            if (slot.Equals("else", StringComparison.OrdinalIgnoreCase))
                elseSteps.Add(child);
            else
                thenSteps.Add(child);
            ReplaceStepAt(idx, MacroActionFactory.WithIfBodies(iff, thenSteps, elseSteps), reloadPanel: true);
            return;
        }

        if (current is LoopStep loop)
        {
            var body = loop.BodySteps.ToList();
            body.Add(child);
            ReplaceStepAt(idx, MacroActionFactory.WithLoopBody(loop, body), reloadPanel: true);
        }
    }

    public void MoveNextIntoSelectedBlock(string slot)
    {
        if (IsRecordingBusy || IsRunning || !ActionEditor.TryCommit()) return;
        if (TryTransferNestedBlock(slot, extract: false)) return;
        int index = SelectedStepIndex;
        var current = GetStepAt(index);
        var child = GetStepAt(index + 1);
        if (current is null || child is null)
        {
            NotifyStatus("Place a configured action directly below this block first.");
            return;
        }
        var children = GetBlockChildren(current, slot);
        if (children is null) return;
        var next = WithBlockChildren(current, slot, children.Append(child).ToList());
        if (!ApplyBlockTransfer(new CompoundEdit(index,
            new ReplaceAtEdit(index, current, next), new RemoveEdit(index + 1, child)))) return;
        NotifyStatus($"Moved the following action into {slot.ToUpperInvariant()}.");
    }

    public void RemoveLastFromSelectedBlock(string slot)
    {
        if (IsRecordingBusy || IsRunning || !ActionEditor.TryCommit()) return;
        if (TryTransferNestedBlock(slot, extract: true)) return;
        int index = SelectedStepIndex;
        var current = GetStepAt(index);
        if (current is null) return;
        var children = GetBlockChildren(current, slot);
        if (children is not { Count: > 0 }) return;
        var child = children[^1];
        var next = WithBlockChildren(current, slot, children.Take(children.Count - 1).ToList());
        if (!ApplyBlockTransfer(new CompoundEdit(index,
            new ReplaceAtEdit(index, current, next), new InsertEdit(index + 1, child)))) return;
        NotifyStatus("Moved the last action below the block. You can edit it there, then move it back in.");
    }

    private static IReadOnlyList<MacroStep>? GetBlockChildren(MacroStep step, string slot) => (step, slot) switch
    {
        (IfThenElseStep iff, "then") => iff.ThenSteps,
        (IfThenElseStep iff, "else") => iff.ElseSteps,
        (LoopStep loop, "body") => loop.BodySteps,
        _ => null
    };

    private static MacroStep WithBlockChildren(MacroStep step, string slot, IReadOnlyList<MacroStep> children)
        => step is IfThenElseStep iff
            ? MacroActionFactory.WithIfBodies(iff, slot == "then" ? children : iff.ThenSteps,
                slot == "else" ? children : iff.ElseSteps)
            : MacroActionFactory.WithLoopBody((LoopStep)step, children);

    private bool ApplyBlockTransfer(CompoundEdit edit)
    {
        var candidate = new MacroActionStore(); candidate.ReplaceAll(_actionStore.Items);
        edit.Apply(candidate);
        if (!ValidateEditorStructure(candidate.Items)) return false;
        _committingSelection = true;
        try
        {
            edit.Apply(_actionStore);
            _editHistory.Push(edit);
            Steps.RefreshFromStore();
            SelectedStep = Steps[edit.Index];
            ActionEditor.LoadFrom(GetStepAt(edit.Index));
        }
        finally { _committingSelection = false; }
        RefreshStepOrder();
        RaiseEditCommands();
        return true;
    }

    public void UpdateSelectedVisualCondition(VisualPresenceCondition condition)
    {
        int idx = SelectedStepIndex;
        var current = GetStepAt(idx);
        if (current is IfThenElseStep iff)
            ReplaceStepAt(idx, MacroActionFactory.WithIfCondition(iff, condition), reloadPanel: false);
        else if (current is LoopStep loop)
            ReplaceStepAt(idx, MacroActionFactory.WithLoopBody(loop, loop.BodySteps, condition), reloadPanel: false);
    }

    public void UpdateSelectedWhileDuration(int maxDurationMs, int checkIntervalMs, int minBodyDelayMs)
    {
        int idx = SelectedStepIndex;
        if (GetStepAt(idx) is LoopStep loop)
            ReplaceStepAt(
                idx,
                MacroActionFactory.WithLoopBody(
                    loop,
                    loop.BodySteps,
                    loop.WhileCondition,
                    maxDurationMs,
                    checkIntervalMs,
                    minBodyDelayMs),
                reloadPanel: false);
    }

    private void RemoveSelectedStep()
    {
        if (IsEditingLocked || !TryCommitPendingActionEdits()) return;
        if (TryEditNested("remove")) return;
        if (SelectedStep == null || IsEditingLocked) return;
        int idx = Steps.IndexOf(SelectedStep);
        if (idx < 0) return;
        var removed = _actionStore[idx];
        _editHistory.Push(new RemoveEdit(idx, removed));
        Steps.RemoveAt(idx);
        SelectedStep = _actionStore.Count == 0 ? null : Steps[Math.Min(idx, _actionStore.Count - 1)];
        RefreshStepOrder();
        RaiseEditCommands();
    }

    private void DuplicateSelectedStep()
    {
        if (IsEditingLocked || !TryCommitPendingActionEdits()) return;
        if (TryEditNested("duplicate")) return;
        if (SelectedStep == null || IsEditingLocked) return;
        int idx = Steps.IndexOf(SelectedStep);
        if (idx < 0) return;
        var clone = MacroActionFactory.Clone(_actionStore[idx]);
        _editHistory.Push(new InsertEdit(idx + 1, clone));
        Steps.Insert(idx + 1, MacroStepItemViewModel.FromStep(clone, idx + 1, _actionStore[idx]));
        SelectedStep = Steps[idx + 1];
        RefreshStepOrder();
        RaiseEditCommands();
    }

    private void MoveStepUp()
    {
        if (IsEditingLocked || !TryCommitPendingActionEdits()) return;
        if (TryEditNested("up")) return;
        if (SelectedStep == null || IsEditingLocked) return;
        int idx = Steps.IndexOf(SelectedStep);
        if (idx > 0)
            MoveStep(idx, idx - 1);
    }

    private void MoveStepDown()
    {
        if (IsEditingLocked || !TryCommitPendingActionEdits()) return;
        if (TryEditNested("down")) return;
        if (SelectedStep == null || IsEditingLocked) return;
        int idx = Steps.IndexOf(SelectedStep);
        if (idx >= 0 && idx < Steps.Count - 1)
            MoveStep(idx, idx + 1);
    }

    private void MoveStepToTop()
    {
        if (IsEditingLocked || !TryCommitPendingActionEdits()) return;
        if (TryEditNested("top")) return;
        if (SelectedStep == null || IsEditingLocked) return;
        int idx = Steps.IndexOf(SelectedStep);
        if (idx > 0)
            MoveStep(idx, 0);
    }

    private void MoveStepToBottom()
    {
        if (IsEditingLocked || !TryCommitPendingActionEdits()) return;
        if (TryEditNested("bottom")) return;
        if (SelectedStep == null || IsEditingLocked) return;
        int idx = Steps.IndexOf(SelectedStep);
        if (idx >= 0 && idx < Steps.Count - 1)
            MoveStep(idx, Steps.Count - 1);
    }

    private void MoveStep(int from, int to)
    {
        _editHistory.Push(new MoveEdit(from, to));
        Steps.Move(from, to);
        SelectedStep = Steps[to];
        RefreshStepOrder();
        RaiseEditCommands();
    }

    private void UndoEdit()
    {
        if (IsEditingLocked) return;
        var edit = _editHistory.PopUndo();
        if (edit is null) return;
        edit.Revert(_actionStore);
        Steps.RefreshFromStore();
        _committingSelection = true;
        try
        {
            ReselectAfterHistory(edit, undone: true);
        }
        finally
        {
            _committingSelection = false;
        }
        RefreshStepOrder();
        RaiseEditCommands();
    }

    private void RedoEdit()
    {
        if (IsEditingLocked) return;
        var edit = _editHistory.PopRedo();
        if (edit is null) return;
        edit.Apply(_actionStore);
        Steps.RefreshFromStore();
        _committingSelection = true;
        try
        {
            ReselectAfterHistory(edit, undone: false);
        }
        finally
        {
            _committingSelection = false;
        }
        RefreshStepOrder();
        RaiseEditCommands();
    }

    private void ReselectAfterHistory(IMacroEdit edit, bool undone)
    {
        int index = edit switch
        {
            ReplaceAtEdit r => r.Index,
            CompoundEdit c => c.Index,
            InsertEdit i => undone ? Math.Max(0, i.Index - 1) : i.Index,
            RemoveEdit r => undone ? r.Index : Math.Min(r.Index, Math.Max(0, _actionStore.Count - 1)),
            MoveEdit m => undone ? m.From : m.To,
            _ => 0
        };
        if (_actionStore.Count == 0)
            SelectedStep = null;
        else
            SelectedStep = Steps[Math.Clamp(index, 0, _actionStore.Count - 1)];
    }

    private void RaiseEditCommands()
    {
        DuplicateStepCommand.RaiseCanExecuteChanged();
        MoveToTopCommand.RaiseCanExecuteChanged();
        MoveToBottomCommand.RaiseCanExecuteChanged();
        UndoEditCommand.RaiseCanExecuteChanged();
        RedoEditCommand.RaiseCanExecuteChanged();
        RemoveStepCommand.RaiseCanExecuteChanged();
        MoveUpCommand.RaiseCanExecuteChanged();
        MoveDownCommand.RaiseCanExecuteChanged();
    }

    public int SelectedStepIndex => RootIndexOf(SelectedStep);

    public MacroStep? GetStepAt(int index)
        => index >= 0 && index < _actionStore.Count ? (IsNestedSelection && index == SelectedStepIndex ? FindInTree(_actionStore[index], SelectedStep!.Step.StepId) : _actionStore[index]) : null;

    public IReadOnlyList<ActionRouteTarget> GetRouteTargets()
    {
        var duplicateIds = _actionStore.Items.GroupBy(step => step.StepId)
            .Where(group => group.Count() != 1).Select(group => group.Key).ToHashSet();
        var targets = new List<ActionRouteTarget>();
        for (int index = 0; index < _actionStore.Count; index++)
        {
            MacroStep step = _actionStore[index];
            if (!step.IsEnabled || duplicateIds.Contains(step.StepId)
                || step is WaitUntilStep { AppearanceRedirect: not null }) continue;
            string label = string.IsNullOrWhiteSpace(step.Label)
                ? MacroActionFactory.KindNames[(int)MacroActionFactory.KindOf(step)] : step.Label;
            targets.Add(new ActionRouteTarget(step.StepId, $"{index + 1}. {label}"));
        }
        return targets;
    }

    private MacroStep WithCameraPathDefaults(MouseTrajectoryStep path)
    {
        if (AppSettings is null)
            return path;

        return MacroActionFactory.UpdateTrajectory(
            path,
            AppSettings.CameraPlaybackHz,
            AppSettings.CameraLookGainX,
            AppSettings.CameraLookGainY,
            AppSettings.CameraPlaybackMode);
    }

    public void ReplaceStepAt(int index, MacroStep next, bool reloadPanel)
        => ReplaceStepAtCore(index, next, reloadPanel, null);

    internal bool TryApplyActionReplacement(int index, MacroStep expected, MacroStep next, AuthoringInteraction? owner)
        => ReferenceEquals(GetStepAt(index), expected) && ReplaceStepAtCore(index, next, reloadPanel: true, owner);

    private bool ReplaceStepAtCore(int index, MacroStep next, bool reloadPanel, AuthoringInteraction? owner)
    {
        if (owner is null ? IsEditingLocked : !OwnsAuthoringDraft(owner)) return false;
        if (index < 0 || index >= _actionStore.Count) return false;
        if (IsNestedSelection && index == SelectedStepIndex && SelectedStep is not null)
            return CommitNestedRoot(index, RewriteTree(_actionStore[index], SelectedStep.Step.StepId, _ => next), next.StepId, owner);
        var previous = _actionStore[index];
        if (ReferenceEquals(previous, next)) return true;
        _editHistory.Push(new ReplaceAtEdit(index, previous, next));
        Steps.ReplaceAt(index, next);
        if (!_committingSelection)
        {
            bool wasCommitting = _committingSelection;
            _committingSelection = true;
            try
            {
                SelectedStep = Steps[index];
                if (reloadPanel)
                    ActionEditor.LoadFrom(next);
            }
            finally
            {
                _committingSelection = wasCommitting;
            }
        }
        else if (reloadPanel)
        {
            ActionEditor.LoadFrom(next);
        }
        RefreshStepOrder();
        RaiseEditCommands();
        return true;
    }

    public void ChangeSelectedActionKind(EditableActionKind kind)
    {
        if (IsEditingLocked) return;
        int idx = SelectedStepIndex;
        var current = GetStepAt(idx);
        if (current is null) return;
        if (MacroActionFactory.KindOf(current) == kind) return;

        if (kind == EditableActionKind.ImageWait)
        {
            if (!TryPromptImageTemplate(out ImageTemplateSelection? selection) || selection is null)
                return;

            var converted = (WaitUntilStep)MacroActionFactory.Convert(current, kind);
            var next = MacroActionFactory.UpdateImageWait(
                converted,
                (int)converted.Timeout.TotalMilliseconds,
                (int)converted.PollInterval.TotalMilliseconds,
                converted.RequiredConfirmations,
                converted.RequiredFalseConfirmations,
                converted.StabilityDurationMs,
                converted.WaitWhile,
                new ImageCondition(selection.ImagePath, selection.TemplateBytes, selection.Confidence, selection.SourceRegion)
                { ScanOptions = selection.SourceRegion is null ? null : ImageScanOptions.ForPlacedArea() });
            ReplaceStepAt(idx, next, reloadPanel: true);
            StatusChanged?.Invoke($"Changed action to Image Wait ({Path.GetFileName(selection.ImagePath)}).");
            return;
        }

        var replaced = MacroActionFactory.Convert(current, kind);
        if (replaced is MouseTrajectoryStep path && current is not MouseTrajectoryStep)
            replaced = WithCameraPathDefaults(path);
        ReplaceStepAt(idx, replaced, reloadPanel: true);
    }

    public void InsertDefaultAction(EditableActionKind kind)
    {
        if (IsEditingLocked || !TryCommitPendingActionEdits()) return;
        if (kind == EditableActionKind.ImageWait)
        {
            AddImageStep();
            return;
        }
        int idx = SelectedStepIndex;
        MacroStep seed = GetStepAt(idx) ?? new DelayStep(TimeSpan.FromMilliseconds(250));
        var step = MacroActionFactory.Convert(seed, kind, newStepId: true);
        if (step is MouseTrajectoryStep inserted)
            step = WithCameraPathDefaults(inserted);
        if (TryInsertNested(step)) return;
        int insertAt = idx < 0 ? _actionStore.Count : idx + 1;
        _editHistory.Push(new InsertEdit(insertAt, step));
        Steps.Insert(insertAt, MacroStepItemViewModel.FromStep(step, insertAt, insertAt > 0 ? GetStepAt(insertAt - 1) : null));
        SelectedStep = Steps[insertAt];
        RefreshStepOrder();
        RaiseEditCommands();
        StatusChanged?.Invoke($"Inserted {MacroActionFactory.KindNames[(int)kind]}.");
    }

    private static int ParseKindParameter(object? p) => p switch
    {
        int i => i,
        string s when int.TryParse(s, out int n) => n,
        EditableActionKind e => (int)e,
        _ => -1
    };

    public void NotifyStatus(string message) => StatusChanged?.Invoke(message);

    public void CommitPendingActionEdits()
    {
        if (IsRecordingBusy || !ActionEditor.HasSelection)
            return;
        ActionEditor.TryCommit();
    }

    public bool TryCommitPendingActionEdits()
        => !IsRecordingBusy && (!ActionEditor.HasSelection || ActionEditor.TryCommit());

    #endregion

    #region Drag and Drop

    public void DragOver(IDropInfo dropInfo)
    {
        if (!IsEditingLocked && dropInfo.Data is MacroStepItemViewModel source && dropInfo.TargetCollection != null)
        {
            var entry = OutlineRows.Find(source.Step.StepId);
            var target = OutlineRows.EntryAt(Math.Clamp(dropInfo.InsertIndex, 0, OutlineRows.Count - 1));
            if (entry is null || target is null || entry.ParentId != target.ParentId || entry.Slot != target.Slot)
            {
                dropInfo.Effects = DragDropEffects.None;
                DragDestinationText = "This drop crosses block boundaries. Select the block and use Move following action into Then, Else, or Do.";
                return;
            }
            DragDestinationText = target.ParentId.HasValue
                ? $"Reorder within {target.Scope}: before action {target.ChildIndex + 1}. Enclosed actions move with their block."
                : $"Reorder the main sequence: before action {target.RootIndex + 1}. Enclosed actions move with their block.";
            dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
            dropInfo.Effects = DragDropEffects.Move;
        }
    }

    public void Drop(IDropInfo dropInfo)
    {
        DragDestinationText = "Drag to reorder within a scope. Use block properties to move actions into or out of a block.";
        if (IsEditingLocked) return;
        if (dropInfo.Data is not MacroStepItemViewModel src)
            return;
        var source = OutlineRows.Find(src.Step.StepId);
        var target = OutlineRows.EntryAt(Math.Clamp(dropInfo.InsertIndex, 0, OutlineRows.Count - 1));
        if (source is null || target is null || source.ParentId != target.ParentId || source.Slot != target.Slot) return;
        if (!TryCommitPendingActionEdits()) return;
        if (source.ParentId is Guid parentId)
        {
            var root = _actionStore[source.RootIndex];
            var parent = FindInTree(root, parentId)!;
            var children = GetBlockChildren(parent, source.Slot)!.ToList();
            var moved = children[source.ChildIndex];
            int destination = target.ChildIndex + (dropInfo.InsertIndex >= OutlineRows.Count ? 1 : 0);
            if (destination > source.ChildIndex) destination--;
            children.RemoveAt(source.ChildIndex); children.Insert(Math.Clamp(destination, 0, children.Count), moved);
            CommitNestedRoot(source.RootIndex, RewriteTree(root, parentId, _ => WithBlockChildren(parent, source.Slot, children)), moved.StepId);
            return;
        }
        int from = source.RootIndex;
        int to = dropInfo.InsertIndex >= OutlineRows.Count ? Steps.Count : target.RootIndex;
        if (from < 0 || to < 0)
            return;
        if (to > from)
            to--;
        if (from == to)
            return;

        MoveStep(from, to);
    }

    #endregion

    #region Persistence

    /// <summary>
    /// Saves the editor content back into the selected script's own library file, so the
    /// Save bar and the script list always refer to the same file on disk.
    /// </summary>
    private void SaveMacro()
    {
        if (!TryCommitPendingActionEdits()) return;
        if (_isPersistenceEnabled && SelectedScript != null)
        {
            SelectedScript.Name = MacroName;
            SelectedScript.Description = Description;
            CapturePlaybackSettingsTo(SelectedScript);
            SelectedScript.Actions.Clear();
            SelectedScript.Actions.AddRange(_actionStore.Items);
            SelectedScript.Steps.Clear();
            SelectedScript.ActionCount = SelectedScript.Actions.Count;
            if (!TrySaveMacroScript(SelectedScript)) return;

            StatusChanged?.Invoke($"Saved '{SelectedScript.Name}' ({SelectedScript.ActionCount} actions) to {Path.GetFileName(SelectedScript.FilePath)}");
            LogActivity($"Saved macro '{SelectedScript.Name}' to library file {SelectedScript.FilePath}.");
            return;
        }

        var model = new MacroModel
        {
            Name = MacroName,
            Description = Description,
            Steps = _actionStore.ToList()
        };

        string path = _documentStore.GetLibraryPath(model.Id);
        object? companionAttempt = null;
        try
        {
            CaptureCompanionSave(model, ref companionAttempt);
            SaveProtectedDocument(model, path, companionAttempt);
        }
        catch (Exception ex)
        {
            CompleteCompanionSave(null, null, companionAttempt);
            StatusChanged?.Invoke($"Could not save '{MacroName}': {ex.Message}");
            LogActivity($"Warning: Failed to save macro '{MacroName}': {ex.Message}");
            return;
        }
        CompleteCompanionSave(null, model, companionAttempt);
        StatusChanged?.Invoke($"Macro saved to {Path.GetFileName(path)}");
        LogActivity($"Saved macro '{MacroName}' to disk.");
    }

    private void LoadMacro()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Load Macro File",
            Filter = "Protected macros and backups (*.zmacro;*.zbackup)|*.zmacro;*.zbackup|Legacy macro files (*.json)|*.json|All Files (*.*)|*.*",
            InitialDirectory = AppDataPathResolver.Default.MacrosDirectory
        };

        if (dialog.ShowDialog() == true) ImportMacroFile(dialog.FileName);
    }

    /// <summary>Imports an owned library copy. Loading or deleting the copy never changes the source file.</summary>
    public bool ImportMacroFile(string sourcePath, string? passphrase = null)
        => ImportProtectedMacroFile(sourcePath, passphrase, static error => error.Message);

    private void ExportMacro()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export encrypted macro backup",
            Filter = "Encrypted macro backup (*.zbackup)|*.zbackup",
            DefaultExt = ".zbackup",
            AddExtension = true,
            FileName = $"{MacroName}.zbackup",
            InitialDirectory = AppDataPathResolver.Default.MacrosDirectory
        };

        if (dialog.ShowDialog() == true) ExportMacroFile(dialog.FileName);
    }

    public bool ExportMacroFile(string destinationPath, string? passphrase = null)
    {
        if (_libraryLoadDeferred) { NotifyStatus("Activate this installation before exporting the macro library."); return false; }
        if (!TryCommitPendingActionEdits()) return false;
        try
        {
            if (!Path.GetExtension(destinationPath).Equals(".zbackup", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose the .zbackup extension for encrypted backups.");
            passphrase ??= BackupPassphrasePrompter(true);
            if (passphrase is null) return false;
            var model = new MacroModel
            {
                Name = MacroName,
                RecordingCommit = SelectedScript?.RecordingCommit,
                Id = SelectedScript?.Id ?? Guid.NewGuid(),
                CreatedAt = SelectedScript?.CreatedAt ?? DateTimeOffset.UtcNow,
                IsFavorite = SelectedScript?.IsFavorite ?? false,
                Description = Description,
                Steps = _actionStore.ToList(),
                Settings = new MacroSettings
                {
                    RepeatCount = RepeatCount,
                    RepeatUntilStopped = RepeatUntilStopped,
                    PlaybackSpeed = PlaybackSpeed,
                    Target = SelectedScript?.Target,
                    Hotkey = CurrentHotkey,
                    IsActive = IsScriptEnabled,
                    MaxRuntime = SelectedScript?.MaxRuntime ?? TimeSpan.FromHours(1),
                    MaxIterations = SelectedScript?.MaxIterations ?? 100_000,
                    MaxLoopDepth = SelectedScript?.MaxLoopDepth ?? 8,
                    MouseSamplingMode = SelectedScript?.MouseSamplingMode ?? MouseSamplingMode.DistanceAndTime
                }
            };

            ValidateProtectedDocument(model);
            _documentStore.ExportBackup(new[] { model }, destinationPath, passphrase);
            StatusChanged?.Invoke($"Exported '{MacroName}' to {Path.GetFileName(destinationPath)}");
            LogActivity($"Exported macro package '{MacroName}' to {destinationPath}.");
            return true;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Could not export '{MacroName}': {ex.Message}");
            LogActivity($"Warning: Macro export failed: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Execution

    public async Task RunMacroAsync() => await RunMacroAsync(SelectedScript);

    public Task RunMacroAsync(MacroScriptItemViewModel? script)
    {
        if (_disposed || _shutdownStarted) return Task.CompletedTask;
        if (IsRunning || _playbackRunTask is { IsCompleted: false }) return _playbackRunTask ?? Task.CompletedTask;
        if (_producerAuthoringRequest is { Kind: HostInputOperationKind.Playback } pending) return pending.Work;
        script ??= SelectedScript;
        if (ActionEditor.RequiresImagePreparation) return StartProducerAfterImageDraft(HostInputOperationKind.Playback, script);
        return RunMacroWithGenerationAsync(script, InputOperations.StopGeneration);
    }

    private Task RunMacroWithGenerationAsync(MacroScriptItemViewModel? script, long generation, ProducerTargetPin? targetPin = null)
    {
        if (_disposed || _shutdownStarted) return Task.CompletedTask;
        if (!InputOperations.TryAcquire(HostInputOperationKind.Playback, generation, out var lease, out string reason))
        { NotifyStatus(reason); return Task.CompletedTask; }
        Task work;
        lock (_playbackSync)
        {
            var owned = new OwnedEditorPlayback(lease!);
            _ownedEditorPlayback = owned;
            work = owned.Work = _playbackRunTask = RunEditorPlaybackOwnedAsync(owned, script, targetPin);
        }
        RefreshInputOwnershipPresentation();
        return work;
    }
    private async Task RunMacroCoreAsync(MacroScriptItemViewModel? script, ResolvedMacroTarget? resolvedTarget,
        ProducerTargetPin? targetPin, OwnedEditorPlayback owned)
    {
        if (LibraryKeyUnavailable) { NotifyStatus("Restore the encrypted library key or a backup before running this macro."); return; }
        if (!await EnsureActivationForOperationAsync(owned.Lease.StopToken)) return;
        if (!owned.Lease.CanContinue || _disposed || _shutdownStarted) return;
        if (ActionEditor.IsTestingDetection)
        {
            NotifyStatus("Finish or cancel the detection test before running a macro.");
            return;
        }
        if (ExternalInputIsActive?.Invoke() == true)
        {
            NotifyStatus("Stop Auto Clicker before running a macro.");
            return;
        }
        if (_disposed) return;
        if (IsRunning) return;
        if (IsRecordingBusy)
        {
            StatusChanged?.Invoke("Cannot play macro while recording is active. Stop recording first.");
            LogActivity("Playback blocked: recording is active.");
            return;
        }

        if (script != null && script != SelectedScript)
        {
            _committingOwnedInputDraft = true;
            try { SelectedScript = script; }
            finally { _committingOwnedInputDraft = false; }
            if (!ReferenceEquals(SelectedScript, script)) return;
        }

        var runningScript = SelectedScript ?? script;
        if (runningScript != null && !runningScript.IsActive)
        {
            StatusChanged?.Invoke("Macro is currently disabled. Toggle to 'Active' to execute.");
            LogActivity($"Playback skipped: '{runningScript.Name}' is disabled.");
            return;
        }

        if (!IsScriptEnabled && runningScript == null)
        {
            StatusChanged?.Invoke("Macro is currently disabled. Toggle to 'Active' to execute.");
            LogActivity($"Playback skipped: '{MacroName}' is disabled.");
            return;
        }

        // Commit visible property-panel edits before taking the execution snapshot.
        if (!CommitOwnedInputDraft()) return;
        if (targetPin is not null && (!ReferenceEquals(runningScript, script)
            || !ReferenceEquals(runningScript, SelectedScript) || !Equals(runningScript?.Target, targetPin.RequestedRule))) return;

        // Flush the editor into the selected script, then run THAT script's actions.
        // This keeps unsaved edits and prevents a leftover editor list from playing
        // when a different macro's hotkey was pressed.
        if (runningScript is not null && runningScript == SelectedScript)
        {
            runningScript.Name = MacroName;
            runningScript.Description = Description;
            runningScript.Actions.Clear();
            runningScript.Actions.AddRange(_actionStore.Items);
        }

        var stepsToRun = runningScript != null && runningScript.Actions.Count > 0
            ? runningScript.Actions.ToList()
            : _actionStore.ToList();
        // Library selection may change while the studio transitions to the Mini toolbar.
        // Freeze the whole playback configuration together with the selected action tree.
        var runDescription = Description;
        var runSettings = new MacroSettings
        {
            Target = runningScript?.Target,
            RepeatCount = RepeatCount,
            RepeatUntilStopped = RepeatUntilStopped,
            PlaybackSpeed = PlaybackSpeed,
            MaxRuntime = runningScript?.MaxRuntime ?? TimeSpan.FromHours(1),
            MaxIterations = runningScript?.MaxIterations ?? 100_000,
            MaxLoopDepth = runningScript?.MaxLoopDepth ?? 8
        };

        if (stepsToRun.Count == 0)
        {
            StatusChanged?.Invoke("Macro has no steps to execute.");
            return;
        }

        if (!owned.Lease.CanContinue) return;
        if (_inputPlayer is IHostInputOperationAwarePlayer aware)
            owned.NativeAttachment = aware.AttachHostOperation(owned.Lease);
        MacroController controller;
        lock (_playbackSync)
        {
            if (_disposed) return;
            controller = new MacroController(_inputPlayer, owned.Lease);
            owned.Controller = controller;
            owned.StopRegistration = owned.Lease.StopToken.Register(static value => ((MacroController)value!).SignalStop(), controller);
            _activeController = controller;
        }
        _runningMacroName = runningScript?.Name ?? MacroName;
        _runningMacroHotkey = runningScript?.Hotkey ?? CurrentHotkey;
        _runningMacroId = runningScript?.Id;
        _failedStepId = null; _failedMacroId = null; LastExecutionError = string.Empty;
        OutlineRows.SetFailure(null, string.Empty);
        _executingActionNumber = 0;
        _presentationRunEnded = false;
        _runStartTimestamp = 0;
        _progressUpdateTimestamp = 0;
        RunProgressText = "Preparing playback…";
        IsRunning = true;
        _recordingTerminalReason = string.Empty; SetRecordingPhase("Idle");
        InputModeGate.Set(InputMode.MacroExecuting);
        IsPaused = false;
        if (runningScript != null)
            runningScript.IsRunning = true;

        PlaybackSessionContext? playbackSession = null;
        BoundedExecutionStatusSink? statusSink = null;
        EditorExecutionStatusSink? presentationSink = null;
        Task? statusDrain = null;
        Task? executionTask = null;
        string runName = runningScript?.Name ?? MacroName;
        string? finalStatus = null;
        string? terminalError = null;
        try
        {
            playbackSession = new PlaybackSessionContext(controller, resolvedTarget is null ? null : Targets, resolvedTarget);
            playbackSession.BindHostOperation(owned.Lease);
            owned.Session = playbackSession;
            _playbackSession = playbackSession;
            playbackSession.TargetPaused += PlaybackTargetPaused;
            if (_inputPlayer is IPlaybackSessionAwarePlayer) owned.SessionAttachment = playbackSession.Attach(_inputPlayer);
            // Hide the studio before the first click so playback cannot land on our own UI.
            if (EnterCompactOverlay != null)
            {
                EnterCompactOverlay.Invoke();
                await Task.Delay(120, controller.StopToken).ConfigureAwait(true);
            }

            if (_disposed) return;
            controller.StopToken.ThrowIfCancellationRequested();
            if (resolvedTarget?.Rule.Mode is not (null or MacroTargetMode.Screen) && !Targets.TryActivate(resolvedTarget))
                throw new InvalidOperationException("Focus the selected target window, then run again.");

            statusSink = new BoundedExecutionStatusSink();
            presentationSink = new EditorExecutionStatusSink(statusSink);

            var settings = runSettings;

            var context = new MacroContext(_captureService, controller)
            {
                PlaybackSession = playbackSession,
                InputPlayer = _inputPlayer,
                TimerResolution = new Win32TimerResolution(),
                VisionRouter = _visionRouter,
                ApprovalGate = _approvalGate,
                FeatureGate = _featureGate,
                ActivationService = ActivationService,
                UsageTracker = _usageTracker,
                AuditLogger = _auditLogger,
                ImageMatcher = _imageMatcher,
                ImageEncoder = _imageEncoder,
                ClickRefiner = _clickRefiner,
                StatusSink = presentationSink,
                Trace = AppSettings?.DetailedExecutionTrace == true
                    ? new DelegateExecutionTraceSink(trace =>
                    {
                        if (_disposed) return;
                        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(() =>
                        {
                            if (_disposed || _presentationRunEnded) return;
                            string line = $"[{trace.Category}] {trace.Message}";
                            LogActivity(line);
                            if (trace.Category is "ConditionWait" or "VisualDetection")
                                StatusChanged?.Invoke(trace.Message);
                        });
                    })
                    : null,
                AutonomousMode = AppSettings?.AutonomousModeActive ?? false,
                ApprovalTimeout = TimeSpan.FromSeconds(AppSettings?.ApprovalTimeoutSeconds ?? 10),
                PlaybackSpeed = settings.PlaybackSpeed,
                MaxRuntime = settings.MaxRuntime,
                MaxIterations = settings.MaxIterations,
                MaxLoopDepth = settings.MaxLoopDepth,
                EnforceRuntimeCap = !settings.RepeatUntilStopped,
                ForegroundWindow = Win32ForegroundWindowInfo.Default,
                WindowCoordinates = WindowCoordinateService.Default,
                ActionValidator = new AIActionValidator
                {
                    MaxTextLength = AppSettings?.MaxAiTextLength ?? 200,
                    MinConfidenceThreshold = AppSettings?.MinAiConfidence ?? 0.50
                }
            };

            _presentationRunId = context.RunId;
            _runStartTimestamp = Stopwatch.GetTimestamp();
            if (statusSink is not null)
                statusDrain = DrainExecutionStatusAsync(statusSink, CreateVisibleActionIndex(stepsToRun));

            var executor = new MacroExecutor();
            executor.StepStarting += (_, args) =>
            {
                if (_disposed) return;
                if (args.Step is MouseMoveStep)
                    return;
                if (args.StepIndex >= 0)
                {
                    DispatchEditorPresentation(() =>
                    {
                        if (!_disposed && ReferenceEquals(_ownedEditorPlayback, owned) && !_presentationRunEnded && IsRunning && args.StepIndex < stepsToRun.Count)
                        {
                            SetExecutingAction(args.Step.StepId, args.StepIndex);
                        }
                    });
                }
            };

            var model = new MacroModel
            {
                Name = runName,
                Description = runDescription,
                Steps = stepsToRun,
                Settings = settings
            };

            string repeatDesc = settings.RepeatUntilStopped ? "Until stopped" : $"Repeat={settings.RepeatCount}";
            StatusChanged?.Invoke($"Executing '{runName}' ({stepsToRun.Count} steps, {repeatDesc}, Speed={settings.PlaybackSpeed:F1}x)...");
            LogActivity($"Running '{runName}' ({stepsToRun.Count} steps, {repeatDesc}, Speed={settings.PlaybackSpeed:F1}x).");

            // Camera timing loops must not occupy the WPF dispatcher.
            lock (_playbackSync)
            {
                if (_disposed) return;
                _activeStatusSink = statusSink;
                executionTask = Task.Run(() => executor.ExecuteAsync(model, context), context.StopToken);
                _playbackExecutionTask = executionTask;
            }
            await executionTask;
            _presentationRunEnded = true;
            if (_disposed) return;
            if (controller.IsPanicTriggered || executor.State == ExecutionState.Aborted)
            {
                finalStatus = "EMERGENCY PANIC TERMINATION TRIGGERED!";
                LogActivity($"Execution of '{runName}' was aborted by emergency panic.");
            }
            else if (executor.State == ExecutionState.Stopped)
            {
                finalStatus = "Macro stopped.";
                LogActivity($"Execution of '{runName}' stopped.");
            }
            else
            {
                TotalRunsCount++;
                CurrentLastRun = "Just now";
                if (runningScript != null)
                    runningScript.LastRunText = "Just now";
                finalStatus = $"'{runName}' completed successfully.";
                LogActivity($"Finished execution of '{runName}'. Total runs: {TotalRunsCount}");
            }
        }
        catch (OperationCanceledException)
        {
            _presentationRunEnded = true;
            if (!_disposed)
            {
                finalStatus = "Macro stopped or cancelled.";
                LogActivity($"Execution of '{runName}' stopped.");
            }
        }
        catch (Exception ex)
        {
            _presentationRunEnded = true;
            if (!_disposed)
            {
                finalStatus = $"Macro error: {ex.Message}";
                terminalError = ex.Message;
                LogActivity($"Error in '{runName}': {ex.Message}");
            }
            else Trace.TraceError($"Playback shutdown fault: {ex}");
        }
        finally
        {
            _presentationRunEnded = true;
            lock (_playbackSync)
            {
                if (ReferenceEquals(_playbackExecutionTask, executionTask)) _playbackExecutionTask = null;
                if (ReferenceEquals(_activeStatusSink, statusSink)) _activeStatusSink = null;
            }
            await CleanupEditorPlaybackAsync(owned);
            if (!owned.CleanupSucceeded)
            { finalStatus = "Input cleanup needs recovery. New input operations remain blocked."; terminalError = finalStatus; }
            statusSink?.Complete();
            if (statusDrain is not null)
            {
                try
                {
                    await statusDrain.ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    // Channel closed after Complete().
                }
            }
            if (!_disposed)
            {
                if (presentationSink?.FirstFailure is { ActionId: { } failedActionId } failed)
                    RecordActionFailure(failedActionId, failed.Message ?? "This action failed.");
                else if (terminalError is not null)
                    LastExecutionError = $"Stopped: {terminalError} Open the Log tab for details.";
                if (_runStartTimestamp != 0)
                    RunProgressText = $"Run elapsed: {DurationDisplay.Format(Stopwatch.GetElapsedTime(_runStartTimestamp).TotalMilliseconds)}";
                if (runningScript != null) runningScript.IsRunning = false;
                foreach (var s in MacroScripts) s.IsRunning = false;
                IsRunning = false;
                IsPaused = false;
                _executingStepId = null;
                OutlineRows.SetExecution(null);
                if (owned.Lease.OwnsCleanup && InputModeGate.Current == InputMode.MacroExecuting) InputModeGate.Set(InputMode.Normal);
                // Publish after terminal state notifications. Queued telemetry must not
                // replace an error or an explicit stop with a generic Ready message.
                if (finalStatus is not null) StatusChanged?.Invoke(finalStatus);
                DetectionOverlayStopped?.Invoke();
                if (!IsRecording) LeaveCompactOverlay?.Invoke();
            }
        }
    }

    private async Task DrainExecutionStatusAsync(BoundedExecutionStatusSink sink, IReadOnlyDictionary<Guid, int> actionRoots)
    {
        ExecutionStatusSnapshot? latest = null;
        int dispatchQueued = 0;
        try
        {
            await foreach (ExecutionStatusSnapshot snapshot in sink.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (_disposed) continue;
                if (snapshot.ActionId is { } id && !actionRoots.ContainsKey(id) && snapshot.Phase != "ActionFailed") continue;
                Volatile.Write(ref latest, snapshot);
                // Keep at most one pending UI update, replacing stale progress. A time-only
                // throttle can lose the start of a long child action that follows a fast one.
                if (Interlocked.Exchange(ref dispatchQueued, 1) != 0) continue;
                DispatchEditorPresentation(() =>
                {
                    Interlocked.Exchange(ref dispatchQueued, 0);
                    ExecutionStatusSnapshot? captured = Interlocked.Exchange(ref latest, null);
                    if (_disposed || _presentationRunEnded || captured is null || captured.RunId != _presentationRunId) return;
                    ExecutionStatusPublished?.Invoke(captured);
                    UpdateRunProgress(captured);
                    if (captured.ActionId is { } actionId && IsRunning)
                    {
                        if (actionRoots.TryGetValue(actionId, out int rootIndex)) SetExecutingAction(actionId, rootIndex);
                    }
                    if (captured.Detection is { } detection)
                        DetectionOverlayUpdated?.Invoke(detection);
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the run completes and the channel is closed.
        }
    }

    public async Task ToggleScriptAsync(MacroScriptItemViewModel? script, Func<bool>? canContinue = null)
    {
        if (_disposed || canContinue?.Invoke() == false) return;
        script ??= SelectedScript;
        if (script == null) return;

        if (IsRunning)
        {
            bool isCurrentTarget = (SelectedScript == script || script.IsRunning);
            await StopMacroAsync();

            if (_disposed || canContinue?.Invoke() == false) return;

            if (isCurrentTarget)
            {
                return;
            }

            // User pressed hotkey for a different script while one was running
            await Task.Delay(100);
        }

        if (_disposed || canContinue?.Invoke() == false) return;
        SelectedScript = script;
        if (script.Actions.Count > 0 || Steps.Count > 0)
        {
            // RunMacroAsync tracks its background executor separately for shutdown;
            // toggling a script returns after starting it so another toggle can stop it.
            _ = RunMacroAsync(script);
        }
        else
        {
            StatusChanged?.Invoke($"'{script.Name}' has no actions yet — record or add actions before running.");
            LogActivity($"Run requested for '{script.Name}' but it contains no actions.");
        }
    }

    private async Task PauseMacroAsync()
    {
        if (_disposed) return;
        if (IsRecording) { ToggleRecordingPause(); return; }
        if (_activeController == null) return;

        if (IsPaused)
        {
            if (_playbackSession is { } session)
            {
                await session.AwaitReleasedAsync(_activeController.StopToken);
                EnterCompactOverlay?.Invoke();
                if (session.Target?.Rule.Mode is not (null or MacroTargetMode.Screen) && !Targets.TryActivate(session.Target))
                { NotifyStatus("Focus the selected target window, then resume manually."); return; }
                bool neutral = await PhysicalInputReadiness.WaitForNeutralAsync(TimeSpan.FromMilliseconds(750), _activeController.StopToken);
                if (!await session.TryResumeAsync(neutral, _activeController.StopToken))
                { NotifyStatus(session.PauseReason ?? "Release held keys and mouse buttons, focus the selected target, then resume again."); return; }
            }
            else await _activeController.ResumeAsync();
            IsPaused = false;
            StatusChanged?.Invoke("Macro resumed.");
        }
        else
        {
            await _activeController.PauseAsync();
            if (_playbackSession is { } pausedSession) await pausedSession.AwaitReleasedAsync(_activeController.StopToken);
            IsPaused = true;
            StatusChanged?.Invoke("Macro paused.");
        }
    }

    public async Task StopMacroAsync()
    {
        if (_disposed) return;
        if (_producerAuthoringRequest is { } authoring)
        {
            authoring.Owner.Lease.SignalStop();
            await authoring.Work;
            return;
        }
        var owned = _ownedEditorPlayback;
        owned?.Lease.SignalStop();
        if (IsRecordingBusy || _recordingInputOperation is { Completion.IsCompleted: false })
            await StopRecordingAsync();

        if (_activeController != null)
        {
            await _activeController.StopAsync();
            IsPaused = false;
            StatusChanged?.Invoke("Stopping macro...");
        }
        if (owned?.Work is { } work) await work;
    }

    public void PanicMacro()
    {
        if (_disposed) return;
        SignalEmergencyStop();
        ActionEditor.CancelDetectionTest();
        if (_ownedEditorPlayback?.Lease.OwnsCleanup == true || _recordingInputOperation?.OwnsCleanup == true)
            InputModeGate.Set(InputMode.PanicStopping);
        _ = StopRecordingAsync();
        try
        {
            _activeController?.SignalPanic();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Panic failed to abort the active macro controller: {ex}");
        }

        CancelLocationPick();
        foreach (var s in MacroScripts)
        {
            s.IsRunning = false;
        }
        _recordingTerminalReason = "Stopped by emergency stop";
        StatusChanged?.Invoke("Emergency stop initiated. Accepted recording input is being preserved.");
    }

    public void Dispose()
    {
        DetachActivationService();
        ActionEditor.DisposeImageSetup();
        ActionEditor.StopDetectionTestForShutdown();
        Steps.CollectionChanged -= OnOutlineSourceChanged;
        MacroController? controller;
        Task? playback;
        lock (_playbackSync)
        {
            if (_disposed) return;
            _disposed = true;
            controller = _activeController;
            playback = _playbackExecutionTask;
            _activeStatusSink?.Complete();
        }
        CloseCompanionCatalog();
        _ownedEditorPlayback?.Lease.SignalStop();
        _recordingInputOperation?.SignalStop();
        _editorCaptureLease?.SignalStop(); _editorSnipLease?.SignalStop();
        _editorInteractionLease?.SignalStop(); _editorPickerLease?.SignalStop();
        if (_ownsInputOperations) InputOperations.Close();
        try { controller?.SignalPanic(); }
        catch (Exception ex) { Trace.TraceError($"Playback shutdown could not cancel its controller: {ex}"); }
        // Drain captured input while Recording mode still permits its accepted tail.
        DisposeRecording();
        try
        {
            // Join only the background executor, never the UI continuation that awaits
            // it. App.OnExit disposes capture/input services immediately after this call.
            playback?.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Trace.TraceError($"Playback shutdown did not finish cleanly: {ex}"); }
        if ((playback is null || playback.IsCompleted) && _ownedEditorPlayback?.Work is not { IsCompleted: false })
        {
            lock (_playbackSync)
            {
                if (ReferenceEquals(_activeController, controller)) _activeController = null;
                if (ReferenceEquals(_playbackExecutionTask, playback)) _playbackExecutionTask = null;
            }
            controller?.Dispose();
        }
        if (_uiDispatcher is null || _uiDispatcher.CheckAccess())
        {
            IsRunning = false;
            IsPaused = false;
            foreach (var script in MacroScripts) script.IsRunning = false;
        }
    }

    private void DisposeRecording()
    {
        var inputOwner = _recordingInputOperation;
        if (inputOwner?.OwnsCleanup != true) return;
        CloseRecordingAdmission();
        var deadline = Stopwatch.StartNew();
        if (_uiUpdateTimer != null)
        {
            _uiUpdateTimer.Tick -= OnRecordingUiTimerTick;
            _uiUpdateTimer.Stop();
            _uiUpdateTimer = null;
        }
        try
        {
            IsRecording = false;
            // Startup cancellation may be suspended on the UI dispatcher; never join
            // that continuation here. Its disposed/token checks prevent late activation.
            if (_isStartingRecording) _recordingCts?.Cancel();
            if (_recordingCaptureStopTask is not { IsCompleted: false })
                _recordingCaptureStopTask = _inputRecorder.StopAsync();
            _recordingCaptureStopTask.WaitAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _recordingFailure = $"Recording shutdown could not stop capture cleanly: {ex.Message}";
            Trace.TraceError($"MacroEditorViewModel.Dispose failed to stop recording: {ex}");
        }
        finally { _recordingInputBuffer?.TryComplete(); }

        try
        {
            // Join only the background consumer. It never waits for the dispatcher.
            if (_recordingConsumerTask is { } consumer)
            {
                var remaining = TimeSpan.FromSeconds(5) - deadline.Elapsed;
                if (remaining <= TimeSpan.Zero) remaining = TimeSpan.FromMilliseconds(1);
                consumer.WaitAsync(remaining).GetAwaiter().GetResult();
            }
            if (_uiDispatcher == null || _uiDispatcher.CheckAccess())
            {
                var tail = new List<MacroStep>();
                while (_recordedStagingQueue.TryDequeue(out var step)) tail.Add(step);
                if (tail.Count > 0)
                {
                    Steps.AddSteps(tail, notify: false);
                    Steps.RefreshFromStore();
                    if (_isPersistenceEnabled && SelectedScript != null) TrySaveMacroScript(SelectedScript);
                }
            }
        }
        catch (Exception ex)
        {
            _recordingFailure = $"Recording shutdown exceeded its drain deadline; this recording may be incomplete: {ex.Message}";
            Trace.TraceError(_recordingFailure);
        }
        finally
        {
            _recordingCts?.Cancel();
            try { _ownedRecording?.Desktop?.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult(); }
            catch (Exception ex) { Trace.TraceError("Recording observation cleanup failed: " + ex.Message); }
            if (inputOwner.OwnsCleanup && InputModeGate.Current == InputMode.Recording) InputModeGate.Set(InputMode.Normal);
        }
    }

    #endregion
}
