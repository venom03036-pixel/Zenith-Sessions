using System.Collections.ObjectModel;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

public enum ServerOrderChoice { ServerDefault, MostFull, MostEmpty, BestPing }
public sealed record ServerOrderOption(ServerOrderChoice Value,string Label);

public sealed class PublicServerRow(RobloxPublicServer server)
{
    public RobloxPublicServer Server { get; }=server;
    public string JobId=>Server.JobId;
    public string Occupancy=>$"{Server.Playing} / {Server.MaxPlayers}";
    public string Availability=>Server.HasOpenSlots?"Space reported":"Full when observed";
    public string Ping=>Server.Ping is >=0?$"{Server.Ping} ms":"Unavailable";
    public string Observed=>Server.ObservedUtc.ToLocalTime().ToString("HH:mm:ss");
}

/// <summary>User-initiated, one-page public browsing. No polling, launch, vault access or raw-link persistence.</summary>
public sealed class ServerBrowserViewModel : Observable,IAsyncDisposable
{
    private readonly IRobloxPublicServerDirectory _directory;
    private readonly Func<bool> _canUse;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _lifetime=new();
    private CancellationTokenSource? _requestStop;
    private Task _active=Task.CompletedTask;
    private Task? _dispose;
    private long _generation;
    private string _input="",_status="Paste a Roblox game or public server link, or use an account's preferred game.";
    private string? _cursor;
    private DateTimeOffset _notBefore;
    private bool _busy,_stopping,_rowsUsable,_replacingDestination;
    private RobloxRequestedDestination? _destination;
    private PublicServerRow? _selected;
    private RobloxPublicServer? _requestedServerObservation;
    // Retain one raw page for reversible page-local ping sorting. Remote query changes discard it.
    private PublicServerRow[] _pageRows=[];
    private ServerOrderChoice _serverOrder;
    private bool _hideFull;
    public IReadOnlyList<ServerOrderOption> ServerOrderChoices {get;}=Array.AsReadOnly<ServerOrderOption>([
        new(ServerOrderChoice.ServerDefault,"Server order"),
        new(ServerOrderChoice.MostFull,"Most full · Roblox order"),
        new(ServerOrderChoice.MostEmpty,"Most empty · Roblox order"),
        new(ServerOrderChoice.BestPing,"Best reported ping · this page")]);
    public ServerOrderChoice SelectedServerOrder
    {
        get=>_serverOrder;
        set
        {
            if(!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            var previousOrder=QuerySortOrder;
            if(Set(ref _serverOrder,value))
            {
                if(previousOrder!=QuerySortOrder) RestartForQueryChange();else ApplyPageView();
                Raise(nameof(OrderingExplanation));
            }
        }
    }
    public bool HideFull
    {
        get=>_hideFull;
        set {if(Set(ref _hideFull,value)) {RestartForQueryChange();Raise(nameof(OrderingExplanation));}}
    }
    private RobloxPublicServerSortOrder QuerySortOrder=>SelectedServerOrder==ServerOrderChoice.MostFull?RobloxPublicServerSortOrder.Desc:RobloxPublicServerSortOrder.Asc;
    public string OrderingExplanation=>(SelectedServerOrder switch
    {
        ServerOrderChoice.MostFull=>"Roblox is asked for occupancy descending across pages. Capacity is advisory and can change.",
        ServerOrderChoice.MostEmpty=>"Roblox is asked for occupancy ascending across pages. Capacity is advisory and can change.",
        ServerOrderChoice.BestPing=>"Lowest reported ping on this page only; unknown ping is last. Requests use ascending occupancy. This is not a client latency test or a global ranking.",
        _=>"Original ascending server order from Roblox. Browse starts over and Next loads another page."
    })+(HideFull?" Roblox is asked to exclude full servers before paging.":" Full servers may be included.");
    public string PageSummary=>_pageRows.Length==0?"No server observations loaded. Browse explicitly to load at most 25 rows."
        :HideFull && Servers.Count==0?$"All {_pageRows.Length} observations on this page were full. Show full servers or use Next when available."
        :$"Showing {Servers.Count} of {_pageRows.Length} observations on this page"+(HideFull?" · full servers hidden.":".");
    public string LinkInput
    {
        get=>_input;
        set
        {
            if(value is null || value.Length>4096) {Status="The destination link is too long.";return;}
            bool wasPending=HasDestinationDraft;
            if(Set(ref _input,value) && wasPending!=HasDestinationDraft) RaiseDestinationAvailability();
        }
    }
    public string Status { get=>_status;private set=>Set(ref _status,value); }
    public bool IsBusy { get=>_busy;private set=>Set(ref _busy,value); }
    public RobloxRequestedDestination? Destination=>_destination;
    public bool HasDestinationDraft=>_input.Length!=0;
    public RobloxRequestedDestination? ReadyDestination=>!HasDestinationDraft && !_replacingDestination && !_stopping
        && !_lifetime.IsCancellationRequested && _canUse()?_destination:null;
    public bool CanUseDestination=>ReadyDestination is not null;
    // Only explicit UseServer captures this immutable observation. Selection and links are requests, not evidence.
    public RobloxPublicServer? RequestedServerObservation=>_requestedServerObservation;
    public RequestedServerCapacityAssessment AssessRequestedCapacity(int recipientCount)=>AssessRequestedCapacity(_destination,recipientCount);
    public RequestedServerCapacityAssessment AssessRequestedCapacity(RobloxRequestedDestination? requested,int recipientCount)=>RequestedServerCapacity.Assess(
        requested,_stopping || !_canUse() || _lifetime.IsCancellationRequested?null:_requestedServerObservation,recipientCount,_clock.GetUtcNow());
    public string DestinationSummary=>_destination is null?"No requested destination":$"Place {_destination.PlaceId}"+(_destination.JobId is null?" · any eligible server":$" · server {_destination.JobId}");
    public bool HasDestination=>_destination is not null;
    public bool CanNext=>CanUseDestination && _cursor is not null && !IsBusy;
    public ObservableCollection<PublicServerRow> Servers {get;}=[];
    public bool CanUseServer=>CanUseDestination && !IsBusy
        && SelectedServer is { } row && IsCurrentRow(row) && IsFresh(row.Server,_clock.GetUtcNow());
    public PublicServerRow? SelectedServer {get=>_selected;set {if(Set(ref _selected,value)) Raise(nameof(CanUseServer));}}
    public Command Resolve {get;}
    public Command UseServer {get;}
    public Command Cancel {get;}
    public AsyncCommand Browse {get;}
    public AsyncCommand Next {get;}
    public ServerBrowserViewModel(IRobloxPublicServerDirectory directory,Func<bool>? canUse=null,TimeProvider? clock=null)
    {
        _directory=directory;_canUse=canUse??(()=>true);_clock=clock??TimeProvider.System;
        Resolve=new(_=>ResolveInput());UseServer=new(_=>UseSelectedServer());Cancel=new(_=>CancelWork(false));
        Browse=new(_=>StartBrowseAsync(false),ReportFailure);Next=new(_=>StartBrowseAsync(true),ReportFailure);
    }
    public void ResolveInput()
    {
        if(_replacingDestination || _stopping || !_canUse() || _lifetime.IsCancellationRequested) return;
        var parsed=RobloxDestinationParser.Parse(LinkInput);
        // Opaque/private access codes are not retained in controller state after parsing.
        ReplaceDestination(parsed.RequestedDestination,parsed.Explanation);
    }
    public void UsePreferredPlace(long placeId)
    {
        if(_replacingDestination || _stopping || !_canUse() || _lifetime.IsCancellationRequested) return;
        ReplaceDestination(placeId>0?new RobloxRequestedDestination(placeId):null,placeId>0
            ?"Preferred place selected as a request. No account was launched or joined."
            :"Choose a positive place ID before browsing.");
    }
    public bool UseFavoriteDestination(FavoriteDestination favorite)
    {
        if(_replacingDestination || _stopping || !_canUse() || _lifetime.IsCancellationRequested) return false;
        ArgumentNullException.ThrowIfNull(favorite);favorite=favorite.Validated();
        if(favorite.Revision<1) throw new ArgumentException("Only a saved favorite may be selected.",nameof(favorite));
        ReplaceDestination(favorite.RequestedDestination,"Favorite selected as a requested destination. Access, capacity, account identity and arrival remain unverified.");
        return true;
    }
    private void UseSelectedServer()
    {
        if(!CanUseDestination || IsBusy || SelectedServer is not { } row || !IsCurrentRow(row)) return;
        var now=_clock.GetUtcNow();
        if(!IsFresh(row.Server,now))
        {ClearRequestedObservation();Status="This server observation is stale. Browse again before selecting it.";Raise(nameof(CanUseServer));return;}
        var destination=row.Server.RequestedDestination;
        _destination=destination;
        _requestedServerObservation=row.Server with {JobId=destination.JobId!};
        Status=row.Server.HasOpenSlots?"Public server selected. Its capacity is advisory; arrival still requires verification.":"This server was full when observed. The request is not a reserved seat.";
        RaiseDestination();Raise(nameof(RequestedServerObservation));
    }
    private Task StartBrowseAsync(bool next)
    {
        if(_stopping || !_canUse() || _lifetime.IsCancellationRequested) return Task.CompletedTask;
        if(IsBusy) {Status="A server request is still stopping or running. Wait for it to finish.";return Task.CompletedTask;}
        if(ReadyDestination is not { } destination)
        {Status=HasDestinationDraft?"Use or discard the pending destination before browsing.":"Choose a valid place before browsing public servers.";return Task.CompletedTask;}
        if(next && _cursor is null) return Task.CompletedTask;
        if(_clock.GetUtcNow()<_notBefore) {Status="Roblox asked us to wait before another server request. No request was sent.";return Task.CompletedTask;}
        var generation=_generation;var query=new RobloxPublicServerQuery(destination.PlaceId,PageSize:25,MaxPages:1,Cursor:next?_cursor:null,
            SortOrder:QuerySortOrder,ExcludeFullGames:HideFull);
        _rowsUsable=false;ClearRequestedObservation();
        _requestStop=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);IsBusy=true;Raise(nameof(CanNext));Raise(nameof(CanUseServer));
        Status="Loading one page of public server observations…";
        return _active=FetchAsync(query,generation,_requestStop);
    }
    private async Task FetchAsync(RobloxPublicServerQuery query,long generation,CancellationTokenSource stop)
    {
        bool Current()=>!_stopping && !stop.IsCancellationRequested && generation==_generation && _canUse();
        void RejectPage()
        {
            ClearRows();ClearRequestedObservation();
            if(Current())Status="The server provider returned inconsistent or stale results. No rows were accepted.";
        }
        try
        {
            if(!Current())return;
            var result=await _directory.BrowseAsync(query,stop.Token);
            if(!Current()) return;
            if(result is null || !Enum.IsDefined(result.Status) || result.Servers is null)
            {RejectPage();return;}
            if((result.RetryAfter??(result.Status==RobloxPublicServerStatus.RateLimited?TimeSpan.FromSeconds(30):null)) is { } retry)
            {
                var now=_clock.GetUtcNow();var delay=retry<TimeSpan.FromSeconds(1)?TimeSpan.FromSeconds(1):retry;
                _notBefore=delay>DateTimeOffset.MaxValue-now?DateTimeOffset.MaxValue:now+delay;
            }
            if(result.Status is RobloxPublicServerStatus.Complete or RobloxPublicServerStatus.PageLimit)
            {
                var now=_clock.GetUtcNow();
                if(result.Servers.Count>25 || result.Servers.Any(s=>s is null || s.PlaceId!=query.PlaceId || !RobloxRequestedDestination.TryNormalizeJobId(s.JobId,out _)
                    || s.Playing<0 || s.MaxPlayers<=0 || !IsFresh(s,now))
                    || result.Servers.Select(s=>Guid.ParseExact(s.JobId,"D")).Distinct().Count()!=result.Servers.Count
                    || result.NextPageCursor is { } cursor && (cursor.Length is <1 or >2048 || cursor.Any(char.IsControl)))
                {RejectPage();return;}
                ClearRows();
                if(!Current())return;
                _pageRows=result.Servers.Select(server=>new PublicServerRow(server)).ToArray();_rowsUsable=true;ApplyPageView();
                if(!Current())return;
                _cursor=result.NextPageCursor;
            }
            else ClearRows();
            if(!Current())return;
            Status=(result.Status is RobloxPublicServerStatus.Complete or RobloxPublicServerStatus.PageLimit) && result.Servers.Count==0
                ?"No public servers were returned in this response. Enter an eligible public server link or browse again later."
                :result.Explanation;
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
        catch(Exception ex)
        {if(Current()) {ClearRows();if(Current())ReportFailure(ex);}}
        finally
        {
            if(ReferenceEquals(_requestStop,stop)) _requestStop=null;
            stop.Dispose();IsBusy=false;Raise(nameof(CanNext));Raise(nameof(CanUseServer));
        }
    }
    public void CancelWork(bool clear)
    {
        if(clear) {ReplaceDestination(null,"Destination cleared. Choose a fresh request when controls are available.");return;}
        InvalidatePage();ClearRequestedObservation();Raise(nameof(CanUseServer));
        Status="Server browsing cancelled. No game or client was changed.";
    }
    private void RestartForQueryChange()
    {
        // Cursors belong to the exact remote query. Never send one with another order/filter.
        CancelWork(false);ClearRows();
        Status="Server order or hide-full changed. Browse to load the first page; no request was sent.";
    }
    private void ApplyPageView()
    {
        long generation=_generation;
        var selected=SelectedServer;
        IEnumerable<PublicServerRow> rows=_pageRows;
        if(HideFull) rows=rows.Where(row=>row.Server.HasOpenSlots);
        // Stable ordering keeps the original server order for equal/unknown measurements.
        rows=SelectedServerOrder switch
        {
            ServerOrderChoice.BestPing=>rows.OrderBy(row=>row.Server.Ping is >=0?0:1)
                .ThenBy(row=>row.Server.Ping is >=0?row.Server.Ping.GetValueOrDefault():0),
            _=>rows
        };
        // Materialize before the collection reset so bindings cannot change the source being enumerated.
        var visible=rows.ToArray();Servers.Clear();
        foreach(var row in visible)
        {
            if(generation!=_generation)return;
            Servers.Add(row);
        }
        if(generation!=_generation)return;
        var now=_clock.GetUtcNow();
        SelectedServer=selected is not null && Servers.Contains(selected) && IsFresh(selected.Server,now)?selected:null;
        Raise(nameof(PageSummary));Raise(nameof(CanUseServer));
    }
    private void ClearRows() {_rowsUsable=false;_pageRows=[];_cursor=null;Servers.Clear();SelectedServer=null;Raise(nameof(PageSummary));Raise(nameof(CanNext));}
    private bool IsCurrentRow(PublicServerRow row)=>_rowsUsable && _pageRows.Contains(row) && Servers.Contains(row)
        && _destination?.PlaceId==row.Server.PlaceId;
    private static bool IsFresh(RobloxPublicServer server,DateTimeOffset now)=>server.ObservedUtc!=default
        && server.ObservedUtc-now<=RequestedServerCapacity.MaximumFutureSkew && now-server.ObservedUtc<=RequestedServerCapacity.MaximumAge;
    private void ClearRequestedObservation()
    {if(_requestedServerObservation is not null) {_requestedServerObservation=null;Raise(nameof(RequestedServerObservation));}}
    private void ReplaceDestination(RobloxRequestedDestination? value,string status)
    {
        bool wasReplacing=_replacingDestination;
        bool inputChanged=HasDestinationDraft,observationChanged=_requestedServerObservation is not null;
        _replacingDestination=true;
        // Install the new state before notifying a binding or a synchronous subscriber.
        _destination=value;_input="";_requestedServerObservation=null;_status=status;
        try
        {
            InvalidatePage();ClearRows();
            if(inputChanged)Raise(nameof(LinkInput));
            if(observationChanged)Raise(nameof(RequestedServerObservation));
            RaiseDestination();Raise(nameof(Status));
        }
        finally
        {
            _replacingDestination=wasReplacing;
            RaiseDestinationAvailability();
        }
    }
    private void InvalidatePage()
    {_rowsUsable=false;Interlocked.Increment(ref _generation);_requestStop?.Cancel();}
    private void RaiseDestination()
    {Raise(nameof(Destination));Raise(nameof(DestinationSummary));Raise(nameof(HasDestination));}
    private void RaiseDestinationAvailability()
    {Raise(nameof(HasDestinationDraft));Raise(nameof(CanUseDestination));Raise(nameof(CanNext));Raise(nameof(CanUseServer));}
    private void ReportFailure(Exception _)
    {if(!_stopping && !_lifetime.IsCancellationRequested && _canUse()) Status="Public server browsing failed. Your profiles are unchanged; retry when the connection is available.";}
    public ValueTask DisposeAsync()=>new(_dispose??=StopAsync());
    private async Task StopAsync()
    {
        _stopping=true;_lifetime.Cancel();CancelWork(true);
        // The provider owns its native/HTTP deadline. Cancellation fences publication, but cannot abandon its task.
        try {await _active;}
        catch(OperationCanceledException) { }
        finally {_lifetime.Dispose();}
    }
}
