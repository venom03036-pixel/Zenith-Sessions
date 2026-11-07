using System.Drawing;
using System.Windows.Threading;
using MacroSuite.App.Authoring;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Input;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Vision;

namespace MacroSuite.App.ViewModels;

public sealed partial class MacroEditorViewModel
{
    public Func<HostInputOperationLease, CancellationToken, Task<ScreenPoint?>>? OwnedLocationPickPrompter { get; set; }
    private HostInputOperationLease? _editorPickerLease;
    private Task<ScreenPoint?>? _editorPickerTask;
    private CancellationTokenSource? _editorPickerCancellation;
    private Task? _editorInteractionCompletion;
    private HostInputOperationLease? _editorInteractionLease;
    private Task<MatchResult>? _editorCaptureTask;
    private HostInputOperationLease? _editorCaptureLease;
    private Task<CaptureFrame?>? _editorSnipTask;
    private HostInputOperationLease? _editorSnipLease;
    private AuthoringInteraction? _authoringInteraction;

    internal sealed class AuthoringInteraction(HostInputOperationLease lease)
    {
        internal HostInputOperationLease Lease { get; } = lease;
        internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? Work;
        internal Task? Observation;
        internal InputMode PreviousMode;
        internal bool RetiredForPublication;
    }

    internal bool IsAuthoringInteractionActive => _authoringInteraction is not null;
    internal bool OwnsAuthoringDraft(AuthoringInteraction? owner) => owner is not null
        && ReferenceEquals(_authoringInteraction, owner) && ValidInteractiveParent(owner.Lease)
        && !_disposed && !_shutdownStarted && HasActivation && !LibraryKeyUnavailable && !IsRunning && !IsRecordingBusy;

    private bool ValidInteractiveParent(HostInputOperationLease parent)
        => InputOperations.Owns(parent) && parent.CanContinue
            && parent.Kind is HostInputOperationKind.Picker or HostInputOperationKind.CaptureTest;

    private T RunOwnedInteraction<T>(Func<T> interaction, T cancelled, HostInputOperationKind kind = HostInputOperationKind.Picker)
        => RunOwnedInteraction(_ => interaction(), cancelled, kind);

    private T RunOwnedInteraction<T>(Func<AuthoringInteraction, T> interaction, T cancelled, HostInputOperationKind kind,
        bool lockDraft = false, long? expectedGeneration = null)
    {
        var owner = BeginEditorInteraction(kind, lockDraft, expectedGeneration);
        return owner is null ? cancelled : RunEditorInteraction(owner, interaction, cancelled, lockDraft);
    }

    private AuthoringInteraction? BeginEditorInteraction(HostInputOperationKind kind, bool lockDraft, long? expectedGeneration)
    {
        if (_disposed || _shutdownStarted) return null;
        if (_authoringInteraction is not null)
        { NotifyStatus("Finish the current authoring interaction first."); return null; }
        if (!InputOperations.TryAcquire(kind, expectedGeneration ?? InputOperations.StopGeneration, out var lease, out string reason))
        { NotifyStatus(reason); return null; }
        var owner = new AuthoringInteraction(lease!);
        _editorInteractionLease = owner.Lease;
        _editorInteractionCompletion = owner.Completion.Task;
        if (lockDraft) _authoringInteraction = owner;
        return owner;
    }

    private T RunEditorInteraction<T>(AuthoringInteraction owner, Func<AuthoringInteraction, T> interaction, T cancelled, bool lockDraft)
    {
        bool clean = true;
        InputMode previous = InputModeGate.Current;
        owner.PreviousMode = previous;
        try
        {
            if (!owner.Lease.CanContinue) return cancelled;
            InputModeGate.Set(InputMode.SelectingRegion);
            if (lockDraft) RefreshInputOwnershipPresentation();
            T result = interaction(owner);
            return CanPublishAuthoringResult(owner) ? result : cancelled;
        }
        catch (OperationCanceledException) { return cancelled; }
        catch (HostInputOperationCleanupException) { clean = false; throw; }
        finally
        {
            if (owner.Work is not { IsCompleted: false }) EndInteractionPresentation(owner, previous);
            Task finish = FinishOwnedInteractionAsync(owner, clean, previous);
            if (ReferenceEquals(_editorInteractionLease, owner.Lease)) _editorInteractionCompletion = finish;
        }
    }

    private void EndInteractionPresentation(AuthoringInteraction owner, InputMode previous)
    {
        if (owner.Lease.OwnsCleanup) InputModeGate.Set(owner.Lease.CanContinue ? previous : InputMode.Normal);
        if (ReferenceEquals(_authoringInteraction, owner)) _authoringInteraction = null;
    }

    private async Task FinishOwnedInteractionAsync(AuthoringInteraction owner, bool clean, InputMode previous)
    {
        await Task.Yield();
        try
        {
            if (owner.Work is { } work)
            {
                try { await work; }
                catch (HostInputOperationCleanupException) { clean = false; }
                catch (Exception) { /* The modal result already reports the error, or its publication was stopped. */ }
            }
            if (owner.Observation is { } observation) await observation;
        }
        catch (Exception error)
        {
            clean = false;
            System.Diagnostics.Trace.TraceError("Authoring completion observation failed: {0}", error);
        }
        finally
        {
            EndInteractionPresentation(owner, previous);
            try { await owner.Lease.CompleteAsync(clean); }
            catch (Exception error)
            {
                clean = false;
                System.Diagnostics.Trace.TraceError("Authoring retirement failed: {0}", error);
            }
            if (ReferenceEquals(_editorInteractionLease, owner.Lease)) _editorInteractionLease = null;
            try
            {
                if (!_disposed && !_shutdownStarted)
                {
                    if (!clean) NotifyStatus("Authoring cleanup is incomplete. New input activities remain blocked.");
                    RefreshInputOwnershipPresentation();
                }
            }
            finally { owner.Completion.TrySetResult(clean); }
        }
    }

    private bool CanPublishAuthoringResult(AuthoringInteraction owner)
        => !_disposed && !_shutdownStarted && (owner.RetiredForPublication
            ? !owner.Lease.StopToken.IsCancellationRequested && owner.Lease.Generation == InputOperations.StopGeneration
                && !InputOperations.Snapshot.IsBusy
            : owner.Lease.CanContinue);

    private T RunAuthoringWork<T>(AuthoringInteraction owner, Func<Task<T>> prepare, bool retireBeforeDelivery = true)
    {
        if (owner.Work is not null) throw new InvalidOperationException("This authoring interaction already owns preparation.");
        var dispatcher = Dispatcher.CurrentDispatcher;
        Task<T> actual = BeginAsync();
        owner.Work = actual;
        try
        {
            if (ModalPreparationBridge.TryComplete(dispatcher, actual, out T result, out owner.Observation)) return result;
        }
        catch
        {
            if (!actual.IsCompleted) owner.Lease.SignalStop();
            throw;
        }
        owner.Lease.SignalStop();
        throw new OperationCanceledException(owner.Lease.StopToken);

        async Task<T> BeginAsync()
        {
            await Dispatcher.Yield(DispatcherPriority.Normal);
            owner.Lease.StopToken.ThrowIfCancellationRequested();
            if (!retireBeforeDelivery) return await prepare();

            bool clean = true;
            T value;
            try { value = await prepare(); }
            catch (HostInputOperationCleanupException) { clean = false; throw; }
            finally
            {
                if (owner.Lease.OwnsCleanup)
                    InputModeGate.Set(owner.Lease.CanContinue ? owner.PreviousMode : InputMode.Normal);
                try { await owner.Lease.CompleteAsync(clean); }
                catch (Exception error)
                {
                    System.Diagnostics.Trace.TraceError("Authoring retirement failed: {0}", error);
                    throw new HostInputOperationCleanupException();
                }
            }
            owner.RetiredForPublication = true;
            return value;
        }
    }

    public Rectangle? PromptOwnedRegion() => RunOwnedInteraction(() => RegionPrompter.Invoke(), (Rectangle?)null);
    public Rectangle? PromptOwnedImageArea(Rectangle? area, Size size)
        => RunOwnedInteraction(() => ImageAreaPlacementPrompter.Invoke(area, size), (Rectangle?)null);
    public void PreviewOwnedImageArea(Rectangle? area, bool fullScreen, string label)
        => RunOwnedInteraction(() => { ImageSearchAreaPreviewer.Invoke(area, fullScreen, label); return true; }, false);

    public Task<ScreenPoint?> PickScreenPointAsync()
    {
        if (!InputOperations.TryAcquire(HostInputOperationKind.Picker, out var lease, out string reason))
        { NotifyStatus(reason); return Task.FromResult<ScreenPoint?>(null); }
        _editorPickerLease = lease;
        return _editorPickerTask = PickOwnedPointAsync(lease!, ownsRoot: true, default);
    }

    public Task<ScreenPoint?> PickScreenPointAsync(HostInputOperationLease parent, CancellationToken ct = default)
    {
        if (!ValidInteractiveParent(parent) || _editorPickerTask is { IsCompleted: false })
            return Task.FromResult<ScreenPoint?>(null);
        _editorPickerLease = parent;
        return _editorPickerTask = PickOwnedPointAsync(parent, ownsRoot: false, ct);
    }

    private async Task<ScreenPoint?> PickOwnedPointAsync(HostInputOperationLease parent, bool ownsRoot, CancellationToken caller)
    {
        await Task.Yield();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(parent.StopToken, caller);
        _editorPickerCancellation = stop;
        using var cancellation = stop.Token.Register(() =>
        {
            if (_locationPicker is IHostInputOperationBoundPicker bound) bound.Cancel(parent);
            else if (InputOperations.Owns(parent)) _locationPicker?.Cancel();
        });
        bool clean = false;
        InputMode previous = InputModeGate.Current;
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            if (!ValidInteractiveParent(parent)) return null;
            InputModeGate.Set(InputMode.PickingLocation);
            ScreenPoint? result = OwnedLocationPickPrompter is { } owned ? await owned(parent, stop.Token)
                : LocationPickPrompter is { } compatibility ? await compatibility()
                : _locationPicker is IHostInputOperationBoundPicker bound ? await bound.ArmAndWaitAsync(parent, stop.Token)
                : _locationPicker is not null ? await _locationPicker.ArmAndWaitAsync(stop.Token) : null;
            clean = true;
            return parent.CanContinue && !stop.IsCancellationRequested ? result : null;
        }
        catch (OperationCanceledException) { clean = _locationPicker?.IsArmed != true; return null; }
        finally
        {
            if (parent.OwnsCleanup) InputModeGate.Set(parent.CanContinue ? previous : InputMode.Normal);
            if (ReferenceEquals(_editorPickerCancellation, stop)) _editorPickerCancellation = null;
            if (ownsRoot) await parent.CompleteAsync(clean);
            if (ReferenceEquals(_editorPickerLease, parent)) _editorPickerLease = null;
        }
    }

    public void CancelLocationPick()
    {
        var parent = _editorPickerLease;
        if (parent is null || !InputOperations.Owns(parent)) return;
        _editorPickerCancellation?.Cancel();
        if (_locationPicker is IHostInputOperationBoundPicker bound) bound.Cancel(parent);
        else _locationPicker?.Cancel();
    }

    public Task<MatchResult> ProbeImageAsync(ImageCondition image, bool fullScreenDefault, CancellationToken ct)
    {
        if (!InputOperations.TryAcquire(HostInputOperationKind.CaptureTest, out var lease, out string reason))
            throw new InvalidOperationException(reason);
        _editorCaptureLease = lease;
        return _editorCaptureTask = ProbeOwnedImageAsync(image, fullScreenDefault, lease!, ct);
    }

    private async Task<MatchResult> ProbeOwnedImageAsync(ImageCondition image, bool fullScreenDefault,
        HostInputOperationLease lease, CancellationToken caller)
    {
        await Task.Yield();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lease.StopToken, caller);
        bool clean = true;
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            MatchResult result = await ProbeImageCoreAsync(image, fullScreenDefault, stop.Token, value => clean = value);
            return result;
        }
        finally
        {
            await lease.CompleteAsync(clean);
            if (ReferenceEquals(_editorCaptureLease, lease)) _editorCaptureLease = null;
        }
    }

    public Task<CaptureFrame?> CaptureOwnedScreenSnipAsync(CancellationToken ct = default)
    {
        if (!InputOperations.TryAcquire(HostInputOperationKind.CaptureTest, out var lease, out string reason))
        { NotifyStatus(reason); return Task.FromResult<CaptureFrame?>(null); }
        _editorSnipLease = lease;
        return _editorSnipTask = CaptureOwnedScreenSnipCoreAsync(lease!, ct);
    }

    private async Task<CaptureFrame?> CaptureOwnedScreenSnipCoreAsync(HostInputOperationLease lease, CancellationToken caller)
    {
        await Task.Yield();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lease.StopToken, caller);
        CancellationToken token = stop.Token;
        bool clean = true;
        InputMode previous = InputModeGate.Current;
        CaptureFrame? frame = null;
        IDisposable? hidden = null;
        var privacyDispatcher = System.Windows.Application.Current?.Dispatcher;
        Exception? failure = null, cleanupFailure = null;
        try
        {
            try
            {
                token.ThrowIfCancellationRequested();
                InputModeGate.Set(InputMode.CapturingImage);
                clean = false;
                hidden = privacyDispatcher is not null && !privacyDispatcher.CheckAccess()
                    ? await privacyDispatcher.InvokeAsync(() => MacroSuite.App.Overlays.ScreenCapturePrivacy.HideEngine())
                    : MacroSuite.App.Overlays.ScreenCapturePrivacy.HideEngine();
                clean = true;
                // HideEngine may pump the dispatcher. Stop must win before opening a picker.
                token.ThrowIfCancellationRequested();
                if (!lease.CanContinue) throw new OperationCanceledException(token);
                clean = false;
                Rectangle? area = PickAuthoringRegion(token); // Explicitly inside this root; never reacquire a child owner.
                clean = true;
                if (area is { Width: >= 4, Height: >= 4 } region && lease.CanContinue)
                    frame = await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        return _captureService.CaptureRegionAsync(region, CancellationToken.None);
                    }, CancellationToken.None);
            }
            catch (Exception error)
            {
                failure = error;
                // Synchronous modal exits and privacy setup rollback have ended.
                // Explicit uncertainty and a later restoration error retain quarantine.
                clean = error is not HostInputOperationCleanupException;
            }
            finally
            {
                try
                {
                    if (hidden is not null && privacyDispatcher is not null && !privacyDispatcher.CheckAccess())
                        await privacyDispatcher.InvokeAsync(hidden.Dispose);
                    else hidden?.Dispose();
                }
                catch (Exception error) { clean = false; cleanupFailure = error; }
                if (lease.OwnsCleanup) InputModeGate.Set(lease.CanContinue ? previous : InputMode.Normal);
                try { await lease.CompleteAsync(clean); }
                catch (Exception error) { clean = false; cleanupFailure ??= error; }
                if (clean && ReferenceEquals(_editorSnipLease, lease)) _editorSnipLease = null;
            }
            if (cleanupFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            if (failure is not null && failure is not OperationCanceledException)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            if (failure is OperationCanceledException || token.IsCancellationRequested || caller.IsCancellationRequested
                || lease.Arbiter.StopGeneration != lease.Generation || _disposed || _shutdownStarted) return null;
            // Transfer only after privacy, mode and actual retirement succeeded. Until this
            // point every exit (including a callback failure) disposes the held frame below.
            CaptureFrame? result = frame; frame = null;
            return result;
        }
        finally { frame?.Dispose(); }
    }
}
