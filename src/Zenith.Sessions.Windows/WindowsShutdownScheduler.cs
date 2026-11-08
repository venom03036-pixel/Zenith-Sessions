using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// Schedules and cancels a Windows shutdown through Windows' own tool, shutdown.exe in the System32 folder (never found through
/// PATH), with <c>/s /t seconds</c> and <c>/a</c>, as the owner approved; this process takes no shutdown privilege of its own.
/// Only the tool's exit code is read: 0 accepted, 5 access denied, 1190 and 1115 a shutdown already scheduled or under way,
/// 1116 nothing to cancel; any other code is reported as not confirmed with the code, never with the tool's text (Rule 10).
/// One request runs at a time and a second is answered Busy; disposal waits for the running one (the runner bounds it) and
/// refuses later ones. A schedule lives in Windows, not here: nothing is persisted.
/// </summary>
public sealed class WindowsShutdownScheduler : IShutdownScheduler
{
    /// <summary>shutdown.exe answers at once; a tool still running after this is stopped and reported as not confirmed.</summary>
    public static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(10);
    /// <summary>Shown by Windows in its own shutdown notice, so the user knows where the schedule came from and how to undo it.</summary>
    internal const string Comment = "Scheduled from Zenith Sessions. Cancel it there, or run shutdown /a.";
    private const int ErrorAccessDenied = 5, ErrorShutdownInProgress = 1115, ErrorNoShutdownInProgress = 1116, ErrorShutdownIsScheduled = 1190;
    private readonly IShutdownToolRunner _runner;
    private readonly string _tool;
    private readonly object _gate = new();
    private Task<ShutdownScheduleResult>? _active;
    private Task? _dispose;

    public WindowsShutdownScheduler() : this(new ShutdownToolProcessRunner()) { }
    internal WindowsShutdownScheduler(IShutdownToolRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _tool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
    }

    public Task<ShutdownScheduleResult> ScheduleAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero || delay > TimeSpan.FromMinutes(ScheduledShutdownPolicy.MaximumMinutes))
            throw new ArgumentOutOfRangeException(nameof(delay), "The shutdown delay must be more than zero and at most one day.");
        // Rounded up, so Windows never shuts down earlier than the time shown.
        string seconds = ((long)Math.Ceiling(delay.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return RunAsync(["/s", "/t", seconds, "/c", Comment], schedule: true, ct);
    }

    public Task<ShutdownScheduleResult> CancelAsync(CancellationToken ct) => RunAsync(["/a"], schedule: false, ct);

    private Task<ShutdownScheduleResult> RunAsync(string[] arguments, bool schedule, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_dispose is not null) return Task.FromResult(new ShutdownScheduleResult(ShutdownScheduleStatus.Closed));
            if (_active is { IsCompleted: false }) return Task.FromResult(new ShutdownScheduleResult(ShutdownScheduleStatus.Busy));
            return _active = RunCoreAsync(arguments, schedule, ct);
        }
    }

    // The runner keeps process creation off the caller's thread; a fault starting the tool reaches the caller unchanged.
    private async Task<ShutdownScheduleResult> RunCoreAsync(string[] arguments, bool schedule, CancellationToken ct)
        => Interpret(await _runner.RunAsync(_tool, arguments, ToolTimeout, ct).ConfigureAwait(false), schedule);

    internal static ShutdownScheduleResult Interpret(int? exitCode, bool schedule) => exitCode switch
    {
        null => new(ShutdownScheduleStatus.NotConfirmed),
        0 => new(schedule ? ShutdownScheduleStatus.Scheduled : ShutdownScheduleStatus.Cancelled),
        ErrorAccessDenied => new(ShutdownScheduleStatus.AccessDenied),
        ErrorShutdownIsScheduled or ErrorShutdownInProgress when schedule => new(ShutdownScheduleStatus.AlreadyScheduled),
        ErrorNoShutdownInProgress when !schedule => new(ShutdownScheduleStatus.NothingScheduled),
        { } code => new(ShutdownScheduleStatus.NotConfirmed, code)
    };

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            // Disposal only waits for the request under way; its result and any fault still reach that request's caller.
            _dispose ??= _active is { IsCompleted: false } active
                ? active.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                : Task.CompletedTask;
            return new(_dispose);
        }
    }
}

/// <summary>Runs one command-line tool to completion. Replaced by a fake in tests, so no test ever schedules a real shutdown.</summary>
internal interface IShutdownToolRunner
{
    /// <summary>
    /// The tool's exit code, or null when it was still running at <paramref name="timeout"/> (it is then stopped). Throws when
    /// the tool cannot start (for example <see cref="Win32Exception"/>) or when <paramref name="ct"/> is cancelled first.
    /// </summary>
    Task<int?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
}

internal sealed class ShutdownToolProcessRunner : IShutdownToolRunner
{
    public async Task<int?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        // Process creation stays off the caller's thread (the manager's UI thread). Nothing the tool prints is read or kept.
        using var process = await Task.Run(() => Process.Start(start), ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Windows did not start the requested tool.");
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(timeout);
        try { await process.WaitForExitAsync(bound.Token).ConfigureAwait(false); return process.ExitCode; }
        catch (OperationCanceledException) when (bound.IsCancellationRequested)
        {
            // Still running: stop it and anything it started, then report no exit code, or the caller's own cancellation.
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // It exited on its own in the meantime.
            catch (Win32Exception) { } // Windows refused to stop it; the caller is told the result is not confirmed.
            if (ct.IsCancellationRequested) throw;
            return null;
        }
    }
}
