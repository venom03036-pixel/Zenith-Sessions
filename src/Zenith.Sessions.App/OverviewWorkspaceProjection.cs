using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

/// <summary>Display-only source facts. These values never grant admission, identity or input authority.</summary>
public sealed record OverviewWorkspaceInput(bool LibraryReady=false,int SavedProfileCount=0,DateTimeOffset? LibraryLoadedUtc=null,
    DesktopSnapshot? Desktop=null,bool MonitorStopped=false,bool QueueReady=false,LaunchQueueSnapshot? Queue=null,
    DateTimeOffset? QueueObservedUtc=null,bool QueuePaused=true,bool Locked=false,bool PressureHeld=true,
    bool ForecastsKnown=false,int? MemoryOnlySlots=null,DateTimeOffset? ForecastObservedUtc=null);

public sealed record OverviewWorkspaceData(int? SavedProfiles,string LibraryCaption,int? ObservedProcesses,string ProcessCaption,
    int? QueuedRequests,string QueueCaption,string QueueSummary,string Physical,string Commit,string Cpu,
    string ResourceCaption,string ResourceSummary,string ForecastCaption,DateTimeOffset? LibraryLoadedUtc=null,
    DateTimeOffset? ProcessObservedUtc=null,DateTimeOffset? ResourceObservedUtc=null,DateTimeOffset? QueueObservedUtc=null,
    DateTimeOffset? ForecastObservedUtc=null);

/// <summary>One bounded, pure projection of existing facts; no queries, worker, timer or credential access.</summary>
public static class OverviewWorkspaceProjection
{
    public const int MaximumProcessRows=128;
    private static readonly TimeSpan ProcessAge=RecentClientHistoryReducer.MaximumInventoryAge,ResourceAge=CapacityPolicy.ResourceSampleExpiry;
    public static OverviewWorkspaceData Create(OverviewWorkspaceInput input,DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);
        bool library=input.LibraryReady && input.SavedProfileCount>=0 && input.LibraryLoadedUtc is { } loaded && ValidDate(loaded,now);
        string libraryCaption=library?"Last loaded "+Stamp(input.LibraryLoadedUtc!.Value)+" · entire saved library."
            :"Library total unavailable until the local library opens.";
        var process=Processes(input.Desktop,now);
        string processCaption=process.Caption+(input.MonitorStopped?" Automatic monitoring stopped.":"");
        var queue=Queue(input,now);
        var resource=input.Desktop?.Resources;
        bool timed=resource is not null && Fresh(resource.ObservedUtc,now,ResourceAge);
        bool memory=timed && resource!.IsValid;
        bool cpu=timed && resource!.Problem is null && resource.CpuPercent is { } percent && double.IsFinite(percent) && percent is >=0 and <=100;
        string resourceCaption=resource is null?"Waiting for an actual system measurement.":
            "Last measured "+DateLabel(resource.ObservedUtc,now)+(timed?".":" · last-known/unavailable.");
        if(input.MonitorStopped) resourceCaption="Automatic monitoring stopped. "+resourceCaption;
        string summary=input.MonitorStopped?"Manual refresh can show a measurement; it does not restart automatic monitoring.":
            !memory || !cpu?"Resource measurements are incomplete or unavailable. No launch readiness is implied.":
            input.PressureHeld?"Resource pressure holds new starts. Existing clients remain under user control.":
            !input.ForecastsKnown?"A retained request has an unknown memory estimate. Review the launch queue.":
            "Resource observations recorded. Account, provider and dispatch checks still apply.";
        string forecast=input.ForecastObservedUtc is { } forecastAt && ValidDate(forecastAt,now)
            ? (memory && cpu && !input.MonitorStopped?"Forecast from ":"Last-known forecast from ")+Stamp(forecastAt)+(input.ForecastsKnown && input.MemoryOnlySlots is >=0
                ? $" · memory-only estimate {input.MemoryOnlySlots} client budgets, not launch permission. Rechecked before dispatch."
                :" · estimate unavailable; review retained requests.") : "Waiting for a dated memory forecast.";
        return new(library?input.SavedProfileCount:null,libraryCaption,process.Count,processCaption,queue.Count,queue.Caption,queue.Summary,
            memory?$"{resource!.AvailablePhysical/(double)CapacityPolicy.GiB:0.0} / {resource.TotalPhysical/(double)CapacityPolicy.GiB:0.0} GiB free":"Unavailable",
            memory?$"{resource!.CommitUsed/(double)CapacityPolicy.GiB:0.0} / {resource.CommitLimit/(double)CapacityPolicy.GiB:0.0} GiB used":"Unavailable",
            cpu?$"{resource!.CpuPercent:0}%":"Unavailable",resourceCaption,summary,forecast,input.LibraryLoadedUtc,
            input.Desktop?.ProcessObservedUtc,resource?.ObservedUtc,input.QueueObservedUtc,input.ForecastObservedUtc);
    }
    private static (int? Count,string Caption) Processes(DesktopSnapshot? desktop,DateTimeOffset now)
    {
        if(desktop?.Sessions is not { } rows)
            return(null,"Process total unknown. Waiting for a bounded process inventory.");
        int count=rows.Count;
        if(count is <0 or >MaximumProcessRows) return(null,"Process total unknown. Waiting for a bounded process inventory.");
        string at=desktop.ProcessObservedUtc is { } observed?"Observed at "+DateLabel(observed,now):"Process observation time unavailable";
        bool complete=desktop.Problem is null && desktop.ProcessInventoryComplete && desktop.ProcessRevision>0
            && desktop.ProcessObservedUtc is { } completed && Fresh(completed,now,ProcessAge);
        var pids=new HashSet<int>();var windows=new HashSet<nint>();
        for(int i=0;i<count;i++)
        {
            var row=rows[i];
            if(row is null || !new SessionProcessKey(null,row.ProcessId,row.ProcessStartUtcTicks).IsValid || row.Problem is not null
                || desktop.ProcessObservedUtc is not { } finished || !Fresh(row.ObservedUtc,now,ProcessAge)
                || row.ObservedUtc>finished || row.ProcessStartUtcTicks>row.ObservedUtc.UtcTicks || !pids.Add(row.ProcessId)
                || row.Window!=0 && !windows.Add(row.Window)) complete=false;
        }
        return complete?(count,at+" · complete process scan. Account and arrival unverified."):
            (null,at+$" · {count} rows reported; total unknown. Partial, stale or unavailable scans do not prove absence.");
    }
    private static (int? Count,string Caption,string Summary) Queue(OverviewWorkspaceInput input,DateTimeOffset now)
    {
        if(!input.QueueReady || input.Queue?.Operations is not { } rows)
            return(null,"Queue unavailable or still opening.","Existing queue files and uncertain requests require review; no empty healthy queue is implied.");
        int count=rows.Count;
        if(count is <0 or >LaunchReservationProjection.MaximumOperations) return(null,"Queue summary unavailable.","The retained queue snapshot needs review.");
        int queued=0,starting=0,waiting=0,held=0,network=0,blocked=0,failed=0,review=0;
        var ids=new HashSet<Guid>();
        for(int i=0;i<count;i++)
        {
            var row=rows[i];
            if(row?.Intent is null || row.Intent.OperationId==Guid.Empty || !ids.Add(row.Intent.OperationId) || !Enum.IsDefined(row.State) || !Enum.IsDefined(row.Problem))
                return(null,"Queue summary unavailable.","The retained queue snapshot needs review.");
            if(row.State==LaunchState.Queued) queued++;
            if(row.State is LaunchState.Preparing or LaunchState.Validating or LaunchState.Launching or LaunchState.Stabilizing) starting++;
            // Capacity waits restart on their own (the runner's own predicate); other blocks need the user.
            if(LaunchDescriptions.AwaitsClientUpdate(row)) held++;
            else if(LaunchDescriptions.AwaitsPublicNetwork(row)) network++;
            else if(LaunchDescriptions.AwaitsCapacity(row)) waiting++;
            else if(row.State==LaunchState.Blocked) blocked++;
            if(row.State==LaunchState.Failed) failed++;
            if(LaunchRow.IsReviewable(row)) review++;
        }
        string mode=input.Locked?"Controls locked":input.QueuePaused?"Queue paused":"Queue active";
        string observed=input.QueueObservedUtc is { } at && ValidDate(at,now)?" · snapshot observed "+Stamp(at):" · observation time unavailable";
        return(queued,mode+observed,$"{starting} preparing/dispatching · {waiting} waiting for capacity (start automatically while the queue is active)"
            +(held>0?$" · {held} held for a possible Roblox update that could close running clients (start automatically once none runs)":"")
            +(network>0?$" · {network} held for a public network (start automatically once the network is private)":"")+$" · {blocked} blocked · {failed} failed · {review} reviewable retained requests. These are requests, not joined accounts.");
    }
    private static bool Fresh(DateTimeOffset at,DateTimeOffset now,TimeSpan age)=>ValidDate(at,now) && now-at<=age;
    private static bool ValidDate(DateTimeOffset at,DateTimeOffset now)=>at!=default && now!=default && at<=now;
    private static string DateLabel(DateTimeOffset at,DateTimeOffset now)=>ValidDate(at,now)?Stamp(at):"time unavailable";
    private static string Stamp(DateTimeOffset at)=>at.ToLocalTime().ToString("G");
}
