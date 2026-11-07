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
    // provider also rejects for local faults, busy, trust and cancel outcomes, so the pause text names
    // no single cause. Starting a client resets it; so does a refusal that arrives 10 minutes after
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
    {
        long started = 0;
        try { started = _time.GetTimestamp(); }
        catch (Exception) { Interlocked.Exchange(ref _pacingClockFailed, 1); }
        Volatile.Write(ref _pacing, new(operationId, started, duration, reason));
    }
    private static LaunchPacingSnapshot Unavailable(PacingGate? gate)
        => new(gate?.OperationId, gate?.Duration ?? TimeSpan.Zero, false, LaunchPacingReason.ClockUnavailable);
    private sealed record PacingGate(Guid OperationId, long Started, TimeSpan Duration, LaunchPacingReason Reason);
}
