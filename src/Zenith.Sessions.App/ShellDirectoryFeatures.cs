using System.Net.Http;
using System.Windows.Media;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed partial class AccountRow
{
    private PublicAccountData? _publicData;
    private LaunchState? _launchState;
    private LaunchProblem _launchProblem;
    public ImageSource? Avatar=>_publicData?.Avatar;
    public bool HasAvatar=>Avatar is not null;
    public string PublicDisplayName=>_publicData?.DisplayName??"";
    public string PublicProfileEvidence=>_publicData?.EvidenceLabel??"Avatar not loaded. Public information does not verify sign-in.";
    public string PowerLabel=>_launchState switch
    {
        LaunchState.Queued=>"Cancel",LaunchState.Validating or LaunchState.Launching or LaunchState.Stabilizing=>"Cancel",
        LaunchState.Uncertain or LaunchState.ReviewRequired or LaunchState.Arrived or LaunchState.ClientStartedUnverified=>"Review",
        LaunchState.Blocked when _launchProblem==LaunchProblem.InsufficientCapacity=>"Cancel",
        LaunchState.Blocked=>"Details",_=>SignInStatusBusy?"Refresh":NeedsSignIn?"Sign in":"Launch"
    };
    public string LaunchStatus=>_launchState switch
    {
        LaunchState.Arrived=>"Arrival verified",LaunchState.ReviewRequired=>"Review required",
        LaunchState.ClientStartedUnverified=>"Client started · account and arrival unverified",
        null=>"No verified client",_=>_launchState.ToString()!
    };
    public string PowerExplanation=>_launchState is LaunchState.Queued or LaunchState.Validating or LaunchState.Launching or LaunchState.Stabilizing?
        "Cancel this account's pending launch. A client may need review if startup already began.":_launchState is { }?LaunchDescriptions.Explain(_launchProblem):SignInStatusBusy?
        "Protected storage was busy, so this account's saved sign-in was not read. Refresh checks it again without opening Roblox sign-in.":KnownUserId is null?
        "Connect this saved profile through Roblox's sign-in page. Games remain native.":NeedsSignIn?
        "This saved sign-in needs renewing before it can launch. Sign in opens Roblox's sign-in page; games remain native.":"Launch this account's preferred game. For a chosen server link, use Queue checked accounts in Sessions → Join a game. Unsupported providers report a blocked result.";
    public void SetPublicPresentation(PublicAccountData value)
    {
        if(value.UserId!=KnownUserId) return;
        _publicData=value;Raise(nameof(Avatar));Raise(nameof(HasAvatar));Raise(nameof(PublicDisplayName));Raise(nameof(PublicProfileEvidence));
    }
    public void ClearPublicPresentation()
    {_publicData=null;Raise(nameof(Avatar));Raise(nameof(HasAvatar));Raise(nameof(PublicDisplayName));Raise(nameof(PublicProfileEvidence));}
    public void SetLaunchStatus(LaunchOperation? operation)
    {
        _launchState=operation?.State;_launchProblem=operation?.Problem??LaunchProblem.None;
        Raise(nameof(PowerLabel));Raise(nameof(LaunchStatus));Raise(nameof(PowerExplanation));
    }
}

public sealed partial class ShellViewModel
{
    private HttpClient? _serverHttp,_publicHttp;
    private PublicAccountPresentation _publicPresentation=null!;
    private PublicAccountPageLoader _publicImageLoader=null!;
    private readonly bool _enableAutomaticPublicImages;
    private bool _publicImagesAllowed,_publicImagesSuspended,_publicImageContextVisible;
    public Task PublicImageOperation=>_publicImageLoader.ActiveOperation;
    private CancellationTokenSource? _publicRefreshStop;
    private long _directoryGeneration;
    private bool _favoritesReloadRequested, _favoritesReloadRunning;
    private Task _favoritesReloadTask=Task.CompletedTask;
    public ServerBrowserViewModel ServerBrowser {get;private set;}=null!;
    public GameDetailsViewModel GameDetails {get;private set;}=null!;
    public DestinationSearchViewModel DestinationSearch {get;private set;}=null!;
    public RobloxFavoriteGamesViewModel RobloxFavorites {get;private set;}=null!;
    public RecentlyPlayedViewModel RecentlyPlayed {get;private set;}=null!;
    public ShareLinkViewModel ShareLink {get;private set;}=null!;
    private bool _destinationSearchContextAllowed;
    public AsyncCommand RefreshPublicAccount {get;private set;}=null!;
    public AsyncCommand QueueTargetedAccounts {get;private set;}=null!;
    public AsyncCommand AccountPower {get;private set;}=null!;
    public Command OpenJoinGameForSelectedAccount {get;private set;}=null!;
    public Command UseSelectedPreferredPlace {get;private set;}=null!;
    private bool CanUseDirectories()=>_ready && !IsLocked && !_shuttingDown && !_lifetime.IsCancellationRequested;
    private void InitializeDirectoryFeatures(IPublicRobloxDirectory? publicDirectory,IRobloxPublicServerDirectory? serverDirectory,IPublicRobloxGameDirectory? gameDirectory)
    {
        if(serverDirectory is null) {_serverHttp=RobloxServerDirectory.CreatePublicHttpClient();serverDirectory=new RobloxServerDirectory(_serverHttp);}
        if(publicDirectory is null) {_publicHttp=RobloxPublicDirectory.CreateAnonymousHttpClient();publicDirectory=new RobloxPublicDirectory(_publicHttp,maximumEncodedMemoryBytes:32L*1024*1024);}
        ServerBrowser=new(serverDirectory,CanUseDirectories);_publicPresentation=new(publicDirectory);
        GameDetails=new(gameDirectory??publicDirectory as IPublicRobloxGameDirectory,ServerBrowser,CanUseDirectories,
            error=>_diagnostics.Record(ManagerEventKind.OperationFailed,error.GetType()));
        // A universe-only share link opens the universe's root place, read anonymously after the signed-in resolve.
        var shareLinkResolver=new RootPlaceShareLinkResolver(_linkService,publicDirectory as IPublicRobloxGameBatchDirectory ?? gameDirectory as IPublicRobloxGameBatchDirectory);
        ShareLink=new(shareLinkResolver,_privateServerStore,ServerBrowser,()=>SelectedAccount?.Profile.Id,CanUseDirectories,
            error=>_diagnostics.Record(ManagerEventKind.OperationFailed,error.GetType()),games:gameDirectory??publicDirectory as IPublicRobloxGameDirectory,
            sessionRejected:(_,ct)=>ReloadAccountsAfterRejectedSessionAsync(ct));
        DestinationSearch=new(publicDirectory as IPublicRobloxGameSearchDirectory ?? gameDirectory as IPublicRobloxGameSearchDirectory,
            ServerBrowser,GameDetails,CanUseDestinationSearch,error=>_diagnostics.Record(ManagerEventKind.OperationFailed,error.GetType()),
            art:publicDirectory as IPublicRobloxGameArtDirectory ?? gameDirectory as IPublicRobloxGameArtDirectory,shareLinkHandler:RouteShareLinkAsync);
        RobloxFavorites=new(publicDirectory as IPublicRobloxFavoriteGamesDirectory ?? gameDirectory as IPublicRobloxFavoriteGamesDirectory,
            ServerBrowser,CanUseDirectories,error=>_diagnostics.Record(ManagerEventKind.OperationFailed,error.GetType()));
        RecentlyPlayed=new(publicDirectory as IPublicRobloxGameBatchDirectory ?? gameDirectory as IPublicRobloxGameBatchDirectory,
            gameDirectory??publicDirectory as IPublicRobloxGameDirectory,ServerBrowser,CanUseDirectories,error=>_diagnostics.Record(ManagerEventKind.OperationFailed,error.GetType()));
        RecentClients.Rows.CollectionChanged+=(_,_)=>{if(!_shuttingDown && IsJoinGame) RefreshRecentlyPlayed();};
        _publicImageLoader=new(_publicPresentation,CanUseDirectories,PublishPublicImageAsync,
            error=>_diagnostics.Record(ManagerEventKind.OperationFailed,error.GetType()));
        RefreshPublicAccount=new(async parameter=>
        {
            if(!CanUseDirectories()) return;
            var row=parameter as AccountRow??SelectedAccount;
            if(row is null || row.KnownUserId is not { } userId || userId<=0 || row.AuthorizationGeneration is not { } grant || grant<=0 || !Accounts.Contains(row)) {Notice="Sign in to establish this account's player ID before loading its public avatar.";return;}
            var epoch=_directoryGeneration;int queryGeneration=_queryGeneration;
            var binding=new PublicAccountPageBinding(row.Profile.Id,userId,grant);
            using var stop=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);_publicRefreshStop=stop;
            Notice="Loading this account's public profile and avatar…";
            try
            {
                var result=await _publicImageLoader.RefreshAsync(queryGeneration,binding,stop.Token);
                if(stop.IsCancellationRequested || epoch!=_directoryGeneration || queryGeneration!=_queryGeneration || !CanUseDirectories()
                    || !Accounts.Contains(row) || row.KnownUserId!=binding.UserId || row.AuthorizationGeneration!=binding.AuthorizationGeneration) return;
                Notice=result.Message;
            }
            catch(OperationCanceledException)
            {if(epoch==_directoryGeneration && CanUseDirectories() && Notice=="Loading this account's public profile and avatar…") Notice="The public image request stopped. Refresh explicitly when ready.";}
            finally {if(ReferenceEquals(_publicRefreshStop,stop)) _publicRefreshStop=null;}
        },Fail);
        OpenJoinGameForSelectedAccount=new(_=>
        {
            if(!CanUseDirectories() || SelectedAccount is not { } selected || !Accounts.Contains(selected)) return;
            Navigate.Execute("JoinGame");
        });
        UseSelectedPreferredPlace=new(_=>
        {
            if(!CanUseDirectories() || SelectedAccount?.Profile.PreferredPlaceId is not { } place) {Notice="Select an account with a preferred game first.";return;}
            ServerBrowser.UsePreferredPlace(place);Tab="JoinGame";
        });
        QueueTargetedAccounts=new(parameter=>QueueTargetedAccountsAsync((TargetedRequest?)parameter),Fail,_=>CaptureTargetedRequest());
        AccountPower=new(async parameter=>
        {
            if(!CanUseDirectories() || parameter is not AccountRow row || !Accounts.Contains(row)) return;
            SelectedAccount=row;
            var launch=Launches.LastOrDefault(l=>l.Operation.Intent.ProfileId==row.Profile.Id && !LaunchDescriptions.Terminal(l.Operation.State));
            if(launch is not null)
            {
                QueueExpanded=true;
                if(launch.Operation.State is LaunchState.Queued or LaunchState.Validating or LaunchState.Launching or LaunchState.Stabilizing
                    || launch.Operation.State==LaunchState.Blocked && launch.Operation.Problem==LaunchProblem.InsufficientCapacity)
                    await _launchQueue.CancelAsync(launch.Id,_lifetime.Token);
                else Notice=launch.Detail.Length>0?launch.Detail:"Review this account's existing client before another launch.";
                return;
            }
            if(row.SignInStatusBusy)
            {
                // Busy storage is not a missing sign-in: reload the page instead of opening Roblox sign-in.
                long epoch=_directoryGeneration;
                await LoadAccountsAsync(_lifetime.Token);
                if(epoch!=_directoryGeneration || !CanUseDirectories()) return;
                Notice=Accounts.FirstOrDefault(a=>a.Profile.Id==row.Profile.Id) is {SignInStatusBusy:true}
                    ?"Protected sign-in storage is still busy. Zenith checks it again shortly; protected files were not changed."
                    :"Saved sign-in status reloaded.";
                return;
            }
            if(row.NeedsSignIn || row.KnownUserId is not { } userId) {await AuthorizeSelectedAsync(false);return;}
            if(!QueueReady) {Notice="The launch queue is unavailable. Resolve its storage error before requesting a client.";return;}
            var binding=new LaunchAccountBinding(userId,row.AuthorizationGeneration??0);
            var operation=await _launchQueue.EnqueueAsync(row.Profile,_lifetime.Token,binding);
            Notice=operation.State==LaunchState.Blocked?LaunchDescriptions.Explain(operation.Problem):"Native launch queued. It is not a confirmed running client.";QueueExpanded=true;
        },Fail);
        RefreshPublicImageActivity();
    }
    private Task PublishPublicImageAsync(PublicAccountPageUpdate update,CancellationToken ct)=>_dispatcher.InvokeAsync(()=>
    {
        if(ct.IsCancellationRequested || update.PageGeneration!=_queryGeneration || !CanUseDirectories()
            || update.Result.Value is not { } data || data.UserId!=update.UserId || !_publicPresentation.IsCurrent(data)) return;
        foreach(var binding in update.Bindings)
        {
            if(ct.IsCancellationRequested || update.PageGeneration!=_queryGeneration || !CanUseDirectories() || !_publicPresentation.IsCurrent(data)) return;
            var row=Accounts.FirstOrDefault(row=>row.Profile.Id==binding.ProfileId && row.KnownUserId==binding.UserId
                && row.AuthorizationGeneration==binding.AuthorizationGeneration);
            row?.SetPublicPresentation(data);
        }
    },System.Windows.Threading.DispatcherPriority.Background,ct).Task;
    private bool CanAutomaticallyLoadPublicImages()=>_enableAutomaticPublicImages && !IsSample && !_publicImagesSuspended && IsAccounts
        && _clientPreviewWindowVisible && CanUseDirectories();
    private bool CanUseDestinationSearch()=>CanUseDirectories() && IsJoinGame && _clientPreviewWindowVisible;
    /// <summary>Join a game opened or the selected account changed: bind Roblox favorites to that account, load once per visit and rebuild recently played; leaving cancels both.</summary>
    private void RefreshJoinGameSources()
    {
        if(RobloxFavorites is null) return;
        var row=SelectedAccount;
        RobloxFavorites.SetAccount(row?.Profile.Id,row?.KnownUserId,row?.AuthorizationGeneration,row?.Label);
        if(IsJoinGame && CanUseDirectories()) _=RobloxFavorites.LoadAsync(); // Owned and tracked by the view model (ActiveOperation); joined on dispose.
        else RobloxFavorites.Cancel();
        if(IsJoinGame && CanUseDirectories()) _=ShareLink.LoadSavedAsync(); else ShareLink.Cancel();
        RefreshRecentlyPlayed();
    }
    /// <summary>
    /// Roblox refused the selected account's saved session during a share-link check, and the service marked it as
    /// needing sign-in. Reload the page so its row and launch button say so now, not after a manual refresh. Owned by the
    /// share-link check (awaited there and joined on its disposal) and by this shell's lifetime; failures become a notice.
    /// </summary>
    private async Task ReloadAccountsAfterRejectedSessionAsync(CancellationToken ct)
    {
        if(_lifetime.IsCancellationRequested || ct.IsCancellationRequested) return;
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token,ct);
        try {await LoadAccountsAsync(stop.Token);}
        catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
        catch(Exception error) {Fail(error);}
    }
    /// <summary>A share link pasted in the Find box: open Browse servers → Private server and check it there. The code never enters search.</summary>
    private Task RouteShareLinkAsync(RobloxShareLink link)
    {
        ShowJoinStep.Execute("2");ShowServerTab.Execute("Private");
        return ShareLink.CheckAsync(link);
    }
    /// <summary>A new client or history observation while Join a game is open: rebuild the recently played chips from what the logs reported.</summary>
    private void RefreshRecentlyPlayed()
    {
        if(RecentlyPlayed is null) return;
        if(IsJoinGame && CanUseDirectories()) _=RecentlyPlayed.UpdateAsync(BuildRecentlyPlayedSources()); // Owned and tracked by the view model; joined on dispose.
        else RecentlyPlayed.Cancel();
    }
    private List<RecentlyPlayedSource> BuildRecentlyPlayedSources()
    {
        var sources=new List<RecentlyPlayedSource>();
        foreach(var row in Sessions)
            if(row.Log.ReportedPlaceId is > 0 && row.Log.ReportedObservedUtc is { } at) sources.Add(new(row.Log.ReportedPlaceId.Value,row.Log.ReportedUniverseId,at));
        foreach(var row in RecentClients.Rows)
            if(row.Entry.LastKnownDestination is { } last) sources.Add(new(last.PlaceId,last.UniverseId,last.ObservedUtc));
        return sources;
    }
    private void RefreshPublicImageActivity()
    {
        GameDetails?.Gallery.SetVisible(_clientPreviewWindowVisible && IsJoinGame && !IsLocked && !_shuttingDown);
        bool searchAllowed=CanUseDestinationSearch();
        if(_destinationSearchContextAllowed && !searchAllowed) DestinationSearch?.CancelWork(clearInput:false);
        _destinationSearchContextAllowed=searchAllowed;DestinationSearch?.RefreshAvailability();
        if(_publicImageLoader is null) return;
        bool visible=IsAccounts && _clientPreviewWindowVisible && !IsLocked && !_shuttingDown;
        if(visible && !_publicImageContextVisible) _publicImagesSuspended=false;
        _publicImageContextVisible=visible;
        bool allowed=CanAutomaticallyLoadPublicImages();
        if(allowed==_publicImagesAllowed) return;
        _publicImagesAllowed=allowed;
        if(allowed) QueueCurrentPublicImages(); else _publicImageLoader.CancelWork();
    }
    private void QueueCurrentPublicImages()
    {
        if(!CanAutomaticallyLoadPublicImages()) return;
        var bindings=Accounts.Where(row=>row.KnownUserId is >0 && row.AuthorizationGeneration is >0)
            .Take(PublicAccountPageLoader.MaximumBindings)
            .Select(row=>new PublicAccountPageBinding(row.Profile.Id,row.KnownUserId!.Value,row.AuthorizationGeneration!.Value)).ToArray();
        _publicImageLoader.ReplacePage(_queryGeneration,bindings);
    }
    private sealed record TargetedRequest(RobloxRequestedDestination Destination,
        IReadOnlyList<(AccountProfile Profile,LaunchAccountBinding? Binding)> Recipients,long Epoch);
    private TargetedRequest? CaptureTargetedRequest()
    {
        if(!CanUseDirectories() || !QueueReady || ServerBrowser.ReadyDestination is not { } target)return null;
        // Pin the command before it notifies bindings or awaits queue work.
        var checkedRows=Accounts.Where(r=>r.IsChecked).ToArray();
        var recipients=checkedRows.Length>0?checkedRows:SelectedAccount is { } selected && Accounts.Contains(selected)?[selected]:[];
        var snapshot=recipients.Select(r=>(r.Profile,Binding:r.KnownUserId is >0 && r.AuthorizationGeneration is >0?new LaunchAccountBinding(r.KnownUserId.Value,r.AuthorizationGeneration.Value):null)).ToArray();
        return new(target,snapshot,_directoryGeneration);
    }
    private async Task QueueTargetedAccountsAsync(TargetedRequest? request)
    {
        if(!CanUseDirectories())return;
        if(request is null)
        {
            if(!_preserveStopNotice)Notice=ServerBrowser.HasDestinationDraft?"Use or discard the pending destination before queuing accounts.":"Choose a validated destination and available launch queue first.";
            return;
        }
        if(request.Epoch!=_directoryGeneration || !QueueReady)return;
        if(request.Recipients.Count==0) {Notice="Select an account or check accounts on this page.";return;}
        string summary=await QueueFrozenRecipientsAsync(request.Destination,request.Recipients,request.Epoch,_lifetime.Token);
        if(request.Epoch==_directoryGeneration && CanUseDirectories())Notice=summary;
    }
    private async Task<string> QueueFrozenRecipientsAsync(RobloxRequestedDestination target,
        IReadOnlyList<(AccountProfile Profile,LaunchAccountBinding? Binding)> snapshot,long epoch,CancellationToken ct)
    {
        // Freeze advisory occupancy with the same recipient/target snapshot. It never reserves
        // seats, authenticates accounts or changes the existing native-provider admission guards.
        var capacityAdvice=ServerBrowser.AssessRequestedCapacity(target,snapshot.Count);
        int queued=0,blocked=0,skipped=0;
        if(!ct.IsCancellationRequested && epoch==_directoryGeneration && CanUseDirectories())
        {
            // One review and one durable queue write for the whole frozen snapshot. The review is
            // fenced by the queue's stop generation (Panic) and the caller's token; the queue owns
            // the write to completion and cancels the accepted subset itself if a stop races it.
            LaunchBatchReview review;
            try{review=_launchQueue.CaptureBatchReview(target,snapshot);}
            catch(ArgumentException error)
            {
                _diagnostics.Record(ManagerEventKind.OperationFailed,error.GetType());
                return $"Place {target.PlaceId} destination requests: one or more selected accounts could not be reviewed. Review the accounts and try again. No request was made.";
            }
            IReadOnlyList<LaunchBatchRow> rows;
            // A stopped or unhealthy queue rejects the whole review before anything is written.
            try{rows=await _launchQueue.EnqueueBatchAsync(review,ct);}
            catch(LaunchQueueException){rows=[];}
            // The queue does not know the page's directory epoch. Leaving the page or locking
            // after the commit withdraws the rows this command created; rows the queue already
            // cancelled for a stop are left alone.
            bool withdrawn=ct.IsCancellationRequested||epoch!=_directoryGeneration;
            foreach(var row in rows)
            {
                if(row is not {Disposition:LaunchBatchDisposition.Recorded,Operation:{ } operation}) continue;
                if(withdrawn)
                {
                    if(!LaunchDescriptions.Terminal(operation.State))
                        try{await _launchQueue.CancelAsync(operation.Intent.OperationId,CancellationToken.None);}
                        catch(Exception error){_diagnostics.Record(ManagerEventKind.OperationFailed,error.GetType());}
                }
                else if(operation.State==LaunchState.Blocked) blocked++;
                else if(!LaunchDescriptions.Terminal(operation.State)) queued++;
            }
            // Already pending, queue full, unavailable forecast, cancelled by a stop or withdrawn.
            skipped=snapshot.Count-queued-blocked;
        }
        QueueExpanded=true;
        return $"Place {target.PlaceId} destination requests: {queued} queued, {blocked} blocked, {skipped} already pending or unavailable. No arrival is assumed. {capacityAdvice.Explanation}";
    }
    private void RefreshAccountLaunchStates()
    {
        var visible=Accounts.ToDictionary(a=>a.Profile.Id);
        foreach(var launch in Launches)
            if(visible.TryGetValue(launch.Operation.Intent.ProfileId,out var profile)) launch.Label=profile.Label;
        foreach(var account in Accounts)
            account.SetLaunchStatus(Launches.LastOrDefault(l=>l.Operation.Intent.ProfileId==account.Profile.Id && !LaunchDescriptions.Terminal(l.Operation.State))?.Operation);
    }
    private void CancelDirectoryFeatures(bool clear=false)
    {
        _favoritesReloadRequested=false;
        _publicImagesAllowed=false;_publicImagesSuspended=true;_publicImageLoader.CancelWork();
        Interlocked.Increment(ref _directoryGeneration);_publicRefreshStop?.Cancel();_groupMembershipStop?.Cancel();ServerBrowser.CancelWork(clear);
        DestinationFavorites.CancelWork(clear);
        RecipientBundles.CancelWork(clear);
        GameDetails.CancelWork(clear:true,clearCache:true);
        _publicPresentation.Clear();foreach(var row in Accounts) row.ClearPublicPresentation();
        DestinationSearch.CancelWork(clearInput:true);
        RobloxFavorites.Cancel();
        RecentlyPlayed.Cancel();
        ShareLink.Cancel();
        _shareLinkClient.ForgetAllTokens(); // Lock and Panic: the next share-link resolve starts with a fresh handshake.
    }
    private async Task StopDirectoryFeaturesAsync()
    {
        DisposeDestinationCapacity();
        CancelDirectoryFeatures(true);
        await Task.WhenAll(RefreshPublicAccount.Execution,QueueTargetedAccounts.Execution,AccountPower.Execution);
        await _publicImageLoader.DisposeAsync();
        await DestinationSearch.DisposeAsync();
        await RobloxFavorites.DisposeAsync();
        await RecentlyPlayed.DisposeAsync();
        await ShareLink.DisposeAsync();
        (_shareLinkClient as IDisposable)?.Dispose();(_privateServerStore as IDisposable)?.Dispose();
        await GameDetails.DisposeAsync();await ServerBrowser.DisposeAsync();await _publicPresentation.DisposeAsync();_serverHttp?.Dispose();_publicHttp?.Dispose();
    }
    private void ReloadFavoritesAfterUnlock()
    {
        _favoritesReloadRequested=true;
        if(_favoritesReloadRunning) return;
        _favoritesReloadRunning=true;_favoritesReloadTask=ReloadFavoritesWhenIdleAsync();
    }
    private async Task ReloadFavoritesWhenIdleAsync()
    {
        try
        {
            while(_favoritesReloadRequested && !_shuttingDown && !_lifetime.IsCancellationRequested)
            {
                _favoritesReloadRequested=false;long generation=_directoryGeneration;
                await DestinationFavorites.ActiveOperation;
                if(generation!=_directoryGeneration || !CanUseDirectories()) continue;
                await DestinationFavorites.LoadAsync(_lifetime.Token);
                await RecipientBundles.ActiveOperation;
                if(generation!=_directoryGeneration||!CanUseDirectories())continue;
                await RecipientBundles.LoadAsync(_lifetime.Token);
            }
        }
        catch(OperationCanceledException) when(_lifetime.IsCancellationRequested) { }
        catch(Exception ex) { if(!_shuttingDown && !_preserveStopNotice) Fail(ex); else _diagnostics.Record(ManagerEventKind.OperationFailed,ex.GetType()); }
        finally {_favoritesReloadRunning=false;}
    }
}
