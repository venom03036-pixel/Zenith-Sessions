using System.Globalization;
using System.Text.Json.Serialization;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

/// <summary>Fixed labels over one transient observation, never launch or account authority.</summary>
public sealed record NativeLaunchDiagnosticPresentation
{
    public Guid? OperationId {get;}
    public bool Available=>Snapshot is not null;
    /// <summary>True when the read threw or returned a snapshot that was invalid or for another operation.
    /// Only the exception type is reported to the caller; this view never carries its message.</summary>
    public bool ReadFailed {get;}
    [JsonIgnore] public NativeLaunchDiagnosticSnapshot? Snapshot {get;}
    public string Summary=>Snapshot is not { } value
        ?OperationId is null?"Select a launch request to inspect its native stages."
            :ReadFailed?"Native-stage details are unavailable because reading them failed or returned invalid data. This is a read fault, not missing history. Diagnostics shows its error type."
            :"Native-stage details are unavailable for this request in this app run. They may predate startup, have been evicted or never recorded."
        :value.Stage switch
        {
            NativeLaunchDiagnosticStage.VerifyingExecutable=>"Checking the selected executable's publisher.",
            NativeLaunchDiagnosticStage.PreparingHandoff=>value.LaunchMode==NativeLaunchMode.DirectTicket
                ?"Preparing the selected account's launch request (direct ticket).":"Preparing the selected account's official-page handoff.",
            NativeLaunchDiagnosticStage.Prepared=>"Handoff prepared. Native startup and arrival are not established.",
            NativeLaunchDiagnosticStage.Dispatching=>"Final native dispatch stage. Review the recorded outcome below.",
            NativeLaunchDiagnosticStage.Stopped=>"Stop requested for this attempt. A native request already delivered cannot be recalled.",
            _=>"Native attempt finished. Its fixed outcomes are diagnostic evidence only."
        };
    public string Times=>Snapshot is { } value?$"Started {At(value.StartedUtc)} · updated {At(value.UpdatedUtc)} · revision {value.Revision.ToString(CultureInfo.InvariantCulture)}"
        :ReadFailed?"No native-stage timestamp could be read."
        :"No native-stage timestamp recorded. Queue history does not restore these transient details.";
    public string StageTimes=>Snapshot is { } value?"Publisher: "+Observed(value.ExecutableObservedUtc)+"\nPreparation: "+Observed(value.PreparationObservedUtc)
        +"\n"+RequestSource+": "+Observed(value.BrowserObservedUtc)+"\nDispatch: "+Observed(value.DispatchObservedUtc)+"\nCleanup: "+Observed(value.CleanupObservedUtc)
        :"Individual stage times unavailable.";
    // The stage that produced the launch request: the official page unless the snapshot recorded the direct ticket mode (R-api-7).
    private bool DirectTicket=>Snapshot?.LaunchMode==NativeLaunchMode.DirectTicket;
    private string RequestSource=>DirectTicket?"Direct ticket":"Official page";
    public string Trust=>Snapshot is {TrustReason:{ } reason} value?"Publisher: "+(value.TrustStatus switch
    {ClientExecutableTrustStatus.VerifiedRobloxPublisher or ClientExecutableTrustStatus.VerifiedAcceptedPublisher=>"verified · ",ClientExecutableTrustStatus.Rejected=>"rejected · ",ClientExecutableTrustStatus.Deferred=>"deferred · ",_=>"unknown · "})
        +TrustReason(reason,value.TrustStatus)
        :"Publisher: not reached or not recorded.";
    public string Preparation=>Snapshot?.PreparationStatus is { } status?"Preparation: "+(status switch
    {
        NativeHandoffPreparationStatus.Ready=>"handoff prepared; not proof of a launched account.",
        NativeHandoffPreparationStatus.Busy=>"another preparation was still owned.",
        NativeHandoffPreparationStatus.RequestChanged=>"the pinned request changed.",
        NativeHandoffPreparationStatus.SignInRequired=>"the selected saved sign-in needs review or renewal.",
        NativeHandoffPreparationStatus.IdentityMismatch=>"the saved sign-in did not verify as the expected player.",
        NativeHandoffPreparationStatus.AuthorizationChanged=>"the selected authorization changed.",
        NativeHandoffPreparationStatus.CaptureRejected=>"the launch request was rejected.",
        NativeHandoffPreparationStatus.CleanupFailed=>"cleanup could not be confirmed.",
        NativeHandoffPreparationStatus.Expired=>"the prepared handoff expired.",
        NativeHandoffPreparationStatus.Cancelled=>"the preparation was cancelled.",
        NativeHandoffPreparationStatus.StorageUnavailable=>"private browser storage was unavailable, so it was refused before the saved sign-in was used; the saved sign-in was kept.",
        NativeHandoffPreparationStatus.ProfileRemoved=>"the account was being removed from the library, so it was refused before the saved sign-in was used or changed.",
        _=>"the preparation failed."
    }):"Preparation: not reached or not recorded.";
    public string Browser
    {
        get
        {
            if(Snapshot?.BrowserOutcome is not { } outcome)return RequestSource+": not reached or not recorded.";
            string request=DirectTicket?"the ticket request":"the page request";
            return RequestSource+": "+outcome switch
            {
                RobloxHandoffProbeOutcome.CapturedAndPrevented=>"external navigation captured and cancelled. Later native dispatch is a separate stage.",
                RobloxHandoffProbeOutcome.TicketIssued=>"Roblox issued a launch ticket for the saved sign-in and Zenith built the launch request itself. Later native dispatch is a separate stage.",
                RobloxHandoffProbeOutcome.ContextRejected=>"page context was rejected.",
                RobloxHandoffProbeOutcome.OriginRejected=>"page origin was rejected.",
                RobloxHandoffProbeOutcome.ExternalSchemeBlocked=>"an unsupported external scheme was blocked.",
                RobloxHandoffProbeOutcome.MalformedHandoff=>"the handoff structure was rejected.",
                RobloxHandoffProbeOutcome.TimedOut=>request+" timed out.",
                RobloxHandoffProbeOutcome.Cancelled=>request+" was cancelled.",
                RobloxHandoffProbeOutcome.BrowserUnavailable=>"the private browser was unavailable.",
                RobloxHandoffProbeOutcome.SignInRequired=>"the saved sign-in needs review or renewal.",
                RobloxHandoffProbeOutcome.IdentityMismatch=>"the page did not verify as the expected player.",
                RobloxHandoffProbeOutcome.AuthorizationChanged=>"the selected authorization changed.",
                RobloxHandoffProbeOutcome.Busy=>"another "+(DirectTicket?"ticket":"page")+" request was still owned.",
                RobloxHandoffProbeOutcome.CleanupFailed=>"private browser cleanup could not be confirmed.",
                RobloxHandoffProbeOutcome.StorageUnavailable=>"private browser storage was unavailable.",
                RobloxHandoffProbeOutcome.RateLimited=>"Roblox asked Zenith to slow down (HTTP 429) before issuing a launch ticket; try again later.",
                RobloxHandoffProbeOutcome.Refused=>"Roblox refused the ticket request (HTTP 403) for the saved sign-in.",
                _=>request+" failed."
            };
        }
    }
    public string Shape=>Snapshot?.ShapeStatus is { } status?"Handoff shape: "+(status switch
    {
        NativeHandoffCaptureStatus.AcceptedSourceGrammar=>"accepted source grammar; compatibility remains unverified.",
        NativeHandoffCaptureStatus.ContextRejected=>"request context was rejected.",
        NativeHandoffCaptureStatus.OriginRejected=>"source origin was rejected.",
        NativeHandoffCaptureStatus.InvalidShape=>"invalid structure.",
        NativeHandoffCaptureStatus.UnsupportedVariant=>"unsupported variant.",
        NativeHandoffCaptureStatus.DestinationMismatch=>"reported target did not match the pinned request.",
        NativeHandoffCaptureStatus.StaleTimestamp=>"the source timestamp was stale.",
        _=>"this operation had already captured a handoff."
    })+(Snapshot.RejectionReason is { } reason?" Rejected rule: "+RejectionRule(reason):""):"Handoff shape: not reached or not recorded.";
    private static string RejectionRule(NativeHandoffRejectionReason reason)=>reason switch
    {
        NativeHandoffRejectionReason.ContextInvalid=>"preparation context.",
        NativeHandoffRejectionReason.PageMismatch=>"expected official page.",
        NativeHandoffRejectionReason.OriginMismatch=>"expected page origin.",
        NativeHandoffRejectionReason.InputLength=>"request length.",
        NativeHandoffRejectionReason.ProtocolPrefix=>"protocol prefix or version.",
        NativeHandoffRejectionReason.InputCharacters=>"request characters.",
        NativeHandoffRejectionReason.OuterFieldCount=>"outer field count.",
        NativeHandoffRejectionReason.OuterFieldFormat=>"outer field structure.",
        NativeHandoffRejectionReason.UnknownOuterField=>"unsupported outer field name.",
        NativeHandoffRejectionReason.DuplicateOuterField=>"duplicate outer field.",
        NativeHandoffRejectionReason.OuterEncoding=>"outer field encoding.",
        NativeHandoffRejectionReason.MissingOuterField=>"required outer fields.",
        NativeHandoffRejectionReason.LaunchMode=>"launch mode.",
        NativeHandoffRejectionReason.LaunchExperience=>"launch experience.",
        NativeHandoffRejectionReason.NonDefaultChannel=>"non-default channel.",
        NativeHandoffRejectionReason.TicketShape=>"ticket structure.",
        NativeHandoffRejectionReason.LocaleShape=>"locale structure.",
        NativeHandoffRejectionReason.TrackerShape=>"outer tracker structure.",
        NativeHandoffRejectionReason.TimestampShape=>"timestamp structure or range.",
        NativeHandoffRejectionReason.StaleTimestamp=>"timestamp window.",
        NativeHandoffRejectionReason.DestinationEndpoint=>"destination endpoint or characters.",
        NativeHandoffRejectionReason.DestinationFieldCount=>"destination field count.",
        NativeHandoffRejectionReason.DestinationFieldFormat=>"destination field structure.",
        NativeHandoffRejectionReason.UnknownDestinationField=>"unsupported destination field name.",
        NativeHandoffRejectionReason.DuplicateDestinationField=>"duplicate destination field.",
        NativeHandoffRejectionReason.DestinationEncoding=>"destination field encoding.",
        NativeHandoffRejectionReason.RequestVariant=>"destination request type.",
        NativeHandoffRejectionReason.NestedTracker=>"matching destination tracker.",
        NativeHandoffRejectionReason.PlaceShape=>"place structure.",
        NativeHandoffRejectionReason.AttemptShape=>"join-attempt structure.",
        NativeHandoffRejectionReason.MissingAttemptOrigin=>"required join-attempt origin.",
        NativeHandoffRejectionReason.PlaceMismatch=>"pinned place match.",
        NativeHandoffRejectionReason.PrivateTargetMismatch=>"pinned private target match.",
        NativeHandoffRejectionReason.PlayTogetherVariant=>"play-together setting.",
        NativeHandoffRejectionReason.GameTargetMismatch=>"pinned game target and origin.",
        NativeHandoffRejectionReason.JobTargetMismatch=>"pinned server target and origin.",
        NativeHandoffRejectionReason.AlreadyCaptured=>"single capture per operation.",
        NativeHandoffRejectionReason.UnsupportedReferrerField=>"unsupported referrer field.",
        NativeHandoffRejectionReason.UnsupportedLaunchDataField=>"unsupported launch-data field.",
        NativeHandoffRejectionReason.UnsupportedEventField=>"unsupported event field.",
        _=>"unavailable."
    };
    public string Dispatch=>Snapshot?.DispatchStatus is { } status?"Native dispatch: "+(status switch
    {
        NativeHandoffAttemptStatus.Busy=>"another dispatch was still owned.",
        NativeHandoffAttemptStatus.NotAuthorized=>"the final current-grant boundary denied authorization.",
        NativeHandoffAttemptStatus.RequestChanged=>"the pinned request changed before dispatch.",
        NativeHandoffAttemptStatus.Expired=>"the handoff expired before dispatch.",
        NativeHandoffAttemptStatus.AdmissionDenied=>"resource or provider admission denied the request.",
        NativeHandoffAttemptStatus.Cancelled=>"dispatch was cancelled.",
        NativeHandoffAttemptStatus.NotStarted=>"no start was reported by this dispatch.",
        NativeHandoffAttemptStatus.StartedUnverified=>"a start was reported; account and arrival remain unverified.",
        _=>"startup is uncertain. Review any possible client before retrying."
    }):"Native dispatch: not reached or not recorded. This does not prove no client started.";
    public string Cleanup=>Snapshot is { } value?"Cleanup: "+(value.CleanupStatus switch
    {
        NativeLaunchCleanupStatus.NotStarted=>"not started or not recorded.",
        NativeLaunchCleanupStatus.Pending=>"still owned; completion has not been recorded.",
        NativeLaunchCleanupStatus.Complete=>"owned cleanup completed. This does not verify an account or arrival.",
        _=>"could not be confirmed. Review cleanup before another trial."
    }):"Cleanup: unavailable.";
    public bool AccountVerified=>false;
    public bool ArrivalVerified=>false;
    public bool InputAuthorized=>false;
    private NativeLaunchDiagnosticPresentation(Guid? operationId,NativeLaunchDiagnosticSnapshot? snapshot,bool readFailed=false)
    {OperationId=operationId;Snapshot=snapshot;ReadFailed=readFailed;}
    public static NativeLaunchDiagnosticPresentation Unavailable(Guid? operationId=null)=>new(operationId,null);
    /// <summary>Fixed read-fault view for one operation. It never carries the snapshot or any exception text.</summary>
    internal static NativeLaunchDiagnosticPresentation Faulted(Guid operationId)=>operationId==Guid.Empty?Unavailable():new(operationId,null,true);
    /// <summary>
    /// A null snapshot is absent history (not yet recorded, from before startup, or evicted). A snapshot that is
    /// present but belongs to another operation or fails validation is a provider fault: its type is reported
    /// through <paramref name="failed"/> and the view says so instead of blaming history.
    /// </summary>
    public static NativeLaunchDiagnosticPresentation Project(Guid operationId,NativeLaunchDiagnosticSnapshot? snapshot,Action<Type>? failed=null)
    {
        if(operationId==Guid.Empty)return Unavailable();
        if(snapshot is null)return Unavailable(operationId);
        if(snapshot.OperationId!=operationId){failed?.Invoke(typeof(InvalidDataException));return Faulted(operationId);}
        try {snapshot=snapshot.Validated();}catch(ArgumentException error){failed?.Invoke(error.GetType());return Faulted(operationId);}
        return new(operationId,snapshot);
    }
    /// <summary>
    /// Reads one transient snapshot. A source exception, an invalid snapshot or a snapshot for another
    /// operation shows the fixed read-fault text, and its exception type is reported through
    /// <paramref name="failed"/> so a provider bug is not mistaken for eviction. The message and inner error
    /// never cross this boundary. The failure is reported even when the view is no longer current; only the
    /// stale view is withheld. Validation is left to <see cref="Project"/> so a snapshot is validated once
    /// per read and is classified exactly as Project classifies it.
    /// </summary>
    internal static NativeLaunchDiagnosticPresentation? ReadCurrent(INativeLaunchDiagnosticSource? source,Guid operationId,Func<bool> current,Action<Type>? failed=null)
    {
        if(!current())return null;NativeLaunchDiagnosticSnapshot? snapshot;bool faulted=false;
        try{snapshot=source?.GetDiagnostic(operationId);}
        catch(Exception error){snapshot=null;faulted=true;failed?.Invoke(error.GetType());}
        var value=faulted?Faulted(operationId):Project(operationId,snapshot,failed);return current()?value:null;
    }
    private static string At(DateTimeOffset at)=>at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'",CultureInfo.InvariantCulture);
    private static string Observed(DateTimeOffset? at)=>at is { } value?At(value):"not recorded";
    private static string TrustReason(ClientExecutableTrustReason reason,ClientExecutableTrustStatus? status)=>reason switch
    {
        // R-launch-17: a bootstrapper's own executable verified by the certificate the user accepted for that product.
        ClientExecutableTrustReason.Verified when status==ClientExecutableTrustStatus.VerifiedAcceptedPublisher=>"the bootstrapper's signature verified with cached Windows evidence and its publisher is the one you accepted; the bootstrapper applies its own client settings; no launch authority.",
        ClientExecutableTrustReason.Verified=>"exact Roblox publisher signature verified with cached Windows evidence; no launch authority.",
        ClientExecutableTrustReason.BootstrapperNotEvaluated=>"the Roblox launcher is not evaluated; select the player or a bootstrapper's own executable.",
        ClientExecutableTrustReason.PublisherNotAccepted=>"the bootstrapper's signature verified, but its publisher is not accepted for that product; nothing was started.",
        ClientExecutableTrustReason.AcceptedPublisherChanged=>"the bootstrapper's signature verified, but its signer is not the one you accepted for that product; nothing was started.",
        ClientExecutableTrustReason.InvalidCandidate=>"the selected native executable was invalid.",
        ClientExecutableTrustReason.UnsupportedPlatform=>"publisher verification is unavailable on this platform.",
        ClientExecutableTrustReason.UnsafePath=>"the selected path was unsafe or redirected.",
        ClientExecutableTrustReason.MissingFile=>"the selected executable was missing.",
        ClientExecutableTrustReason.AccessDenied=>"Windows denied access to the selected executable.",
        ClientExecutableTrustReason.FileTooLarge=>"the executable exceeded the verification size bound.",
        ClientExecutableTrustReason.FileChanged=>"the selected file changed during verification.",
        ClientExecutableTrustReason.Unsigned=>"an embedded executable signature was not established.",
        ClientExecutableTrustReason.InvalidSignature=>"Windows rejected the executable signature.",
        ClientExecutableTrustReason.RevocationUnavailable=>"cached certificate revocation evidence was unavailable; no online retrieval was attempted.",
        ClientExecutableTrustReason.UntrustedChain=>"the cached code-signing chain was not trusted.",
        ClientExecutableTrustReason.PublisherMismatch=>"the signer did not establish Roblox Corporation as publisher.",
        ClientExecutableTrustReason.MetadataUnavailable=>"the exact file or signer metadata was unavailable.",
        ClientExecutableTrustReason.Busy=>"another publisher check was still owned.",
        _=>"publisher verification was unavailable."
    };
}
