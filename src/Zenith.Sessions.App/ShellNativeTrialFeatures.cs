using System.ComponentModel;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private readonly SelectableNativeHandoffCapture _nativeCapture;
    private readonly WindowsConfiguredNativeLaunchProvider _nativeLaunch;
    private int _nativeMetadataPending;
    private long _nativeTrialEpoch;
    private long _nativeReviewVersion;
    private NativeTrialReview? _nativeTrialReview;
    private AccountRow? _nativeReviewAccount;
    private CancellationTokenSource? _nativeReviewStop;
    private Task _nativeMetadataRefresh=Task.CompletedTask;
    private string _nativeTrialStatus="Native launch is unverified. Trials are explicit, one account at a time; ordinary and group launching remain blocked.";
    public string NativeTrialStatus {get=>_nativeTrialStatus;private set=>Set(ref _nativeTrialStatus,value);}
    public string NativeTrialSelection=>ServerBrowser.HasDestinationDraft?"Search / Use the pending destination draft before reviewing a trial.":SelectedAccount is {KnownUserId:>0} row&&ServerBrowser.ReadyDestination is { } destination
        ?$"{row.Label} · expected player {row.KnownUserId} · place {destination.PlaceId}"+(destination.JobId is { } job?$" · server {job}":" · any server")
        :"Select a saved, signed-in account and an explicit game or server in Sessions → Join a game.";
    // Only the file the provider accepted is the trial installation (the native player, or since R-launch-17 an accepted
    // bootstrapper's own executable); a rejected row is never shown as one.
    public string NativeTrialInstallation=>SelectedInstallation?.Candidate is not { } candidate?"Choose a native Roblox installation in Settings first."
        :_nativeLaunch.Selected?candidate.ExecutablePath
        :"The selected installation is not usable for a native trial; see the trial status.";
    // While a recorded trial has turned ordinary launching on (R-launch-0) the queue refuses a trial, so none is offered.
    private bool CanPrepareNativeTrial=>_ready&&QueueReady&&!IsSample&&!IsLocked&&!_shuttingDown&&!IsAuthorizing
        &&_nativeLaunch.Selected&&_nativeLaunch.CleanupHealthy&&!_nativeLaunch.IsBusy
        &&SelectedAccount is {KnownUserId:>0,AuthorizationGeneration:>0};
    public bool CanRunNativeTrial=>CanPrepareNativeTrial&&!IsNativeLaunchVerified&&ServerBrowser.ReadyDestination is {PlaceId:>0};
    public bool CanRunPrivateNativeTrial=>CanPrepareNativeTrial&&SavedNativeLaunchMode==NativeLaunchMode.OfficialPage
        &&ShareLink is {SelectedSaved: {Revision:>0} saved}&&ShareLink.Saved.Contains(saved);
    public AsyncCommand StartNativeTrial {get;private set;}=null!;
    public Command CancelNativeTrial {get;private set;}=null!;

    // This bounded, memory-only object is a review of one exact request. It grants
    // no provider capability and can be consumed only by the Shell that captured it.
    public sealed class NativeTrialReview
    {
        internal AccountProfile Profile {get;}
        internal long PlayerId {get;}
        internal long AuthorizationGeneration {get;}
        internal RobloxRequestedDestination Destination {get;}
        internal ClientInstallationCandidate Installation {get;}
        internal long Epoch {get;}
        internal long Version {get;}
        /// <summary>The saved native launch mode at review time (R-api-7); the trial status repeats what it does.</summary>
        internal NativeLaunchMode Mode {get;}
        public string SelectionSummary {get;}
        public string InstallationSummary {get;}
        /// <summary>The extra client arguments the direct start will add after Roblox's request, as saved now: this account's own set,
        /// else the saved default, else none (R-launch-16). Shown in the review only; never logged.</summary>
        public string ArgumentsSummary {get;}
        /// <summary>What confirming does in the saved mode: open the page and wait for Play, or request a ticket and start the player.</summary>
        public string LaunchModeSummary {get;}
        internal NativeTrialReview(AccountRow row,RobloxRequestedDestination destination,ClientInstallationCandidate installation,long epoch,long version,string defaultClientArguments="",
            NativeLaunchMode mode=NativeLaunchMode.OfficialPage,string? privateLabel=null)
        {
            Profile=row.Profile;PlayerId=row.KnownUserId!.Value;AuthorizationGeneration=row.AuthorizationGeneration!.Value;
            Destination=new(destination.PlaceId,destination.JobId,destination.ServerKind,destination.PrivateServer);Installation=installation;Epoch=epoch;Version=version;Mode=mode;
            SelectionSummary=$"{row.Label} · expected player {PlayerId} · place {Destination.PlaceId}"
                +(Destination.PrivateServer is not null?" · saved private server “"+privateLabel+"” · arrival remains unverified":Destination.JobId is { } job?$" · server {job}":" · any server");
            // A bootstrapper installation (R-launch-17): Zenith starts the bootstrapper, which starts the player with its own settings.
            bool bootstrapper=BootstrapperProducts.IsBootstrapper(installation.Product);
            InstallationSummary=bootstrapper
                ?$"{installation.ExecutablePath} (launch through {installation.Product}: it starts the Roblox player and applies its own client settings and FFlags; Zenith does not write client files)"
                :installation.ExecutablePath;
            ArgumentsSummary=bootstrapper?$"Extra client arguments: not passed (the start goes through {installation.Product})"
                :ExtraClientArgumentPolicy.Describe(row.Profile.ExtraClientArguments,defaultClientArguments);
            string started=bootstrapper?$"start {installation.Product} with that request":"start the selected native player itself";
            LaunchModeSummary="Native launch mode: "+NativeLaunchModes.Label(mode)+". "+(mode==NativeLaunchMode.DirectTicket
                ?$"Zenith will request a launch ticket with this account's saved sign-in and {started}; no Roblox page opens. This may start a real Roblox client."
                :"Continue to the selected account's Roblox game page. Choosing Play there may start a real Roblox client.");
        }
        public override string ToString()=>"Selected-account native trial review";
    }

    public NativeTrialReview? CaptureNativeTrialReview()
    {
        _nativeTrialReview=null;
        if(!CanRunNativeTrial || SelectedAccount is not { } selected || !Accounts.Contains(selected)
            ||ServerBrowser.ReadyDestination is not { } destination || SelectedInstallation?.Candidate is not { } installation)return null;
        return _nativeTrialReview=new(selected,destination,installation,Interlocked.Read(ref _nativeTrialEpoch),Interlocked.Read(ref _nativeReviewVersion),_savedSettings.DefaultClientArguments,
            SavedNativeLaunchMode);
    }
    public NativeTrialReview? CapturePrivateNativeTrialReview()
    {
        _nativeTrialReview=null;
        if(!CanRunPrivateNativeTrial || SelectedAccount is not { } selected || !Accounts.Contains(selected)
            ||ShareLink.SelectedSaved is not { } saved || SelectedInstallation?.Candidate is not { } installation)return null;
        return _nativeTrialReview=new(selected,new(saved.PlaceId,null,RobloxServerKind.Private,new(saved.Id,saved.Revision)),
            installation,Interlocked.Read(ref _nativeTrialEpoch),Interlocked.Read(ref _nativeReviewVersion),_savedSettings.DefaultClientArguments,NativeLaunchMode.OfficialPage,saved.Name+" · …"+saved.Last4);
    }
    private void PrivateTrialSelectionChanged(object? sender,PropertyChangedEventArgs args)
    { if(args.PropertyName==nameof(ShareLinkViewModel.SelectedSaved)) { InvalidateNativeTrialReview();Raise(nameof(CanRunPrivateNativeTrial)); } }
    private void StopPrivateTrialForRemoval()
    {
        // Always fence before storage: a just-committed request may not have reached the UI snapshot yet.
        try { FenceNativeTrial("Private-server trial stopped because a saved server is being removed. Review the queue before resuming."); }
        finally { _launchQueue.SignalPanic(); }
    }
    public void DiscardNativeTrialReview(NativeTrialReview review)
    {Interlocked.CompareExchange(ref _nativeTrialReview,null,review);}

    private void InitializeNativeTrialFeatures()
    {
        StartNativeTrial=new(StartNativeTrialAsync,error=>Fail(error,ManagerSubsystem.Launch));
        CancelNativeTrial=new(_=>{FenceNativeTrial(NativeTrialStoppedByUser);_launchQueue.SignalPanic();});
        PropertyChanged+=NativeTrialOwnerChanged;ServerBrowser.PropertyChanged+=NativeTrialDestinationChanged;ShareLink.PropertyChanged+=PrivateTrialSelectionChanged;
        InitializeLaunchEvidenceFeatures();InitializeNativeLaunchVerificationFeatures();
    }
    private void NativeTrialOwnerChanged(object? sender,PropertyChangedEventArgs args)
    {
        if(args.PropertyName==nameof(SelectedAccount))
        {
            if(_nativeReviewAccount is not null)_nativeReviewAccount.PropertyChanged-=NativeTrialAccountChanged;
            _nativeReviewAccount=SelectedAccount;
            if(_nativeReviewAccount is not null)_nativeReviewAccount.PropertyChanged+=NativeTrialAccountChanged;
            InvalidateNativeTrialReview();
        }
        if(args.PropertyName is nameof(IsReady) or nameof(QueueReady) or nameof(IsLocked) or nameof(HasSelection) or nameof(IsAuthorizing)){RefreshNativeTrialFeatures();RefreshLaunchEvidence();}
    }
    private void NativeTrialAccountChanged(object? sender,PropertyChangedEventArgs args)
    {if(args.PropertyName is nameof(AccountRow.KnownUserId) or nameof(AccountRow.AuthorizationGeneration))InvalidateNativeTrialReview();}
    private void NativeTrialDestinationChanged(object? sender,PropertyChangedEventArgs args)
    {
        if(args.PropertyName is nameof(ServerBrowserViewModel.Destination) or nameof(ServerBrowserViewModel.LinkInput))InvalidateNativeTrialReview();
        if(args.PropertyName is nameof(ServerBrowserViewModel.Destination) or nameof(ServerBrowserViewModel.CanUseDestination))RefreshNativeTrialFeatures();
    }
    private void InvalidateNativeTrialReview()
    {Interlocked.Increment(ref _nativeReviewVersion);Interlocked.Exchange(ref _nativeTrialReview,null);_nativeReviewStop?.Cancel();}
    private void ConfigureNativeTrialInstallation()
    {
        // A cleared selection (Refresh, Cancel check, Panic or Lock) is not a rejected row;
        // only a row that is still selected may be described as unsupported.
        var selection=SelectedInstallation?.Candidate;
        // The provider's own acceptance rule, not a looser copy: the launcher and bootstrapper-managed
        // players are present Roblox rows that Configure would reject, so they are never eligible.
        // This build offers standard-player trials only; bootstrapper inspection remains available.
        var eligible=WindowsConfiguredNativeLaunchProvider.CanSelect(selection)&&selection!.Product==ClientInstallationProduct.Roblox?selection:null;
        // Revoke trial permission (and pause the queue) only when the trial installation can
        // actually change: off a selected native file, or onto a row the provider accepts. Moving
        // between rows that can never be the trial installation only updates the status text.
        bool interrupted=false, queueWasRunning=!EffectiveQueuePaused, selectionFenced=_nativeLaunch.Selected||eligible is not null;
        if(selectionFenced)
        {
            interrupted=HasNativeTrialInProgress();
            InvalidateNativeTrialReview();
            Interlocked.Increment(ref _nativeTrialEpoch);
            _launchQueue.SignalPanic();
        }
        try
        {
            _nativeLaunch.Configure(eligible);
            // A recorded trial for the saved mode and this row's product (R-launch-0) means ordinary launching is on through it.
            NativeTrialStatus=_nativeLaunch.Selected?(SavedNativeLaunchVerification is { } verified&&IsNativeLaunchVerified?VerifiedNativeLaunchStatus(verified)
                    :BootstrapperProducts.IsBootstrapper(eligible!.Product)
                    ?$"Launch through {eligible.Product} selected for an explicit trial. {eligible.Product} starts the Roblox player and applies its own client settings and FFlags; Zenith does not write client files. Its signature is checked against the publisher you accepted, and the account and destination are checked again, before any start; compatibility remains unverified."
                    :"Native file selected for an explicit trial. Its publisher, account and destination are checked again; compatibility remains unverified.")
                :selection is null?NoNativeInstallationSelected:UnsupportedNativeInstallation(SelectedInstallation);
        }
        catch(ArgumentException)
        {
            _nativeLaunch.Configure(null);
            NativeTrialStatus=UnsupportedNativeInstallation(SelectedInstallation);
        }
        // The fence above is intended; replace the selection text and the stale "Trial queued" banner
        // so the user knows the trial stopped. A Panic or Lock reason keeps priority.
        if(interrupted&&!IsLocked&&!_shuttingDown&&!_preserveStopNotice)NativeTrialStatus=Notice=NativeTrialStoppedBySelection;
        else if(queueWasRunning&&!IsLocked&&!_shuttingDown&&!_preserveStopNotice&&selectionFenced)
            Notice="The installation selection changed and the launch queue was paused. Review the selection, then choose Resume queue when ready.";
        PublishLaunchRelease();
        RefreshNativeTrialFeatures();RefreshNativeLaunchVerification();
    }
    private const string NativeTrialStoppedBySelection="Trial stopped because the installation selection changed, and the launch queue is paused. Review any process-start receipt in the queue before retrying; existing Roblox clients remain open.";
    // Stop trial signals the queue's Panic, which pauses every queued request (other accounts' too), not only this trial; the
    // card says so and names Resume queue, since the checking line sends users here just to cancel a release check (O-C9-3).
    private const string NativeTrialStoppedByUser="Trial stopped, and the launch queue is paused. Review any process-start receipt in the queue before retrying, then choose Resume queue when ready; existing Roblox clients remain open.";
    // Read before the fence: the queue's Panic signal ends IsTrialOperation for the current request.
    private bool HasNativeTrialInProgress()=>_nativeLaunch.IsBusy||StartNativeTrial is {Execution.IsCompleted:false}
        ||Volatile.Read(ref _latestQueue).Operations.Any(operation=>(operation.State is LaunchState.Queued or LaunchState.Validating or LaunchState.Preparing
            or LaunchState.Launching or LaunchState.Stabilizing)&&_launchQueue.IsTrialOperation(operation.Intent.OperationId));
    private const string NoNativeInstallationSelected="No native installation is selected. In Settings, choose Refresh installation, then select a present native Roblox player file.";
    private static string UnsupportedNativeInstallation(InstallationCandidateRow? row)=>row?.ManagedBy is { } owner
        ?$"This Roblox player is managed by {owner}. Native trials are not supported here: bootstrapper launching is deferred until its exact player can be associated with the launch. Select a Roblox player that Roblox installed itself. The inventory row remains available for inspection."
        :row?.Candidate is { } candidate&&BootstrapperProducts.IsBootstrapper(candidate.Product)
        ?$"This row is the {candidate.Product} bootstrapper. Native trials are not supported here: bootstrapper launching is deferred until its exact Roblox player can be associated with the launch. Publisher inspection remains available; use the standard Roblox player for this build."
        :"This installation is not supported for a native trial. Select a present native Roblox player file; the inventory row remains available for inspection.";
    private void FenceNativeTrial(string reason)
    {
        InvalidateNativeTrialReview();
        Interlocked.Increment(ref _nativeTrialEpoch);_nativeLaunch.CancelActive();ClearSelectedNativeDiagnostics();NativeTrialStatus=reason;
        _launchEvidenceExportStop?.Cancel();
    }
    // Removing a profile or saved sign-in stops the whole queue by design and waits before changing
    // saved state. That also ends any trial in progress, so fence its card and return a notice suffix
    // naming the stop; nothing is added when the queue was already paused and no trial was running.
    private string StopQueueForSavedStateRemoval(string trialReason)
    {
        bool trialStopped=_nativeLaunch.IsBusy||!StartNativeTrial.Execution.IsCompleted,queueWasActive=!EffectiveQueuePaused;
        if(trialStopped)FenceNativeTrial(trialReason);
        _launchQueue.SignalPanic();
        return trialStopped?" The native trial in progress was stopped and the launch queue was paused. Review the queue and choose Resume queue when ready."
            :queueWasActive?" The launch queue was paused. Review the queue and choose Resume queue when ready.":"";
    }
    private void RefreshNativeTrialFeatures()
    {
        Raise(nameof(CanRunNativeTrial));Raise(nameof(CanRunPrivateNativeTrial));Raise(nameof(CanRefreshInstallations));Raise(nameof(CanAddRobloxAccount));Raise(nameof(NativeTrialSelection));Raise(nameof(NativeTrialInstallation));Raise(nameof(CanInspectNativeHandoff));
        if(!_ready||IsLocked||_shuttingDown||_nativeLaunch.IsBusy||IsAuthorizing||!_nativeMetadataRefresh.IsCompleted
            ||Interlocked.Exchange(ref _nativeMetadataPending,0)==0)return;
        // Reopen only the nonsecret projection after an owned preparation may rotate a session.
        // One tracked reload, never one worker per account or an authorization auto-refresh.
        _nativeMetadataRefresh=RefreshNativeMetadataAsync();
    }
    private async Task RefreshNativeMetadataAsync()
    {
        try{await LoadAccountsAsync(_lifetime.Token);}
        catch(OperationCanceledException)when(_lifetime.IsCancellationRequested){}
        catch(Exception error){_diagnostics.RecordFailure(ManagerSubsystem.Library,error.GetType());NativeTrialStatus="Refresh the account list to see the latest saved sign-in state. Existing files were preserved.";}
    }
    private bool IsNativeTrialReviewCurrent(NativeTrialReview review)=>(review.Destination.PrivateServer is not null?CanRunPrivateNativeTrial:CanRunNativeTrial)
        &&review.Mode==SavedNativeLaunchMode
        &&review.Epoch==Interlocked.Read(ref _nativeTrialEpoch)&&review.Version==Interlocked.Read(ref _nativeReviewVersion)
        &&SelectedAccount is { } selected && Accounts.Contains(selected) && selected.Profile==review.Profile
        &&selected.KnownUserId==review.PlayerId&&selected.AuthorizationGeneration==review.AuthorizationGeneration
        &&SelectedInstallation?.Candidate==review.Installation
        &&(review.Destination.PrivateServer is { } reference
            ? ShareLink.SelectedSaved is { } saved&&saved.Id==reference.Id&&saved.Revision==reference.Revision&&saved.PlaceId==review.Destination.PlaceId
            : ServerBrowser.ReadyDestination is { } destination&&destination.PlaceId==review.Destination.PlaceId
                &&destination.JobId==review.Destination.JobId&&destination.ServerKind==review.Destination.ServerKind);
    private const string NativeTrialReviewUnavailable="This trial review changed or is no longer available. Review the selected account and destination again before starting.";
    // A review change cancelled a start that was still checking Roblox's release or the saved sign-in: the user has just read
    // that the trial is being checked, so the card must say it was not queued rather than show what it said before the start.
    private const string NativeTrialNotStartedReviewChanged="Trial not started: the selected account or destination changed while it was being checked. Review the selected account and destination again before starting.";
    private void RejectNativeTrialReview(string reason=NativeTrialReviewUnavailable)
    {
        if(IsLocked||_shuttingDown||_preserveStopNotice)return;
        NativeTrialStatus=Notice=reason;
    }
    private async Task StartNativeTrialAsync(object? parameter)
    {
        var pending=Interlocked.Exchange(ref _nativeTrialReview,null);
        if(parameter is NativeTrialReview {Destination.PrivateServer:not null} ? !CanRunPrivateNativeTrial : !CanRunNativeTrial)return;
        if(parameter is not NativeTrialReview review||!ReferenceEquals(pending,review)||!IsNativeTrialReviewCurrent(review))
        {RejectNativeTrialReview();return;}
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _nativeReviewStop=stop;
        LaunchOperation request;
        try
        {
            // The queue holds this start while a client runs if the selected row reads "Not current" (Roblox's update may close them).
            await ReadLaunchReleaseAsync(stop.Token);
            var profile=await _accounts.FindAsync(review.Profile.Id,stop.Token);
            var grant=await _vault.GetSummaryAsync(review.Profile.Id,stop.Token);
            if(!IsNativeTrialReviewCurrent(review)||profile!=review.Profile||grant is not {Health:AuthorizationHealth.Linked}
                ||grant.ProfileId!=review.Profile.Id||grant.VerifiedUserId!=review.PlayerId||grant.Generation!=review.AuthorizationGeneration
                ||grant.ExpiresUtc<=DateTimeOffset.UtcNow||grant.Provider!=RobloxSessionIdentity.Provider
                ||!AuthorizationIdentity.HasVerifiedContext(grant.Provider,grant.Issuer,grant.Subject,grant.VerifiedUserId))
            {RejectNativeTrialReview();return;}
            stop.Token.ThrowIfCancellationRequested();
            _preserveStopNotice=false;
            request=await _launchQueue.EnqueueTrialAsync(review.Profile,stop.Token,new(review.PlayerId,review.AuthorizationGeneration),review.Destination);
            // The one request that may later be recorded as verified (R-launch-0), with the mode and installation it actually uses.
            _lastTrialStart=review.Destination.PrivateServer is null?(request.Intent.OperationId,review.Mode,review.Installation.Product):null;
            if(stop.IsCancellationRequested||review.Epoch!=Interlocked.Read(ref _nativeTrialEpoch)
                ||review.Version!=Interlocked.Read(ref _nativeReviewVersion))
            {await CancelAbandonedNativeTrialAsync(request);return;}
        }
        // A review change (another account or destination, a reloaded user id or grant) cancels this start through `stop` while it
        // is still reading Roblox's release, the profile or the grant, and sets no line of its own, so the start says the trial
        // was not queued (O-C9-2). Stop trial, an installation change, Panic and Lock advance the epoch and set their own reason,
        // which is kept; shutdown cancels the lifetime and says nothing. AsyncCommand would otherwise swallow this cancellation.
        catch(OperationCanceledException) when(stop.IsCancellationRequested&&!_lifetime.IsCancellationRequested
            &&review.Epoch==Interlocked.Read(ref _nativeTrialEpoch))
        {RejectNativeTrialReview(NativeTrialNotStartedReviewChanged);return;}
        finally {if(ReferenceEquals(_nativeReviewStop,stop))_nativeReviewStop=null;}
        if(IsLocked||_shuttingDown||_lifetime.IsCancellationRequested||review.Epoch!=Interlocked.Read(ref _nativeTrialEpoch))return;
        _preferredEvidenceOperation=request.Intent.OperationId;RefreshLaunchEvidence();
        QueueExpanded=true;
        bool resumed=await _launchQueue.ResumeTrialAsync(request.Intent.OperationId,_lifetime.Token);
        if(review.Epoch!=Interlocked.Read(ref _nativeTrialEpoch)||_preserveStopNotice||IsLocked||_shuttingDown)return;
        NativeTrialStatus=resumed?"Trial queued for "+review.SelectionSummary+". "+DescribeNativeLaunchMode(review.Mode)
            :"The trial was stopped before it could resume. Review the queue and create a fresh request.";
        Notice=NativeTrialStatus;
    }
    // The review went stale after the queue saved this request and passed its own stop check. A review
    // change does not stop the queue, so the saved row keeps this request's in-memory trial consent and a
    // later general Resume queue could start it without a current review. Cancel it durably, as the queue
    // does for a stop during the save. Shutdown is left out: its Panic already revoked the consent, the
    // queue is closing, and a restart reloads a never-started request as interrupted.
    private async Task CancelAbandonedNativeTrialAsync(LaunchOperation request)
    {
        if(LaunchDescriptions.Terminal(request.State)||_shuttingDown||_lifetime.IsCancellationRequested)return;
        try{await _launchQueue.CancelAsync(request.Intent.OperationId,CancellationToken.None);}
        catch(Exception error)
        {
            Fail(error,ManagerSubsystem.Queue,"The trial review changed after its request was queued, and that request could not be cancelled. "
                +"Review it in the launch queue before choosing Resume queue.");
        }
    }
    private async Task DisposeNativeTrialFeaturesAsync()
    {
        InvalidateNativeTrialReview();
        if(_nativeReviewAccount is not null)_nativeReviewAccount.PropertyChanged-=NativeTrialAccountChanged;
        PropertyChanged-=NativeTrialOwnerChanged;ServerBrowser.PropertyChanged-=NativeTrialDestinationChanged;ShareLink.PropertyChanged-=PrivateTrialSelectionChanged;
        await StartNativeTrial.Execution;await _nativeMetadataRefresh;
        _launchEvidenceExportStop?.Cancel();await ExportLaunchEvidence.Execution;
        try{await _nativeLaunch.DisposeAsync();}
        catch(Exception error){ReportNativeTrialCleanupFailure(error);}
        finally
        {
            try{await _nativeCapture.DisposeAsync();}
            catch(Exception error){ReportNativeTrialCleanupFailure(error);}
            finally{(_launchTicketClient as IDisposable)?.Dispose();} // After the capture that borrowed it; kept tokens go with it.
        }
    }
    private void ReportNativeTrialCleanupFailure(Exception error)
    {
        _diagnostics.RecordFailure(ManagerSubsystem.Launch,error.GetType());
        NativeTrialStatus=Notice="Native trial cleanup could not be confirmed. Existing data was preserved; review cleanup before another launch.";
    }
}
