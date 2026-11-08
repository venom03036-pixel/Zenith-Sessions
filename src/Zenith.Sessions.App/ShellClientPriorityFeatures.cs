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
    private sealed record ClientPriorityRequest(long Generation, SessionObservation[] Processes, string Refusal);
    private string _clientPriorityStatus = "Choose clients to change their Windows CPU priority, efficiency mode or cores.";

    public ObservableCollection<ClientProcessPriorityResult> ClientPriorityResults { get; } = [];
    public bool CanChangeClientPriority => _priorityPermitted && _ready && !IsLocked && !_shuttingDown
        && !IsChangingClientPriority && (Sessions.Any(row => row.IsChecked) || SelectedSession is not null);
    public bool IsChangingClientPriority => _changingClientPriority;
    public int PendingPriorityRestoreCount => _clientPriority.PendingRestoreCount;
    public string ClientPriorityStatus { get => _clientPriorityStatus; private set => Set(ref _clientPriorityStatus, value); }
    public AsyncCommand SetClientsNormalPriority { get; private set; } = null!;
    public AsyncCommand SetClientsBelowNormalPriority { get; private set; } = null!;
    public AsyncCommand SetClientsEfficiencyMode { get; private set; } = null!;
    public AsyncCommand SpreadClientsOverCores { get; private set; } = null!;
    public AsyncCommand RestoreClientsPriority { get; private set; } = null!;
    public Command CancelClientPriority { get; private set; } = null!;

    private void InitializeClientPriorityFeatures(IClientProcessPriorityControl? control)
    {
        _priorityPermitted = !IsSample || control is not null;
        _clientPriority = control ?? new WindowsClientProcessPriorityControl();
        SetClientsNormalPriority = new(p => ChangeClientPriorityAsync(ClientProcessPriorityAction.Normal, p), ReportClientPriorityFailure, CaptureClientPriorityRequest);
        SetClientsBelowNormalPriority = new(p => ChangeClientPriorityAsync(ClientProcessPriorityAction.BelowNormal, p), ReportClientPriorityFailure, CaptureClientPriorityRequest);
        SetClientsEfficiencyMode = new(p => ChangeClientPriorityAsync(ClientProcessPriorityAction.Efficiency, p), ReportClientPriorityFailure, CaptureClientPriorityRequest);
        SpreadClientsOverCores = new(p => ChangeClientPriorityAsync(ClientProcessPriorityAction.SpreadCores, p), ReportClientPriorityFailure, CaptureClientPriorityRequest);
        RestoreClientsPriority = new(p => ChangeClientPriorityAsync(ClientProcessPriorityAction.RestoreOriginal, p), ReportClientPriorityFailure, CaptureClientPriorityRequest);
        CancelClientPriority = new(_ => StopClientPriorityOperations());
        PropertyChanged += ClientPriorityOwnerChanged;
    }

    private void ClientPriorityOwnerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(HasSelectedSession) or nameof(CheckedClientCount) or nameof(IsReady) or nameof(IsLocked))
            Raise(nameof(CanChangeClientPriority));
    }

    private const string ClientSelectionChangedText = "The client list is incomplete or changed. Wait for a fresh scan, then select the clients again.";
    private const string ClientSelectionUnreadableText = "Windows could not read the identity of a selected client, for example one run by another Windows user, so nothing was changed. Uncheck or deselect that row, then try again.";

    /// <summary>Pins the checked (or selected) exact instances from the latest fresh scan; empty with <paramref name="refusal"/> otherwise.</summary>
    private SessionObservation[] PinClientPrioritySelection(out string refusal)
    {
        var requested = Sessions.Where(row => row.IsChecked).Select(row => row.Observation).ToArray();
        if (requested.Length == 0 && SelectedSession is { } selected && Sessions.Contains(selected))
            requested = [selected.Observation];
        return PinFreshClientInstances(requested, out refusal);
    }

    /// <summary>
    /// The latest scan, when it is fresh and complete enough to name every readable client instance exactly; null otherwise.
    /// The monitor reports a process it could not open (for example another Windows user's client) as a row with a Problem and
    /// no start time, and that row alone makes it call the scan incomplete. The row names no instance to act on, so it is
    /// skipped, as TelemetryHistory skips it; its PID still takes part in the duplicate check. Any other incompleteness, and a
    /// scan that reached its budget, still refuses.
    /// </summary>
    private static IReadOnlyList<SessionObservation>? FreshClientInventory(DesktopSnapshot? desktop, DateTimeOffset now)
    {
        if (desktop is not { Problem: null, ProcessRevision: > 0 }
            || desktop.ProcessObservedUtc is not { } observed || observed == default || observed > now
            || now - observed > TimeSpan.FromSeconds(3) || desktop.Sessions is null || desktop.Sessions.Count > 128)
            return null;
        var current = desktop.Sessions;
        bool unreadablePresent = current.Any(row => row is not null && IdentityUnavailable(row, observed, now));
        if (!desktop.ProcessInventoryComplete
            && (!unreadablePresent || current.Count >= DesktopSamplingCadence.MaximumProcessSamples))
            return null;
        if (current.Any(row => row is null || (!IdentityUnavailable(row, observed, now) && (row.Problem is not null
                || row.ProcessId <= 0 || row.ProcessStartUtcTicks <= 0
                || row.ObservedUtc > observed || row.ProcessStartUtcTicks > row.ObservedUtc.UtcTicks
                || row.ObservedUtc == default || now - row.ObservedUtc > TimeSpan.FromSeconds(3))))
            || current.Select(row => row.ProcessId).Distinct().Count() != current.Count)
            return null;
        return current;
    }

    /// <summary>Pins <paramref name="requested"/> exact instances from the latest fresh scan; empty with <paramref name="refusal"/> otherwise.</summary>
    private SessionObservation[] PinFreshClientInstances(SessionObservation[] requested, out string refusal)
    {
        refusal = ClientSelectionChangedText;
        if (requested.Length is < 1 or > 128 || FreshClientInventory(Volatile.Read(ref _latestDesktop), DateTimeOffset.UtcNow) is not { } current
            || requested.Select(row => row.InstanceKey).Distinct().Count() != requested.Length)
            return [];
        var pinned = new SessionObservation[requested.Length];
        for (int index = 0; index < requested.Length; index++)
        {
            var match = current.SingleOrDefault(row => row.InstanceKey == requested[index].InstanceKey);
            if (match is null) return [];
            // Only identity-unavailable rows pass the checks above with a Problem.
            if (match.Problem is not null) { refusal = ClientSelectionUnreadableText; return []; }
            pinned[index] = match;
        }
        return pinned;
    }

    private static bool IdentityUnavailable(SessionObservation row, DateTimeOffset observed, DateTimeOffset now)
        => row.Problem is not null && row.ProcessStartUtcTicks == 0 && row.ProcessId > 0 && row.ObservedUtc != default
            && row.ObservedUtc <= observed && now - row.ObservedUtc <= TimeSpan.FromSeconds(3);

    private object? CaptureClientPriorityRequest(object? parameter)
        => CanChangeClientPriority
            ? new ClientPriorityRequest(_clientPriorityGeneration, PinClientPrioritySelection(out var refusal), refusal) : null;

    private Task ChangeClientPriorityAsync(ClientProcessPriorityAction action, object? parameter)
    {
        if (!CanChangeClientPriority || parameter is not ClientPriorityRequest request
            || request.Generation != _clientPriorityGeneration) return Task.CompletedTask;
        var pinned = request.Processes;
        if (pinned.Length == 0)
        {
            ClientPriorityStatus = request.Refusal;
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
