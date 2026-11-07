namespace Zenith.Sessions.Core;

/// <summary>One Settings choice for the optional idle lock. <see cref="Minutes"/> 0 is Off.</summary>
public sealed record IdleLockChoice(int Minutes, string Label);

/// <summary>What the idle-lock timer should do when it wakes.</summary>
public enum IdleLockStep { Off, Wait, Lock }

/// <summary>
/// The optional idle lock: how long the manager may go without keyboard or mouse input before it locks itself the same way
/// as Lock. Off by default. Plain state over an injected clock, so it is tested without a screen, mouse or keyboard; the
/// shell feeds it input and owns the one timer that asks it when to look again. Not thread-safe: use it from one thread.
/// </summary>
public sealed class IdleLockPolicy
{
    public const int MaximumMinutes = 1440;
    /// <summary>The shortest wait the timer is given, so a timer that wakes a moment early never spins.</summary>
    public static readonly TimeSpan MinimumWait = TimeSpan.FromSeconds(1);
    private static readonly IReadOnlyList<IdleLockChoice> Standard = Array.AsReadOnly(new[] { 0, 5, 10, 15, 30, 60 }.Select(Choice).ToArray());
    private readonly TimeProvider _clock;
    private long _lastActivity;
    private bool _hasPointer;
    private double _pointerX, _pointerY;

    public IdleLockPolicy(TimeProvider? clock = null) { _clock = clock ?? TimeProvider.System; _lastActivity = _clock.GetTimestamp(); }

    /// <summary>The configured idle time in minutes; 0 when the lock is off.</summary>
    public int Minutes { get; private set; }

    /// <summary>Whole minutes from 0 (Off) to one day. A saved file may hold any of them; Settings offers <see cref="ChoicesFor"/>.</summary>
    public static bool IsValidMinutes(int minutes) => minutes is >= 0 and <= MaximumMinutes;

    /// <summary>"5 minutes" or "1 minute".</summary>
    public static string Duration(int minutes) => minutes == 1 ? "1 minute" : $"{minutes} minutes";

    /// <summary>The Settings choices: Off and a few common times, plus <paramref name="saved"/> when it is valid and not among them.</summary>
    public static IReadOnlyList<IdleLockChoice> ChoicesFor(int saved)
    {
        if (!IsValidMinutes(saved) || Standard.Any(c => c.Minutes == saved)) return Standard;
        return Array.AsReadOnly(Standard.Append(Choice(saved)).OrderBy(c => c.Minutes).ToArray());
    }

    /// <summary>Turns the lock on for <paramref name="minutes"/> (0 turns it off) and starts counting from now.</summary>
    public void Configure(int minutes)
    {
        if (!IsValidMinutes(minutes)) throw new ArgumentOutOfRangeException(nameof(minutes), "The idle lock time must be 0 (off) to 1440 minutes.");
        Minutes = minutes; RecordActivity();
    }

    /// <summary>A key, mouse button or wheel reached a manager window, or the user reopened it.</summary>
    public void RecordActivity() => _lastActivity = _clock.GetTimestamp();

    /// <summary>
    /// The pointer moved over a manager window to this position (relative to that window). Only a change counts: WPF repeats a
    /// still pointer's last position when the layout under it changes, and that must not keep the manager unlocked.
    /// </summary>
    public void RecordPointer(double x, double y)
    {
        if (_hasPointer && x == _pointerX && y == _pointerY) return;
        _hasPointer = true; _pointerX = x; _pointerY = y; RecordActivity();
    }

    /// <summary>
    /// Called when the timer wakes. <see cref="IdleLockStep.Lock"/> once the whole idle time has passed without input;
    /// otherwise <see cref="IdleLockStep.Wait"/> with how long until it could (at least <see cref="MinimumWait"/>), or
    /// <see cref="IdleLockStep.Off"/>. <paramref name="busy"/> is work the user is doing outside the manager's own windows
    /// (a sign-in or native trial page it opened): it counts as activity, so the count restarts instead of a lock cancelling it.
    /// </summary>
    public IdleLockStep Check(bool busy, out TimeSpan wait)
    {
        wait = TimeSpan.Zero;
        if (Minutes == 0) return IdleLockStep.Off;
        var timeout = TimeSpan.FromMinutes(Minutes);
        if (busy) RecordActivity();
        var idle = _clock.GetElapsedTime(_lastActivity);
        if (idle >= timeout) return IdleLockStep.Lock;
        wait = timeout - idle < MinimumWait ? MinimumWait : timeout - idle;
        return IdleLockStep.Wait;
    }

    private static IdleLockChoice Choice(int minutes) => new(minutes, minutes == 0 ? "Off" : "After " + Duration(minutes));
}
