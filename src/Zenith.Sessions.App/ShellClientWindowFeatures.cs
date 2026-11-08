using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed record ClientDisplayChoice(ClientDisplay Display)
{
    public string Label => $"{Display.Name} · {Display.WorkArea.Width} × {Display.WorkArea.Height} · {Display.ScalePercent}%";
}
public sealed record ClientPlacementRow(ClientWindowPlacementResult Result, bool ConfirmsContentSize = false)
{
    public string Client => $"PID {Result.Session.ProcessId}";
    public string Outcome => Result.Status switch
    {
        ClientWindowPlacementStatus.Applied => ConfirmsContentSize ? "Content size confirmed" : Result.RequestedBounds is null ? "State confirmed" : "Position confirmed",
        ClientWindowPlacementStatus.InvalidClient => "Client changed",
        ClientWindowPlacementStatus.UnsupportedState => "Restore or use windowed mode",
        ClientWindowPlacementStatus.DisplayUnavailable => "Monitor unavailable",
        ClientWindowPlacementStatus.LayoutUnavailable => "Does not fit",
        ClientWindowPlacementStatus.Denied => "Windows declined",
        ClientWindowPlacementStatus.Cancelled => "Cancelled",
        _ => ConfirmsContentSize ? "Content size unconfirmed" : Result.RequestedBounds is null ? "State unconfirmed" : "Position unconfirmed"
    };
    public string Explanation => Result.Explanation;
}

public sealed partial class ShellViewModel
{
    private CancellationTokenSource? _arrangeStop, _displayStop;
    private long _windowOperationsGeneration;
    private bool _arranging, _updatingSessionChecks;
    private ClientDisplayChoice? _selectedDisplay;
    private string _windowStatus = "Choose a monitor, check clients, then arrange them. Window changes never verify an account.";
    private string _tileColumns = "", _tileGap = "8", _tileMinimumWidth = "320", _tileMinimumHeight = "240";
    private string _clientContentWidth = "640", _clientContentHeight = "480";
    private string? _preferredDisplayId;
    private bool _loadingWindowDraft;
    public ObservableCollection<ClientDisplayChoice> ClientDisplays { get; } = [];
    public ObservableCollection<ClientPlacementRow> ClientPlacementResults { get; } = [];
    public ClientDisplayChoice? SelectedClientDisplay { get => _selectedDisplay; set { if (Set(ref _selectedDisplay, value)) { Raise(nameof(CanArrangeClients)); if (!_loadingWindowDraft) { _preferredDisplayId = value?.Display.Id; PreferencesEdited(); } } } }
    public bool CanManageClientWindows => !IsSample && _monitor is IClientWindowArranger or IClientWindowBatchControl or IClientWindowResizer;
    public bool CanRefreshClientDisplays => !IsSample && _monitor is IClientWindowArranger;
    public bool CanArrangeClients => CanManageClientWindows && _monitor is IClientWindowArranger && !IsLocked && !_shuttingDown && _ready && !_arranging
        && SelectedClientDisplay is not null && (Sessions.Any(row => row.IsChecked) || SelectedSession is not null);
    public bool CanChangeClientStates => !IsSample && _monitor is IClientWindowBatchControl && !IsLocked && !_shuttingDown && _ready && !_arranging
        && (Sessions.Any(row => row.IsChecked) || SelectedSession is not null);
    public bool CanEditClientSize => !IsSample && _monitor is IClientWindowResizer && !IsLocked && !_shuttingDown && _ready && !_arranging;
    public bool CanResizeClients => CanEditClientSize && (Sessions.Any(row => row.IsChecked) || SelectedSession is not null);
    public bool IsArrangingClients { get => _arranging; private set { if (Set(ref _arranging, value)) { Raise(nameof(CanArrangeClients)); Raise(nameof(CanChangeClientStates)); Raise(nameof(CanEditClientSize)); Raise(nameof(CanResizeClients)); if (!value) ManualWindowOperationEnded(); } } }
    public string CheckedClientCount
    {
        get { int count=Sessions.Count(row => row.IsChecked); return $"{count} {(count == 1 ? "client" : "clients")} checked"; }
    }
    public string ClientWindowStatus { get => _windowStatus; private set => Set(ref _windowStatus, value); }
    public string TileColumns { get => _tileColumns; set { if (Set(ref _tileColumns, value)) PreferencesEdited(); } }
    public string TileGap { get => _tileGap; set { if (Set(ref _tileGap, value)) PreferencesEdited(); } }
    public string TileMinimumWidth { get => _tileMinimumWidth; set { if (Set(ref _tileMinimumWidth, value)) PreferencesEdited(); } }
    public string TileMinimumHeight { get => _tileMinimumHeight; set { if (Set(ref _tileMinimumHeight, value)) PreferencesEdited(); } }
    // Explicit, temporary operation drafts; these are not saved layout preferences.
    public string ClientContentWidth { get => _clientContentWidth; set => Set(ref _clientContentWidth, value); }
    public string ClientContentHeight { get => _clientContentHeight; set => Set(ref _clientContentHeight, value); }
    public Command SelectClientPage { get; private set; } = null!;
    public Command CancelClientWindowWork { get; private set; } = null!;
    public AsyncCommand RefreshClientDisplays { get; private set; } = null!;
    public AsyncCommand ArrangeClients { get; private set; } = null!;
    public AsyncCommand MinimizeCheckedClients { get; private set; } = null!;
    public AsyncCommand RestoreCheckedClients { get; private set; } = null!;
    public AsyncCommand ResizeCheckedClients { get; private set; } = null!;
    public Command UseTinyClientSize { get; private set; } = null!;

    private void InitializeClientWindowFeatures()
    {
        SelectClientPage = new(_ =>
        {
            if (IsLocked || _shuttingDown) return;
            bool select = Sessions.Any(row => !row.IsChecked);
            _updatingSessionChecks = true;
            try { foreach (var row in Sessions) row.IsChecked = select; }
            finally { _updatingSessionChecks = false; NotifyClientSelection(); }
        });
        CancelClientWindowWork = new(_ =>
        {
            StopClientWindowOperations();
            ClientWindowStatus = "Window operations cancelled. Requests already sent to Windows cannot be undone automatically.";
        });
        RefreshClientDisplays = new(_ => RefreshClientDisplaysAsync(), ReportClientWindowFailure);
        ArrangeClients = new(_ => ArrangeClientsAsync(), ReportClientWindowFailure);
        MinimizeCheckedClients = new(_ => ChangeClientStatesAsync(ClientWindowAction.Minimize), ReportClientWindowFailure);
        RestoreCheckedClients = new(_ => ChangeClientStatesAsync(ClientWindowAction.Restore), ReportClientWindowFailure);
        ResizeCheckedClients = new(_ => ResizeClientsAsync(), ReportClientWindowFailure);
        UseTinyClientSize = new(_ =>
        {
            if (!CanEditClientSize) return;
            ClientContentWidth = "320"; ClientContentHeight = "240";
        });
        PropertyChanged += ClientWindowShellStateChanged;
    }
    private void ClientWindowShellStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(HasSelectedSession) or nameof(IsReady) or nameof(IsLocked))
        { Raise(nameof(CanEditClientSize)); Raise(nameof(CanResizeClients)); Raise(nameof(CanResumeClientArrivalLayout)); }
    }
    private void SessionSelectionChanged(object? sender, PropertyChangedEventArgs args)
    { if (!_updatingSessionChecks && args.PropertyName == nameof(SessionRow.IsChecked)) NotifyClientSelection(); }
    private void NotifyClientSelection()
    { Raise(nameof(CheckedClientCount)); Raise(nameof(CanArrangeClients)); Raise(nameof(CanChangeClientStates)); Raise(nameof(CanResizeClients)); }

    private SessionObservation[] PinClientWindowSelection()
    {
        var selected = Sessions.Where(row => row.IsChecked).Select(row => row.Observation).ToArray();
        if (selected.Length == 0 && SelectedSession is { } current) selected = [current.Observation];
        if (selected.Length is < 1 or > 128) throw new ArgumentException("Choose between one and 128 current client windows per operation.");
        return selected;
    }

    private async Task ChangeClientStatesAsync(ClientWindowAction action)
    {
        if (!CanChangeClientStates || _monitor is not IClientWindowBatchControl windows)
        { ClientWindowStatus = "Check current client windows, or select one, before minimizing or restoring."; return; }
        var selected = PinClientWindowSelection();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _arrangeStop = stop; long generation = _windowOperationsGeneration;
        IsArrangingClients = true; ClientPlacementResults.Clear();
        ClientWindowStatus = action == ClientWindowAction.Minimize ? "Minimizing the pinned client selection…" : "Restoring the pinned client selection without requesting foreground focus…";
        try
        {
            var result = await windows.ChangeManyAsync(Array.AsReadOnly(selected), action, stop.Token);
            if (_shuttingDown || !ReferenceEquals(_arrangeStop, stop)) return;
            bool stopped = stop.IsCancellationRequested || generation != _windowOperationsGeneration || IsLocked;
            PublishClientWindowResults(result, selected, stopped, positions: false);
        }
        finally
        {
            if (ReferenceEquals(_arrangeStop, stop)) _arrangeStop = null;
            IsArrangingClients = false;
        }
    }

    private async Task ResizeClientsAsync()
    {
        if (!CanResizeClients || _monitor is not IClientWindowResizer windows)
        { ClientWindowStatus = "Content resizing requires a supported live provider and checked clients, or one selected client."; return; }
        ClientContentSize size;
        try { size = ReadClientContentSizeDraft(); }
        catch (ArgumentException error) { ClientWindowStatus = error.Message; return; }
        // Exact process/window recipients and physical content dimensions cannot follow later draft edits.
        var selected = PinClientWindowSelection();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _arrangeStop = stop; long generation = _windowOperationsGeneration;
        IsArrangingClients = true; ClientPlacementResults.Clear();
        ClientWindowStatus = $"Applying {size.Width} × {size.Height} physical content pixels to the pinned clients on their current monitors without requesting foreground focus…";
        try
        {
            var result = await windows.ResizeAsync(Array.AsReadOnly(selected), size, stop.Token);
            if (_shuttingDown || !ReferenceEquals(_arrangeStop, stop)) return;
            bool stopped = stop.IsCancellationRequested || generation != _windowOperationsGeneration || IsLocked;
            PublishClientWindowResults(result, selected, stopped, positions: true, contentSize: true);
        }
        catch (Exception error) when (stop.IsCancellationRequested || generation != _windowOperationsGeneration || IsLocked || _shuttingDown)
        {
            if (!_shuttingDown) ClientWindowStatus = "Content resizing stopped. Final client sizes could not be confirmed; requests already sent to Windows cannot be undone automatically.";
            if (error is not OperationCanceledException) _diagnostics.RecordFailure(ManagerSubsystem.ClientWindows, error.GetType());
        }
        finally
        {
            if (ReferenceEquals(_arrangeStop, stop)) _arrangeStop = null;
            IsArrangingClients = false;
        }
    }

    private ClientContentSize ReadClientContentSizeDraft()
    {
        static int Dimension(string text, string label, int minimum)
        {
            if (text is null || text.Length > 16 || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                || value < minimum || value > ClientContentSize.MaximumDimension)
                throw new ArgumentException($"Content {label} must be a whole number from {minimum} to {ClientContentSize.MaximumDimension} physical pixels.");
            return value;
        }
        return new ClientContentSize(Dimension(ClientContentWidth, "width", ClientContentSize.MinimumWidth),
            Dimension(ClientContentHeight, "height", ClientContentSize.MinimumHeight)).Validated();
    }

    /// <summary>
    /// Fills the monitor list once the shell is ready and unlocked (startup, or the first unlock after a lock that
    /// covered startup), so Tile clients and the saved monitor choice work without the manual refresh button. It runs
    /// through the RefreshClientDisplays command, so the read is tracked, cancelled by Panic/Lock and awaited on dispose.
    /// </summary>
    private void LoadClientDisplaysOnce()
    {
        if (CanRefreshClientDisplays && !IsLocked && !_shuttingDown && _ready && ClientDisplays.Count == 0)
            RefreshClientDisplays.Execute(null);
    }
    private async Task RefreshClientDisplaysAsync()
    {
        if (IsLocked || _shuttingDown || !_ready) return;
        if (!CanManageClientWindows || _monitor is not IClientWindowArranger windows)
        { ClientWindowStatus = IsSample ? "Sample mode does not change real client windows." : "This desktop provider does not support window arrangement."; return; }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _displayStop = stop; long generation = _windowOperationsGeneration;
        try
        {
            var list = await windows.GetDisplaysAsync(stop.Token);
            if (stop.IsCancellationRequested || generation != _windowOperationsGeneration || IsLocked || _shuttingDown) return;
            if (list.Displays.Count > 32 || list.Displays.Any(display => !display.IsValid)
                || list.Displays.Select(display => display.Id).Distinct(StringComparer.Ordinal).Count() != list.Displays.Count)
                throw new InvalidOperationException("The display provider returned inconsistent monitor information.");
            string? selected = SelectedClientDisplay?.Display.Id ?? _preferredDisplayId;
            _loadingWindowDraft = true;
            try
            {
                ClientDisplays.Clear(); foreach (var display in list.Displays) ClientDisplays.Add(new(display));
                SelectedClientDisplay = ClientDisplays.FirstOrDefault(choice => choice.Display.Id == selected)
                    ?? ClientDisplays.FirstOrDefault(choice => choice.Display.IsPrimary) ?? ClientDisplays.FirstOrDefault();
            }
            finally { _loadingWindowDraft = false; }
            ClientWindowStatus = list.Explanation;
        }
        finally { if (ReferenceEquals(_displayStop, stop)) _displayStop = null; }
    }
    private async Task ArrangeClientsAsync()
    {
        if (!CanArrangeClients || _monitor is not IClientWindowArranger windows
            || SelectedClientDisplay is not { } display || !ClientDisplays.Contains(display))
        { ClientWindowStatus = "Refresh monitors and check clients, or select one client, before arranging."; return; }
        // A bad Layout preferences value is a typing mistake, not an operation failure: show it inline, like resizing.
        ClientWindowTileOptions options;
        try { options = ReadClientLayoutDraft().Tile!; }
        catch (ArgumentException error) { ClientWindowStatus = error.Message; return; }
        // Recipient identities and geometry preferences are immutable for this operation. Cells follow launch order, not PID.
        var selected = ClientWindowSlotOrder.Order(PinClientWindowSelection(), Volatile.Read(ref _latestQueue));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _arrangeStop = stop; long generation = _windowOperationsGeneration;
        IsArrangingClients = true; ClientPlacementResults.Clear();
        ClientWindowStatus = "Arranging the pinned client selection without requesting foreground focus…";
        try
        {
            var result = await windows.ArrangeAsync(Array.AsReadOnly(selected), display.Display.Id, options, stop.Token);
            if (_shuttingDown || !ReferenceEquals(_arrangeStop, stop)) return;
            bool stopped = stop.IsCancellationRequested || generation != _windowOperationsGeneration || IsLocked;
            PublishClientWindowResults(result, selected, stopped, positions: true);
        }
        finally
        {
            if (ReferenceEquals(_arrangeStop, stop)) _arrangeStop = null;
            IsArrangingClients = false;
        }
    }
    private void PublishClientWindowResults(ClientWindowArrangementResult result, SessionObservation[] selected, bool stopped, bool positions, bool contentSize = false)
    {
        if (result.Results.Count != selected.Length || result.Results.Any(item => !Enum.IsDefined(item.Status)
                || positions && item.Status == ClientWindowPlacementStatus.Applied && item.RequestedBounds is not { IsValid: true }
                || !positions && item.RequestedBounds is not null
                || !selected.Any(process => process.InstanceKey == item.Session.InstanceKey && process.Window == item.Session.Window))
            || result.Results.Select(item => (item.Session.InstanceKey, item.Session.Window)).Distinct().Count() != selected.Length)
        {
            if (!stopped) throw new InvalidOperationException("The window provider returned inconsistent client results.");
            _diagnostics.RecordFailure(ManagerSubsystem.ClientWindows); return;
        }
        foreach (var item in result.Results) ClientPlacementResults.Add(new(item, contentSize));
        ClientWindowStatus = stopped ? $"Stopped operation report: {result.Results.Count(item => item.Status == ClientWindowPlacementStatus.Applied)} of {selected.Length} {(contentSize ? "content sizes" : positions ? "positions" : "window states")} were confirmed. No automatic continuation; requests already issued cannot be undone."
            : result.Explanation;
    }
    private void ReportClientWindowFailure(Exception exception)
    { ClientWindowStatus = "The window operation could not finish. Refresh clients and monitors before retrying."; Fail(exception, ManagerSubsystem.ClientWindows); }
    private void StopClientWindowOperations()
    {
        Interlocked.Increment(ref _windowOperationsGeneration);
        if (_arrangeStop is not null || _displayStop is not null || _arrivalStop is not null)
            ClientWindowStatus = "Window operations stopped. Requests already sent to Windows cannot be undone automatically.";
        // Clients waiting for an automatic arrangement are dropped; they count as seen, so they are not arranged later.
        _arrivedClients = null;
        _arrangeStop?.Cancel(); _displayStop?.Cancel(); _arrivalStop?.Cancel();
        if (_monitor is IClientWindowBatchControl batch) batch.CancelWindowOperations();
        else if (_monitor is IClientWindowArranger arranger) arranger.CancelWindowOperations();
        else (_monitor as IClientWindowResizer)?.CancelWindowOperations();
    }
    private async Task DisposeClientWindowFeaturesAsync()
    {
        StopClientWindowOperations();
        try { await Task.WhenAll(RefreshClientDisplays.Execution, ArrangeClients.Execution, MinimizeCheckedClients.Execution, RestoreCheckedClients.Execution, ResizeCheckedClients.Execution, _arrivalWork); }
        catch (OperationCanceledException) { }
        PropertyChanged -= ClientWindowShellStateChanged;
        foreach (var row in Sessions) row.PropertyChanged -= SessionSelectionChanged;
    }

    public string? BuildRequestedDestinationLink() => ServerBrowser.ReadyDestination?.ToWebStartUri().AbsoluteUri;
    /// <summary>Public game page only (/games/{placeId}): a server job, share or private code never reaches the browser.</summary>
    public string? BuildRequestedGamePageLink() => ServerBrowser.ReadyDestination?.ToGamePageUri().AbsoluteUri;
    public string? BuildReportedDestinationLink(SessionRow row) => BuildReportedDestinationLink(row, includeServer: true);
    public string? BuildReportedDestinationLink(SessionRow row, bool includeServer)
    {
        if (!CanUseDirectories() || row is null || !Sessions.Contains(row)) return null;
        var key = new SessionProcessKey(null, row.Observation.ProcessId, row.Observation.ProcessStartUtcTicks);
        // Re-evaluate the lease at the click, independently of the last visible UI refresh.
        var latest = Volatile.Read(ref _latestDesktop);
        return _logProjection.BuildCurrentLink(key, latest, _logCorrelator?.Snapshot, DateTimeOffset.UtcNow, includeServer);
    }

    /// <summary>
    /// Join the same game (place only) or the same server (place and job) a running client reports: the click-time
    /// evidence becomes the requested destination and Ready when you are opens. Nothing is queued or launched; a stale,
    /// ambiguous or expired log yields false and changes nothing.
    /// </summary>
    public bool JoinSameAsReported(SessionRow row, bool sameServer)
    {
        if (row is null) return false;
        string instance = row.InstanceKey;
        string? link = BuildReportedDestinationLink(row, sameServer);
        if (link is null || RobloxDestinationParser.Parse(link).RequestedDestination is not { } expected) return false;
        long generation = _directoryGeneration;
        ServerBrowser.LinkInput = link;
        if (!CanUseDirectories() || generation != _directoryGeneration || !Sessions.Contains(row) || row.InstanceKey != instance
            || ServerBrowser.LinkInput != link) return false;
        ServerBrowser.ResolveInput();
        if (ServerBrowser.Destination is not { } chosen || chosen.PlaceId != expected.PlaceId
            || !string.Equals(chosen.JobId, expected.JobId, StringComparison.Ordinal) || !CanUseDirectories() || generation != _directoryGeneration) return false;
        Navigate.Execute("JoinGame"); SetJoinStep(3);
        return CanUseDirectories() && generation == _directoryGeneration && IsJoinGame && IsJoinReadyStep
            && Sessions.Contains(row) && row.InstanceKey == instance;
    }
    private void LoadClientLayoutDraft(ClientWindowLayoutSettings settings)
    {
        settings = settings.Validated(); _preferredDisplayId = settings.PreferredDisplayId;
        var options = settings.Tile!;
        TileColumns = options.Columns?.ToString(CultureInfo.InvariantCulture) ?? "";
        TileGap = options.GapDip.ToString(CultureInfo.InvariantCulture);
        TileMinimumWidth = options.MinimumWidthDip.ToString(CultureInfo.InvariantCulture);
        TileMinimumHeight = options.MinimumHeightDip.ToString(CultureInfo.InvariantCulture);
        ClientArrivalAction = settings.ArrivalAction;
    }
    private ClientWindowLayoutSettings ReadClientLayoutDraft()
    {
        static int Number(string text, string label)
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                throw new ArgumentException($"Enter a whole number for {label}.");
            return value;
        }
        int? columns = string.IsNullOrWhiteSpace(TileColumns) ? null : Number(TileColumns, "columns (or leave blank for automatic)");
        int gap = Number(TileGap, "gap"), width = Number(TileMinimumWidth, "minimum width"), height = Number(TileMinimumHeight, "minimum height");
        if (columns is < 1 or > 128) throw new ArgumentException("Columns must be between 1 and 128, or blank for automatic.");
        if (gap is < 0 or > 64) throw new ArgumentException("Gap must be between 0 and 64 logical pixels.");
        if (width is < 160 or > 8192) throw new ArgumentException("Minimum width must be between 160 and 8192 logical pixels.");
        if (height is < 120 or > 8192) throw new ArgumentException("Minimum height must be between 120 and 8192 logical pixels.");
        return new ClientWindowLayoutSettings(new(gap, width, height, columns), _preferredDisplayId, ClientArrivalAction).Validated();
    }
    private bool TryReadClientLayoutDraft(out ClientWindowLayoutSettings? settings)
    {
        try { settings = ReadClientLayoutDraft(); return true; }
        catch (Exception error) when (error is ArgumentException or System.IO.InvalidDataException) { settings = null; return false; }
    }
}
