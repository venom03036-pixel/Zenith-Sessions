using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

/// <summary>
/// Closing a client (R-instances-12). Close asks the client's checked window to close, asks again for Roblox's leave prompt
/// and waits a bounded time for the exact process to exit, through the monitor's <see cref="IClientCloseControl"/>. A
/// client still open after that wait gets Force close offered on its own row; the window asks the user to confirm it by
/// name before <see cref="ForceCloseAsync"/> runs, and an accepted termination is recorded in recent history as a forced
/// end. Nothing here is automatic: no close or force close starts without a click, Panic, Lock and exit stop every waiting
/// close and withdraw every offer, and a monitor without the control (sample mode, tests) keeps the single request.
/// </summary>
public sealed partial class ShellViewModel
{
    private IClientCloseControl? _clientClose;
    private readonly Dictionary<SessionProcessKey, CancellationTokenSource> _closingClients = [];
    private readonly List<Task> _clientCloseWork = [];
    private long _clientCloseGeneration;

    private void InitializeClientCloseFeatures() => _clientClose = IsSample ? null : _monitor as IClientCloseControl;

    /// <summary>UI thread, after the window's confirmation. Available while the dashboard is locked; the force-close offer is not.</summary>
    public async Task RequestCloseAsync(SessionObservation session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_clientClose is null)
        {
            // Monitors without the close control: one close request and no wait, as before.
            var requestedAt = DateTimeOffset.UtcNow;
            bool requested = await _monitor.RequestCloseAsync(session, _lifetime.Token);
            if (requested) RecentClients.RecordCloseRequest(session.ProcessKey, requestedAt);
            Notice = requested ? "Normal close requested. The request does not prove that the process exited." : "Close was not sent because the window changed or Windows rejected the request.";
            return;
        }
        if (!BeginClientClose(session, out var stop)) return;
        await TrackClientCloseAsync(RunClientCloseAsync(session, stop));
    }

    /// <summary>UI thread, after the window's confirmation that named the client. Refused unless Close left this exact client open.</summary>
    public async Task ForceCloseAsync(SessionObservation session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var row = Sessions.FirstOrDefault(item => item.ProcessKey == session.ProcessKey);
        if (_clientClose is null || row is null || !row.ForceCloseOffered || IsLocked || _shuttingDown)
        {
            Notice = $"Force close is offered only after a close request leaves Roblox process {session.ProcessId} open, and not while the dashboard is locked. Nothing was ended.";
            return;
        }
        if (!BeginClientClose(session, out var stop)) return;
        await TrackClientCloseAsync(RunForceCloseAsync(session, row, stop));
    }

    private bool BeginClientClose(SessionObservation session, out CancellationTokenSource stop)
    {
        stop = null!;
        if (_closingClients.ContainsKey(session.ProcessKey))
        { Notice = $"A close request for PID {session.ProcessId} is already waiting for it to exit."; return false; }
        if (_shuttingDown) { Notice = "Zenith Sessions is exiting; no close request was sent."; return false; }
        stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _closingClients[session.ProcessKey] = stop;
        _preserveStopNotice = false;
        return true;
    }

    private async Task TrackClientCloseAsync(Task work)
    {
        _clientCloseWork.Add(work);
        try { await work; } finally { _clientCloseWork.Remove(work); }
    }

    private void EndClientClose(SessionProcessKey key, CancellationTokenSource stop)
    {
        if (_closingClients.TryGetValue(key, out var current) && ReferenceEquals(current, stop)) _closingClients.Remove(key);
        stop.Dispose();
    }

    private async Task RunClientCloseAsync(SessionObservation session, CancellationTokenSource stop)
    {
        var key = session.ProcessKey; int pid = session.ProcessId;
        long generation = _clientCloseGeneration;
        var requestedAt = DateTimeOffset.UtcNow;
        Notice = $"Asking Roblox process {pid} to close. Zenith asks again for Roblox's leave prompt and waits a few seconds for it to exit.";
        ClientCloseResult result;
        try { result = await _clientClose!.CloseAsync(session, stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { result = new(ClientCloseStatus.Cancelled, 0, $"Close of Roblox process {pid} stopped. A request already sent cannot be recalled."); }
        finally { EndClientClose(key, stop); }
        // The request is history, not a cause: it is recorded when at least one reached the client.
        if (result.CloseRequests > 0) RecentClients.RecordCloseRequest(key, requestedAt);
        var row = Sessions.FirstOrDefault(item => item.ProcessKey == key);
        switch (result.Status)
        {
            case ClientCloseStatus.StillOpen:
                bool offered = result.OffersForceClose && row is not null && generation == _clientCloseGeneration && !IsLocked && !_shuttingDown;
                if (offered) row!.ForceCloseOffered = true;
                Notice = result.Explanation + (offered
                    ? " Force close is now offered on its row: it ends the process at once and loses unsaved game activity, and nothing happens without your confirmation."
                    : IsLocked ? " Unlock the dashboard to be offered Force close." : "");
                break;
            case ClientCloseStatus.Exited:
                if (row is not null) row.ForceCloseOffered = false;
                Notice = result.Explanation;
                break;
            case ClientCloseStatus.NotCurrent:
                Notice = "Close was not sent because the client exited or its window changed. Refresh and select it again.";
                break;
            case ClientCloseStatus.NotSent:
                Notice = $"Close was not sent because Windows rejected the request for the window of Roblox process {pid}.";
                break;
            default:
                AddEvent($"Close stopped for PID {pid}. A close request already sent cannot be recalled.");
                if (!_preserveStopNotice && !IsLocked && !_shuttingDown) Notice = result.Explanation;
                break;
        }
    }

    private async Task RunForceCloseAsync(SessionObservation session, SessionRow row, CancellationTokenSource stop)
    {
        var key = session.ProcessKey; int pid = session.ProcessId;
        ClientForceCloseResult result;
        try { result = await _clientClose!.ForceCloseAsync(session, stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { result = new(ClientForceCloseStatus.Cancelled, null, "Force close stopped before anything was sent. Nothing was ended."); }
        finally { EndClientClose(key, stop); }
        if (result.Accepted && result.EndedUtc is { } endedUtc)
        {
            RecentClients.RecordForcedEnd(key, endedUtc);
            row.ForceCloseOffered = false;
            AddEvent($"Ended PID {pid} by force after your confirmation.");
        }
        else if (result.Status == ClientForceCloseStatus.NotCurrent) row.ForceCloseOffered = false;
        if (result.Status == ClientForceCloseStatus.Cancelled)
        {
            AddEvent($"Force close of PID {pid} stopped; nothing was ended.");
            if (_preserveStopNotice || IsLocked || _shuttingDown) return;
        }
        Notice = result.Explanation;
    }

    /// <summary>Panic, Lock and exit: every waiting close ends at its next step and no row offers Force close any more.</summary>
    private void StopClientCloseOperations()
    {
        _clientCloseGeneration++;
        foreach (var stop in _closingClients.Values.ToArray()) stop.Cancel();
        _clientClose?.CancelCloseOperations();
        foreach (var row in Sessions) row.ForceCloseOffered = false;
    }

    private async Task DisposeClientCloseFeaturesAsync()
    {
        StopClientCloseOperations();
        foreach (var work in _clientCloseWork.ToArray())
        {
            try { await work; }
            catch (OperationCanceledException) { }
            catch (Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.ClientWindows, error.GetType()); }
        }
    }
}
