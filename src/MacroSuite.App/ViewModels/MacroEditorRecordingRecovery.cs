using MacroSuite.App.Common;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Models;

namespace MacroSuite.App.ViewModels;

public sealed partial class MacroEditorViewModel
{
    private IReadOnlyList<RecordingRecoveryCandidate> _pendingRecordingRecovery = [];
    private Task _recordingRecoveryTask = Task.CompletedTask;
    private bool _recoveringRecording;
    private RelayCommand? _recoverRecordingCommand;
    public bool HasPendingRecordingRecovery => _pendingRecordingRecovery.Count > 0;
    public string RecordingRecoveryText => _recoveringRecording ? "Recovering recording…" : $"Recover recordings ({_pendingRecordingRecovery.Count})";
    public RelayCommand RecoverRecordingCommand => _recoverRecordingCommand ??= new RelayCommand(() =>
    {
        if (_recordingRecoveryTask.IsCompleted) _recordingRecoveryTask = RecoverRecordingsAsync();
    }, () => !_recoveringRecording && !IsRecordingBusy && !IsRunning && !_shutdownStarted && HasPendingRecordingRecovery);

    public void CheckForRecordingRecovery()
    {
        if (!_isPersistenceEnabled || _disposed || !_recordingRecoveryTask.IsCompleted || _documentStore is not IRecordingRecoveryProvider) return;
        _recordingRecoveryTask = CheckForRecordingRecoveryAsync();
    }
    private async Task CheckForRecordingRecoveryAsync()
    {
        if (_documentStore is not IRecordingRecoveryProvider provider) return;
        try { _pendingRecordingRecovery = await provider.RecordingRecovery.ReadPendingAsync(); }
        catch (Exception ex) { NotifyStatus("Recording recovery could not be opened. Its encrypted files were preserved: " + ex.Message); }
        RefreshRecordingRecovery();
    }
    private void RefreshRecordingRecovery()
    {
        OnPropertyChanged(nameof(HasPendingRecordingRecovery)); OnPropertyChanged(nameof(RecordingRecoveryText));
        RecoverRecordingCommand.RaiseCanExecuteChanged();
    }
    private async Task RecoverRecordingsAsync()
    {
        if (_documentStore is not IRecordingRecoveryProvider provider || _recoveringRecording) return;
        // Recovery is allowed after activation loss; it emits no input. Preserve visible drafts before reloading their saved macro.
        if (!IsEditingLocked && SelectedScript != null && (!TryCommitPendingActionEdits() || !TrySaveMacroScript(SelectedScript))) return;
        _recoveringRecording = true; RefreshRecordingRecovery();
        try
        {
            int recovered = 0, deferredForDraft = 0;
            foreach (var candidate in _pendingRecordingRecovery)
            {
                if (candidate.Start == null) { NotifyStatus(candidate.Problem ?? "Recovery cannot authenticate this session. Original files were preserved."); continue; }
                var visibleDestination = MacroScripts.FirstOrDefault(script => script.Id == candidate.Start.Baseline.Id);
                if (visibleDestination != null &&
                    ((_scriptSaveStates.TryGetValue(visibleDestination.Id, out var saveState) &&
                        (saveState.HasFailed || saveState.IsSaving || saveState.Baseline != CurrentMetadata(visibleDestination))) ||
                     (ReferenceEquals(visibleDestination, SelectedScript) && ActionEditor.HasDraftChanges)))
                {
                    // A locked or failed-save draft is still owned by the editor. Keep
                    // its journal pending instead of replacing either the draft or disk baseline.
                    deferredForDraft++; continue;
                }
                object? companionMutation = null;
                BeginCompanionMutation(null, ref companionMutation);
                try
                {
                    MacroModel? model = await provider.RecordingRecovery.RecoverAsync(candidate);
                    if (model == null) continue;
                    recovered++;
                    // Never replace a visible unsaved draft: recovery with a changed baseline already has a separate ID.
                    if (!_libraryLoadDeferred)
                    {
                        var existing = MacroScripts.FirstOrDefault(s => s.Id == model.Id);
                        string savedPath = existing?.FilePath ?? _documentStore.GetLibraryPath(model.Id);
                        if (existing != null) MacroScripts.Remove(existing);
                        var settings = model.Settings;
                        var script = new MacroScriptItemViewModel(model.Name, model.Steps.Count, settings.Hotkey,
                            settings.IsActive, "Recovered", model.Id, _documentStore.GetLibraryPath(model.Id), model.CreatedAt, model.IsFavorite)
                        { Description = model.Description, Target = settings.Target, RecordingCommit = model.RecordingCommit,
                            RepeatCount = settings.RepeatCount, RepeatUntilStopped = settings.RepeatUntilStopped, PlaybackSpeed = settings.PlaybackSpeed,
                            MaxRuntime = settings.MaxRuntime, MaxIterations = settings.MaxIterations, MaxLoopDepth = settings.MaxLoopDepth,
                            MouseSamplingMode = settings.MouseSamplingMode };
                        script.Actions.AddRange(model.Steps); MacroScripts.Add(script);
                        if (ReferenceEquals(_selectedScript, existing)) _selectedScript = null;
                        SelectedScript = script;
                        CompleteCompanionMutation(companionMutation);
                        companionMutation = null;
                        Task? companionRead = null;
                        ObserveCompanionRecovery(model.Id, savedPath, ref companionRead);
                        if (companionRead is not null) await companionRead;
                    }
                    if (candidate.Problem != null) LogActivity(candidate.Problem);
                }
                finally { CompleteCompanionMutation(companionMutation); }
            }
            _pendingRecordingRecovery = await provider.RecordingRecovery.ReadPendingAsync();
            NotifyStatus($"Recovered {recovered} recording(s). Damaged recovery originals are kept for inspection." +
                (deferredForDraft > 0 ? $" {deferredForDraft} recovery session(s) remain pending: save or revert the destination's open draft, then recover again." : ""));
            ApplyScriptSorting(); HotkeysUpdated?.Invoke();
        }
        catch (Exception ex) { NotifyStatus("Recording recovery failed. Original files and retained actions were preserved: " + ex.Message); }
        finally { _recoveringRecording = false; RefreshRecordingRecovery(); }
    }
}
