using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Xunit;
// The namespace Zenith.Sessions.App hides its App class from this namespace.
using SessionsApp = Zenith.Sessions.App.App;

namespace Zenith.Sessions.Tests;

// App.OnStartup's failure path: a fault while opening the workspace is recorded by type, shown to the user and ends the app
// with code 2. App itself cannot be built here (one Application per process), so these drive the startup it delegates to.
public sealed class StartupFailureTests : IDisposable
{
    private const string SecretLink="https://www.roblox.com/share?code=SECRET123&type=Server";
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-startup-failure-tests-"+Guid.NewGuid().ToString("N"));
    private string Data=>Path.Combine(_root,"data");

    [Fact]
    public Task AStartupFaultStopsTheShownWorkspaceBeforeItsMessageAndEndsWithCode2()=>OnUi(async ()=>
    {
        ShellViewModel? shell=null; var recorded=new List<Type>(); string? message=null; bool stoppedFirst=false; int? exitCode=null;
        await SessionsApp.OpenWorkspaceAsync(()=>
        {
            shell=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor());
            // MainWindow.Show() queues the window's Loaded event, and Loaded opens the workspace.
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded,()=>_=shell.InitializeAsync());
            // A fault after the window was shown, for example its restore listener refusing to start.
            throw new InvalidOperationException(SecretLink);
        },()=>shell,recorded.Add,text=>
        {
            stoppedFirst=shell!.IsShuttingDown;
            // The real message box runs a modal loop, so the queued Loaded event fires while it is open.
            Pump();
            message=text;
        },code=>exitCode=code);
        Assert.True(stoppedFirst,"The half-started workspace was still running while its startup failure was shown.");
        Assert.Equal(2,exitCode);
        Assert.Equal(new[]{typeof(InvalidOperationException)},recorded);
        Assert.Equal(SessionsApp.StartupFailureMessage(typeof(InvalidOperationException)),message);
        Assert.DoesNotContain("SECRET123",message);
        string[] kinds=ReadKinds();
        Assert.DoesNotContain("Started",kinds);
        Assert.Equal("Shutdown",kinds[^1]);
        Assert.False(shell!.IsReady,"The workspace opened behind its startup failure message.");
        Assert.DoesNotContain(shell.Events,e=>e.Contains("Operation failed",StringComparison.Ordinal));
    });

    [Fact]
    public Task AStartupFaultIsShownAndEndsWithCode2WithoutAWorkspaceOrWhenItCannotStop()=>OnUi(async ()=>
    {
        // A successful start neither reports nor ends the app.
        await SessionsApp.OpenWorkspaceAsync(()=>{},()=>null,_=>throw new Xunit.Sdk.XunitException("Nothing failed."),
            _=>throw new Xunit.Sdk.XunitException("No failure to show."),_=>throw new Xunit.Sdk.XunitException("The app must keep running."));

        // The fault came before the workspace existed (for example the theme or the data folder).
        var recorded=new List<Type>(); string? message=null; int? exitCode=null;
        await SessionsApp.OpenWorkspaceAsync(()=>throw new UnauthorizedAccessException(SecretLink),()=>null,recorded.Add,text=>message=text,code=>exitCode=code);
        Assert.Equal(2,exitCode); Assert.Equal(new[]{typeof(UnauthorizedAccessException)},recorded);
        Assert.Equal(SessionsApp.StartupFailureMessage(typeof(UnauthorizedAccessException)),message);

        // Stopping the half-started workspace fails too: that fault is kept by type and the app still ends with code 2.
        recorded.Clear(); message=null; exitCode=null;
        await SessionsApp.OpenWorkspaceAsync(()=>throw new InvalidOperationException(SecretLink),()=>new WorkspaceThatCannotStop(),recorded.Add,text=>message=text,code=>exitCode=code);
        Assert.Equal(2,exitCode); Assert.NotNull(message); Assert.DoesNotContain("SECRET123",message);
        Assert.Equal(new[]{typeof(InvalidOperationException),typeof(IOException)},recorded);
    });

    [Fact]
    public Task AStoppedWorkspaceDoesNotOpenWhenItsWindowLoadsLater()=>OnUi(async ()=>
    {
        var shell=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor());
        await shell.DisposeAsync();
        // The window's Loaded event can still arrive, for example while the startup failure message is open.
        await shell.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(shell.IsReady);
        Assert.NotEqual("Startup needs attention",shell.SaveStatus);
        Assert.DoesNotContain(shell.Events,e=>e.Contains("Operation failed",StringComparison.Ordinal));
        Assert.DoesNotContain("Started",ReadKinds());
    });

    private sealed class WorkspaceThatCannotStop : IAsyncDisposable
    { public ValueTask DisposeAsync()=>ValueTask.FromException(new IOException(SecretLink)); }
    // Runs everything already queued on this dispatcher, as a modal message box's own loop does.
    private static void Pump()
    {
        var frame=new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,()=>frame.Continue=false);
        Dispatcher.PushFrame(frame);
    }
    private string[] ReadKinds()
    {
        string path=Path.Combine(Data,"diagnostics","manager.jsonl");
        if(!File.Exists(path)) return [];
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        using var reader=new StreamReader(file);
        return reader.ReadToEnd().Split('\n').Select(l=>l.Trim()).Where(l=>l.Length>0)
            .Select(l=> { using var line=JsonDocument.Parse(l); return line.RootElement.GetProperty("Kind").GetString()!; }).ToArray();
    }
    private sealed class SyntheticMonitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)=>Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow,32*CapacityPolicy.GiB,20*CapacityPolicy.GiB,64*CapacityPolicy.GiB,20*CapacityPolicy.GiB,20),[]));
        public Task<bool> FocusAsync(SessionObservation session,CancellationToken ct)=>Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session,CancellationToken ct)=>Task.FromResult(false);
    }
    private static Task OnUi(Func<Task> work)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(()=>_=RunAsync());
            async Task RunAsync(){try{await work();done.TrySetResult();}catch(Exception ex){done.TrySetException(ex);}finally{dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}}
            try {Dispatcher.Run();}
            catch(Exception ex) {done.TrySetException(new Xunit.Sdk.XunitException($"A dispatcher fault escaped and ended the UI thread ({ex.GetType().Name})."));}
        }) {IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    public void Dispose()
    {
        string full=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-startup-failure-tests-",StringComparison.Ordinal))throw new InvalidOperationException("Unexpected test cleanup path.");
        if(Directory.Exists(full))Directory.Delete(full,true);
    }
}
