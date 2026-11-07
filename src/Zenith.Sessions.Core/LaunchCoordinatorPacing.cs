namespace Zenith.Sessions.Core;

public enum LaunchPacingReason { None, StartupStabilization, BetweenStarts, ClockUnavailable }

/// <summary>Nonsecret timing only. Ready does not establish admission, authorization, arrival or an unpaused queue.</summary>
public sealed record LaunchPacingSnapshot(Guid? OperationId, TimeSpan Remaining, bool Ready, LaunchPacingReason Reason);

public sealed partial class LaunchCoordinator
{
    // The production settings have the same maxima; zero remains useful for controlled tests.
    private static readonly TimeSpan MaximumPacingStabilization = TimeSpan.FromMinutes(2), MaximumPacingStagger = TimeSpan.FromSeconds(30);
    private PacingGate? _pacing;
    private int _pacingClockFailed;

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
