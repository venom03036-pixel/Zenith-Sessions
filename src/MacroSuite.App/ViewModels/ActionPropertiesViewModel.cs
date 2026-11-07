using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using MacroSuite.App.Common;
using MacroSuite.App.Overlays;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Vision;

namespace MacroSuite.App.ViewModels;

/// <summary>Draft fields for the selected action. Commits replace the store entry at the same index.</summary>
public sealed partial class ActionPropertiesViewModel : ObservableObject
{
    private readonly MacroEditorViewModel _editor;
    private bool _loading;
    private int _kindIndex;
    private int _x;
    private int _y;
    private string _xVariable = string.Empty;
    private string _yVariable = string.Empty;
    private int _clickOffsetX;
    private int _clickOffsetY;
    private bool _clickIsClientRelative;
    private (string X, string Y, int OffsetX, int OffsetY, bool ClientRelative)? _previousClickTarget;
    private int _lookPeakXMin;
    private int _lookPeakXMax;
    private int _lookPeakYMin;
    private int _lookPeakYMax;
    private int _width = 1;
    private int _height = 1;
    private int _durationMs = 250;
    private double _sensitivityX = MouseTrajectoryStep.DefaultLookGainX;
    private double _sensitivityY = MouseTrajectoryStep.DefaultLookGainY;
    private int _playbackHz = MouseSweepMotion.DefaultHz;
    private int _trajectoryModeIndex;
    private int _interpolationIndex = 4;
    private int _horizontalSmoothing = MouseSweepMotion.DefaultHorizontalSmoothing;
    private int _verticalSmoothing = MouseSweepMotion.DefaultVerticalSmoothing;
    private bool _filterVerticalJitter = true;
    private bool _filterHorizontalJitter;
    private int _profileIndex = 1;
    private int _clickCount = 1;
    private int _mouseButtonIndex;
    private string _keyName = "ENTER";
    private int _keyCode = 13;
    private int _similarityPercent = 90;
    private int _timeoutMs = 10_000;
    private int _searchModeIndex;
    private int _onTimeoutIndex;
    private int _r = 63;
    private int _g = 167;
    private int _b = 214;
    private int _tolerance;
    private int _detectionModeIndex;
    private double _minimumMatchPercentage = 0.05;
    private string _paletteText = string.Empty;
    private int _requiredConfirmations = 1;
    private int _requiredFalseConfirmations = 1;
    private int _pollIntervalMs = 150;
    private int _loopCheckIntervalMs = 50;
    private int _sampleStride = 1;
    private int _stabilityDurationMs;
    private int _failurePolicyIndex;
    private int _conditionTimeoutMs = 10_000;
    private string _prompt = "";
    private string _expectedWindow = "";
    private string _minConfidenceText = "";
    private bool _maximumAccuracy;
    private string _pickLocationButtonText = "Pick Location";
    private string _pickHint = "";
    private bool _isPickingLocation;
    private bool _isCapturingKey;
    private int _strokeIndex;
    private int _conditionKindIndex;
    private bool _waitWhile;
    private bool _repeatImageWhileVisible;
    private int _imageRepeatDelayMs = 500;
    private bool _redirectOnAppearance;
    private Guid? _appearanceTargetStepId;
    private int _appearanceCooldownMs = 500;
    private int _onNotSatisfiedRouteIndex;
    private int _onFailureRouteIndex;
    private Guid? _routeTargetStepId;
    private int _routeRetryCount = 3;
    private int _routeRetryDelayMs = 100;
    private int _minBodyDelayMs;
    private string _thenSummary = "THEN: (empty)";
    private string _elseSummary = "ELSE: (empty)";
    private string _bodySummary = "DO: (empty)";
    private string? _currentPresenceTemplatePath;
    private byte[]? _currentPresenceTemplateBytes;
    private double _currentPresenceMinConfidence = 0.8;
    private string _imageTemplateSummary = string.Empty;
    private ImageCondition? _pendingImageCondition;
    private System.Drawing.Rectangle? _imageSearchRegion;
    private Guid? _sourceStepId;
    private string? _draftError;
    private ICondition? _preservedCondition;
    private bool _preserveOriginalCondition;

    public ActionPropertiesViewModel(MacroEditorViewModel editor)
    {
        _editor = editor;
        KindNames = MacroActionFactory.KindNames;
        ApplyCommand = new RelayCommand(() => Apply(), () => HasSelection && !_editor.IsEditingLocked);
        CancelCommand = new RelayCommand(CancelEdits);
        ArmPickLocationCommand = new RelayCommand(async () => await ArmPickLocationAsync(), () => HasSelection && !_editor.IsEditingLocked && !_isPickingLocation);
        CaptureImageTemplateCommand = new RelayCommand(CaptureImageTemplate, () => HasSelection && !_editor.IsEditingLocked);
        ShowImageSearchAreaCommand = new RelayCommand(ShowImageSearchArea, CanEditImageSearchArea);
        ChooseImageSearchAreaCommand = new RelayCommand(ChooseImageSearchArea, CanEditImageSearchArea);
        ResetImageSearchAreaCommand = new RelayCommand(ResetImageSearchArea, CanEditImageSearchArea);
        InitializeImageSetup();
        ArmCaptureKeyCommand = new RelayCommand(() => IsCapturingKey = true, () => HasSelection && !_editor.IsEditingLocked);
        AddThenWaitCommand = new RelayCommand(() => _editor.AppendToSelectedBlock("then", new DelayStep(TimeSpan.FromMilliseconds(100))));
        AddThenKeyCommand = new RelayCommand(() => _editor.AppendToSelectedBlock("then", new KeyPressStep(_keyCode)));
        AddThenClickCommand = new RelayCommand(() => _editor.AppendToSelectedBlock("then", new ClickStep(new System.Drawing.Point(_x, _y))));
        AddElseWaitCommand = new RelayCommand(() => _editor.AppendToSelectedBlock("else", new DelayStep(TimeSpan.FromMilliseconds(100))));
        AddElseKeyCommand = new RelayCommand(() => _editor.AppendToSelectedBlock("else", new KeyPressStep(_keyCode)));
        AddBodyWaitCommand = new RelayCommand(() => _editor.AppendToSelectedBlock("body", new DelayStep(TimeSpan.FromMilliseconds(50))));
        AddBodyKeyDownCommand = new RelayCommand(() => _editor.AppendToSelectedBlock("body", new KeyPressStep(_keyCode) { Stroke = "Down" }));
        AddBodyKeyUpCommand = new RelayCommand(() => _editor.AppendToSelectedBlock("body", new KeyPressStep(_keyCode) { Stroke = "Up" }));
        AddBodyKeyPressCommand = new RelayCommand(() => _editor.AppendToSelectedBlock("body", new KeyPressStep(_keyCode)));
        AddBodyBreakCommand = new RelayCommand(() => _editor.AppendToSelectedBlock("body", new BreakStep()));
        AddBodyContinueCommand = new RelayCommand(() => _editor.AppendToSelectedBlock("body", new ContinueStep()));
        MoveNextIntoThenCommand = new RelayCommand(() => _editor.MoveNextIntoSelectedBlock("then"));
        MoveNextIntoElseCommand = new RelayCommand(() => _editor.MoveNextIntoSelectedBlock("else"));
        MoveNextIntoBodyCommand = new RelayCommand(() => _editor.MoveNextIntoSelectedBlock("body"));
        RemoveLastThenCommand = new RelayCommand(() => _editor.RemoveLastFromSelectedBlock("then"));
        RemoveLastElseCommand = new RelayCommand(() => _editor.RemoveLastFromSelectedBlock("else"));
        RemoveLastBodyCommand = new RelayCommand(() => _editor.RemoveLastFromSelectedBlock("body"));
        InitializeDetectionPresentation();
    }

    public IReadOnlyList<string> KindNames { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ArmPickLocationCommand { get; }
    public RelayCommand CaptureImageTemplateCommand { get; }
    public RelayCommand ShowImageSearchAreaCommand { get; }
    public RelayCommand ChooseImageSearchAreaCommand { get; }
    public RelayCommand ResetImageSearchAreaCommand { get; }
    public RelayCommand ArmCaptureKeyCommand { get; }
    public RelayCommand AddThenWaitCommand { get; }
    public RelayCommand AddThenKeyCommand { get; }
    public RelayCommand AddThenClickCommand { get; }
    public RelayCommand AddElseWaitCommand { get; }
    public RelayCommand AddElseKeyCommand { get; }
    public RelayCommand AddBodyWaitCommand { get; }
    public RelayCommand AddBodyKeyDownCommand { get; }
    public RelayCommand AddBodyKeyUpCommand { get; }
    public RelayCommand AddBodyKeyPressCommand { get; }
    public RelayCommand AddBodyBreakCommand { get; }
    public RelayCommand AddBodyContinueCommand { get; }
    public RelayCommand MoveNextIntoThenCommand { get; }
    public RelayCommand MoveNextIntoElseCommand { get; }
    public RelayCommand MoveNextIntoBodyCommand { get; }
    public RelayCommand RemoveLastThenCommand { get; }
    public RelayCommand RemoveLastElseCommand { get; }
    public RelayCommand RemoveLastBodyCommand { get; }

    public bool HasSelection => _editor.SelectedStepIndex >= 0;
    public bool HasNoSelection => !HasSelection;

    public int KindIndex
    {
        get => _kindIndex;
        set
        {
            if (!SetProperty(ref _kindIndex, value)) return;
            RaiseKindVis();
        }
    }

    /// <summary>
    /// Image Wait needs a live template. Changing the combo to that type starts capture immediately
    /// so swapping a key press actually becomes a real Image Wait instead of a draft.
    /// Other kinds stay draft until Apply.
    /// </summary>
    public void CommitKindChange()
    {
        if (_loading || !HasSelection || _editor.IsRecordingBusy)
            return;
        if (Kind == EditableActionKind.ImageWait)
            Apply();
    }

    public int X { get => _x; set => SetProperty(ref _x, value); }
    public int Y { get => _y; set => SetProperty(ref _y, value); }
    public string XVariable
    {
        get => _xVariable;
        set { if (SetProperty(ref _xVariable, value?.Trim() ?? string.Empty)) RaiseClickTargetFields(); }
    }
    public string YVariable
    {
        get => _yVariable;
        set { if (SetProperty(ref _yVariable, value?.Trim() ?? string.Empty)) RaiseClickTargetFields(); }
    }
    public int ClickOffsetX
    {
        get => _clickOffsetX;
        set { if (SetProperty(ref _clickOffsetX, value)) OnPropertyChanged(nameof(ShowClickOffsetFields)); }
    }
    public int ClickOffsetY
    {
        get => _clickOffsetY;
        set { if (SetProperty(ref _clickOffsetY, value)) OnPropertyChanged(nameof(ShowClickOffsetFields)); }
    }
    public bool ClickDetectedImage
    {
        get => ShowClickFields
            && _xVariable.TrimStart('$').Equals(ImageCondition.LastMatchXVariable, StringComparison.OrdinalIgnoreCase)
            && _yVariable.TrimStart('$').Equals(ImageCondition.LastMatchYVariable, StringComparison.OrdinalIgnoreCase);
        set
        {
            if (_loading || value == ClickDetectedImage) return;
            if (value)
            {
                _previousClickTarget = (_xVariable, _yVariable, _clickOffsetX, _clickOffsetY, _clickIsClientRelative);
                _xVariable = ImageCondition.LastMatchXVariable;
                _yVariable = ImageCondition.LastMatchYVariable;
                // Image matches publish desktop coordinates, not window-client coordinates.
                _clickIsClientRelative = false;
            }
            else
            {
                var previous = _previousClickTarget ?? (string.Empty, string.Empty, 0, 0, false);
                (_xVariable, _yVariable, _clickOffsetX, _clickOffsetY, _clickIsClientRelative) = previous;
                _previousClickTarget = null;
            }
            OnPropertyChanged(nameof(XVariable));
            OnPropertyChanged(nameof(YVariable));
            OnPropertyChanged(nameof(ClickOffsetX));
            OnPropertyChanged(nameof(ClickOffsetY));
            RaiseClickTargetFields();
        }
    }
    private void RaiseClickTargetFields()
    {
        OnPropertyChanged(nameof(ClickDetectedImage));
        OnPropertyChanged(nameof(ShowClickOffsetFields));
        OnPropertyChanged(nameof(ShowPointFields));
    }
    public int LookPeakXMin
    {
        get => _lookPeakXMin;
        set => SetProperty(ref _lookPeakXMin, (int)Math.Clamp(Math.Min(0, value), -MouseTrajectoryPlayback.MaxExtentPx, 0));
    }
    public int LookPeakXMax
    {
        get => _lookPeakXMax;
        set => SetProperty(ref _lookPeakXMax, (int)Math.Clamp(Math.Max(0, value), 0, MouseTrajectoryPlayback.MaxExtentPx));
    }
    public int LookPeakYMin
    {
        get => _lookPeakYMin;
        set => SetProperty(ref _lookPeakYMin, (int)Math.Clamp(Math.Min(0, value), -MouseTrajectoryPlayback.MaxExtentPx, 0));
    }
    public int LookPeakYMax
    {
        get => _lookPeakYMax;
        set => SetProperty(ref _lookPeakYMax, (int)Math.Clamp(Math.Max(0, value), 0, MouseTrajectoryPlayback.MaxExtentPx));
    }
    public int Width { get => _width; set => SetProperty(ref _width, value); }
    public int Height { get => _height; set => SetProperty(ref _height, value); }
    public int DurationMs { get => _durationMs; set => SetProperty(ref _durationMs, value); }
    public double SensitivityX
    {
        get => _sensitivityX;
        set => SetProperty(ref _sensitivityX, MouseTrajectoryStep.ClampLookGain(value));
    }
    public double SensitivityY
    {
        get => _sensitivityY;
        set => SetProperty(ref _sensitivityY, MouseTrajectoryStep.ClampLookGain(value));
    }
    public int HorizontalSmoothing
    {
        get => _horizontalSmoothing;
        set
        {
            if (!SetProperty(ref _horizontalSmoothing, MouseSweepMotion.ClampJitter(value)))
                return;
            if (!_loading)
                SyncProfileFromFields();
        }
    }
    public int VerticalSmoothing
    {
        get => _verticalSmoothing;
        set
        {
            if (!SetProperty(ref _verticalSmoothing, MouseSweepMotion.ClampJitter(value)))
                return;
            if (!_loading)
                SyncProfileFromFields();
        }
    }
    public bool FilterVerticalJitter
    {
        get => _filterVerticalJitter;
        set
        {
            if (!SetProperty(ref _filterVerticalJitter, value))
                return;
            if (!_loading)
                SyncProfileFromFields();
        }
    }
    public bool FilterHorizontalJitter
    {
        get => _filterHorizontalJitter;
        set
        {
            if (!SetProperty(ref _filterHorizontalJitter, value))
                return;
            if (!_loading)
                SyncProfileFromFields();
        }
    }
    public int TrajectoryHzIndex
    {
        get => _playbackHz switch { 60 => 0, 250 => 2, _ => 1 };
        set
        {
            int hz = value switch { 0 => 60, 2 => 250, _ => 125 };
            if (!SetProperty(ref _playbackHz, hz))
                return;
            OnPropertyChanged(nameof(TrajectoryHzIndex));
        }
    }
    public int TrajectoryModeIndex
    {
        get => _trajectoryModeIndex;
        set => SetProperty(ref _trajectoryModeIndex, Math.Clamp(value, 0, 1));
    }

    public int PlaybackHzIndex
    {
        get => HzToIndex(_playbackHz);
        set
        {
            int hz = IndexToHz(value);
            if (!SetProperty(ref _playbackHz, hz))
                return;
            OnPropertyChanged(nameof(PlaybackHzIndex));
            if (!_loading)
                SyncProfileFromFields();
        }
    }
    public int InterpolationIndex
    {
        get => _interpolationIndex;
        set
        {
            if (!SetProperty(ref _interpolationIndex, Math.Clamp(value, 0, 4)))
                return;
            if (!_loading)
                SyncProfileFromFields();
        }
    }
    public int ProfileIndex
    {
        get => _profileIndex;
        set
        {
            int next = Math.Clamp(value, 0, 3);
            if (!SetProperty(ref _profileIndex, next))
                return;
            if (_loading)
                return;
            if (next < 3)
            {
                MouseSweepMotion.ApplyProfile(
                    (MouseSweepProfile)next, out int hz, out var interp,
                    out int horiz, out int vert, out bool filterY);
                _playbackHz = hz;
                _interpolationIndex = (int)interp;
                _horizontalSmoothing = horiz;
                _verticalSmoothing = vert;
                _filterVerticalJitter = filterY;
                OnPropertyChanged(nameof(PlaybackHzIndex));
                OnPropertyChanged(nameof(InterpolationIndex));
                OnPropertyChanged(nameof(HorizontalSmoothing));
                OnPropertyChanged(nameof(VerticalSmoothing));
                OnPropertyChanged(nameof(FilterVerticalJitter));
            }
        }
    }
    public int ClickCountIndex
    {
        get => _clickCount <= 1 ? 0 : 1;
        set
        {
            int count = value <= 0 ? 1 : 2;
            if (SetProperty(ref _clickCount, count))
                OnPropertyChanged(nameof(ClickCountIndex));
        }
    }
    public int StrokeIndex
    {
        get => _strokeIndex;
        set => SetProperty(ref _strokeIndex, value);
    }
    public int ConditionKindIndex
    {
        get => _conditionKindIndex;
        set
        {
            if (SetProperty(ref _conditionKindIndex, value))
                OnPropertyChanged(nameof(ShowPresenceImageFields));
        }
    }
    public string ThenSummary { get => _thenSummary; set => SetProperty(ref _thenSummary, value); }
    public string ElseSummary { get => _elseSummary; set => SetProperty(ref _elseSummary, value); }
    public string BodySummary { get => _bodySummary; set => SetProperty(ref _bodySummary, value); }

    public bool ShowIfFields => Kind == EditableActionKind.IfBranch;
    public bool ShowWhileFields => Kind is EditableActionKind.WhileLoop or EditableActionKind.RepeatUntil or EditableActionKind.RepeatUntilGone;
    public string LoopHelpText => Kind is EditableActionKind.RepeatUntil or EditableActionKind.RepeatUntilGone
        ? "Repeat until present runs while the target is absent. Repeat until gone runs while the target is present."
        : "WHILE checks before every body run and continues while the condition is true.";
    public bool WaitWhile
    {
        get => _waitWhile;
        set
        {
            if (!SetProperty(ref _waitWhile, value))
                return;
            if (value)
            {
                _repeatImageWhileVisible = false;
                _redirectOnAppearance = false;
                OnPropertyChanged(nameof(RedirectOnAppearance));
                OnPropertyChanged(nameof(RepeatImageWhileVisible));
                OnPropertyChanged(nameof(ImageOneShot));
            }
            OnPropertyChanged(nameof(ImageSearchAreaSummary));
            if (!_loading && HasSelection)
                Apply();
        }
    }
    public bool RepeatImageWhileVisible
    {
        get => _repeatImageWhileVisible;
        set
        {
            if (!SetProperty(ref _repeatImageWhileVisible, value)) return;
            if (value)
            {
                _waitWhile = false;
                _redirectOnAppearance = false;
                _imageScanOptions = (_imageScanOptions ?? new ImageScanOptions()) with { MatchHandling = ImageMatchHandling.All };
                OnPropertyChanged(nameof(RedirectOnAppearance));
                OnPropertyChanged(nameof(WaitWhile));
                NotifyImageSetupChanged();
            }
            OnPropertyChanged(nameof(ImageOneShot));
            OnPropertyChanged(nameof(ImageSearchAreaSummary));
            if (!_loading && HasSelection) Apply();
        }
    }
    public int ImageRepeatDelayMs
    {
        get => _imageRepeatDelayMs;
        set => SetProperty(ref _imageRepeatDelayMs, Math.Clamp(value, 50, 60_000));
    }
    public bool ImageOneShot => !_repeatImageWhileVisible && !_redirectOnAppearance;
    public bool RedirectOnAppearance
    {
        get => _redirectOnAppearance;
        set
        {
            if (!SetProperty(ref _redirectOnAppearance, value)) return;
            if (value)
            {
                _waitWhile = false;
                _repeatImageWhileVisible = false;
                OnPropertyChanged(nameof(WaitWhile));
                OnPropertyChanged(nameof(RepeatImageWhileVisible));
            }
            OnPropertyChanged(nameof(ImageOneShot));
            OnPropertyChanged(nameof(ImageSearchAreaSummary));
        }
    }
    public System.Drawing.Rectangle? ImageSearchRegion => _imageSearchRegion;
    public string ImageSearchAreaSummary => DescribeImageSearchArea();
    public Guid? AppearanceTargetStepId { get => _appearanceTargetStepId; set { if (!_loading && value.HasValue) SetProperty(ref _appearanceTargetStepId, value); } }
    public int AppearanceCooldownMs { get => _appearanceCooldownMs; set => SetProperty(ref _appearanceCooldownMs, Math.Clamp(value, 50, 60_000)); }
    public IReadOnlyList<ActionRouteTarget> RouteTargets
    {
        get
        {
            var targets = _editor.GetRouteTargets().ToList();
            foreach (Guid id in new[] { _routeTargetStepId, _appearanceTargetStepId }.OfType<Guid>().Distinct())
                if (!targets.Any(t => t.StepId == id))
                    targets.Add(new ActionRouteTarget(id, "Unavailable destination — choose another action"));
            return targets;
        }
    }
    public int OnNotSatisfiedRouteIndex { get => _onNotSatisfiedRouteIndex; set => SetProperty(ref _onNotSatisfiedRouteIndex, Math.Clamp(value, 0, 4)); }
    public int OnFailureRouteIndex { get => _onFailureRouteIndex; set => SetProperty(ref _onFailureRouteIndex, Math.Clamp(value, 0, 4)); }
    // Rebuilding the choice list temporarily clears ComboBox.SelectedValue. That binding
    // transition must not erase the stable destination saved in the draft.
    public Guid? RouteTargetStepId { get => _routeTargetStepId; set { if (!_loading && value.HasValue) SetProperty(ref _routeTargetStepId, value); } }
    public int RouteRetryCount { get => _routeRetryCount; set => SetProperty(ref _routeRetryCount, Math.Clamp(value, 0, 20)); }
    public int RouteRetryDelayMs { get => _routeRetryDelayMs; set => SetProperty(ref _routeRetryDelayMs, Math.Clamp(value, 10, 10_000)); }
    public void RefreshRouteTargets()
    {
        OnPropertyChanged(nameof(RouteTargets));
        OnPropertyChanged(nameof(RouteTargetStepId));
        OnPropertyChanged(nameof(AppearanceTargetStepId));
    }
    public bool ShowPresenceImageFields => ShowControlFlowFields && _conditionKindIndex >= 2;
    public string PresenceTemplateSummary => string.IsNullOrWhiteSpace(_currentPresenceTemplatePath)
        ? _currentPresenceTemplateBytes is { Length: > 0 } ? "Captured image template" : "Capture an image for this condition."
        : $"Template: {Path.GetFileName(_currentPresenceTemplatePath)}";
    public int MinBodyDelayMs
    {
        get => _minBodyDelayMs;
        set => SetProperty(ref _minBodyDelayMs, Math.Clamp(value, 0, 10_000));
    }
    public bool ShowControlFlowFields => ShowIfFields || ShowWhileFields;
    public bool CanEditPresenceCondition => !_preserveOriginalCondition;
    public bool HasPreservedCondition => ShowControlFlowFields && _preserveOriginalCondition;
    public bool ShowDetectionRegionFields => ShowControlFlowFields || ShowColorFields || ShowVisualFields;
    public string ImageTemplateSummary
    {
        get => _imageTemplateSummary;
        private set => SetProperty(ref _imageTemplateSummary, value ?? string.Empty);
    }
    public string? DraftError => _draftError;
    public int MouseButtonIndex
    {
        get => _mouseButtonIndex;
        set => SetProperty(ref _mouseButtonIndex, value);
    }
    public string KeyName { get => _keyName; set => SetProperty(ref _keyName, value); }
    public int SimilarityPercent { get => _similarityPercent; set => SetProperty(ref _similarityPercent, Math.Clamp(value, 70, 99)); }
    public int TimeoutMs { get => _timeoutMs; set => SetProperty(ref _timeoutMs, value); }
    public int SearchModeIndex { get => _searchModeIndex; set => SetProperty(ref _searchModeIndex, value); }
    public int OnTimeoutIndex { get => _onTimeoutIndex; set => SetProperty(ref _onTimeoutIndex, value); }
    public int R { get => _r; set => SetProperty(ref _r, Math.Clamp(value, 0, 255)); }
    public int G { get => _g; set => SetProperty(ref _g, Math.Clamp(value, 0, 255)); }
    public int B { get => _b; set => SetProperty(ref _b, Math.Clamp(value, 0, 255)); }
    public int Tolerance { get => _tolerance; set => SetProperty(ref _tolerance, Math.Max(0, value)); }
    public int DetectionModeIndex
    {
        get => _detectionModeIndex;
        set => SetProperty(ref _detectionModeIndex, Math.Clamp(value, 0, 3));
    }
    public double MinimumMatchPercent
    {
        get => _minimumMatchPercentage * 100;
        set
        {
            if (SetProperty(ref _minimumMatchPercentage, Math.Clamp(value / 100d, 0.0001, 1)))
                OnPropertyChanged(nameof(MinimumMatchPercent));
        }
    }
    public string PaletteText { get => _paletteText; set => SetProperty(ref _paletteText, value ?? string.Empty); }
    public int RequiredConfirmations
    {
        get => _requiredConfirmations;
        set => SetProperty(ref _requiredConfirmations, Math.Clamp(value, 1, 100));
    }
    public int RequiredFalseConfirmations
    {
        get => _requiredFalseConfirmations;
        set => SetProperty(ref _requiredFalseConfirmations, Math.Clamp(value, 1, 100));
    }
    public int PollIntervalMs
    {
        get => _pollIntervalMs;
        set { if (SetProperty(ref _pollIntervalMs, Math.Clamp(value, 10, 10_000))) NotifyImageSetupChanged(); }
    }
    public int LoopCheckIntervalMs
    {
        get => _loopCheckIntervalMs;
        set => SetProperty(ref _loopCheckIntervalMs, Math.Clamp(value, 10, 10_000));
    }
    public int SampleStride
    {
        get => _sampleStride;
        set => SetProperty(ref _sampleStride, Math.Clamp(value, 1, 64));
    }
    public int StabilityDurationMs
    {
        get => _stabilityDurationMs;
        set => SetProperty(ref _stabilityDurationMs, Math.Max(0, value));
    }
    public int FailurePolicyIndex
    {
        get => _failurePolicyIndex;
        set => SetProperty(ref _failurePolicyIndex, Math.Clamp(value, 0, 3));
    }
    public int ConditionTimeoutMs
    {
        get => _conditionTimeoutMs;
        set => SetProperty(ref _conditionTimeoutMs, Math.Max(0, value));
    }
    public string Prompt { get => _prompt; set => SetProperty(ref _prompt, value); }
    public string ExpectedWindow { get => _expectedWindow; set => SetProperty(ref _expectedWindow, value ?? string.Empty); }
    public string MinConfidence { get => _minConfidenceText; set => SetProperty(ref _minConfidenceText, value ?? string.Empty); }
    public bool MaximumAccuracy { get => _maximumAccuracy; set => SetProperty(ref _maximumAccuracy, value); }

    public string PickLocationButtonText { get => _pickLocationButtonText; set => SetProperty(ref _pickLocationButtonText, value); }
    public string PickHint { get => _pickHint; set => SetProperty(ref _pickHint, value); }
    public bool IsPickingLocation { get => _isPickingLocation; set => SetProperty(ref _isPickingLocation, value); }
    public bool IsCapturingKey
    {
        get => _isCapturingKey;
        set
        {
            if (SetProperty(ref _isCapturingKey, value))
                OnPropertyChanged(nameof(CaptureKeyButtonText));
        }
    }
    public string CaptureKeyButtonText => IsCapturingKey ? "Press a key…" : "Change key";

    public bool ShowClickFields => Kind == EditableActionKind.MouseClick;
    public bool ShowClickOffsetFields => ShowClickFields && (!string.IsNullOrWhiteSpace(_xVariable)
        || !string.IsNullOrWhiteSpace(_yVariable) || _clickOffsetX != 0 || _clickOffsetY != 0);
    public bool ShowCoordinateVariableFields => Kind is EditableActionKind.MouseClick or EditableActionKind.MouseMove;
    public bool ShowMoveFields => Kind == EditableActionKind.MouseMove;
    public bool ShowPointFields => (ShowClickFields && !ClickDetectedImage)
        || Kind is EditableActionKind.MouseMove or EditableActionKind.VisualWait or EditableActionKind.ColorWait or EditableActionKind.IfBranch or EditableActionKind.WhileLoop or EditableActionKind.RepeatUntil or EditableActionKind.RepeatUntilGone;
    public bool ShowWaitFields => Kind == EditableActionKind.Wait;
    public bool ShowSweepFields => Kind == EditableActionKind.MouseSweep;
    public bool ShowTrajectoryFields => Kind == EditableActionKind.MouseTrajectory;
    public bool ShowKeyFields => Kind == EditableActionKind.KeyPress;
    public bool ShowVisualFields => Kind == EditableActionKind.VisualWait;
    public bool ShowColorFields => Kind == EditableActionKind.ColorWait;
    public bool ShowImageFields => Kind == EditableActionKind.ImageWait;
    public bool ShowAiFields => Kind == EditableActionKind.AiVision;
    public bool ShowPickLocation => ShowPointFields || ShowControlFlowFields;

    private EditableActionKind Kind => (EditableActionKind)Math.Clamp(_kindIndex, 0, MacroActionFactory.KindNames.Length - 1);

    public void LoadFrom(MacroStep? step)
    {
        _loading = true;
        try
        {
            bool keepBodyKey = step is IfThenElseStep or LoopStep && step.StepId == _sourceStepId;
            int bodyKey = _keyCode;
            string bodyKeyName = _keyName;
            ResetDraftFields();
            if (keepBodyKey)
            {
                _keyCode = bodyKey;
                _keyName = bodyKeyName;
            }
            if (step is null)
            {
                _sourceStepId = null;
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(HasNoSelection));
                RaiseKindVis();
                return;
            }

            _sourceStepId = step.StepId;
            _kindIndex = (int)MacroActionFactory.KindOf(step);
            switch (step)
            {
                case ClickStep click:
                    _x = click.Position.X;
                    _y = click.Position.Y;
                    _xVariable = click.XVariable ?? string.Empty;
                    _yVariable = click.YVariable ?? string.Empty;
                    _clickOffsetX = click.OffsetX;
                    _clickOffsetY = click.OffsetY;
                    _clickIsClientRelative = click.IsClientRelative;
                    _mouseButtonIndex = click.Button switch
                    {
                        MouseButton.Right => 1,
                        MouseButton.Middle => 2,
                        _ => 0
                    };
                    _clickCount = click.ClickCount <= 1 ? 1 : 2;
                    break;
                case MouseMoveStep move:
                    _x = move.Position.X;
                    _y = move.Position.Y;
                    _xVariable = move.XVariable ?? string.Empty;
                    _yVariable = move.YVariable ?? string.Empty;
                    _clickIsClientRelative = move.IsClientRelative;
                    break;
                case DelayStep delay:
                    _durationMs = (int)delay.Duration.TotalMilliseconds;
                    break;
                case MouseTrajectoryStep path:
                    MouseTrajectoryPlayback.MeasureExtents(path.Samples, out float minX, out float maxX, out float minY, out float maxY);
                    _lookPeakXMin = (int)Math.Round(minX);
                    _lookPeakXMax = (int)Math.Round(maxX);
                    _lookPeakYMin = (int)Math.Round(minY);
                    _lookPeakYMax = (int)Math.Round(maxY);
                    _x = Math.Abs(_lookPeakXMin) >= _lookPeakXMax ? _lookPeakXMin : _lookPeakXMax;
                    _y = Math.Abs(_lookPeakYMin) >= _lookPeakYMax ? _lookPeakYMin : _lookPeakYMax;
                    _durationMs = path.DurationMs;
                    _playbackHz = MouseTrajectoryPlayback.ClampHz(path.PlaybackHz);
                    if (Math.Abs(path.Sensitivity - 4.0) < 0.0001 && Math.Abs(path.SensitivityY - 8.0) < 0.0001)
                    {
                        _sensitivityX = MouseTrajectoryStep.DefaultLookGainX;
                        _sensitivityY = MouseTrajectoryStep.DefaultLookGainY;
                    }
                    else
                    {
                        _sensitivityX = MouseTrajectoryStep.ClampLookGain(path.Sensitivity);
                        _sensitivityY = MouseTrajectoryStep.ClampLookGain(path.SensitivityY);
                    }
                    _trajectoryModeIndex = path.Mode == TrajectoryPlaybackMode.PacketReference ? 1 : 0;
                    break;
                case MouseSweepStep sweep:
                    _x = sweep.DeltaX;
                    _y = sweep.DeltaY;
                    _durationMs = sweep.DurationMs;
                    _sensitivityX = MouseSweepMotion.ClampSensitivity(sweep.SensitivityX);
                    _sensitivityY = MouseSweepMotion.ClampSensitivity(sweep.SensitivityY);
                    if (Math.Abs(sweep.Sensitivity - 1.0) > 0.0001
                        && Math.Abs(_sensitivityX - 1.0) < 0.0001
                        && Math.Abs(_sensitivityY - 1.0) < 0.0001)
                    {
                        _sensitivityX = MouseSweepMotion.ClampSensitivity(sweep.Sensitivity);
                        _sensitivityY = _sensitivityX;
                    }
                    _playbackHz = MouseSweepMotion.ClampHz(sweep.PlaybackHz);
                    _interpolationIndex = Math.Clamp((int)sweep.Interpolation, 0, 4);
                    _horizontalSmoothing = MouseSweepMotion.ClampJitter(sweep.HorizontalSmoothing);
                    _verticalSmoothing = MouseSweepMotion.ClampJitter(
                        sweep.VerticalSmoothing > 0 ? sweep.VerticalSmoothing : sweep.JitterReduction);
                    _filterVerticalJitter = sweep.FilterVerticalJitter;
                    _filterHorizontalJitter = sweep.FilterHorizontalJitter;
                    _profileIndex = (int)(sweep.Profile == MouseSweepProfile.Custom
                        ? MouseSweepMotion.InferProfile(
                            _playbackHz, sweep.Interpolation,
                            _horizontalSmoothing, _verticalSmoothing, _filterVerticalJitter)
                        : sweep.Profile);
                    break;
                case KeyPressStep key:
                    _keyCode = key.KeyCode;
                    _keyName = MacroStepItemViewModel.VirtualKeyName(key.KeyCode);
                    _strokeIndex = (key.Stroke ?? "Press").ToLowerInvariant() switch
                    {
                        "down" => 1,
                        "up" => 2,
                        _ => 0
                    };
                    break;
                case VisualWaitStep visual:
                    _x = visual.X;
                    _y = visual.Y;
                    _width = visual.Width;
                    _height = visual.Height;
                    _r = visual.R;
                    _g = visual.G;
                    _b = visual.B;
                    _similarityPercent = visual.SimilarityPercent;
                    _minimumMatchPercentage = Math.Clamp(visual.RequiredPixelFraction, 0.0001, 1);
                    _timeoutMs = visual.TimeoutMs;
                    _conditionTimeoutMs = visual.TimeoutMs;
                    _pollIntervalMs = visual.PollIntervalMs;
                    _requiredConfirmations = Math.Clamp(visual.RequiredConfirmations, 1, 100);
                    _requiredFalseConfirmations = Math.Clamp(visual.RequiredFalseConfirmations, 1, 100);
                    _stabilityDurationMs = Math.Max(0, visual.StabilityDurationMs);
                    _sampleStride = Math.Clamp(visual.SampleStride, 1, 64);
                    _detectionModeIndex = (int)visual.ComparisonMode;
                    _paletteText = ColorPalette.FormatExtras(visual.AdditionalTargets);
                    _failurePolicyIndex = visual.FailurePolicy != ConditionFailurePolicy.Stop
                        ? (int)visual.FailurePolicy
                        : visual.OnTimeout.ToLowerInvariant() switch
                        {
                            "skip" => 1,
                            "retry" => 2,
                            _ => 0
                        };
                    _searchModeIndex = visual.SearchMode.ToLowerInvariant() switch
                    {
                        "nearby" => 1,
                        "screen" => 2,
                        _ => 0
                    };
                    _onTimeoutIndex = _failurePolicyIndex;
                    _maximumAccuracy = visual.Quality.Equals("MaximumAccuracy", StringComparison.OrdinalIgnoreCase);
                    break;
                case WaitUntilStep until:
                    _timeoutMs = (int)until.Timeout.TotalMilliseconds;
                    _pollIntervalMs = (int)until.PollInterval.TotalMilliseconds;
                    _requiredConfirmations = Math.Clamp(until.RequiredConfirmations, 1, 100);
                    _requiredFalseConfirmations = Math.Clamp(until.RequiredFalseConfirmations, 1, 100);
                    _stabilityDurationMs = Math.Max(0, until.StabilityDurationMs);
                    _waitWhile = until.WaitWhile;
                    _repeatImageWhileVisible = until.Condition is ImageCondition && until.RepeatWhileVisible;
                    _imageRepeatDelayMs = Math.Clamp(until.RepeatDelayMs, 50, 60_000);
                    _redirectOnAppearance = until.AppearanceRedirect is not null;
                    _appearanceTargetStepId = until.AppearanceRedirect?.TargetStepId;
                    _appearanceCooldownMs = until.AppearanceRedirect?.CooldownMs ?? 500;
                    if (until.Condition is ImageCondition image)
                    {
                        _imageSearchRegion = image.SearchRegion;
                        _imageScanOptions = image.ScanOptions;
                        _imageTemplateSummary = string.IsNullOrEmpty(image.TemplatePath)
                            ? $"Captured image template (≥{image.MinConfidence:P0})"
                            : $"Template: {Path.GetFileName(image.TemplatePath)} (≥{image.MinConfidence:P0}) — path is read-only.";
                        _currentPresenceMinConfidence = image.MinConfidence;
                    }
                    else if (until.Condition is ColorCondition color)
                    {
                        _r = color.R;
                        _g = color.G;
                        _b = color.B;
                        _tolerance = color.Tolerance;
                        _detectionModeIndex = (int)color.ComparisonMode;
                        _minimumMatchPercentage = color.MinMatchPercentage;
                        _paletteText = ColorPalette.FormatExtras(color.AdditionalTargets);
                        _sampleStride = color.SampleStride;
                        _requiredFalseConfirmations = Math.Clamp(color.RequiredFalseConfirmations, 1, 100);
                        _stabilityDurationMs = Math.Max(0, color.StabilityDurationMs);
                        _conditionTimeoutMs = color.TimeoutMs;
                        _failurePolicyIndex = (int)color.FailurePolicy;
                        _pollIntervalMs = color.PollIntervalMs;
                        _requiredConfirmations = Math.Clamp(color.RequiredConfirmations, 1, 100);
                        if (until.PollInterval > TimeSpan.Zero)
                            _pollIntervalMs = (int)until.PollInterval.TotalMilliseconds;
                        if (until.RequiredConfirmations > 0)
                            _requiredConfirmations = Math.Clamp(until.RequiredConfirmations, 1, 100);
                        if (color.Location is { } loc)
                        {
                            _x = loc.X;
                            _y = loc.Y;
                        }
                        if (color.Region is { } region)
                        {
                            _x = region.X;
                            _y = region.Y;
                            _width = region.Width;
                            _height = region.Height;
                        }
                    }
                    break;
                case AIStep ai:
                    _prompt = ai.Prompt;
                    _timeoutMs = (int)ai.Timeout.TotalMilliseconds;
                    _expectedWindow = ai.ExpectedWindow ?? string.Empty;
                    _minConfidenceText = ai.MinConfidence is double confidence
                        ? confidence.ToString("0.###", CultureInfo.InvariantCulture)
                        : string.Empty;
                    break;
                case IfThenElseStep iff:
                    LoadPresence(iff.Condition);
                    LoadRouting(iff.Routing);
                    ThenSummary = "THEN: " + Summarize(iff.ThenSteps);
                    ElseSummary = "ELSE: " + Summarize(iff.ElseSteps);
                    break;
                case LoopStep loop:
                    LoadPresence(loop.WhileCondition);
                    LoadRouting(loop.Routing);
                    _timeoutMs = Math.Max(0, loop.MaxDurationMs);
                    _loopCheckIntervalMs = Math.Clamp(loop.CheckIntervalMs, 10, 10_000);
                    _minBodyDelayMs = Math.Clamp(loop.MinBodyDelayMs, 0, 10_000);
                    BodySummary = "DO: " + Summarize(loop.BodySteps);
                    break;
            }

            NotifyAllFields();
        }
        finally
        {
            UpdateDetectionPresentation();
            _hasDraftChanges = false;
            _loading = false;
            CaptureDraftBaseline();
        }
    }

    public void ReloadFromStore()
    {
        int idx = _editor.SelectedStepIndex;
        if (idx < 0) return;
        LoadFrom(_editor.GetStepAt(idx));
    }

    public void CancelEdits()
    {
        ReloadFromStore();
        _editor.ShowActionEditor = false;
        _editor.NotifyStatus("Closed properties.");
    }

    public bool TryCommit() => TryCommit(null);

    internal bool TryCommit(MacroEditorViewModel.AuthoringInteraction? owner)
    {
        if (_editor.IsAuthoringInteractionActive && !_editor.OwnsAuthoringDraft(owner)) return false;
        EditorBindingFlush.FlushFocusedText();
        return !_hasDraftChanges || Apply(owner);
    }

    public bool Apply() => Apply(null);

    private bool Apply(MacroEditorViewModel.AuthoringInteraction? owner)
    {
        bool ownedDraft = _editor.OwnsAuthoringDraft(owner);
        if (_editor.IsAuthoringInteractionActive && !ownedDraft) return false;
        EditorBindingFlush.FlushFocusedText();
        if (_loading || !HasSelection || _editor.IsEditingLocked && !ownedDraft)
            return true;

        int idx = _editor.SelectedStepIndex;
        var stored = _editor.GetStepAt(idx);
        if (stored is null) return true;

        var targetKind = Kind;
        if (stored is MouseTrajectoryStep && targetKind == EditableActionKind.MouseSweep)
            targetKind = EditableActionKind.MouseTrajectory;

        EditableActionKind storedKind = MacroActionFactory.KindOf(stored);
        ImageCondition? promptedImage = null;
        var imageStamp = targetKind == EditableActionKind.ImageWait ? _editor.CaptureAuthoringSelection() : default;
        if (targetKind == EditableActionKind.ImageWait
            && !TryEnsureImageTemplate(stored, storedKind, owner, out promptedImage))
        {
            if (_editor.IsCurrentAuthoringSelection(imageStamp)) LoadFrom(stored);
            return false;
        }
        if (targetKind == EditableActionKind.ImageWait
            && _imageScanOptions?.SearchScope == ImageSearchScope.PlacedArea
            && _imageSearchRegion is not { Width: > 0, Height: > 0 })
        {
            _draftError = "Place a search area or choose Anywhere on screen before applying.";
            OnPropertyChanged(nameof(DraftError));
            return false;
        }
        if (targetKind == EditableActionKind.ImageWait
            && !ValidateImageSearchArea(_pendingImageCondition ?? promptedImage
                ?? (stored as WaitUntilStep)?.Condition as ImageCondition,
                _imageScanOptions?.SearchScope is ImageSearchScope.Anywhere or ImageSearchScope.TargetWindow ? null : _imageSearchRegion))
            return false;

        MacroStep draft = storedKind == targetKind
            ? stored
            : MacroActionFactory.Convert(stored, targetKind);

        IReadOnlyList<PixelColor>? extraColors = null;
        if (targetKind is EditableActionKind.ColorWait or EditableActionKind.VisualWait
            or EditableActionKind.IfBranch or EditableActionKind.WhileLoop or EditableActionKind.RepeatUntil
            or EditableActionKind.RepeatUntilGone)
        {
            try
            {
                extraColors = ColorPalette.ParseExtras(_paletteText);
            }
            catch (FormatException ex)
            {
                _draftError = ex.Message;
                _editor.NotifyStatus(ex.Message);
                return false;
            }
        }

        if (targetKind is EditableActionKind.IfBranch or EditableActionKind.WhileLoop
            or EditableActionKind.RepeatUntil or EditableActionKind.RepeatUntilGone)
        {
            if (!TryBuildRouting(out BlockRoutingOptions routing)) return false;
            ICondition? condition = _preserveOriginalCondition ? _preservedCondition : BuildPresence(extraColors);
            if (condition is VisualPresenceCondition { Kind: VisualPresenceKind.ImagePresent or VisualPresenceKind.ImageAbsent } presence
                && string.IsNullOrWhiteSpace(presence.TemplatePath) && presence.TemplateBytes is not { Length: > 0 })
            {
                _draftError = "Capture an image template before saving this image condition.";
                OnPropertyChanged(nameof(DraftError));
                _editor.NotifyStatus(_draftError);
                return false;
            }
            MacroStep block = draft is IfThenElseStep iff
                ? MacroActionFactory.WithIfCondition(iff, condition!, routing)
                : MacroActionFactory.WithLoopBody((LoopStep)draft, ((LoopStep)draft).BodySteps,
                    condition, Math.Max(0, _timeoutMs), _loopCheckIntervalMs, _minBodyDelayMs, routing);
            if (!_editor.TryApplyActionReplacement(idx, stored, block, owner)) return false;
            _draftError = null;
            _editor.NotifyStatus("Saved action.");
            return true;
        }

        string stroke = _strokeIndex switch { 1 => "Down", 2 => "Up", _ => "Press" };
        if (targetKind == EditableActionKind.ImageWait && _redirectOnAppearance
            && !ValidateRouteTarget(_appearanceTargetStepId, excludeCurrent: true)) return false;
        if (!TryParseMinConfidence(out double? minConfidence))
        {
            _draftError = "Minimum confidence is invalid.";
            return false;
        }

        MacroStep next = draft switch
        {
            ClickStep click =>
                MacroActionFactory.UpdateClick(
                    click,
                    _x,
                    _y,
                    ButtonFromIndex(),
                    _clickCount,
                    _xVariable,
                    _yVariable,
                    _clickOffsetX,
                    _clickOffsetY,
                    _clickIsClientRelative),
            MouseMoveStep move =>
                MacroActionFactory.UpdateMove(move, _x, _y, _xVariable, _yVariable),
            DelayStep delay =>
                MacroActionFactory.UpdateWait(delay, _durationMs),
            MouseSweepStep sweep =>
                MacroActionFactory.UpdateSweep(
                    sweep, _x, _y, _durationMs,
                    _sensitivityX, _sensitivityY,
                    _playbackHz,
                    (MouseInterpolation)_interpolationIndex,
                    _horizontalSmoothing,
                    _verticalSmoothing,
                    _filterVerticalJitter,
                    _filterHorizontalJitter,
                    (MouseSweepProfile)_profileIndex),
            MouseTrajectoryStep path =>
                stored is MouseTrajectoryStep recorded && Kind == EditableActionKind.MouseSweep
                    ? MacroActionFactory.UpdateTrajectory(
                        recorded, recorded.PlaybackHz, _sensitivityX, _sensitivityY, recorded.Mode)
                    : ApplyTrajectoryPeaks(
                        path,
                        _playbackHz <= 0 ? path.PlaybackHz : MouseTrajectoryPlayback.ClampHz(_playbackHz),
                        _sensitivityX,
                        _sensitivityY,
                        _trajectoryModeIndex == 1
                            ? TrajectoryPlaybackMode.PacketReference
                            : TrajectoryPlaybackMode.Reconstructed),
            KeyPressStep key =>
                MacroActionFactory.UpdateKey(key, _keyCode, stroke),
            VisualWaitStep visual =>
                MacroActionFactory.UpdateVisual(visual, BuildVisualDraft(visual, extraColors)),
            WaitUntilStep wait when targetKind == EditableActionKind.ImageWait =>
                MacroActionFactory.UpdateImageWait(
                    wait,
                    _timeoutMs,
                    _pollIntervalMs,
                    _requiredConfirmations,
                    _requiredFalseConfirmations,
                    _stabilityDurationMs,
                    _waitWhile,
                    WithImageSearchRegion(_pendingImageCondition ?? promptedImage ?? (ImageCondition)wait.Condition),
                    _repeatImageWhileVisible,
                    _imageRepeatDelayMs,
                    _redirectOnAppearance ? new AppearanceRedirectOptions
                    {
                        TargetStepId = _appearanceTargetStepId!.Value,
                        CooldownMs = _appearanceCooldownMs
                    } : null,
                    replaceAppearanceRedirect: true),
            WaitUntilStep wait =>
                MacroActionFactory.UpdateColorWait(
                    wait,
                    _x,
                    _y,
                    _width,
                    _height,
                    (byte)_r,
                    (byte)_g,
                    (byte)_b,
                    _tolerance,
                    _minimumMatchPercentage,
                    (ColorComparisonMode)_detectionModeIndex,
                    _timeoutMs,
                    _pollIntervalMs,
                    _requiredConfirmations,
                    extraColors,
                    _sampleStride,
                    _requiredFalseConfirmations,
                    _stabilityDurationMs,
                    (ConditionFailurePolicy)_failurePolicyIndex,
                    _waitWhile),
            AIStep ai =>
                MacroActionFactory.UpdateAi(ai, _prompt, _timeoutMs, _expectedWindow, minConfidence),
            _ => draft
        };

        if (!ReferenceEquals(next, stored))
        {
            if (!_editor.TryApplyActionReplacement(idx, stored, next, owner)) return false;
        }
        else
            LoadFrom(next);

        _pendingImageCondition = null;
        _draftError = null;
        _editor.MarkDraftApplied();
        _editor.NotifyStatus(next is MouseTrajectoryStep
            ? "Updated camera path in the editor."
            : next is MouseSweepStep
                ? "Updated camera sweep in the editor."
                : "Updated action in the editor.");
        return true;
    }

    private MacroStep ApplyTrajectoryPeaks(
        MouseTrajectoryStep path,
        int playbackHz,
        double gainX,
        double gainY,
        TrajectoryPlaybackMode mode)
    {
        if (MouseTrajectoryPlayback.ExtentsMatch(
            path.Samples, _lookPeakXMin, _lookPeakXMax, _lookPeakYMin, _lookPeakYMax))
        {
            return MacroActionFactory.UpdateTrajectory(path, playbackHz, gainX, gainY, mode);
        }

        var samples = MouseTrajectoryPlayback.ScaleByExtents(
            path.Samples, _lookPeakXMin, _lookPeakXMax, _lookPeakYMin, _lookPeakYMax);
        var packets = path.Packets is { Length: > 0 }
            ? MouseTrajectoryPlayback.ScalePacketsByExtents(
                path.Packets, path.Samples, _lookPeakXMin, _lookPeakXMax, _lookPeakYMin, _lookPeakYMax)
            : path.Packets;
        return MacroActionFactory.UpdateTrajectory(path, playbackHz, gainX, gainY, mode, samples, packets);
    }

    public bool TryCaptureKey(int virtualKey)
    {
        if (!IsCapturingKey) return false;
        IsCapturingKey = false;
        _keyCode = virtualKey;
        KeyName = MacroStepItemViewModel.VirtualKeyName(virtualKey);
        if (!ShowControlFlowFields) Apply();
        return true;
    }

    public async Task ArmPickLocationAsync()
    {
        if (_editor.IsRecordingBusy)
        {
            _editor.NotifyStatus("Stop recording before picking a location.");
            return;
        }

        IsPickingLocation = true;
        PickLocationButtonText = "Move mouse · Tab to confirm";
        PickHint = "Move the cursor to the target. Press Tab to confirm, Esc to cancel.";
        try
        {
            var point = await _editor.PickScreenPointAsync().ConfigureAwait(true);
            if (point is null)
            {
                PickHint = "Location pick cancelled.";
                return;
            }

            _loading = true;
            X = point.Value.X;
            Y = point.Value.Y;
            _loading = false;
            Apply();
            PickHint = $"X = {point.Value.X}   Y = {point.Value.Y}";
        }
        finally
        {
            IsPickingLocation = false;
            PickLocationButtonText = "Pick Location";
            ArmPickLocationCommand.RaiseCanExecuteChanged();
        }
    }

    private VisualWaitStep BuildVisualDraft(VisualWaitStep source, IReadOnlyList<PixelColor>? extraColors)
        => new()
        {
            Label = source.Label,
            Mode = source.Mode,
            SearchMode = _searchModeIndex switch { 1 => "Nearby", 2 => "Screen", _ => "Fixed" },
            MatchingMethod = source.MatchingMethod,
            Quality = _maximumAccuracy ? "MaximumAccuracy" : "Balanced",
            OnTimeout = _onTimeoutIndex switch { 1 => "Skip", 2 => "Retry", _ => "Stop" },
            X = _x,
            Y = _y,
            Width = _width,
            Height = _height,
            R = (byte)_r,
            G = (byte)_g,
            B = (byte)_b,
            SimilarityPercent = _similarityPercent,
            NeighborhoodSize = source.NeighborhoodSize,
            RequiredPixelFraction = _minimumMatchPercentage,
            AdditionalTargets = extraColors ?? ColorPalette.ParseExtras(_paletteText),
            PollIntervalMs = _pollIntervalMs,
            RequiredConfirmations = _requiredConfirmations,
            RequiredFalseConfirmations = _requiredFalseConfirmations,
            StabilityDurationMs = _stabilityDurationMs,
            TimeoutMs = _timeoutMs,
            RetryCount = source.RetryCount,
            SearchRadius = source.SearchRadius,
            AiMinIntervalMs = source.AiMinIntervalMs,
            ComparisonMode = (ColorComparisonMode)_detectionModeIndex,
            SampleStride = _sampleStride,
            FailurePolicy = (ConditionFailurePolicy)_onTimeoutIndex,
            TemplateBgra = source.TemplateBgra,
            TemplateWidth = source.TemplateWidth,
            TemplateHeight = source.TemplateHeight
        };

    private void LoadRouting(BlockRoutingOptions? routing)
    {
        routing ??= new BlockRoutingOptions();
        _onNotSatisfiedRouteIndex = (int)routing.OnNotSatisfied;
        _onFailureRouteIndex = (int)routing.OnFailure;
        _routeTargetStepId = routing.TargetStepId;
        _routeRetryCount = Math.Clamp(routing.RetryCount, 0, 20);
        _routeRetryDelayMs = Math.Clamp(routing.RetryDelayMs, 10, 10_000);
    }

    private bool TryBuildRouting(out BlockRoutingOptions routing)
    {
        routing = new BlockRoutingOptions
        {
            OnNotSatisfied = (FlowRoute)_onNotSatisfiedRouteIndex,
            OnFailure = (FlowRoute)_onFailureRouteIndex,
            TargetStepId = _routeTargetStepId,
            RetryCount = _routeRetryCount,
            RetryDelayMs = _routeRetryDelayMs
        };
        return (routing.OnNotSatisfied != FlowRoute.Jump && routing.OnFailure != FlowRoute.Jump)
            || ValidateRouteTarget(routing.TargetStepId);
    }

    private bool ValidateRouteTarget(Guid? targetId, bool excludeCurrent = false)
    {
        if (targetId.HasValue && (!excludeCurrent || targetId != _sourceStepId)
            && _editor.GetRouteTargets().Any(t => t.StepId == targetId.Value)) return true;
        _draftError = "Choose an enabled action in the main action list. The saved destination may have been deleted, moved into a block, or disabled.";
        OnPropertyChanged(nameof(DraftError));
        _editor.NotifyStatus(_draftError);
        return false;
    }

    private VisualPresenceCondition BuildPresence(IReadOnlyList<PixelColor>? extraColors = null)
    {
        VisualPresenceKind presenceKind = (VisualPresenceKind)Math.Clamp(_conditionKindIndex, 0, 3);
        if (Kind == EditableActionKind.RepeatUntilGone)
            presenceKind = presenceKind is VisualPresenceKind.ImagePresent or VisualPresenceKind.ImageAbsent
                ? VisualPresenceKind.ImageAbsent : VisualPresenceKind.ColorAbsent;
        else if (Kind == EditableActionKind.RepeatUntil)
            presenceKind = presenceKind is VisualPresenceKind.ImagePresent or VisualPresenceKind.ImageAbsent
                ? VisualPresenceKind.ImagePresent : VisualPresenceKind.ColorPresent;

        return new VisualPresenceCondition
        {
            Kind = presenceKind,
            X = _x,
            Y = _y,
            Width = Math.Max(1, _width),
            Height = Math.Max(1, _height),
            R = (byte)_r,
            G = (byte)_g,
            B = (byte)_b,
            SimilarityPercent = _similarityPercent,
            RequiredPixelFraction = _minimumMatchPercentage,
            AdditionalTargets = extraColors ?? ColorPalette.ParseExtras(_paletteText),
            SearchMode = _searchModeIndex switch { 1 => "Nearby", 2 => "Screen", _ => "Fixed" },
            TemplatePath = _currentPresenceTemplatePath,
            TemplateBytes = _currentPresenceTemplateBytes,
            MinConfidence = _currentPresenceMinConfidence,
            ComparisonMode = (ColorComparisonMode)_detectionModeIndex,
            SampleStride = _sampleStride,
            PollIntervalMs = _pollIntervalMs,
            RequiredConfirmations = _requiredConfirmations,
            RequiredFalseConfirmations = _requiredFalseConfirmations,
            StabilityDurationMs = _stabilityDurationMs,
            TimeoutMs = _conditionTimeoutMs,
            FailurePolicy = (ConditionFailurePolicy)_failurePolicyIndex
        };
    }

    private void LoadPresence(ICondition? condition)
    {
        // Four presence fields cannot represent expression trees or a counted loop.
        // Keep those expressions when changing routing, timing, or child actions.
        _preserveOriginalCondition = condition is not VisualPresenceCondition;
        _preservedCondition = _preserveOriginalCondition ? condition : null;
        _currentPresenceTemplatePath = null;
        _currentPresenceTemplateBytes = null;
        _currentPresenceMinConfidence = 0.8;
        if (condition is VisualPresenceCondition vis)
        {
            _conditionKindIndex = (int)vis.Kind;
            _x = vis.X;
            _y = vis.Y;
            _width = vis.Width;
            _height = vis.Height;
            _r = vis.R;
            _g = vis.G;
            _b = vis.B;
            _similarityPercent = vis.SimilarityPercent;
            _minimumMatchPercentage = Math.Clamp(vis.RequiredPixelFraction, 0.0001, 1);
            _paletteText = ColorPalette.FormatExtras(vis.AdditionalTargets);
            _currentPresenceTemplatePath = vis.TemplatePath;
            _currentPresenceTemplateBytes = vis.TemplateBytes;
            _currentPresenceMinConfidence = vis.MinConfidence;
            _detectionModeIndex = (int)vis.ComparisonMode;
            _sampleStride = Math.Clamp(vis.SampleStride, 1, 64);
            _pollIntervalMs = ConditionReliability.ClampPollIntervalMs(vis.PollIntervalMs, ConditionReliability.DefaultVisualPollIntervalMs);
            _requiredConfirmations = ConditionReliability.ClampConfirmations(vis.RequiredConfirmations);
            _requiredFalseConfirmations = ConditionReliability.ClampConfirmations(vis.RequiredFalseConfirmations);
            _stabilityDurationMs = Math.Max(0, vis.StabilityDurationMs);
            _conditionTimeoutMs = vis.TimeoutMs;
            _failurePolicyIndex = (int)vis.FailurePolicy;
            _searchModeIndex = vis.SearchMode.ToLowerInvariant() switch
            {
                "nearby" => 1,
                "screen" => 2,
                _ => 0
            };
            return;
        }

        if (condition is ColorCondition color)
        {
            _conditionKindIndex = 0;
            _r = color.R;
            _g = color.G;
            _b = color.B;
            _tolerance = color.Tolerance;
            _detectionModeIndex = (int)color.ComparisonMode;
            _minimumMatchPercentage = color.MinMatchPercentage;
            _paletteText = ColorPalette.FormatExtras(color.AdditionalTargets);
            _sampleStride = color.SampleStride;
            _pollIntervalMs = color.PollIntervalMs;
            _requiredConfirmations = color.RequiredConfirmations;
            _requiredFalseConfirmations = color.RequiredFalseConfirmations;
            _stabilityDurationMs = color.StabilityDurationMs;
            _conditionTimeoutMs = color.TimeoutMs;
            _failurePolicyIndex = (int)color.FailurePolicy;
            if (color.Location is { } loc)
            {
                _x = loc.X;
                _y = loc.Y;
            }
            if (color.Region is { } region)
            {
                _x = region.X;
                _y = region.Y;
                _width = region.Width;
                _height = region.Height;
            }
        }
    }

    private static string Summarize(IReadOnlyList<MacroStep> steps)
    {
        if (steps.Count == 0) return "(empty — add actions below)";
        return string.Join(" → ", steps.Take(6).Select(s => s.Label ?? s.GetType().Name))
               + (steps.Count > 6 ? "…" : "");
    }

    private void SyncProfileFromFields()
    {
        var inferred = MouseSweepMotion.InferProfile(
            _playbackHz,
            (MouseInterpolation)_interpolationIndex,
            _horizontalSmoothing,
            _verticalSmoothing,
            _filterVerticalJitter);
        if (SetProperty(ref _profileIndex, (int)inferred, nameof(ProfileIndex)))
        {
            // Property name already raised by SetProperty when provided.
        }
    }

    private static int HzToIndex(int hz) => hz switch
    {
        120 => 0,
        144 => 1,
        _ => 2
    };

    private static int IndexToHz(int index) => index switch
    {
        0 => 120,
        1 => 144,
        _ => 240
    };

    private void ResetDraftFields()
    {
        _draftError = null;
        _x = 0;
        _y = 0;
        _xVariable = string.Empty;
        _yVariable = string.Empty;
        _clickOffsetX = 0;
        _clickOffsetY = 0;
        _clickIsClientRelative = false;
        _previousClickTarget = null;
        _lookPeakXMin = 0;
        _lookPeakXMax = 0;
        _lookPeakYMin = 0;
        _lookPeakYMax = 0;
        _width = 1;
        _height = 1;
        _durationMs = 250;
        _clickCount = 1;
        _mouseButtonIndex = 0;
        _keyName = "ENTER";
        _keyCode = 13;
        _similarityPercent = 90;
        _timeoutMs = 10_000;
        _searchModeIndex = 0;
        _onTimeoutIndex = 0;
        _r = 63;
        _g = 167;
        _b = 214;
        _tolerance = 0;
        _detectionModeIndex = 0;
        _minimumMatchPercentage = 0.05;
        _paletteText = string.Empty;
        _requiredConfirmations = 1;
        _requiredFalseConfirmations = 1;
        _pollIntervalMs = 150;
        _loopCheckIntervalMs = 50;
        _sampleStride = 1;
        _stabilityDurationMs = 0;
        _failurePolicyIndex = (int)ConditionFailurePolicy.Default;
        _conditionTimeoutMs = 10_000;
        _prompt = "";
        _expectedWindow = "";
        _minConfidenceText = "";
        _maximumAccuracy = false;
        _strokeIndex = 0;
        _conditionKindIndex = 0;
        _waitWhile = false;
        _repeatImageWhileVisible = false;
        _imageRepeatDelayMs = 500;
        _preservedCondition = null;
        _preserveOriginalCondition = false;
        _redirectOnAppearance = false;
        _appearanceTargetStepId = null;
        _appearanceCooldownMs = 500;
        _onNotSatisfiedRouteIndex = 0;
        _onFailureRouteIndex = 0;
        _routeTargetStepId = null;
        _routeRetryCount = 3;
        _routeRetryDelayMs = 100;
        _minBodyDelayMs = 0;
        _imageTemplateSummary = string.Empty;
        _pendingImageCondition = null;
        _imageSearchRegion = null;
        _imageScanOptions = null;
        _currentPresenceTemplatePath = null;
        _currentPresenceTemplateBytes = null;
        _currentPresenceMinConfidence = 0.8;
    }

    private MouseButton ButtonFromIndex() => _mouseButtonIndex switch
    {
        1 => MouseButton.Right,
        2 => MouseButton.Middle,
        _ => MouseButton.Left
    };

    private bool TryParseMinConfidence(out double? minConfidence)
    {
        minConfidence = null;
        if (string.IsNullOrWhiteSpace(_minConfidenceText))
            return true;
        if (!double.TryParse(
                _minConfidenceText.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsed))
        {
            _editor.NotifyStatus("Minimum confidence must be a number between 0 and 1.");
            return false;
        }

        minConfidence = Math.Clamp(parsed, 0.0, 1.0);
        return true;
    }

    private void RaiseKindVis()
    {
        OnPropertyChanged(nameof(ShowClickFields));
        RaiseClickTargetFields();
        OnPropertyChanged(nameof(ShowCoordinateVariableFields));
        OnPropertyChanged(nameof(ShowMoveFields));
        OnPropertyChanged(nameof(ShowPointFields));
        OnPropertyChanged(nameof(ShowWaitFields));
        OnPropertyChanged(nameof(ShowSweepFields));
        OnPropertyChanged(nameof(ShowTrajectoryFields));
        OnPropertyChanged(nameof(ShowKeyFields));
        OnPropertyChanged(nameof(ShowVisualFields));
        OnPropertyChanged(nameof(ShowColorFields));
        OnPropertyChanged(nameof(ShowImageFields));
        OnPropertyChanged(nameof(ShowAiFields));
        OnPropertyChanged(nameof(ShowIfFields));
        OnPropertyChanged(nameof(ShowWhileFields));
        OnPropertyChanged(nameof(LoopHelpText));
        OnPropertyChanged(nameof(ShowControlFlowFields));
        OnPropertyChanged(nameof(ShowPresenceImageFields));
        OnPropertyChanged(nameof(PresenceTemplateSummary));
        OnPropertyChanged(nameof(ShowDetectionRegionFields));
        OnPropertyChanged(nameof(ShowPickLocation));
        OnPropertyChanged(nameof(KindIndex));
    }

    private void NotifyAllFields()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(RepeatImageWhileVisible));
        OnPropertyChanged(nameof(ImageRepeatDelayMs));
        OnPropertyChanged(nameof(RedirectOnAppearance));
        OnPropertyChanged(nameof(AppearanceTargetStepId));
        OnPropertyChanged(nameof(AppearanceCooldownMs));
        OnPropertyChanged(nameof(OnNotSatisfiedRouteIndex));
        OnPropertyChanged(nameof(OnFailureRouteIndex));
        OnPropertyChanged(nameof(RouteTargetStepId));
        OnPropertyChanged(nameof(RouteRetryCount));
        OnPropertyChanged(nameof(RouteRetryDelayMs));
        RefreshRouteTargets();
        OnPropertyChanged(nameof(ImageOneShot));
        OnPropertyChanged(nameof(HasNoSelection));
        OnPropertyChanged(nameof(KindIndex));
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(XVariable));
        OnPropertyChanged(nameof(YVariable));
        OnPropertyChanged(nameof(ClickOffsetX));
        OnPropertyChanged(nameof(ClickOffsetY));
        RaiseClickTargetFields();
        OnPropertyChanged(nameof(LookPeakXMin));
        OnPropertyChanged(nameof(LookPeakXMax));
        OnPropertyChanged(nameof(LookPeakYMin));
        OnPropertyChanged(nameof(LookPeakYMax));
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Height));
        OnPropertyChanged(nameof(DurationMs));
        OnPropertyChanged(nameof(SensitivityX));
        OnPropertyChanged(nameof(SensitivityY));
        OnPropertyChanged(nameof(PlaybackHzIndex));
        OnPropertyChanged(nameof(TrajectoryHzIndex));
        OnPropertyChanged(nameof(TrajectoryModeIndex));
        OnPropertyChanged(nameof(InterpolationIndex));
        OnPropertyChanged(nameof(HorizontalSmoothing));
        OnPropertyChanged(nameof(VerticalSmoothing));
        OnPropertyChanged(nameof(FilterVerticalJitter));
        OnPropertyChanged(nameof(FilterHorizontalJitter));
        OnPropertyChanged(nameof(ProfileIndex));
        OnPropertyChanged(nameof(ClickCountIndex));
        OnPropertyChanged(nameof(MouseButtonIndex));
        OnPropertyChanged(nameof(KeyName));
        OnPropertyChanged(nameof(SimilarityPercent));
        OnPropertyChanged(nameof(TimeoutMs));
        OnPropertyChanged(nameof(SearchModeIndex));
        OnPropertyChanged(nameof(OnTimeoutIndex));
        OnPropertyChanged(nameof(R));
        OnPropertyChanged(nameof(G));
        OnPropertyChanged(nameof(B));
        OnPropertyChanged(nameof(Tolerance));
        OnPropertyChanged(nameof(DetectionModeIndex));
        OnPropertyChanged(nameof(MinimumMatchPercent));
        OnPropertyChanged(nameof(PaletteText));
        OnPropertyChanged(nameof(RequiredConfirmations));
        OnPropertyChanged(nameof(RequiredFalseConfirmations));
        OnPropertyChanged(nameof(PollIntervalMs));
        OnPropertyChanged(nameof(LoopCheckIntervalMs));
        OnPropertyChanged(nameof(SampleStride));
        OnPropertyChanged(nameof(StabilityDurationMs));
        OnPropertyChanged(nameof(FailurePolicyIndex));
        OnPropertyChanged(nameof(ConditionTimeoutMs));
        OnPropertyChanged(nameof(Prompt));
        OnPropertyChanged(nameof(ExpectedWindow));
        OnPropertyChanged(nameof(MinConfidence));
        OnPropertyChanged(nameof(MaximumAccuracy));
        OnPropertyChanged(nameof(StrokeIndex));
        OnPropertyChanged(nameof(ConditionKindIndex));
        OnPropertyChanged(nameof(CanEditPresenceCondition));
        OnPropertyChanged(nameof(HasPreservedCondition));
        OnPropertyChanged(nameof(WaitWhile));
        OnPropertyChanged(nameof(ImageTemplateSummary));
        OnPropertyChanged(nameof(ImageSearchRegion));
        OnPropertyChanged(nameof(ImageSearchAreaSummary));
        OnPropertyChanged(nameof(DraftError));
        OnPropertyChanged(nameof(MinBodyDelayMs));
        OnPropertyChanged(nameof(ThenSummary));
        OnPropertyChanged(nameof(ElseSummary));
        OnPropertyChanged(nameof(BodySummary));
        RaiseKindVis();
        ApplyCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ArmPickLocationCommand.RaiseCanExecuteChanged();
        CaptureImageTemplateCommand.RaiseCanExecuteChanged();
        ShowImageSearchAreaCommand.RaiseCanExecuteChanged();
        ChooseImageSearchAreaCommand.RaiseCanExecuteChanged();
        ResetImageSearchAreaCommand.RaiseCanExecuteChanged();
        NotifyImageSetupChanged();
    }

    private bool CanEditImageSearchArea()
        => !_loading && HasSelection && ShowImageFields && !_editor.IsRunning && !_editor.IsEditingLocked;

    private ImageCondition WithImageSearchRegion(ImageCondition image)
        => new(image.TemplatePath, image.TemplateBytes, image.MinConfidence, _imageSearchRegion) { ScanOptions = _imageScanOptions };

    private void ShowImageSearchArea()
    {
        if (!CanEditImageSearchArea()) return;
        try
        {
            _editor.PreviewOwnedImageArea(
                _imageScanOptions?.SearchScope == ImageSearchScope.Anywhere ? _imageScanOptions.MonitorBounds
                    : _imageScanOptions?.SearchScope == ImageSearchScope.TargetWindow ? null : _imageSearchRegion,
                _imageScanOptions?.SearchScope != ImageSearchScope.TargetWindow &&
                (_imageScanOptions?.SearchScope == ImageSearchScope.Anywhere || _repeatImageWhileVisible && !_redirectOnAppearance),
                _editor.GetStepAt(_editor.SelectedStepIndex)?.Label ?? "Image search area");
        }
        catch (Exception ex)
        {
            _editor.NotifyStatus($"Could not show image search area: {ex.Message}");
        }
    }

    private void ChooseImageSearchArea()
    {
        if (!CanEditImageSearchArea()) return;
        Guid? selectedId = _sourceStepId;
        try
        {
            System.Drawing.Rectangle? selected = _editor.PromptOwnedRegion();
            // A modal picker can process playback or selection changes before returning.
            if (!CanEditImageSearchArea() || selectedId != _sourceStepId) return;
            if (selected is null)
            {
                _editor.NotifyStatus("Search area selection cancelled.");
                return;
            }
            if (selected.Value.Width <= 0 || selected.Value.Height <= 0)
            {
                _editor.NotifyStatus("Choose a search area with a positive width and height.");
                return;
            }
            ImageCondition? image = _pendingImageCondition
                ?? (_editor.GetStepAt(_editor.SelectedStepIndex) as WaitUntilStep)?.Condition as ImageCondition;
            if (!ValidateImageSearchArea(image, selected)) return;
            _imageSearchRegion = selected;
            if (_imageScanOptions is not null)
                _imageScanOptions = _imageScanOptions with { SearchScope = ImageSearchScope.PlacedArea };
            _draftError = null;
            NotifyImageSearchAreaChanged();
            NotifyImageSetupChanged();
            _editor.NotifyStatus("Search area selected. Apply, then Save macro to keep it.");
        }
        catch (Exception ex)
        {
            _editor.NotifyStatus($"Could not choose image search area: {ex.Message}");
        }
    }

    private void ResetImageSearchArea()
    {
        if (!CanEditImageSearchArea()) return;
        _imageSearchRegion = null;
        if (_imageScanOptions is not null)
            _imageScanOptions = _imageScanOptions with { SearchScope = ImageSearchScope.Anywhere, Resolution = ImageScanResolution.P720 };
        _draftError = null;
        NotifyImageSearchAreaChanged();
        NotifyImageSetupChanged();
        _editor.NotifyStatus("Using the default search area. Apply, then Save macro to keep it.");
    }

    private void NotifyImageSearchAreaChanged()
    {
        OnPropertyChanged(nameof(ImageSearchRegion));
        OnPropertyChanged(nameof(ImageSearchAreaSummary));
        OnPropertyChanged(nameof(DraftError));
    }

    private bool ValidateImageSearchArea(ImageCondition? image, System.Drawing.Rectangle? region)
    {
        // Default scopes are resolved at playback. Only reject an explicit area when
        // decoded metadata proves it cannot contain the complete matching template.
        if (region is not { Width: > 0, Height: > 0 } area || image is null
            || !TryReadImageTemplateSize(image, out System.Drawing.Size size)
            || (area.Width >= size.Width && area.Height >= size.Height))
            return true;

        _draftError = $"Search area is too small ({area.Width} × {area.Height} pixels). "
            + $"Choose an area at least {size.Width} × {size.Height} pixels to contain the entire template, or use the default area.";
        OnPropertyChanged(nameof(DraftError));
        _editor.NotifyStatus(_draftError);
        return false;
    }

    private static bool TryReadImageTemplateSize(ImageCondition image, out System.Drawing.Size size)
    {
        size = default;
        try
        {
            // Embedded bytes take precedence over paths, exactly as in ImageCondition.
            // DelayCreation reads metadata without decoding/copying all image pixels.
            using Stream stream = image.TemplateBytes is not null
                ? new MemoryStream(image.TemplateBytes, writable: false)
                : File.Open(image.TemplatePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            BitmapDecoder decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) return false;
            BitmapFrame frame = decoder.Frames[0];
            size = new System.Drawing.Size(frame.PixelWidth, frame.PixelHeight);
            return size.Width > 0 && size.Height > 0;
        }
        catch (Exception ex) when (ex is IOException or FormatException or NotSupportedException
            or ArgumentException or InvalidOperationException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException)
        {
            // Unavailable metadata does not prove a size mismatch. Preserve the existing
            // engine's missing/corrupt-template handling rather than introducing a new gate.
            System.Diagnostics.Trace.TraceInformation(
                "Image template size unavailable for editor area validation ({0}); playback validation remains authoritative.",
                ex.GetType().Name);
            return false;
        }
    }

    internal bool RequiresImagePreparation => HasDraftChanges && Kind == EditableActionKind.ImageWait
        && _pendingImageCondition is null
        && (_editor.GetStepAt(_editor.SelectedStepIndex) is not WaitUntilStep { Condition: ImageCondition existing }
            || !HasUsableImageTemplate(existing));

    private bool TryEnsureImageTemplate(MacroStep stored, EditableActionKind storedKind,
        MacroEditorViewModel.AuthoringInteraction? owner, out ImageCondition? prompted)
    {
        prompted = _pendingImageCondition;
        if (prompted != null)
            return true;

        if (storedKind == EditableActionKind.ImageWait
            && stored is WaitUntilStep { Condition: ImageCondition existing }
            && HasUsableImageTemplate(existing))
        {
            return true;
        }

        if (!_editor.TryPromptImageTemplate(owner, out ImageTemplateSelection? selection) || selection is null)
            return false;

        prompted = new ImageCondition(selection.ImagePath, selection.TemplateBytes, selection.Confidence);
        AdoptNewImageSelection(selection);
        _pendingImageCondition = prompted;
        return true;
    }

    private static bool HasUsableImageTemplate(ImageCondition image)
        => !string.IsNullOrWhiteSpace(image.TemplatePath) || image.TemplateBytes is { Length: > 0 };

    private void CaptureImageTemplate()
    {
        if (_editor.IsEditingLocked) return;
        var stamp = _editor.CaptureAuthoringSelection();
        if (!_editor.TryPromptImageTemplate(out ImageTemplateSelection? selection) || selection is null)
            return;

        if (!_editor.IsCurrentAuthoringSelection(stamp)) return;
        if (ShowControlFlowFields)
        {
            _currentPresenceTemplatePath = selection.ImagePath;
            _currentPresenceTemplateBytes = selection.TemplateBytes;
            _currentPresenceMinConfidence = selection.Confidence;
            if (_conditionKindIndex < 2) ConditionKindIndex = (int)VisualPresenceKind.ImagePresent;
            OnPropertyChanged(nameof(PresenceTemplateSummary));
            Apply();
            return;
        }

        _pendingImageCondition = new ImageCondition(selection.ImagePath, selection.TemplateBytes, selection.Confidence);
        AdoptNewImageSelection(selection);
        ImageTemplateSummary = $"Template: {Path.GetFileName(selection.ImagePath)} (≥{selection.Confidence:P0})";
        if (Kind != EditableActionKind.ImageWait)
        {
            _kindIndex = (int)EditableActionKind.ImageWait;
            RaiseKindVis();
        }

        Apply();
    }
}
