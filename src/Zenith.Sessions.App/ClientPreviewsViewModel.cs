using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

public sealed class ClientPreviewRow(ClientPreviewTarget target) : Observable
{
    private BitmapSource? _image;
    private string _status="Waiting for a fresh window image.",_resolution="",_captured="";
    internal DateTimeOffset? ImageTime;
    public ClientPreviewTarget Target {get;}=target;
    public string Client=>$"Client PID {Target.Process.ProcessId}";
    public BitmapSource? Image {get=>_image;private set {if(Set(ref _image,value)) Raise(nameof(HasImage));}}
    public bool HasImage=>Image is not null;
    public string Status {get=>_status;private set=>Set(ref _status,value);}
    public string Resolution {get=>_resolution;private set=>Set(ref _resolution,value);}
    public string Captured {get=>_captured;private set=>Set(ref _captured,value);}
    internal void Clear(string status)
    {Image=null;ImageTime=null;Resolution="";Captured="";Status=status;}
    internal void Publish(BitmapSource image,ClientPreviewFrame frame,string status)
    {
        Image=image;ImageTime=frame.CapturedUtc;Status=status;
        Resolution=$"{frame.Width} × {frame.Height} preview · source {frame.SourceWidth} × {frame.SourceHeight}";
        Captured="Window image captured "+frame.CapturedUtc.ToLocalTime().ToString("T")+" · account unverified";
    }
}

/// <summary>One bounded presenter for the shared capture owner. Images are detached/frozen, never persisted.</summary>
public sealed partial class ClientPreviewsViewModel : Observable,IAsyncDisposable
{
    private readonly ClientPreviewCoordinator _coordinator;
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _canUse;
    private readonly Action<Exception> _failed;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _lifetime=new();
    private readonly SemaphoreSlim _wake=new(0,1);
    private readonly Task _presenter;
    private Task? _dispose;
    private volatile bool _stopping;
    private ClientPreviewSnapshot? _shown;
    private ClientPreviewTarget[] _targets=[];
    private string? _stopReason;
    private volatile bool _faulted;
    private bool _running,_releasing;
    private bool _moreImages;
    private string _status="Previews are off. Select up to four observed client windows, then start explicitly.";
    public ObservableCollection<ClientPreviewRow> Rows {get;}=[];
    public string Status {get=>_status;private set=>Set(ref _status,value);}
    public bool IsRunning {get=>_running;private set {if(Set(ref _running,value)) Raise(nameof(CanStop));}}
    public bool IsReleasing {get=>_releasing;private set {if(Set(ref _releasing,value)) Raise(nameof(CanStop));}}
    public bool HasTargets=>Rows.Count>0;
    public bool IsAvailable=>!_stopping&&!_faulted;
    public bool CanStop=>IsRunning||IsReleasing||Rows.Any(row=>row.HasImage)||OverviewRows.Any(row=>row.HasImage);
    public Task ActiveCapture=>_coordinator.ActiveOperation;
    public Task Completion=>_presenter;

    public ClientPreviewsViewModel(IClientPreviewProvider provider,Dispatcher dispatcher,Func<bool> canUse,
        Action<Exception>? failed=null,TimeProvider? clock=null)
    {
        _dispatcher=dispatcher;_canUse=canUse;_failed=failed??(_=>{});_clock=clock??TimeProvider.System;
        _coordinator=new(provider,_clock);
        _presenter=Task.Run(()=>PresentAsync(_lifetime.Token));
    }
    public void Start(IReadOnlyList<ClientPreviewTarget> targets,DateTimeOffset eligibilityExpiresUtc)
    {
        if(!IsAvailable||!_canUse()) return;
        long epoch=Interlocked.Increment(ref _presentationEpoch);
        _coordinator.Start(targets,eligibilityExpiresUtc);
        Volatile.Write(ref _targets,targets.ToArray());
        Volatile.Write(ref _stopReason,null);_shown=null;IsOverview=false;
        if(epoch!=Volatile.Read(ref _presentationEpoch)) return;
        ClearOverviewImages("Overview previews are off.");if(epoch!=Volatile.Read(ref _presentationEpoch)) return;
        Rows.Clear();if(epoch!=Volatile.Read(ref _presentationEpoch)) return;
        foreach(var target in targets) {Rows.Add(new(target));if(epoch!=Volatile.Read(ref _presentationEpoch)) return;}
        var state=_coordinator.GetSnapshot();
        IsRunning=state.Running;if(epoch!=Volatile.Read(ref _presentationEpoch)) return;
        IsReleasing=state.Stopping;Raise(nameof(HasTargets));if(epoch!=Volatile.Read(ref _presentationEpoch)) return;
        Status=state.Stopping?"Releasing previous capture resources before starting the pinned selection."
            :"Starting pinned previews. Window images do not verify a signed-in account or game arrival.";
        Wake();
    }
    public void Fence(string reason)
    {
        if(_stopping) return;
        Interlocked.Increment(ref _presentationEpoch);
        Volatile.Write(ref _stopReason,reason);
        // A replacement Start can be pending behind cleanup with Running=false. Fence that
        // pending request as well, before cleanup can admit another native capture.
        _coordinator.Stop();
        if(_dispatcher.CheckAccess())
        {
            foreach(var row in Rows) row.Clear("Preview stopped.");
            ClearOverviewImages("Preview stopped.");
            IsRunning=false;IsReleasing=_coordinator.GetSnapshot().Stopping;Status=reason;
        }
        Wake();
    }
    public void RenewResourceLease(DateTimeOffset expiresUtc)
    {
        if(IsOverview && Volatile.Read(ref _overviewLeaseUtcTicks)>_clock.GetUtcNow().UtcTicks)
            Volatile.Write(ref _overviewLeaseUtcTicks,expiresUtc.UtcTicks);
        _coordinator.RenewEligibility(expiresUtc);
    }
    public void ValidateObservedTargets(DesktopSnapshot observed)
    {
        var state=_coordinator.GetSnapshot();
        if(state.Mode==ClientPreviewMode.Overview) return; // Overview reconciles each exact tile independently.
        if(!state.Running&&!state.Stopping) return;
        var now=_clock.GetUtcNow();
        bool complete=observed.Problem is null && observed.ProcessInventoryComplete && observed.ProcessRevision>0
            && observed.ProcessObservedUtc is { } at && at-now<=ClientPreviewLimits.MaximumFutureSkew
            && now-at<=RecentClientHistoryReducer.MaximumInventoryAge;
        foreach(var target in Volatile.Read(ref _targets))
        {
            if(complete && !observed.Sessions.Any(row=>row.ProcessId==target.Process.ProcessId
                && row.ProcessStartUtcTicks==target.Process.ProcessStartUtcTicks && row.Window==target.Window && row.Problem is null))
            {Fence("A pinned preview target is absent from the fresh complete inventory. Select current clients and start again.");return;}
            foreach(var row in observed.Sessions)
                if(row.ProcessId==target.Process.ProcessId && row.Problem is null
                    && (row.ProcessStartUtcTicks!=target.Process.ProcessStartUtcTicks || row.Window!=target.Window))
                {Fence("A preview's process instance or window changed. Select the current clients and start again.");return;}
        }
    }
    private void Wake()
    {
        if(_stopping) return;
        // Signals only wake one shared presenter; there is no queue of frames or window-specific timer.
        try {if(_wake.CurrentCount==0) _wake.Release();}
        catch(SemaphoreFullException) { /* Concurrent context changes coalesce one already pending wake. */ }
        catch(ObjectDisposedException) when(_stopping) { /* Teardown has joined the presenter. */ }
    }
    private async Task PresentAsync(CancellationToken ct)
    {
        try
        {
            while(true)
            {
                ct.ThrowIfCancellationRequested();
                await _dispatcher.InvokeAsync(ApplyOverviewInventory,DispatcherPriority.Background,ct).Task.ConfigureAwait(false);
                var snapshot=_coordinator.GetSnapshot();
                if(!_canUse() && snapshot.Running)
                {Fence("Previews paused because this workspace is hidden, locked or under resource pressure. Start again when ready.");snapshot=_coordinator.GetSnapshot();}
                bool changed=!ReferenceEquals(snapshot,_shown) || _moreImages;
                var prepared=new Dictionary<ClientPreviewTarget,(ClientPreviewResult Result,BitmapSource? Image)>();
                var retained=new HashSet<ClientPreviewTarget>();
                int converted=0;bool moreImages=false;
                // While Core releases the previous sources for an overview roster change it keeps the
                // validated frames of unchanged identities: keep their frozen bitmaps, convert nothing new.
                bool convert=snapshot.Running;
                if(changed && (convert || OverviewRosterRelease(snapshot)) && _canUse())
                    foreach(var result in snapshot.Results)
                    {
                        ct.ThrowIfCancellationRequested();BitmapSource? image=null;
                        if(result.Status is ClientPreviewStatus.Ready or ClientPreviewStatus.Stale && result.Frame is { } frame && RetainFrame(snapshot,frame))
                        {
                            retained.Add(result.Target);var token=Token(frame);
                            if(_images.TryGetValue(result.Target,out var cached) && cached.Token==token) image=cached.Image;
                            else if(convert && converted<ClientPreviewLimits.MaximumTargets)
                            {
                                var bitmap=BitmapSource.Create(frame.Width,frame.Height,96,96,PixelFormats.Bgra32,null,frame.Pixels.ToArray(),frame.Stride);
                                bitmap.Freeze();image=bitmap;converted++;
                            }
                            else if(convert) moreImages=true;
                        }
                        prepared[result.Target]=(result,image);
                    }
                if(changed) foreach(var key in _images.Keys.ToArray()) if(!retained.Contains(key)) _images.Remove(key);
                _moreImages=moreImages;
                long epoch=Volatile.Read(ref _presentationEpoch);
                await _dispatcher.InvokeAsync(()=>
                {
                    if(_stopping) return;
                    if(ReferenceEquals(snapshot,_coordinator.GetSnapshot()))
                    {
                        if(changed)
                        {
                            IsRunning=snapshot.Running;IsReleasing=snapshot.Stopping;
                            if(epoch!=Volatile.Read(ref _presentationEpoch)) return;
                            Status=!snapshot.Running && Volatile.Read(ref _stopReason) is { } reason
                                ? reason+(snapshot.Stopping?" Releasing capture resources…":"") : snapshot.Explanation;
                            if(epoch!=Volatile.Read(ref _presentationEpoch)) return;
                            bool rosterRefresh=OverviewRosterRelease(snapshot) && CanRetainOverviewPixels(snapshot);
                            if(snapshot.Mode==ClientPreviewMode.Selected) foreach(var row in Rows)
                            {
                                prepared.TryGetValue(row.Target,out var value);
                                if(snapshot.Running && _canUse() && value.Image is { } pixels && value.Result.Frame is { } frame && Fresh(frame.CapturedUtc))
                                {row.Publish(pixels,frame,value.Result.Explanation);if(epoch==Volatile.Read(ref _presentationEpoch)) _images[row.Target]=new(Token(frame),pixels);}
                                else row.Clear(snapshot.Stopping?"Releasing capture resources…":value.Result?.Explanation??"Preview stopped or waiting for a fresh frame.");
                                if(epoch!=Volatile.Read(ref _presentationEpoch) || !ReferenceEquals(snapshot,_coordinator.GetSnapshot())) return;
                            }
                            else foreach(var row in OverviewRows)
                            {
                                var target=row.CaptureTarget;var value=target is not null && prepared.TryGetValue(target,out var found)?found:default;
                                if(snapshot.Running && _canUse() && row.CanCapture && value.Image is { } image && value.Result.Frame is { } frame && RetainFrame(snapshot,frame))
                                {row.Publish(image,frame,value.Result.Explanation,_clock.GetUtcNow(),value.Result.Status==ClientPreviewStatus.Ready);if(epoch==Volatile.Read(ref _presentationEpoch)) _images[target!]=new(Token(frame),image);}
                                // Roster replacement temporarily releases Core sources. An absent
                                // partial-scan tile keeps only its already accepted frozen image;
                                // it remains excluded from capture and focus throughout cleanup.
                                // An unchanged capturable tile keeps its image as last-known while
                                // Core still retains a frame for that exact identity.
                                else if(CanRetainOverviewPixels(snapshot) && row.HasImage && row.CanRetainLastKnown
                                    && value.Result?.Status is not (ClientPreviewStatus.InvalidClient or ClientPreviewStatus.Minimized or ClientPreviewStatus.Hidden or ClientPreviewStatus.Unsupported or ClientPreviewStatus.Cancelled)
                                    && (!row.CanCapture || (moreImages || rosterRefresh) && value.Result?.Frame is { } waiting && RetainFrame(snapshot,waiting)))
                                    row.RetainLastKnown(_clock.GetUtcNow());
                                else row.Clear(!row.CanCapture?row.UnavailableStatus():snapshot.Stopping?"Releasing capture resources…":value.Result?.Explanation??"No captured image is available.");
                                if(epoch!=Volatile.Read(ref _presentationEpoch) || !ReferenceEquals(snapshot,_coordinator.GetSnapshot())) return;
                            }
                            RefreshSummary=snapshot.Running?"One shared capture lane visits up to four targets per batch. Refresh intervals are measured per tile; no per-client FPS is promised."
                                :rosterRefresh?"Updating the observed tile roster. Capture resumes after the previous capture sources are released."
                                :"Previews are off; observed tiles remain listed. Start explicitly to resume.";
                            if(epoch!=Volatile.Read(ref _presentationEpoch)) return;
                            _shown=snapshot;
                        }
                        foreach(var row in Rows)
                            if(row.ImageTime is { } at && (!Fresh(at)||!_canUse())) row.Clear("Old image removed. Waiting for a fresh window image.");
                        foreach(var row in OverviewRows)
                        {if(!_canUse()) row.Clear("Overview previews are off.");else row.UpdateClock(_clock.GetUtcNow());if(epoch!=Volatile.Read(ref _presentationEpoch)) return;}
                    }
                },DispatcherPriority.Background,ct).Task.ConfigureAwait(false);
                var current=_coordinator.GetSnapshot();
                // Cleanup can finish between dispatch and this read. Paint that final state before
                // sleeping: completion alone has no UI wake signal and must not leave Releasing stuck.
                if(!ReferenceEquals(current,_shown) || _moreImages) continue;
                if(current.Running||current.Stopping) await Task.Delay(TimeSpan.FromMilliseconds(250),_clock,ct).ConfigureAwait(false);
                else await _wake.WaitAsync(ct).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
        catch(Exception error)
        {
            _faulted=true;_coordinator.Stop();_failed(error);
            await _dispatcher.InvokeAsync(()=>
            {foreach(var row in Rows) row.Clear("Preview unavailable.");ClearOverviewImages("Preview unavailable.");IsRunning=false;Raise(nameof(IsAvailable));Status="The preview presenter stopped after an error. Restart the manager before trying again.";},DispatcherPriority.Background).Task.ConfigureAwait(false);
        }
    }
    private bool Fresh(DateTimeOffset at)
    {var now=_clock.GetUtcNow();return at!=default && at-now<=ClientPreviewLimits.MaximumFutureSkew && now-at<=ClientPreviewLimits.MaximumFrameAge;}
    public ValueTask DisposeAsync()=>new(_dispose??=StopAsync());
    private async Task StopAsync()
    {
        _stopping=true;Interlocked.Increment(ref _presentationEpoch);_coordinator.Stop();_lifetime.Cancel();
        try
        {
            // InvokeAsync queues the notifications, so DisposeAsync has published its owned
            // task before a reentrant subscriber can request disposal again.
            await _dispatcher.InvokeAsync(()=>
            {foreach(var row in Rows) row.Clear("Previews disposed.");ClearOverviewImages("Previews disposed.");IsRunning=false;},DispatcherPriority.Background).Task.ConfigureAwait(false);
        }
        finally
        {
            try {await _presenter.ConfigureAwait(false);}
            finally
            {
                try {await _coordinator.DisposeAsync().ConfigureAwait(false);}
                finally {_images.Clear();_wake.Dispose();_lifetime.Dispose();}
            }
        }
    }
}
