using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

/// <summary>Shared private-browser owner. Inspection and candidate preparation both cancel every external request.</summary>
public sealed class OwnedRobloxHandoffProbe(string dataRoot, Dispatcher dispatcher) : IRobloxHandoffProbe
{
    private static readonly TimeSpan InspectionDeadline=TimeSpan.FromMinutes(3);
    // How long a cleanup waits for its browser's exit, well past the five-second observation. Until then other owners hear
    // that the browser is still closing; past it the folder is kept and marked, and they ask for a restart. Disposal joins
    // it, for at most ShutdownJoinLimit.
    internal static readonly TimeSpan BrowserExitLimit=TimeSpan.FromSeconds(30);
    // How long disposal (Exit) waits for an inspection still stopping and its tracked cleanup, together (Z1559, owner
    // decision 2026-11-08). WebView2 initialization has no cancellation contract and a browser may never report its exit;
    // neither may hold Exit open. Past it the folder's lease is released and the marked folder is left for the startup sweep.
    internal static readonly TimeSpan ShutdownJoinLimit=TimeSpan.FromSeconds(10);
    private readonly TimeProvider _time=TimeProvider.System;
    private readonly object _state=new();
    private readonly CancellationTokenSource _lifetime=new();
    private TaskCompletionSource? _owner;
    private Task _cleanup=Task.CompletedTask;
    // The running inspection's folder, from its admission until the inspection returns, so a disposal that stops waiting
    // for an inspection that never returns can still release the folder's lease.
    private SignInProfileDirectory? _admitted;
    private SignInProfileDirectory? _retainedDirectory;
    private bool _blocked,_disposed;
    private Task? _dispose;

    /// <summary>Tests fire the unchanged 3-minute deadline through <paramref name="time"/> instead of waiting for it.</summary>
    internal OwnedRobloxHandoffProbe(string dataRoot,Dispatcher dispatcher,TimeProvider time):this(dataRoot,dispatcher)=>_time=time;

    /// <summary>
    /// What <see cref="InspectAsync"/> would answer without opening anything: CleanupFailed once latched, while
    /// leftover sign-in data kept by the startup sweep or any admission waits for review, or while another owner keeps
    /// its folder after a failed cleanup; StorageUnavailable while the data folder is remote or the last admission or
    /// sweep failed on storage itself (see <see cref="SignInProfileDirectory.StorageUnavailable"/>); Busy while this or
    /// another owner holds a private folder it may yet release; Cancelled after disposal. Memory only.
    /// </summary>
    public RobloxHandoffProbeOutcome? Refusal
    {
        get
        {
            lock(_state)
            {
                if(_disposed)return RobloxHandoffProbeOutcome.Cancelled;
                if(_blocked)return RobloxHandoffProbeOutcome.CleanupFailed;
                if(_owner is not null)return RobloxHandoffProbeOutcome.Busy;
            }
            // That leftover survives restarts and stops every owner's admission, including a fresh probe's first one.
            // A folder kept after another owner's failed cleanup stops every admission until Zenith closes: retrying cannot help.
            if(SignInProfileDirectory.LeftoverNeedsReview(dataRoot)||SignInProfileDirectory.HasFolderKeptAfterFailedCleanup(dataRoot))
                return RobloxHandoffProbeOutcome.CleanupFailed;
            // The next admission would most likely fail on the same storage after its grant was reserved, costing that
            // account its saved sign-in. Not a cleanup failure, so native launch is refused without stopping.
            if(SignInProfileDirectory.StorageUnavailable(dataRoot))return RobloxHandoffProbeOutcome.StorageUnavailable;
            return SignInProfileDirectory.HasLiveOwner(dataRoot)?RobloxHandoffProbeOutcome.Busy:null;
        }
    }

    public async Task<RobloxHandoffProbeCapture> InspectAsync(RobloxHandoffProbeContext context, RobloxSessionCredential credential,CancellationToken ct)
    {
        using var captured=await InspectCoreAsync(context,credential,ct,null).ConfigureAwait(false);
        return new(captured.Diagnostic??new(captured.Outcome),captured.TakeCredential());
    }
    internal async Task<NativeBrowserCapture> CaptureNativeAsync(NativeHandoffContext context,RobloxSessionCredential credential,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);context.Validated();
        using var captured=await InspectCoreAsync(new(context.ProfileId,context.ExpectedUserId,
            context.PendingAuthorizationGeneration,context.Destination.PlaceId),credential,ct,context).ConfigureAwait(false);
        return new(captured.Outcome,captured.TakeCandidate(),captured.TakeCredential(),captured.ShapeStatus);
    }
    private Task<OwnedCapture> InspectCoreAsync(RobloxHandoffProbeContext context, RobloxSessionCredential credential,CancellationToken ct,NativeHandoffContext? native)
    {
        ArgumentNullException.ThrowIfNull(credential);ct.ThrowIfCancellationRequested();
        if(context is null)return Task.FromResult(Capture(RobloxHandoffProbeOutcome.ContextRejected));
        try{context.Validated();}catch(ArgumentException){return Task.FromResult(Capture(RobloxHandoffProbeOutcome.ContextRejected));}
        TaskCompletionSource owner;CancellationTokenSource deadline,stop;
        lock(_state)
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            if(_owner is not null)return Task.FromResult(Capture(RobloxHandoffProbeOutcome.Busy));
            if(_blocked)return Task.FromResult(Capture(RobloxHandoffProbeOutcome.CleanupFailed));
            owner=new(TaskCreationOptions.RunContinuationsAsynchronously);_owner=owner;
            deadline=new(InspectionDeadline,_time);
            stop=CancellationTokenSource.CreateLinkedTokenSource(ct,_lifetime.Token,deadline.Token);
        }
        return RunOwnedAsync(context,credential,deadline,stop,owner,ct,native);
    }

    private async Task<OwnedCapture> RunOwnedAsync(RobloxHandoffProbeContext context,RobloxSessionCredential credential,
        CancellationTokenSource deadline,CancellationTokenSource stop,TaskCompletionSource owner,CancellationToken caller,NativeHandoffContext? native)
    {
        SignInProfileDirectory? directory=null;bool browserStarted=false;OwnedCapture? result=null;
        try
        {
            try
            {
                directory=await Task.Run(()=>
                {
                    var admitted=SignInProfileDirectory.Create(dataRoot,context.ProfileId,stop.Token);
                    lock(_state)_admitted=admitted; // Recorded with the lease it holds, before anything can wait on this inspection.
                    return admitted;
                },stop.Token).ConfigureAwait(false);
            }
            // Leftover data kept for review persists until someone reviews it. Latch so later checks are refused
            // before any account's grant is reserved, instead of blaming WebView2 each time. Any other storage failure
            // (remote, redirected, unwritable or full storage, or a file another program has open) is not this probe's
            // cleanup: it falls through to BrowserUnavailable below and latches nothing here. Admission remembers it, so
            // Refusal answers StorageUnavailable before the next grant is reserved while native launch stays usable.
            catch(SignInDirectoryNeedsReviewException)
            {lock(_state)_blocked=true;result=Capture(RobloxHandoffProbeOutcome.CleanupFailed);return result;}
            result=await dispatcher.InvokeAsync(()=>
            {
                stop.Token.ThrowIfCancellationRequested();
                return InspectBrowserAsync(context,credential,directory,stop.Token,()=>browserStarted=true,native);
            },DispatcherPriority.Normal,CancellationToken.None).Task.Unwrap().ConfigureAwait(false);
        }
        catch(OperationCanceledException){result=Capture(RobloxHandoffProbeOutcome.Cancelled);}
        // Another owner's folder (for example a sign-in browser still closing) is busy, not a missing
        // runtime. This attempt created and cleaned nothing, so it is not this probe's cleanup failure and latches
        // nothing. A folder that owner kept after its cleanup failed stays held until Zenith closes, so it is
        // CleanupFailed for now; Refusal says the same until that owner releases it.
        catch(SignInDirectoryInUseException ex)
        {result=Capture(ex.KeptAfterFailedCleanup?RobloxHandoffProbeOutcome.CleanupFailed:RobloxHandoffProbeOutcome.Busy);}
        catch(Exception){result=Capture(RobloxHandoffProbeOutcome.BrowserUnavailable);}
        finally
        {
            if(directory is not null&&!browserStarted)
            {
                try{using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));await Task.Run(()=>directory.Remove(cleanup.Token)).ConfigureAwait(false);}
                catch(Exception)
                {
                    // Kept with its lease until disposal, so other owners report a restart instead of a busy browser.
                    directory.KeepAfterFailedCleanup();
                    lock(_state){_blocked=true;_retainedDirectory=directory;}result?.Dispose();result=Capture(RobloxHandoffProbeOutcome.CleanupFailed);
                }
            }
            if(result?.Outcome==RobloxHandoffProbeOutcome.Cancelled&&stop.IsCancellationRequested&&!caller.IsCancellationRequested&&!_lifetime.IsCancellationRequested)
            {result.Dispose();result=Capture(RobloxHandoffProbeOutcome.TimedOut);}
            // The folder is removed, or retained above or by the tracked cleanup, so it is no longer the running inspection's.
            stop.Dispose();deadline.Dispose();lock(_state){if(ReferenceEquals(_owner,owner))_owner=null;_admitted=null;}owner.TrySetResult();
        }
        return result??Capture(RobloxHandoffProbeOutcome.Failed);
    }

    private async Task<OwnedCapture> InspectBrowserAsync(RobloxHandoffProbeContext context,RobloxSessionCredential credential,
        SignInProfileDirectory directory,CancellationToken ct,Action ownCleanup,NativeHandoffContext? native)
    {
        var browser=new WebView2();
        Window? allocatedWindow=null;bool bodyOwnsCleanup=false,closing=false;
        try
        {
        var status=new TextBlock{Text="Opening the selected account's private game page…",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0)};
        var cancel=new Button{Content="Cancel inspection",MinHeight=36,Padding=new Thickness(16,4,16,4),HorizontalAlignment=HorizontalAlignment.Right};
        var footer=new StackPanel{Margin=new Thickness(16)};footer.Children.Add(cancel);footer.Children.Add(status);
        var heading=new TextBlock{Text=$"Expected player ID {context.ExpectedUserId} · place {context.PlaceId} · preparation test only",
            Margin=new Thickness(16,12,16,4),TextWrapping=TextWrapping.Wrap};
        var explanation=new TextBlock{Text=native is null
            ?"Choose Play on Roblox's own page. Zenith cancels every external launch and keeps only structural facts. No Roblox client starts. An incomplete check may require signing in again."
            :"This private page prepares the selected account and destination. Choose Play if requested. Every external launch is cancelled; only a short-lived request is kept in memory for separate verification. This window starts no client. Cancelling may require signing in again.",
            Margin=new Thickness(16,0,16,12),TextWrapping=TextWrapping.Wrap};
        var header=new StackPanel();header.Children.Add(heading);header.Children.Add(explanation);
        var content=new DockPanel();DockPanel.SetDock(header,Dock.Top);DockPanel.SetDock(footer,Dock.Bottom);
        content.Children.Add(header);content.Children.Add(footer);content.Children.Add(browser);
        var window=new Window{Title="Native launch preparation — Zenith Sessions",Width=760,Height=820,MinWidth=480,MinHeight=500,
            WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=content};
        allocatedWindow=window;
        window.SetResourceReference(Window.BackgroundProperty,"CardBackground");window.SetResourceReference(Window.ForegroundProperty,"TextPrimary");
        status.SetResourceReference(TextBlock.ForegroundProperty,"TextSecondary");explanation.SetResourceReference(TextBlock.ForegroundProperty,"TextSecondary");
        if(Application.Current?.MainWindow is{IsVisible:true} main)window.Owner=main;
        var completion=new TaskCompletionSource<OwnedCapture>(TaskCreationOptions.RunContinuationsAsynchronously);
        var browserExited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CoreWebView2Environment? environment=null;CoreWebView2? core=null;Task? captureWork=null;
        bool controllerReady=false,initializationStarted=false,externalInstalled=false;
        OwnedCapture? accepted=null;
        EventHandler<CoreWebView2BrowserProcessExitedEventArgs> exited=(_,_)=>browserExited.TrySetResult();
        void HidePending(){if(!closing){window.IsEnabled=false;window.Hide();}}
        void CancelPending(){completion.TrySetCanceled(ct.IsCancellationRequested?ct:new CancellationToken(true));HidePending();}
        CancelEventHandler close=(_,e)=>{if(!closing){e.Cancel=true;CancelPending();}};
        RoutedEventHandler cancelClick=(_,_)=>CancelPending();window.Closing+=close;cancel.Click+=cancelClick;
        using var registration=ct.Register(()=>
        {
            completion.TrySetCanceled(ct);
            if(!dispatcher.HasShutdownStarted)dispatcher.BeginInvoke(HidePending,DispatcherPriority.Send);
        });
        bodyOwnsCleanup=true;ownCleanup();
        try
        {
            ct.ThrowIfCancellationRequested();window.Show();
            if(Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(key=>key.StartsWith("WEBVIEW2_",StringComparison.OrdinalIgnoreCase)))
                throw new ProbeFailure(RobloxHandoffProbeOutcome.BrowserUnavailable);
            // Initialization has no cancellation contract. Join its actual completion before disposing it.
            environment=await CoreWebView2Environment.CreateAsync(null,directory.Path,new CoreWebView2EnvironmentOptions{AreBrowserExtensionsEnabled=false});
            environment.BrowserProcessExited+=exited;
            if(!string.Equals(Path.GetFullPath(environment.UserDataFolder),Path.GetFullPath(directory.Path),StringComparison.OrdinalIgnoreCase))
                throw new ProbeFailure(RobloxHandoffProbeOutcome.BrowserUnavailable);
            ct.ThrowIfCancellationRequested();var options=environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled=true;options.ProfileName=native is null?"ZenithHandoffInspection":"ZenithNativePreparation";
            initializationStarted=true;await browser.EnsureCoreWebView2Async(environment,options);controllerReady=true;ct.ThrowIfCancellationRequested();core=browser.CoreWebView2;
            // If this API cannot be installed, do not seed a session or navigate to any page.
            core.LaunchingExternalUriScheme+=ExternalScheme;externalInstalled=true;
            core.NavigationStarting+=NavigationStarting;core.FrameNavigationStarting+=FrameNavigationStarting;
            core.NewWindowRequested+=NewWindowRequested;core.DownloadStarting+=DownloadStarting;
            core.PermissionRequested+=PermissionRequested;core.ProcessFailed+=ProcessFailed;
            core.Settings.IsPasswordAutosaveEnabled=false;core.Settings.IsGeneralAutofillEnabled=false;
            core.Settings.AreDevToolsEnabled=false;core.Settings.AreDefaultContextMenusEnabled=false;
            core.Settings.IsStatusBarEnabled=true;
            var cookie=core.CookieManager.CreateCookie(".ROBLOSECURITY",credential.CopyValue(),".roblox.com","/");
            cookie.IsSecure=true;cookie.IsHttpOnly=true;cookie.SameSite=CoreWebView2CookieSameSiteKind.Lax;
            if(credential.ExpiresUtc is { } expiry&&expiry!=DateTimeOffset.MaxValue)
            {
                if(expiry<=DateTimeOffset.UtcNow)throw new ProbeFailure(RobloxHandoffProbeOutcome.SignInRequired);
                cookie.Expires=expiry.UtcDateTime;
            }
            core.CookieManager.AddOrUpdateCookie(cookie);ct.ThrowIfCancellationRequested();
            core.Navigate(native is null?RobloxHandoffProbePolicy.CreateGamePageUri(context):NativeHandoffNavigation.InitialPage(native));
            status.Text="Choose Play on this official game page. The external request will be cancelled before inspection; no client or server arrival is being tested.";
            accepted=await completion.Task;
        }
        catch(OperationCanceledException){accepted=Capture(RobloxHandoffProbeOutcome.Cancelled);}
        catch(ProbeFailure error){accepted=Capture(error.Outcome);}
        catch(WebView2RuntimeNotFoundException){accepted=Capture(RobloxHandoffProbeOutcome.BrowserUnavailable);}
        catch(Exception){accepted=Capture(RobloxHandoffProbeOutcome.Failed);}
        finally
        {
            HidePending();if(captureWork is not null)await captureWork;
            bool cleanupProblem=false;
            if(core is not null)
            {
                try
                {
                    // Keep every cancellation guard installed while the controller is alive,
                    // including during cookie deletion and native controller shutdown.
                    core.Stop();core.CookieManager.DeleteAllCookies();
                }
                catch(Exception){cleanupProblem=true;}
            }
            try{browser.Dispose();}catch(Exception){cleanupProblem=true;}
            closing=true;window.Closing-=close;cancel.Click-=cancelClick;
            try{window.Close();}catch(Exception){cleanupProblem=true;}
            bool awaitBrowserExit=controllerReady;
            if(!controllerReady&&initializationStarted&&environment is not null)
                try{awaitBrowserExit=environment.GetProcessInfos().Count!=0;}
                catch(Exception){awaitBrowserExit=true;cleanupProblem=true;}
            // The task stays owned after a five-second observation timeout. No second browser may start, and DisposeAsync
            // joins it, for at most ShutdownJoinLimit, before relinquishing its directory.
            Task cleanup=FinishCleanupAsync(environment,exited,browserExited.Task,awaitBrowserExit,directory,cleanupProblem,DetachClosedGuards,_time);
            if(!await TrackCleanupAsync(cleanup,directory,TimeSpan.FromSeconds(5))){accepted?.Dispose();accepted=Capture(RobloxHandoffProbeOutcome.CleanupFailed);}
            if(completion.Task.IsCompletedSuccessfully&&!ReferenceEquals(accepted,completion.Task.Result))completion.Task.Result.Dispose();
        }
        return accepted??Capture(RobloxHandoffProbeOutcome.Failed);

        void NavigationStarting(object? _,CoreWebView2NavigationStartingEventArgs e)
        {
            if(e.Cancel)return;
            if(ct.IsCancellationRequested||completion.Task.IsCompleted){e.Cancel=true;return;}
            var decision=native is null?RobloxHandoffProbePolicy.ClassifyNavigation(context,e.Uri,externalInstalled)
                :NativeHandoffNavigation.Classify(native,e.Uri,externalInstalled);
            if(decision==RobloxHandoffProbeNavigation.Block)
            {
                e.Cancel=true;
                completion.TrySetResult(Capture(RobloxHandoffProbeOutcome.ContextRejected));
            }
        }
        void FrameNavigationStarting(object? _,CoreWebView2NavigationStartingEventArgs e)
        {
            if(e.Cancel)return;
            if(ct.IsCancellationRequested||completion.Task.IsCompleted){e.Cancel=true;return;}
            // Embedded same-site HTTPS/about:blank frames may host the official page. No frame
            // receives a native proxy, and all external schemes still meet the cancelling event.
            if(e.Uri=="about:blank"||RobloxSignInNavigation.IsAllowed(e.Uri))return;
            if((native is null?RobloxHandoffProbePolicy.ClassifyNavigation(context,e.Uri,externalInstalled)
                :NativeHandoffNavigation.Classify(native,e.Uri,externalInstalled))==RobloxHandoffProbeNavigation.AllowCancelledRobloxPlayer
                &&(native is null?RobloxHandoffProbePolicy.IsAllowedGamePage(context,core?.Source):NativeHandoffNavigation.IsAllowedPage(native,core?.Source)))return;
            e.Cancel=true;
        }
        void ExternalScheme(object? _,CoreWebView2LaunchingExternalUriSchemeEventArgs e)
        {
            e.Cancel=true; // First and unconditional. Never forward the request, including on error.
            if(ct.IsCancellationRequested||completion.Task.IsCompleted||captureWork is not null)return;
            try
            {
                browser.IsEnabled=false;
                if(native is null)
                {
                    var shape=RobloxHandoffProbePolicy.InspectExternal(context,core?.Source,e.InitiatingOrigin,e.Uri);
                    var result=new RobloxHandoffProbeResult(shape.Outcome,shape.Shape,e.IsUserInitiated);
                    captureWork=CompleteCaptureAsync(result.Outcome,result,null,null); // Diagnostic retains no URI.
                }
                else
                {
                    var parsed=NativeRobloxHandoff.Capture(native,core?.Source,e.InitiatingOrigin,e.Uri);
                    var outcome=parsed.Candidate is null?RobloxHandoffProbeOutcome.MalformedHandoff:RobloxHandoffProbeOutcome.CapturedAndPrevented;
                    captureWork=CompleteCaptureAsync(outcome,null,parsed.Candidate,parsed.Status);
                }
            }
            catch(Exception){completion.TrySetResult(Capture(RobloxHandoffProbeOutcome.Failed));}
        }
        async Task CompleteCaptureAsync(RobloxHandoffProbeOutcome outcome,RobloxHandoffProbeResult? diagnostic,
            NativeHandoffCandidate? candidate,NativeHandoffCaptureStatus? shapeStatus)
        {
            RobloxSessionCredential? current=null;
            try
            {
                ct.ThrowIfCancellationRequested();
                if(outcome==RobloxHandoffProbeOutcome.CapturedAndPrevented)
                    current=await OwnedRobloxSessionCookie.ReadAsync(core!.CookieManager,ct);
                ct.ThrowIfCancellationRequested();
                var capture=new OwnedCapture(outcome,current,candidate,shapeStatus,diagnostic);current=null;candidate=null;
                if(!completion.TrySetResult(capture))capture.Dispose();
            }
            catch(OperationCanceledException){completion.TrySetCanceled(ct);}
            catch(Exception){completion.TrySetResult(Capture(RobloxHandoffProbeOutcome.SignInRequired));}
            finally{current?.Dispose();candidate?.Dispose();}
        }
        void NewWindowRequested(object? _,CoreWebView2NewWindowRequestedEventArgs e){e.Handled=true;status.Text="The page requested a pop-up, which this inspection blocks. No external launch was permitted.";}
        void DownloadStarting(object? _,CoreWebView2DownloadStartingEventArgs e){e.Cancel=true;}
        void PermissionRequested(object? _,CoreWebView2PermissionRequestedEventArgs e){e.State=CoreWebView2PermissionState.Deny;}
        void ProcessFailed(object? _,CoreWebView2ProcessFailedEventArgs e)
        {
            // A helper or subframe process WebView2 restarts by itself keeps the check and its guards; ending it here
            // would leave native preparation's pending record needing a new sign-in.
            WebView2ProcessFailurePolicy.HandleProcessFailure(e.ProcessFailedKind,text=>status.Text=text,()=>completion.TrySetResult(Capture(RobloxHandoffProbeOutcome.Failed)));
        }
        void DetachClosedGuards()
        {
            if(core is null)return;
            try
            {
                if(externalInstalled)core.LaunchingExternalUriScheme-=ExternalScheme;
                core.NavigationStarting-=NavigationStarting;core.FrameNavigationStarting-=FrameNavigationStarting;
                core.NewWindowRequested-=NewWindowRequested;core.DownloadStarting-=DownloadStarting;
                core.PermissionRequested-=PermissionRequested;core.ProcessFailed-=ProcessFailed;
            }
            // Native exit is already confirmed. Closed RCWs may reject event removal, but no
            // live controller is left without its cancellation guard.
            catch(InvalidOperationException){}
            catch(System.Runtime.InteropServices.COMException){}
        }
        }
        catch
        {
            if(!bodyOwnsCleanup){closing=true;try{browser.Dispose();}finally{allocatedWindow?.Close();}}
            throw;
        }
    }

    /// <summary>Tracks one browser's folder cleanup for disposal to join and watches it for <paramref name="observation"/>.
    /// A cleanup that failed or is still pending by then latches this probe and keeps the folder's lease until disposal.</summary>
    internal async Task<bool> TrackCleanupAsync(Task cleanup,SignInProfileDirectory directory,TimeSpan observation)
    {
        lock(_state)_cleanup=cleanup;
        try{await cleanup.WaitAsync(observation);return true;}
        catch(Exception){lock(_state){_blocked=true;_retainedDirectory=directory;}return false;}
    }
    /// <summary>Removes the private folder after the browser's confirmed exit. An exit not reported within
    /// <see cref="BrowserExitLimit"/> is a failed cleanup: the folder is kept and marked, every guard stays installed on
    /// the browser that may still be live, and disposal's join of this task is bounded by <see cref="ShutdownJoinLimit"/>,
    /// so Exit finishes and the startup sweep or the next admission recovers the folder once its lease is released.
    /// <paramref name="time"/> lets tests fire that limit.</summary>
    internal static async Task FinishCleanupAsync(CoreWebView2Environment? environment,EventHandler<CoreWebView2BrowserProcessExitedEventArgs> exited,
        Task browserExited,bool awaitBrowserExit,SignInProfileDirectory directory,bool priorProblem,Action detachGuards,TimeProvider time)
    {
        try
        {
            // Bounded: a browser that never reports its exit would otherwise hold this task, the folder and Exit open for good.
            // On a timeout the guards are not detached: that browser's exit was never confirmed.
            if(awaitBrowserExit)await browserExited.WaitAsync(BrowserExitLimit,time);
            detachGuards();
            if(priorProblem&&!awaitBrowserExit)throw new IOException("Private browser cleanup needs review.");
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Task.Run(()=>directory.Remove(deadline.Token));
        }
        // A failure, even one after the five-second observation, keeps the folder and its lease until disposal. A cleanup
        // still waiting for the browser's exit is not marked: other owners see it as still closing until BrowserExitLimit.
        catch(Exception){directory.KeepAfterFailedCleanup();throw;}
        finally{if(environment is not null)environment.BrowserProcessExited-=exited;}
    }
    private static OwnedCapture Capture(RobloxHandoffProbeOutcome outcome)=>new(outcome,null);
    private sealed class OwnedCapture(RobloxHandoffProbeOutcome outcome,RobloxSessionCredential? credential,
        NativeHandoffCandidate? candidate=null,NativeHandoffCaptureStatus? shapeStatus=null,RobloxHandoffProbeResult? diagnostic=null):IDisposable
    {
        public RobloxHandoffProbeOutcome Outcome{get;}=outcome;
        public RobloxHandoffProbeResult? Diagnostic{get;}=diagnostic;
        public NativeHandoffCaptureStatus? ShapeStatus{get;}=shapeStatus;
        public RobloxSessionCredential? TakeCredential()=>Interlocked.Exchange(ref credential,null);
        public NativeHandoffCandidate? TakeCandidate()=>Interlocked.Exchange(ref candidate,null);
        public void Dispose(){Interlocked.Exchange(ref credential,null)?.Dispose();Interlocked.Exchange(ref candidate,null)?.Dispose();}
    }
    private sealed class ProbeFailure(RobloxHandoffProbeOutcome outcome):Exception{public RobloxHandoffProbeOutcome Outcome{get;}=outcome;}
    public ValueTask DisposeAsync(){lock(_state){_disposed=true;return new(_dispose??=StopAsync(_owner?.Task));}}
    /// <summary>Exit's join: the inspection still stopping, then its tracked cleanup, for at most <see cref="ShutdownJoinLimit"/>
    /// together. A cleanup joined within that limit is unchanged and still waits for the browser's real exit (for at most
    /// <see cref="BrowserExitLimit"/>), and its failure keeps its own type. Past the limit the folder's lease is released,
    /// whether the cleanup or the inspection itself still holds it, and the unconfirmed cleanup is reported as a
    /// <see cref="TimeoutException"/>; the folder keeps its marker and data, so the startup sweep removes it once the browser
    /// is gone, or keeps it for review. Nothing here ends the browser or the inspection: that browser's guards stay installed.</summary>
    private async Task StopAsync(Task? owner)
    {
        await Task.Yield();_lifetime.Cancel();
        using var limit=new CancellationTokenSource(ShutdownJoinLimit,_time);
        bool timedOut=false;
        try
        {
            if(owner is not null)await owner.WaitAsync(limit.Token).ConfigureAwait(false);
            Task cleanup;lock(_state)cleanup=_cleanup;
            await cleanup.WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch(OperationCanceledException ex) when(ex.CancellationToken==limit.Token)
        {
            timedOut=true;
            throw new TimeoutException("Exit stopped waiting for the private browser to close. Its sign-in folder is left for the next start to remove.");
        }
        finally
        {
            SignInProfileDirectory? admitted,retained;
            lock(_state){admitted=_admitted;retained=_retainedDirectory;_admitted=null;_retainedDirectory=null;}
            admitted?.ReleaseOwnership();retained?.ReleaseOwnership();
            // An inspection still running after a timed-out join may yet read _lifetime, so it is disposed only after a full join.
            if(!timedOut)_lifetime.Dispose();
        }
    }
}
