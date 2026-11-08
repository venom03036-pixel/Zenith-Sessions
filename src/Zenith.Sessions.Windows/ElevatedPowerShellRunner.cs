using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Zenith.Sessions.Windows;

internal enum ElevatedRunKind
{
    /// <summary>The run finished; <see cref="ElevatedRunOutcome.ExitCode"/> is its exit code.</summary>
    Exited,
    /// <summary>The user declined Windows' administrator prompt; nothing ran.</summary>
    Declined,
    /// <summary>Windows PowerShell could not be started; nothing ran.</summary>
    NotStarted,
    /// <summary>The run did not finish within the wait; it may still finish on its own.</summary>
    TimedOut
}

internal readonly record struct ElevatedRunOutcome(ElevatedRunKind Kind, int ExitCode = 0);

/// <summary>Runs one fixed script through Windows PowerShell as administrator and reports how it ended. Tests inject a fake.</summary>
internal interface IElevatedPowerShellRunner
{
    /// <summary>
    /// Shows Windows' administrator prompt, runs the script hidden once it is approved, and waits at most <paramref name="timeout"/>
    /// for its exit. Throws <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled first; a run the user
    /// already approved then finishes on its own.
    /// </summary>
    Task<ElevatedRunOutcome> RunAsync(string script, TimeSpan timeout, CancellationToken ct);
    /// <summary>Waits for the handle of a run abandoned at its prompt to be released; bounded by <paramref name="ct"/>.</summary>
    Task ReleaseAsync(CancellationToken ct);
}

/// <summary>
/// Windows PowerShell 5.1 (always present on Windows) started through ShellExecute with the runas verb, which is the UAC
/// prompt. The script goes in as an encoded command, so no quoting and no execution-policy override is involved; execution
/// policy applies to script files, not to a command. ShellExecute returns only once the prompt is answered, so the start runs
/// off the caller's thread, and a cancelled wait leaves that start to release its handle when it comes (tracked, awaited by
/// <see cref="ReleaseAsync"/>). The process's exit code is read through the handle ShellExecute hands back.
/// </summary>
internal sealed class ElevatedPowerShellRunner(bool elevate = true) : IElevatedPowerShellRunner
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: the administrator prompt was declined.
    private readonly object _gate = new();
    private Task _releasing = Task.CompletedTask;

    internal static string PowerShellPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

    internal static string Arguments(string script)
        => "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    public async Task<ElevatedRunOutcome> RunAsync(string script, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(PowerShellPath)) return new(ElevatedRunKind.NotStarted);
        var start = new ProcessStartInfo(PowerShellPath)
        {
            Arguments = Arguments(script), UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, Verb = elevate ? "runas" : ""
        };
        var starting = Task.Run(() => Start(start), CancellationToken.None);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), cancelled))
        {
            if (await Task.WhenAny(starting, cancelled.Task).ConfigureAwait(false) != starting)
            {
                // Abandoned at the prompt: whatever the answer starts is released once ShellExecute returns.
                var release = starting.ContinueWith(static done => { if (done.Status == TaskStatus.RanToCompletion) done.Result.Process?.Dispose(); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                lock (_gate) _releasing = Task.WhenAll(_releasing, release);
                ct.ThrowIfCancellationRequested();
            }
        }
        var (process, kind) = await starting.ConfigureAwait(false);
        if (process is null) return new(kind);
        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try { await process.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(ElevatedRunKind.TimedOut); }
            return new(ElevatedRunKind.Exited, process.ExitCode);
        }
    }

    public Task ReleaseAsync(CancellationToken ct)
    {
        Task releasing;
        lock (_gate) releasing = _releasing;
        return releasing.WaitAsync(ct);
    }

    private static (Process? Process, ElevatedRunKind Kind) Start(ProcessStartInfo start)
    {
        try
        {
            var process = Process.Start(start);
            return process is null ? (null, ElevatedRunKind.NotStarted) : (process, ElevatedRunKind.Exited);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == ErrorCancelled) { return (null, ElevatedRunKind.Declined); }
        catch (Win32Exception) { return (null, ElevatedRunKind.NotStarted); }
    }
}
