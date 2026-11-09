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
    private string _launchDetail="";
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
        LaunchState.Blocked when _launchProblem==LaunchProblem.Interrupted=>LaunchRow.NotStartedInterrupted,
        LaunchState.ClientStartedUnverified=>"Client started · account and arrival unverified",
        null=>"No verified client",_=>_launchState.ToString()!
    };
    public string PowerExplanation=>_launchState is LaunchState.Queued or LaunchState.Validating or LaunchState.Launching or LaunchState.Stabilizing?
        "Cancel this account's pending launch. A client may need review if startup already began.":_launchState is { }?_launchDetail:SignInStatusBusy?
        "Protected storage was busy, so this account's saved sign-in was not read. Refresh checks it again without opening Roblox sign-in.":KnownUserId is null?
        "Connect this saved profile through Roblox's sign-in page. Games remain native.":NeedsSignIn?
        "This saved sign-in needs renewing before it can launch. Sign in opens Roblox's sign-in page; games remain native.":"Launch this account's preferred game, or choose a game and complete this installation's launch test first. For a chosen server link, use Sessions → Join a game.";
    public void SetPublicPresentation(PublicAccountData value)
    {
        if(value.UserId!=KnownUserId) return;
        _publicData=value;Raise(nameof(Avatar));Raise(nameof(HasAvatar));Raise(nameof(PublicDisplayName));Raise(nameof(PublicProfileEvidence));
    }
    public void ClearPublicPresentation()
    {_publicData=null;Raise(nameof(Avatar));Raise(nameof(HasAvatar));Raise(nameof(PublicDisplayName));Raise(nameof(PublicProfileEvidence));}
    private (long PlaceId,string Name)? _preferredGame;
    /// <summary>Show the public name found for this row's preferred place. A name for any other place is ignored.</summary>
    public void SetPreferredGameName(long placeId,string name)
    {
        if(placeId!=Profile.PreferredPlaceId || string.IsNullOrWhiteSpace(name)) return;
        _preferredGame=(placeId,name);Raise(nameof(Destination));
    }
    public void ClearPreferredGameName() {if(_preferredGame is null) return;_preferredGame=null;Raise(nameof(Destination));}
    public void SetLaunchStatus(LaunchOperation? operation)
    {
        _launchState=operation?.State;_launchProblem=operation?.Problem??LaunchProblem.None;
        _launchDetail=operation is null?"":LaunchDescriptions.Explain(operation);
        Raise(nameof(PowerLabel));Raise(nameof(LaunchStatus));Raise(nameof(PowerExplanation));
    }
}

public sealed partial class ShellViewModel
{
    private HttpClient? _serverHttp,_publicHttp;
    // The public directory's release reader; installation features decide whether to use it (never beside injected discovery).
    private IPublicRobloxClientReleaseDirectory? _publicClientReleases;
    private PublicAccountPresentation _publicPresentation=null!;
    private PublicAccountPageLoader _publicImageLoader=null!;
    private readonly bool _enableAutomaticPublicImages;
    private bool _publicImagesAllowed,_publicImagesSuspended,_publicImageContextVisible;
    /// <summary>
    /// Set by Panic and Lock: Join a game's public lookups (Roblox favorites, recently played names) and the saved-server list
    /// stay stopped. Cleared only when Join a game is opened again or another profile is chosen there, never by a reload.
    /// </summary>
    private bool _joinGameSourcesSuspended;
    /// <summary>The last profile selected, on any page or while locked; a reload that selects it again is not a new choice.</summary>
    private Guid? _joinGameProfile;
    public Task PublicImageOperation=>_publicImageLoader.ActiveOperation;
    /// <summary>A page holds 100 rows; past this many places the remembered names are dropped and asked again when selected.</summary>
    private const int MaximumPreferredGameNames=100;
    private IPublicRobloxGameDirectory? _preferredGameDirectory;
    private readonly Dictionary<long,string> _preferredGameNames=new();
    private readonly List<Task> _preferredGameRuns=[];
    private CancellationTokenSource? _preferredGameStop;
    private long _preferredGamePlace;
    /// <summary>Every tracked preferred-game name lookup; completes once all started lookups have finished or been fenced out.</summary>
    public Task PreferredGameNameOperation {get {_preferredGameRuns.RemoveAll(run=>run.IsCompleted);return _preferredGameRuns.Count==0?Task.CompletedTask:Task.WhenAll(_preferredGameRuns);}}
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
    private bool _destinationSearchContextAllowed,_destinationSearchSuspended;
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
        _publicClientReleases=publicDirectory as IPublicRobloxClientReleaseDirectory;
        ServerBrowser=new(serverDirectory,CanUseDirectories);_publicPresentation=new(publicDirectory);
        _preferredGameDirectory=gameDirectory??publicDirectory as IPublicRobloxGameDirectory;
        GameDetails=new(gameDirectory??publicDirectory as IPublicRobloxGameDirectory,ServerBrowser,CanUseDirectories,
            error=>_diagnostics.RecordFailure(ManagerSubsystem.Directory,error.GetType()));
        // A universe-only share link opens the universe's root place, read anonymously after the signed-in resolve.
        var shareLinkResolver=new RootPlaceShareLinkResolver(_linkService,publicDirectory as IPublicRobloxGameBatchDirectory ?? gameDirectory as IPublicRobloxGameBatchDirectory);
        ShareLink=new(shareLinkResolver,_privateServerStore,ServerBrowser,()=>SelectedAccount?.Profile.Id,CanUseDirectories,
            error=>_diagnostics.RecordFailure(ManagerSubsystem.Directory,error.GetType()),games:gameDirectory??publicDirectory as IPublicRobloxGameDirectory,
            sessionRejected:(_,ct)=>ReloadAccountsAfterRejectedSessionAsync(ct),removingSaved:StopPrivateTrialForRemoval);
        DestinationSearch=new(publicDirectory as IPublicRobloxGameSearchDirectory ?? gameDirectory as IPublicRobloxGameSearchDirectory,
            ServerBrowser,GameDetails,CanUseDestinationSearch,error=>_diagnostics.RecordFailure(ManagerSubsystem.Directory,error.GetType()),
            art:publicDirectory as IPublicRobloxGameArtDirectory ?? gameDirectory as IPublicRobloxGameArtDirectory,shareLinkHandler:RouteShareLinkAsync);
        RobloxFavorites=new(publicDirectory as IPublicRobloxFavoriteGamesDirectory ?? gameDirectory as IPublicRobloxFavoriteGamesDirectory,
            ServerBrowser,CanUseDirectories,error=>_diagnostics.RecordFailure(ManagerSubsystem.Directory,error.GetType()));
        RecentlyPlayed=new(publicDirectory as IPublicRobloxGameBatchDirectory ?? gameDirectory as IPublicRobloxGameBatchDirectory,
            gameDirectory??publicDirectory as IPublicRobloxGameDirectory,ServerBrowser,CanUseDirectories,error=>_diagnostics.RecordFailure(ManagerSubsystem.Directory,error.GetType()));
        _publicImageLoader=new(_publicPresentation,CanUseDirectories,PublishPublicImageAsync,
            error=>_diagnostics.RecordFailure(ManagerSubsystem.Directory,error.GetType()));
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
        },error=>Fail(error,ManagerSubsystem.Directory));
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
        QueueTargetedAccounts=new(parameter=>QueueTargetedAccountsAsync((TargetedRequest?)parameter),error=>Fail(error,ManagerSubsystem.Queue),_=>CaptureTargetedRequest());
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
            if(!IsNativeLaunchAvailable || row.Profile.PreferredPlaceId is not >0)
            {
                // A single-account action must not inherit a previous batch's checked recipients.
                // Take the user to the existing reviewed launch path, without persisting a doomed request.
                foreach(var account in Accounts)account.IsChecked=ReferenceEquals(account,row);
                if(row.Profile.PreferredPlaceId is >0)ServerBrowser.UsePreferredPlace(row.Profile.PreferredPlaceId.Value);
                Navigate.Execute("JoinGame");ShowJoinStep.Execute(ServerBrowser.CanUseDestination?"3":"1");
                Notice=ServerBrowser.CanUseDestination?"Review this account and destination before starting. Select an installed Roblox player in Settings if launching is unavailable."
                    :"Choose a game for this account, then review the launch. No client has started.";
                return;
            }
            var binding=new LaunchAccountBinding(userId,row.AuthorizationGeneration??0);
            var operation=await _launchQueue.EnqueueAsync(row.Profile,_lifetime.Token,binding);
            Notice=operation.State==LaunchState.Blocked?LaunchDescriptions.Explain(operation.Problem)
                :EffectiveQueuePaused?"Native launch queued. Choose Resume queue to start Roblox."
                :"Native launch queued. It is not a confirmed running client.";QueueExpanded=true;
        },error=>Fail(error,ManagerSubsystem.Queue));
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
    /// <summary>
    /// Join a game opened (<paramref name="opened"/>, from the Tab setter) or the selected account changed: bind Roblox
    /// favorites to that account, load once per visit and rebuild recently played; a share-link check made with another
    /// account's session stops. Leaving cancels them all. Only the page opening or another profile chosen there is a user
    /// action that restarts them after Panic or Lock. An account reload (the global Refresh, the reload after a cancelled
    /// sign-in or handoff inspection, the one a native trial queues for the next presentation) selects a new row for the same
    /// profile, after the list's momentary empty selection while it is rebuilt: that restarts nothing.
    /// </summary>
    private void RefreshJoinGameSources(bool opened=false)
    {
        if(RobloxFavorites is null) return;
        var row=SelectedAccount;
        bool chosen=opened || row is not null && row.Profile.Id!=_joinGameProfile;
        if(row is not null) _joinGameProfile=row.Profile.Id; // Also while locked, so a reload after Unlock is not a new choice.
        if(!IsJoinGame || !CanUseDirectories())
        {
            RobloxFavorites.SetAccount(row?.Profile.Id,row?.KnownUserId,row?.AuthorizationGeneration,row?.Label);
            RobloxFavorites.Cancel();ShareLink.Cancel();RefreshRecentlyPlayed();return;
        }
        if(row is null && !opened) return; // The account list's empty selection while it reloads; the same profile follows.
        if(chosen) {_joinGameSourcesSuspended=false;RecentlyPlayed?.ForgetLocalRefusals();}
        // The same profile re-selected keeps its favorites page; a changed sign-in for it drops the old page.
        bool rebound=RobloxFavorites.SetAccount(row?.Profile.Id,row?.KnownUserId,row?.AuthorizationGeneration,row?.Label);
        ShareLink.SelectedAccountChanged();
        if(!_joinGameSourcesSuspended)
        {
            if(chosen || rebound) _=RobloxFavorites.LoadAsync(); // Owned and tracked by the view model (ActiveOperation); joined on dispose.
            if(chosen) _=ShareLink.LoadSavedAsync();
        }
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
        catch(Exception error) {Fail(error,ManagerSubsystem.Library);}
    }
    /// <summary>A share link pasted in the Find box: open Browse servers → Private server and check it there. The code never enters search.</summary>
    private Task RouteShareLinkAsync(RobloxShareLink link)
    {
        ShowJoinStep.Execute("2");ShowServerTab.Execute("Private");
        return ShareLink.CheckAsync(link);
    }
    /// <summary>
    /// Join a game opened, or one desktop presentation while it is open: rebuild the recently played chips from what the logs
    /// reported, once, after the history rows and the client log views are both current. History row changes do not call
    /// this one by one. After Panic or Lock the chips stay stopped; a desktop sample is not a user action.
    /// </summary>
    private void RefreshRecentlyPlayed()
    {
        if(RecentlyPlayed is null) return;
        if(IsJoinGame && CanUseDirectories() && !_joinGameSourcesSuspended) _=RecentlyPlayed.UpdateAsync(BuildRecentlyPlayedSources()); // Owned and tracked by the view model; joined on dispose.
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
        // Leaving Join a game, Lock and shutdown clear the search page. Hiding the window (minimize, close to the tray) while
        // the page stays open only suspends it: running work still stops, a finished page and its pictures stay.
        bool pageOpen=CanUseDirectories() && IsJoinGame,searchAllowed=CanUseDestinationSearch();
        bool owned=_destinationSearchContextAllowed || _destinationSearchSuspended;
        if(owned && !pageOpen) DestinationSearch?.CancelWork(clearInput:false);
        else if(_destinationSearchContextAllowed && !searchAllowed) DestinationSearch?.Suspend();
        else if(_destinationSearchSuspended && searchAllowed) DestinationSearch?.Resume();
        _destinationSearchSuspended=owned && pageOpen && !searchAllowed;
        _destinationSearchContextAllowed=searchAllowed;DestinationSearch?.RefreshAvailability();
        if(_publicImageLoader is null) return;
        bool visible=IsAccounts && _clientPreviewWindowVisible && !IsLocked && !_shuttingDown;
        if(visible && !_publicImageContextVisible) _publicImagesSuspended=false;
        _publicImageContextVisible=visible;
        bool allowed=CanAutomaticallyLoadPublicImages();
        if(allowed==_publicImagesAllowed) return;
        _publicImagesAllowed=allowed;
        if(allowed) {QueueCurrentPublicImages();RefreshPreferredGameName();} else {_publicImageLoader.CancelWork();StopPreferredGameLookup();}
    }
    /// <summary>
    /// Name the selected account's preferred game through the public game directory, as Join a game does. Only the selected
    /// row asks, one place at a time, under the automatic headshot rule: runtime composition, Accounts page and window
    /// visible, not after Panic or Lock. A found name labels every row on the page with that place, and rows published by a
    /// reload reuse it without asking (<see cref="ApplyKnownPreferredGameName"/>). Found names stay in memory until Lock or
    /// Panic; a failure keeps "Place {id}".
    /// </summary>
    private void RefreshPreferredGameName()
    {
        if(_preferredGameDirectory is null) return;
        var row=SelectedAccount;long place=row?.Profile.PreferredPlaceId??0;string? name=null;
        bool known=place>0 && _preferredGameNames.TryGetValue(place,out name);
        if(known) row!.SetPreferredGameName(place,name!);
        bool ask=place>0 && !known && CanAutomaticallyLoadPublicImages();
        if(ask && _preferredGameStop is not null && _preferredGamePlace==place) return; // The running lookup already covers this place.
        StopPreferredGameLookup();
        if(!ask) return;
        var stop=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _preferredGameStop=stop;_preferredGamePlace=place;
        _preferredGameRuns.RemoveAll(run=>run.IsCompleted);
        _preferredGameRuns.Add(LookupPreferredGameAsync(_preferredGameDirectory,place,_directoryGeneration,stop));
    }
    /// <summary>A row being published shows the name already found for its preferred place. Memory only; no request is made.</summary>
    private void ApplyKnownPreferredGameName(AccountRow row)
    {
        if(row.Profile.PreferredPlaceId is { } place && _preferredGameNames.TryGetValue(place,out var name)) row.SetPreferredGameName(place,name);
    }
    private void StopPreferredGameLookup()
    {
        var stop=_preferredGameStop;_preferredGameStop=null;_preferredGamePlace=0;
        stop?.Cancel(); // The lookup disposes its own token source when it ends.
    }
    private async Task LookupPreferredGameAsync(IPublicRobloxGameDirectory games,long place,long epoch,CancellationTokenSource stop)
    {
        try
        {
            var result=await games.LookupGameAsync(place,stop.Token);
            if(stop.IsCancellationRequested || epoch!=_directoryGeneration || !CanUseDirectories()
                || result.State!=PublicDirectoryState.Success || result.Value is not { } game || game.RequestedPlaceId!=place || string.IsNullOrWhiteSpace(game.Name)) return;
            if(_preferredGameNames.Count>=MaximumPreferredGameNames) _preferredGameNames.Clear();
            _preferredGameNames[place]=game.Name;
            foreach(var row in Accounts) row.SetPreferredGameName(place,game.Name); // Every row on the page with this place; others ignore it.
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
        catch(Exception error) {_diagnostics.RecordFailure(ManagerSubsystem.Directory,error.GetType());} // The name is decoration; "Place {id}" stands without it.
        finally
        {
            if(ReferenceEquals(_preferredGameStop,stop)) {_preferredGameStop=null;_preferredGamePlace=0;}
            stop.Dispose();
        }
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
        // Spreading is asked for but a server is pinned: the pinned server still goes to every account.
        bool spread=ServerBrowser.SpreadsFor(target);
        string advice=ServerBrowser.SpreadAcrossServers && !spread
            ?"A server is pinned, so every account was sent to it. To give each account its own listed server, choose the game without a server. "+capacityAdvice.Explanation
            :capacityAdvice.Explanation;
        int queued=0,blocked=0,skipped=0;
        if(!ct.IsCancellationRequested && epoch==_directoryGeneration && CanUseDirectories())
        {
            // One review and one durable queue write for the whole frozen snapshot. The review is
            // fenced by the queue's stop generation (Panic) and the caller's token; the queue owns
            // the write to completion and cancels the accepted subset itself if a stop races it.
            // A spread review reads the loaded server list once, here, and freezes the shuffled server
            // order; the queue hands servers only to the requests it records.
            LaunchBatchReview review;
            try
            {
                review=spread?_launchQueue.CaptureSpreadBatchReview(target.PlaceId,ServerBrowser.LoadedServers(target.PlaceId),snapshot,Random.Shared)
                    :_launchQueue.CaptureBatchReview(target,snapshot);
            }
            catch(ArgumentException error)
            {
                _diagnostics.RecordFailure(ManagerSubsystem.Queue,error.GetType());
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
            // The recorded requests are one batch for Discord alerts, which announce it once every request has settled.
            if(!withdrawn) NoteLaunchBatchRecorded(rows,target.PlaceId);
            int withServer=0;
            foreach(var row in rows)
            {
                if(row is not {Disposition:LaunchBatchDisposition.Recorded,Operation:{ } operation}) continue;
                if(withdrawn)
                {
                    if(!LaunchDescriptions.Terminal(operation.State))
                        try{await _launchQueue.CancelAsync(operation.Intent.OperationId,CancellationToken.None);}
                        catch(Exception error){_diagnostics.RecordFailure(ManagerSubsystem.Queue,error.GetType());}
                    continue;
                }
                if(operation.State==LaunchState.Blocked) blocked++;
                else if(!LaunchDescriptions.Terminal(operation.State)) queued++;
                else continue;
                if(operation.Intent.RequestedJobId is not null) withServer++;
            }
            // Already pending, queue full, unavailable forecast, cancelled by a stop or withdrawn.
            skipped=snapshot.Count-queued-blocked;
            // The queue hands a spread's servers only to the requests it records, so describe those, not the reviewed accounts.
            if(review.Spread is { } servers) advice=servers.ExplainRecorded(queued+blocked,withServer);
        }
        QueueExpanded=true;
        string resume=queued>0&&EffectiveQueuePaused?" Queue paused: choose Resume queue to start Roblox.":"";
        return $"Place {target.PlaceId} destination requests: {queued} queued, {blocked} blocked, {skipped} already pending or unavailable.{resume} No arrival is assumed. {advice}";
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
        StopPreferredGameLookup();_preferredGameNames.Clear();foreach(var row in Accounts) row.ClearPreferredGameName();
        DestinationSearch.CancelWork(clearInput:true);
        RobloxFavorites.Cancel();
        _joinGameSourcesSuspended=true;RecentlyPlayed.Cancel();
        ShareLink.Cancel();
        _shareLinkClient.ForgetAllTokens(); // Lock and Panic: the next share-link resolve starts with a fresh handshake.
    }
    private async Task StopDirectoryFeaturesAsync()
    {
        DisposeDestinationCapacity();
        CancelDirectoryFeatures(true);
        await Task.WhenAll(RefreshPublicAccount.Execution,QueueTargetedAccounts.Execution,AccountPower.Execution,PreferredGameNameOperation);
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
        catch(Exception ex) { if(!_shuttingDown && !_preserveStopNotice) Fail(ex,ManagerSubsystem.Directory); else _diagnostics.RecordFailure(ManagerSubsystem.Directory,ex.GetType()); }
        finally {_favoritesReloadRunning=false;}
    }
}
