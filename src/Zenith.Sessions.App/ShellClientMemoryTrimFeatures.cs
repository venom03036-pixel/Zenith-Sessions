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
    private IClientWorkingSetTrimControl _clientTrim = null!;
    private bool _trimPermitted, _trimmingClients;
    private CancellationTokenSource? _clientTrimStop;
    private Task _clientTrimWork = Task.CompletedTask;
    private long _clientTrimGeneration;
    private sealed record ClientTrimRequest(long Generation, SessionObservation[] Processes);
    private string _clientTrimStatus = "Choose clients, then ask Windows to trim their working set now.";

    public ObservableCollection<ClientWorkingSetTrimResult> ClientTrimResults { get; } = [];
    public bool CanTrimClients => _trimPermitted && _ready && !IsLocked && !_shuttingDown
        && !_trimmingClients && (Sessions.Any(row => row.IsChecked) || SelectedSession is not null);
    public bool IsTrimmingClients => _trimmingClients;
    public string ClientTrimStatus { get => _clientTrimStatus; private set => Set(ref _clientTrimStatus, value); }
    public AsyncCommand TrimClientsNow { get; private set; } = null!;
    public Command CancelClientTrim { get; private set; } = null!;

    private void InitializeClientMemoryTrimFeatures(IClientWorkingSetTrimControl? control)
    {
        _trimPermitted = !IsSample || control is not null;
        _clientTrim = control ?? new WindowsClientWorkingSetTrimControl();
        TrimClientsNow = new(TrimClientsAsync, ReportClientTrimFailure, CaptureClientTrimRequest);
        CancelClientTrim = new(_ => StopClientTrimOperations());
        PropertyChanged += ClientTrimOwnerChanged;
    }

    private void ClientTrimOwnerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(HasSelectedSession) or nameof(CheckedClientCount) or nameof(IsReady) or nameof(IsLocked))
            Raise(nameof(CanTrimClients));
    }

    // The same pinned, fresh, exact-instance selection the priority controls use.
    private object? CaptureClientTrimRequest(object? parameter)
        => CanTrimClients ? new ClientTrimRequest(_clientTrimGeneration, PinClientPrioritySelection()) : null;

    private Task TrimClientsAsync(object? parameter)
    {
        if (!CanTrimClients || parameter is not ClientTrimRequest request || request.Generation != _clientTrimGeneration)
            return Task.CompletedTask;
        var pinned = request.Processes;
        if (pinned.Length == 0)
        {
            ClientTrimStatus = "The client list is incomplete or changed. Wait for a fresh scan, then select the clients again.";
            return Task.CompletedTask;
        }
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _clientTrimStop = stop;
        long generation = _clientTrimGeneration;
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _trimmingClients = true;
        _clientTrimStatus = $"Trimming one client every {ClientWorkingSetTrimPolicy.TrimSpacing.TotalSeconds:0} s, then reading each about {ClientWorkingSetTrimPolicy.AfterReadingDelay.TotalSeconds:0} s later…";
        _clientTrimWork = RunClientTrimAsync(pinned, generation, stop, published.Task);
        try
        {
            ClientTrimResults.Clear();
            Raise(nameof(IsTrimmingClients)); Raise(nameof(ClientTrimStatus)); Raise(nameof(CanTrimClients));
        }
        finally { published.TrySetResult(); }
        return _clientTrimWork;
    }

    private async Task RunClientTrimAsync(SessionObservation[] pinned, long generation, CancellationTokenSource stop, Task published)
    {
        await published;
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            var result = await _clientTrim.TrimAsync(Array.AsReadOnly(pinned), stop.Token);
            if (result.Results.Count != pinned.Length
                || result.Results.Any(row => row is null || !Enum.IsDefined(row.Status)
                    || !pinned.Any(client => client.InstanceKey == row.Session.InstanceKey))
                || result.Results.Select(row => row.Session.InstanceKey).Distinct().Count() != pinned.Length)
                throw new InvalidOperationException("The memory trim report did not match the pinned selection.");
            foreach (var row in result.Results) ClientTrimResults.Add(row);
            bool stopped = stop.IsCancellationRequested || generation != _clientTrimGeneration || IsLocked || _shuttingDown;
            ClientTrimStatus = stopped ? "Trim work stopped. Trims Windows already accepted stay in effect; review the results below."
                : result.Explanation;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { ClientTrimStatus = "Trim work cancelled before any client was trimmed."; }
        finally
        {
            if (ReferenceEquals(_clientTrimStop, stop)) _clientTrimStop = null;
            _trimmingClients = false;
            stop.Dispose();
            Raise(nameof(IsTrimmingClients)); Raise(nameof(CanTrimClients));
        }
    }

    private void StopClientTrimOperations()
    {
        _clientTrimGeneration++;
        _clientTrimStop?.Cancel();
        _clientTrim.CancelOperations();
    }

    private void ReportClientTrimFailure(Exception error)
    {
        ClientTrimStatus = "The trim could not be confirmed. Review the clients before trying again.";
        Fail(error, ManagerSubsystem.ClientMemory);
    }

    private async Task DisposeClientMemoryTrimFeaturesAsync()
    {
        StopClientTrimOperations();
        try { await _clientTrimWork; }
        catch (Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.ClientMemory,error.GetType()); }
        PropertyChanged -= ClientTrimOwnerChanged;
        try { await _clientTrim.DisposeAsync(); }
        catch (Exception error) { _diagnostics.RecordFailure(ManagerSubsystem.ClientMemory,error.GetType()); }
    }
}
