using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private IClientProcessPriorityControl _clientPriority = null!;
    private bool _priorityPermitted, _changingClientPriority;
    private CancellationTokenSource? _clientPriorityStop;
    private Task _clientPriorityWork = Task.CompletedTask;
    private long _clientPriorityGeneration;
    private sealed record ClientPriorityRequest(long Generation, SessionObservation[] Processes);
    private string _clientPriorityStatus = "Choose clients to change their Windows CPU scheduling priority.";

    public ObservableCollection<ClientProcessPriorityResult> ClientPriorityResults { get; } = [];
    public bool CanChangeClientPriority => _priorityPermitted && _ready && !IsLocked && !_shuttingDown
        && !IsChangingClientPriority && (Sessions.Any(row => row.IsChecked) || SelectedSession is not null);
    public bool IsChangingClientPriority => _changingClientPriority;
    public int PendingPriorityRestoreCount => _clientPriority.PendingRestoreCount;
    public string ClientPriorityStatus { get => _clientPriorityStatus; private set => Set(ref _clientPriorityStatus, value); }
    public AsyncCommand SetClientsNormalPriority { get; private set; } = null!;
    public AsyncCommand SetClientsBelowNormalPriority { get; private set; } = null!;
    public AsyncCommand RestoreClientsPriority { get; private set; } = null!;
    public Command CancelClientPriority { get; private set; } = null!;

    private void InitializeClientPriorityFeatures(IClientProcessPriorityControl? control)
    {
        _priorityPermitted = !IsSample || control is not null;
        _clientPriority = control ?? new WindowsClientProcessPriorityControl();
        SetClientsNormalPriority = new(p => ChangeClientPriorityAsync(ClientProcessPriorityAction.Normal, p), ReportClientPriorityFailure, CaptureClientPriorityRequest);
        SetClientsBelowNormalPriority = new(p => ChangeClientPriorityAsync(ClientProcessPriorityAction.BelowNormal, p), ReportClientPriorityFailure, CaptureClientPriorityRequest);
        RestoreClientsPriority = new(p => ChangeClientPriorityAsync(ClientProcessPriorityAction.RestoreOriginal, p), ReportClientPriorityFailure, CaptureClientPriorityRequest);
        CancelClientPriority = new(_ => StopClientPriorityOperations());
        PropertyChanged += ClientPriorityOwnerChanged;
    }

    private void ClientPriorityOwnerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(HasSelectedSession) or nameof(CheckedClientCount) or nameof(IsReady) or nameof(IsLocked))
            Raise(nameof(CanChangeClientPriority));
    }

    private SessionObservation[] PinClientPrioritySelection()
    {
        var requested = Sessions.Where(row => row.IsChecked).Select(row => row.Observation).ToArray();
        if (requested.Length == 0 && SelectedSession is { } selected && Sessions.Contains(selected))
            requested = [selected.Observation];
        var desktop = Volatile.Read(ref _latestDesktop);
        var now = DateTimeOffset.UtcNow;
        if (requested.Length is < 1 or > 128 || desktop is not { Problem: null, ProcessInventoryComplete: true, ProcessRevision: > 0 }
            || desktop.ProcessObservedUtc is not { } observed || observed == default || observed > now
            || now - observed > TimeSpan.FromSeconds(3) || desktop.Sessions is null || desktop.Sessions.Count > 128)
            return [];
        var current = desktop.Sessions;
        if (current.Any(row => row is null || row.Problem is not null || row.ProcessId <= 0 || row.ProcessStartUtcTicks <= 0
                || row.ObservedUtc > observed || row.ProcessStartUtcTicks > row.ObservedUtc.UtcTicks
                || row.ObservedUtc == default || now - row.ObservedUtc > TimeSpan.FromSeconds(3))
            || current.Select(row => row.ProcessId).Distinct().Count() != current.Count
            || requested.Select(row => row.InstanceKey).Distinct().Count() != requested.Length)
            return [];
        var pinned = new SessionObservation[requested.Length];
        for (int index = 0; index < requested.Length; index++)
        {
            var match = current.SingleOrDefault(row => row.InstanceKey == requested[index].InstanceKey);
            if (match is null) return [];
            pinned[index] = match;
        }
        return pinned;
    }

    private object? CaptureClientPriorityRequest(object? parameter)
        => CanChangeClientPriority ? new ClientPriorityRequest(_clientPriorityGeneration, PinClientPrioritySelection()) : null;

    private Task ChangeClientPriorityAsync(ClientProcessPriorityAction action, object? parameter)
    {
        if (!CanChangeClientPriority || parameter is not ClientPriorityRequest request
            || request.Generation != _clientPriorityGeneration) return Task.CompletedTask;
        var pinned = request.Processes;
        if (pinned.Length == 0)
        {
            ClientPriorityStatus = "The client list is incomplete or changed. Wait for a fresh scan, then select the clients again.";
            return Task.CompletedTask;
        }
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _clientPriorityStop = stop;
        long generation = _clientPriorityGeneration;
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _changingClientPriority = true;
        _clientPriorityStatus = "Checking the selected process instances and applying their priority…";
        _clientPriorityWork = RunClientPriorityAsync(pinned, action, generation, stop, published.Task);
        try
        {
            ClientPriorityResults.Clear();
            Raise(nameof(IsChangingClientPriority)); Raise(nameof(ClientPriorityStatus)); Raise(nameof(CanChangeClientPriority));
        }
        finally { published.TrySetResult(); }
        return _clientPriorityWork;
    }

    private async Task RunClientPriorityAsync(SessionObservation[] pinned, ClientProcessPriorityAction action,
        long generation, CancellationTokenSource stop, Task published)
    {
        await published;
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            var result = await _clientPriority.ChangeAsync(Array.AsReadOnly(pinned), action, stop.Token);
            if (result.Results.Count != pinned.Length || result.PendingRestoreCount is < 0 or > 128
                || result.Results.Any(row => row is null || row.Action != action || !Enum.IsDefined(row.Status)
                    || !pinned.Any(client => client.InstanceKey == row.Session.InstanceKey))
                || result.Results.Select(row => row.Session.InstanceKey).Distinct().Count() != pinned.Length)
                throw new InvalidOperationException("The process priority report did not match the pinned selection.");
            // Retain actual partial outcomes after Stop. The global stop notice keeps its own state.
            foreach (var row in result.Results) ClientPriorityResults.Add(row);
            bool stopped = stop.IsCancellationRequested || generation != _clientPriorityGeneration || IsLocked || _shuttingDown;
            ClientPriorityStatus = stopped ? "Priority work stopped. Review the individual results and pending restores below."
                : result.Explanation;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { ClientPriorityStatus = "Priority work cancelled. Settings already accepted by Windows may need restoration."; }
        finally
        {
            if (ReferenceEquals(_clientPriorityStop, stop)) _clientPriorityStop = null;
            _changingClientPriority = false;
            stop.Dispose();
            Raise(nameof(IsChangingClientPriority)); Raise(nameof(CanChangeClientPriority)); Raise(nameof(PendingPriorityRestoreCount));
        }
    }

    private void StopClientPriorityOperations()
    {
        _clientPriorityGeneration++;
        _clientPriorityStop?.Cancel();
        _clientPriority.CancelOperations();
    }

    private void ReportClientPriorityFailure(Exception error)
    {
        ClientPriorityStatus = "The priority operation could not be confirmed. Review pending restores before trying again.";
        Fail(error, ManagerSubsystem.ClientPriority);
    }

    private async Task DisposeClientPriorityFeaturesAsync()
    {
        StopClientPriorityOperations();
        try { await _clientPriorityWork; }
        catch (Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.ClientPriority, error.GetType()); }
        PropertyChanged -= ClientPriorityOwnerChanged;
        try { await _clientPriority.DisposeAsync(); }
        catch (Exception error)
        {
            _diagnostics.RecordFailure(ManagerSubsystem.ClientPriority, error.GetType());
            ClientPriorityStatus = "Windows could not confirm every original priority was restored. Review those clients in Task Manager.";
        }
    }
}
