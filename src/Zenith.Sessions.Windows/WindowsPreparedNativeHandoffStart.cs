using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>Fresh selected-file evidence and its optional single held start. This does not enable a launch provider.</summary>
public sealed class WindowsNativeHandoffPreparation : IDisposable
{
    internal WindowsNativeHandoffPreparation(ClientExecutableTrustResult trust, IPreparedNativeHandoffStart? prepared = null)
    { Trust = trust; Prepared = prepared; }
    public ClientExecutableTrustResult Trust { get; }
    public IPreparedNativeHandoffStart? Prepared { get; }
    public void Dispose() => Prepared?.Dispose();
    public override string ToString() => $"WindowsNativeHandoffPreparation {{ Status = {Trust.Status}, Prepared = {Prepared is not null}, Handoff = [redacted] }}";
}

public sealed partial class WindowsClientExecutableTrust
{
    private readonly INativeHandoffProcessStarter _starter;

    /// <summary>
    /// Reuses the diagnostic signature/hash policy, retaining its exact non-inheritable file handle.
    /// One active or prepared operation occupies this verifier until consumed/disposed. No URI is
    /// accepted here. Callers must await actual dispatch before disposing their preparation.
    /// </summary>
    public Task<WindowsNativeHandoffPreparation> PrepareAsync(ClientInstallationCandidate candidate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate); ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not null) return Task.FromResult(new WindowsNativeHandoffPreparation(
                Result(candidate, ClientExecutableTrustStatus.Deferred, ClientExecutableTrustReason.Busy)));
            var operation = new Operation(CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token));
            _active = operation;
            var worker = Task.Run(() =>
            {
                VerifiedExecutableLease? held = null; bool transferred = false;
                try
                {
                    var trust = VerifyCore(candidate, operation.Stop.Token, out held);
                    operation.Stop.Token.ThrowIfCancellationRequested();
                    if (trust.Status != ClientExecutableTrustStatus.VerifiedRobloxPublisher || held is null)
                        return new WindowsNativeHandoffPreparation(trust);
                    var prepared = new PreparedNativeHandoffStart(this, operation, held, _starter, operation.Stop.Token);
                    lock (_gate)
                    {
                        if (_disposed) throw new OperationCanceledException("Executable preparation was stopped.");
                        operation.Stop.Token.ThrowIfCancellationRequested();
                        operation.Prepared = prepared; held = null; transferred = true;
                    }
                    return new WindowsNativeHandoffPreparation(trust, prepared);
                }
                finally { try { held?.Dispose(); } finally { if (!transferred) FinishOperation(operation); } }
            }, CancellationToken.None);
            operation.Worker = worker; return worker;
        }
    }

    private sealed class VerifiedExecutableLease(FileStream file, ClientExecutableFileSnapshot expected) : IDisposable
    {
        public FileStream File { get; } = file;
        public ClientExecutableFileSnapshot Expected { get; } = expected;
        public void Dispose() => File.Dispose();
    }

    private bool RevalidateHeldFile(VerifiedExecutableLease held, CancellationToken ct)
    {
        CheckAncestors(held.Expected.FullPath, ct);
        using var current = OpenRead(held.Expected.FullPath);
        return _snapshot(held.File.SafeFileHandle) == held.Expected && _snapshot(current) == held.Expected;
    }

    private sealed class PreparedNativeHandoffStart(WindowsClientExecutableTrust owner, Operation operation,
        VerifiedExecutableLease held, INativeHandoffProcessStarter starter, CancellationToken stop) : IPreparedNativeHandoffStart
    {
        // CreateProcess includes the executable, quoting/separator and terminating NUL in this
        // 32767 UTF-16 limit. Input forbids quotes/backslashes/whitespace, so six reserved chars
        // conservatively cover quoting both arguments, their separator and NUL without guessing.
        private const int MaximumCommandLineCharacters = 32767, CommandLineOverhead = 6;
        private readonly object _state = new();
        private bool _consumed, _inFlight, _disposed;

        public NativeHandoffDispatchResult Start(string officialHandoff)
        {
            lock (_state)
            {
                if (_consumed || _disposed) return new(NativeHandoffDispatchOutcome.NotStarted);
                _consumed = true; _inFlight = true; // Own the actual synchronous operation before any native start.
            }
            bool invoked = false;
            var result = new NativeHandoffDispatchResult(NativeHandoffDispatchOutcome.NotStarted);
            try
            {
                if (!stop.IsCancellationRequested && HasBoundedArgument(held.Expected.FullPath, officialHandoff)
                    && owner.RevalidateHeldFile(held, stop))
                    result = InvokeOnce(officialHandoff, ref invoked);
            }
            catch (Exception)
            {
                // A native/delegate exception can contain its argument. Return only fixed facts.
                result = new(invoked ? NativeHandoffDispatchOutcome.Uncertain : NativeHandoffDispatchOutcome.NotStarted,
                    result.ProcessReceipt is { IsValid: true } receipt ? receipt : null);
            }
            finally
            {
                lock (_state) { _inFlight = false; _disposed = true; }
                try { held.Dispose(); }
                catch (Exception)
                {
                    result = new(invoked ? NativeHandoffDispatchOutcome.Uncertain : NativeHandoffDispatchOutcome.NotStarted,
                        result.ProcessReceipt is { IsValid: true } receipt ? receipt : null);
                }
                finally { owner.FinishOperation(operation); }
            }
            return result;
        }

        private NativeHandoffDispatchResult InvokeOnce(string officialHandoff, ref bool invoked)
        {
            var start = new ProcessStartInfo
            {
                FileName = held.Expected.FullPath, UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(held.Expected.FullPath)!, ErrorDialog = false
            };
            INativeHandoffStartedProcess? process = null; LaunchProcessReceipt? receipt = null;
            var outcome = NativeHandoffDispatchOutcome.NotStarted;
            try
            {
                start.ArgumentList.Add(officialHandoff);
                lock (_state)
                {
                    if (!_disposed && !stop.IsCancellationRequested)
                    { invoked = true; outcome = NativeHandoffDispatchOutcome.Uncertain; }
                }
                if (invoked)
                {
                    process = starter.Start(start);
                    receipt = process?.CaptureReceipt(owner._time.GetUtcNow());
                    if (receipt is { IsValid: true }) outcome = NativeHandoffDispatchOutcome.StartedUnverified;
                    else receipt = null;
                }
            }
            catch (Exception) { /* No raw URI, path, exception, credentials or environment are returned. */ }
            finally
            {
                start.ArgumentList.Clear();
                // Disposing this wrapper releases only our returned process handle. It never
                // requests client closure, termination, focus or any change to another process.
                try { process?.Dispose(); }
                catch (Exception) { outcome = NativeHandoffDispatchOutcome.Uncertain; }
            }
            return new(outcome, receipt);
        }

        private static bool HasBoundedArgument(string path, string? handoff)
        {
            if (handoff is not { Length: > 0 and <= NativeRobloxHandoff.MaximumCharacters }
                || !handoff.StartsWith("roblox-player:1+", StringComparison.Ordinal)
                || (long)path.Length + handoff.Length + CommandLineOverhead > MaximumCommandLineCharacters) return false;
            foreach (char value in handoff)
                if (value is < (char)0x21 or > (char)0x7e or '"' or '\\') return false;
            return true;
        }

        public void Dispose()
        {
            lock (_state)
            {
                if (_disposed) return;
                _disposed = true;
                if (_inFlight) return; // The actual Start owner releases the held file in its finally.
                _consumed = true;
            }
            try { held.Dispose(); }
            finally { owner.FinishOperation(operation); }
        }
        public override string ToString() => "PreparedNativeHandoffStart { Executable = [held], Handoff = [redacted] }";
    }
}

// Internal seams carry only an operational argument and the exact returned process object.
// Tests supply synthetic objects; neither seam permits PID enumeration or a public launcher override.
internal interface INativeHandoffProcessStarter
{
    INativeHandoffStartedProcess? Start(ProcessStartInfo start);
}
internal interface INativeHandoffStartedProcess : IDisposable
{
    LaunchProcessReceipt? CaptureReceipt(DateTimeOffset recordedUtc);
}
internal sealed class WindowsNativeHandoffProcessStarter : INativeHandoffProcessStarter
{
    public INativeHandoffStartedProcess? Start(ProcessStartInfo start)
    {
        Process? process = Process.Start(start);
        if (process is null) return null;
        try { return new StartedProcess(process); }
        catch { process.Dispose(); throw; }
    }
    private sealed class StartedProcess(Process process) : INativeHandoffStartedProcess
    {
        public LaunchProcessReceipt? CaptureReceipt(DateTimeOffset recordedUtc)
        {
            SafeProcessHandle handle = process.SafeHandle;
            if (handle.IsInvalid || handle.IsClosed) return null;
            uint pid = GetProcessId(handle);
            if (pid is 0 or > int.MaxValue
                || !GetProcessTimes(handle, out long creation, out _, out _, out _)) return null;
            DateTimeOffset created;
            try { created = DateTimeOffset.FromFileTime(creation).ToUniversalTime(); }
            catch (ArgumentOutOfRangeException) { return null; }
            var receipt = new LaunchProcessReceipt((int)pid, created.UtcTicks, recordedUtc);
            return receipt.IsValid ? receipt : null;
        }
        public void Dispose() => process.Dispose();
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
}
