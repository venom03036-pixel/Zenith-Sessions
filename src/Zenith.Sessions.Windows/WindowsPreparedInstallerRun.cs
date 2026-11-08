using System.Diagnostics;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// Fresh evidence about a downloaded Roblox installer and, when Windows verified its signature from Roblox Corporation, its one
/// held run. Disposing it without running releases the held file. It does not enable a launch provider.
/// </summary>
public sealed class WindowsInstallerPreparation : IDisposable
{
    internal WindowsInstallerPreparation(ClientExecutableTrustResult trust, IPreparedInstallerRun? run = null) { Trust = trust; Run = run; }
    public ClientExecutableTrustResult Trust { get; }
    internal IPreparedInstallerRun? Run { get; }
    public void Dispose() => Run?.Dispose();
    public override string ToString() => $"WindowsInstallerPreparation {{ Status = {Trust.Status}, Run = {Run is not null}, Path = [held] }}";
}

/// <summary>One run of a held, verified installer. The run is consumed whether or not Windows started the process.</summary>
internal interface IPreparedInstallerRun : IDisposable
{
    /// <summary>
    /// Re-checks the held file, starts it with no arguments from its own folder and waits for it to exit, for at most
    /// <paramref name="waitLimit"/>. Cancellation or the limit only ends the wait: the installer is never closed by Zenith.
    /// </summary>
    Task<ClientUpdateInstallResult> RunAsync(TimeSpan waitLimit, CancellationToken ct);
}

public sealed partial class WindowsClientExecutableTrust
{
    private readonly IInstallerProcessStarter _installerStarter;

    /// <summary>
    /// Verifies a downloaded Roblox installer exactly as a selected player is verified (path safety, bounded size, digest,
    /// cached-evidence Authenticode signature, Roblox Corporation publisher, repeated digest and re-open comparison) and, when
    /// verified, keeps the exact file handle open with read sharing for one run. One active or prepared operation occupies
    /// this verifier until it is consumed or disposed.
    /// </summary>
    internal Task<WindowsInstallerPreparation> PrepareInstallerAsync(string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(path); ct.ThrowIfCancellationRequested();
        var candidate = new ClientInstallationCandidate(ClientInstallationProduct.Roblox, path, null, null, ClientInstallationFileState.PresentUnverified,
            Array.Empty<ClientInstallationEvidence>());
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not null) return Task.FromResult(new WindowsInstallerPreparation(
                Result(candidate, ClientExecutableTrustStatus.Deferred, ClientExecutableTrustReason.Busy)));
            var operation = new Operation(CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token));
            _active = operation;
            var worker = Task.Run(() =>
            {
                VerifiedExecutableLease? held = null; bool transferred = false;
                try
                {
                    var trust = VerifyCore(candidate, operation.Stop.Token, out held, installer: true);
                    operation.Stop.Token.ThrowIfCancellationRequested();
                    if (trust.Status != ClientExecutableTrustStatus.VerifiedRobloxPublisher || held is null)
                        return new WindowsInstallerPreparation(trust);
                    var run = new PreparedInstallerRun(this, operation, held, _installerStarter, operation.Stop.Token);
                    lock (_gate)
                    {
                        if (_disposed) throw new OperationCanceledException("Installer preparation was stopped.");
                        operation.Stop.Token.ThrowIfCancellationRequested();
                        operation.Prepared = run; held = null; transferred = true;
                    }
                    return new WindowsInstallerPreparation(trust, run);
                }
                finally { try { held?.Dispose(); } finally { if (!transferred) FinishOperation(operation); } }
            }, CancellationToken.None);
            operation.Worker = worker; return worker;
        }
    }

    private sealed class PreparedInstallerRun(WindowsClientExecutableTrust owner, Operation operation, VerifiedExecutableLease held,
        IInstallerProcessStarter starter, CancellationToken stop) : IPreparedInstallerRun
    {
        private readonly object _state = new();
        private bool _consumed, _inFlight, _disposed;

        public async Task<ClientUpdateInstallResult> RunAsync(TimeSpan waitLimit, CancellationToken ct)
        {
            lock (_state)
            {
                if (_consumed || _disposed) return NotStarted("No verified installer is held, so nothing was run.");
                _consumed = true; _inFlight = true; // Own the run before any native start.
            }
            IStartedInstallerProcess? process = null; bool started = false;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, ct);
                if (linked.IsCancellationRequested) return NotStarted("The run was stopped before Roblox's installer started.");
                // The held handle keeps the verified bytes; the folder entry is re-read so a replaced entry never runs.
                if (!owner.RevalidateHeldFile(held, linked.Token))
                    return NotStarted("The downloaded installer changed before it could run, so it was not run.");
                // The verified file itself, with no arguments: Zenith adds no channel, flag or address. Not through the shell,
                // so no file association or elevation prompt of Zenith's own is involved; the installer asks Windows itself.
                var start = new ProcessStartInfo
                {
                    FileName = held.Expected.FullPath, UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(held.Expected.FullPath)!, ErrorDialog = false
                };
                process = starter.Start(start); started = true;
                if (process is null) return NotStarted("Windows did not start Roblox's installer.");
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                wait.CancelAfter(waitLimit);
                try
                {
                    int? code = await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                    return new(ClientUpdateInstallOutcome.Exited, code, code switch
                    {
                        0 => "Roblox's installer finished.",
                        { } other => $"Roblox's installer exited with code {other}.",
                        null => "Roblox's installer exited."
                    });
                }
                catch (OperationCanceledException) when (!linked.IsCancellationRequested)
                {
                    return new(ClientUpdateInstallOutcome.StillRunning, null,
                        $"Roblox's installer was still running after {(int)Math.Ceiling(waitLimit.TotalMinutes)} minutes. Zenith stopped waiting and left it running.");
                }
                catch (OperationCanceledException)
                { return new(ClientUpdateInstallOutcome.Stopped, null, "Zenith stopped waiting for Roblox's installer. The installer itself was not closed."); }
            }
            catch (OperationCanceledException) when (!started) { return NotStarted("The run was stopped before Roblox's installer started."); }
            catch (Exception error) when (!started)
            {
                // A revalidation or start failure: a fixed sentence, the exception only by type (its text can carry the path).
                return new(ClientUpdateInstallOutcome.NotStarted, null, "Windows could not start Roblox's installer, so nothing was run.", error);
            }
            catch (Exception error)
            {
                return new(ClientUpdateInstallOutcome.Stopped, null, "Zenith could not wait for Roblox's installer; it may still be running.", error);
            }
            finally
            {
                lock (_state) { _inFlight = false; _disposed = true; }
                try { process?.Dispose(); } // Releases only our process handle; it never ends or signals the installer.
                catch (Exception) { }
                finally
                {
                    try { held.Dispose(); }
                    finally { owner.FinishOperation(operation); }
                }
            }
        }

        private static ClientUpdateInstallResult NotStarted(string summary) => new(ClientUpdateInstallOutcome.NotStarted, null, summary);

        public void Dispose()
        {
            lock (_state)
            {
                if (_disposed) return;
                _disposed = true;
                if (_inFlight) return; // The run owner releases the held file in its finally.
                _consumed = true;
            }
            try { held.Dispose(); }
            finally { owner.FinishOperation(operation); }
        }
        public override string ToString() => "PreparedInstallerRun { Executable = [held] }";
    }
}

// Internal seams: only the start information and the exact returned process object cross them. Tests supply synthetic objects;
// neither seam can end a process or enumerate processes.
internal interface IInstallerProcessStarter
{
    IStartedInstallerProcess? Start(ProcessStartInfo start);
}
internal interface IStartedInstallerProcess : IDisposable
{
    /// <summary>Waits for the process to exit and returns its exit code when Windows reports one. Cancellation ends only the wait.</summary>
    Task<int?> WaitForExitAsync(CancellationToken ct);
}
internal sealed class WindowsInstallerProcessStarter : IInstallerProcessStarter
{
    public IStartedInstallerProcess? Start(ProcessStartInfo start)
    {
        Process? process = Process.Start(start);
        if (process is null) return null;
        try { return new StartedProcess(process); }
        catch { process.Dispose(); throw; }
    }
    private sealed class StartedProcess(Process process) : IStartedInstallerProcess
    {
        public async Task<int?> WaitForExitAsync(CancellationToken ct)
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            try { return process.ExitCode; }
            catch (InvalidOperationException) { return null; }
        }
        public void Dispose() => process.Dispose();
    }
}
