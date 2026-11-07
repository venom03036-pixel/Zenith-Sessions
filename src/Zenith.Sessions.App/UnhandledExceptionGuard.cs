using System.Windows;
using System.Windows.Threading;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

/// <summary>
/// Last-chance record of faults that escape every other handler: the UI dispatcher, unobserved tasks and the
/// process itself. Only the exception's Type is recorded (Rule 10). Messages and stack text can carry links,
/// share or private-server codes and account names, so this class never reads Message or ToString().
/// </summary>
internal sealed class UnhandledExceptionGuard : IDisposable
{
    // ManagerDiagnostics stops its writer within 3 s; this bound also covers the cancelled final write.
    private static readonly TimeSpan FlushLimit=TimeSpan.FromSeconds(5);
    // A fault that recurs on every layout or render pass repeats at the display's refresh rate (tens per second), so it passes
    // this limit within its first second. A person repeating an action that fails rarely does so more than ten times in ten seconds.
    internal const int ContainedFaultLimit=10;
    internal static readonly TimeSpan ContainedFaultWindow=TimeSpan.FromSeconds(10);
    private readonly TimeProvider _time;
    // UI thread only (the dispatcher raises its handler there): when the latest ContainedFaultLimit contained faults happened.
    private readonly long[] _containedAt=new long[ContainedFaultLimit];
    private int _containedNext,_containedCount;
    private bool _exitRequested;
    private readonly Dispatcher _dispatcher;
    private readonly string _dataRoot;
    private readonly Func<ShellViewModel?> _shell;
    private readonly Action<int> _shutdown;
    private readonly object _fallbackGate=new();
    private int _detached;

    private UnhandledExceptionGuard(Dispatcher dispatcher,string dataRoot,Func<ShellViewModel?> shell,Action<int> shutdown,TimeProvider time)
    { _dispatcher=dispatcher;_dataRoot=dataRoot;_shell=shell;_shutdown=shutdown;_time=time; }

    /// <summary>Subscribes the dispatcher, unobserved-task and process handlers. Disposing the result unsubscribes them.</summary>
    public static UnhandledExceptionGuard Attach(Dispatcher dispatcher,string dataRoot,Func<ShellViewModel?> shell,Action<int>? shutdown=null,TimeProvider? time=null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);ArgumentNullException.ThrowIfNull(shell);
        var guard=new UnhandledExceptionGuard(dispatcher,dataRoot,shell,shutdown??(code=>ShutdownApplication(dispatcher,code)),time??TimeProvider.System);
        dispatcher.UnhandledException+=guard.OnDispatcherUnhandled;
        TaskScheduler.UnobservedTaskException+=guard.OnUnobservedTask;
        AppDomain.CurrentDomain.UnhandledException+=guard.OnProcessUnhandled;
        return guard;
    }

    /// <summary>Records a fault the caller caught but has nowhere else to report. Safe on any thread.</summary>
    internal void Record(Type errorType)=>Record(errorType,processEnding:false);

    private void OnDispatcherUnhandled(object? sender,DispatcherUnhandledExceptionEventArgs e)
    {
        Type errorType=e.Exception.GetType();
        // Handled first: whatever follows, this fault must not end the process through a Windows crash report.
        e.Handled=true;
        ShellViewModel? shell=_shell();
        if(shell is {IsShuttingDown:false})
        {
            // Containing a fault that keeps recurring would never end: the dispatcher would spend its time on it (Rule 18)
            // and could starve lower-priority work such as input, which carries the Panic shortcut. Past the limit, or once
            // this guard has asked the app to end, end instead of showing it as kept running.
            if(_exitRequested || !AdmitContained()) {EndWhileRunning(errorType);return;}
            try {shell.ContainUnhandled(errorType);return;}
            // The shell could not show the record (for example the same broken binding fired again). End instead of looping.
            catch(Exception nested) {EndWhileRunning(errorType,nested.GetType());return;}
        }
        Record(errorType,processEnding:false);
        // With no shell there is nothing to keep running. An exit already underway finishes its own teardown.
        if(shell is null) _shutdown(2);
    }

    // UI thread only. False when the previous ContainedFaultLimit contained faults all happened within ContainedFaultWindow.
    private bool AdmitContained()
    {
        long now=_time.GetTimestamp();
        if(_containedCount==ContainedFaultLimit && _time.GetElapsedTime(_containedAt[_containedNext],now)<ContainedFaultWindow) return false;
        _containedAt[_containedNext]=now;_containedNext=(_containedNext+1)%ContainedFaultLimit;
        if(_containedCount<ContainedFaultLimit) _containedCount++;
        return true;
    }

    // The shell is not disposed on this path, so its log is flushed now. The app is asked to end once; a fault raised while
    // that exit is underway is still recorded.
    private void EndWhileRunning(Type errorType,Type? nested=null)
    {
        Record(errorType,processEnding:true);
        if(nested is not null) Record(nested,processEnding:true);
        if(_exitRequested) return;
        _exitRequested=true;_shutdown(2);
    }

    private void OnUnobservedTask(object? sender,UnobservedTaskExceptionEventArgs e)
    {
        // Finalizer thread: never touch view state here. .NET does not end the process for these faults,
        // but without this handler they vanish with no record.
        e.SetObserved();
        if(e.Exception is not {} aggregate) return;
        var faults=aggregate.Flatten().InnerExceptions;
        // A task abandoned after cancellation is expected shutdown, not an error (Error Handling Standards).
        if(faults.Count>0 && faults.All(f=>f is OperationCanceledException)) return;
        Record(faults.Count==1?faults[0].GetType():aggregate.GetType(),processEnding:false);
    }

    // The runtime is ending the process: flush whichever log can still take the record.
    private void OnProcessUnhandled(object? sender,UnhandledExceptionEventArgs e)=>Record(e.ExceptionObject.GetType(),processEnding:true);

    private void Record(Type errorType,bool processEnding)
    {
        ShellViewModel? shell=_shell();
        if(shell is not null)
        {
            bool kept=shell.TryRecordUnhandled(errorType);
            // A running shell's own writer persists the record. Once its shutdown has begun, or the process is
            // ending, nothing else guarantees that flush, so close its log (idempotent) and wait for the writer.
            if(kept && !processEnding && !shell.IsShuttingDown) return;
            Wait(shell.CloseDiagnosticsAsync());
            if(kept) return;
        }
        // Only now is manager.jsonl free of the shell's writer, so a short-lived second writer may append to it.
        WriteFallback(errorType);
    }

    private void WriteFallback(Type errorType)
    {
        // Serialized: two short-lived writers appending to the same file would contend for it.
        lock(_fallbackGate)
        {
            try
            {
                var log=new ManagerDiagnostics(_dataRoot);
                log.Record(ManagerEventKind.OperationFailed,errorType);
                Wait(log.DisposeAsync());
            }
            // This is the last place a record can go. Throwing here would replace the original fault (or crash the
            // dispatcher handler), so an unusable data-root path drops the record deliberately.
            catch(Exception ex) when(ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Wait(ValueTask pending)
    {
        // Bounded, and safe on the UI thread: ManagerDiagnostics completes on the thread pool, never the dispatcher.
        try {pending.AsTask().Wait(FlushLimit);}
        // The log already keeps its own failure state; a last-chance handler has no further place to report it.
        catch(AggregateException) { }
    }

    private static void ShutdownApplication(Dispatcher dispatcher,int code)
    {
        // Only the application that owns this dispatcher may be ended from its handler thread.
        if(Application.Current is {} app && app.Dispatcher==dispatcher) app.Shutdown(code);
    }

    public void Dispose()
    {
        if(Interlocked.Exchange(ref _detached,1)!=0) return;
        _dispatcher.UnhandledException-=OnDispatcherUnhandled;
        TaskScheduler.UnobservedTaskException-=OnUnobservedTask;
        AppDomain.CurrentDomain.UnhandledException-=OnProcessUnhandled;
    }
}
