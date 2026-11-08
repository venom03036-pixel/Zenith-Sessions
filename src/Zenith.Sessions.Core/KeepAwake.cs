namespace Zenith.Sessions.Core;

/// <summary>One Settings choice for Keep clients awake. <see cref="Minutes"/> 0 is Off.</summary>
public sealed record KeepAwakeChoice(int Minutes, string Label);

/// <summary>What the keep-awake timer should do when it wakes.</summary>
public enum KeepAwakeStep { Off, Refused, Defer, Press }

/// <summary>
/// Keep clients awake (R-instances-17, owner decision 2026-11-08): every N minutes, bring each client forward, send it one F13 press
/// through the one targeted input owner and restore the window that was in front. Off by default. Roblox disconnects a client after
/// 20 idle minutes, so the gap is bounded to 5-19 minutes. Pure decisions over values the shell reads; it owns the one timer.
/// </summary>
public static class KeepAwakePolicy
{
    public const int DefaultMinutes = 15, MinimumMinutes = 5, MaximumMinutes = 19;
    /// <summary>The user must have left the keyboard and mouse alone this long before a press takes the foreground.</summary>
    public static readonly TimeSpan UserIdleThreshold = TimeSpan.FromSeconds(10);
    /// <summary>How long a press waits for the user to go idle before it looks again.</summary>
    public static readonly TimeSpan Deferral = TimeSpan.FromSeconds(15);
    private static readonly IReadOnlyList<KeepAwakeChoice> Standard = Array.AsReadOnly(new[] { 0, 5, 10, DefaultMinutes }.Select(Choice).ToArray());

    /// <summary>0 (Off) or a whole number of minutes from <see cref="MinimumMinutes"/> to <see cref="MaximumMinutes"/>.</summary>
    public static bool IsValidMinutes(int minutes) => minutes == 0 || minutes is >= MinimumMinutes and <= MaximumMinutes;

    /// <summary>"5 minutes" or "1 minute".</summary>
    public static string Duration(int minutes) => minutes == 1 ? "1 minute" : $"{minutes} minutes";

    /// <summary>The Settings choices: Off and a few common gaps, plus <paramref name="saved"/> when it is valid and not among them.</summary>
    public static IReadOnlyList<KeepAwakeChoice> ChoicesFor(int saved)
    {
        if (!IsValidMinutes(saved) || Standard.Any(c => c.Minutes == saved)) return Standard;
        return Array.AsReadOnly(Standard.Append(Choice(saved)).OrderBy(c => c.Minutes).ToArray());
    }

    /// <summary>
    /// What the timer does when it wakes. <see cref="KeepAwakeStep.Off"/> for 0 minutes; <see cref="KeepAwakeStep.Refused"/> when the
    /// capability gate does not permit the feature; <see cref="KeepAwakeStep.Defer"/> with <see cref="Deferral"/> while the user used the
    /// keyboard or mouse within <see cref="UserIdleThreshold"/>, so the foreground is never taken from them; otherwise
    /// <see cref="KeepAwakeStep.Press"/> with the full gap as the next wait. Idle time Windows could not report (null) does not stop
    /// a press: a missed press is a disconnected client.
    /// </summary>
    public static KeepAwakeStep Decide(int minutes, bool permitted, TimeSpan? userIdleFor, out TimeSpan wait)
    {
        if (!IsValidMinutes(minutes)) throw new ArgumentOutOfRangeException(nameof(minutes), "The keep-awake gap must be 0 (off) or 5 to 19 minutes.");
        wait = TimeSpan.Zero;
        if (minutes == 0) return KeepAwakeStep.Off;
        if (!permitted) return KeepAwakeStep.Refused;
        if (userIdleFor is { } idle && idle < UserIdleThreshold) { wait = Deferral; return KeepAwakeStep.Defer; }
        wait = TimeSpan.FromMinutes(minutes);
        return KeepAwakeStep.Press;
    }

    private static KeepAwakeChoice Choice(int minutes) => new(minutes, minutes == 0 ? "Off" : "Every " + Duration(minutes));
}

/// <summary>Reads and requests the foreground window. Windows may refuse a request; the answer says whether the window is in front afterwards.</summary>
public interface IForegroundWindowSwitch
{
    nint Current { get; }
    /// <summary>Asks Windows to bring <paramref name="window"/> to the front; true only when it is the foreground window afterwards.</summary>
    bool BringForward(nint window);
}

/// <summary>How long ago the user last used the keyboard or mouse anywhere; null when Windows could not say.</summary>
public interface IUserInputIdleSource
{
    TimeSpan? IdleFor { get; }
}

/// <summary>
/// The one input executor composition the keep-awake plan may send through: the shared targeted input owner and the verified
/// transport's registration of a client window as a target. Production has none until the live input isolation trial supplies a
/// verified transport (Z1702), and the <see cref="CapabilityGate"/> refuses the plan before this is consulted.
/// </summary>
public interface IKeepAwakeInputExecutor
{
    TargetedInputCoordinator Coordinator { get; }
    /// <summary>A registered target for this exact client incarnation, or null when its window cannot be registered.</summary>
    TargetedInputTarget? Register(SessionObservation client);
}

public enum KeepAwakeOutcome { Pressed, NotRegistered, LeaseRefused, NotInFront, InputRejected, Cancelled }

public sealed record KeepAwakeClientResult(SessionProcessKey Client, KeepAwakeOutcome Outcome, string Explanation);

/// <summary>One tick's outcome per client, in the order given, plus whether the earlier foreground window is back in front.</summary>
public sealed record KeepAwakeTickResult(IReadOnlyList<KeepAwakeClientResult> Clients, int LeftForNextTick, nint PreviousForeground, bool ForegroundRestored)
{
    public int Pressed => Count(KeepAwakeOutcome.Pressed);
    private int Count(KeepAwakeOutcome outcome) { int n = 0; foreach (var row in Clients) if (row.Outcome == outcome) n++; return n; }

    public string Summary
    {
        get
        {
            int total = Clients.Count;
            if (total == 0) return "No client to keep awake.";
            var parts = new List<string>(5);
            Add(parts, Count(KeepAwakeOutcome.NotInFront), "could not be brought forward", "could not be brought forward");
            Add(parts, Count(KeepAwakeOutcome.LeaseRefused), "is owned by another input run", "are owned by other input runs");
            Add(parts, Count(KeepAwakeOutcome.InputRejected), "refused the key", "refused the key");
            Add(parts, Count(KeepAwakeOutcome.NotRegistered), "could not be registered", "could not be registered");
            Add(parts, Count(KeepAwakeOutcome.Cancelled), "was skipped because the press was stopped", "were skipped because the press was stopped");
            int pressed = Pressed;
            string text = pressed == total ? $"Sent one F13 press to {Plural(total, "client")}."
                : $"Sent one F13 press to {pressed} of {Plural(total, "client")}: {string.Join(", ", parts)}.";
            if (LeftForNextTick > 0) text += $" {LeftForNextTick} more {(LeftForNextTick == 1 ? "client waits" : "clients wait")} for the next press.";
            if (PreviousForeground != 0 && !ForegroundRestored) text += " The window that was in front before could not be brought back.";
            return text;
        }
    }
    private static void Add(List<string> parts, int count, string one, string many)
    { if (count > 0) parts.Add(count + " " + (count == 1 ? one : many)); }
    private static string Plural(int count, string noun) => count == 1 ? "1 " + noun : count + " " + noun + "s";
}

/// <summary>
/// One keep-awake tick. For each client, in order and one at a time: register its window as a target, open a lease on the one
/// input owner, bring the window forward, send F13 down and up through that lease, release the lease. A client whose window is not
/// in front after the request gets no key, so a focus change can never deliver F13 to another program; a window another run owns is
/// left to that run. The window that was in front before the tick is brought back at the end whatever happened. No fallback to
/// global input, no polling, no retry; at most <see cref="MaximumClients"/> clients per tick (Rule 18).
/// </summary>
public sealed class KeepAwakeRunner
{
    public const int MaximumClients = 32;
    public const int F13VirtualKey = 0x7C;
    private static readonly TargetedInputSignal[] Press = [new(TargetedInputKind.KeyDown, F13VirtualKey), new(TargetedInputKind.KeyUp, F13VirtualKey)];
    private readonly IKeepAwakeInputExecutor _executor;
    private readonly IForegroundWindowSwitch _foreground;
    private int _nextClientIndex;

    public KeepAwakeRunner(IKeepAwakeInputExecutor executor, IForegroundWindowSwitch foreground)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
    }

    public async Task<KeepAwakeTickResult> RunAsync(IReadOnlyList<SessionObservation> clients, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clients);
        int count = Math.Min(clients.Count, MaximumClients);
        int start = clients.Count == 0 ? 0 : _nextClientIndex % clients.Count;
        _nextClientIndex = clients.Count == 0 ? 0 : (start + count) % clients.Count;
        var rows = new KeepAwakeClientResult[count];
        nint previous;
        try { previous = _foreground.Current; } catch (Exception) { previous = 0; }
        bool stopped = false, restored = false;
        try
        {
            for (int index = 0; index < count; index++)
            {
                var client = clients[(start + index) % clients.Count];
                if (stopped || ct.IsCancellationRequested)
                {
                    stopped = true;
                    rows[index] = new(client.ProcessKey, KeepAwakeOutcome.Cancelled, "Skipped: the press was stopped before this client's turn.");
                    continue;
                }
                rows[index] = await PressAsync(client, ct).ConfigureAwait(false);
                if (rows[index].Outcome == KeepAwakeOutcome.Cancelled) stopped = true;
            }
        }
        finally
        {
            // Whatever happened, the window that was in front before the tick is asked back; a refusal is reported, never retried.
            if (previous != 0)
                try { restored = _foreground.Current == previous || _foreground.BringForward(previous); }
                catch (Exception) { restored = false; }
        }
        return new(Array.AsReadOnly(rows), clients.Count - count, previous, restored);
    }

    private async Task<KeepAwakeClientResult> PressAsync(SessionObservation client, CancellationToken ct)
    {
        var key = client.ProcessKey;
        TargetedInputTarget? target;
        try { target = _executor.Register(client); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { return new(key, KeepAwakeOutcome.Cancelled, "Stopped while registering this client's window; no key was sent."); }
        catch (Exception error)
        { return new(key, KeepAwakeOutcome.NotRegistered, $"This client's window could not be registered ({error.GetType().Name}); no key was sent."); }
        if (target is not { IsValid: true })
            return new(key, KeepAwakeOutcome.NotRegistered, "This client's window could not be registered with the input transport, so no key was sent to it.");
        var opened = _executor.Coordinator.TryOpen(target, TargetedInputPurpose.KeepAwake);
        if (opened.Run is not { } run) return new(key, KeepAwakeOutcome.LeaseRefused, opened.Explanation);
        KeepAwakeClientResult result;
        var release = TargetedInputStopReason.NotStarted;
        try
        {
            if (ct.IsCancellationRequested)
                result = new(key, KeepAwakeOutcome.Cancelled, "Stopped before this client was brought forward; no key was sent to it.");
            else if (!_foreground.BringForward(target.Window))
                result = new(key, KeepAwakeOutcome.NotInFront, "Windows did not bring this client's window to the front, so no key was sent to it.");
            else
            {
                var dispatch = await run.SubmitAsync(Press, ct).ConfigureAwait(false);
                if (dispatch.Status == TargetedInputDispatchStatus.Submitted && dispatch.SubmittedCount == Press.Length)
                { result = new(key, KeepAwakeOutcome.Pressed, "One F13 press was sent to this client through the input owner."); release = TargetedInputStopReason.KeepAwakeSent; }
                else if (dispatch.Status == TargetedInputDispatchStatus.Cancelled || ct.IsCancellationRequested)
                    result = new(key, KeepAwakeOutcome.Cancelled, "Stopped while this client's press was being sent. " + dispatch.Run.Explanation);
                else
                { result = new(key, KeepAwakeOutcome.InputRejected, "The input owner did not send the key to this client. " + dispatch.Run.Explanation); release = TargetedInputStopReason.AfterFailure; }
            }
        }
        catch (OperationCanceledException)
        { result = new(key, KeepAwakeOutcome.Cancelled, "Stopped while this client's press was being sent."); }
        catch (Exception error)
        {
            // Type only (Rule 10): a transport or window error can carry a title or a path.
            result = new(key, KeepAwakeOutcome.InputRejected, $"The press to this client failed ({error.GetType().Name}).");
            release = TargetedInputStopReason.AfterFailure;
        }
        // The lease is released whatever happened. A release the owner could not complete keeps the window reserved, and says so.
        TargetedInputRunSnapshot stopped;
        try { stopped = await run.StopAsync(release).ConfigureAwait(false); }
        catch (Exception error)
        { return result with { Explanation = result.Explanation + $" Releasing its input lease failed ({error.GetType().Name}); the window stays reserved." }; }
        if (stopped.State != TargetedInputRunState.Stopped)
            return result with { Explanation = result.Explanation + " Its input lease could not be released and the window stays reserved: " + stopped.Explanation };
        return result;
    }
}
