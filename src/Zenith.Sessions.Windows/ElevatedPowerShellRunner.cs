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
    /// already approved then finishes on its own. A cancelled/timed-out owner reports a definite no-start or its actual
    /// eventual exit through <paramref name="abandonedStartCompleted"/>; <see cref="ReleaseAsync"/> retains that lifetime.
    /// </summary>
    Task<ElevatedRunOutcome> RunAsync(string script, TimeSpan timeout, CancellationToken ct, Action<ElevatedRunOutcome>? abandonedStartCompleted = null);
    /// <summary>Waits for an abandoned start to prove no-start or its approved process to exit; bounded by <paramref name="ct"/>.</summary>
    Task ReleaseAsync(CancellationToken ct);
}

/// <summary>
/// Windows PowerShell 5.1 (always present on Windows) started through ShellExecute with the runas verb, which is the UAC
/// prompt. The script goes in as an encoded command, so no quoting and no execution-policy override is involved; execution
/// policy applies to script files, not to a command. ShellExecute returns only once the prompt is answered, so the start runs
/// off the caller's thread, and a cancelled wait leaves that start and any approved process owned through actual exit (tracked, awaited by
/// <see cref="ReleaseAsync"/>). The process's exit code is read through the handle ShellExecute hands back.
/// </summary>
internal sealed class ElevatedPowerShellRunner(bool elevate = true,
    Func<ProcessStartInfo, Task<(Process? Process, ElevatedRunKind Kind)>>? startProcess = null,
    Func<Process, CancellationToken, Task<int>>? waitForExit = null) : IElevatedPowerShellRunner
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: the administrator prompt was declined.
    private readonly object _gate = new();
    private Task _releasing = Task.CompletedTask;

    internal static string PowerShellPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

    internal static string Arguments(string script)
        => "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    public async Task<ElevatedRunOutcome> RunAsync(string script, TimeSpan timeout, CancellationToken ct, Action<ElevatedRunOutcome>? abandonedStartCompleted = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        if (ct.IsCancellationRequested) { abandonedStartCompleted?.Invoke(new(ElevatedRunKind.NotStarted)); ct.ThrowIfCancellationRequested(); }
        if (!File.Exists(PowerShellPath)) return new(ElevatedRunKind.NotStarted);
        var start = new ProcessStartInfo(PowerShellPath)
        {
            Arguments = Arguments(script), UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, Verb = elevate ? "runas" : ""
        };
        var starting = startProcess is null ? Task.Run(() => Start(start), CancellationToken.None) : startProcess(start);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), cancelled))
        {
            if (await Task.WhenAny(starting, cancelled.Task).ConfigureAwait(false) != starting)
            {
                // Abandoned at the prompt: approval is not completion; retain any process through its actual exit.
                Track(CompleteAbandonedStartAsync(starting, abandonedStartCompleted));
                ct.ThrowIfCancellationRequested();
            }
        }
        var (process, kind) = await starting.ConfigureAwait(false);
        if (process is null) return new(kind);
        bool transferred = false;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try { return new(ElevatedRunKind.Exited, await WaitForExitAsync(process, limit.Token).ConfigureAwait(false)); }
            catch (Exception)
            {
                // Cancellation ends this wait, not the external writer. Keep its handle and observe real exit.
                transferred = true; Track(CompleteProcessAsync(process, abandonedStartCompleted));
                ct.ThrowIfCancellationRequested();
                if (limit.IsCancellationRequested) return new(ElevatedRunKind.TimedOut);
                throw;
            }
        }
        finally { if (!transferred) process.Dispose(); }
    }

    private async Task<int> WaitForExitAsync(Process process, CancellationToken ct)
    {
        if (waitForExit is not null) return await waitForExit(process, ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false); return process.ExitCode;
    }

    public Task ReleaseAsync(CancellationToken ct)
    {
        Task releasing;
        lock (_gate) releasing = _releasing;
        return releasing.WaitAsync(ct);
    }

    private void Track(Task release)
    { lock (_gate) _releasing = _releasing.IsCompletedSuccessfully ? release : Task.WhenAll(_releasing, release); }

    private async Task CompleteProcessAsync(Process process, Action<ElevatedRunOutcome>? completed)
    {
        using (process)
        {
            int exit = await WaitForExitAsync(process, CancellationToken.None).ConfigureAwait(false);
            completed?.Invoke(new(ElevatedRunKind.Exited, exit));
        }
    }

    private async Task CompleteAbandonedStartAsync(Task<(Process? Process, ElevatedRunKind Kind)> starting,
        Action<ElevatedRunOutcome>? completed)
    {
        var (process, kind) = await starting.ConfigureAwait(false);
        if (process is not null) await CompleteProcessAsync(process, completed).ConfigureAwait(false);
        else if (kind is ElevatedRunKind.Declined or ElevatedRunKind.NotStarted) completed?.Invoke(new(kind));
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
