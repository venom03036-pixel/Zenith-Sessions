using System.ComponentModel;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private readonly IRobloxNativeHandoffCapture _nativeCapture;
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
    // Only the file the provider accepted is the trial installation; a rejected row is never shown as one.
    public string NativeTrialInstallation=>SelectedInstallation?.Candidate is not { } candidate?"Choose a native Roblox installation in Settings first."
        :candidate.Product==ClientInstallationProduct.Roblox&&_nativeLaunch.Selected?candidate.ExecutablePath
        :"The selected installation is not usable for a native trial; see the trial status.";
    public bool CanRunNativeTrial=>_ready&&QueueReady&&!IsSample&&!IsLocked&&!_shuttingDown&&!IsAuthorizing
        &&_nativeLaunch.Selected&&_nativeLaunch.CleanupHealthy&&!_nativeLaunch.IsBusy
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
        public string SelectionSummary {get;}
        public string InstallationSummary {get;}
        internal NativeTrialReview(AccountRow row,RobloxRequestedDestination destination,ClientInstallationCandidate installation,long epoch,long version)
        {
            Profile=row.Profile;PlayerId=row.KnownUserId!.Value;AuthorizationGeneration=row.AuthorizationGeneration!.Value;
            Destination=new(destination.PlaceId,destination.JobId,destination.ServerKind);Installation=installation;Epoch=epoch;Version=version;
            SelectionSummary=$"{row.Label} · expected player {PlayerId} · place {Destination.PlaceId}"
                +(Destination.JobId is { } job?$" · server {job}":" · any server");
            InstallationSummary=installation.ExecutablePath;
        }
        public override string ToString()=>"Selected-account native trial review";
    }

    public NativeTrialReview? CaptureNativeTrialReview()
    {
        _nativeTrialReview=null;
        if(!CanRunNativeTrial || SelectedAccount is not { } selected || !Accounts.Contains(selected)
            ||ServerBrowser.ReadyDestination is not { } destination || SelectedInstallation?.Candidate is not { } installation)return null;
        return _nativeTrialReview=new(selected,destination,installation,Interlocked.Read(ref _nativeTrialEpoch),Interlocked.Read(ref _nativeReviewVersion));
    }
    public void DiscardNativeTrialReview(NativeTrialReview review)
    {Interlocked.CompareExchange(ref _nativeTrialReview,null,review);}

    private void InitializeNativeTrialFeatures()
    {
        StartNativeTrial=new(StartNativeTrialAsync,Fail);
        CancelNativeTrial=new(_=>{FenceNativeTrial("Trial stopped. Review any process-start receipt before retrying; existing Roblox clients remain open.");_launchQueue.SignalPanic();});
        PropertyChanged+=NativeTrialOwnerChanged;ServerBrowser.PropertyChanged+=NativeTrialDestinationChanged;
        InitializeLaunchEvidenceFeatures();
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
        var eligible=selection is {Product:ClientInstallationProduct.Roblox,FileState:ClientInstallationFileState.PresentUnverified} candidate?candidate:null;
        // Revoke trial permission (and pause the queue) only when the trial installation can
        // actually change: off a selected native file, or onto a present native row. Moving
        // between rows that can never be the trial installation only updates the status text.
        if(_nativeLaunch.Selected||eligible is not null)
        {
            InvalidateNativeTrialReview();
            Interlocked.Increment(ref _nativeTrialEpoch);
            _launchQueue.SignalPanic();
        }
        try
        {
            _nativeLaunch.Configure(eligible);
            NativeTrialStatus=_nativeLaunch.Selected?"Native file selected for an explicit trial. Its publisher, account and destination are checked again; compatibility remains unverified."
                :selection is null?NoNativeInstallationSelected:UnsupportedNativeInstallation(SelectedInstallation);
        }
        catch(ArgumentException)
        {
            _nativeLaunch.Configure(null);
            NativeTrialStatus=UnsupportedNativeInstallation(SelectedInstallation);
        }
        RefreshNativeTrialFeatures();
    }
    private const string NoNativeInstallationSelected="No native installation is selected. In Settings, choose Refresh installation, then select a present native Roblox player file.";
    private static string UnsupportedNativeInstallation(InstallationCandidateRow? row)=>row?.ManagedBy is { } owner
        ?$"This Roblox player is managed by {owner}. Native trials are not supported for players managed by a bootstrapper; select a Roblox player that Roblox installed itself. The inventory row remains available for inspection."
        :"This installation is not supported for a native trial. Select a present native Roblox player file; the inventory row remains available for inspection.";
    private void FenceNativeTrial(string reason)
    {
        InvalidateNativeTrialReview();
        Interlocked.Increment(ref _nativeTrialEpoch);_nativeLaunch.CancelActive();ClearSelectedNativeDiagnostics();NativeTrialStatus=reason;
        _launchEvidenceExportStop?.Cancel();
    }
    private void RefreshNativeTrialFeatures()
    {
        Raise(nameof(CanRunNativeTrial));Raise(nameof(CanAddRobloxAccount));Raise(nameof(NativeTrialSelection));Raise(nameof(NativeTrialInstallation));Raise(nameof(CanInspectNativeHandoff));
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
        catch(Exception error){_diagnostics.Record(ManagerEventKind.OperationFailed,error.GetType());NativeTrialStatus="Refresh the account list to see the latest saved sign-in state. Existing files were preserved.";}
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
            if(stop.IsCancellationRequested||review.Epoch!=Interlocked.Read(ref _nativeTrialEpoch)
                ||review.Version!=Interlocked.Read(ref _nativeReviewVersion))return;
        }
        finally {if(ReferenceEquals(_nativeReviewStop,stop))_nativeReviewStop=null;}
        if(IsLocked||_shuttingDown||_lifetime.IsCancellationRequested||review.Epoch!=Interlocked.Read(ref _nativeTrialEpoch))return;
        _preferredEvidenceOperation=request.Intent.OperationId;RefreshLaunchEvidence();
        QueueExpanded=true;
        bool resumed=await _launchQueue.ResumeTrialAsync(request.Intent.OperationId,_lifetime.Token);
        if(review.Epoch!=Interlocked.Read(ref _nativeTrialEpoch)||_preserveStopNotice||IsLocked||_shuttingDown)return;
        NativeTrialStatus=resumed?"Trial queued for "+review.SelectionSummary+". The private Roblox game page may open. Choose Play there. A process receipt alone does not verify its account or arrival."
            :"The trial was stopped before it could resume. Review the queue and create a fresh request.";
        Notice=NativeTrialStatus;
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
        }
    }
    private void ReportNativeTrialCleanupFailure(Exception error)
    {
        _diagnostics.Record(ManagerEventKind.OperationFailed,error.GetType());
        NativeTrialStatus=Notice="Native trial cleanup could not be confirmed. Existing data was preserved; review cleanup before another launch.";
    }
}
