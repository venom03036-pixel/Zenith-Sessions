using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private bool _clientPreviewMode,_clientPreviewWindowVisible;
    private volatile bool _clientPreviewAllowed;
    private string _previewPage="";
    private CancellationTokenSource? _overviewFocusStop;
    private long _overviewFocusEpoch;
    public ClientPreviewsViewModel ClientPreviews {get;private set;}=null!;
    public Command StartClientPreviews {get;private set;}=null!;
    public Command StopClientPreviews {get;private set;}=null!;
    public Command ShowClientPreviewMode {get;private set;}=null!;
    public Command ShowClientTableMode {get;private set;}=null!;
    public Command ShowWindowsOverview {get;private set;}=null!;
    public Command StartWindowsOverview {get;private set;}=null!;
    public AsyncCommand FocusOverviewClient {get;private set;}=null!;
    public bool IsClientPreviewMode=>_clientPreviewMode;
    public bool IsClientTableMode=>!_clientPreviewMode&&!_clientHistoryMode;
    public bool CanStartClientPreviews=>_clientPreviewAllowed && IsSessions && _clientPreviewMode && ClientPreviews.IsAvailable && FreshPreviewResources();
    public bool CanStartWindowsOverview=>_clientPreviewAllowed && IsWindows && ClientPreviews.IsAvailable && FreshPreviewResources();
    public bool CanFocusOverviewClient=>_ready && !_shuttingDown && !IsLocked && !IsSample && IsWindows && _clientPreviewWindowVisible
        && ClientPreviews.SelectedOverviewRow is {CanFocus:true};

    private void InitializeClientPreviews(IClientPreviewProvider? provider)
    {
        ClientPreviews=new(provider??new WindowsClientPreviewProvider(),_dispatcher,()=>_clientPreviewAllowed,
            error=>_diagnostics.RecordFailure(ManagerSubsystem.Previews,error.GetType()));
        ClientPreviews.PropertyChanged+=(_,args)=>
        {if(args.PropertyName==nameof(ClientPreviewsViewModel.IsAvailable)) {Raise(nameof(CanStartClientPreviews));Raise(nameof(CanStartWindowsOverview));}
            if(args.PropertyName==nameof(ClientPreviewsViewModel.SelectedOverviewRow)) Raise(nameof(CanFocusOverviewClient));};
        ShowClientPreviewMode=new(_=>SetClientPreviewMode(true));
        ShowClientTableMode=new(_=>SetClientPreviewMode(false));
        StopClientPreviews=new(_=>ClientPreviews.Fence("Previews stopped. Start explicitly to resume."));
        StartClientPreviews=new(_=>StartPinnedClientPreviews());
        ShowWindowsOverview=new(_=>{if(!IsLocked && !_shuttingDown) Tab="Windows";});
        StartWindowsOverview=new(_=>
        {
            RefreshClientPreviewContext();
            if(!CanStartWindowsOverview) {ClientPreviews.Fence("Overview previews need a visible unlocked Windows page and fresh healthy resources. Sample mode never captures real windows.");return;}
            ClientPreviews.StartOverview(Volatile.Read(ref _latestDesktop)!.Resources.ObservedUtc.AddSeconds(3));
        });
        FocusOverviewClient=new(_=>FocusOverviewClientAsync(),error=>Fail(error,ManagerSubsystem.ClientWindows));
    }
    private bool FreshPreviewResources()
    {
        var snapshot=Volatile.Read(ref _latestDesktop);
        var now=DateTimeOffset.UtcNow;
        return snapshot is not null && snapshot.Resources.IsValid && snapshot.Resources.ObservedUtc!=default
            && snapshot.Resources.ObservedUtc-now<=ClientPreviewLimits.MaximumFutureSkew
            && now-snapshot.Resources.ObservedUtc<=TimeSpan.FromSeconds(3) && !_lastPressure.Held;
    }
    private void SetClientPreviewMode(bool previews)=>SetSessionPresentation(previews,false);
    private void SetSessionPresentation(bool previews,bool history)
    {
        if(_clientPreviewMode==previews && _clientHistoryMode==history) return;
        _clientPreviewMode=previews;_clientHistoryMode=history;
        Raise(nameof(IsClientPreviewMode));Raise(nameof(IsClientTableMode));Raise(nameof(IsClientHistoryMode));
        RefreshClientPreviewContext();
        RefreshTelemetryPresentation();
    }
    public void SetPreviewWindowVisible(bool visible)
    {
        _clientPreviewWindowVisible=visible;
        RefreshClientPreviewContext();
        RefreshPublicImageActivity();
    }
    private void RefreshClientPreviewContext()
    {
        bool wasAllowed=_clientPreviewAllowed;
        string page=IsWindows?"Windows":IsSessions && _clientPreviewMode?"Selected":"";
        bool changedPage=_previewPage!=page;_previewPage=page;
        _clientPreviewAllowed=_ready && !_shuttingDown && !IsLocked && !IsSample && (IsWindows || IsSessions && _clientPreviewMode)
            && _clientPreviewWindowVisible && FreshPreviewResources();
        if(wasAllowed && (!_clientPreviewAllowed || changedPage)) ClientPreviews?.Fence("Previews are off while hidden, locked, outside this page or waiting for healthy resources. Start again when ready.");
        if(!CanFocusOverviewClient) CancelOverviewFocus();
        Raise(nameof(CanStartClientPreviews));
        Raise(nameof(CanStartWindowsOverview));Raise(nameof(CanFocusOverviewClient));
    }
    private void ObserveClientPreviewContext(DesktopSnapshot snapshot)
    {
        ClientPreviews.ObserveInventory(snapshot);ClientPreviews.ValidateObservedTargets(snapshot);
        RefreshClientPreviewContext();
        if(_clientPreviewAllowed)
            ClientPreviews.RenewResourceLease(snapshot.Resources.ObservedUtc.AddSeconds(3));
    }
    private void CancelOverviewFocus()
    {
        Interlocked.Increment(ref _overviewFocusEpoch);
        try {Volatile.Read(ref _overviewFocusStop)?.Cancel();}
        catch(ObjectDisposedException) { /* The actual focus owner has already completed. */ }
    }
    private async Task FocusOverviewClientAsync()
    {
        if(!CanFocusOverviewClient || ClientPreviews.SelectedOverviewRow is not {CanFocus:true} row) return;
        // Pin the exact observed process/window. Later tile selection cannot redirect this request.
        var observation=row.Observation;long epoch=Interlocked.Increment(ref _overviewFocusEpoch);
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _overviewFocusStop=stop;
        try
        {
            await Task.Yield();stop.Token.ThrowIfCancellationRequested();
            if(!CanFocusOverviewClient || epoch!=Volatile.Read(ref _overviewFocusEpoch)) return;
            bool activated=await _monitor.FocusAsync(observation,stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            if(epoch==Volatile.Read(ref _overviewFocusEpoch) && CanFocusOverviewClient)
                Notice=activated?"Selected client window activated. Account identity remains unverified.":"Window changed or Windows denied activation. Use its taskbar button to bring it forward.";
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
        catch(Exception error)
        {
            if(epoch==Volatile.Read(ref _overviewFocusEpoch) && !stop.IsCancellationRequested) Fail(error,ManagerSubsystem.ClientWindows);
            else _diagnostics.RecordFailure(ManagerSubsystem.ClientWindows,error.GetType());
        }
        finally {if(ReferenceEquals(_overviewFocusStop,stop)) _overviewFocusStop=null;}
    }
    private void StartPinnedClientPreviews()
    {
        RefreshClientPreviewContext();
        if(!CanStartClientPreviews)
        {ClientPreviews.Fence("Previews need an unlocked, visible Sessions page and fresh healthy resources. Sample mode never captures real windows.");return;}
        SessionObservation[] selected;
        try {selected=PinClientWindowSelection();}
        catch(ArgumentException)
        {ClientPreviews.Fence("In Table view, check one to four clients, or select one row, then choose Previews and start.");return;}
        if(selected.Length>ClientPreviewLimits.MaximumTargets || selected.Any(row=>row.Window==0 || row.ProcessId<=0 || row.ProcessStartUtcTicks<=0 || row.Problem is not null))
        {ClientPreviews.Fence("Choose up to four current client windows with a valid process start time. No window was substituted.");return;}
        var resources=Volatile.Read(ref _latestDesktop)!.Resources;
        ClientPreviews.Start(selected.Select(row=>new ClientPreviewTarget(new(null,row.ProcessId,row.ProcessStartUtcTicks),row.Window)).ToArray(),resources.ObservedUtc.AddSeconds(3));
    }
}
