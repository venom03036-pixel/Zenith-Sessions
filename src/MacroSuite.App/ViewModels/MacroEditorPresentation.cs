using System.Collections.Specialized;
using System.IO;
using MacroSuite.App.Common;
using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Execution;
using MacroSuite.App.Overlays;
using MacroSuite.App.Services;
using MacroSuite.Infrastructure.Windows;
using MacroSuite.Infrastructure.Security;

namespace MacroSuite.App.ViewModels;

public sealed partial class MacroEditorViewModel
{
    private string _autoClickerHotkey = string.Empty;
    private string _saveStateText = "Saved";
    private string _runningMacroName = string.Empty;
    private Guid? _runningMacroId;
    private Guid? _executingStepId;
    private int _executingActionNumber;
    private bool _followCurrentAction = true;
    private double _libraryPaneWidth = 240;
    private bool _rebuildingOutline;
    private MacroScriptItemViewModel? _deletedMacro;
    private string? _deletedMacroPath;
    private System.Windows.Threading.DispatcherOperation? _detectionRestoreOperation;
    private Guid _presentationRunId;
    private volatile bool _presentationRunEnded;
    private long _runStartTimestamp;
    private long _progressUpdateTimestamp;
    private string _runProgressText = string.Empty;
    private Task? _playbackRunTask;
    private Task? _shutdownTask;
    private bool _shutdownStarted;
    private Guid? _failedMacroId;
    private Guid? _failedStepId;
    private string _lastExecutionError = string.Empty;
    private string _dragDestinationText = "Drag to reorder within a scope. Use block properties to move actions into or out of a block.";
    public bool HasDeletedMacro => _deletedMacro is not null;
    public string DeletedMacroNotice => _deletedMacro is { } deleted ? $"Deleted ‘{deleted.Name}’." : string.Empty;
    public RelayCommand UndoDeleteMacroCommand { get; private set; } = null!;
    public StructuredActionCollection OutlineRows { get; private set; } = null!;
    public RelayCommand PrimaryTransportCommand { get; private set; } = null!;
    public RelayCommand ShowMacroPropertiesCommand { get; private set; } = null!;
    public RelayCommand ToggleBlockCommand { get; private set; } = null!;
    public RelayCommand DuplicateMacroCommand { get; private set; } = null!;
    public Func<bool>? ExternalInputIsActive { get; set; }
    internal bool IsDisposed => _disposed;
    public string AutoClickerHotkey
    {
        get => _autoClickerHotkey;
        set { if (SetProperty(ref _autoClickerHotkey, value ?? string.Empty)) { RefreshKeybindRows(); HotkeysUpdated?.Invoke(); } }
    }
    public double LibraryPaneWidth
    {
        get => _libraryPaneWidth;
        set { if (SetProperty(ref _libraryPaneWidth, Math.Clamp(value, 220, 360))) LayoutPrefsChanged?.Invoke(); }
    }
    public bool FollowCurrentAction { get => _followCurrentAction; set => SetProperty(ref _followCurrentAction, value); }
    public bool IsEditingLocked => !HasActivation || LibraryKeyUnavailable || IsRunning || IsRecordingBusy || _shutdownStarted
        || InputOwnershipBlocksEditing || IsAuthoringInteractionActive;
    public bool CanEditMacro => !IsEditingLocked;
    public bool ShowMacroProperties => SelectedStep is null;
    public string InspectorTitle => ShowMacroProperties ? "Macro properties" : "Action properties";
    public string EditingAvailabilityText => IsRunning ? "Editing is locked until playback stops. You can inspect any action."
        : IsRecordingBusy ? "Recording actions. Editing is available after recording stops." : "Applied action edits save automatically. Save macro commits any open drafts.";
    public string InputTargetText => TargetSummary;
    public string PrimaryTransportText => IsRecordingBusy ? "Stop recording" : IsPaused ? "Resume" : IsRunning ? "Pause" : "Run";
    public string RecordingButtonText => IsRecordingBusy ? "Stop recording" : "Record";
    public string RunningMacroName => _runningMacroName;
    public Guid? ExecutingStepId => _executingStepId;
    public string TransportStateText => IsRecordingBusy ? $"{RecordingStateText} · {EventsCapturedCount} actions"
        : IsRunning ? $"{(IsPaused ? "Paused" : "Running")} · {_runningMacroName}" + (_executingActionNumber > 0 ? $" · Action {_executingActionNumber}" : "") : string.IsNullOrWhiteSpace(RecordingStateText) || RecordingStateText == "Idle" ? "Ready" : RecordingStateText;
    public string SaveStateText { get => _saveStateText; private set => SetProperty(ref _saveStateText, value); }
    public string LastExecutionError { get => _lastExecutionError; private set { if (SetProperty(ref _lastExecutionError, value)) OnPropertyChanged(nameof(HasExecutionError)); } }
    public bool HasExecutionError => !string.IsNullOrEmpty(LastExecutionError);
    public string DragDestinationText { get => _dragDestinationText; private set => SetProperty(ref _dragDestinationText, value); }
    public string RecordedDelaysText => DurationDisplay.Format(_actionStore.Items.OfType<DelayStep>().Sum(step => step.Duration.TotalMilliseconds));
    public string RunProgressText { get => _runProgressText; private set => SetProperty(ref _runProgressText, value); }

    private void UpdateRunProgress(ExecutionStatusSnapshot snapshot)
    {
        // These are samples from existing execution events, not a prediction of remaining time.
        // No UI timer, additional capture loop, or per-input-packet object is needed.
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_progressUpdateTimestamp != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_progressUpdateTimestamp, now).TotalMilliseconds < 100) return;
        _progressUpdateTimestamp = now;
        string elapsed = DurationDisplay.Format(System.Diagnostics.Stopwatch.GetElapsedTime(_runStartTimestamp, now).TotalMilliseconds);
        RunProgressText = snapshot.Checks > 0
            ? $"Elapsed at last update: {elapsed} · Condition check {snapshot.Checks} · Waiting {DurationDisplay.Format(snapshot.Elapsed.TotalMilliseconds)}"
            : snapshot.Phase == "ActionCompleted"
                ? $"Elapsed at last update: {elapsed} · Last action took {DurationDisplay.Format(snapshot.Elapsed.TotalMilliseconds)}"
                : $"Elapsed at last update: {elapsed} · Action in progress";
    }

    // Terminal errors cannot be lost when the bounded progress channel drops old samples.
    // Retain one reference per run; no second queue or task is introduced.
    private sealed class EditorExecutionStatusSink(BoundedExecutionStatusSink progress) : IExecutionStatusSink
    {
        private ExecutionStatusSnapshot? _firstFailure;
        public ExecutionStatusSnapshot? FirstFailure => Volatile.Read(ref _firstFailure);
        public bool TryWrite(ExecutionStatusSnapshot snapshot)
        {
            if (snapshot.Phase == "ActionFailed") Interlocked.CompareExchange(ref _firstFailure, snapshot, null);
            return progress.TryWrite(snapshot);
        }
    }

    private static Dictionary<Guid, int> CreateVisibleActionIndex(IReadOnlyList<MacroStep> steps)
    {
        var index = new Dictionary<Guid, int>();
        for (int root = 0; root < steps.Count; root++) Add(steps[root], root);
        return index;
        void Add(MacroStep step, int root)
        {
            // Pointer packets can number in the hundreds of thousands; they neither
            // allocate index entries nor drive row/label updates during playback.
            if (step is not MouseMoveStep) index[step.StepId] = root;
            foreach (var (_, children) in StructuredActionCollection.Branches(step))
                foreach (var child in children) Add(child, root);
        }
    }

    private void InitializeEditorPresentation()
    {
        OutlineRows = new StructuredActionCollection(Steps);
        Steps.CollectionChanged += OnOutlineSourceChanged;
        PrimaryTransportCommand = new RelayCommand(async () =>
        {
            if (IsRecordingBusy) await StopMacroAsync();
            else if (IsRunning) await PauseMacroAsync();
            else await RunMacroAsync();
        }, () => !_disposed && !ActionEditor.IsTestingDetection && (IsRunning || IsRecordingBusy || Steps.Count > 0));
        UndoDeleteMacroCommand = new RelayCommand(() =>
        {
            if (IsEditingLocked || _deletedMacro is not { } deleted) return;
            object? companionMutation = null;
            BeginCompanionMutation(deleted.Id, ref companionMutation);
            try
            {
                bool fileRestored = false;
                if (_deletedMacroPath is { } path && File.Exists(path))
                { File.Move(path, deleted.FilePath); fileRestored = true; }
                MacroScripts.Add(deleted);
                RestoreCompanionDeletedScript(deleted, fileRestored);
                ApplyScriptSorting(); SelectedScript = deleted;
                _deletedMacro = null; _deletedMacroPath = null;
                OnPropertyChanged(nameof(HasDeletedMacro)); OnPropertyChanged(nameof(DeletedMacroNotice)); HotkeysUpdated?.Invoke();
                NotifyStatus($"Restored '{deleted.Name}'.");
            }
            catch (Exception ex) { NotifyStatus($"Could not restore '{deleted.Name}': {ex.Message}"); }
            finally { CompleteCompanionMutation(companionMutation); }
        }, () => HasDeletedMacro && CanEditMacro);
        ShowMacroPropertiesCommand = new RelayCommand(() => { SelectedStep = null; ShowActionEditor = true; });
        ToggleBlockCommand = new RelayCommand(value =>
        {
            if (value is not MacroStepItemViewModel step || !step.HasChildren) return;
            // Commit before refreshing the projection so a draft cannot disappear.
            if (!IsEditingLocked && !TryCommitPendingActionEdits()) return;
            OutlineRows.Toggle(step.Step.StepId);
        });
        DuplicateMacroCommand = new RelayCommand(() =>
        {
            if (IsEditingLocked || SelectedScript is null || !TryCommitPendingActionEdits()) return;
            var source = SelectedScript;
            var copy = new MacroModel { RecordingCommit = source.RecordingCommit, Name = source.Name + " copy", Description = Description,
                Steps = _actionStore.Items.Select(step => MacroActionFactory.Clone(step)).ToList(),
                Settings = new MacroSettings { RepeatCount = RepeatCount, RepeatUntilStopped = RepeatUntilStopped,
                    Target = source.Target, PlaybackSpeed = PlaybackSpeed, MaxRuntime = source.MaxRuntime, MaxIterations = source.MaxIterations,
                    MaxLoopDepth = source.MaxLoopDepth } };
            AddGeneratedMacro(copy);
        }, () => CanEditMacro && SelectedScript is not null);
    }
    private void OnOutlineSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _rebuildingOutline = true;
        try { OutlineRows.Rebuild(); }
        finally { _rebuildingOutline = false; }
        PrimaryTransportCommand?.RaiseCanExecuteChanged();
    }
    internal void MarkPendingEdits()
    {
        if (!IsEditingLocked) RefreshSaveState();
    }
    internal void MarkDraftApplied() => RefreshSaveState();
    private void RaiseTransportPresentation()
    {
        RefreshImportAvailability();
        foreach (var property in new[] { nameof(TransportStateText), nameof(PrimaryTransportText), nameof(RecordingButtonText),
            nameof(CanEditMacro), nameof(IsEditingLocked), nameof(EditingAvailabilityText), nameof(RunningMacroName), nameof(RunningMacroHotkey) }) OnPropertyChanged(property);
        PrimaryTransportCommand?.RaiseCanExecuteChanged();
        ActionEditor?.RefreshCommandAvailability();
        if (DuplicateStepCommand is not null) RaiseEditCommands();
    }
    internal void RefreshDetectionAvailability()
    {
        RefreshImportAvailability();
        PrimaryTransportCommand?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(IsDetectionTesting));
    }
    public bool IsDetectionTesting => ActionEditor.IsTestingDetection;

    /// <summary>Stops producers and joins owned work before the application disposes shared services.</summary>
    public Task ShutdownAsync()
    {
        if (_shutdownTask is not null) return _shutdownTask;
        _shutdownStarted = true;
        _ownedEditorPlayback?.Lease.SignalStop();
        _recordingInputOperation?.SignalStop();
        _editorCaptureLease?.SignalStop();
        _editorSnipLease?.SignalStop();
        _editorInteractionLease?.SignalStop();
        _editorPickerLease?.SignalStop();
        if (_ownsInputOperations) InputOperations.Close();
        CloseCompanionCatalog();
        RaiseTransportPresentation();
        return _shutdownTask = ShutdownCoreAsync();
    }

    private async Task ShutdownCoreAsync()
    {
        var authoringProducer = _producerAuthoringRequest?.Work;
        ActionEditor.CancelDetectionTest();
        if (_isStartingRecording) _recordingCts?.Cancel();
        MacroController? controller;
        Task? playback;
        lock (_playbackSync) { controller = _activeController; playback = _playbackRunTask ?? _playbackExecutionTask; }
        if (controller is not null) await DrainOwnedTaskAsync(controller.StopAsync(), "playback cancellation");
        // Keep the dispatcher free: recording startup/finalization and playback's final
        // presentation are allowed to complete before shared capture/input are released.
        if (_recordingStartCompletion is { } startup)
            await DrainOwnedTaskAsync(startup.Task, "recording startup");
        if (_recordingInputStartup is { } inputStartup)
            await DrainOwnedTaskAsync(inputStartup, "owned recording startup");
        await DrainOwnedTaskAsync(StopRecordingAsync(), "recording finalization");
        if (_recordingCommandTask is { } recordingCommand)
            await DrainOwnedTaskAsync(recordingCommand, "recording command");
        if (_recordingCaptureStopTask is { } captureStop)
            await DrainOwnedTaskAsync(captureStop, "recording capture stop");
        _recordingInputBuffer?.TryComplete();
        if (_recordingConsumerTask is { } consumer)
            await DrainOwnedTaskAsync(consumer, "recording consumer");
        if (_recordingStopTask is { } recordingStop)
            await DrainOwnedTaskAsync(recordingStop, "recording finalization");
        await FinishRecordingInputOwnershipAsync();
        if (playback is not null) await DrainOwnedTaskAsync(playback, "playback");
        if (authoringProducer is not null) await DrainOwnedTaskAsync(authoringProducer, "producer image preparation");
        await ActionEditor.StopDetectionTestAsync();
        await ShutdownTargetSelectionAsync();
        if (_editorCaptureTask is { } captureWork) await DrainOwnedTaskAsync(captureWork, "owned capture test");
        if (_editorSnipTask is { } snipWork) await DrainOwnedTaskAsync(snipWork, "owned screen snip");
        if (_editorPickerTask is { } pickerWork) await DrainOwnedTaskAsync(pickerWork, "owned location picker");
        if (_editorInteractionCompletion is { } interactionWork) await DrainOwnedTaskAsync(interactionWork, "owned modal interaction");
        await DrainOwnedTaskAsync(_recordingRecoveryTask, "recording recovery");
        Task? companionRead = null;
        DrainCompanionDocuments(ref companionRead);
        if (companionRead is not null) await DrainOwnedTaskAsync(companionRead, "saved document read");
        if (_detectionRestoreOperation is { } restore)
            await DrainOwnedTaskAsync(restore.Task, "detection preview restoration");
    }

    private static async Task DrainOwnedTaskAsync(Task task, string operation)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError($"Editor shutdown {operation} failed: {ex}"); }
    }

    private void SetExecutingAction(Guid id, int rootIndex)
    {
        _executingStepId = id;
        _executingActionNumber = rootIndex + 1;
        OnPropertyChanged(nameof(ExecutingStepId));
        OnPropertyChanged(nameof(TransportStateText));
        OutlineRows.SetExecution(SelectedScript?.Id == _runningMacroId ? id : null);
        if (FollowCurrentAction && SelectedScript?.Id == _runningMacroId)
            StepNavigationRequested?.Invoke(OutlineRows.FindVisibleExecution(id));
    }
    private void RecordActionFailure(Guid actionId, string message)
    {
        if (_failedStepId.HasValue) return; // Keep the original child, not each enclosing block's rethrow.
        _failedStepId = actionId;
        _failedMacroId = _runningMacroId;
        LastExecutionError = $"Stopped: {message} Open the Log tab for details.";
        OutlineRows.SetFailure(SelectedScript?.Id == _failedMacroId ? actionId : null, message);
    }
    private static void DispatchEditorPresentation(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else
        {
            try { if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(action); }
            catch (InvalidOperationException) when (dispatcher.HasShutdownStarted) { }
        }
    }

    /// <summary>Imports reviewed generated content as an owned library entry; never executes it.</summary>
    public bool AddGeneratedMacro(MacroModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (IsEditingLocked || !TryCommitPendingActionEdits()) return false;
        try
        {
            string json = _serializer.Serialize(model);
            var validation = new MacroFileValidator().Validate(json, "Generated macro.json");
            if (!validation.IsValid) throw new InvalidDataException(string.Join(" ", validation.Violations));
            // Detach every action/template from provider-owned objects before accepting the draft.
            MacroModel detached = _serializer.Deserialize(json);
            MacroSettings settings = detached.Settings ?? new MacroSettings();
            var script = new MacroScriptItemViewModel(string.IsNullOrWhiteSpace(detached.Name) ? "Generated macro" : detached.Name,
                detached.Steps.Count, string.Empty, settings.IsActive, "Never", Guid.NewGuid(), string.Empty, DateTimeOffset.UtcNow)
            {
                Description = detached.Description, MaxRuntime = settings.MaxRuntime,
                MaxIterations = settings.MaxIterations, MaxLoopDepth = settings.MaxLoopDepth,
                RepeatCount = Math.Max(1, settings.RepeatCount), RepeatUntilStopped = settings.RepeatUntilStopped,
                PlaybackSpeed = settings.PlaybackSpeed, MouseSamplingMode = settings.MouseSamplingMode
            };
            script.Actions.AddRange(detached.Steps);
            BlockRouting.Validate(script.Actions);
            // Save before changing selection; a failure leaves the current editor intact.
            if (_isPersistenceEnabled && !TrySaveMacroScript(script)) return false;
            MacroScripts.Add(script);
            ApplyScriptSorting();
            SelectedScript = script;
            SelectedStep = null;
            NotifyStatus($"Added '{script.Name}' to the library. Review its actions, then choose Run when ready.");
            return true;
        }
        catch (Exception ex)
        {
            RefreshSaveState();
            NotifyStatus($"Could not add generated macro: {ex.Message}");
            return false;
        }
    }

    private bool IsNestedSelection => SelectedStep is not null && Steps.IndexOf(SelectedStep) < 0;
    private async Task<MatchResult> ProbeImageCoreAsync(ImageCondition image, bool fullScreenDefault, CancellationToken ct, Action<bool> cleanupState)
    {
        if (!await EnsureActivationForOperationAsync(ct)) throw new MacroSuite.Core.Licensing.ActivationRequiredException("Activate this installation before testing detection.");
        if (IsEditingLocked || ExternalInputIsActive?.Invoke() == true)
            throw new InvalidOperationException("Stop playback, recording, and Auto Clicker before testing detection.");
        ct.ThrowIfCancellationRequested();
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        cleanupState(false);
        IDisposable hidden;
        try
        {
            hidden = dispatcher is not null && !dispatcher.CheckAccess()
                ? await dispatcher.InvokeAsync(() => ScreenCapturePrivacy.HideEngine())
                : ScreenCapturePrivacy.HideEngine();
        }
        catch (Exception error)
        {
            // Privacy setup guarantees rollback or reports explicit unresolved cleanup.
            cleanupState(error is not MacroSuite.Core.Input.HostInputOperationCleanupException);
            throw;
        }
        try
        {
            var region = image.SearchRegion;
            if (image.ScanOptions?.SearchScope == ImageSearchScope.Anywhere)
                region = image.ScanOptions.MonitorBounds;
            bool targetWindow = image.ScanOptions?.SearchScope == ImageSearchScope.TargetWindow;
            if (targetWindow) region = null;
            if (!region.HasValue && (targetWindow || !fullScreenDefault && image.ScanOptions?.SearchScope != ImageSearchScope.Anywhere)
                && System.Windows.Application.Current is not null)
            {
                var coordinates = WindowCoordinateService.Default;
                IntPtr foreground = coordinates.GetForegroundWindow();
                if (foreground != IntPtr.Zero && coordinates.IsWindowValid(foreground))
                {
                    var (width, height) = coordinates.GetClientSize(foreground);
                    var (x, y) = coordinates.ClientToScreen(foreground, 0, 0);
                    if (width > 32 && height > 32) region = new System.Drawing.Rectangle(x, y, width, height);
                }
            }
            if (targetWindow && region is null) throw new InvalidOperationException("Focus the target window before testing this image.");
            if (image.ScanOptions?.SearchScope == ImageSearchScope.PlacedArea && region is null)
                throw new InvalidOperationException("Place a search area before testing this image.");
            var probe = new ImageDetectionProbe(_captureService, _imageMatcher);
            return await Task.Run(() => probe.TestAsync(image, region, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                _detectionRestoreOperation = dispatcher.BeginInvoke(hidden.Dispose);
                await _detectionRestoreOperation.Task.ConfigureAwait(false);
            }
            else hidden.Dispose();
            cleanupState(true);
        }
    }

    private bool TryTransferNestedBlock(string slot, bool extract)
    {
        if (!IsNestedSelection || SelectedStep is null) return false;
        var entry = OutlineRows.Find(SelectedStep.Step.StepId);
        if (entry?.ParentId is not Guid parentId) return true;
        var root = _actionStore[entry.RootIndex];
        var parent = FindInTree(root, parentId)!;
        var siblings = GetBlockChildren(parent, entry.Slot)!.ToList();
        var block = siblings[entry.ChildIndex];
        var children = GetBlockChildren(block, slot)?.ToList();
        if (children is null) return true;
        if (extract)
        {
            if (children.Count == 0) return true;
            var child = children[^1]; children.RemoveAt(children.Count - 1);
            siblings[entry.ChildIndex] = WithBlockChildren(block, slot, children);
            siblings.Insert(entry.ChildIndex + 1, child);
        }
        else
        {
            if (entry.ChildIndex + 1 >= siblings.Count) { NotifyStatus("Place an action after this block in the same scope first."); return true; }
            var child = siblings[entry.ChildIndex + 1]; siblings.RemoveAt(entry.ChildIndex + 1); children.Add(child);
            siblings[entry.ChildIndex] = WithBlockChildren(block, slot, children);
        }
        var nextParent = WithBlockChildren(parent, entry.Slot, siblings);
        CommitNestedRoot(entry.RootIndex, RewriteTree(root, parentId, _ => nextParent), block.StepId);
        return true;
    }
    private int RootIndexOf(MacroStepItemViewModel? item)
    {
        if (item is null) return -1;
        for (int i = 0; i < _actionStore.Count; i++)
            if (StructuredActionCollection.ContainsStep(_actionStore[i], item.Step.StepId)) return i;
        return -1;
    }
    private static MacroStep? FindInTree(MacroStep root, Guid id)
    {
        if (root.StepId == id) return root;
        foreach (var branch in StructuredActionCollection.Branches(root))
            foreach (var child in branch.Children) if (FindInTree(child, id) is { } found) return found;
        return null;
    }
    internal string NextConditionalDestination(Guid? sourceId)
    {
        if (sourceId is null) return "next eligible condition, or finish this scope if none remains";
        var path = new List<(IReadOnlyList<MacroStep> Steps, int Index)>();
        if (!Find(_actionStore.Items, sourceId.Value)) return "next eligible condition";
        for (int level = path.Count - 1; level >= 0; level--)
        {
            var (steps, index) = path[level];
            int next = BlockRouting.FindNextConditional(steps, index, steps.Count);
            if (next >= 0)
                return $"{(level == 0 ? "action " + (next + 1) : "next condition in the containing scope")}: {MacroStepItemViewModel.FromStep(steps[next], next).ReadableName}";
        }
        return "finish the macro (no eligible condition remains)";

        bool Find(IReadOnlyList<MacroStep> steps, Guid id)
        {
            for (int i = 0; i < steps.Count; i++)
            {
                path.Add((steps, i));
                if (steps[i].StepId == id) return true;
                foreach (var branch in StructuredActionCollection.Branches(steps[i]))
                    if (Find(branch.Children, id)) return true;
                path.RemoveAt(path.Count - 1);
            }
            return false;
        }
    }
    private static MacroStep RewriteTree(MacroStep root, Guid id, Func<MacroStep, MacroStep?> replace)
    {
        if (root.StepId == id) return replace(root) ?? root;
        MacroStep current = root;
        foreach (var branch in StructuredActionCollection.Branches(root))
        {
            bool changed = false;
            var children = new List<MacroStep>();
            foreach (MacroStep child in branch.Children)
            {
                MacroStep? next = child.StepId == id ? replace(child) : RewriteTree(child, id, replace);
                changed |= !ReferenceEquals(next, child);
                if (next is not null) children.Add(next);
            }
            if (changed) current = WithBlockChildren(current, branch.Slot, children);
        }
        return current;
    }
    private bool CommitNestedRoot(int index, MacroStep nextRoot, Guid? selectionId, AuthoringInteraction? owner = null)
    {
        if (owner is null ? IsEditingLocked : !OwnsAuthoringDraft(owner)) return false;
        var candidate = _actionStore.ToList(); candidate[index] = nextRoot;
        if (!ValidateEditorStructure(candidate)) return false;
        _editHistory.Push(new ReplaceAtEdit(index, _actionStore[index], nextRoot));
        _committingSelection = true;
        try
        {
            Steps.ReplaceAt(index, nextRoot);
            SelectedStep = selectionId is { } id && FindInTree(nextRoot, id) is { } selected
                ? (selected.StepId == nextRoot.StepId ? Steps[index] : OutlineRows.FindVisibleExecution(selected.StepId)) : Steps[index];
            ActionEditor.LoadFrom(SelectedStep?.Step);
        }
        finally { _committingSelection = false; }
        RefreshStepOrder(); RaiseEditCommands();
        return true;
    }
    private bool TryEditNested(string operation)
    {
        if (!IsNestedSelection || SelectedStep is null) return false;
        if (IsEditingLocked) return true;
        var entry = OutlineRows.Find(SelectedStep.Step.StepId);
        if (entry?.ParentId is not Guid parentId) return true;
        MacroStep root = _actionStore[entry.RootIndex];
        MacroStep parent = FindInTree(root, parentId)!;
        var children = GetBlockChildren(parent, entry.Slot)!.ToList();
        int i = entry.ChildIndex;
        Guid? selectedId = SelectedStep.Step.StepId;
        if (operation == "remove") { children.RemoveAt(i); selectedId = parentId; }
        else if (operation == "duplicate") { var clone = MacroActionFactory.Clone(children[i]); children.Insert(i + 1, clone); selectedId = clone.StepId; }
        else
        {
            int to = operation switch { "up" => Math.Max(0, i - 1), "down" => Math.Min(children.Count - 1, i + 1), "top" => 0, _ => children.Count - 1 };
            var child = children[i]; children.RemoveAt(i); children.Insert(to, child);
        }
        MacroStep nextParent = WithBlockChildren(parent, entry.Slot, children);
        CommitNestedRoot(entry.RootIndex, RewriteTree(root, parentId, _ => nextParent), selectedId);
        return true;
    }
    private bool TryInsertNested(MacroStep step)
    {
        if (!IsNestedSelection || SelectedStep is null) return false;
        var entry = OutlineRows.Find(SelectedStep.Step.StepId);
        if (entry?.ParentId is not Guid parentId) return true;
        var root = _actionStore[entry.RootIndex];
        var parent = FindInTree(root, parentId)!;
        var children = GetBlockChildren(parent, entry.Slot)!.ToList();
        children.Insert(entry.ChildIndex + 1, step);
        CommitNestedRoot(entry.RootIndex, RewriteTree(root, parentId, _ => WithBlockChildren(parent, entry.Slot, children)), step.StepId);
        return true;
    }
    private bool CanMoveSelection(bool forward)
    {
        if (IsEditingLocked || SelectedStep is null) return false;
        var entry = OutlineRows.Find(SelectedStep.Step.StepId);
        if (entry is null) return false;
        if (entry.ParentId is not Guid parentId) return forward ? entry.RootIndex < Steps.Count - 1 : entry.RootIndex > 0;
        var parent = FindInTree(_actionStore[entry.RootIndex], parentId)!;
        return forward ? entry.ChildIndex < GetBlockChildren(parent, entry.Slot)!.Count - 1 : entry.ChildIndex > 0;
    }
    private bool ValidateEditorStructure(IReadOnlyList<MacroStep> roots)
    {
        try
        {
            BlockRouting.Validate(roots);
            Visit(roots, 0, false);
            return true;
        }
        catch (InvalidOperationException ex) { NotifyStatus($"Cannot move or edit this block: {ex.Message}"); return false; }
        void Visit(IReadOnlyList<MacroStep> children, int depth, bool insideLoop)
        {
            if (depth > (SelectedScript?.MaxLoopDepth ?? 8)) throw new InvalidOperationException("The maximum block nesting depth would be exceeded.");
            foreach (var child in children)
            {
                if (child is BreakStep or ContinueStep && !insideLoop)
                    throw new InvalidOperationException("Break and Continue must stay inside a loop.");
                foreach (var branch in StructuredActionCollection.Branches(child)) Visit(branch.Children, depth + 1, insideLoop || child is LoopStep);
            }
        }
    }
}
