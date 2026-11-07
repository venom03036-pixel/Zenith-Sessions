using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class UnhandledExceptionGuardTests : IDisposable
{
    private const string SecretLink="https://www.roblox.com/share?code=SECRET123&type=Server";
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-unhandled-guard-tests-"+Guid.NewGuid().ToString("N"));
    private string Data=>Path.Combine(_root,"data");

    [Fact]
    public Task DispatcherFaultIsRecordedByTypeOnlyAndTheDispatcherKeepsRunning()=>OnUi(async ()=>
    {
        int? exitCode=null;
        using var guard=UnhandledExceptionGuard.Attach(Dispatcher.CurrentDispatcher,_root,()=>null,code=>exitCode=code);
        await ThrowOnDispatcherAsync();
        // No workspace exists to keep running, so the app is asked to end with the failure code instead of crashing.
        Assert.Equal(2,exitCode);
        string log=ReadLog(_root);
        Assert.Contains("\"OperationFailed\"",log);Assert.Contains("InvalidOperationException",log);
        Assert.DoesNotContain("SECRET123",log);Assert.DoesNotContain("roblox.com",log);
    });

    [Fact]
    public Task RunningShellContainsTheFaultWithAFixedNoticeAndItsOwnLogKeepsTheType()=>OnUi(async ()=>
    {
        int? exitCode=null;
        var shell=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor());
        try
        {
            await shell.InitializeAsync();
            using var guard=UnhandledExceptionGuard.Attach(Dispatcher.CurrentDispatcher,Data,()=>shell,code=>exitCode=code);
            await ThrowOnDispatcherAsync();
            Assert.Null(exitCode);
            Assert.Contains("unexpected error",shell.ErrorNotice);Assert.DoesNotContain("SECRET123",shell.ErrorNotice);Assert.DoesNotContain("SECRET123",shell.Notice);
            Assert.Contains(shell.Events,e=>e.Contains("(InvalidOperationException)",StringComparison.Ordinal));
            Assert.All(shell.Events,e=>Assert.DoesNotContain("SECRET123",e));
        }
        finally { await shell.DisposeAsync(); }
        // The running shell's single writer persisted the record before its own Shutdown entry.
        string[] lines=File.ReadAllLines(LogPath(Data));
        Assert.Contains(lines,l=>l.Contains("InvalidOperationException",StringComparison.Ordinal));
        Assert.Contains("\"Shutdown\"",lines[^1]);
        Assert.DoesNotContain("SECRET123",string.Join('\n',lines));
    });

    [Fact]
    public Task RepeatedFaultsInsideTheWindowEndTheAppWithTheirTypeFlushedButSpreadOutFaultsAreContained()=>OnUi(async ()=>
    {
        int? exitCode=null;var clock=new ManualClock();
        var shell=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor());
        try
        {
            await shell.InitializeAsync();
            using var guard=UnhandledExceptionGuard.Attach(Dispatcher.CurrentDispatcher,Data,()=>shell,code=>exitCode=code,clock);
            for(int i=0;i<UnhandledExceptionGuard.ContainedFaultLimit;i++) await ThrowOnDispatcherAsync();
            Assert.Null(exitCode);
            // Once the window has passed, the same number again is still contained.
            clock.Advance(UnhandledExceptionGuard.ContainedFaultWindow);
            for(int i=0;i<UnhandledExceptionGuard.ContainedFaultLimit;i++) await ThrowOnDispatcherAsync();
            Assert.True(exitCode is null,"Faults spread over more than the window ended the app.");
            // One more inside the window is a fault that keeps recurring (for example on every layout or render pass):
            // containing it forever would burn CPU and could starve input such as the Panic shortcut, so the app ends.
            await ThrowOnDispatcherAsync();
            Assert.True(exitCode==2,"A fault that kept recurring was contained forever.");
            // While that exit is underway, a further fault is still recorded but no longer reported as caught and kept running.
            int caught=shell.Events.Count(e=>e.Contains("Unexpected error caught",StringComparison.Ordinal));
            await ThrowOnDispatcherAsync();
            Assert.Equal(caught,shell.Events.Count(e=>e.Contains("Unexpected error caught",StringComparison.Ordinal)));
            Assert.Equal(2,exitCode);
        }
        finally { await shell.DisposeAsync(); }
        // The shell's log was flushed with the fault that ended the app before the exit began.
        string[] lines=File.ReadAllLines(LogPath(Data));
        Assert.True(lines.Count(l=>l.Contains("InvalidOperationException",StringComparison.Ordinal))>=2*UnhandledExceptionGuard.ContainedFaultLimit+1);
        Assert.DoesNotContain("SECRET123",string.Join('\n',lines));
    });

    [Fact]
    public Task FaultAfterTheShellClosedItsLogIsAppendedAfterTheShutdownRecordWithoutForcingExit()=>OnUi(async ()=>
    {
        int? exitCode=null;
        var shell=new ShellViewModel(Data,false,Dispatcher.CurrentDispatcher,new SyntheticMonitor());
        await shell.InitializeAsync();await shell.DisposeAsync();
        using var guard=UnhandledExceptionGuard.Attach(Dispatcher.CurrentDispatcher,Data,()=>shell,code=>exitCode=code);
        await ThrowOnDispatcherAsync();
        // An exit already underway finishes on its own; the guard must not cut its teardown short.
        Assert.Null(exitCode);
        string[] lines=File.ReadAllLines(LogPath(Data));
        Assert.Contains("\"Shutdown\"",lines[^2]);Assert.Contains("InvalidOperationException",lines[^1]);
        Assert.DoesNotContain("SECRET123",string.Join('\n',lines));
    });

    [Fact]
    public Task StartupFailureRecordWithoutAShellIsWrittenByTypeOnly()=>OnUi(()=>
    {
        using var guard=UnhandledExceptionGuard.Attach(Dispatcher.CurrentDispatcher,_root,()=>null,_=>{});
        guard.Record(typeof(UnauthorizedAccessException));
        string log=ReadLog(_root);
        Assert.Contains("\"OperationFailed\"",log);Assert.Contains("UnauthorizedAccessException",log);
        return Task.CompletedTask;
    });

    [Fact]
    public Task UnobservedTaskFaultIsObservedAndRecordedByItsInnerTypeOnly()=>OnUi(()=>
    {
        using var guard=UnhandledExceptionGuard.Attach(Dispatcher.CurrentDispatcher,_root,()=>null,_=>{});
        AbandonFaultedTask();
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();GC.WaitForPendingFinalizers();
        string log=ReadLog(_root);
        Assert.Contains("TimeoutException",log);Assert.DoesNotContain("SECRET123",log);
        return Task.CompletedTask;
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AbandonFaultedTask()=>_=Task.FromException(new TimeoutException(SecretLink));
    private static async Task ThrowOnDispatcherAsync()
    {
        var dispatcher=Dispatcher.CurrentDispatcher;
        var survived=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fault=dispatcher.BeginInvoke(new Action(()=>throw new InvalidOperationException(SecretLink)));
        var after=dispatcher.BeginInvoke(new Action(()=>survived.TrySetResult()));
        await survived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DispatcherOperationStatus.Completed,after.Status);Assert.NotNull(fault);
    }
    private static string LogPath(string root)=>Path.Combine(root,"diagnostics","manager.jsonl");
    private static string ReadLog(string root)
    {
        string path=LogPath(root);
        Assert.True(File.Exists(path),"No diagnostics record was written for the escaped fault.");
        return File.ReadAllText(path);
    }
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks=1;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override long GetTimestamp()=>_ticks;
        public void Advance(TimeSpan by)=>_ticks+=by.Ticks;
    }
    private sealed class SyntheticMonitor : IDesktopMonitor
    {
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)=>Task.FromResult(new DesktopSnapshot(new(DateTimeOffset.UtcNow,32*CapacityPolicy.GiB,20*CapacityPolicy.GiB,64*CapacityPolicy.GiB,20*CapacityPolicy.GiB,20),[]));
        public Task<bool> FocusAsync(SessionObservation session,CancellationToken ct)=>Task.FromResult(false);
        public Task<bool> RequestCloseAsync(SessionObservation session,CancellationToken ct)=>Task.FromResult(false);
    }
    // A fault that escapes the dispatcher ends Dispatcher.Run; report it as a test failure instead of tearing down the test host.
    private static Task OnUi(Func<Task> work)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(()=>_=RunAsync());
            async Task RunAsync(){try{await work();done.TrySetResult();}catch(Exception ex){done.TrySetException(ex);}finally{dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}}
            try {Dispatcher.Run();}
            catch(Exception ex) {done.TrySetException(new Xunit.Sdk.XunitException($"A dispatcher fault escaped and ended the UI thread ({ex.GetType().Name}: {ex.Message})."));}
        }) {IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    public void Dispose()
    {
        string full=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-unhandled-guard-tests-",StringComparison.Ordinal))throw new InvalidOperationException("Unexpected test cleanup path.");
        if(Directory.Exists(full))Directory.Delete(full,true);
    }
}
