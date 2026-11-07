using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

public sealed partial class ClientPreviewsViewModel
{
    private DesktopSnapshot? _pendingInventory,_overviewInventory;
    private readonly object _inventoryGate=new();
    private ClientOverviewRow? _selectedOverview;
    private bool _isOverview;
    private long _presentationEpoch;
    private long _overviewLeaseUtcTicks;
    private string _inventoryStatus="Waiting for a process inventory.",_refreshSummary="Previews are off; all observed clients remain listed.";
    private readonly Dictionary<ClientPreviewTarget,PreparedImage> _images=[];
    public ObservableCollection<ClientOverviewRow> OverviewRows {get;}=[];
    public ClientOverviewRow? SelectedOverviewRow
    {get=>_selectedOverview;set {if(value is null || OverviewRows.Contains(value)) Set(ref _selectedOverview,value);}}
    public bool IsOverview {get=>_isOverview;private set=>Set(ref _isOverview,value);}
    public string InventoryStatus {get=>_inventoryStatus;private set=>Set(ref _inventoryStatus,value);}
    public string RefreshSummary {get=>_refreshSummary;private set=>Set(ref _refreshSummary,value);}

    /// <summary>One latest detached inventory, including unavailable rows; no capture is started here.</summary>
    public void ObserveInventory(DesktopSnapshot snapshot)
    {
        if(_stopping) return;
        ArgumentNullException.ThrowIfNull(snapshot);
        int count=Math.Min(snapshot.Sessions.Count,ClientPreviewLimits.MaximumOverviewTargets);
        var rows=new SessionObservation[count];for(int i=0;i<count;i++) rows[i]=snapshot.Sessions[i];
        var owned=snapshot with { Sessions=Array.AsReadOnly(rows),ProcessInventoryComplete=snapshot.ProcessInventoryComplete && snapshot.Sessions.Count<=count,
            Problem=snapshot.Sessions.Count>count ? "Process observation budget reached." : snapshot.Problem };
        lock(_inventoryGate)
        {
            if(_pendingInventory is { } pending && OlderInventory(owned,pending)
                || Volatile.Read(ref _overviewInventory) is { } shown && OlderInventory(owned,shown)) return;
            Interlocked.Exchange(ref _pendingInventory,owned);
        }
        Wake();
    }
    public void StartOverview(DateTimeOffset eligibilityExpiresUtc)
    {
        if(!IsAvailable || !_canUse()) return;
        ApplyOverviewInventory();
        if(!IsAvailable || !_canUse()) return; // Inventory notifications may reenter Lock/Dispose.
        if(!OverviewInventoryFresh(_clock.GetUtcNow()))
        {Fence("Overview previews need a fresh process inventory. Refresh observations and start again.");return;}
        long epoch=Interlocked.Increment(ref _presentationEpoch);
        Volatile.Write(ref _overviewLeaseUtcTicks,eligibilityExpiresUtc.UtcTicks);
        _coordinator.StartOverview(OverviewTargets(),eligibilityExpiresUtc);
        Volatile.Write(ref _targets,[]);Volatile.Write(ref _stopReason,null);_shown=null;
        IsOverview=true;if(epoch!=Volatile.Read(ref _presentationEpoch)) return;
        foreach(var row in Rows) {row.Clear("Selected previews are off.");if(epoch!=Volatile.Read(ref _presentationEpoch)) return;}
        var state=_coordinator.GetSnapshot();IsRunning=state.Running;
        if(epoch!=Volatile.Read(ref _presentationEpoch)) return;
        IsReleasing=state.Stopping;if(epoch!=Volatile.Read(ref _presentationEpoch)) return;
        Status=state.Stopping?"Releasing previous capture resources before starting the overview.":"Starting optional overview images. Account and game identity remain unverified.";
        Wake();
    }
    private bool OverviewInventoryFresh(DateTimeOffset now)=>_overviewInventory is { ProcessRevision:>0,ProcessObservedUtc:{ } at }
        && at!=default && at-now<=ClientPreviewLimits.MaximumFutureSkew && now-at<=RecentClientHistoryReducer.MaximumInventoryAge;
    private ClientPreviewTarget[] OverviewTargets()=>OverviewRows.Where(row=>row.CanCapture).Select(row=>row.CaptureTarget!).ToArray();
    private void ApplyOverviewInventory()
    {
        var incoming=Interlocked.Exchange(ref _pendingInventory,null);
        var now=_clock.GetUtcNow();
        if(incoming is not null)
        {
            // A delayed/cached sample cannot replace a newer process enumeration.
            if(_overviewInventory is { } prior && OlderInventory(incoming,prior))
                incoming=null;
        }
        if(incoming is not null)
        {
            Volatile.Write(ref _overviewInventory,incoming);
            bool fresh=OverviewInventoryFresh(now);
            bool complete=fresh && incoming.Problem is null && incoming.ProcessInventoryComplete;
            long epoch=Volatile.Read(ref _presentationEpoch);
            var identities=incoming.Sessions.Where(row=>row is not null && row.ProcessId>0).ToArray();
            var duplicatePids=identities.GroupBy(row=>row.ProcessId).Where(group=>group.Count()>1).Select(group=>group.Key).ToHashSet();
            var duplicateWindows=identities.Where(row=>row.Window!=0).GroupBy(row=>row.Window).Where(group=>group.Count()>1).Select(group=>group.Key).ToHashSet();
            complete=complete && identities.Length==incoming.Sessions.Count && duplicatePids.Count==0 && duplicateWindows.Count==0
                && identities.All(row=>ValidOverviewObservation(row,incoming,now));
            var seen=new HashSet<(int,long,nint)>();
            foreach(var observation in identities)
            {
                var identity=(observation.ProcessId,observation.ProcessStartUtcTicks,observation.Window);
                if(!seen.Add(identity)) continue;
                // An explicit new incarnation/window never inherits the old tile's pixels or selection.
                for(int i=OverviewRows.Count-1;i>=0;i--)
                    if(OverviewRows[i].ProcessId==observation.ProcessId && !OverviewRows[i].SameIdentity(observation))
                    {RemoveOverviewRow(i);if(epoch!=Volatile.Read(ref _presentationEpoch)) return;}
                var row=OverviewRows.FirstOrDefault(value=>value.SameIdentity(observation));
                var safe=duplicatePids.Contains(observation.ProcessId) || duplicateWindows.Contains(observation.Window)?observation with {Problem="Ambiguous process observations."}:observation;
                if(row is null)
                {
                    if(OverviewRows.Count==ClientPreviewLimits.MaximumOverviewTargets)
                    {RemoveOverviewRow(0);if(epoch!=Volatile.Read(ref _presentationEpoch)) return;}
                    row=new(safe);OverviewRows.Add(row);if(epoch!=Volatile.Read(ref _presentationEpoch)) return;
                }
                bool current=fresh && ValidOverviewObservation(observation,incoming,now);
                row.Observe(safe,current);if(epoch!=Volatile.Read(ref _presentationEpoch)) return;
            }
            for(int i=OverviewRows.Count-1;i>=0;i--)
            {
                var row=OverviewRows[i];
                if(seen.Contains((row.ProcessId,row.ProcessStartUtcTicks,row.Window))) continue;
                if(complete) RemoveOverviewRow(i);else row.MarkLastKnown();
                if(epoch!=Volatile.Read(ref _presentationEpoch)) return;
            }
            _inventoryStatus=complete?$"{OverviewRows.Count} observed clients · complete process scan. Account identity is unverified."
                :$"{OverviewRows.Count} observed or last-known clients · scan incomplete or unavailable; absence does not prove exit. Roster limit 128.";
            Raise(nameof(InventoryStatus));if(epoch!=Volatile.Read(ref _presentationEpoch)) return;
            var snapshot=_coordinator.GetSnapshot();
            if(IsOverview && (snapshot.Running || snapshot.Stopping)) _coordinator.UpdateOverviewTargets(OverviewTargets());
        }
        if(!OverviewInventoryFresh(now))
        {
            var state=_coordinator.GetSnapshot();
            if(IsOverview && (state.Running || state.Stopping)) Fence("Process inventory is stale or unavailable. Overview images stopped; start explicitly after a fresh scan.");
            foreach(var row in OverviewRows) row.Observe(row.Observation,false);
            if(_overviewInventory is not null) InventoryStatus=$"{OverviewRows.Count} last-known clients · process inventory is stale or unavailable. No exit is inferred.";
        }
    }
    private static bool OlderInventory(DesktopSnapshot candidate,DesktopSnapshot current)=>candidate.ProcessRevision<current.ProcessRevision
        || candidate.ProcessRevision==current.ProcessRevision && candidate.ProcessObservedUtc.GetValueOrDefault()<current.ProcessObservedUtc.GetValueOrDefault();
    private static bool ValidOverviewObservation(SessionObservation row,DesktopSnapshot inventory,DateTimeOffset now)=>
        new SessionProcessKey(null,row.ProcessId,row.ProcessStartUtcTicks).IsValid && row.Problem is null && row.ObservedUtc!=default
        && inventory.ProcessObservedUtc is { } completed && row.ObservedUtc<=completed
        && row.ObservedUtc-now<=ClientPreviewLimits.MaximumFutureSkew && now-row.ObservedUtc<=RecentClientHistoryReducer.MaximumInventoryAge
        && row.ProcessStartUtcTicks<=row.ObservedUtc.UtcTicks;
    private bool CanRetainOverviewPixels(ClientPreviewSnapshot snapshot)=>IsOverview && snapshot.Mode==ClientPreviewMode.Overview
        && Volatile.Read(ref _stopReason) is null && _canUse() && OverviewInventoryFresh(_clock.GetUtcNow())
        && Volatile.Read(ref _overviewLeaseUtcTicks)>_clock.GetUtcNow().UtcTicks;
    private void RemoveOverviewRow(int index)
    {
        var row=OverviewRows[index];row.Clear("Observation replaced or no longer reported in the complete scan.");
        if(ReferenceEquals(SelectedOverviewRow,row)) SelectedOverviewRow=null;
        OverviewRows.Remove(row);
    }
    private void ClearOverviewImages(string reason)
    {foreach(var row in OverviewRows) row.Clear(reason);}

    private readonly record struct FrameToken(long Generation,long Surface,DateTimeOffset Captured,int Width,int Height,int Stride);
    private sealed record PreparedImage(FrameToken Token,BitmapSource Image);
    private static FrameToken Token(ClientPreviewFrame frame)=>new(frame.Generation,frame.SurfaceTimestampTicks,frame.CapturedUtc,frame.Width,frame.Height,frame.Stride);
    private bool RetainFrame(ClientPreviewSnapshot snapshot,ClientPreviewFrame frame)
    {
        var now=_clock.GetUtcNow();var maximum=snapshot.Mode==ClientPreviewMode.Overview ? ClientPreviewLimits.MaximumOverviewFrameAge : ClientPreviewLimits.MaximumFrameAge;
        return frame.CapturedUtc!=default && frame.CapturedUtc-now<=ClientPreviewLimits.MaximumFutureSkew && now-frame.CapturedUtc<=maximum;
    }
}
