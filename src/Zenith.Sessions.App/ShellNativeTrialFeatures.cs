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
    public bool CanRunNativeTrial=>_ready&&QueueReady&&!IsSample&&!IsLocked&&!_shuttingDown&&!IsAuthorizing
        &&_nativeLaunch.Selected&&_nativeLaunch.CleanupHealthy&&!_nativeLaunch.IsBusy&&!IsNativeLaunchVerified
        &&SelectedAccount is {KnownUserId:>0,AuthorizationGeneration:>0}&&ServerBrowser.ReadyDestination is {PlaceId:>0};
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
            NativeLaunchMode mode=NativeLaunchMode.OfficialPage)
        {
            Profile=row.Profile;PlayerId=row.KnownUserId!.Value;AuthorizationGeneration=row.AuthorizationGeneration!.Value;
            Destination=new(destination.PlaceId,destination.JobId,destination.ServerKind);Installation=installation;Epoch=epoch;Version=version;Mode=mode;
            SelectionSummary=$"{row.Label} · expected player {PlayerId} · place {Destination.PlaceId}"
                +(Destination.JobId is { } job?$" · server {job}":" · any server");
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
    public void DiscardNativeTrialReview(NativeTrialReview review)
    {Interlocked.CompareExchange(ref _nativeTrialReview,null,review);}

    private void InitializeNativeTrialFeatures()
    {
        StartNativeTrial=new(StartNativeTrialAsync,error=>Fail(error,ManagerSubsystem.Launch));
        CancelNativeTrial=new(_=>{FenceNativeTrial("Trial stopped. Review any process-start receipt before retrying; existing Roblox clients remain open.");_launchQueue.SignalPanic();});
        PropertyChanged+=NativeTrialOwnerChanged;ServerBrowser.PropertyChanged+=NativeTrialDestinationChanged;
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
        // players are present Roblox rows that Configure would reject, so they are never eligible. A
        // bootstrapper's own row (R-launch-17) is eligible only while its publisher is accepted in the saved
        // settings; the trust verifier checks the file against that acceptance again before any start.
        var eligible=WindowsConfiguredNativeLaunchProvider.CanSelect(selection)&&(selection!.Product==ClientInstallationProduct.Roblox||IsAcceptedBootstrapper(selection))?selection:null;
        // Revoke trial permission (and pause the queue) only when the trial installation can
        // actually change: off a selected native file, or onto a row the provider accepts. Moving
        // between rows that can never be the trial installation only updates the status text.
        bool interrupted=false;
        if(_nativeLaunch.Selected||eligible is not null)
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
        PublishLaunchRelease();
        RefreshNativeTrialFeatures();RefreshNativeLaunchVerification();
    }
    private const string NativeTrialStoppedBySelection="Trial stopped because the installation selection changed, and the launch queue is paused. Review any process-start receipt in the queue before retrying; existing Roblox clients remain open.";
    // Read before the fence: the queue's Panic signal ends IsTrialOperation for the current request.
    private bool HasNativeTrialInProgress()=>_nativeLaunch.IsBusy||StartNativeTrial is {Execution.IsCompleted:false}
        ||Volatile.Read(ref _latestQueue).Operations.Any(operation=>(operation.State is LaunchState.Queued or LaunchState.Validating or LaunchState.Preparing
            or LaunchState.Launching or LaunchState.Stabilizing)&&_launchQueue.IsTrialOperation(operation.Intent.OperationId));
    private const string NoNativeInstallationSelected="No native installation is selected. In Settings, choose Refresh installation, then select a present native Roblox player file.";
    private static string UnsupportedNativeInstallation(InstallationCandidateRow? row)=>row?.ManagedBy is { } owner
        ?$"This Roblox player is managed by {owner}. Native trials are not supported for players managed by a bootstrapper; select a Roblox player that Roblox installed itself, or select the {owner} row itself to launch through {owner} once its publisher is accepted. The inventory row remains available for inspection."
        :row?.Candidate is { } candidate&&BootstrapperProducts.IsBootstrapper(candidate.Product)
        ?(BootstrapperProducts.IsOwnExecutable(candidate.Product,candidate.ExecutablePath)&&candidate.FileState==ClientInstallationFileState.PresentUnverified
            ?$"This row is the {candidate.Product} bootstrapper. Launching through it is not supported until you verify its publisher and choose Accept publisher for {candidate.Product} in Settings → Roblox installation. {candidate.Product} then starts the Roblox player and applies its own client settings and FFlags; Zenith never writes client files. The inventory row remains available for inspection."
            :$"This {candidate.Product} row is not supported for a native trial: only {candidate.Product}'s own present executable ({BootstrapperProducts.ExecutableName(candidate.Product)}) can start it. The inventory row remains available for inspection.")
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
        Raise(nameof(CanRunNativeTrial));Raise(nameof(CanRefreshInstallations));Raise(nameof(CanAddRobloxAccount));Raise(nameof(NativeTrialSelection));Raise(nameof(NativeTrialInstallation));Raise(nameof(CanInspectNativeHandoff));
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
    private bool IsNativeTrialReviewCurrent(NativeTrialReview review)=>CanRunNativeTrial
        &&review.Epoch==Interlocked.Read(ref _nativeTrialEpoch)&&review.Version==Interlocked.Read(ref _nativeReviewVersion)
        &&SelectedAccount is { } selected && Accounts.Contains(selected) && selected.Profile==review.Profile
        &&selected.KnownUserId==review.PlayerId&&selected.AuthorizationGeneration==review.AuthorizationGeneration
        &&SelectedInstallation?.Candidate==review.Installation
        &&ServerBrowser.ReadyDestination is { } destination&&destination.PlaceId==review.Destination.PlaceId
        &&destination.JobId==review.Destination.JobId&&destination.ServerKind==review.Destination.ServerKind;
    private void RejectNativeTrialReview()
    {
        if(IsLocked||_shuttingDown||_preserveStopNotice)return;
        NativeTrialStatus=Notice="This trial review changed or is no longer available. Review the selected account and destination again before starting.";
    }
    private async Task StartNativeTrialAsync(object? parameter)
    {
        var pending=Interlocked.Exchange(ref _nativeTrialReview,null);
        if(!CanRunNativeTrial)return;
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
            _lastTrialStart=(request.Intent.OperationId,review.Mode,review.Installation.Product);
            if(stop.IsCancellationRequested||review.Epoch!=Interlocked.Read(ref _nativeTrialEpoch)
                ||review.Version!=Interlocked.Read(ref _nativeReviewVersion))
            {await CancelAbandonedNativeTrialAsync(request);return;}
        }
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
        PropertyChanged-=NativeTrialOwnerChanged;ServerBrowser.PropertyChanged-=NativeTrialDestinationChanged;
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
