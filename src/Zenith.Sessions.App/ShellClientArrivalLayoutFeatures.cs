using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed record ClientArrivalChoice(ClientArrivalWindowAction Action, string Label);

// Optional automatic window layout, off by default. Each presentation step tells the pure arrival tracker the latest scan
// and log evidence. When a client first reports Connected and the saved choice is Arrange or Minimize, one request goes to
// the existing window lane: Arrange tiles every connected client that is not minimized or hidden, in launch order on the
// saved monitor; Minimize minimizes the new client without activating anything. Connected is an unverified log diagnostic; it only gates this window change.
// Clients already running at the first baseline scan after Zenith starts never arrive, though their Connected line can be minutes old.
// Until such a scan, a client that reports Connected is left alone and the window report says why, once, so the saved choice
// is never silently inert.
// Clients that connect while a request runs wait for one follow-up request, never one each. Clients that connect while a
// manual window operation is queued or running are left alone and count as seen: a request queued behind it on the lane
// would re-tile every connected client right after the user's arrangement and replace its report. Panic and Lock cancel the
// request, drop the waiting clients and stop the feature (Rule 11): it stays stopped after Unlock, as automation does, until
// the user chooses Resume automatic layout. Clients that connect meanwhile count as seen, so they never arrive later. One
// tracked task, awaited on dispose (Rule 12); each request in it has its own stop, so no client waits after the task ends.
public sealed partial class ShellViewModel
{
    private readonly ClientArrivalTracker _clientArrivals = new();
    private ClientArrivalWindowAction _clientArrivalAction;
    private List<SessionObservation>? _arrivedClients;
    private CancellationTokenSource? _arrivalStop;
    private Task _arrivalWork = Task.CompletedTask;
    private bool _arrivalLayoutStopped, _arrivalsLeftAloneForManualOperation, _arrivalBaselineReported;
    private Command? _resumeClientArrivalLayout;
    public IReadOnlyList<ClientArrivalChoice> ClientArrivalChoices { get; } =
    [
        new(ClientArrivalWindowAction.None, "Leave windows alone"),
        new(ClientArrivalWindowAction.Arrange, "Arrange all connected clients on the saved monitor"),
        new(ClientArrivalWindowAction.Minimize, "Minimize the client that connected")
    ];
    /// <summary>Settings draft: what happens to client windows when a client first connects. Applies after Save all settings.</summary>
    public ClientArrivalWindowAction ClientArrivalAction { get => _clientArrivalAction; set { if (Set(ref _clientArrivalAction, value)) PreferencesEdited(); } }
    /// <summary>Tests: the automatic window request in flight, or a completed task.</summary>
    internal Task ClientArrivalWork => _arrivalWork;
    /// <summary>Panic or Lock stopped automatic layout, the saved choice acts on windows, and the dashboard is unlocked.</summary>
    public bool CanResumeClientArrivalLayout => _arrivalLayoutStopped && SavedArrivalAction != ClientArrivalWindowAction.None
        && !IsLocked && !_shuttingDown && _ready;
    /// <summary>The explicit start after Panic or Lock. It acts on nothing by itself; the next client that connects does.</summary>
    public Command ResumeClientArrivalLayout => _resumeClientArrivalLayout ??= new(_ =>
    {
        if (!CanResumeClientArrivalLayout) return;
        _arrivalLayoutStopped = false; Raise(nameof(CanResumeClientArrivalLayout));
        ClientWindowStatus = "Automatic layout resumed. Zenith acts again when the next client connects.";
    });

    // Dispatcher thread, from the presentation step, with the same scan and evidence the rows show.
    private void ObserveClientArrivals(DesktopSnapshot snapshot, IReadOnlyList<SessionEvidenceSnapshot> evidence)
    {
        // The tracker always observes, so a client that connected while this was off or locked never arrives again later.
        var arrived = _clientArrivals.Observe(snapshot, evidence);
        if (_clientArrivals.AwaitingBaseline) ReportClientArrivalBaselineMissing();
        if (arrived.Count == 0 || SavedArrivalAction == ClientArrivalWindowAction.None || IsSample || _shuttingDown || !_ready) return;
        if (_arrivalLayoutStopped || IsLocked)
        {
            // A manual window operation owns the report while it runs.
            if (!_arranging) ClientWindowStatus = "A client connected, but automatic layout is stopped by Panic or Lock, so Zenith left its window alone. Choose Resume automatic layout to start it again.";
            return;
        }
        (_arrivedClients ??= []).AddRange(arrived);
        if (_arrivalWork.IsCompleted) _arrivalWork = RunClientArrivalWorkAsync();
    }
    // A manual window operation owns the report while it runs, and a stopped feature reports that it is stopped instead.
    private void ReportClientArrivalBaselineMissing()
    {
        if (_arrivalBaselineReported || SavedArrivalAction == ClientArrivalWindowAction.None || IsSample || _shuttingDown || !_ready
            || _arrivalLayoutStopped || IsLocked || _arranging) return;
        _arrivalBaselineReported = true;
        ClientWindowStatus = "A client reports it connected, but no client scan has shown yet which clients were already running, so automatic layout left the windows alone. It acts on clients that connect after the first scan that does.";
    }
    private ClientArrivalWindowAction SavedArrivalAction => _savedSettings.WindowLayout?.ArrivalAction ?? ClientArrivalWindowAction.None;
    // A StopOperations step for Panic and Lock. Only ResumeClientArrivalLayout clears it; Unlock and Save all settings do not.
    private void StopClientArrivalLayout()
    {
        _arrivalLayoutStopped = true; _arrivedClients = null;
        Raise(nameof(CanResumeClientArrivalLayout));
    }

    // A stop ends only the request it interrupts, and it empties the waiting list. So a client still waiting when that request
    // returns connected after the stop (after Resume, for Panic) and gets its own request now, not with a later arrival.
    private async Task RunClientArrivalWorkAsync()
    {
        try
        {
            while (_arrivedClients is { Count: > 0 } arrived && !_lifetime.IsCancellationRequested && !_arrivalLayoutStopped && !IsLocked && !_shuttingDown)
            {
                _arrivedClients = null;
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _arrivalStop = stop; long generation = _windowOperationsGeneration;
                bool Stopped() => stop.IsCancellationRequested || generation != _windowOperationsGeneration || _arrivalLayoutStopped || IsLocked || _shuttingDown;
                try { await ApplyClientArrivalAsync(arrived, stop.Token, Stopped); }
                finally { if (ReferenceEquals(_arrivalStop, stop)) _arrivalStop = null; }
            }
        }
        // No client stays waiting once this loop ends, so a later arrival never acts on a client that connected earlier.
        finally { _arrivedClients = null; }
    }

    private async Task ApplyClientArrivalAsync(List<SessionObservation> arrived, CancellationToken ct, Func<bool> stopped)
    {
        // A manual operation sets IsArrangingClients before it waits for the window lane, so this sees one queued or running.
        if (_arranging) { _arrivalsLeftAloneForManualOperation = true; return; }
        var layout = _savedSettings.WindowLayout ?? new ClientWindowLayoutSettings().Validated();
        var action = layout.ArrivalAction;
        SessionObservation[] selected;
        try
        {
            ClientWindowArrangementResult result;
            if (action == ClientArrivalWindowAction.Arrange && _monitor is IClientWindowArranger arranger)
            {
                if (ArrivalDisplay(layout.PreferredDisplayId) is not { } display)
                { ClientWindowStatus = "A client connected, but there is no monitor to arrange clients on. Refresh monitors."; return; }
                // Minimized and hidden clients are left out: the move refuses them, so their cells would stay empty.
                selected = _clientArrivals.Slots(Volatile.Read(ref _latestQueue));
                if (selected.Length == 0)
                {
                    if (!_arranging) ClientWindowStatus = "A client connected, but every connected client is minimized or hidden, so Zenith left the windows alone. Restore them, and Zenith arranges them when the next client connects.";
                    return;
                }
                result = await arranger.ArrangeAsync(Array.AsReadOnly(selected), display.Id, layout.Tile!, ct);
            }
            else if (action == ClientArrivalWindowAction.Minimize && _monitor is IClientWindowBatchControl batch)
            {
                selected = arrived.Count > 128 ? arrived.GetRange(0, 128).ToArray() : arrived.ToArray();
                result = await batch.ChangeManyAsync(Array.AsReadOnly(selected), ClientWindowAction.Minimize, ct);
            }
            else return;
            // A manual window operation started meanwhile owns the report; its own result replaces this one.
            if (_shuttingDown || _arranging) return;
            bool wasStopped = stopped();
            ClientPlacementResults.Clear();
            PublishClientWindowResults(result, selected, wasStopped, positions: action == ClientArrivalWindowAction.Arrange);
            if (!wasStopped) ClientWindowStatus = (action == ClientArrivalWindowAction.Arrange
                ? "A client connected, so Zenith arranged the connected clients. " : "A client connected, so Zenith minimized it. ") + ClientWindowStatus;
        }
        catch (Exception error) when (stopped())
        {
            if (error is not OperationCanceledException) _diagnostics.RecordFailure(ManagerSubsystem.ClientWindows, error.GetType());
        }
        catch (Exception error) { ReportClientWindowFailure(error); }
    }

    // The manual operation's report is final (its result, its stopped report or its failure report), so add why Zenith left a
    // client alone. RunManualWindowOperationAsync calls this after the operation's outcome is handled, never the
    // IsArrangingClients setter: a failure's report comes after that setter runs and would replace the note (O-C5-3).
    private void ManualWindowOperationEnded()
    {
        if (!_arrivalsLeftAloneForManualOperation) return;
        _arrivalsLeftAloneForManualOperation = false;
        if (!_shuttingDown) ClientWindowStatus += " A client connected during this window operation, so automatic layout left the windows alone.";
    }

    // The saved monitor when it is still listed, else the primary monitor, else the first one; null before the list loads.
    private ClientDisplay? ArrivalDisplay(string? preferred)
    {
        ClientDisplayChoice? primary = null, first = null;
        foreach (var choice in ClientDisplays)
        {
            if (preferred is not null && choice.Display.Id == preferred) return choice.Display;
            first ??= choice; if (primary is null && choice.Display.IsPrimary) primary = choice;
        }
        return (primary ?? first)?.Display;
    }
}
