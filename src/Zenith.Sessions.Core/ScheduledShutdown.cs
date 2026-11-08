using System.Globalization;

namespace Zenith.Sessions.Core;

/// <summary>One Settings choice for the scheduled shutdown delay.</summary>
public sealed record ShutdownDelayChoice(int Minutes, string Label);

/// <summary>What Windows' shutdown tool answered, read from its exit code only.</summary>
public enum ShutdownScheduleStatus { Scheduled, Cancelled, AlreadyScheduled, NothingScheduled, AccessDenied, NotConfirmed, Busy, Closed }

/// <summary>
/// The outcome of one schedule or cancel request. <see cref="ErrorCode"/> is the tool's exit code when it is one Zenith does not
/// recognise; a number only, never the tool's text (Rule 10).
/// </summary>
public sealed record ShutdownScheduleResult(ShutdownScheduleStatus Status, int? ErrorCode = null)
{
    public bool Succeeded => Status is ShutdownScheduleStatus.Scheduled or ShutdownScheduleStatus.Cancelled;
    public string Explanation => Status switch
    {
        ShutdownScheduleStatus.Scheduled => "Windows accepted the shutdown schedule.",
        ShutdownScheduleStatus.Cancelled => "Windows cancelled the scheduled shutdown.",
        ShutdownScheduleStatus.AlreadyScheduled => "Windows already has a shutdown scheduled, by another program or an earlier request, so nothing was changed.",
        ShutdownScheduleStatus.NothingScheduled => "Windows had no shutdown scheduled, so there was nothing to cancel.",
        ShutdownScheduleStatus.AccessDenied => "Windows refused the request (access denied).",
        ShutdownScheduleStatus.Busy => "Another shutdown request is still running.",
        ShutdownScheduleStatus.Closed => "Scheduled shutdown is closed.",
        _ => ErrorCode is { } code
            ? $"Windows did not confirm the request (shutdown tool exit code {code.ToString(CultureInfo.InvariantCulture)})."
            : "Windows did not confirm the request (its shutdown tool did not finish in time)."
    };
}

/// <summary>
/// The scheduled Windows shutdown: an explicit action, never a saved preference, so nothing is scheduled unless the user asks
/// in this run. Plain arithmetic and text; the shell owns the one countdown timer and the Windows layer runs the tool.
/// </summary>
public static class ScheduledShutdownPolicy
{
    /// <summary>One day: long enough for any overnight run, short enough that a forgotten schedule is noticed.</summary>
    public const int MaximumMinutes = 1440;
    public const int DefaultMinutes = 30;
    /// <summary>How often the visible countdown is redrawn while a shutdown is scheduled; nothing ticks otherwise.</summary>
    public static readonly TimeSpan CountdownTick = TimeSpan.FromSeconds(1);
    public static IReadOnlyList<ShutdownDelayChoice> Choices { get; } =
        Array.AsReadOnly(new[] { 5, 10, 15, 30, 45, 60, 90, 120, 180, 240 }.Select(minutes => new ShutdownDelayChoice(minutes, "After " + Duration(minutes))).ToArray());

    /// <summary>Whole minutes from 1 to one day.</summary>
    public static bool IsValidMinutes(int minutes) => minutes is >= 1 and <= MaximumMinutes;

    /// <summary>"5 minutes", "1 hour", "1 hour 30 minutes", "2 hours".</summary>
    public static string Duration(int minutes)
    {
        int hours = minutes / 60, rest = minutes % 60;
        string h = hours == 1 ? "1 hour" : $"{hours} hours", m = rest == 1 ? "1 minute" : $"{rest} minutes";
        return hours == 0 ? m : rest == 0 ? h : h + " " + m;
    }

    /// <summary>"1 h 29 min 58 s", "29 min 58 s", "45 s", or "now". Part seconds round up, so 0 s is never shown while time is left.</summary>
    public static string Countdown(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return "now";
        long seconds = (long)Math.Ceiling(remaining.TotalSeconds);
        long hours = seconds / 3600, minutes = seconds % 3600 / 60, rest = seconds % 60;
        return hours > 0 ? $"{hours} h {minutes} min {rest} s" : minutes > 0 ? $"{minutes} min {rest} s" : $"{rest} s";
    }
}

/// <summary>
/// Asks Windows to shut this PC down after a delay, or to call a scheduled shutdown off, through Windows' own shutdown tool.
/// One request runs at a time; a second is answered <see cref="ShutdownScheduleStatus.Busy"/>. Cancel works on any scheduled
/// shutdown, whoever asked for it, because that is what the tool does. Nothing here persists: a schedule lives in Windows.
/// </summary>
public interface IShutdownScheduler : IAsyncDisposable
{
    Task<ShutdownScheduleResult> ScheduleAsync(TimeSpan delay, CancellationToken ct);
    Task<ShutdownScheduleResult> CancelAsync(CancellationToken ct);
}
