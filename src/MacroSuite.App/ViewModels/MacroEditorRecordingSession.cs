using System.Diagnostics;
using MacroSuite.Core.Execution;
using MacroSuite.App.Common;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Core.Storage;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Rules;
using MacroSuite.Infrastructure.Input;
using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.App.ViewModels;

public sealed partial class MacroEditorViewModel
{
    private sealed class OwnedRecording
    {
        public readonly Guid Id = Guid.NewGuid();
        public required MacroScriptItemViewModel Destination;
        public required MacroModel Baseline;
        public required long StartTick;
        public required TimeSpan AppendOffset;
        public required RecordingHotkeyFilter[] Filters;
        public required (uint Modifiers, uint VirtualKey)[] Shortcuts;
        public ResolvedMacroTarget? Target;
        public RecordingDesktopSnapshot? Desktop;
        public IRecordingRecoverySession? Journal;
        public readonly TaskCompletionSource StopSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? SignalWatcher;
        public HostInputOperationLease? InputLease;
        public CancellationTokenRegistration InputStop;
        public bool InputRetirementFailed;
        public int Paused, Emergency, Closed, RestoreRequested, NeedsFinalization;
        public int CapturedButtons;
        public bool ButtonsInitialized;
        public readonly object AdmissionSync = new();
        public long ExclusionStart, ExcludedTicks, Epoch, LastTick;
        public bool RecoveryFailed;
        public string? DestinationFileHash;
        public bool DestinationRevisionCaptured;
        public bool DestinationRevisionConflict;
        public int PlaybackHz;
        public double GainX, GainY;
        public TrajectoryPlaybackMode CameraMode;
        public MouseSamplingMode SamplingMode;
        public int MinDistancePx;
        public double MinTimeMs;
        public bool UseRawMouseInput;
    }
    private OwnedRecording? _ownedRecording;
    private RecordedInput _convertingPacket;
    private string _recordingPhase = "Idle";
    private string _recordingTerminalReason = string.Empty;
    private PlaybackSessionContext? _playbackSession;
    private ITargetWindowService? _targets;
    private ITargetWindowService Targets => _targets ??= new Win32TargetWindowService();
    public bool IsRecordingPaused => _ownedRecording is { } s && Volatile.Read(ref s.Paused) != 0;
    public bool IsRecordingFinishing => _recordingPhase == "Stopping";
    public string RecordingStateText => _recordingPhase == "Idle" ? _recordingTerminalReason : _recordingPhase;
    public string RecordingCheckpointText => _ownedRecording?.Journal?.Checkpoint.SavedAt is { } saved
        ? $"Recovery saved through {saved.LocalDateTime:T}. The newest unflushed input may be lost in a sudden crash."
        : "Waiting for the first encrypted recovery checkpoint.";
    private void SetRecordingPhase(string phase)
    {
        _recordingPhase = phase;
        void Publish() { OnPropertyChanged(nameof(RecordingStateText)); OnPropertyChanged(nameof(IsRecordingPaused));
            OnPropertyChanged(nameof(IsRecordingFinishing)); OnPropertyChanged(nameof(RecordingCheckpointText)); RaiseTransportPresentation(); }
        if (!TryPostToUi(Publish)) Publish();
    }
    private void RequestRecordingEditorRestore()
    {
        var session = _ownedRecording;
        if (session != null && Interlocked.Exchange(ref session.RestoreRequested, 1) != 0) return;
        LeaveCompactOverlay?.Invoke();
    }
    private MacroModel SnapshotRecordingDestination(MacroScriptItemViewModel script) => new()
    {
        Id = script.Id, Name = script.Name, Description = Description, CreatedAt = script.CreatedAt,
        IsFavorite = script.IsFavorite, RecordingCommit = script.RecordingCommit,
        Steps = ReferenceEquals(script, SelectedScript) ? _actionStore.Items.ToArray() : [],
        Settings = new MacroSettings { Target = script.Target, Hotkey = script.Hotkey, IsActive = script.IsActive,
            RepeatCount = RepeatCount, RepeatUntilStopped = RepeatUntilStopped, PlaybackSpeed = PlaybackSpeed,
            MaxRuntime = script.MaxRuntime, MaxIterations = script.MaxIterations, MaxLoopDepth = script.MaxLoopDepth,
            MouseSamplingMode = MouseSamplingMode }
    };
    private OwnedRecording ClaimRecording(ProducerTargetPin? targetPin)
    {
        var script = SelectedScript ?? new MacroScriptItemViewModel("New Macro", 0, string.Empty) { Target = new MacroTargetRule() };
        var start = Stopwatch.GetTimestamp();
        var shortcuts = new[] { RecordHotkey, StopHotkey, PlayHotkey, PauseHotkey, PanicHotkey, MiniToolbarClickThroughHotkey };
        var parsed = shortcuts.Select(h => { HotkeyCodec.TryParse(h, out uint mods, out uint key); return (mods, key); }).ToArray();
        var filters = shortcuts.Select(h => { HotkeyCodec.TryParse(h, out uint mods, out uint key); return new RecordingHotkeyFilter(mods, key); }).ToArray();
        bool hasSelection = SelectedScript != null;
        // A hidden editor may still display the last document after deselection.
        // New recording destinations must never inherit that document's fields or timeline.
        var snapshot = hasSelection ? SnapshotRecordingDestination(script)
            : new MacroModel { Id = script.Id, Name = script.Name, Settings = new MacroSettings { Target = new MacroTargetRule() } };
        var session = new OwnedRecording { Destination = script, Baseline = snapshot,
            AppendOffset = hasSelection ? GetLatestRelativeTimestamp() : TimeSpan.Zero, StartTick = start, LastTick = start, Filters = filters, Shortcuts = parsed,
            PlaybackHz = AppSettings?.CameraPlaybackHz ?? MouseTrajectoryPlayback.DefaultHz,
            GainX = AppSettings?.CameraLookGainX ?? MouseTrajectoryStep.DefaultLookGainX,
            GainY = AppSettings?.CameraLookGainY ?? MouseTrajectoryStep.DefaultLookGainY,
            CameraMode = AppSettings?.CameraPlaybackMode ?? TrajectoryPlaybackMode.Reconstructed,
            SamplingMode = MouseSamplingMode, MinDistancePx = MouseMinDistancePx, MinTimeMs = MouseMinTimeMs,
            UseRawMouseInput = AppSettings?.UseRawMouseRecording ?? false };
        // Pin the external identity before any window/overlay transition or asynchronous license check.
        session.Target = targetPin is null ? ResolveProducerTarget(script.Target) : targetPin.Target;
        _ownedRecording = session; _recordingTerminalReason = string.Empty;
        return session;
    }
    private void AttachAuthorizedRecordingDestination(OwnedRecording session)
    {
        if (SelectedScript != null) return;
        MacroScripts.Add(session.Destination);
        _selectedScript = session.Destination;
        session.Destination.IsSelected = true;
        _macroName = session.Destination.Name; _description = session.Destination.Description;
        _currentHotkey = session.Destination.Hotkey; Steps.ReplaceAll(session.Baseline.Steps);
        ApplyPlaybackSettingsFrom(session.Destination);
        SelectedStep = null;
        OnPropertyChanged(nameof(SelectedScript)); OnPropertyChanged(nameof(MacroName));
        OnPropertyChanged(nameof(Description)); OnPropertyChanged(nameof(CurrentHotkey));
    }
    private RecordedInput? AdmitRecordingInput(OwnedRecording session, InputEvent input)
    {
        lock (session.AdmissionSync) return AdmitRecordingInputLocked(session, input);
    }
    private RecordedInput? AdmitRecordingInputLocked(OwnedRecording session, InputEvent input)
    {
        if (!ReferenceEquals(_ownedRecording, session) || Volatile.Read(ref session.Closed) != 0
            || session.InputLease is { CanContinue: false }) return null;
        bool include = true;
        for (int i = 0; i < session.Filters.Length; i++)
        {
            bool pass = session.Filters[i].ShouldRecord(input, i != 5 || IsMiniToolbarVisible);
            if (!pass && input.Type == InputEventType.KeyDown && (i == 1 || i == 4))
            { if (i == 4) Interlocked.Exchange(ref session.Emergency, 1); Interlocked.Exchange(ref session.Closed, 1); session.StopSignal.TrySetResult(); }
            include &= pass;
        }
        if (!include) return null;
        var desktop = session.Desktop?.Current;
        if (!session.ButtonsInitialized) { session.CapturedButtons = desktop?.HeldButtons ?? 0; session.ButtonsInitialized = true; }
        if (input.Type == InputEventType.MouseDown) session.CapturedButtons |= 1 << input.Data;
        if (input.Type == InputEventType.MouseUp) session.CapturedButtons &= ~(1 << input.Data);
        bool own = desktop?.OwnForeground == true;
        if (input.Type is InputEventType.MouseMove or InputEventType.MouseDown or InputEventType.MouseUp or InputEventType.MouseWheel or InputEventType.MouseHWheel)
            own |= desktop?.OwnBounds.Any(rect => rect.Contains(input.X, input.Y)) == true;
        var target = session.Target;
        var foreground = _targets?.Snapshot.Foreground;
        bool strict = target?.Rule.Mode is not (null or MacroTargetMode.Screen);
        if (strict && (foreground is null || target!.Window is null || !PlaybackSessionContext.SameIdentity(target.Window, foreground) || foreground.IsOwnWindow))
            Interlocked.Exchange(ref session.Paused, 1);
        long stamp = Math.Max(input.TimestampTicks, session.LastTick);
        if (own || Volatile.Read(ref session.Paused) != 0)
        { if (session.ExclusionStart == 0) { session.ExclusionStart = stamp; session.Epoch++; } return null; }
        if (session.ExclusionStart != 0) { session.ExcludedTicks += Math.Max(0, stamp - session.ExclusionStart); session.ExclusionStart = 0; }
        session.LastTick = stamp;
        var bounds = strict ? foreground!.ClientBounds : default;
        bool absolute = input.Type is InputEventType.MouseMove or InputEventType.MouseDown or InputEventType.MouseUp;
        var ev = new InputEvent(input.Type, stamp - session.ExcludedTicks,
            strict && absolute ? input.X - bounds.X : input.X, strict && absolute ? input.Y - bounds.Y : input.Y, input.Data, input.ExtraInfo);
        return new(ev, Epoch: session.Epoch, HeldButtons: session.CapturedButtons,
            PointerConstrained: desktop?.PointerConstrained ?? false, IsClientRelative: strict && absolute,
            ClientWidth: bounds.Width, ClientHeight: bounds.Height);
    }
    private async Task ObserveRecordingStopSignalAsync(OwnedRecording session)
    {
        await session.StopSignal.Task.ConfigureAwait(false);
        Task stop;
        lock (_recordingLifecycleGate)
        {
            if (!ReferenceEquals(_ownedRecording, session)) return;
            if (Volatile.Read(ref session.Emergency) != 0) session.InputLease?.SignalStop();
            stop = StopRecordingCoreAsync(); // Do not join the startup task that joins this watcher.
        }
        await stop.ConfigureAwait(false);
    }
    private void CloseRecordingAdmission()
    {
        var session = _ownedRecording;
        if (session != null) { Interlocked.Exchange(ref session.Closed, 1); session.StopSignal.TrySetResult(); }
        _recordingInputBuffer?.TryComplete();
    }
    /// <summary>Called directly from the hotkey owner. No UI, saving, waiting or native joins.</summary>
    public void SignalEmergencyStop()
    {
        InputOperations.SignalStop();
        var session = _ownedRecording;
        if (session != null)
        {
            Interlocked.Exchange(ref session.Emergency, 1);
            Interlocked.Exchange(ref session.Closed, 1);
            session.StopSignal.TrySetResult();
        }
        // The owned watcher closes/drains the channel and cancels startup off the hook.
        // Cancellation callbacks and buffered-modifier flushing must never run here.
        _activeController?.SignalPanic();
    }
    public async Task PauseForEditorFocusAsync()
    {
        // Playback cleanup disposes and then retracts these off the UI thread, so read them once.
        MacroController? controller; PlaybackSessionContext? session;
        lock (_playbackSync) { controller = _activeController; session = _playbackSession; }
        if (controller is not null && IsRunning)
        {
            bool retiring = false;
            // A controller is disposed only while its run retires (cleanup or editor shutdown); that run owns IsPaused.
            try { await controller.PauseAsync(); }
            catch (ObjectDisposedException) { retiring = true; }
            if (!retiring)
            {
                if (session is not null) await session.AwaitReleasedAsync();
                bool current; lock (_playbackSync) current = ReferenceEquals(_activeController, controller);
                if (current && IsRunning) IsPaused = true;
            }
        }
        if (IsRecording && _ownedRecording is { } recording && Volatile.Read(ref recording.Closed) == 0 && recording.Target?.Rule.Mode is { } mode && mode != MacroTargetMode.Screen)
        {
            lock (recording.AdmissionSync)
            {
                Interlocked.Exchange(ref recording.Paused, 1);
                if (recording.ExclusionStart == 0) { recording.ExclusionStart = Stopwatch.GetTimestamp(); recording.Epoch++; }
            }
            SetRecordingPhase("Paused");
        }
    }
    private void ToggleRecordingPause()
    {
        var session = _ownedRecording;
        if (session == null || !IsRecording || Volatile.Read(ref session.Closed) != 0) return;
        if (Volatile.Read(ref session.Paused) != 0)
        {
            if (session.RecoveryFailed) { NotifyStatus("Recovery storage failed. Stop to save the retained actions before recording again."); return; }
            if (session.Target?.Rule.Mode is not (null or MacroTargetMode.Screen) && !Targets.Inspect(session.Target!).AllowsInput)
            { NotifyStatus("Focus the selected window, then resume recording manually."); return; }
            long now = Stopwatch.GetTimestamp();
            lock (session.AdmissionSync)
            {
                if (session.ExclusionStart != 0) { session.ExcludedTicks += Math.Max(0, now - session.ExclusionStart); session.ExclusionStart = 0; }
                Interlocked.Exchange(ref session.Paused, 0);
            }
            SetRecordingPhase("Recording");
        }
        else
        {
            lock (session.AdmissionSync)
            {
                Interlocked.Exchange(ref session.Paused, 1);
                session.ExclusionStart = Stopwatch.GetTimestamp(); session.Epoch++;
            }
            SetRecordingPhase("Paused");
        }
    }
    private async Task JournalRecordingInputAsync(OwnedRecording session, RecordedInput packet)
    {
        if (session.Journal == null || session.RecoveryFailed) return;
        var ev = packet.Event;
        var durable = new RecordingRecoveryPacket(packet.Sequence,
            session.AppendOffset.Ticks + Math.Max(0, Stopwatch.GetElapsedTime(session.StartTick, ev.TimestampTicks).Ticks),
            ev.Type, ev.X, ev.Y, ev.Data, ev.ExtraInfo, packet.Epoch, packet.IsClientRelative, packet.ClientWidth, packet.ClientHeight);
        try
        {
            if (session.Journal.TryAppend(durable)) return;
            Interlocked.Exchange(ref session.Paused, 1); SetRecordingPhase("Paused");
            await session.Journal.CheckpointAsync().ConfigureAwait(false);
            if (!session.Journal.TryAppend(durable)) throw new System.IO.IOException("The recovery queue remains full.");
        }
        catch (Exception ex)
        {
            session.RecoveryFailed = true; Interlocked.Exchange(ref session.Paused, 1);
            _recordingFailure = "Recovery saving failed; capture is paused. Stop to save the retained actions. " + ex.Message;
            SetRecordingPhase("Paused");
            if (!TryPostToUi(() => NotifyStatus(_recordingFailure))) NotifyStatus(_recordingFailure);
        }
    }
}
