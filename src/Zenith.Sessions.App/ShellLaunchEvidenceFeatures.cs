using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

/// <summary>Immutable facts in a reusable presentation row, never a session/account mapping.</summary>
public sealed class LaunchEvidenceRow(LaunchOperation operation) : Observable
{
    public LaunchOperation Operation {get;private set;}=operation;
    public Guid Id=>Operation.Intent.OperationId;
    public string Label=>$"Expected player {Operation.Intent.UserId} · place {Operation.Intent.PlaceId} · {Operation.State}";
    public NativeTrialEvidenceView? View {get;private set;}
    public string ExpectedPlayer=>View?.ExpectedUserId?.ToString(CultureInfo.InvariantCulture)??"Unknown";
    public string RequestedPlace=>View?.RequestedPlaceId?.ToString(CultureInfo.InvariantCulture)??"Unknown";
    public string RequestedServer=>View?.RequestedJobId??"Any server · none pinned";
    public string ProcessSummary=>View is {ProcessReceipt:{IsValid:true} receipt} facts
        ?$"PID {receipt.ProcessId} · {Presence(facts.Presence)} · started {At(new DateTimeOffset(receipt.ProcessStartUtcTicks,TimeSpan.Zero))}"
        :"No native process-start receipt";
    public string ObservationTime=>View?.ProcessMeasurementUtc is { } at?"Process measured "+At(at):"No current process measurement";
    private string _launchToLog=DescribeLaunchToLog(operation.ProcessReceipt,null);
    /// <summary>Launch-to-log, shown apart from the process receipt: whether one client log names this launch's browser tracker.</summary>
    public string LaunchToLog=>_launchToLog;
    internal void UpdateLaunchToLog(LaunchLogLink? link)
    {
        string text=DescribeLaunchToLog(Operation.ProcessReceipt,link);
        if(string.Equals(text,_launchToLog,StringComparison.Ordinal))return;
        _launchToLog=text;Raise(nameof(LaunchToLog));
    }
    // The tracker number itself is never shown: it is corroboration, not something to act on.
    internal static string DescribeLaunchToLog(LaunchProcessReceipt? receipt,LaunchLogLink? link)=>receipt is not {IsValid:true,BrowserTrackerId:not null}
        ?"Client log: not matched · no browser tracker was recorded for this launch"
        :link?.Status switch
        {
            LaunchLogLinkStatus.Linked when link.Process is { } holder=>$"Client log: names this launch's browser tracker · held by PID {holder.ProcessId} · supporting evidence only, not account or arrival proof",
            LaunchLogLinkStatus.Linked=>"Client log: names this launch's browser tracker · no running process holds it now · supporting evidence only, not account or arrival proof",
            LaunchLogLinkStatus.NotLinked=>"Client log: none found yet that names this launch's browser tracker",
            LaunchLogLinkStatus.Ambiguous=>"Client log: not matched · more than one log or launch shares this browser tracker",
            LaunchLogLinkStatus.Unavailable=>"Client log: unavailable · client logs could not be read right now",
            _=>"Client log: not checked yet"
        };
    public string ReportedPlayer=>User(View?.ReportedUser);
    public string ReportedPlace=>View?.ReportedDestination is {PlaceId:{ } place} destination
        ?$"{place} · {Status(destination.PlaceStatus)} · {At(destination.ObservedUtc)}":"Unknown · current place not established";
    public string ReportedServer=>View?.ReportedDestination is {JobId:{ } job} destination
        ?$"{job:D} · {Status(destination.JobStatus)} · {At(destination.ObservedUtc)}":"Unknown · current server not established";
    public string LastKnown=>"Last-known player: "+User(View?.LastKnownUser)+". Last-known destination: "+
        (View?.LastKnownDestination is {PlaceId:{ } place,JobId:{ } job} prior?$"place {place} · {job:D} · {At(prior.ObservedUtc)} · unverified":"Unknown");
    public string Comparison=>View?.Outcome switch
    {
        NativeTrialEvidenceOutcome.ReportedMismatchUnverified=>"Reported values differ · unverified",
        NativeTrialEvidenceOutcome.ReportedMatchUnverified=>"Reported values match · still unverified",
        _=>"Account and arrival remain unverified"
    };
    public string Explanation=>View?.Explanation??"No evidence is available for this request.";
    // Each presentation tick re-measures the process, so the view record changes every second.
    // Raise only the texts whose source fields changed; each text lists the view fields it reads.
    public void Update(LaunchOperation value,NativeTrialEvidenceView view)
    {
        var before=View;bool operationChanged=Operation!=value,viewChanged=before!=view;
        if(!operationChanged&&!viewChanged)return;
        Operation=value;View=view;
        if(operationChanged)Raise(nameof(Label));
        if(!viewChanged)return;
        Raise(nameof(View));
        if(before?.ExpectedUserId!=view.ExpectedUserId)Raise(nameof(ExpectedPlayer));
        if(before?.RequestedPlaceId!=view.RequestedPlaceId)Raise(nameof(RequestedPlace));
        if(!string.Equals(before?.RequestedJobId,view.RequestedJobId,StringComparison.Ordinal))Raise(nameof(RequestedServer));
        if(before?.ProcessReceipt!=view.ProcessReceipt||before?.Presence!=view.Presence)Raise(nameof(ProcessSummary));
        if(before?.ProcessMeasurementUtc!=view.ProcessMeasurementUtc)Raise(nameof(ObservationTime));
        if(before?.ReportedUser!=view.ReportedUser)Raise(nameof(ReportedPlayer));
        if(before?.ReportedDestination!=view.ReportedDestination){Raise(nameof(ReportedPlace));Raise(nameof(ReportedServer));}
        if(before?.LastKnownUser!=view.LastKnownUser||before?.LastKnownDestination!=view.LastKnownDestination)Raise(nameof(LastKnown));
        if(before?.Outcome!=view.Outcome)Raise(nameof(Comparison));
        if(!string.Equals(before?.Explanation,view.Explanation,StringComparison.Ordinal))Raise(nameof(Explanation));
    }
    private static string User(NativeTrialReportedUser? value)=>value?.UserId is { } id?$"{id} · {Status(value.Status)} · {At(value.ObservedUtc)}":"Unknown · current player not established";
    private static string At(DateTimeOffset? value)=>value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture)??"not observed";
    private static string Status(NativeTrialReportedStatus value)=>value switch
    {
        NativeTrialReportedStatus.ReportedMatchUnverified=>"reported match · unverified",NativeTrialReportedStatus.ReportedMismatchUnverified=>"reported mismatch · unverified",
        NativeTrialReportedStatus.LastKnownOnly=>"last-known only",NativeTrialReportedStatus.Stale=>"expired",NativeTrialReportedStatus.NotRequested=>"not pinned · unverified",
        NativeTrialReportedStatus.Ambiguous=>"ambiguous",NativeTrialReportedStatus.Uncorrelated=>"not correlated",_=>"unknown"
    };
    private static string Presence(NativeTrialProcessPresence value)=>value switch
    {
        NativeTrialProcessPresence.Observed=>"exact process observed",NativeTrialProcessPresence.NotObserved=>"not currently observed · exit unconfirmed",
        NativeTrialProcessPresence.PidReused=>"PID belongs to a different process instance",NativeTrialProcessPresence.Stale=>"process evidence expired",
        NativeTrialProcessPresence.Ambiguous=>"ambiguous process evidence",NativeTrialProcessPresence.Incomplete=>"process inventory incomplete",
        NativeTrialProcessPresence.InvalidReceipt=>"invalid process receipt",_=>"process unavailable"
    };
}

public sealed partial class ShellViewModel
{
    private const int MaximumLaunchEvidenceRows=32;
    private LaunchEvidenceRow? _selectedLaunchEvidence;
    private Guid? _preferredEvidenceOperation;
    private CancellationTokenSource? _launchEvidenceExportStop;
    private string _launchPacingStatus="No startup pacing delay.";
    private long _launchEvidenceSelectionEpoch;
    private NativeLaunchDiagnosticPresentation _selectedNativeDiagnostics=NativeLaunchDiagnosticPresentation.Unavailable();
    private (Guid Id,Type Error)? _nativeDiagnosticFailure;
    public NativeLaunchDiagnosticPresentation SelectedNativeDiagnostics=>_selectedNativeDiagnostics;
    public ObservableCollection<LaunchEvidenceRow> LaunchEvidenceRows {get;}=[];
    public LaunchEvidenceRow? SelectedLaunchEvidence
    {
        get=>_selectedLaunchEvidence;
        set
        {
            if((value is not null&&!LaunchEvidenceRows.Contains(value))||ReferenceEquals(value,_selectedLaunchEvidence))return;
            _selectedLaunchEvidence=value;_preferredEvidenceOperation=value?.Id;long epoch=++_launchEvidenceSelectionEpoch;
            RefreshSelectedNativeDiagnostics();
            if(epoch!=_launchEvidenceSelectionEpoch)return;
            Raise(nameof(SelectedLaunchEvidence));Raise(nameof(HasLaunchEvidence));Raise(nameof(CanShowEvidenceProcess));
        }
    }
    public bool HasLaunchEvidence=>!IsLocked&&SelectedLaunchEvidence?.View is not null;
    public string LaunchPacingStatus {get=>_launchPacingStatus;private set=>Set(ref _launchPacingStatus,value);}
    public bool CanShowEvidenceProcess=>HasLaunchEvidence&&SelectedLaunchEvidence?.View?.Presence==NativeTrialProcessPresence.Observed;
    public Command ShowEvidenceProcess {get;private set;}=null!;
    public AsyncCommand ExportLaunchEvidence {get;private set;}=null!;
    private void RefreshSelectedNativeDiagnostics()=>RefreshSelectedNativeDiagnostics(_nativeLaunch as INativeLaunchDiagnosticSource);
    // Optional source injection keeps selected-publication races testable without native work.
    // It is a synchronous bounded metadata read; callers never transfer ownership of the source.
    internal void RefreshSelectedNativeDiagnostics(INativeLaunchDiagnosticSource? source)
    {
        var selected=SelectedLaunchEvidence;long selectionEpoch=_launchEvidenceSelectionEpoch,nativeEpoch=Interlocked.Read(ref _nativeTrialEpoch);
        if(IsLocked||_shuttingDown||selected is null||!LaunchEvidenceRows.Contains(selected)) {ClearSelectedNativeDiagnostics();return;}
        bool readFailed=false;
        var view=NativeLaunchDiagnosticPresentation.ReadCurrent(source,selected.Id,()=>!IsLocked&&!_shuttingDown
            &&selectionEpoch==_launchEvidenceSelectionEpoch&&nativeEpoch==Interlocked.Read(ref _nativeTrialEpoch)
            &&ReferenceEquals(selected,SelectedLaunchEvidence)&&LaunchEvidenceRows.Contains(selected),
            failed:error=>{readFailed=true;RecordNativeDiagnosticFailure(selected.Id,error);});
        if(view is not null&&!readFailed)_nativeDiagnosticFailure=null;
        if(view is not null&&_selectedNativeDiagnostics!=view){_selectedNativeDiagnostics=view;Raise(nameof(SelectedNativeDiagnostics));}
    }
    // This read runs on every telemetry sample, so a failing source records one typed row per
    // (operation, error type) until a read succeeds or the selection is cleared, not one per tick.
    private void RecordNativeDiagnosticFailure(Guid operationId,Type error)
    {
        if(_nativeDiagnosticFailure==(operationId,error))return;
        _nativeDiagnosticFailure=(operationId,error);_diagnostics.RecordFailure(ManagerSubsystem.Launch,error);
    }
    private void ClearSelectedNativeDiagnostics()
    {
        _nativeDiagnosticFailure=null;
        var view=NativeLaunchDiagnosticPresentation.Unavailable(IsLocked||_shuttingDown?null:SelectedLaunchEvidence?.Id);
        if(_selectedNativeDiagnostics==view)return;_selectedNativeDiagnostics=view;Raise(nameof(SelectedNativeDiagnostics));
    }
    private void InitializeLaunchEvidenceFeatures()
    {
        ShowEvidenceProcess=new(_=>
        {
            if(IsLocked||_shuttingDown)return;RefreshLaunchEvidence();
            if(!CanShowEvidenceProcess||SelectedLaunchEvidence?.Operation.ProcessReceipt is not { } receipt)return;
            var matches=Sessions.Where(row=>row.Observation.ProcessId==receipt.ProcessId&&row.Observation.ProcessStartUtcTicks==receipt.ProcessStartUtcTicks).Take(2).ToArray();
            if(matches.Length!=1)return;SelectedSession=matches[0];Tab="Sessions";
            Notice="Selected the exact observed process. Its account and arrival remain unverified.";
        });
        ExportLaunchEvidence=new(p=>p is string path?ExportLaunchEvidenceAsync(path):Task.CompletedTask,error=>Fail(error,ManagerSubsystem.Launch));
    }
    private void RefreshLaunchEvidence()=>RefreshLaunchEvidenceWith(null);
    // The sample tick passes the log snapshot it already took; other callers take their own.
    private void RefreshLaunchEvidenceWith(IReadOnlyList<SessionEvidenceSnapshot>? logs)
    {
        if(IsLocked||_shuttingDown)
        {LaunchEvidenceRows.Clear();SelectedLaunchEvidence=null;ClearSelectedNativeDiagnostics();LaunchPacingStatus="Controls locked or stopping. Monitoring does not grant launch permission.";Raise(nameof(HasLaunchEvidence));return;}
        var now=DateTimeOffset.UtcNow;var queue=Volatile.Read(ref _latestQueue);var desktop=Volatile.Read(ref _latestDesktop);
        logs??=_logProjection.EvidenceSnapshot(now);
        var candidates=queue.Operations.Where(row=>row.Intent.UserId>0&&(row.Intent.HasExplicitDestination||row.ProcessReceipt is not null))
            .OrderByDescending(row=>row.Intent.OperationId==_preferredEvidenceOperation).ThenByDescending(row=>row.UpdatedUtc).Take(MaximumLaunchEvidenceRows).ToArray();
        var candidateIds=new Guid[candidates.Length];for(int index=0;index<candidates.Length;index++)candidateIds[index]=candidates[index].Intent.OperationId;
        var ids=candidateIds.ToHashSet();
        for(int index=LaunchEvidenceRows.Count-1;index>=0;index--)if(!ids.Contains(LaunchEvidenceRows[index].Id))LaunchEvidenceRows.RemoveAt(index);
        var rows=LaunchEvidenceRows.ToDictionary(row=>row.Id);
        // One projection per tick: the queue and process inventory are read once, not once per row.
        var views=NativeTrialEvidence.ProjectMany(queue,candidateIds,desktop,logs,now);
        var links=_logCorrelator?.Snapshot.Launches;
        for(int index=0;index<candidates.Length;index++)
        {
            var operation=candidates[index];
            if(!rows.TryGetValue(operation.Intent.OperationId,out var row)){row=new(operation);LaunchEvidenceRows.Add(row);}
            row.Update(operation,views[index]);
            LaunchLogLink? link=null;
            if(links is not null)foreach(var candidate in links)if(candidate.OperationId==operation.Intent.OperationId){link=candidate;break;}
            row.UpdateLaunchToLog(link);
        }
        var selected=_preferredEvidenceOperation is { } id?LaunchEvidenceRows.FirstOrDefault(row=>row.Id==id):null;
        if(selected is not null)SelectedLaunchEvidence=selected;
        else if(SelectedLaunchEvidence is not null&&!LaunchEvidenceRows.Contains(SelectedLaunchEvidence))SelectedLaunchEvidence=null;
        else if(SelectedLaunchEvidence is null&&LaunchEvidenceRows.Count>0)SelectedLaunchEvidence=LaunchEvidenceRows[0];
        RefreshSelectedNativeDiagnostics();
        var pacing=_launchQueue.ReadPacing();
        LaunchPacingStatus=pacing.Ready?"No startup pacing delay. Fresh resources, provider eligibility and explicit permission are still required."
            :pacing.Reason==LaunchPacingReason.ClockUnavailable?"Launch timing is unavailable. Starts are held; preserve existing clients and restart the manager before retrying."
            :pacing.Reason==LaunchPacingReason.RefusalBackoff?$"Waiting {Math.Ceiling(pacing.Remaining.TotalSeconds):0} s before the next start because the last start ended before any client started. Each such start in a row doubles the wait, up to about 1 minute."
            :$"Waiting {Math.Ceiling(pacing.Remaining.TotalSeconds):0} s for "+(pacing.Reason==LaunchPacingReason.StartupStabilization?"startup stabilization":"launch spacing")+". Already started clients remain open.";
        Raise(nameof(HasLaunchEvidence));Raise(nameof(CanShowEvidenceProcess));
    }
    public string BuildNativeTrialEvidenceReport()
    {
        if(IsLocked||_shuttingDown)throw new InvalidOperationException("Unlock the dashboard before exporting launch evidence.");
        var requested=SelectedLaunchEvidence;long selectionEpoch=_launchEvidenceSelectionEpoch,nativeEpoch=Interlocked.Read(ref _nativeTrialEpoch);
        RefreshLaunchEvidence();
        if(IsLocked||_shuttingDown||selectionEpoch!=_launchEvidenceSelectionEpoch||nativeEpoch!=Interlocked.Read(ref _nativeTrialEpoch)
            ||!ReferenceEquals(requested,SelectedLaunchEvidence))throw new InvalidOperationException("Launch evidence selection changed. Review the request and retry explicitly.");
        if(SelectedLaunchEvidence is not {View:{ } view} selected)throw new InvalidOperationException("Select a launch request with evidence first.");
        var diagnostic=SelectedNativeDiagnostics.OperationId==selected.Id?SelectedNativeDiagnostics:NativeLaunchDiagnosticPresentation.Unavailable(selected.Id);
        var stages=diagnostic.Snapshot;
        var report=new
        {
            FormatVersion=2,Product="Zenith Sessions Personal",CreatedUtc=DateTimeOffset.UtcNow,
            Version=typeof(ShellViewModel).Assembly.GetName().Version?.ToString(),Scope="Unverified launch diagnostics; expected values never replace observations.",
            ProviderCapability=CapabilityState.Unverified,selected.Operation.Intent.OperationId,selected.Operation.State,selected.Operation.Problem,
            Evidence=view,Pacing=_launchQueue.ReadPacing(),AccountVerified=false,ArrivalVerified=false,InputAuthorized=false,
            NativeDiagnostics=new
            {
                diagnostic.OperationId,diagnostic.Available,Revision=stages?.Revision,StartedUtc=stages?.StartedUtc,UpdatedUtc=stages?.UpdatedUtc,
                Stage=stages?.Stage,CleanupStatus=stages?.CleanupStatus,Stopped=stages?.Stopped,TrustStatus=stages?.TrustStatus,TrustReason=stages?.TrustReason,
                PreparationStatus=stages?.PreparationStatus,BrowserOutcome=stages?.BrowserOutcome,ShapeStatus=stages?.ShapeStatus,DispatchStatus=stages?.DispatchStatus,
                ExecutableObservedUtc=stages?.ExecutableObservedUtc,PreparationObservedUtc=stages?.PreparationObservedUtc,BrowserObservedUtc=stages?.BrowserObservedUtc,
                DispatchObservedUtc=stages?.DispatchObservedUtc,CleanupObservedUtc=stages?.CleanupObservedUtc,
                AccountVerified=false,ArrivalVerified=false,InputAuthorized=false
            }
        };
        return JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true,Converters={new JsonStringEnumConverter()}});
    }
    private async Task ExportLaunchEvidenceAsync(string path)
    {
        if(IsLocked||_shuttingDown||!HasLaunchEvidence)return;
        long epoch=Interlocked.Read(ref _nativeTrialEpoch);string report;
        try{report=BuildNativeTrialEvidenceReport();}
        catch(Exception error)when(epoch!=Interlocked.Read(ref _nativeTrialEpoch)||IsLocked||_shuttingDown)
        {_diagnostics.RecordFailure(ManagerSubsystem.Launch,error.GetType());return;}
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);_launchEvidenceExportStop=stop;
        try
        {
            string dataRoot=DataRoot,applicationRoot=AppContext.BaseDirectory;
            await Task.Run(()=>
            {
                byte[] bytes=Encoding.UTF8.GetBytes(report);if(bytes.Length>65536)throw new InvalidDataException("The launch evidence report exceeds its size limit.");
                return ExplicitReportFile.WriteReportAsync(path,bytes,dataRoot,applicationRoot,stop.Token);
            },stop.Token);
            if(epoch==Interlocked.Read(ref _nativeTrialEpoch)&&!IsLocked&&!_shuttingDown)Notice="Launch evidence saved. It contains player/place/server IDs and timestamps; no credentials, raw logs or executable paths.";
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        catch(Exception error)when(stop.IsCancellationRequested||epoch!=Interlocked.Read(ref _nativeTrialEpoch)||IsLocked||_shuttingDown)
        {
            // A late disk/path failure remains observable without replacing a newer stop/lock
            // message. The actual write task has completed; no worker or temp file is abandoned.
            _diagnostics.RecordFailure(ManagerSubsystem.Launch,FailureType(error));
        }
        finally{if(ReferenceEquals(_launchEvidenceExportStop,stop))_launchEvidenceExportStop=null;}
    }
}
