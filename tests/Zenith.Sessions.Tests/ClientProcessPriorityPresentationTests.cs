using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientPreviewPresentationTests
{
    [Theory]
    [InlineData(ClientProcessPriorityAction.Normal)]
    [InlineData(ClientProcessPriorityAction.BelowNormal)]
    [InlineData(ClientProcessPriorityAction.RestoreOriginal)]
    public Task PriorityCommandsPassOnlyThePinnedCheckedProcessesAndExactAction(ClientProcessPriorityAction action)=>OnUi(async ()=>
    {
        var control=new PriorityControlProbe();var monitor=new PriorityDesktopMonitor(3);
        await using var shell=await ReadyPriority(monitor,control);
        shell.SelectedSession=shell.Sessions[1];shell.Sessions[0].IsChecked=true;shell.Sessions[2].IsChecked=true;
        var expected=new[]{shell.Sessions[0].Observation,shell.Sessions[2].Observation};
        var command=PriorityCommand(shell,action);command.Execute(null);await command.Execution;
        var call=await control.NextAsync();
        Assert.Equal(action,call.Action);Assert.Equal(expected,call.Targets);
        Assert.Equal(expected.Select(row=>row.InstanceKey),shell.ClientPriorityResults.Select(row=>row.Session.InstanceKey));
        Assert.All(shell.ClientPriorityResults,row=>Assert.Equal(action,row.Action));
        Assert.False(shell.IsChangingClientPriority);Assert.True(shell.CanChangeClientPriority);
    });

    [Fact]
    public Task PrioritySelectedFallbackAllowsAnObservedWindowlessProcessAndPinsSelectionBeforeAwait()=>OnUi(async ()=>
    {
        var control=new PriorityControlProbe{DeferChange=true};var monitor=new PriorityDesktopMonitor(2){Windowless=true};
        await using var shell=await ReadyPriority(monitor,control);
        shell.SelectedSession=shell.Sessions[0];var selected=shell.SelectedSession.Observation;
        shell.SetClientsBelowNormalPriority.Execute(null);var call=await control.NextAsync();
        try
        {
            Assert.Equal(selected,Assert.Single(call.Targets));Assert.Equal((nint)0,selected.Window);
            shell.SelectedSession=shell.Sessions[1];shell.Sessions[1].IsChecked=true;
            shell.SetClientsNormalPriority.Execute(null);await shell.SetClientsNormalPriority.Execution;
            Assert.Equal(1,control.ChangeCalls);Assert.True(shell.IsChangingClientPriority);
        }
        finally{call.Reply.TrySetResult(PriorityReport(call));await shell.SetClientsBelowNormalPriority.Execution;}
        Assert.Equal(selected,Assert.Single(shell.ClientPriorityResults).Session);
        Assert.Equal(1,shell.PendingPriorityRestoreCount);
    });

    [Theory]
    [InlineData("partial")]
    [InlineData("stale-inventory")]
    [InlineData("stale-row")]
    [InlineData("future-inventory")]
    [InlineData("problem")]
    [InlineData("duplicate-pid")]
    [InlineData("unversioned")]
    public Task PriorityRefusesUnhealthyOrAmbiguousCompleteInventoryWithoutBackendCall(string invalid)=>OnUi(async ()=>
    {
        var control=new PriorityControlProbe();var monitor=new PriorityDesktopMonitor(2);
        await using var shell=await ReadyPriority(monitor,control);
        monitor.Invalid=invalid;await Refresh(shell);shell.SelectedSession=shell.Sessions[0];
        shell.SetClientsBelowNormalPriority.Execute(null);await shell.SetClientsBelowNormalPriority.Execution;
        Assert.Equal(0,control.ChangeCalls);Assert.Empty(shell.ClientPriorityResults);
        Assert.Contains("fresh scan",shell.ClientPriorityStatus,StringComparison.OrdinalIgnoreCase);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PriorityRefusesAStaleSelectedInstanceEvenWhenAnotherProcessReusesThePid(bool checkedRow)=>OnUi(async ()=>
    {
        var control=new PriorityControlProbe();var monitor=new PriorityDesktopMonitor(1);
        await using var shell=await ReadyPriority(monitor,control);
        var original=shell.Sessions[0];
        original.Update(original.Observation with{ProcessStartUtcTicks=original.Observation.ProcessStartUtcTicks-1});
        if(checkedRow)original.IsChecked=true;else shell.SelectedSession=original;
        shell.SetClientsNormalPriority.Execute(null);await shell.SetClientsNormalPriority.Execution;
        Assert.Equal(0,control.ChangeCalls);Assert.Empty(shell.ClientPriorityResults);
    });

    [Theory]
    [InlineData(128,true)]
    [InlineData(129,false)]
    public Task PriorityPinsAtMost128ProcessesWithoutTruncatingTheRequestedBatch(int count,bool allowed)=>OnUi(async ()=>
    {
        var control=new PriorityControlProbe();var monitor=new PriorityDesktopMonitor(count);
        await using var shell=await ReadyPriority(monitor,control);
        foreach(var row in shell.Sessions)row.IsChecked=true;
        shell.SetClientsNormalPriority.Execute(null);await shell.SetClientsNormalPriority.Execution;
        Assert.Equal(allowed?1:0,control.ChangeCalls);
        if(allowed){var call=await control.NextAsync();Assert.Equal(count,call.Targets.Count);Assert.Equal(count,shell.ClientPriorityResults.Count);}
        else Assert.Empty(shell.ClientPriorityResults);
    });

    [Theory]
    [InlineData("panic")]
    [InlineData("lock")]
    [InlineData("cancel")]
    public Task PriorityReentrantStopDuringBusyPublicationCancelsBeforeBackendAdmission(string stop)=>OnUi(async ()=>
    {
        var control=new PriorityControlProbe();var monitor=new PriorityDesktopMonitor(1);
        await using var shell=await ReadyPriority(monitor,control);shell.SelectedSession=shell.Sessions[0];
        bool stopped=false;
        void StopOnPublication(object? sender,System.ComponentModel.PropertyChangedEventArgs args)
        {
            if(stopped||args.PropertyName!=nameof(shell.IsChangingClientPriority)||!shell.IsChangingClientPriority)return;
            stopped=true;
            if(stop=="lock")shell.LockDashboard();else if(stop=="panic")shell.Panic.Execute(null);else shell.CancelClientPriority.Execute(null);
        }
        shell.PropertyChanged+=StopOnPublication;
        try{shell.SetClientsBelowNormalPriority.Execute(null);await shell.SetClientsBelowNormalPriority.Execution;}
        finally{shell.PropertyChanged-=StopOnPublication;}
        Assert.True(stopped);Assert.Equal(0,control.ChangeCalls);Assert.Empty(shell.ClientPriorityResults);
        Assert.False(shell.IsChangingClientPriority);
    });

    [Fact]
    public Task PriorityPanicAtFirstCommandNotificationFencesTheCapturedClick()=>OnUi(async ()=>
    {
        var control=new PriorityControlProbe();
        await using var shell=await ReadyPriority(new PriorityDesktopMonitor(1),control);
        shell.SelectedSession=shell.Sessions[0];
        var command=shell.SetClientsBelowNormalPriority;
        bool stopped=false;
        void StopAtFirstNotification(object? sender,EventArgs args)
        {
            if(stopped||command.CanExecute(null))return;
            stopped=true;
            shell.Panic.Execute(null);
        }
        command.CanExecuteChanged+=StopAtFirstNotification;
        try
        {
            command.Execute(null);
            await command.Execution;
        }
        finally{command.CanExecuteChanged-=StopAtFirstNotification;}
        Assert.True(stopped);
        Assert.Equal(0,control.ChangeCalls);
        Assert.Empty(shell.ClientPriorityResults);
        Assert.False(shell.IsChangingClientPriority);
    });

    [Fact]
    public Task PrioritySelectionAtFirstCommandNotificationCannotRetargetTheCapturedClick()=>OnUi(async ()=>
    {
        var control=new PriorityControlProbe();
        await using var shell=await ReadyPriority(new PriorityDesktopMonitor(2),control);
        shell.SelectedSession=shell.Sessions[0];
        var original=shell.SelectedSession.Observation;
        var replacement=shell.Sessions[1];
        var command=shell.SetClientsBelowNormalPriority;
        bool changed=false;
        void ChangeSelectionAtFirstNotification(object? sender,EventArgs args)
        {
            if(changed||command.CanExecute(null))return;
            changed=true;
            shell.SelectedSession=replacement;
            replacement.IsChecked=true;
        }
        command.CanExecuteChanged+=ChangeSelectionAtFirstNotification;
        try
        {
            command.Execute(null);
            await command.Execution;
        }
        finally{command.CanExecuteChanged-=ChangeSelectionAtFirstNotification;}
        Assert.True(changed);
        Assert.Same(replacement,shell.SelectedSession);
        Assert.Equal(original,Assert.Single((await control.NextAsync()).Targets));
        Assert.Equal(original,Assert.Single(shell.ClientPriorityResults).Session);
        Assert.Equal(1,control.ChangeCalls);
    });

    [Fact]
    public Task PriorityStopRetainsAcceptedPartialResultsAndDoesNotOverwriteThePanicNotice()=>OnUi(async ()=>
    {
        var control=new PriorityControlProbe{DeferChange=true};var monitor=new PriorityDesktopMonitor(2);
        await using var shell=await ReadyPriority(monitor,control);
        foreach(var row in shell.Sessions)row.IsChecked=true;
        shell.SetClientsBelowNormalPriority.Execute(null);var call=await control.NextAsync();
        string? panicNotice=null;
        try
        {
            shell.Panic.Execute(null);panicNotice=shell.Notice;
            Assert.True(call.Token.IsCancellationRequested);Assert.True(shell.IsChangingClientPriority);
            Assert.False(shell.SetClientsBelowNormalPriority.Execution.IsCompleted);
        }
        finally
        {
            var first=call.Targets[0];var second=call.Targets[1];
            call.Reply.TrySetResult(new([
                new(first,call.Action,ClientProcessPriorityStatus.Applied,ClientProcessPriorityClass.Normal,ClientProcessPriorityClass.BelowNormal,ClientProcessPriorityClass.BelowNormal,true),
                new(second,call.Action,ClientProcessPriorityStatus.Cancelled,null,ClientProcessPriorityClass.BelowNormal,null,false)],true,1));
            await shell.SetClientsBelowNormalPriority.Execution;
        }
        Assert.Equal(new[]{ClientProcessPriorityStatus.Applied,ClientProcessPriorityStatus.Cancelled},shell.ClientPriorityResults.Select(row=>row.Status));
        Assert.Equal(1,shell.PendingPriorityRestoreCount);Assert.Equal(panicNotice,shell.Notice);
        Assert.Contains("stopped",shell.ClientPriorityStatus,StringComparison.OrdinalIgnoreCase);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PriorityDisposeJoinsActualChangeBeforeRestoringWithAnIndependentToken(bool restoreFails)=>OnUi(async ()=>
    {
        var control=new PriorityControlProbe{DeferChange=true,RestoreFails=restoreFails};var monitor=new PriorityDesktopMonitor(1);
        var shell=await ReadyPriority(monitor,control);shell.SelectedSession=shell.Sessions[0];
        control.DeferRestore=true;
        var diagnostics=(ManagerDiagnostics)typeof(ShellViewModel).GetField("_diagnostics",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(shell)!;
        long errorsBefore=diagnostics.GetSnapshot().ErrorCount;
        shell.SetClientsBelowNormalPriority.Execute(null);var call=await control.NextAsync();
        Task disposing=shell.DisposeAsync().AsTask();
        try
        {
            await Until(()=>call.Token.IsCancellationRequested);
            Assert.False(disposing.IsCompleted);Assert.False(control.DisposeEntered.Task.IsCompleted);Assert.False(control.RestoreEntered.Task.IsCompleted);
            call.Reply.TrySetResult(PriorityReport(call));
            await control.RestoreEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(control.ChangeCompleted);Assert.False(control.RestoreToken.IsCancellationRequested);Assert.False(disposing.IsCompleted);
        }
        finally
        {
            call.Reply.TrySetResult(PriorityReport(call));control.RestoreRelease.TrySetResult();
            await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(new[]{"change-terminal","restore-entered","dispose-terminal"},control.Order.ToArray());
        Assert.Equal(1,control.RestoreCalls);
        if(restoreFails)
        {
            Assert.Equal(1,shell.PendingPriorityRestoreCount);
            Assert.True(diagnostics.GetSnapshot().ErrorCount>errorsBefore);
            Assert.Contains("could not confirm",shell.ClientPriorityStatus,StringComparison.OrdinalIgnoreCase);
        }
        else Assert.Equal(0,shell.PendingPriorityRestoreCount);
    });

    [Fact]
    public Task PriorityDisposeRestoresBeforeWaitingForUnrelatedInitializationCleanup()=>OnUi(async ()=>
    {
        var control=new PriorityControlProbe();
        var shell=await ReadyPriority(new PriorityDesktopMonitor(1),control);
        var initializeTail=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? disposing=null;
        try
        {
            shell.SelectedSession=shell.Sessions[0];
            shell.SetClientsBelowNormalPriority.Execute(null);
            await shell.SetClientsBelowNormalPriority.Execution;
            Assert.Equal(1,shell.PendingPriorityRestoreCount);
            // Hold the actual unrelated task that the Shell joins during disposal.
            // Initialization itself completed normally before this synthetic tail.
            typeof(ShellViewModel).GetField("_initializeTask",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(shell,initializeTail.Task);
            disposing=shell.DisposeAsync().AsTask();
            await control.RestoreEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(initializeTail.Task.IsCompleted);
            Assert.False(disposing.IsCompleted);
            Assert.False(control.RestoreToken.IsCancellationRequested);
            Assert.Equal(0,shell.PendingPriorityRestoreCount);
        }
        finally
        {
            initializeTail.TrySetResult();
            await (disposing??shell.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(1,control.RestoreCalls);
    });

    [Fact]
    public Task PrioritySampleAllowsAnExplicitInjectedControl()=>OnUi(async ()=>
    {
        var control=new PriorityControlProbe();var monitor=new PriorityDesktopMonitor(1);
        await using var shell=await ReadyPriority(monitor,control,sample:true);shell.SelectedSession=shell.Sessions[0];
        Assert.True(shell.CanChangeClientPriority);
        shell.SetClientsNormalPriority.Execute(null);await shell.SetClientsNormalPriority.Execution;
        Assert.Equal(1,control.ChangeCalls);
    });
    [Fact]
    public Task PrioritySampleRefusesTheDefaultNativeControlEvenWhenCalledDirectly()=>OnUi(async ()=>
    {
        await using var shell=await ReadyPriority(new PriorityDesktopMonitor(1),null,sample:true);shell.SelectedSession=shell.Sessions[0];
        Assert.False(shell.CanChangeClientPriority);
        shell.SetClientsBelowNormalPriority.Execute(null);await shell.SetClientsBelowNormalPriority.Execution;
        Assert.Empty(shell.ClientPriorityResults);Assert.Equal(0,shell.PendingPriorityRestoreCount);
    });

    private async Task<ShellViewModel> ReadyPriority(PriorityDesktopMonitor monitor,PriorityControlProbe? control,bool sample=false)
    {
        var capture=new PreviewProvider();
        var shell=new ShellViewModel(_root,sample,Dispatcher.CurrentDispatcher,monitor,
            new NoPublicDirectory(),new NoServerDirectory(),previewProvider:capture,clientPriority:control);
        try
        {
            await shell.InitializeAsync();await Refresh(shell);Assert.True(shell.IsReady,shell.Notice);
            Assert.Equal(0,capture.CaptureCalls);return shell;
        }
        catch{await shell.DisposeAsync();throw;}
    }
    private static AsyncCommand PriorityCommand(ShellViewModel shell,ClientProcessPriorityAction action)=>action switch
    {
        ClientProcessPriorityAction.Normal=>shell.SetClientsNormalPriority,
        ClientProcessPriorityAction.BelowNormal=>shell.SetClientsBelowNormalPriority,
        _=>shell.RestoreClientsPriority
    };
    private static ClientProcessPriorityBatchResult PriorityReport(PriorityCall call)
    {
        bool restoring=call.Action==ClientProcessPriorityAction.RestoreOriginal;
        var requested=call.Action==ClientProcessPriorityAction.BelowNormal?ClientProcessPriorityClass.BelowNormal:ClientProcessPriorityClass.Normal;
        return new(call.Targets.Select(row=>new ClientProcessPriorityResult(row,call.Action,
            restoring?ClientProcessPriorityStatus.Restored:ClientProcessPriorityStatus.Applied,
            ClientProcessPriorityClass.Normal,requested,requested,!restoring)).ToArray(),false,restoring?0:call.Targets.Count);
    }
    private sealed record PriorityCall(IReadOnlyList<SessionObservation> Targets,ClientProcessPriorityAction Action,CancellationToken Token)
    {internal TaskCompletionSource<ClientProcessPriorityBatchResult> Reply {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);}
    private sealed class PriorityControlProbe:IClientProcessPriorityControl
    {
        private readonly Channel<PriorityCall> _calls=Channel.CreateBounded<PriorityCall>(4);
        private int _pending;
        internal bool DeferChange,DeferRestore,RestoreFails,ChangeCompleted;
        internal int ChangeCalls,RestoreCalls;
        internal CancellationToken RestoreToken;
        internal ConcurrentQueue<string> Order {get;}=new();
        internal TaskCompletionSource DisposeEntered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RestoreEntered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RestoreRelease {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PendingRestoreCount=>_pending;
        public async Task<ClientProcessPriorityBatchResult> ChangeAsync(IReadOnlyList<SessionObservation> sessions,ClientProcessPriorityAction action,CancellationToken ct)
        {
            ChangeCalls++;var call=new PriorityCall(Array.AsReadOnly(sessions.ToArray()),action,ct);
            Assert.True(_calls.Writer.TryWrite(call));
            var result=DeferChange?await call.Reply.Task:PriorityReport(call);
            _pending=result.PendingRestoreCount;ChangeCompleted=true;Order.Enqueue("change-terminal");return result;
        }
        internal Task<PriorityCall> NextAsync()=>_calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public void CancelOperations(){}
        public async Task<ClientProcessPriorityBatchResult> RestoreAllAsync(CancellationToken ct)
        {
            RestoreCalls++;RestoreToken=ct;Order.Enqueue("restore-entered");RestoreEntered.TrySetResult();
            if(DeferRestore)await RestoreRelease.Task;
            if(!RestoreFails)_pending=0;
            return new([],false,_pending);
        }
        public async ValueTask DisposeAsync()
        {
            DisposeEntered.TrySetResult();
            try
            {
                await RestoreAllAsync(CancellationToken.None);
                if(_pending!=0)throw new ClientProcessPriorityCleanupException(_pending);
            }
            finally{Order.Enqueue("dispose-terminal");}
        }
    }
    private sealed class PriorityDesktopMonitor(int count):IDesktopMonitor
    {
        internal string? Invalid;
        internal bool Windowless;
        private long _revision;
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();var now=DateTimeOffset.UtcNow;
            var at=Invalid=="stale-inventory"?now.AddSeconds(-10):Invalid=="future-inventory"?now.AddSeconds(10):now;
            var rows=Enumerable.Range(1,count).Select(pid=>new SessionObservation(Invalid=="duplicate-pid"?1:pid,
                new DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero).UtcTicks,"Synthetic",Windowless?0:pid+100,"Synthetic",Windowless,!Windowless,
                1,1,0,Invalid=="stale-row"?now.AddSeconds(-10):at)).ToArray();
            return Task.FromResult(new DesktopSnapshot(new(now,32*CapacityPolicy.GiB,20*CapacityPolicy.GiB,
                64*CapacityPolicy.GiB,20*CapacityPolicy.GiB,10),rows,Invalid=="problem"?"Synthetic incomplete scan":null,
                at,Invalid=="unversioned"?0:Interlocked.Increment(ref _revision),Invalid!="partial"));
        }
        public Task<bool> FocusAsync(SessionObservation session,CancellationToken ct)=>throw new InvalidOperationException("No window mutation expected.");
        public Task<bool> RequestCloseAsync(SessionObservation session,CancellationToken ct)=>throw new InvalidOperationException("No process action expected.");
    }
}
