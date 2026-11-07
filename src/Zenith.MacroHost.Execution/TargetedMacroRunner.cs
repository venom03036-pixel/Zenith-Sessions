using MacroSuite.Core.Execution;
using MacroSuite.Core.Input;
using Zenith.Sessions.Core;

namespace Zenith.MacroHost.Execution;
public enum TargetedMacroStartStatus
{
    Started,
    Rejected,
    Busy,
    InvalidTarget,
    UnsupportedInput
}

public enum TargetedMacroState
{
    Running,
    Pausing,
    Paused,
    Resuming,
    Stopping,
    Completed,
    Stopped,
    Faulted
}

public enum TargetedMacroResultStatus
{
    Completed,
    Stopped,
    Faulted,
    CleanupFailed
}

public enum TargetedMacroProblem
{
    None,
    Stopped,
    RuntimeLimit,
    InputRejected,
    InterpreterFault,
    CleanupFailed
}

public sealed record TargetedMacroStartResult(TargetedMacroStartStatus Status, TargetedMacroRun? Run);
public sealed record TargetedMacroRunSnapshot(Guid RunId, TargetedInputTarget Target, TargetedMacroState State, int CompletedSteps, int? CurrentStep, TargetedInputRunSnapshot Input, string Explanation)
{
    public bool AccountVerified => false;
    public bool RobloxQualified => false;
}

public sealed record TargetedMacroResult(TargetedMacroResultStatus Status, TargetedMacroProblem Problem, TargetedMacroRunSnapshot Snapshot);
internal sealed record UnstartedTargetedMacro(TargetedInputRun Input, Task<TargetedInputRunSnapshot> Completion);
/// <summary>Owns actual interpreters and transitions; borrows the one shared input coordinator.</summary>
public sealed class TargetedMacroRunner : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly TargetedInputCoordinator _input;
    private readonly TargetedMacroInputSupport _support;
    private readonly Func<TargetedInputTarget, TargetedMacroScope, bool> _eligible;
    private readonly int _maximum;
    private readonly Dictionary<Guid, TargetedMacroRun> _runs = new();
    private readonly HashSet<Task> _cleanup = new();
    private readonly HashSet<Task> _workers = new();
    private HostInputOperationLease? _boundOperation;
    private long _generation;
    private bool _closed, _disposing;
    private Task? _dispose;
    public TargetedMacroRunner(TargetedInputCoordinator input, TargetedMacroInputSupport support, Func<TargetedInputTarget, TargetedMacroScope, bool> eligible, int maximumRuns = 32)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _support = support ?? throw new ArgumentNullException(nameof(support));
        _eligible = eligible ?? throw new ArgumentNullException(nameof(eligible));
        if (maximumRuns is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(maximumRuns));
        _maximum = maximumRuns;
    }

    public TargetedMacroStartResult TryStart(TargetedMacroPlan plan, TargetedInputTarget target)
        => TryStartCore(plan, target, null, out _);

    internal long StopGeneration { get { lock (_sync) return _generation; } }

    internal bool TryBindOperation(HostInputOperationLease lease, long expectedGeneration)
    {
        lock (_sync)
        {
            _cleanup.RemoveWhere(static work => work.IsCompleted);
            _workers.RemoveWhere(static work => work.IsCompleted);
            if (_closed || _generation != expectedGeneration || lease.Kind != HostInputOperationKind.Companion || !lease.CanContinue
                || _boundOperation is { OwnsCleanup: true } || _runs.Count != 0 || _cleanup.Count != 0 || _workers.Count != 0)
                return false;
            _boundOperation = lease;
            return true;
        }
    }

    // The companion publishes the exact run before allowing it to send input.
    internal TargetedMacroStartResult TryPrepareBound(TargetedMacroPlan plan, TargetedInputTarget target,
        HostInputOperationLease lease, out UnstartedTargetedMacro? unstarted)
        => TryStartCore(plan, target, lease, out unstarted);

    private TargetedMacroStartResult TryStartCore(TargetedMacroPlan plan, TargetedInputTarget target,
        HostInputOperationLease? lease, out UnstartedTargetedMacro? unstarted)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(target);
        unstarted = null;
        if (lease is not null && (lease.Kind != HostInputOperationKind.Companion || !lease.CanContinue))
            return new(TargetedMacroStartStatus.Rejected, null);
        if (!_support.Supports(plan) || plan.PeakHolds > _input.MaximumHoldsPerRun)
            return new(TargetedMacroStartStatus.UnsupportedInput, null);
        if (!plan.Fits(target))
            return new(TargetedMacroStartStatus.InvalidTarget, null);
        long generation;
        lock (_sync)
        {
            if (_closed || !AllowsOwner(lease))
                return new(TargetedMacroStartStatus.Rejected, null);
            generation = _generation;
        }

        bool eligible;
        try
        {
            eligible = _eligible(target, plan.Scope);
        }
        catch (Exception)
        {
            eligible = false;
        }

        if (!eligible)
            return new(TargetedMacroStartStatus.Rejected, null);
        var model = plan.CreateModel();
        lock (_sync)
        {
            _cleanup.RemoveWhere(static item => item.IsCompleted);
            _workers.RemoveWhere(static item => item.IsCompleted);
            if (_closed || generation != _generation || !AllowsOwner(lease))
                return new(TargetedMacroStartStatus.Rejected, null);
            if (_runs.Count >= _maximum || _cleanup.Count >= _maximum)
                return new(TargetedMacroStartStatus.Busy, null);
            var opened = _input.TryOpen(target, TargetedInputPurpose.OwnedWindowDiagnostic,
                lease is null ? null : new HostAdmission(lease));
            if (opened.Run is not { } inputRun)
                return new(TargetedMacroStartStatus.Busy, null);
            try
            {
                var run = new TargetedMacroRun(this, inputRun, model, lease);
                _runs.Add(run.Id, run);
                _workers.Add(run.Completion);
                if (lease is null) run.Begin();
                return new(TargetedMacroStartStatus.Started, run);
            }
            catch (Exception)
            {
                // The pure lease can still own cleanup after a composition failure. Track
                // its actual transition before permitting disposal or another admission.
                var completion = inputRun.StopAsync();
                _cleanup.Add(completion);
                unstarted = new(inputRun, completion);
                return new(TargetedMacroStartStatus.Rejected, null);
            }
        }
    }

    private bool AllowsOwner(HostInputOperationLease? lease) => lease is null
        ? _boundOperation is not { OwnsCleanup: true }
        : ReferenceEquals(_boundOperation, lease) && lease.CanContinue;

    private sealed class HostAdmission(HostInputOperationLease lease) : ITargetedInputAdmission
    {
        public bool CanSubmit => lease.CanContinue;
        public bool OwnsCleanup => lease.OwnsCleanup;
    }

    internal void Finished(TargetedMacroRun run)
    {
        lock (_sync)
            if (run.InputSnapshot.State == TargetedInputRunState.Stopped && _runs.TryGetValue(run.Id, out var current) && ReferenceEquals(current, run))
                _runs.Remove(run.Id);
    }

    internal Task<TargetedMacroRunSnapshot> RetryCleanup(TargetedMacroRun run)
    {
        lock (_sync)
        {
            if (_disposing)
                return Task.FromResult(run.GetSnapshot());
            var task = run.BeginRetryCleanup();
            _cleanup.RemoveWhere(static item => item.IsCompleted);
            _cleanup.Add(task);
            return task;
        }
    }

    public void SignalPanic()
    {
        lock (_sync)
        {
            _generation++;
            _boundOperation?.SignalStop();
            foreach (var run in _runs.Values)
                run.SignalPanic();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_dispose is null || _dispose.IsFaulted)
            {
                _closed = true;
                _disposing = true;
                _generation++;
                var bound = _boundOperation;
                bound?.SignalStop();
                var runs = _runs.Values.ToArray();
                foreach (var run in runs)
                    run.SignalPanic();
                _dispose = DisposeCoreAsync(runs, _cleanup.Concat(_workers).ToArray(), bound);
            }

            return new(_dispose);
        }
    }

    private async Task DisposeCoreAsync(TargetedMacroRun[] runs, Task[] cleanup, HostInputOperationLease? bound)
    {
        await Task.Yield();
        try
        {
            await Task.WhenAll(runs.Select(run => run.Completion).Cast<Task>().Concat(cleanup)
                .Append(bound?.Completion ?? Task.CompletedTask)).ConfigureAwait(false);
            if (bound is { OwnsCleanup: true } || runs.Any(run => run.InputSnapshot.State != TargetedInputRunState.Stopped))
                throw new InvalidOperationException("Targeted macro cleanup is incomplete. The recipient remains quarantined.");
        }
        finally
        {
            lock (_sync)
                _disposing = false;
        }
    }
}

public sealed class TargetedMacroRun : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly TargetedMacroRunner _owner;
    private readonly TargetedInputRun _input;
    private readonly TargetedMacroPlayer _player;
    private readonly MacroController _control;
    private readonly PlaybackSessionContext _session;
    private readonly MacroExecutor _executor = new();
    private readonly MacroContext _context;
    private readonly MacroSuite.Core.Models.MacroModel _model;
    private readonly HostInputOperationLease? _hostOperation;
    private readonly TaskCompletionSource _start = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task<TargetedMacroResult> _completion;
    private Task<TargetedMacroRunSnapshot>? _transition, _retry;
    private readonly HashSet<Task> _transitions = new();
    private Task<TargetedInputRunSnapshot>? _stopping;
    private Task _controllerStopping = Task.CompletedTask;
    private long _generation;
    private int _completed;
    private int? _current;
    private bool _stopRequested, _finalizing;
    private TargetedMacroState _state = TargetedMacroState.Running;
    private TargetedMacroProblem _problem;
    public Guid Id { get; }
    public TargetedInputTarget Target => _input.Target;
    public Task<TargetedMacroResult> Completion => _completion;
    internal TargetedInputRunSnapshot InputSnapshot => _input.Snapshot;
    internal bool LifecycleCleanupFailed { get; private set; }

    internal TargetedMacroRun(TargetedMacroRunner owner, TargetedInputRun input, MacroSuite.Core.Models.MacroModel model,
        HostInputOperationLease? hostOperation = null)
    {
        _owner = owner;
        _input = input;
        Id = input.Snapshot.RunId;
        _model = model;
        _hostOperation = hostOperation;
        _player = new(input);
        _control = new(_player);
        // Clock/control only. There is deliberately no fabricated foreground/target service.
        _session = new(_control);
        _player.Session = _session;
        _context = new(new NoCapture(), _control)
        {
            RunId = Id,
            InputPlayer = _player,
            PlaybackSession = _session,
            PlaybackSpeed = model.Settings.PlaybackSpeed,
            MaxRuntime = model.Settings.MaxRuntime,
            MaxIterations = model.Settings.MaxIterations,
            MaxLoopDepth = model.Settings.MaxLoopDepth,
            EnforceRuntimeCap = true
        };
        _completion = ExecuteAsync();
    }

    internal void Begin() => _start.TrySetResult();
    public TargetedMacroRunSnapshot GetSnapshot()
    {
        lock (_sync)
            return new(Id, Target, _state, _completed, _current, _input.Snapshot, Explanation(_state, _problem));
    }

    public Task<TargetedMacroRunSnapshot> PauseAsync(CancellationToken ct = default)
    {
        Task<TargetedMacroRunSnapshot> task;
        lock (_sync)
        {
            if (_stopRequested || _finalizing || Terminal(_state) || _state == TargetedMacroState.Paused)
                return Task.FromResult(GetSnapshot());
            if (_transition is { IsCompleted: false } && _state != TargetedMacroState.Resuming)
                task = _transition;
            else
            {
                _player.CloseAdmission();
                var paused = _input.PauseAsync();
                var controlPaused = _control.PauseAsync();
                _state = TargetedMacroState.Pausing;
                long generation = ++_generation;
                task = _transition = PauseCoreAsync(paused, controlPaused, generation);
                TrackTransition(task);
            }
        }

        return ct.CanBeCanceled ? task.WaitAsync(ct) : task;
    }

    private async Task<TargetedMacroRunSnapshot> PauseCoreAsync(Task<TargetedInputRunSnapshot> paused, Task controlPaused, long generation)
    {
        await Task.Yield();
        await controlPaused.ConfigureAwait(false);
        var result = await paused.ConfigureAwait(false);
        lock (_sync)
        {
            if (generation == _generation && !_stopRequested)
            {
                if (result.State == TargetedInputRunState.Paused)
                    _state = TargetedMacroState.Paused;
                else
                    RequestStopLocked(false, TargetedMacroProblem.InputRejected);
            }

            return GetSnapshot();
        }
    }

    public Task<TargetedMacroRunSnapshot> ResumeAsync(CancellationToken ct = default)
    {
        Task<TargetedMacroRunSnapshot> task;
        lock (_sync)
        {
            if (_stopRequested || _finalizing || _state != TargetedMacroState.Paused)
                return Task.FromResult(GetSnapshot());
            if (_transition is { IsCompleted: false })
                task = _transition;
            else
            {
                _state = TargetedMacroState.Resuming;
                long generation = ++_generation;
                task = _transition = ResumeCoreAsync(_input.ResumeAsync(), generation);
                TrackTransition(task);
            }
        }

        return ct.CanBeCanceled ? task.WaitAsync(ct) : task;
    }

    private async Task<TargetedMacroRunSnapshot> ResumeCoreAsync(Task<TargetedInputRunSnapshot> resumed, long generation)
    {
        await Task.Yield();
        var result = await resumed.ConfigureAwait(false);
        Task controlResumed = Task.CompletedTask;
        lock (_sync)
        {
            if (generation == _generation && !_stopRequested && !_finalizing)
            {
                if (result.State == TargetedInputRunState.Running)
                {
                    controlResumed = _control.ResumeAsync();
                    _state = TargetedMacroState.Running;
                    _player.OpenAdmission();
                }
                else
                    RequestStopLocked(false, TargetedMacroProblem.InputRejected);
            }
        }

        await controlResumed.ConfigureAwait(false);
        return GetSnapshot();
    }

    public Task<TargetedMacroResult> StopAsync()
    {
        lock (_sync)
            RequestStopLocked(false, TargetedMacroProblem.Stopped);
        return _completion;
    }

    public void SignalPanic()
    {
        lock (_sync)
            RequestStopLocked(true, TargetedMacroProblem.Stopped);
    }

    private void RequestStopLocked(bool panic, TargetedMacroProblem reason)
    {
        if (Terminal(_state))
            return;
        _stopRequested = true;
        _generation++;
        _problem = reason;
        _state = TargetedMacroState.Stopping;
        _player.CloseAdmission();
        if (_finalizing)
            return;
        if (!HoldsNeedRecovery(_input.Snapshot))
            _stopping ??= _input.StopAsync();
        if (panic)
            _control.SignalPanic();
        else
            _controllerStopping = _control.StopAsync();
    }

    private void TrackTransition(Task task)
    {
        _transitions.RemoveWhere(static value => value.IsCompletedSuccessfully);
        _transitions.Add(task);
    }

    public Task<TargetedMacroRunSnapshot> RetryCleanupAsync() => _owner.RetryCleanup(this);
    internal Task<TargetedMacroRunSnapshot> BeginRetryCleanup()
    {
        lock (_sync)
        {
            if (!_completion.IsCompleted)
                return Task.FromResult(GetSnapshot());
            if (_retry is { IsCompleted: false })
                return _retry;
            return _retry = RetryCoreAsync();
        }
    }

    private async Task<TargetedMacroRunSnapshot> RetryCoreAsync()
    {
        await Task.Yield();
        var state = await _input.RetryCleanupAsync().ConfigureAwait(false);
        lock (_sync)
        {
            if (state.State == TargetedInputRunState.Stopped)
                _state = TargetedMacroState.Stopped;
        }

        _owner.Finished(this);
        return GetSnapshot();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (_input.Snapshot.State != TargetedInputRunState.Stopped)
            throw new InvalidOperationException("Targeted macro cleanup is incomplete. Retry cleanup explicitly.");
    }

    private async Task<TargetedMacroResult> ExecuteAsync()
    {
        await _start.Task.ConfigureAwait(false);
        void Starting(object? _, (int StepIndex, MacroSuite.Core.Models.MacroStep Step) value)
        {
            lock (_sync)
                _current = value.StepIndex;
        }

        void Completed(object? _, (int StepIndex, MacroSuite.Core.Models.MacroStep Step, TimeSpan Duration) value)
        {
            lock (_sync)
                _completed++;
        }

        _executor.StepStarting += Starting;
        _executor.StepCompleted += Completed;
        try
        {
            if (_hostOperation is { } host) _session.BindHostOperation(host);
            await _executor.ExecuteAsync(_model, _context, _hostOperation?.StopToken ?? default).ConfigureAwait(false);
        }
        catch (Exception)
        {
            lock (_sync)
                _problem = _hostOperation is { CanContinue: false } ? TargetedMacroProblem.Stopped
                    : _player.Rejected ? TargetedMacroProblem.InputRejected : TargetedMacroProblem.InterpreterFault;
        }
        finally
        {
            _executor.StepStarting -= Starting;
            _executor.StepCompleted -= Completed;
            lock (_sync)
            {
                _finalizing = true;
                _generation++;
                _state = TargetedMacroState.Stopping;
                _player.CloseAdmission();
            }

            bool cleanupFailed = false, holdCleanupFailed = false;
            // Failure of one cleanup leg never skips the other actual owned tasks. A faulted
            // input lease that still owns submitted holds remains quarantined; finalization
            // does not retry it. A fault with nothing left to release is stopped normally.
            try
            {
                await _control.WaitForPanicReleaseAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                cleanupFailed = true;
            }

            Task controllerStopping;
            lock (_sync)
                controllerStopping = _controllerStopping;
            try
            {
                await controllerStopping.ConfigureAwait(false);
            }
            catch (Exception)
            {
                cleanupFailed = true;
            }

            try
            {
                await _player.DrainClearAsync().ConfigureAwait(false);
            }
            catch (TargetedMacroHoldCleanupFailure)
            {
                holdCleanupFailed = true;
            }
            catch (Exception)
            {
                cleanupFailed = true;
            }

            try
            {
                if (!HoldsNeedRecovery(_input.Snapshot))
                {
                    Task<TargetedInputRunSnapshot> stop;
                    lock (_sync)
                        stop = _stopping ??= _input.StopAsync();
                    await stop.ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                cleanupFailed = true;
            }

            Task[] transitions;
            lock (_sync)
                transitions = _transitions.ToArray();
            try
            {
                await Task.WhenAll(transitions).ConfigureAwait(false);
            }
            catch (Exception)
            {
                cleanupFailed = true;
            }

            try
            {
                await _session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                cleanupFailed = true;
            }

            try
            {
                _player.Dispose();
            }
            catch (Exception)
            {
                cleanupFailed = true;
            }

            try
            {
                _control.Dispose();
            }
            catch (Exception)
            {
                cleanupFailed = true;
            }

            if (cleanupFailed || holdCleanupFailed)
                lock (_sync)
                {
                    LifecycleCleanupFailed = cleanupFailed;
                    _problem = TargetedMacroProblem.CleanupFailed;
                }
        }

        TargetedMacroResult result;
        lock (_sync)
        {
            if (_input.Snapshot.State != TargetedInputRunState.Stopped || _problem == TargetedMacroProblem.CleanupFailed)
            {
                _problem = TargetedMacroProblem.CleanupFailed;
                _state = TargetedMacroState.Faulted;
                result = new(TargetedMacroResultStatus.CleanupFailed, _problem, GetSnapshot());
            }
            else if (_problem is TargetedMacroProblem.InterpreterFault or TargetedMacroProblem.InputRejected)
            {
                _state = TargetedMacroState.Faulted;
                result = new(TargetedMacroResultStatus.Faulted, _problem, GetSnapshot());
            }
            else if (_stopRequested || _hostOperation is { CanContinue: false } || _executor.State != ExecutionState.Completed)
            {
                _state = TargetedMacroState.Stopped;
                _problem = _stopRequested || _hostOperation is { CanContinue: false } ? TargetedMacroProblem.Stopped : TargetedMacroProblem.RuntimeLimit;
                result = new(TargetedMacroResultStatus.Stopped, _problem, GetSnapshot());
            }
            else
            {
                _state = TargetedMacroState.Completed;
                result = new(TargetedMacroResultStatus.Completed, TargetedMacroProblem.None, GetSnapshot());
            }
        }

        _owner.Finished(this);
        return result;
    }

    /// <summary>
    /// A faulted input run is a cleanup failure only while it still owns submitted holds. The
    /// coordinator empties them only after every release was queued or the recipient is definitively
    /// gone; a rejected, uncertain or unowned release keeps them, so that fault stays quarantined.
    /// </summary>
    internal static bool HoldsNeedRecovery(TargetedInputRunSnapshot input)
        => input.State == TargetedInputRunState.Faulted && input.SubmittedHolds > 0;

    private static bool Terminal(TargetedMacroState state) => state is TargetedMacroState.Completed or TargetedMacroState.Stopped or TargetedMacroState.Faulted;
    private static string Explanation(TargetedMacroState state, TargetedMacroProblem problem) => problem == TargetedMacroProblem.CleanupFailed ? "Cleanup failed; the recipient remains quarantined until explicit successful cleanup." : state switch
    {
        TargetedMacroState.Pausing => "Pausing this interpreter and releasing its submitted holds.",
        TargetedMacroState.Paused => "This interpreter is paused; unfinished logical holds may restore only after explicit resume.",
        TargetedMacroState.Resuming => "Revalidating this recipient and restoring unfinished holds.",
        TargetedMacroState.Stopping => "Stopped admission; joining actual interpreter and cleanup work.",
        TargetedMacroState.Completed => "The interpreter completed and its input lease was released. Native consumption and account identity are not established.",
        TargetedMacroState.Stopped => "The interpreter stopped and its input lease was released.",
        TargetedMacroState.Faulted => "The interpreter failed with a fixed diagnostic outcome.",
        _ => "Owned-window diagnostic interpreter running. Account and Roblox input support remain unverified."
    };
}
