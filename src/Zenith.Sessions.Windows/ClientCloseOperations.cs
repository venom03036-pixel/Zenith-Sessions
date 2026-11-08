using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>What Windows answered to one TerminateProcess call on the checked handle.</summary>
internal enum ClientTerminateStatus { Accepted, AlreadyExited, Denied }

/// <summary>
/// One open handle to the exact process instance a close or force close acts on. The adapter opens it only after the
/// handle's own creation time matched the observation (a reused PID never matches), so everything done through it reaches
/// that instance or nothing. The owner is <see cref="ClientCloseOperations"/>, which disposes it when the operation ends.
/// </summary>
internal interface IClientCloseHandle : IDisposable
{
    /// <summary>One WM_CLOSE to the observation's identity-checked main window. False when the window is gone, changed or Windows refused.</summary>
    bool PostClose(SessionObservation session);
    /// <summary>Waits, event-driven, until the process exits or <paramref name="timeout"/> passes; true when it exited.</summary>
    Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct);
    /// <summary>TerminateProcess on this handle, after a final liveness check. Only the force path opens a handle that allows it.</summary>
    ClientTerminateStatus Terminate();
}

internal interface IClientCloseAdapter
{
    /// <summary>
    /// Opens the exact instance named by the observation, or null when it has exited, its PID was reused, it is not an
    /// accepted client executable, or Windows refused to open it. <paramref name="forTermination"/> adds the terminate right.
    /// </summary>
    IClientCloseHandle? Open(SessionObservation session, bool forTermination);
}

/// <summary>
/// The escalating close and the confirmed force close for one client (R-instances-12). Close posts WM_CLOSE to the
/// client's checked window, posts again after <see cref="LeavePromptDelay"/> so Roblox's own leave prompt is answered,
/// then waits until <see cref="ExitGrace"/> has passed for the exact process to exit. It never ends the process: a client
/// still alive is reported StillOpen, and the shell may then offer Force close behind its own confirmation. Force close
/// opens its own handle with the terminate right, checks PID and start time on that handle, terminates, and confirms the
/// exit for <see cref="TerminationGrace"/>. The waits are process-exit waits, not polls. <see cref="CancelCloseOperations"/>
/// (Panic, Lock, exit) ends every waiting close at its next step; a request already posted cannot be recalled.
/// </summary>
internal sealed class ClientCloseOperations : IClientCloseControl
{
    public static readonly TimeSpan LeavePromptDelay = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan DefaultExitGrace = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan TerminationGrace = TimeSpan.FromSeconds(2);
    private readonly IClientCloseAdapter _adapter;
    private readonly TimeProvider _clock;
    private long _generation;

    /// <param name="exitGrace">The whole bounded wait of a close, from the first post; tests shorten it. Longer than <see cref="LeavePromptDelay"/>, at most a minute.</param>
    public ClientCloseOperations(IClientCloseAdapter adapter, TimeProvider? clock = null, TimeSpan? exitGrace = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _clock = clock ?? TimeProvider.System;
        ExitGrace = exitGrace ?? DefaultExitGrace;
        if (ExitGrace <= LeavePromptDelay || ExitGrace > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(exitGrace));
    }

    public TimeSpan ExitGrace { get; }
    public void CancelCloseOperations() => Interlocked.Increment(ref _generation);
    private bool Stopped(long generation) => Interlocked.Read(ref _generation) != generation;

    public async Task<ClientCloseResult> CloseAsync(SessionObservation session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ct.ThrowIfCancellationRequested();
        long generation = Interlocked.Read(ref _generation);
        int pid = session.ProcessId;
        if (pid <= 0 || session.ProcessStartUtcTicks <= 0) return NotCurrent();
        using var handle = await Task.Run(() => _adapter.Open(session, forTermination: false), ct).ConfigureAwait(false);
        if (handle is null) return NotCurrent();
        int requests = 0;
        try
        {
            if (Stopped(generation)) return Cancelled(requests);
            if (!await Task.Run(() => handle.PostClose(session), ct).ConfigureAwait(false))
                return new(ClientCloseStatus.NotSent, 0, $"Close was not sent because Windows rejected the request for the window of Roblox process {pid}.");
            requests = 1;
            if (await handle.WaitForExitAsync(LeavePromptDelay, ct).ConfigureAwait(false)) return Exited(requests);
            if (Stopped(generation)) return Cancelled(requests);
            // Roblox shows its leave prompt on the first request and leaves on the second. A window already torn down while the
            // process winds down refuses the second post; the wait still runs on the process itself.
            if (await Task.Run(() => handle.PostClose(session), ct).ConfigureAwait(false)) requests = 2;
            if (await handle.WaitForExitAsync(ExitGrace - LeavePromptDelay, ct).ConfigureAwait(false)) return Exited(requests);
            if (Stopped(generation)) return Cancelled(requests);
            return new(ClientCloseStatus.StillOpen, requests,
                $"Roblox process {pid} is still open {ExitGrace.TotalSeconds:0} s after {Requests(requests)}.");
        }
        // A token cancelled during a wait (Panic, Lock, exit) keeps the count of requests already sent, so the caller can
        // still record that a close was asked for.
        catch (OperationCanceledException) when (ct.IsCancellationRequested && requests > 0) { return Cancelled(requests); }

        ClientCloseResult NotCurrent() => new(ClientCloseStatus.NotCurrent, 0,
            "Close was not sent because the client exited or its window changed. Refresh and select it again.");
        ClientCloseResult Exited(int count) => new(ClientCloseStatus.Exited, count, $"Roblox process {pid} exited after {Requests(count)}.");
        ClientCloseResult Cancelled(int count) => new(ClientCloseStatus.Cancelled, count, count == 0
            ? $"Close of Roblox process {pid} stopped before a close request was sent."
            : $"Close of Roblox process {pid} stopped after {Requests(count)}. A request already sent cannot be recalled.");
        static string Requests(int count) => count == 1 ? "one close request" : "two close requests";
    }

    public async Task<ClientForceCloseResult> ForceCloseAsync(SessionObservation session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ct.ThrowIfCancellationRequested();
        long generation = Interlocked.Read(ref _generation);
        int pid = session.ProcessId;
        const string nothing = " Nothing was ended.";
        if (pid <= 0 || session.ProcessStartUtcTicks <= 0) return NotCurrent();
        using var handle = await Task.Run(() => _adapter.Open(session, forTermination: true), ct).ConfigureAwait(false);
        if (handle is null) return NotCurrent();
        if (Stopped(generation)) return new(ClientForceCloseStatus.Cancelled, null, "Force close stopped before anything was sent." + nothing);
        var status = await Task.Run(() => handle.Terminate(), ct).ConfigureAwait(false);
        if (status == ClientTerminateStatus.AlreadyExited) return NotCurrent();
        if (status != ClientTerminateStatus.Accepted)
            return new(ClientForceCloseStatus.Denied, null, $"Windows refused to end Roblox process {pid}, for example because another Windows user runs it." + nothing);
        var endedUtc = _clock.GetUtcNow();
        bool exited;
        // Windows has accepted the termination: a stop during the confirmation wait changes nothing about that.
        try { exited = await handle.WaitForExitAsync(TerminationGrace, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { exited = false; }
        return exited
            ? new(ClientForceCloseStatus.Ended, endedUtc, $"Roblox process {pid} was ended by force.")
            : new(ClientForceCloseStatus.ExitUnconfirmed, endedUtc,
                $"Windows accepted ending Roblox process {pid} by force but had not reported its exit within {TerminationGrace.TotalSeconds:0} s.");

        ClientForceCloseResult NotCurrent() => new(ClientForceCloseStatus.NotCurrent, null,
            $"Roblox process {pid} already exited or is not the same process any more." + nothing);
    }
}
