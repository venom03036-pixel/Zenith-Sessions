namespace Zenith.Sessions.Core;

/// <summary>
/// One bounded target-directed transport owner. No capture, authorization, macro interpretation or physical input.
/// Distinct unfinished/submitted holds are message ownership, never proof of a game's physical input state.
/// </summary>
public sealed class TargetedInputCoordinator : IAsyncDisposable
{
    internal readonly object Sync = new();
    internal readonly SemaphoreSlim Lane = new(1, 1);
    internal readonly ITargetedInputTransport Transport;
    internal readonly TargetedInputLimits Limits;
    private readonly Dictionary<nint, TargetedInputRun> _runs = [];
    private readonly HashSet<Task> _workers = [];
    private bool _disposed;
    private Task? _dispose;
    public TargetedInputCoordinator(ITargetedInputTransport transport, TargetedInputLimits? limits = null)
    { Transport = transport ?? throw new ArgumentNullException(nameof(transport)); Limits = (limits ?? new()).Validated(); }

    /// <summary>The exact configured hold budget, for whole-plan admission before opening a run lease.</summary>
    public int MaximumHoldsPerRun => Limits.MaximumHolds;

    public TargetedInputOpenResult TryOpen(TargetedInputTarget target, TargetedInputPurpose purpose)
        => TryOpen(target, purpose, null);

    public TargetedInputOpenResult TryOpen(TargetedInputTarget target, TargetedInputPurpose purpose,
        ITargetedInputAdmission? admission)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (Sync)
        {
            if (_disposed) return new(null, "The input owner is shutting down.");
            if (!target.IsValid || !Enum.IsDefined(purpose)) return new(null, "A current registered physical target and purpose are required.");
            if (admission is { CanSubmit: false }) return new(null, "The captured input owner no longer permits submission.");
            if (_runs.ContainsKey(target.Window)) return new(null, "This window is still owned by a run, including paused or unfinished cleanup.");
            if (!HasDispatchBudgetLocked()) return new(null, "Input work is still completing; wait before opening another run.");
            if (_runs.Count >= Limits.MaximumRuns) return new(null, "The active input-run budget is full. Saved accounts are unaffected.");
            var run = new TargetedInputRun(this, target, purpose, admission); _runs.Add(target.Window, run);
            return new(run, "Input ownership opened. Target/account eligibility and actual input consumption are not established by this lease.");
        }
    }

    /// <summary>Closes admission without awaiting UI, native work, credentials or saving. Finalization remains tracked.</summary>
    public void SignalStopAll()
    { lock (Sync) foreach (var run in _runs.Values) run.RequestStopLocked(TargetedInputRun.StoppedByEmergency); }

    internal void ReleaseLocked(TargetedInputRun run)
    { if (_runs.TryGetValue(run.Target.Window, out var current) && ReferenceEquals(run, current)) _runs.Remove(run.Target.Window); }
    internal bool HasDispatchBudgetLocked()
    { _workers.RemoveWhere(static task => task.IsCompleted); return _workers.Count < Limits.MaximumRuns * 2; }
    internal void TrackLocked(Task task) { _workers.RemoveWhere(static item => item.IsCompleted); _workers.Add(task); }
    internal bool DisposalInProgressLocked => _dispose is { IsCompleted: false };

    public ValueTask DisposeAsync()
    {
        lock (Sync)
        {
            if (_dispose is null || _dispose.IsFaulted)
            {
                _disposed = true;
                var runs = _runs.Values.ToArray();
                foreach (var run in runs) run.RequestStopLocked("Input owner closed.");
                // A completed lease can be removed just before its worker releases Lane. Track actual workers
                // independently so shutdown joins that final native/managed tail as well.
                _dispose = DisposeCoreAsync(_workers.ToArray());
            }
            return new(_dispose);
        }
    }
    private async Task DisposeCoreAsync(Task[] workers)
    {
        await Task.Yield();
        await Task.WhenAll(workers).ConfigureAwait(false);
        lock (Sync) if (_runs.Count != 0) throw new InvalidOperationException("Targeted input cleanup is incomplete. The affected targets remain faulted.");
        Lane.Dispose();
    }
}

public sealed class TargetedInputRun
{
    private enum Transition { None, Pause, Resume, Stop, Fault, Clear }
    private readonly TargetedInputCoordinator _owner;
    private readonly ITargetedInputAdmission? _admission;
    private readonly List<TargetedInputSignal> _logical = [], _submitted = [];
    private readonly Guid _id = Guid.NewGuid();
    private TargetedInputRunState _state = TargetedInputRunState.Running;
    private Transition _requested;
    private Task<TargetedInputRunSnapshot>? _transition;
    private Task<TargetedInputDispatchResult>? _batch;
    private bool _batchActive, _transitionActive, _clearPending, _clearReturnPaused, _stopAfterFault;
    private long _generation = 1, _sequence;
    private int _x, _y;
    private string _explanation = "Input ownership opened; no input has been sent.";
    // What a fault's text says happened, without its request for cleanup; a stop that then releases the holds
    // reports this instead of an instruction it has just carried out. Faulted is terminal, so it is never stale.
    private string? _faultSummary;
    public TargetedInputTarget Target { get; }
    public TargetedInputPurpose Purpose { get; }
    internal TargetedInputRun(TargetedInputCoordinator owner, TargetedInputTarget target,
        TargetedInputPurpose purpose, ITargetedInputAdmission? admission)
    { _owner = owner; Target = target; Purpose = purpose; _admission = admission; }

    private bool CanSubmit => _admission?.CanSubmit != false;
    private bool OwnsCleanup => _admission?.OwnsCleanup != false;

    public TargetedInputRunSnapshot Snapshot { get { lock (_owner.Sync) return SnapshotLocked(); } }
    private TargetedInputRunSnapshot SnapshotLocked() => new(_id, Target, Purpose, _state, _generation, _sequence,
        _logical.Count, _submitted.Count, _explanation);

    public Task<TargetedInputDispatchResult> SubmitAsync(IReadOnlyList<TargetedInputSignal> signals, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signals);
        lock (_owner.Sync)
        {
            if (_state != TargetedInputRunState.Running || !CanSubmit) return Result(TargetedInputDispatchStatus.Inactive);
            if (_batchActive || !_owner.HasDispatchBudgetLocked()) return Result(TargetedInputDispatchStatus.Busy);
            int count = signals.Count;
            if (count < 1 || count > _owner.Limits.MaximumBatch) return Result(TargetedInputDispatchStatus.Invalid);
            var frozen = new TargetedInputSignal[count];
            for (int index = 0; index < count; index++)
            { var signal = signals[index]; if (!signal.IsValid(Target)) return Result(TargetedInputDispatchStatus.Invalid); frozen[index] = signal; }
            _batchActive = true;
            _batch = DispatchAsync(frozen, _generation, ct);
            _owner.TrackLocked(_batch); return _batch;
        }
    }
    private Task<TargetedInputDispatchResult> Result(TargetedInputDispatchStatus status)
        => Task.FromResult(new TargetedInputDispatchResult(status, 0, 0, SnapshotLocked()));

    public Task<TargetedInputRunSnapshot> PauseAsync()
    {
        lock (_owner.Sync)
        {
            if (_state is TargetedInputRunState.Stopped or TargetedInputRunState.Faulted or TargetedInputRunState.Paused)
                return Task.FromResult(SnapshotLocked());
            if (_requested is not (Transition.Stop or Transition.Fault))
            { _requested = Transition.Pause; _state = TargetedInputRunState.Pausing; _generation++; _explanation = "Pausing and releasing submitted holds."; }
            return EnsureTransitionLocked();
        }
    }
    public Task<TargetedInputRunSnapshot> ResumeAsync()
    {
        lock (_owner.Sync)
        {
            if (_state != TargetedInputRunState.Paused || _transitionActive)
                return _transitionActive ? _transition! : Task.FromResult(SnapshotLocked());
            if (!CanSubmit) return Task.FromResult(SnapshotLocked());
            _requested = Transition.Resume; _state = TargetedInputRunState.Resuming; _generation++;
            _explanation = "Revalidating the pinned target and restoring unfinished holds.";
            return EnsureTransitionLocked();
        }
    }

    /// <summary>
    /// Fences ordinary emission, discards unfinished restore intent and joins release of this run's submitted
    /// holds while retaining its target lease. A paused run stays paused; clearing during restoration also
    /// requires explicit resume. Stop/fault takes priority and failed cleanup never restores admission.
    /// Like Pause/Stop, cleanup remains owned rather than accepting a caller token that could abandon it.
    /// </summary>
    public Task<TargetedInputRunSnapshot> ClearHoldsAsync()
    {
        lock (_owner.Sync)
        {
            if (_state is TargetedInputRunState.Stopped or TargetedInputRunState.Faulted)
                return Task.FromResult(SnapshotLocked());
            if (_requested is Transition.Stop or Transition.Fault || _clearPending)
                return EnsureTransitionLocked();
            _clearPending = true;
            _clearReturnPaused = _state != TargetedInputRunState.Running;
            _generation++;
            if (_requested != Transition.Pause)
            { _requested = Transition.Clear; _state = TargetedInputRunState.Clearing; }
            _explanation = "Clearing run-owned holds while retaining the pinned target lease.";
            return EnsureTransitionLocked();
        }
    }
    internal const string StoppedByUser = "Stopped by user.", StoppedByEmergency = "Stopped by emergency stop.";

    /// <summary>A stop the user asked for.</summary>
    public Task<TargetedInputRunSnapshot> StopAsync() => StopAsync(TargetedInputStopReason.User);

    /// <summary>
    /// A stop that says why it happened, for an owner that releases the lease when the user did not ask. The reason
    /// is one of a closed set whose texts this owner keeps; a value outside it is refused before anything changes.
    /// </summary>
    public Task<TargetedInputRunSnapshot> StopAsync(TargetedInputStopReason reason)
    {
        string text = Text(reason);
        lock (_owner.Sync)
        {
            // While shutdown is joining a fixed set of actual workers, reuse that finalization.
            // A failed shutdown can be explicitly retried after its result is available.
            if (_owner.DisposalInProgressLocked && !_transitionActive) return _transition ?? Task.FromResult(SnapshotLocked());
            RequestStopLocked(text); return EnsureTransitionLocked();
        }
    }

    // Only a user's Stop says the user stopped it; the other texts say what released the lease instead.
    private static string Text(TargetedInputStopReason reason) => reason switch
    {
        TargetedInputStopReason.User => StoppedByUser,
        TargetedInputStopReason.EmergencyStop => StoppedByEmergency,
        TargetedInputStopReason.Completed => "Macro completed; input lease released.",
        TargetedInputStopReason.RuntimeLimit => "Macro reached its runtime limit; input lease released.",
        TargetedInputStopReason.AfterFailure => "Macro stopped after a failure; input lease released.",
        TargetedInputStopReason.CompanionStopped => "Its companion operation stopped; input lease released.",
        TargetedInputStopReason.RunnerClosed => "Macro runner closed; input lease released.",
        TargetedInputStopReason.NotStarted => "Run could not start; input lease released.",
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };

    /// <summary>Explicit retry; a failed cleanup never frees a target lease automatically.</summary>
    public Task<TargetedInputRunSnapshot> RetryCleanupAsync() => StopAsync();
    internal void RequestStopLocked(string reason)
    {
        if (_state == TargetedInputRunState.Stopped || _owner.DisposalInProgressLocked && !_transitionActive) return;
        if (_requested != Transition.Stop)
        {
            // Stopping a fault that released (or is releasing) its holds only frees the lease, whoever asks;
            // keep why it faulted (its summary, once released). A failed cleanup's text already replaced that
            // reason, so it keeps the stop's.
            _stopAfterFault = _requested == Transition.Fault || _state == TargetedInputRunState.Faulted && _submitted.Count == 0;
            if (!_stopAfterFault) _explanation = reason;
            _generation++; _requested = Transition.Stop; _state = TargetedInputRunState.Stopping;
        }
        EnsureTransitionLocked();
    }
    private Task<TargetedInputRunSnapshot> EnsureTransitionLocked()
    {
        if (_transitionActive) return _transition!;
        if (_state == TargetedInputRunState.Stopped) return _transition = Task.FromResult(SnapshotLocked());
        _transitionActive = true;
        _transition = TransitionAsync(); _owner.TrackLocked(_transition); return _transition;
    }
    internal async Task JoinAsync()
    {
        Task<TargetedInputDispatchResult>? batch; Task<TargetedInputRunSnapshot>? transition;
        lock (_owner.Sync) { batch = _batch; transition = _transition; }
        if (batch is not null) await batch.ConfigureAwait(false);
        if (transition is not null) await transition.ConfigureAwait(false);
    }
    private bool Open(long generation, bool restore = false)
    {
        lock (_owner.Sync)
            return _generation == generation
                && _state == (restore ? TargetedInputRunState.Resuming : TargetedInputRunState.Running)
                && CanSubmit;
    }

    private async Task<TargetedInputDispatchResult> DispatchAsync(TargetedInputSignal[] signals, long generation, CancellationToken ct)
    {
        await Task.Yield();
        int sent = 0, uncertain = 0; bool acquired = false;
        var status = TargetedInputDispatchStatus.Submitted;
        try
        {
            await _owner.Lane.WaitAsync(ct).ConfigureAwait(false); acquired = true;
            for (int index = 0; index < signals.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                if (!Open(generation)) { status = TargetedInputDispatchStatus.Inactive; break; }
                var validation = await ValidateAsync(false, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!Open(generation)) { status = TargetedInputDispatchStatus.Inactive; break; }
                if (validation != TargetedInputValidation.Current)
                {
                    lock (_owner.Sync) FaultLocked("The pinned target identity or geometry changed; reselect it before a new run.");
                    status = TargetedInputDispatchStatus.Faulted; break;
                }
                var signal = signals[index];
                lock (_owner.Sync)
                {
                    if (signal.IsDown && Find(_logical, signal.HoldId) < 0 && _logical.Count >= _owner.Limits.MaximumHolds)
                    { FaultLocked("The per-run held-control budget was reached; cleanup is required.", "The per-run held-control budget was reached."); status = TargetedInputDispatchStatus.Faulted; break; }
                    if (signal.IsUp && Find(_logical, signal.HoldId) < 0)
                    { FaultLocked("A release without a matching run-owned press was rejected."); status = TargetedInputDispatchStatus.Faulted; break; }
                }
                var outcome = await EmitAsync(signal, generation, cleanup: false, restore: false, ct).ConfigureAwait(false);
                if (outcome == TargetedInputSubmission.Queued) sent++;
                if (outcome == TargetedInputSubmission.Uncertain) uncertain++;
                if (ct.IsCancellationRequested)
                { lock (_owner.Sync) RequestStopLocked(CancelledText(sent, uncertain)); status = TargetedInputDispatchStatus.Cancelled; break; }
                if (outcome == TargetedInputSubmission.Rejected && !Open(generation))
                { status = TargetedInputDispatchStatus.Inactive; break; }
                if (outcome != TargetedInputSubmission.Queued)
                {
                    lock (_owner.Sync)
                    {
                        if (outcome == TargetedInputSubmission.Uncertain)
                            FaultLocked("Submission may have occurred; owned cleanup must be checked.", "Submission may have occurred.");
                        else FaultLocked("Target-directed input was rejected; no foreground fallback was used.");
                    }
                    status = TargetedInputDispatchStatus.Faulted; break;
                }
                if (!Open(generation)) { status = TargetedInputDispatchStatus.Inactive; break; }
            }
        }
        catch (OperationCanceledException)
        { lock (_owner.Sync) RequestStopLocked(CancelledText(sent, uncertain)); status = TargetedInputDispatchStatus.Cancelled; }
        catch (Exception)
        { lock (_owner.Sync) FaultLocked("The input transport failed. Submitted or uncertain holds require cleanup.", "The input transport failed."); status = TargetedInputDispatchStatus.Faulted; }
        finally
        {
            lock (_owner.Sync) _batchActive = false;
            if (acquired) _owner.Lane.Release();
        }
        Task<TargetedInputRunSnapshot>? transition;
        lock (_owner.Sync) transition = _transition;
        if (transition is not null) await transition.ConfigureAwait(false);
        lock (_owner.Sync) return new(status, sent, uncertain, SnapshotLocked());
    }

    // A cancelled batch stops the run and releases its holds either way; only say part of the batch went out when it did
    // (the caller's token can be cancelled while the transport still holds the first signal, which it then refuses).
    private static string CancelledText(int sent, int uncertain) => sent + uncertain > 0
        ? "Input batch cancelled after a submitted or uncertain prefix; releasing its holds."
        : "Input batch cancelled before any of it was sent; releasing the run's holds.";

    /// <param name="summary">The part of <paramref name="reason"/> that stays true once the holds are released; omit it when that is all of it.</param>
    private void FaultLocked(string reason, string? summary = null)
    {
        if (_requested == Transition.Stop) return;
        _requested = Transition.Fault; _state = TargetedInputRunState.Pausing; _generation++; _explanation = reason;
        _faultSummary = summary ?? reason;
        EnsureTransitionLocked();
    }
    private async Task<TargetedInputRunSnapshot> TransitionAsync()
    {
        // Establish the tracked task before any native/transport work or callback can reenter the owner.
        await Task.Yield();
        await _owner.Lane.WaitAsync().ConfigureAwait(false);
        try
        {
            while (true)
            {
                Transition request; long generation;
                lock (_owner.Sync)
                {
                    request = _requested; generation = _generation;
                    // Only this transition/lane mutates the collection. Clear requests can fence a pending
                    // restoration immediately without racing its enumeration of the logical hold list.
                    if (_clearPending) _logical.Clear();
                }
                if (request == Transition.Resume)
                {
                    bool restored = await RestoreAsync(generation).ConfigureAwait(false);
                    lock (_owner.Sync)
                    {
                        if (_generation != generation) continue;
                        if (!CanSubmit)
                        {
                            RequestStopLocked("The captured input owner stopped during hold restoration.");
                            continue;
                        }
                        if (restored)
                        { _requested = Transition.None; _state = TargetedInputRunState.Running; _transitionActive = false; _explanation = "Resumed after target validation; only unfinished holds were restored."; return SnapshotLocked(); }
                        _requested = Transition.Fault; _state = TargetedInputRunState.Pausing;
                        _explanation = "Hold restoration or target validation failed. The run remains faulted until cleanup is explicitly checked.";
                        _faultSummary = "Hold restoration or target validation failed.";
                    }
                    continue;
                }
                bool released = await ReleaseAsync().ConfigureAwait(false);
                lock (_owner.Sync)
                {
                    request = _requested;
                    // A clear can arrive after ReleaseAsync completed but before this publication lock.
                    if (_clearPending) _logical.Clear();
                    bool clearReturnsPaused = _clearReturnPaused;
                    _clearPending = false; _clearReturnPaused = false;
                    if (!released)
                    { _requested = Transition.None; _state = TargetedInputRunState.Faulted; _transitionActive = false; _explanation = "Input cleanup is incomplete or uncertain. This target remains reserved; retry cleanup explicitly."; return SnapshotLocked(); }
                    _requested = Transition.None;
                    _transitionActive = false;
                    if (request == Transition.Stop)
                    {
                        _logical.Clear(); _state = TargetedInputRunState.Stopped; _owner.ReleaseLocked(this);
                        if (_stopAfterFault)
                            _explanation = (_faultSummary ?? _explanation) + " Stopped after the fault; its holds were released and the window lease is free.";
                    }
                    else if (request == Transition.Fault) _state = TargetedInputRunState.Faulted;
                    else if (request == Transition.Clear)
                    {
                        _state = clearReturnsPaused ? TargetedInputRunState.Paused : TargetedInputRunState.Running;
                        _explanation = clearReturnsPaused
                            ? "Run-owned holds cleared; the pinned target remains paused. Resume is explicit."
                            : "Run-owned holds cleared; the pinned target lease is retained for subsequent input.";
                    }
                    else { _state = TargetedInputRunState.Paused; _explanation = "Paused; submitted holds released. Resume is explicit."; }
                    return SnapshotLocked();
                }
            }
        }
        catch (Exception)
        {
            lock (_owner.Sync)
            {
                if (_clearPending) _logical.Clear();
                _clearPending = false; _clearReturnPaused = false;
                _requested = Transition.None; _state = TargetedInputRunState.Faulted; _transitionActive = false;
                _explanation = "Native cleanup failed. The target remains reserved; retry cleanup explicitly.";
                return SnapshotLocked();
            }
        }
        finally { _owner.Lane.Release(); }
    }

    private async Task<bool> RestoreAsync(long generation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            if (!Open(generation, true)
                || await ValidateAsync(false, deadline.Token).ConfigureAwait(false) != TargetedInputValidation.Current
                || !Open(generation, true)) return false;
            for (int index = 0; index < _logical.Count; index++)
            {
                if (!Open(generation, true) || deadline.IsCancellationRequested) return false;
                if (await ValidateAsync(false, deadline.Token).ConfigureAwait(false) != TargetedInputValidation.Current) return false;
                var held = _logical[index];
                if (held.Kind != TargetedInputKind.KeyDown) held = held with { X = _x, Y = _y };
                if (await EmitAsync(held, generation, false, true, deadline.Token).ConfigureAwait(false) != TargetedInputSubmission.Queued) return false;
            }
            return true;
        }
        catch (Exception) { return false; } // Transition owner still joins cleanup of any partially restored holds.
    }
    private async Task<bool> ReleaseAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool okay = true;
        for (int index = _submitted.Count - 1; index >= 0; index--)
        {
            if (!OwnsCleanup) return false;
            var validation = await ValidateAsync(true, deadline.Token).ConfigureAwait(false);
            if (!OwnsCleanup) return false;
            if (validation == TargetedInputValidation.Gone) { lock (_owner.Sync) _submitted.Clear(); return true; }
            if (validation != TargetedInputValidation.Current || deadline.IsCancellationRequested) return false;
            var release = _submitted[index].Release(_x, _y);
            var outcome = await EmitAsync(release, 0, true, false, deadline.Token).ConfigureAwait(false);
            if (outcome != TargetedInputSubmission.Queued) okay = false;
        }
        return okay && _submitted.Count == 0;
    }
    private async ValueTask<TargetedInputValidation> ValidateAsync(bool cleanup, CancellationToken ct)
    {
        try { return await _owner.Transport.ValidateAsync(Target, cleanup, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return TargetedInputValidation.Unavailable; }
    }
    private async ValueTask<TargetedInputSubmission> EmitAsync(TargetedInputSignal signal, long generation, bool cleanup, bool restore, CancellationToken ct)
    {
        bool repeat; TargetedInputModifiers modifiers; int previous, oldX, oldY;
        lock (_owner.Sync)
        {
            if (cleanup ? !OwnsCleanup : !Open(generation, restore)) return TargetedInputSubmission.Rejected;
            previous = Find(_submitted, signal.HoldId); repeat = signal.IsDown && previous >= 0;
            // Reserve possible ownership BEFORE a call that can throw after performing its native side effect.
            if (signal.IsDown && previous < 0) _submitted.Add(signal);
            oldX = _x; oldY = _y;
            if (signal.Kind is not (TargetedInputKind.KeyDown or TargetedInputKind.KeyUp)) { _x = signal.X; _y = signal.Y; }
            modifiers = Modifiers(signal); _sequence++;
        }
        var command = new TargetedInputCommand(_id, _sequence, Target, signal, cleanup, repeat, modifiers);
        TargetedInputSubmission outcome;
        try
        {
            outcome = await _owner.Transport.SubmitAsync(command,
                () => !ct.IsCancellationRequested && (cleanup ? OwnsCleanup : Open(generation, restore)), ct).ConfigureAwait(false);
            if (!Enum.IsDefined(outcome)) outcome = TargetedInputSubmission.Uncertain;
        }
        catch (Exception) { outcome = TargetedInputSubmission.Uncertain; }
        lock (_owner.Sync)
        {
            if (outcome == TargetedInputSubmission.Rejected && signal.IsDown && previous < 0) Remove(_submitted, signal.HoldId);
            if (outcome == TargetedInputSubmission.Rejected) { _x = oldX; _y = oldY; }
            if (outcome == TargetedInputSubmission.Queued)
            {
                if (signal.IsUp) Remove(_submitted, signal.HoldId);
                if (!cleanup && !restore)
                {
                    if (signal.IsDown && Find(_logical, signal.HoldId) < 0) _logical.Add(signal);
                    if (signal.IsUp) Remove(_logical, signal.HoldId);
                }
            }
        }
        return outcome;
    }
    private TargetedInputModifiers Modifiers(TargetedInputSignal pending)
    {
        TargetedInputModifiers value = TargetedInputModifiers.None;
        foreach (var held in _submitted)
        {
            if (pending.IsUp && held.HoldId == pending.HoldId) continue;
            value |= held.HoldId switch { 16 or 160 or 161 => TargetedInputModifiers.Shift, 17 or 162 or 163 => TargetedInputModifiers.Control,
                256 => TargetedInputModifiers.LeftButton, 257 => TargetedInputModifiers.RightButton, 258 => TargetedInputModifiers.MiddleButton, _ => 0 };
        }
        return value;
    }
    private static int Find(List<TargetedInputSignal> values, int id)
    { for (int index = 0; index < values.Count; index++) if (values[index].HoldId == id) return index; return -1; }
    private static void Remove(List<TargetedInputSignal> values, int id)
    { int index = Find(values, id); if (index >= 0) values.RemoveAt(index); }
}
