using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using MacroSuite.Core.Licensing;

namespace Zenith.MacroHost.Execution;

public enum ForegroundPlaybackStatus
{
    InvalidRequest, Busy, Stopped, PreparationRejected, TargetUnavailable, InputHeld, LicenseRequired,
    Completed, Faulted, CleanupFailed
}

public sealed record ForegroundPlaybackResult(ForegroundPlaybackStatus Status,
    SavedMacroPreparationStatus? Preparation = null, TargetedMacroPlanProblem? DocumentProblem = null);

public sealed record ForegroundPlaybackStart(ForegroundPlaybackStatus? Rejection, CompanionForegroundPlayback.Run? Operation);

/// <summary>Host-local, single-window playback. The caller supplies an already owned target. This type grants no IPC authority.</summary>
public sealed class CompanionForegroundPlayback
{
    private readonly HostInputOperationArbiter _arbiter;
    private readonly SavedMacroPlanPreparer _preparer;
    private readonly ITargetWindowService _targets;
    private readonly IInputPlayer _player;
    private readonly IHostInputOperationAwarePlayer _ownedPlayer;
    private readonly IActivationService _activation;
    private readonly IFeatureGate _features;
    private readonly Func<CancellationToken, Task<bool>> _neutral;
    private readonly Action<Type> _fault;

    public CompanionForegroundPlayback(HostInputOperationArbiter arbiter, SavedMacroPlanPreparer preparer,
        ITargetWindowService targets, IInputPlayer player, IActivationService activation, IFeatureGate features,
        Func<CancellationToken, Task<bool>> waitForNeutral, Action<Type> fault)
    {
        _arbiter = arbiter ?? throw new ArgumentNullException(nameof(arbiter));
        _preparer = preparer ?? throw new ArgumentNullException(nameof(preparer));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        if (player is not (IHostInputOperationAwarePlayer and IPlaybackSessionAwarePlayer and IPlaybackSessionBoundSink))
            throw new ArgumentException("Foreground playback requires the host-owned, session-bound input executor.", nameof(player));
        _player = player;
        _ownedPlayer = (IHostInputOperationAwarePlayer)player;
        _activation = activation ?? throw new ArgumentNullException(nameof(activation));
        _features = features ?? throw new ArgumentNullException(nameof(features));
        _neutral = waitForNeutral ?? throw new ArgumentNullException(nameof(waitForNeutral));
        _fault = fault ?? throw new ArgumentNullException(nameof(fault));
    }

    public ForegroundPlaybackStart Start(SavedMacroReference reference, TargetWindowInfo window,
        long expectedStopGeneration, CancellationToken ct = default)
    {
        if (!reference.IsValid || window is not { IsOwnWindow: false, ProcessId: > 0, ProcessInstance: > 0 }
            || window.Handle == 0 || window.Handle == (nint)0xffff)
            return new(ForegroundPlaybackStatus.InvalidRequest, null);
        if (ct.IsCancellationRequested || expectedStopGeneration != _arbiter.StopGeneration)
            return new(ForegroundPlaybackStatus.Stopped, null);
        if (!_arbiter.TryAcquire(HostInputOperationKind.Companion, expectedStopGeneration, out var lease, out _))
            return new(expectedStopGeneration == _arbiter.StopGeneration ? ForegroundPlaybackStatus.Busy : ForegroundPlaybackStatus.Stopped, null);
        return new(null, new Run(this, lease!, reference, window, ct));
    }

    private void RecordFault(Exception error)
    {
        try { _fault(error.GetType()); } catch (Exception) { }
    }

    public sealed class Run
    {
        private readonly CompanionForegroundPlayback _host;
        private readonly HostInputOperationLease _lease;
        private readonly SemaphoreSlim _controls = new(1, 1);
        private PlaybackSessionContext? _session;

        public Guid Id => _lease.OperationId;
        internal bool HasSession => Volatile.Read(ref _session) is not null;
        internal bool IsStopping => !_lease.CanContinue;
        public bool IsPaused => Volatile.Read(ref _session)?.Control.IsPaused == true;
        public Task<ForegroundPlaybackResult> Completion { get; }

        internal Run(CompanionForegroundPlayback host, HostInputOperationLease lease,
            SavedMacroReference reference, TargetWindowInfo window, CancellationToken caller)
        {
            _host = host;
            _lease = lease;
            Completion = Task.Run(() => ExecuteAsync(reference, window, caller), CancellationToken.None);
        }

        public void Stop() => _lease.SignalStop();

        public Task<bool> PauseAsync(CancellationToken ct = default) => ChangePauseAsync(resume: false, ct);
        public Task<bool> ResumeAsync(CancellationToken ct = default) => ChangePauseAsync(resume: true, ct);

        private async Task<bool> ChangePauseAsync(bool resume, CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lease.StopToken);
            try
            {
                await _controls.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    if (!_lease.CanContinue || _session is not { } session) return false;
                    if (resume)
                    {
                        if (!session.Control.IsPaused) return false;
                        bool neutral = await _host._neutral(linked.Token).ConfigureAwait(false);
                        return await session.TryResumeAsync(neutral, linked.Token).ConfigureAwait(false);
                    }
                    await session.Control.PauseAsync(linked.Token).ConfigureAwait(false);
                    await session.AwaitReleasedAsync(linked.Token).ConfigureAwait(false);
                    return _lease.CanContinue && session.Control.IsPaused;
                }
                finally { _controls.Release(); }
            }
            catch (OperationCanceledException) when (!_lease.CanContinue && !ct.IsCancellationRequested) { return false; }
        }

        private async Task<ForegroundPlaybackResult> ExecuteAsync(SavedMacroReference reference, TargetWindowInfo expected,
            CancellationToken caller)
        {
            MacroController? controller = null;
            PlaybackSessionContext? session = null;
            IDisposable? native = null, attachment = null;
            CancellationTokenRegistration callerStop = default, controllerStop = default;
            ForegroundPlaybackResult result;
            try { result = await ExecuteCoreAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (!_lease.CanContinue || caller.IsCancellationRequested)
            { result = new(ForegroundPlaybackStatus.Stopped); }
            catch (Exception error) when (error is ActivationRequiredException or FeatureNotLicensedException)
            { result = new(ForegroundPlaybackStatus.LicenseRequired); }
            catch (Exception error)
            { _host.RecordFault(error); result = new(ForegroundPlaybackStatus.Faulted); }

            if (!_lease.CanContinue && result.Status == ForegroundPlaybackStatus.Completed)
                result = new(ForegroundPlaybackStatus.Stopped);
            // Prevent queued controls from entering and cancel any actual control work before detaching the session.
            _lease.SignalStop();
            await _controls.WaitAsync().ConfigureAwait(false);
            bool clean = true;
            try
            {
                Volatile.Write(ref _session, null);
                await CleanAsync(() => callerStop.DisposeAsync().AsTask()).ConfigureAwait(false);
                await CleanAsync(() => controllerStop.DisposeAsync().AsTask()).ConfigureAwait(false);
                if (controller is not null)
                {
                    await CleanAsync(controller.StopAsync).ConfigureAwait(false);
                    await CleanAsync(() => controller.WaitForStopSignalAsync()).ConfigureAwait(false);
                }
                if (session is not null) await CleanAsync(() => session.DisposeAsync().AsTask()).ConfigureAwait(false);
                if (native is not null) Clean(() => _host._ownedPlayer.ReleaseHostOperationInputs(_lease));
                Clean(() => attachment?.Dispose());
                Clean(() => controller?.Dispose());
                Clean(() => native?.Dispose());
            }
            finally { _controls.Release(); }
            await CleanAsync(() => _lease.CompleteAsync(clean)).ConfigureAwait(false);
            return clean ? result : new(ForegroundPlaybackStatus.CleanupFailed);

            async Task<ForegroundPlaybackResult> ExecuteCoreAsync()
            {
                callerStop = caller.UnsafeRegister(static value => ((HostInputOperationLease)value!).SignalStop(), _lease);
                if (!_lease.CanContinue) return new(ForegroundPlaybackStatus.Stopped);
                _host._activation.RequireActive();
                _host._features.Require(FeatureId.BasicPlayback);
                var prepared = await _host._preparer.PrepareForegroundAsync(reference, _lease.StopToken).ConfigureAwait(false);
                if (!_lease.CanContinue) return new(ForegroundPlaybackStatus.Stopped);
                if (prepared.Prepared is not { } saved)
                    return new(ForegroundPlaybackStatus.PreparationRejected, prepared.Status, prepared.DocumentProblem);
                if (!await _host._neutral(_lease.StopToken).ConfigureAwait(false))
                    return new(ForegroundPlaybackStatus.InputHeld);
                if (!_lease.CanContinue) return new(ForegroundPlaybackStatus.Stopped);
                ResolvedMacroTarget resolved;
                try { resolved = _host._targets.Resolve(TargetedMacroPlan.ForegroundRule, expected); }
                catch (InvalidOperationException) { return new(ForegroundPlaybackStatus.TargetUnavailable); }
                if (resolved.Rule != TargetedMacroPlan.ForegroundRule || resolved.Window is not { IsOwnWindow: false } actual
                    || !PlaybackSessionContext.SameIdentity(expected, actual) || !_host._targets.Inspect(resolved).AllowsInput)
                    return new(ForegroundPlaybackStatus.TargetUnavailable);
                if (!saved.IsCurrent()) return new(ForegroundPlaybackStatus.PreparationRejected, SavedMacroPreparationStatus.Changed);

                var model = saved.Plan.CreateModel();
                controller = new MacroController(_host._player, _lease);
                controllerStop = _lease.StopToken.UnsafeRegister(static value => ((MacroController)value!).SignalStop(), controller);
                session = new PlaybackSessionContext(controller, _host._targets, resolved);
                session.BindHostOperation(_lease);
                native = _host._ownedPlayer.AttachHostOperation(_lease);
                attachment = session.Attach(_host._player);
                Volatile.Write(ref _session, session);
                var context = new MacroContext(new NoCapture(), controller)
                {
                    RunId = Id, PlaybackSession = session, InputPlayer = _host._player,
                    ActivationService = _host._activation, FeatureGate = _host._features,
                    PlaybackSpeed = model.Settings.PlaybackSpeed, MaxRuntime = model.Settings.MaxRuntime,
                    MaxIterations = model.Settings.MaxIterations, MaxLoopDepth = model.Settings.MaxLoopDepth,
                    EnforceRuntimeCap = true
                };
                var executor = new MacroExecutor();
                await executor.ExecuteAsync(model, context, _lease.StopToken).ConfigureAwait(false);
                return new(executor.State switch
                {
                    ExecutionState.Completed => ForegroundPlaybackStatus.Completed,
                    ExecutionState.Stopped or ExecutionState.Aborted => ForegroundPlaybackStatus.Stopped,
                    _ => ForegroundPlaybackStatus.Faulted
                });
            }

            void Clean(Action cleanup)
            {
                try { cleanup(); }
                catch (Exception error) { clean = false; _host.RecordFault(error); }
            }

            async Task CleanAsync(Func<Task> cleanup)
            {
                try { await cleanup().ConfigureAwait(false); }
                catch (Exception error) { clean = false; _host.RecordFault(error); }
            }
        }
    }
}
