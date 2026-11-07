using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zenith.MacroHost.Contracts;

namespace Zenith.Sessions.App;

/// <summary>Transient reviewed page identity. It must never be persisted as a durable macro revision.</summary>
public sealed record MacroCatalogSelection(Guid HostSessionId, long CatalogRevision, int Offset, SavedMacroDescriptor Macro);

/// <summary>Explicit metadata requests only. This link cannot run macros or authorize an account/target.</summary>
public sealed class MacroHostCatalogViewModel : Observable, IAsyncDisposable
{
    private readonly IZenithMacroCatalogClient _client;
    private readonly Func<bool> _canUse;
    private readonly Action<Exception> _failure;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operationStop;
    private Task _active = Task.CompletedTask;
    private Task? _dispose;
    private long _generation;
    private Guid? _hostSession;
    private long? _catalogRevision;
    private int _offset, _total;
    private int? _nextOffset;
    private bool _busy, _stale, _hasSnapshot;
    private DateTimeOffset? _received;
    private string _status = "Open Personal Zenith, then refresh to see its saved macros.", _hostVersion = "Not connected";
    private SavedMacroDescriptor? _selected;
    private IReadOnlyList<SavedMacroDescriptor> _macros = Array.Empty<SavedMacroDescriptor>();
    public IReadOnlyList<SavedMacroDescriptor> Macros => _macros;
    public AsyncCommand Refresh { get; }
    public AsyncCommand PreviousPage { get; }
    public AsyncCommand NextPage { get; }
    public Command Cancel { get; }
    public Task ActiveOperation => _active;
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) RefreshAvailability(); } }
    public bool CanRefresh => CanStart() && !IsBusy;
    public bool CanPrevious => CanRefresh && _hasSnapshot && !_stale && _offset > 0;
    public bool CanNext => CanRefresh && _hasSnapshot && !_stale && _nextOffset is not null;
    public bool IsStale => _stale;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string HostVersion { get => _hostVersion; private set => Set(ref _hostVersion, value); }
    public string PageSummary => !_hasSnapshot ? "No saved-macro snapshot received."
        : $"{(_total == 0 ? 0 : _offset + 1)}–{_offset + Macros.Count} of {_total} saved macros" + (_stale ? " · last received, stale" : "");
    public string ReceivedSummary => _received is { } time ? $"Last received {time.ToLocalTime():T}. Changes in Zenith require Refresh." : "No automatic connection or macro-library scan.";
    public SavedMacroDescriptor? SelectedMacro
    { get => _selected; set { if (Set(ref _selected, value)) Raise(nameof(SelectedReference)); } }
    public string SelectedReference => SelectedMacro is { } selected
        ? $"Macro {selected.Id:D} · saved revision {selected.SavedRevision:N}"
        : "Select a saved macro to inspect its reference. Playback is still being integrated.";

    public MacroHostCatalogViewModel(IZenithMacroCatalogClient client, Func<bool> canUse, Action<Exception>? failure = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _canUse = canUse ?? throw new ArgumentNullException(nameof(canUse)); _failure = failure ?? (_ => { });
        Refresh = new(_ => RefreshAsync(), ReportFailure);
        PreviousPage = new(_ => PreviousAsync(), ReportFailure); NextPage = new(_ => NextAsync(), ReportFailure);
        Cancel = new(_ => CancelWork());
    }
    public void RefreshAvailability() { Raise(nameof(CanRefresh)); Raise(nameof(CanPrevious)); Raise(nameof(CanNext)); }
    private bool CanStart() => _dispose is null && !_lifetime.IsCancellationRequested && _canUse();
    public Task RefreshAsync(CancellationToken ct = default) => RequestAsync(new(), ct);
    public Task PreviousAsync(CancellationToken ct = default) => CanPrevious
        ? RequestAsync(new(Math.Max(0, _offset - MacroCatalogLimits.MaximumPageSize), MacroCatalogLimits.MaximumPageSize, _hostSession, _catalogRevision), ct)
        : Task.CompletedTask;
    public Task NextAsync(CancellationToken ct = default) => CanNext
        ? RequestAsync(new(_nextOffset!.Value, MacroCatalogLimits.MaximumPageSize, _hostSession, _catalogRevision), ct)
        : Task.CompletedTask;

    public MacroCatalogSelection? CaptureSelection()
    {
        if (!CanRefresh || !_hasSnapshot || _stale || _hostSession is not { } session || _catalogRevision is not { } revision
            || SelectedMacro is not { } selected || !Macros.Contains(selected)) return null;
        return new(session, revision, _offset, selected);
    }

    /// <summary>Explicitly rechecks the captured page without redirecting to a later selection.</summary>
    public async Task<bool> RevalidateSelectionAsync(MacroCatalogSelection expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected); ct.ThrowIfCancellationRequested();
        if (!CanRefresh || !_hasSnapshot || _stale || _hostSession != expected.HostSessionId
            || _catalogRevision != expected.CatalogRevision || _offset != expected.Offset || !Macros.Contains(expected.Macro)) return false;
        long priorGeneration = _generation;
        await RequestAsync(new(expected.Offset, MacroCatalogLimits.MaximumPageSize, expected.HostSessionId, expected.CatalogRevision), ct);
        ct.ThrowIfCancellationRequested();
        return _generation == priorGeneration + 1 && CanRefresh && !_stale && _hostSession == expected.HostSessionId
            && _catalogRevision == expected.CatalogRevision && _offset == expected.Offset && Macros.Contains(expected.Macro);
    }

    private Task RequestAsync(MacroCatalogQuery query, CancellationToken ct)
    {
        if (!CanRefresh) return Task.CompletedTask;
        query = query.Validated();
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ct);
        _operationStop = stop; long generation = ++_generation;
        _busy = true; _status = "Reading saved macros from Personal Zenith…";
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = _active = ReadAsync(query, generation, stop, published.Task);
        // Reentrant stop/dispose from a binding must see this exact owned task before it can run.
        try { Raise(nameof(IsBusy)); Raise(nameof(Status)); RefreshAvailability(); }
        finally { published.TrySetResult(); }
        return active;
    }
    private async Task ReadAsync(MacroCatalogQuery query, long generation, CancellationTokenSource stop, Task published)
    {
        await published;
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            if (generation != _generation) return;
            if (!CanStart())
            { _stale = true; _status = "Macro request stopped because the controls are no longer available. Refresh explicitly when ready."; PublishSummary(); return; }
            var page = (await _client.GetPageAsync(query, stop.Token)).Validated(query);
            if (stop.IsCancellationRequested || generation != _generation || !CanStart())
            {
                if (generation == _generation) { _stale = true; _status = "Macro request stopped because the controls are no longer available. Refresh explicitly when ready."; PublishSummary(); }
                return;
            }
            if (page.State != MacroCatalogState.Ready)
            { _stale = true; _status = MacroCatalogMessages.For(page.State); PublishSummary(); return; }
            Guid? selectedId = SelectedMacro?.Id; Guid? selectedRevision = SelectedMacro?.SavedRevision;
            // Validated copied rows are immutable; draft edits and later peer mutation cannot alter this page.
            _macros = page.Items;
            _offset = query.Offset; _total = page.Total; _nextOffset = page.NextOffset;
            _hostSession = page.HostSessionId; _catalogRevision = page.CatalogRevision;
            _received = DateTimeOffset.UtcNow; _hasSnapshot = true; _stale = false;
            _hostVersion = "Personal Zenith " + page.HostVersion;
            _selected = Macros.FirstOrDefault(item => item.Id == selectedId && item.SavedRevision == selectedRevision);
            _status = _total == 0 ? "Personal Zenith has no saved macros in this snapshot."
                : "Saved macros received. Edit and record them in Zenith; this connection does not start playback.";
            PublishSummary();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { if (generation == _generation) { _stale = true; _status = "Macro request cancelled. Refresh explicitly when ready."; PublishSummary(); } }
        catch (Exception error)
        {
            if (generation == _generation && CanStart())
            { _stale = true; _status = "The macro connection could not be verified. Existing rows were kept as stale; open Personal Zenith and refresh."; ReportFailure(error); PublishSummary(); }
        }
        finally
        {
            if (ReferenceEquals(_operationStop, stop)) { _operationStop = null; IsBusy = false; }
            stop.Dispose();
        }
    }
    private void PublishSummary()
    {
        // All fields are committed before notification; a callback may stop/lock the owner here.
        // Subsequent notifications read the current state and must never overwrite that stop.
        Raise(nameof(Macros)); Raise(nameof(Status)); Raise(nameof(HostVersion)); Raise(nameof(SelectedMacro));
        Raise(nameof(IsStale)); Raise(nameof(PageSummary)); Raise(nameof(ReceivedSummary)); Raise(nameof(SelectedReference)); RefreshAvailability();
    }
    private void ReportFailure(Exception error)
    { try { _failure(error); } catch { /* A diagnostic observer cannot undo a read-only request or replace its status. */ } }
    public void CancelWork()
    {
        _generation++; _operationStop?.Cancel();
        _stale = true; _status = "Macro request stopped. Refresh explicitly when you are ready."; PublishSummary();
    }
    public ValueTask DisposeAsync() => new(_dispose ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        _generation++; _lifetime.Cancel(); _operationStop?.Cancel();
        await _active;
        if (_client is IAsyncDisposable owner) await owner.DisposeAsync();
        _lifetime.Dispose(); RefreshAvailability();
    }
}
