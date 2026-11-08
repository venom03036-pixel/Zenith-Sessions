using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private DateTimeOffset? _overviewLibraryLoadedUtc,_overviewQueueObservedUtc,_overviewForecastObservedUtc;
    private DesktopSnapshot? _overviewDesktop;
    private LaunchQueueSnapshot? _overviewQueue;
    private bool _overviewMonitorStopped,_overviewForecastsKnown;
    private int? _overviewMemorySlots;
    private bool _queuePresentationStopHeld;
    private long _queuePresentationStopEpoch;
    private bool EffectiveQueuePaused=>_queuePresentationStopHeld || _queuePaused;
    private OverviewWorkspaceData _overview=OverviewWorkspaceProjection.Create(new(),DateTimeOffset.UtcNow);
    private Command? _openOverviewQueue;
    public OverviewWorkspaceData Overview {get=>_overview;private set=>Set(ref _overview,value);}
    public Command OpenOverviewQueue=>_openOverviewQueue??=new(_=>{if(!IsLocked && !_shuttingDown) QueueExpanded=true;});
    private void RecordOverviewLibraryLoaded()
    {_overviewLibraryLoadedUtc=DateTimeOffset.UtcNow;RefreshOverview();}
    private void RecordOverviewQueue(LaunchQueueSnapshot snapshot)
    {if(!ReferenceEquals(_overviewQueue,snapshot)) _overviewQueueObservedUtc=DateTimeOffset.UtcNow;_overviewQueue=snapshot;RefreshOverview();}
    private void RecordOverviewSample(DesktopSnapshot snapshot,CapacityDecision capacity,LaunchReservationProjectionResult projection)
    {
        _overviewDesktop=snapshot;_overviewForecastObservedUtc=snapshot.Resources.ObservedUtc;
        _overviewForecastsKnown=projection.ForecastsKnown;_overviewMemorySlots=capacity.SamplesHealthy?capacity.MemoryOnlySlots:null;
        RefreshOverview();
    }
    private void MarkOverviewMonitorStopped()
    {_overviewMonitorStopped=true;RefreshOverview();}
    private void RefreshOverview()
    {
        if(_dispatcher is null) return;
        Overview=OverviewWorkspaceProjection.Create(new(_ready && !_libraryUnavailable,AccountCount,_overviewLibraryLoadedUtc,
            _overviewDesktop,_overviewMonitorStopped,QueueReady,_overviewQueue,_overviewQueueObservedUtc,EffectiveQueuePaused,IsLocked,
            Volatile.Read(ref _lastPressure).Held,_overviewForecastsKnown,_overviewMemorySlots,_overviewForecastObservedUtc),DateTimeOffset.UtcNow);
    }
    private void HoldQueuePresentation()
    {
        Interlocked.Increment(ref _queuePresentationStopEpoch);_queuePresentationStopHeld=true;
        RefreshOverview();Raise(nameof(QueuePauseLabel));
    }
    private async Task ToggleQueuePausePresentationAsync()
    {
        if(IsLocked || !QueueReady) return;
        bool paused=!EffectiveQueuePaused;long epoch=Interlocked.Read(ref _queuePresentationStopEpoch);
        bool accepted=true;
        if(paused) {await _launchQueue.SetPausedAsync(true,_lifetime.Token);_queuePaused=true;}
        else accepted=await _launchQueue.TryResumeAsync(_lifetime.Token);
        if(!paused)
        {
            if(accepted && epoch==Interlocked.Read(ref _queuePresentationStopEpoch) && !IsLocked && !_shuttingDown && QueueReady)
            {_queuePresentationStopHeld=false;_queuePaused=false;}
            else _queuePaused=true;
        }
        Raise(nameof(QueuePauseLabel));RefreshVisibleQueueStatus();RefreshOverview();
    }
    private void RefreshVisibleQueueStatus()
    {
        if(!QueueReady || _overviewQueue is not { } snapshot) return;
        // Capacity waits start by themselves once headroom returns, so they are not "blocked". The segment
        // appears only when non-zero: the footer is capped at 420 px and the usual line already uses ~372 px.
        // A Roblox-update hold also starts by itself (once no client runs), so it is counted apart, likewise only when non-zero.
        int held=snapshot.Operations.Count(LaunchDescriptions.AwaitsClientUpdate);
        int waiting=snapshot.Operations.Count(LaunchDescriptions.AwaitsCapacity)-held;
        QueueStatus=$"{(EffectiveQueuePaused?"Paused":"Active")} · {snapshot.Operations.Count(row=>row.State==LaunchState.Queued)} queued"
            +(waiting>0?$" · {waiting} waiting for capacity":"")+(held>0?$" · {held} held for a Roblox update":"")
            +$" · {snapshot.Operations.Count(row=>row.State is LaunchState.Preparing or LaunchState.Validating or LaunchState.Launching or LaunchState.Stabilizing)} preparing/dispatching"
            +$" · {snapshot.Operations.Count(row=>row.State==LaunchState.Blocked && !LaunchDescriptions.AwaitsCapacity(row))} blocked · {snapshot.Operations.Count(LaunchRow.IsReviewable)} need review";
    }
}
