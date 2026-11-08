namespace Zenith.Sessions.Core;

public enum LaunchPacingReason { None, StartupStabilization, BetweenStarts, ClockUnavailable, RefusalBackoff }

/// <summary>Nonsecret timing only. Ready does not establish admission, authorization, arrival or an unpaused queue.</summary>
public sealed record LaunchPacingSnapshot(Guid? OperationId, TimeSpan Remaining, bool Ready, LaunchPacingReason Reason);

public sealed partial class LaunchCoordinator
{
    // The production settings have the same maxima; zero remains useful for controlled tests.
    private static readonly TimeSpan MaximumPacingStabilization = TimeSpan.FromMinutes(2), MaximumPacingStagger = TimeSpan.FromSeconds(30);
    private PacingGate? _pacing;
    private int _pacingClockFailed;
    // Refusal backoff. A provider's Rejected answer before any client started (StartupRejected, or a
    // provider-reported PreparationRejected) doubles the next gap per refusal in a row, up to 60 s,
    // with up to ±1 s of jitter so retries do not line up. The answer does not say why: the native
    // provider also rejects for its own local faults, a busy provider, an untrusted client and a page
    // the user closed or cancelled, so the pause text names no single cause. A stop or timeout from
    // this coordinator is never counted: its wait ends with an exception, not a Rejected answer (the
    // native provider throws when this coordinator's token is cancelled), and a Rejected answer that
    // lands after a stop is not counted either. Starting a client resets it; so does a refusal that arrives 10 minutes after
    // the previous one. An explicit resume does not: it is permission to continue, not proof the
    // cause is gone. Memory-only like the gate itself: no timer, no persisted deadline. The existing
    // presentation cadence re-signals the queue.
    private static readonly TimeSpan RefusalGapCap = TimeSpan.FromSeconds(60), RefusalJitterBound = TimeSpan.FromSeconds(1),
        RefusalMemory = TimeSpan.FromMinutes(10);
    private const int RefusalsBeforePause = 2, MaximumCountedRefusals = 16;
    private readonly Func<TimeSpan> _refusalJitter;
    private int _refusals; // owned by _serial
    private long _lastRefusal; // owned by _serial
    private volatile LaunchQueuePauseReason _pauseReason;
    private static TimeSpan RandomRefusalJitter() => TimeSpan.FromMilliseconds(Random.Shared.Next(-1000, 1001));

    // Caller owns _serial and writes the refused row next, so the pause is saved in that same write.
    private void RecordRefusalOwned(Guid operationId, TimeSpan stagger)
    {
        long now = _lastRefusal;
        try { now = _time.GetTimestamp(); }
        catch (Exception) { Interlocked.Exchange(ref _pacingClockFailed, 1); }
        if (_refusals > 0 && _time.GetElapsedTime(_lastRefusal, now) >= RefusalMemory) _refusals = 0;
        _refusals = Math.Min(_refusals + 1, MaximumCountedRefusals); _lastRefusal = now;
        BeginPacing(operationId, RefusalGap(stagger, _refusals), LaunchPacingReason.RefusalBackoff);
        // Remaining rows stay Queued; only an explicit resume continues. This is a normal pause, not
        // a stop: it does not advance the stop generation or cancel anything.
        if (_refusals >= RefusalsBeforePause) { _pauseReason = LaunchQueuePauseReason.RepeatedRefusals; _paused = true; }
    }
    // Caller owns _serial. A client was reported started, so the refusal streak has ended.
    private void ForgetRefusalsOwned() => _refusals = 0;
    private TimeSpan RefusalGap(TimeSpan stagger, int refusals)
    {
        // Zero spacing (controlled tests; settings allow 1–30 s) has nothing to double or spread.
        if (stagger <= TimeSpan.Zero) return TimeSpan.Zero;
        long ticks = stagger.Ticks;
        for (int doubled = 0; doubled < refusals && ticks < RefusalGapCap.Ticks; doubled++) ticks *= 2;
        TimeSpan jitter;
        // Jitter only spreads retries apart. A failing injected source falls back to none rather than
        // failing the refused row's queue write; the default source does not throw.
        try { jitter = _refusalJitter(); }
        catch (Exception) { jitter = TimeSpan.Zero; }
        if (jitter > RefusalJitterBound) jitter = RefusalJitterBound;
        else if (jitter < -RefusalJitterBound) jitter = -RefusalJitterBound;
        var gap = TimeSpan.FromTicks(Math.Min(ticks, RefusalGapCap.Ticks)) + jitter;
        return gap < stagger ? stagger : gap;
    }

    /// <summary>
    /// Thread-safe immutable observation for the existing shared presentation cadence. It never
    /// starts a timer/worker, persists a deadline, resumes a queue or changes a launch state.
    /// A backwards/failed monotonic observation remains fail-closed until this coordinator ends.
    /// </summary>
    public LaunchPacingSnapshot ReadPacing()
    {
        var gate = Volatile.Read(ref _pacing);
        if (Volatile.Read(ref _pacingClockFailed) != 0) return Unavailable(gate);
        if (gate is null) return new(null, TimeSpan.Zero, true, LaunchPacingReason.None);
        TimeSpan elapsed;
        try
        {
            elapsed = _time.GetElapsedTime(gate.Started, _time.GetTimestamp());
            if (elapsed < TimeSpan.Zero) throw new InvalidOperationException("The monotonic pacing clock moved backwards.");
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref _pacingClockFailed, 1); return Unavailable(gate);
        }
        if (Volatile.Read(ref _pacingClockFailed) != 0) return Unavailable(gate);
        if (elapsed >= gate.Duration) return new(gate.OperationId, TimeSpan.Zero, true, LaunchPacingReason.None);
        return new(gate.OperationId, gate.Duration - elapsed, false, gate.Reason);
    }

    private void BeginPacing(Guid operationId, TimeSpan duration, LaunchPacingReason reason)
        => BeginStartupPacing(operationId, duration, reason, TimeSpan.Zero, 0, 0);
    // The gate after a reported start (ClientStartedUnverified) also remembers the receipt it waits for and the spacing
    // floor, so a later join report for exactly that process may shorten it (ReleasePacingEarly). Other gates carry none.
    private void BeginStartupPacing(Guid operationId, TimeSpan duration, LaunchPacingReason reason, TimeSpan floor, int processId, long processStartUtcTicks)
    {
        long started = 0;
        try { started = _time.GetTimestamp(); }
        catch (Exception) { Interlocked.Exchange(ref _pacingClockFailed, 1); }
        Volatile.Write(ref _pacing, new(operationId, started, duration, reason, floor, processId, processStartUtcTicks));
    }

    /// <summary>
    /// R-launch-2 (owner decision 2026-11-08). The started client the current stabilization gate waits for has reported,
    /// in its own log, that it joined: <paramref name="evidence"/> holds a Connected, unambiguous, fully read entry for
    /// exactly the receipt's PID and creation time. Shortens that wait to the launch spacing measured from the start
    /// (never below it, never beyond the full stabilization) and wakes the queue. The report is advisory: it is not an
    /// account, arrival or admission fact, so the next start still needs a fresh exact observation of the started client
    /// and healthy admission, and one start at a time stays. No gate, a different process, a refusal backoff, a
    /// verified-arrival stabilization, a spacing that is already the longer wait or a failed clock changes nothing.
    /// Returns true when the wait was shortened.
    /// </summary>
    public bool ReleasePacingEarly(IReadOnlyList<SessionEvidenceSnapshot> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var gate = Volatile.Read(ref _pacing);
        if (gate is not { Reason: LaunchPacingReason.StartupStabilization, ProcessId: > 0 }) return false;
        var key = new SessionProcessKey(null, gate.ProcessId, gate.ProcessStartUtcTicks);
        foreach (var item in evidence)
            if (item is { Lifecycle: ObservedSessionLifecycle.Connected, Ambiguous: false, AwaitingLogTail: false, CurrentDestination: not null } && item.Process == key)
                return ReleasePacingEarly(gate.OperationId, gate.ProcessId, gate.ProcessStartUtcTicks);
        return false;
    }

    /// <summary>The same release for one reported process; see the evidence overload. Thread-safe against a newer gate.</summary>
    public bool ReleasePacingEarly(Guid operationId, int processId, long processStartUtcTicks)
    {
        if (processId <= 0 || processStartUtcTicks <= 0) return false;
        while (true)
        {
            var gate = Volatile.Read(ref _pacing);
            if (gate is null || Volatile.Read(ref _pacingClockFailed) != 0 || gate.Reason != LaunchPacingReason.StartupStabilization
                || gate.OperationId != operationId || gate.ProcessId != processId || gate.ProcessStartUtcTicks != processStartUtcTicks) return false;
            TimeSpan elapsed;
            try
            {
                elapsed = _time.GetElapsedTime(gate.Started, _time.GetTimestamp());
                if (elapsed < TimeSpan.Zero) throw new InvalidOperationException("The monotonic pacing clock moved backwards.");
            }
            catch (Exception) { Interlocked.Exchange(ref _pacingClockFailed, 1); return false; }
            // The spacing is the floor, the stabilization the ceiling; the gate is already over if neither applies.
            var duration = elapsed > gate.Floor ? elapsed : gate.Floor;
            if (duration >= gate.Duration) return false;
            // The remaining wait is launch spacing now; the receipt is dropped so the same report cannot shorten it twice.
            var shortened = gate with { Duration = duration, Reason = LaunchPacingReason.BetweenStarts, ProcessId = 0, ProcessStartUtcTicks = 0 };
            if (!ReferenceEquals(Interlocked.CompareExchange(ref _pacing, shortened, gate), gate)) continue;
            Signal(); return true;
        }
    }
    private static LaunchPacingSnapshot Unavailable(PacingGate? gate)
        => new(gate?.OperationId, gate?.Duration ?? TimeSpan.Zero, false, LaunchPacingReason.ClockUnavailable);
    // Floor, ProcessId and ProcessStartUtcTicks are set only by BeginStartupPacing (a reported start); zero elsewhere.
    private sealed record PacingGate(Guid OperationId, long Started, TimeSpan Duration, LaunchPacingReason Reason,
        TimeSpan Floor = default, int ProcessId = 0, long ProcessStartUtcTicks = 0);
}
