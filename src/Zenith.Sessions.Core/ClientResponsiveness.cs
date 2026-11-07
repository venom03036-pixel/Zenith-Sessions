namespace Zenith.Sessions.Core;

/// <summary>
/// Turns per-sample hung-window evidence (<see cref="SessionObservation.WindowHung"/>) into a stable
/// <see cref="SessionObservation.NotResponding"/> state. Windows itself reports a window as hung only after
/// about five seconds without message processing; this adds consecutive-sample hysteresis so one late sample
/// cannot flip the shown state. A window that could not be checked is never called frozen.
/// Caller-serialized: one call per accepted process enumeration. No IO, timers or native calls. State is kept
/// only for clients present in the latest call, so it is bounded by the caller's inventory budget.
/// </summary>
public sealed class ClientResponsivenessReducer
{
    public const int HungSamplesBeforeNotResponding = 3, RespondingSamplesBeforeClear = 2;
    private readonly record struct Streak(int Hung, int Responding, bool NotResponding);
    private Dictionary<SessionProcessKey, Streak> _current = [], _next = [];

    public SessionObservation[] Apply(IReadOnlyList<SessionObservation> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var result = new SessionObservation[sessions.Count];
        _next.Clear();
        for (int index = 0; index < sessions.Count; index++)
        {
            var row = sessions[index] ?? throw new ArgumentException("A client observation was missing.", nameof(sessions));
            var key = new SessionProcessKey(null, row.ProcessId, row.ProcessStartUtcTicks);
            bool notResponding = false;
            // Unavailable rows, unchecked windows and duplicate identities carry no responsiveness evidence.
            if (row.Problem is null && row.WindowHung is { } hung && key.IsValid && !_next.ContainsKey(key))
            {
                _current.TryGetValue(key, out var streak);
                streak = hung
                    ? new(Math.Min(streak.Hung + 1, HungSamplesBeforeNotResponding), 0,
                        streak.NotResponding || streak.Hung + 1 >= HungSamplesBeforeNotResponding)
                    : new(0, Math.Min(streak.Responding + 1, RespondingSamplesBeforeClear),
                        streak.NotResponding && streak.Responding + 1 < RespondingSamplesBeforeClear);
                _next[key] = streak; notResponding = streak.NotResponding;
            }
            result[index] = row.NotResponding == notResponding ? row : row with { NotResponding = notResponding };
        }
        (_current, _next) = (_next, _current);
        _next.Clear();
        return result;
    }

    public void Clear() { _current.Clear(); _next.Clear(); }
}
