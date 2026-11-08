using System.ComponentModel;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private IRobloxHandoffProbe _handoffProbe=null!;
    private string _handoffInspectionStatus="Select a saved account and a game-only destination. This test observes and cancels Roblox's native handoff; it starts no client.";
    public AsyncCommand InspectNativeHandoff {get;private set;}=null!;
    public string HandoffInspectionStatus {get=>_handoffInspectionStatus;private set=>Set(ref _handoffInspectionStatus,value);}
    public bool CanInspectNativeHandoff=>_ready&&!IsLocked&&!IsAuthorizing&&!_nativeLaunch.IsBusy&&!_shuttingDown&&!_lifetime.IsCancellationRequested
        &&SelectedAccount?.KnownUserId is >0&&ServerBrowser.ReadyDestination is {PlaceId:>0,JobId:null}
        // Only the permanent cleanup latch disables the button; a transient Busy is answered on click,
        // since nothing would re-enable the button when another browser finishes closing. A storage
        // refusal is answered on click too, so its status can say what lifts it.
        &&_handoffProbe.Refusal is not RobloxHandoffProbeOutcome.CleanupFailed;
    public string HandoffSelectionSummary=>SelectedAccount is { } row
        ?$"Selected: {row.Profile.Label} · "+(row.KnownUserId is { } id?$"last-known player ID {id}":"saved sign-in required")
        :"Select an account in Accounts first.";
    private void InitializeHandoffProbe(IRobloxHandoffProbe? probe)
    {
        _handoffProbe=probe??new OwnedRobloxHandoffProbe(DataRoot,_dispatcher);
        InspectNativeHandoff=new(_=>InspectNativeHandoffAsync(),error=>Fail(error,ManagerSubsystem.Launch));
        PropertyChanged+=HandoffOwnerChanged;ServerBrowser.PropertyChanged+=HandoffDestinationChanged;
    }
    private void HandoffOwnerChanged(object? sender,PropertyChangedEventArgs args)
    {
        if(args.PropertyName is nameof(IsReady) or nameof(IsLocked) or nameof(HasSelection) or nameof(IsAuthorizing))
        {Raise(nameof(CanInspectNativeHandoff));Raise(nameof(HandoffSelectionSummary));}
    }
    private void HandoffDestinationChanged(object? sender,PropertyChangedEventArgs args)
    {if(args.PropertyName is nameof(ServerBrowserViewModel.Destination) or nameof(ServerBrowserViewModel.CanUseDestination))Raise(nameof(CanInspectNativeHandoff));}
    private async Task InspectNativeHandoffAsync()
    {
        if(!CanInspectNativeHandoff||SelectedAccount is not { } selected||ServerBrowser.ReadyDestination is not {JobId:null} destination)return;
        Guid profileId=selected.Profile.Id;long place=destination.PlaceId;
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _authorizationStop=stop;_linkingProfile=profileId;Raise(nameof(IsAuthorizing));_preserveStopNotice=false;
        HandoffInspectionStatus=$"Inspecting the selected account's game page for place {place}. Choose Play there; every external launch is cancelled. Sign-in may need renewal if this check is interrupted.";
        Notice="Native preparation inspection is open. No Roblox client will be started.";
        try
        {
            var result=await Task.Run(()=>_linkService.InspectHandoffAsync(profileId,place,_handoffProbe,stop.Token),stop.Token);
            if(!_lifetime.IsCancellationRequested)await LoadAccountsAsync(_lifetime.Token);
            stop.Token.ThrowIfCancellationRequested();if(IsLocked||_shuttingDown)return;
            HandoffInspectionStatus=DescribeHandoffResult(result,place);Notice=HandoffInspectionStatus;
            var kind=DiagnosticKindFor(result.Outcome);
            if(kind==ManagerEventKind.OperationFailed) _diagnostics.RecordFailure(FailedSubsystemFor(result.Outcome));else _diagnostics.Record(kind);
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested)
        {
            if(!_lifetime.IsCancellationRequested)await LoadAccountsAsync(_lifetime.Token);
            if(!_preserveStopNotice&&!IsLocked&&!_shuttingDown)
                HandoffInspectionStatus=Notice="Inspection cancelled. No native dispatch was permitted. Check the account's sign-in state before retrying.";
        }
        catch(Exception)
        {
            if(!_lifetime.IsCancellationRequested)await LoadAccountsAsync(_lifetime.Token);
            if(stop.IsCancellationRequested||IsLocked||_shuttingDown)return;
            HandoffInspectionStatus="The inspection could not finish. No native dispatch was permitted. Check saved sign-in and private browser cleanup before retrying.";
            throw;
        }
        finally{_authorizationStop=null;_linkingProfile=null;Raise(nameof(IsAuthorizing));}
    }
    // A closed page, another owner holding the lane, or no handoff before the deadline is inconclusive,
    // not a failure: it stays out of the Error count. Rejections and cleanup problems remain errors.
    private static ManagerEventKind DiagnosticKindFor(RobloxHandoffProbeOutcome outcome)=>outcome switch
    {
        RobloxHandoffProbeOutcome.CapturedAndPrevented=>ManagerEventKind.HandoffInspected,
        RobloxHandoffProbeOutcome.Cancelled or RobloxHandoffProbeOutcome.Busy or RobloxHandoffProbeOutcome.TimedOut=>ManagerEventKind.HandoffNotCompleted,
        _=>ManagerEventKind.OperationFailed
    };
    // A rejected or changed sign-in is a sign-in failure; every other failed outcome is in the native handoff itself.
    private static ManagerSubsystem FailedSubsystemFor(RobloxHandoffProbeOutcome outcome)=>outcome is RobloxHandoffProbeOutcome.SignInRequired
        or RobloxHandoffProbeOutcome.IdentityMismatch or RobloxHandoffProbeOutcome.AuthorizationChanged?ManagerSubsystem.SignIn:ManagerSubsystem.Launch;
    public static string DescribeHandoffResult(RobloxHandoffProbeResult result,long place)=>result.Outcome switch
    {
        RobloxHandoffProbeOutcome.CapturedAndPrevented when result.Shape is { } shape=>$"Place {place}: external handoff captured and cancelled. {shape.FieldCount} fields; known labels: {shape.KnownFields}; {shape.UnknownFieldCount} unknown; {shape.DuplicateFieldCount} duplicates. Page gesture reported: {(result.UserInitiated is true?"yes":result.UserInitiated is false?"no":"unknown")}. No native client, account selection or arrival was verified.",
        RobloxHandoffProbeOutcome.SignInRequired=>"The selected saved sign-in needs renewal. Sign in again before another inspection; no client was started.",
        RobloxHandoffProbeOutcome.IdentityMismatch=>"The private page's current sign-in did not match the selected player ID. It was not saved as that account; no client was started.",
        RobloxHandoffProbeOutcome.AuthorizationChanged=>"The account's saved authorization changed during inspection. The old result was discarded; no client was started.",
        RobloxHandoffProbeOutcome.CleanupFailed=>"Private browser or sign-in cleanup did not finish. No native dispatch was permitted. Preserve the data folder and close Zenith before retrying; if this repeats, review signin-browser-data in the Zenith data folder.",
        RobloxHandoffProbeOutcome.BrowserUnavailable=>"The private browser or its storage is unavailable, or its required launch-cancellation guard could not be installed. Check WebView2 Runtime, and that the Zenith data folder is on a local drive that is not redirected, with free space and write permission. No page handoff was allowed. The selected account's saved sign-in now needs renewal: sign in again once this is fixed.",
        RobloxHandoffProbeOutcome.StorageUnavailable=>"The private browser's storage could not be prepared on the last attempt, so this check was refused before it used the selected account's saved sign-in, which is still saved; no page was opened. The Zenith data folder must be on a local drive that is not redirected, with free space and write permission, and no other program may be using signin-browser-data. Fix that, then sign in to any account or restart Zenith Sessions; checks stay refused until one of those prepares the storage.",
        RobloxHandoffProbeOutcome.Busy=>"Another account operation or private browser window is still open or closing. Finish or cancel it, then retry the inspection; no page was opened.",
        RobloxHandoffProbeOutcome.ContextRejected=>"The game page changed or was not the approved game-only destination. No handoff was accepted. Renew sign-in if required, then retry with the intended game.",
        RobloxHandoffProbeOutcome.OriginRejected=>"The request did not report the expected official Roblox page origin. It was cancelled and no client was started.",
        RobloxHandoffProbeOutcome.ExternalSchemeBlocked=>"The page requested an unexpected external application. The request was cancelled and no client was started.",
        RobloxHandoffProbeOutcome.MalformedHandoff=>"The emitted handoff was outside the bounded diagnostic format. It was cancelled; it was not rewritten, stored or launched.",
        RobloxHandoffProbeOutcome.TimedOut=>"No acceptable handoff was observed before the deadline. This is inconclusive, not a native-launch result. Check sign-in before retrying.",
        RobloxHandoffProbeOutcome.Cancelled=>"Inspection cancelled. No native dispatch was permitted. Check sign-in before retrying.",
        _=>"Native preparation inspection failed. No dispatch was permitted; check sign-in and browser cleanup before retrying."
    };
    private async Task DisposeHandoffProbeAsync()
    {
        PropertyChanged-=HandoffOwnerChanged;ServerBrowser.PropertyChanged-=HandoffDestinationChanged;
        await InspectNativeHandoff.Execution;await _handoffProbe.DisposeAsync();
    }
}
