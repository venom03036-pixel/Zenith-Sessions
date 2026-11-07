using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Infrastructure.Input;

namespace MacroSuite.App.ViewModels;

public sealed partial class MacroEditorViewModel
{
    public HostInputOperationArbiter InputOperations { get; }
    private readonly bool _ownsInputOperations;
    private OwnedEditorPlayback? _ownedEditorPlayback;
    private HostInputOperationLease? _recordingInputOperation;
    private Task? _recordingInputStartup;
    private Task? _recordingOwnershipStop;
    private bool _committingOwnedInputDraft;

    private sealed class OwnedEditorPlayback(HostInputOperationLease lease)
    {
        public HostInputOperationLease Lease { get; } = lease;
        public Task? Work;
        public Task? Cleanup;
        public MacroController? Controller;
        public PlaybackSessionContext? Session;
        public IDisposable? SessionAttachment, NativeAttachment;
        public CancellationTokenRegistration StopRegistration;
        public bool CleanupSucceeded = true;
        public bool RetirementFailed;
    }

    private bool InputOwnershipBlocksEditing => InputOperations.Snapshot.IsQuarantined || InputOperations.Snapshot.IsRecovering
        || (!_committingOwnedInputDraft && (_ownedEditorPlayback is not null || _recordingInputOperation is not null));

    private bool CommitOwnedInputDraft()
    {
        _committingOwnedInputDraft = true;
        try { return TryCommitPendingActionEdits(); }
        finally { _committingOwnedInputDraft = false; }
    }

    private void RefreshInputOwnershipPresentation()
    {
        RaiseTransportPresentation();
        RunCommand?.RaiseCanExecuteChanged(); RecordCommand?.RaiseCanExecuteChanged(); StopCommand?.RaiseCanExecuteChanged();
    }

    private async Task RunEditorPlaybackOwnedAsync(OwnedEditorPlayback owned, MacroScriptItemViewModel? script,
        ProducerTargetPin? targetPin)
    {
        // Run/task/lease identity is published before license, property or overlay callbacks.
        await Task.Yield();
        try
        {
            if (!owned.Lease.CanContinue || _disposed || _shutdownStarted || !EnsureEmergencyStopReady()) return;
            ResolvedMacroTarget? target = null;
            try
            {
                if (targetPin is not null && !Equals(script?.Target, targetPin.RequestedRule)) return;
                target = targetPin is null ? ResolveProducerTarget(script?.Target) : targetPin.Target;
            }
            catch (Exception error) { NotifyStatus("Could not start the selected target: " + error.Message); return; }
            await RunMacroCoreAsync(script, target, targetPin, owned);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_disposed) NotifyStatus("Playback ended: " + error.Message);
            System.Diagnostics.Trace.TraceError("Owned playback ended: {0}", error);
        }
        finally
        {
            await CleanupEditorPlaybackAsync(owned);
            try
            {
                if (ReferenceEquals(_ownedEditorPlayback, owned))
                {
                    if (!_disposed)
                    {
                        IsRunning = false; IsPaused = false;
                        foreach (var row in MacroScripts) row.IsRunning = false;
                    }
                    if (owned.Lease.OwnsCleanup && InputModeGate.Current is InputMode.MacroExecuting or InputMode.PanicStopping)
                        InputModeGate.Set(InputMode.Normal);
                }
            }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Owned playback presentation failed: {0}", error); }
            try { await owned.Lease.CompleteAsync(owned.CleanupSucceeded); }
            catch (Exception error)
            {
                owned.CleanupSucceeded = false;
                owned.RetirementFailed = true;
                System.Diagnostics.Trace.TraceError("Owned playback retirement failed: {0}", error);
            }
            if (owned.CleanupSucceeded && ReferenceEquals(_ownedEditorPlayback, owned)) _ownedEditorPlayback = null;
            try
            {
                if (!_disposed)
                {
                    if (!owned.CleanupSucceeded) NotifyStatus("Input cleanup needs recovery. New input operations remain blocked.");
                    RefreshInputOwnershipPresentation();
                }
            }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Owned playback final presentation failed: {0}", error); }
        }
    }

    private Task CleanupEditorPlaybackAsync(OwnedEditorPlayback owned)
    {
        lock (_playbackSync) return owned.Cleanup ??= CleanupEditorPlaybackCoreAsync(owned);
    }

    private async Task CleanupEditorPlaybackCoreAsync(OwnedEditorPlayback owned)
    {
        await Task.Yield();
        void Failed(Exception error)
        {
            owned.CleanupSucceeded = false;
            System.Diagnostics.Trace.TraceError("Owned playback cleanup failed: {0}", error);
        }
        try { await owned.StopRegistration.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { Failed(error); }
        if (owned.Controller is { } controller)
        {
            try { await controller.StopAsync().ConfigureAwait(false); } catch (Exception error) { Failed(error); }
            try { await controller.WaitForStopSignalAsync().ConfigureAwait(false); } catch (Exception error) { Failed(error); }
            try { await controller.WaitForPanicReleaseAsync().ConfigureAwait(false); } catch (Exception error) { Failed(error); }
        }
        if (owned.Session is { } session)
        {
            session.TargetPaused -= PlaybackTargetPaused;
            try { await session.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { Failed(error); }
        }
        if (owned.Controller is not null || owned.Session is not null || owned.NativeAttachment is not null)
        {
            try { await Task.Run(() => ReleaseEditorOwnedInputs(owned.Lease)).ConfigureAwait(false); }
            catch (Exception error) { Failed(error); }
        }
        try { owned.SessionAttachment?.Dispose(); } catch (Exception error) { Failed(error); }
        try { owned.Controller?.Dispose(); } catch (Exception error) { Failed(error); }
        try { owned.NativeAttachment?.Dispose(); } catch (Exception error) { Failed(error); }
        lock (_playbackSync)
        {
            if (ReferenceEquals(_activeController, owned.Controller)) _activeController = null;
            if (ReferenceEquals(_playbackSession, owned.Session)) _playbackSession = null;
        }
    }

    private void ReleaseEditorOwnedInputs(HostInputOperationLease lease)
    {
        if (!InputOperations.Owns(lease) || !lease.OwnsCleanup)
            throw new InvalidOperationException("The editor no longer owns this input cleanup.");
        if (_inputPlayer is IHostInputOperationAwarePlayer aware) aware.ReleaseHostOperationInputs(lease);
        else _inputPlayer.ReleaseAllHeldKeys();
    }

    private async Task StartEditorRecordingOwnedAsync(HostInputOperationLease lease, ProducerTargetPin? targetPin,
        MacroScriptItemViewModel? pinnedDocument)
    {
        await Task.Yield();
        try
        {
            if (lease.CanContinue) await StartRecordingCoreAsync(lease, targetPin, pinnedDocument);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_ownedRecording?.InputLease != lease || !IsRecording)
                await FinishRecordingInputOwnershipAsync(lease);
            if (!_disposed) RefreshInputOwnershipPresentation();
        }
    }

    private async Task FinishRecordingInputOwnershipAsync(HostInputOperationLease? lease = null)
    {
        lease ??= _ownedRecording?.InputLease ?? _recordingInputOperation;
        if (lease is null || !InputOperations.Owns(lease) || lease.Completion.IsCompleted) return;
        // A timed-out actual producer or consumer still owns its resources. A later Stop/Shutdown joins it.
        if (_recordingCaptureStopTask is { IsCompleted: false } || _recordingConsumerTask is { IsCompleted: false }) return;
        bool captureClean = _recordingCaptureStopTask is null || _recordingCaptureStopTask.IsCompletedSuccessfully;
        bool consumerClean = _recordingConsumerTask is null || _recordingConsumerTask.IsCompletedSuccessfully;
        var recording = _ownedRecording;
        bool finalized = recording is null || !ReferenceEquals(recording.InputLease, lease) || Volatile.Read(ref recording.NeedsFinalization) == 0;
        bool clean = captureClean && consumerClean && finalized && !_inputRecorder.IsRecording;
        try { if (ReferenceEquals(recording?.InputLease, lease)) await recording!.InputStop.DisposeAsync(); }
        catch (Exception error)
        {
            clean = false;
            if (ReferenceEquals(recording?.InputLease, lease)) recording!.InputRetirementFailed = true;
            System.Diagnostics.Trace.TraceError("Owned recording stop registration failed: {0}", error);
        }
        if (lease.OwnsCleanup && InputModeGate.Current is InputMode.Recording or InputMode.PanicStopping)
            InputModeGate.Set(InputMode.Normal);
        try { await lease.CompleteAsync(clean); }
        catch (Exception error)
        {
            clean = false;
            if (ReferenceEquals(recording?.InputLease, lease)) recording!.InputRetirementFailed = true;
            System.Diagnostics.Trace.TraceError("Owned recording retirement failed: {0}", error);
        }
        if (clean && ReferenceEquals(_recordingInputOperation, lease)) _recordingInputOperation = null;
        if (!clean)
        {
            _recordingFailure ??= "Input cleanup needs recovery. New input operations remain blocked.";
            _recordingTerminalReason = _recordingFailure;
            SetRecordingPhase("Faulted");
            if (!_disposed) NotifyStatus(_recordingFailure);
        }
        if (!_disposed) RefreshInputOwnershipPresentation();
    }

    public Task StopRecordingAsync()
    {
        if (_producerAuthoringRequest is { Kind: HostInputOperationKind.Recording } authoring)
        {
            authoring.Owner.Lease.SignalStop();
            return authoring.Work;
        }
        if (_recordingInputOperation is { Completion.IsCompleted: true }) return RecoverInputOwnershipAsync();
        lock (_recordingLifecycleGate)
            return _recordingOwnershipStop is { IsCompleted: false } ? _recordingOwnershipStop
                : _recordingOwnershipStop = StopRecordingOwnedAsync(_recordingInputStartup);
    }

    private async Task StopRecordingOwnedAsync(Task? startup)
    {
        await Task.Yield();
        Exception? failure = null;
        async Task Join(Task? task)
        {
            if (task is null) return;
            try { await task; }
            catch (Exception error) { failure ??= error; }
        }
        await Join(StopRecordingCoreAsync());
        await Join(startup);
        // The presentation deadline does not retire a still-running native producer.
        // Keep the public stop owner until actual capture and accepted-tail work end.
        await Join(_recordingCaptureStopTask);
        await Join(_recordingConsumerTask);
        await FinishRecordingInputOwnershipAsync();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Explicit recovery of this editor's retired owner only; never clears another route's quarantine.</summary>
    public async Task<bool> RecoverInputOwnershipAsync(CancellationToken ct = default)
    {
        var snapshot = InputOperations.Snapshot;
        var playback = _ownedEditorPlayback;
        var recording = _ownedRecording;
        var lease = playback is not null && playback.Lease.OperationId == snapshot.OperationId ? playback.Lease
            : recording?.InputLease is { } recordingLease && recordingLease.OperationId == snapshot.OperationId ? recordingLease : null;
        if (!snapshot.IsQuarantined || lease is null || !lease.Completion.IsCompleted) return false;
        // Retirement can precede the old method's final presentation tail. Join only captured
        // tasks, then recheck the exact incarnation; never drain a replacement operation.
        async Task JoinTail(Task? task)
        {
            if (task is null) return;
            try { await task.WaitAsync(ct); }
            catch (Exception error) when (task.IsFaulted && !ct.IsCancellationRequested)
            { System.Diagnostics.Trace.TraceError("Owned input recovery joined a failed tail: {0}", error); }
        }
        if (lease.Kind == HostInputOperationKind.Playback)
        {
            await JoinTail(playback!.Work);
            await JoinTail(playback.Cleanup);
        }
        else if (lease.Kind == HostInputOperationKind.Recording)
        {
            Task? startup = _recordingInputStartup, stop = _recordingOwnershipStop,
                finalization = _recordingStopTask, capture = _recordingCaptureStopTask, consumer = _recordingConsumerTask;
            await JoinTail(startup); await JoinTail(stop); await JoinTail(finalization);
            await JoinTail(capture); await JoinTail(consumer);
        }
        var current = InputOperations.Snapshot;
        if (!InputOperations.Owns(lease) || current.OperationId != snapshot.OperationId
            || current.StopGeneration != snapshot.StopGeneration || !current.IsQuarantined) return false;
        bool recovered = await InputOperations.TryRecoverAsync(async token =>
        {
            token.ThrowIfCancellationRequested();
            if (!InputOperations.Owns(lease)) throw new InvalidOperationException("The editor cleanup owner changed.");
            if (lease.Kind == HostInputOperationKind.Playback)
            {
                if (playback?.Work is { IsCompleted: false } || playback?.Cleanup is { IsCompleted: false })
                    throw new InvalidOperationException("Playback cleanup is still running.");
                if (playback!.RetirementFailed) throw new HostInputOperationCleanupException();
                playback!.CleanupSucceeded = true;
                await CleanupEditorPlaybackCoreAsync(playback).ConfigureAwait(false);
                if (!playback.CleanupSucceeded) throw new HostInputOperationCleanupException();
            }
            else if (lease.Kind == HostInputOperationKind.Recording && ReferenceEquals(recording, _ownedRecording))
            {
                if (recording!.InputRetirementFailed) throw new HostInputOperationCleanupException();
                if (_recordingInputStartup is { IsCompleted: false } || _recordingCaptureStopTask is { IsCompleted: false }
                    || _recordingConsumerTask is { IsCompleted: false }) throw new InvalidOperationException("Recording cleanup is still running.");
                if (_uiDispatcher is not null && !_uiDispatcher.CheckAccess())
                    await (await _uiDispatcher.InvokeAsync(FinishStopRecordingAsync));
                else await FinishStopRecordingAsync();
                if (_inputRecorder.IsRecording || _recordingCaptureStopTask is { IsCompletedSuccessfully: false }
                    || _recordingConsumerTask is { IsCompletedSuccessfully: false })
                    throw new InvalidOperationException("Recording capture cleanup remains uncertain.");
            }
            else throw new InvalidOperationException("This operation belongs to another editor route.");
        }, ct);
        if (recovered)
        {
            if (ReferenceEquals(_ownedEditorPlayback, playback)) _ownedEditorPlayback = null;
            if (ReferenceEquals(_recordingInputOperation, lease)) _recordingInputOperation = null;
            if (!_disposed) RefreshInputOwnershipPresentation();
        }
        return recovered;
    }
}
