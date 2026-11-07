using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;

namespace MacroSuite.App.ViewModels;

public sealed partial class ActionPropertiesViewModel
{
    private object?[]? _draftBaseline;
    private object?[]? _observedDraftValues;
    internal long DraftRevision { get; private set; }
    private bool _refreshingDraftState;
    public bool HasDraftChanges => _hasDraftChanges;

    // Compare editable values, not PropertyChanged names. Route refreshes, previews,
    // capture prompts and visibility notifications are presentation, not edits.
    private object?[] ReadDraftValues()
    {
        var storedImage = (_editor.GetStepAt(_editor.SelectedStepIndex) as WaitUntilStep)?.Condition as ImageCondition;
        var image = _pendingImageCondition ?? storedImage;
        return [
            _kindIndex, _x, _y, _xVariable, _yVariable, _clickOffsetX, _clickOffsetY, _clickIsClientRelative,
            _lookPeakXMin, _lookPeakXMax, _lookPeakYMin, _lookPeakYMax, _width, _height, _durationMs,
            _sensitivityX, _sensitivityY, _playbackHz, _trajectoryModeIndex, _interpolationIndex,
            _horizontalSmoothing, _verticalSmoothing, _filterVerticalJitter, _filterHorizontalJitter,
            _profileIndex, _clickCount, _mouseButtonIndex, _keyName, _keyCode, _strokeIndex,
            _similarityPercent, _timeoutMs, _searchModeIndex, _onTimeoutIndex,
            _r, _g, _b, _tolerance, _detectionModeIndex, _minimumMatchPercentage, _paletteText,
            _requiredConfirmations, _requiredFalseConfirmations, _pollIntervalMs, _loopCheckIntervalMs,
            _sampleStride, _stabilityDurationMs, _failurePolicyIndex, _conditionTimeoutMs,
            _prompt, _expectedWindow, _minConfidenceText, _maximumAccuracy, _conditionKindIndex,
            _waitWhile, _repeatImageWhileVisible, _imageRepeatDelayMs, _redirectOnAppearance,
            _appearanceTargetStepId, _appearanceCooldownMs, _onNotSatisfiedRouteIndex, _onFailureRouteIndex,
            _routeTargetStepId, _routeRetryCount, _routeRetryDelayMs, _minBodyDelayMs,
            _currentPresenceTemplatePath, _currentPresenceTemplateBytes, _currentPresenceMinConfidence,
            image?.TemplatePath, image?.TemplateBytes, image?.MinConfidence, _imageSearchRegion, _imageScanOptions ?? new ImageScanOptions()
        ];
    }

    private void CaptureDraftBaseline()
    {
        _draftBaseline = HasSelection ? ReadDraftValues() : null;
        _observedDraftValues = _draftBaseline;
        DraftRevision++;
        _hasDraftChanges = false;
        OnPropertyChanged(nameof(HasDraftChanges));
        _editor.RefreshSaveState();
    }

    private void RefreshDraftState()
    {
        if (_loading || _refreshingDraftState) return;
        _refreshingDraftState = true;
        try
        {
            object?[]? values = HasSelection && _draftBaseline is not null ? ReadDraftValues() : null;
            if (!DraftValuesEqual(_observedDraftValues, values))
            {
                _observedDraftValues = values;
                DraftRevision++;
            }
            bool changed = values is not null && !DraftValuesEqual(_draftBaseline, values);
            if (_hasDraftChanges != changed)
            {
                _hasDraftChanges = changed;
                OnPropertyChanged(nameof(HasDraftChanges));
            }
            _editor.RefreshSaveState();
            OnPropertyChanged(nameof(ImageOutcomeSummary));
            OnPropertyChanged(nameof(BlockOutcomeSummary));
        }
        finally { _refreshingDraftState = false; }
    }

    private static bool DraftValuesEqual(object?[]? left, object?[]? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++)
        {
            if (ReferenceEquals(left[i], right[i])) continue;
            if (left[i] is byte[] a && right[i] is byte[] b)
            {
                if (!a.AsSpan().SequenceEqual(b)) return false;
            }
            else if (!Equals(left[i], right[i])) return false;
        }
        return true;
    }
}

public sealed partial class MacroEditorViewModel
{
    private sealed record MacroMetadata(string Name, string Description, int RepeatCount, bool RepeatUntilStopped, double PlaybackSpeed, MacroTargetRule? Target);
    private sealed class ScriptSaveState(MacroMetadata baseline)
    {
        public MacroMetadata Baseline { get; set; } = baseline;
        public bool IsSaving { get; set; }
        public bool HasFailed { get; set; }
    }
    private readonly Dictionary<Guid, ScriptSaveState> _scriptSaveStates = new();

    public bool HasMacroMetadataChanges => SelectedScript is { } script
        && _scriptSaveStates.TryGetValue(script.Id, out var state)
        && state.Baseline != CurrentMetadata(script);

    private MacroMetadata CurrentMetadata(MacroScriptItemViewModel script) => script == SelectedScript
        ? new(MacroName, Description, RepeatCount, RepeatUntilStopped, PlaybackSpeed, script.Target)
        : new(script.Name, script.Description, script.RepeatCount, script.RepeatUntilStopped, script.PlaybackSpeed, script.Target);

    private ScriptSaveState SaveStateFor(MacroScriptItemViewModel script)
    {
        if (!_scriptSaveStates.TryGetValue(script.Id, out var state))
            _scriptSaveStates[script.Id] = state = new(CurrentMetadata(script));
        return state;
    }

    internal void RefreshSaveState()
    {
        var state = SelectedScript is { } script ? SaveStateFor(script) : null;
        SaveStateText = state?.HasFailed == true ? "Save failed"
            : state?.IsSaving == true ? "Saving…"
            : ActionEditor?.HasDraftChanges == true ? "Unapplied changes"
            : HasMacroMetadataChanges ? "Unsaved macro changes"
            : _isPersistenceEnabled ? "Saved" : "Not saved to disk";
        OnPropertyChanged(nameof(HasMacroMetadataChanges));
    }

    private void BeginScriptSave(MacroScriptItemViewModel script)
    {
        SaveStateFor(script).IsSaving = true;
        RefreshSaveState();
    }

    private void CompleteScriptSave(MacroScriptItemViewModel script, MacroModel? saved, object? companionAttempt = null)
    {
        var state = SaveStateFor(script);
        state.IsSaving = false;
        state.HasFailed = saved is null;
        if (saved is not null)
        {
            script.RecordingCommit = saved.RecordingCommit;
            state.Baseline = new(saved.Name, saved.Description, saved.Settings.RepeatCount,
                saved.Settings.RepeatUntilStopped, saved.Settings.PlaybackSpeed, saved.Settings.Target);
        }
        try { RefreshSaveState(); }
        finally { CompleteCompanionSave(script, saved, companionAttempt); }
    }
}
