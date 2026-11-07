using System.Collections.ObjectModel;
using System.Globalization;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

public sealed record RecentClientHistoryRow(RecentClientHistoryEntry Entry)
{
    public Guid Id => Entry.EntryId;
    public string Client => $"PID {Entry.Process.ProcessId}";
    public string Started => new DateTimeOffset(Entry.Process.ProcessStartUtcTicks, TimeSpan.Zero).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string LastSeen => Entry.LastObservedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string MissingSince => Entry.NoLongerObservedUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "Unknown";
    public string Outcome => "No longer observed" + (Entry.CloseRequestedUtc is null ? "" : " · close requested") + Entry.EndCause switch
    {
        ClientEndCause.Crashed => " · Windows recorded a crash" + (Entry.FaultCheck?.ExceptionCode is { } code ? $" (exception 0x{code:X8})" : ""),
        ClientEndCause.NotResponding => " · it was not responding when last seen",
        ClientEndCause.Disconnected => " · Roblox logged a disconnect" + (Entry.DisconnectReason is { } reason ? $" (reason {reason})" : ""),
        ClientEndCause.LeftGame => $" · Roblox logged the player leaving (reason {Entry.DisconnectReason})",
        ClientEndCause.LaunchedElsewhere => $" · Roblox logged that the account launched in another client (reason {Entry.DisconnectReason})",
        ClientEndCause.PossibleCrash => " · possible crash: a Roblox crash dump was written as it ended",
        _ => " · cause unknown"
    };
    public string CloseRequest => Entry.CloseRequestedUtc is { } time ? "Zenith requested a normal close at " + time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "No Zenith close request was recorded. This does not establish a crash or deliberate user closure.";
    public string CrashRecords => Entry.FaultCheck switch
    {
        null => "Crash records were not checked for this client.",
        { FaultRecordedUtc: { } fault } => "Windows recorded an application fault for this exact process at " + fault.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) + ".",
        { CrashDumps: > 0 } check => $"Roblox's crash folder gained {check.CrashDumps} crash dump file(s) as it ended. Another client may have written them.",
        { Complete: true } => "No Windows fault record or new Roblox crash dump was found.",
        _ => "Crash records could not be fully read."
    };
    public string Details => CloseRequest + Environment.NewLine + CrashRecords;
    public string LastReportedPlayer => Entry.LastKnownUserId is { } id && Entry.LastKnownUserIdentityUtc is { } time
        ? $"{id} · {time.ToLocalTime():g} · unverified" : "Unknown";
    public string LastReportedDestination => Entry.LastKnownDestination is { } destination
        ? $"Place {destination.PlaceId} · {destination.JobId:D} · {destination.ObservedUtc.ToLocalTime():g} · unverified" : "Unknown";
}

/// <summary>Optional metadata history, one coalescing save owner, no live-state restoration or action authority.</summary>
public sealed class RecentClientHistoryViewModel : Observable, IAsyncDisposable
{
    private readonly ISessionHistoryStore _store;
    private readonly Func<bool> _canUse;
    private readonly Action<Exception> _failed;
    private readonly IClientFaultProbe? _faultProbe;
    private readonly RecentClientHistoryReducer _history = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task _load = Task.CompletedTask, _save = Task.CompletedTask, _faultCheck = Task.CompletedTask;
    private Task? _dispose;
    private long _savedRevision, _shownRevision = -1;
    private bool _loading, _saving, _loaded, _loadFailed, _saveFailed, _trackingFailed, _stopping, _checkingFaults, _faultChecksFailed;
    private string _status = "Recent client history has not opened yet.";
    public ObservableCollection<RecentClientHistoryRow> Rows { get; } = [];
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool HasRows => Rows.Count > 0;
    public bool IsSaving => _saving;
    public bool CanClear => _loaded && !_stopping && !_trackingFailed && _canUse() && HasRows;
    public bool CanRetry => !_stopping && !_loading && !_saving && !_trackingFailed && _canUse() && (_loadFailed || _saveFailed);
    public Task ActiveSave => _save;
    public AsyncCommand Retry { get; }
    public Command Clear { get; }

    public Task ActiveFaultCheck => _faultCheck;
    /// <param name="faultProbe">Optional read-only crash-record source; null records no fault checks.</param>
    public RecentClientHistoryViewModel(ISessionHistoryStore store, Func<bool> canUse, Action<Exception>? failed = null, IClientFaultProbe? faultProbe = null)
    {
        _store = store; _canUse = canUse; _failed = failed ?? (_ => { }); _faultProbe = faultProbe;
        Retry = new(_ => RetryAsync(), ReportUnexpectedFailure);
        Clear = new(_ => ClearEnded(), _ => CanClear);
    }
    public Task InitializeAsync()
    {
        if (_stopping || _loaded || _loading) return _load;
        _loading = true; _loadFailed = false; RefreshAvailability();
        Status = "Opening recent client history…";
        return _load = LoadAsync();
    }
    private async Task LoadAsync()
    {
        try
        {
            var snapshot = await Task.Run(() => _store.LoadAsync(_lifetime.Token), CancellationToken.None);
            _lifetime.Token.ThrowIfCancellationRequested();
            _history.Restore(snapshot); _savedRevision = snapshot.Revision; _loaded = true;
            Publish(); UpdateStatus();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        { Status = "History opening was cancelled; stored data was preserved."; }
        catch (Exception error)
        { _loadFailed = true; Status = "History unavailable. Its stored data was preserved; monitoring still works. Retry after resolving the storage problem."; _failed(error); }
        finally { _loading = false; RefreshAvailability(); }
    }
    public void Observe(DesktopSnapshot snapshot, IReadOnlyList<SessionEvidenceSnapshot> diagnostics, DateTimeOffset now)
    {
        if (!_loaded || _stopping || _trackingFailed) return;
        try
        {
            if (_history.Observe(snapshot, diagnostics, now)) { Publish(); StartSaveIfNeeded(); }
            StartFaultCheckIfNeeded(now);
        }
        catch (Exception error) when(error is InvalidOperationException or ArgumentException) { PauseTracking(error); }
    }
    private void StartFaultCheckIfNeeded(DateTimeOffset now)
    {
        // Driven by the existing sample lane: at most one check in flight, the next starts from a later sample.
        if (_faultProbe is not { } probe || _checkingFaults || _faultChecksFailed || _stopping || _trackingFailed
            || _history.NextFaultCheck(now) is not { } ended) return;
        _checkingFaults = true;
        _faultCheck = CheckFaultAsync(probe, ended, now);
    }
    private async Task CheckFaultAsync(IClientFaultProbe probe, RecentClientHistoryEntry ended, DateTimeOffset now)
    {
        try
        {
            var token = _lifetime.Token;
            var check = await Task.Run(() => probe.Check(ended, now, token), CancellationToken.None);
            if (!_stopping && !_trackingFailed && _history.RecordFaultCheck(ended, check)) { Publish(); StartSaveIfNeeded(); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Crash records are optional evidence: stop checking, keep presence history running, report once.
            _faultChecksFailed = true; _failed(error);
        }
        finally { _checkingFaults = false; }
    }
    public void RecordCloseRequest(SessionProcessKey process, DateTimeOffset requestedUtc)
    {
        // A normal close remains available under dashboard lock. This only records the successful request.
        if (!_loaded || _stopping || _trackingFailed) return;
        try
        { if (_history.RecordCloseRequest(process, requestedUtc)) { Publish(); StartSaveIfNeeded(); } }
        catch (Exception error) when(error is InvalidOperationException or ArgumentException) { PauseTracking(error); }
    }
    private void Publish()
    {
        var view = _history.Snapshot();
        if (view.Revision == _shownRevision) return;
        _shownRevision = view.Revision;
        var entries = view.Ended;
        for (int index = Rows.Count - 1; index >= 0; index--)
            if (!entries.Any(entry => entry.EntryId == Rows[index].Id)) Rows.RemoveAt(index);
        for (int index = 0; index < entries.Count; index++)
        {
            var entry = entries[index]; int current = -1;
            for (int candidate = 0; candidate < Rows.Count; candidate++)
                if (Rows[candidate].Id == entry.EntryId) { current = candidate; break; }
            if (current < 0) Rows.Insert(index, new(entry));
            else
            {
                if (current != index) Rows.Move(current, index);
                if (Rows[index].Entry != entry) Rows[index] = new(entry);
            }
        }
        Raise(nameof(HasRows)); RefreshAvailability();
    }
    private void StartSaveIfNeeded()
    {
        if (!_loaded || _saving || _saveFailed || _history.Snapshot().Revision <= _savedRevision) return;
        _saving = true; Raise(nameof(IsSaving)); RefreshAvailability(); UpdateStatus();
        _save = SaveLatestAsync();
    }
    private async Task SaveLatestAsync()
    {
        try
        {
            while (_history.Snapshot().Revision > _savedRevision)
            {
                // One immutable bounded snapshot in flight. New observations replace desired state, not a queue.
                var pending = _history.PersistedSnapshot();
                await Task.Run(() => _store.SaveAsync(pending, _lifetime.Token), CancellationToken.None);
                _savedRevision = pending.Revision;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        { _saveFailed = true; }
        catch (Exception error)
        { _saveFailed = true; _failed(error); }
        finally { _saving = false; Raise(nameof(IsSaving)); UpdateStatus(); RefreshAvailability(); }
    }
    private void ClearEnded()
    {
        if (!CanClear) return;
        try { if (_history.ClearEnded()) { Publish(); StartSaveIfNeeded(); UpdateStatus(); } }
        catch (Exception error) when(error is InvalidOperationException or ArgumentException) { PauseTracking(error); }
    }
    private async Task RetryAsync()
    {
        if (!CanRetry) return;
        if (!_loaded) { await InitializeAsync(); return; }
        _saveFailed = false; StartSaveIfNeeded(); await _save;
    }
    private void UpdateStatus()
    {
        if (_loadFailed || !_loaded) return;
        Status = _trackingFailed ? "History tracking paused. Resolve its metadata problem and restart; monitoring continues. Stored history was preserved."
            : _saveFailed
            ? "History save failed. The latest view is only in memory; the previous stored snapshot was preserved. Retry save."
            : _saving ? "Saving recent client history…"
            : _savedRevision==0 && Rows.Count==0 ? "History ready · no recent clients recorded. Missing observations do not prove exit or crash."
            : $"{Rows.Count} recent clients retained · saved. Missing observations do not prove an exit, crash, account or arrival.";
    }
    private void ReportUnexpectedFailure(Exception error)
    { Status = "The history operation could not finish. Stored data was preserved."; _failed(error); }
    private void PauseTracking(Exception error)
    { _trackingFailed=true; UpdateStatus(); RefreshAvailability(); _failed(error); }
    public void RefreshAvailability()
    { Raise(nameof(CanClear)); Raise(nameof(CanRetry)); Clear.Refresh(); }
    public ValueTask DisposeAsync() => new(_dispose ??= StopAsync());
    private async Task StopAsync()
    {
        _stopping = true; RefreshAvailability();
        if (_loading) _lifetime.Cancel();
        try { await _load; await Retry.Execution; await _save; }
        finally
        {
            _lifetime.Cancel();
            // The crash-record check observes the cancelled token and records nothing once stopping.
            try { await _faultCheck; }
            finally { _lifetime.Dispose(); (_store as IDisposable)?.Dispose(); }
        }
    }
}
