using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

public sealed class PublicGameSearchRow(PublicRobloxGameSearchResult result):Observable
{
    private ImageSource? _art;private bool _current;
    public PublicRobloxGameSearchResult Result {get;}=result.Validated();
    /// <summary>Decoded, frozen card picture owned by the search page; cleared whenever the page is replaced or cancelled.</summary>
    public ImageSource? Art {get=>_art;internal set {if(Set(ref _art,value))Raise(nameof(HasArt));}}
    public bool HasArt=>_art is not null;
    /// <summary>True while this row is the requested destination chosen from this page. Presentation only.</summary>
    public bool IsCurrent {get=>_current;internal set=>Set(ref _current,value);}
    internal bool ArtAttempted;
    internal double RatingValue=>Result is {UpVotes:{ } up,DownVotes:{ } down} && up+down>0?up*100.0/(up+down):-1;
    public string Name=>Result.Name;
    public string Identifiers=>$"Root place {Result.RootPlaceId.ToString(CultureInfo.InvariantCulture)} · universe {Result.UniverseId.ToString(CultureInfo.InvariantCulture)}";
    public string Creator=>Result.CreatorName is { } name?"Creator: "+name:Result.CreatorId is { } id?$"Creator ID {id.ToString(CultureInfo.InvariantCulture)}":"Creator unavailable";
    public string Activity=>Result.Playing is { } count?$"{count:N0} players across the experience":"Player count unavailable";
    public string Promotion=>Result.Sponsored switch {true=>"Sponsored result",false=>"Not marked sponsored",_=>"Promotion status unavailable"};
    // Card labels. The verified badge and the Sponsored label are shown only when Roblox reported them as true; unknown stays unmarked.
    public bool IsCreatorVerified=>Result.CreatorVerified==true;
    public bool IsSponsored=>Result.Sponsored==true;
    public string CreatorLine=>Result.CreatorName is { } name?"by "+name:Creator;
    public string PlayingLabel=>Result.Playing is { } count?CompactCount(count)+" playing":"Player count unavailable";
    public string? RatingLabel=>Result is {UpVotes:{ } up,DownVotes:{ } down} && up+down>0
        ?Math.Round(up*100.0/(up+down),MidpointRounding.AwayFromZero).ToString("0",CultureInfo.InvariantCulture)+"% liked":null;
    public bool HasRating=>RatingLabel is not null;
    public string AutomationName=>Name+". "+(IsSponsored?"Sponsored. ":"")+Identifiers+". "+Creator+". Public listing, not verified access or arrival.";
    private static string CompactCount(long count)=>count<1_000?count.ToString(CultureInfo.InvariantCulture)
        :count<1_000_000?(count/1_000.0).ToString("0.#",CultureInfo.InvariantCulture)+"K"
        :(count/1_000_000.0).ToString("0.#",CultureInfo.InvariantCulture)+"M";
}

/// <summary>
/// UI-context owner for one explicit search/use operation and one bounded page. Directory, browser
/// and details are borrowed. No queries on typing, implicit result selection, retries or timers.
/// </summary>
public sealed class DestinationSearchViewModel:Observable,IAsyncDisposable
{
    public const int MaximumManualPages=10;
    public const int PageSize=PublicRobloxGameArtLimits.MaximumBatch;
    public const string SortRelevance="Relevance",SortPlaying="Playing now",SortRating="Rating";
    private readonly GameCardArtLoader _art;
    private readonly Func<RobloxShareLink,Task>? _shareLinks;
    private int _visibleCount=PageSize;
    private string _sort=SortRelevance;
    private readonly IPublicRobloxGameSearchDirectory? _directory;
    private readonly ServerBrowserViewModel _browser;
    private readonly GameDetailsViewModel _details;
    private readonly Func<bool> _canUse;
    private readonly Action<Exception> _failed;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _lifetime=new();
    private Work? _work;
    private Task _active=Task.CompletedTask;
    private Task? _dispose;
    private long _generation;
    private bool _busy,_disposed,_mutatingBrowser;
    private string _expectedInput="";
    private RobloxRequestedDestination? _expectedDestination;
    private PublicRobloxGameSearchPage? _page;
    private string? _pageInput;
    private PublicGameSearchRow? _selected;
    private DateTimeOffset _notBefore;
    private readonly HashSet<string> _visitedTokens=new(StringComparer.Ordinal);
    private int _pagesVisited;
    private string? _pagingStop;
    private string _status="Enter a game name, place ID or supported Roblox link, then choose Search / Use.";
    public ObservableCollection<PublicGameSearchRow> Results {get;}=[];
    /// <summary>The sorted first N rows of the page (N grows by PageSize per Show more). Pictures load only for these.</summary>
    public ObservableCollection<PublicGameSearchRow> VisibleResults {get;}=[];
    public IReadOnlyList<string> SortOptions {get;}=[SortRelevance,SortPlaying,SortRating];
    public string SelectedSort
    {get=>_sort;set {if(!SortOptions.Contains(value) || !Set(ref _sort,value))return;_art.Cancel();RebuildVisible();LoadVisibleArt();}}
    public bool CanShowMore=>!_disposed && VisibleResults.Count<Results.Count;
    public Task ArtOperation=>_art.Active;
    public Command ShowMore {get;}
    public AsyncCommand UseCard {get;}
    public PublicGameSearchRow? SelectedResult
    {get=>_selected;set {if(value is null || Results.Any(row=>ReferenceEquals(row,value))) {Set(ref _selected,value);Raise(nameof(CanUseSelected));}}}
    public string Status=>_status;
    public bool IsBusy=>_busy;
    public bool HasResults=>Results.Count>0;
    public bool CanSearch=>Allowed && !_busy && !string.IsNullOrWhiteSpace(_browser.LinkInput);
    public bool CanNext=>Allowed && !_busy && _pagingStop is null && _page?.NextPageToken is not null && PageCurrent();
    public bool CanUseSelected=>Allowed && !_busy && SelectedResult is { } selected
        && Results.Any(row=>ReferenceEquals(row,selected)) && PageCurrent();
    public bool CanCancel=>!_disposed && (_busy || _page is not null || _browser.LinkInput.Length>0);
    public string PageSummary=>_page is null?"Search results — choose a destination. Searching does not change the current request."
        :$"{Results.Count} public results on this page · retrieved {_page.RetrievedUtc.ToLocalTime():T}. Select one, then use it."
            +(_page.ResultsTruncated?" This response omitted results; refine the name. A continuation does not recover omitted rows.":" This is not exhaustive search coverage.")
            +(_pagingStop is null?"":" "+_pagingStop);
    public Task ActiveOperation=>_active;
    public AsyncCommand SearchOrUse {get;}
    public AsyncCommand Next {get;}
    public AsyncCommand UseSelected {get;}
    public Command Cancel {get;}
    private bool Allowed=>!_disposed && !_lifetime.IsCancellationRequested && _canUse();
    public DestinationSearchViewModel(IPublicRobloxGameSearchDirectory? directory,ServerBrowserViewModel browser,
        GameDetailsViewModel details,Func<bool> canUse,Action<Exception>? failed=null,TimeProvider? clock=null,IPublicRobloxGameArtDirectory? art=null,
        Func<RobloxShareLink,Task>? shareLinkHandler=null)
    {
        _directory=directory;_browser=browser;_details=details;_canUse=canUse;_failed=failed??(_=>{});_clock=clock??TimeProvider.System;
        _art=new(art,_failed);_shareLinks=shareLinkHandler;
        SearchOrUse=new(_=>SearchOrUseAsync(),ReportFailure);Next=new(_=>NextAsync(),ReportFailure);
        UseSelected=new(_=>UseSelectedAsync(),ReportFailure);Cancel=new(_=>CancelWork());
        ShowMore=new(_=>ShowMoreRows());UseCard=new(parameter=>parameter is PublicGameSearchRow row?UseCardAsync(row):Task.CompletedTask,ReportFailure);
        browser.PropertyChanged+=SourceChanged;
    }
    public void RefreshAvailability()=>NotifyAvailability();
    public Task SearchOrUseAsync(CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();if(!Admit()) return Task.CompletedTask;
        string input=_browser.LinkInput;
        if(string.IsNullOrWhiteSpace(input)) {SetStatus("Enter a game name, place ID or supported Roblox link first.");return Task.CompletedTask;}
        if(_shareLinks is not null && RobloxShareLinkParser.TryParse(input,out var share))
        {
            // A share code is never searched or logged: clear the box, then hand the link to the Private server check, which owns it.
            Invalidate("Checking the share link under Browse servers → Private server. Its code was not searched.",clearInput:true);
            return _shareLinks(share!);
        }
        PublicRobloxGameSearchQuery? query;
        try {query=new PublicRobloxGameSearchQuery(input,Guid.NewGuid()).Validated();}
        catch(ArgumentException) {query=null;}
        if(query is not null && _directory is null) {Invalidate("Public name search is unavailable. Use a place ID or supported link.",false);return Task.CompletedTask;}
        if(query is not null && CoolingDown()) return Task.CompletedTask;
        return Start(query is null?Kind.Direct:Kind.Search,input,query,null,ct);
    }
    public Task NextAsync(CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();if(!Admit()) return Task.CompletedTask;
        if(!CanNext || _page is not {NextPageToken:{ } next} page)
        {if(_page is not null && !PageCurrent()) Invalidate("Search results expired or changed. Search explicitly again.",false);else SetStatus(_pagingStop??"No next search page is available.");return Task.CompletedTask;}
        if(CoolingDown()) return Task.CompletedTask;
        return Start(Kind.Search,_browser.LinkInput,page.Query with {PageToken=next},null,ct);
    }
    public Task UseSelectedAsync(CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();if(!Admit()) return Task.CompletedTask;
        if(!CanUseSelected || SelectedResult is not { } row)
        {if(_page is not null && !PageCurrent()) Invalidate("Search results expired. Search explicitly again.",false);else SetStatus("Select a current search result before choosing a destination.");return Task.CompletedTask;}
        return Start(Kind.Use,_browser.LinkInput,null,row,ct);
    }
    /// <summary>Clicking a card uses it: the page is kept and the card is marked current. Nothing is queued or launched.</summary>
    public Task UseCardAsync(PublicGameSearchRow row,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();if(!Admit()) return Task.CompletedTask;
        if(row is null || !Results.Any(current=>ReferenceEquals(current,row)) || !PageCurrent())
        {if(_page is not null && !PageCurrent()) Invalidate("Search results expired. Search explicitly again.",false);else SetStatus("Choose a card from the current search results.");return Task.CompletedTask;}
        _selected=row;Raise(nameof(SelectedResult));
        return Start(Kind.Use,_browser.LinkInput,null,row,ct);
    }
    private void ShowMoreRows() {if(!CanShowMore)return;_visibleCount+=PageSize;RebuildVisible();LoadVisibleArt();}
    private bool Admit()
    {
        if(!Allowed) return false;
        if(_busy || !_active.IsCompleted) {SetStatus("The previous search or use operation is still running or stopping. Wait for it to finish.");return false;}
        return true;
    }
    private bool CoolingDown()
    {if(_clock.GetUtcNow()>=_notBefore)return false;SetStatus("The public service requested a cooldown. No search request was sent; try again after that period.");return true;}
    private Task Start(Kind kind,string input,PublicRobloxGameSearchQuery? query,PublicGameSearchRow? selected,CancellationToken caller)
    {
        var stop=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token,caller);
        var work=new Work(kind,Interlocked.Increment(ref _generation),input,query,selected,stop);
        var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _work=work;_busy=true;_status=kind==Kind.Search?"Searching public experiences… The current requested destination is unchanged.":"Choosing the explicit destination…";
        Task active=_active=RunAsync(work,gate.Task);
        try
        {
            if(kind==Kind.Search && query?.PageToken is null) {_visitedTokens.Clear();_pagesVisited=0;_pagingStop=null;}
            // Using a card keeps the page so the grid can mark it current; searching replaces the page and its pictures.
            if(kind!=Kind.Use) {_page=null;_pageInput=null;_selected=null;ClearRows();}
            Raise(nameof(Status));Raise(nameof(IsBusy));Raise(nameof(HasResults));Raise(nameof(PageSummary));Raise(nameof(SelectedResult));NotifyAvailability();
        }
        finally {gate.TrySetResult();}
        return active;
    }
    private bool ContextCurrent(Work work)=>Allowed && ReferenceEquals(_work,work)
        && work.Generation==Interlocked.Read(ref _generation) && string.Equals(_browser.LinkInput,work.Input,StringComparison.Ordinal);
    private bool Current(Work work)=>ContextCurrent(work) && !work.Stop.IsCancellationRequested;
    private async Task RunAsync(Work work,Task gate)
    {
        try
        {
            await gate;if(!Current(work))return;
            if(work.Kind==Kind.Search) await ReadPageAsync(work);
            else await ChooseAsync(work);
        }
        catch(OperationCanceledException) when(work.Stop.IsCancellationRequested)
        {if(ContextCurrent(work)) SetStatus("Destination search stopped. The requested destination was preserved.");}
        catch(Exception)
        {if(Current(work)) {ClearPage();if(Current(work)) SetStatus("The public destination operation could not finish. Retry explicitly; no account was launched.");}ReportSafeFailure();}
        finally
        {
            // A borrowed provider may finish normally despite caller cancellation, or the
            // caller can cancel during our pre-request notifications. Neither path throws.
            if(ContextCurrent(work) && work.Stop.IsCancellationRequested)
                SetStatus("Destination search stopped. The requested destination was preserved.");
            if(ReferenceEquals(_work,work)) _work=null;
            work.Stop.Dispose();_busy=false;Raise(nameof(IsBusy));NotifyAvailability();
        }
    }
    private async Task ReadPageAsync(Work work)
    {
        var query=work.Query!.Validated();var result=await _directory!.SearchGamesAsync(query,work.Stop.Token);
        if(!Current(work))return;ObserveRetry(result.RetryAfter);
        if(result.State!=PublicDirectoryState.Success || result.Value is null)
        {ClearPage();if(Current(work))SetStatus(result.State==PublicDirectoryState.Deferred?"Public search is deferred. Respect the service cooldown and retry explicitly.":"No public search page is available. The current requested destination is unchanged.");return;}
        var page=result.Value.Validated();
        if(page.Query!=query || !Fresh(page.RetrievedUtc)) throw new InvalidDataException("Public search context was inconsistent.");
        var rows=page.Items.Select(item=>new PublicGameSearchRow(item)).ToArray();
        _pagesVisited++;if(query.PageToken is { } used) _visitedTokens.Add(used);
        _pagingStop=page.NextPageToken is { } next && _visitedTokens.Contains(next)?"The service repeated a continuation. Refine the name before browsing again.":
            _pagesVisited>=MaximumManualPages && page.NextPageToken is not null?"The ten-page manual browsing budget was reached. Refine the name to start a new search.":null;
        _page=page;_pageInput=work.Input;_selected=null;_status=rows.Length==0?"No public experiences matched this page. Refine the name; the current request is unchanged.":"Search results — choose a destination. No result has been selected automatically.";
        if(_pagingStop is not null) _status=_pagingStop;
        ClearRows();if(!Current(work))return;
        long? chosen=_browser.Destination is {JobId:null} destination?destination.PlaceId:null;
        foreach(var row in rows) {row.IsCurrent=chosen==row.Result.RootPlaceId;Results.Add(row);if(!Current(work))return;}
        _visibleCount=PageSize;RebuildVisible();LoadVisibleArt();
        Raise(nameof(SelectedResult));Raise(nameof(HasResults));Raise(nameof(PageSummary));Raise(nameof(Status));NotifyAvailability();
    }
    private async Task ChooseAsync(Work work)
    {
        RobloxRequestedDestination? expected=work.Kind==Kind.Use?new(work.Selected!.Result.RootPlaceId):RobloxDestinationParser.Parse(work.Input).RequestedDestination;
        if(!Current(work))return;
        _expectedInput="";_expectedDestination=expected;_mutatingBrowser=true;
        try
        {
            work.Input="";
            if(work.Kind==Kind.Use) _browser.UsePreferredPlace(expected!.PlaceId);else _browser.ResolveInput();
        }
        finally {_mutatingBrowser=false;}
        if(!Current(work) || !SameDestination(_browser.Destination,expected))return;
        // Pin the actual browser publication after matching the explicitly parsed values. A
        // later equal-valued destination is still a new external selection and invalidates us.
        var chosen=_browser.Destination;
        if(expected is null) {SetStatus(_browser.Status);return;}
        if(work.Kind==Kind.Use)
        {
            // The page survives a use: rebind it to the now-cleared input and mark the chosen card.
            _pageInput=_browser.LinkInput;
            foreach(var row in Results) row.IsCurrent=ReferenceEquals(row,work.Selected);
            NotifyAvailability();
        }
        SetStatus("Requested destination selected. Loading its public details; no account is launched or verified.");
        if(!Current(work))return;
        // Borrowed details may still own a cancelled request. Join it before reuse; cancellation
        // of this search never broadly cancels a manually started details operation.
        await _details.ActiveOperation.WaitAsync(work.Stop.Token);
        if(!Current(work) || !ReferenceEquals(_browser.Destination,chosen))return;
        await _details.LoadAsync(work.Stop.Token);
        if(Current(work) && ReferenceEquals(_browser.Destination,chosen)) SetStatus("Destination request selected. Public details remain optional; access and arrival are unverified.");
    }
    private bool PageCurrent()=>_page is { } page && Fresh(page.RetrievedUtc) && string.Equals(_pageInput,_browser.LinkInput,StringComparison.Ordinal);
    private bool Fresh(DateTimeOffset at)=>at!=default && at<=_clock.GetUtcNow().AddSeconds(1) && _clock.GetUtcNow()-at<=TimeSpan.FromMinutes(5);
    private void SourceChanged(object? sender,PropertyChangedEventArgs args)
    {
        if(_disposed)return;
        if(args.PropertyName==nameof(ServerBrowserViewModel.LinkInput))
        {
            if(_mutatingBrowser && _browser.LinkInput==_expectedInput)return;
            Invalidate("Input changed. Search / Use explicitly; the current requested destination is unchanged.",false);
        }
        else if(args.PropertyName==nameof(ServerBrowserViewModel.Destination))
        {
            if(_mutatingBrowser && SameDestination(_browser.Destination,_expectedDestination))return;
            Invalidate("The requested destination changed. Old search results were cleared.",false);
        }
    }
    private static bool SameDestination(RobloxRequestedDestination? left,RobloxRequestedDestination? right)
        =>ReferenceEquals(left,right) || left is not null && right is not null && left.PlaceId==right.PlaceId
            && string.Equals(left.JobId,right.JobId,StringComparison.Ordinal) && left.ServerKind==right.ServerKind;
    public void CancelWork(bool clearInput=true)=>Invalidate("Destination search stopped. The current requested destination was preserved.",clearInput);
    private void Invalidate(string status,bool clearInput)
    {
        Interlocked.Increment(ref _generation);try {_work?.Stop.Cancel();}catch(ObjectDisposedException) { }
        _page=null;_pageInput=null;_selected=null;_status=status;_visitedTokens.Clear();_pagesVisited=0;_pagingStop=null;ClearRows();
        if(clearInput && _browser.LinkInput.Length!=0)
        {
            bool mutating=_mutatingBrowser;string prior=_expectedInput;_mutatingBrowser=true;_expectedInput="";
            try {_browser.LinkInput="";}finally {_mutatingBrowser=mutating;_expectedInput=prior;}
        }
        Raise(nameof(SelectedResult));Raise(nameof(HasResults));Raise(nameof(PageSummary));Raise(nameof(Status));NotifyAvailability();
    }
    private void ClearPage()
    {_page=null;_pageInput=null;_selected=null;ClearRows();Raise(nameof(SelectedResult));Raise(nameof(HasResults));Raise(nameof(PageSummary));NotifyAvailability();}
    private void ClearRows()
    {
        _art.Cancel();
        foreach(var row in Results) {row.Art=null;row.IsCurrent=false;}
        Results.Clear();VisibleResults.Clear();_visibleCount=PageSize;Raise(nameof(CanShowMore));
    }
    private void RebuildVisible()
    {
        IEnumerable<PublicGameSearchRow> ordered=_sort switch
        {
            SortPlaying=>Results.OrderByDescending(row=>row.Result.Playing??-1), // Stable: ties keep server order; unknown last.
            SortRating=>Results.OrderByDescending(row=>row.RatingValue),
            _=>Results
        };
        VisibleResults.Clear();
        foreach(var row in ordered.Take(_visibleCount)) VisibleResults.Add(row);
        Raise(nameof(CanShowMore));
    }
    // The loader owns and tracks the task it starts (see ArtOperation); a late result for a replaced page is fenced, never shown.
    private void LoadVisibleArt()
    {
        var pending=VisibleResults.Where(row=>row.Art is null && !row.ArtAttempted).ToArray();
        if(pending.Length!=0) _=_art.LoadAsync(pending,Interlocked.Read(ref _generation));
    }
    private void SetStatus(string status){_status=status;Raise(nameof(Status));}
    private void NotifyAvailability(){Raise(nameof(CanSearch));Raise(nameof(CanNext));Raise(nameof(CanUseSelected));Raise(nameof(CanCancel));}
    private void ObserveRetry(TimeSpan? retry)
    {if(retry is not { } delay || delay<=TimeSpan.Zero)return;var now=_clock.GetUtcNow();var deadline=delay>DateTimeOffset.MaxValue-now?DateTimeOffset.MaxValue:now+delay;if(deadline>_notBefore)_notBefore=deadline;}
    private void ReportFailure(Exception _){if(!_disposed)SetStatus("The destination operation could not finish. Retry explicitly when controls are available.");ReportSafeFailure();}
    private void ReportSafeFailure(){try{_failed(new InvalidOperationException("The public destination operation failed."));}catch(Exception){/* Diagnostics cannot become a second operation owner. */}}
    public ValueTask DisposeAsync()
    {
        if(_dispose is not null)return new(_dispose);
        _disposed=true;_browser.PropertyChanged-=SourceChanged;
        var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);_dispose=StopAsync(_active,gate.Task);
        try{_lifetime.Cancel();CancelWork();}finally{gate.TrySetResult();}
        return new(_dispose);
    }
    private async Task StopAsync(Task active,Task gate)
    {await gate;try{await active;}catch(OperationCanceledException){}try{await _art.DisposeAsync();}finally{_lifetime.Dispose();}}
    private enum Kind {Search,Direct,Use}
    private sealed class Work(Kind kind,long generation,string input,PublicRobloxGameSearchQuery? query,PublicGameSearchRow? selected,CancellationTokenSource stop)
    {public Kind Kind {get;}=kind;public long Generation {get;}=generation;public string Input=input;public PublicRobloxGameSearchQuery? Query {get;}=query;public PublicGameSearchRow? Selected {get;}=selected;public CancellationTokenSource Stop {get;}=stop;}
}
