using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientPreviewPresentationTests
{
    [Fact]
    public void PendingRestoreLabelNamesEverySettingARestoreCanCover()
    {
        // O-C6-5: the pending count covers the priority class, EcoQoS, memory priority, affinity and (R-instances-7) the CPU
        // cap, so the label names the same "priority, efficiency, core or CPU cap settings" as the Exit warning instead of
        // "priorities" alone.
        using var source=typeof(ClientPreviewPresentationTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        var document=XDocument.Load(source);
        XNamespace presentation="http://schemas.microsoft.com/winfx/2006/xaml/presentation",xaml="http://schemas.microsoft.com/winfx/2006/xaml";
        var tools=document.Descendants(presentation+"Expander").Single(element=>(string?)element.Attribute(xaml+"Name")=="ClientPriorityTools");
        var label=tools.Descendants(presentation+"TextBlock").Single(element=>((string?)element.Attribute("Text"))?.StartsWith("{Binding PendingPriorityRestoreCount,",StringComparison.Ordinal)==true);
        Assert.Equal("{Binding PendingPriorityRestoreCount, StringFormat='Clients with original priority, efficiency, core or CPU cap settings awaiting restoration: {0}'}",(string)label.Attribute("Text")!);
    }

    [Theory]
    [InlineData(ClientProcessPriorityAction.Normal)]
    [InlineData(ClientProcessPriorityAction.BelowNormal)]
    [InlineData(ClientProcessPriorityAction.RestoreOriginal)]
    [InlineData(ClientProcessPriorityAction.Efficiency)]
    [InlineData(ClientProcessPriorityAction.SpreadCores)]
    [InlineData(ClientProcessPriorityAction.CapCpu)]
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
    public Task CapCpuPassesTheChosenShareOfAllCpusWithThePinnedClientsAndDefaultsToAQuarter()=>OnUi(async ()=>
    {
        var control=new PriorityControlProbe();var monitor=new PriorityDesktopMonitor(2);
        await using var shell=await ReadyPriority(monitor,control);
        // R-instances-7: the share is a per-click choice, never a saved preference, so every start offers the same default.
        Assert.Equal(25,shell.ClientCpuCapPercent);Assert.Equal([10,25,50,75],shell.ClientCpuCapChoices);
        shell.ClientCpuCapPercent=50;shell.Sessions[1].IsChecked=true;
        shell.CapClientsCpu.Execute(null);await shell.CapClientsCpu.Execution;
        var call=await control.NextAsync();
        Assert.Equal(ClientProcessPriorityAction.CapCpu,call.Action);Assert.Equal(50,call.CpuCapPercent);
        Assert.Equal([shell.Sessions[1].Observation],call.Targets);
        Assert.Equal(ClientProcessPriorityAction.CapCpu,Assert.Single(shell.ClientPriorityResults).Action);
        Assert.Equal(1,shell.PendingPriorityRestoreCount);
    });

    [Fact]
    public Task StartupChecksForCapsAnEarlierRunLeftAndStaysQuietWhenThereAreNone()=>OnUi(async ()=>
    {
        var control=new PriorityControlProbe();
        await using var shell=await ReadyPriority(new PriorityDesktopMonitor(1),control);
        Assert.Equal(1,control.CheckCalls);
        Assert.Equal("",shell.ErrorNotice);
        Assert.DoesNotContain(shell.Events,row=>row.Contains("CPU cap",StringComparison.Ordinal));
    });

    // A cap lives in a Windows job only the run that created it can reach, so a run that did not exit normally leaves it for
    // the client's lifetime: the next start names those clients and says closing them is the way out, instead of pretending.
    [Theory]
    [InlineData(1,0,false,"1 client still carries the CPU cap an earlier run of Zenith set and could not clear, because that run did not exit normally. "
        +"Windows keeps a process in its job for life, so the cap lasts until that client exits; close and reopen it to lift the cap.")]
    [InlineData(2,0,false,"2 clients still carry the CPU cap an earlier run of Zenith set and could not clear, because that run did not exit normally. "
        +"Windows keeps a process in its job for life, so each cap lasts until that client exits; close and reopen them to lift the caps.")]
    [InlineData(0,1,false,"Zenith could not check whether 1 client an earlier run capped still runs. A cap it left lasts until that client exits.")]
    [InlineData(0,0,true,"Zenith could not read its record of the CPU caps earlier runs set (client-cpu-caps.json in the data folder), so it cannot name the clients that still carry one; such a cap lasts until that client exits. The next cap replaces the record.")]
    public Task StartupNamesTheClientsAnEarlierRunLeftCappedOrSaysWhatItCouldNotCheck(int stillCapped,int notChecked,bool unreadable,string expected)=>OnUi(async ()=>
    {
        var control=new PriorityControlProbe{Leftover=new(stillCapped,notChecked,unreadable,false)};
        await using var shell=await ReadyPriority(new PriorityDesktopMonitor(1),control);
        Assert.Equal(expected,shell.ErrorNotice);
        Assert.Contains(shell.Events,row=>row.EndsWith(expected,StringComparison.Ordinal));
        Assert.True(shell.IsReady);
        var diagnostics=(ManagerDiagnostics)typeof(ShellViewModel).GetField("_diagnostics",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(shell)!;
        Assert.Contains(diagnostics.GetSnapshot().Events,row=>row.Kind==ManagerEventKind.OperationFailed && row.Subsystem==ManagerSubsystem.ClientPriority);
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

    [Fact]
    public Task PrioritySkipsAnotherProcessWhoseIdentityWindowsCouldNotRead()=>OnUi(async ()=>
    {
        // Another Windows user's client: the monitor reports it as an identity-unavailable row and an incomplete scan.
        var control=new PriorityControlProbe();var monitor=new PriorityDesktopMonitor(2){Unreadable="other"};
        await using var shell=await ReadyPriority(monitor,control);
        Assert.Contains(shell.Sessions,row=>row.Observation.ProcessId==PriorityDesktopMonitor.UnreadablePid);
        shell.SelectedSession=shell.Sessions.First(row=>row.Observation.ProcessId==2);var expected=shell.SelectedSession.Observation;
        shell.SetClientsBelowNormalPriority.Execute(null);await shell.SetClientsBelowNormalPriority.Execution;
        Assert.Equal(1,control.ChangeCalls);
        Assert.Equal(expected,Assert.Single((await control.NextAsync()).Targets));
        Assert.Equal(expected.InstanceKey,Assert.Single(shell.ClientPriorityResults).Session.InstanceKey);
    });

    [Theory]
    [InlineData("same-pid",2)]
    [InlineData("stale",2)]
    [InlineData("no-problem",2)]
    [InlineData("other",127)]
    public Task PriorityStillRefusesAnUnreadableRowThatIsAmbiguousStaleMalformedOrAtTheScanBudget(string unreadable,int count)=>OnUi(async ()=>
    {
        var control=new PriorityControlProbe();var monitor=new PriorityDesktopMonitor(count);
        await using var shell=await ReadyPriority(monitor,control);
        monitor.Unreadable=unreadable;await Refresh(shell);
        shell.SelectedSession=shell.Sessions.First(row=>row.Observation.ProcessId==2);
        shell.SetClientsBelowNormalPriority.Execute(null);await shell.SetClientsBelowNormalPriority.Execution;
        Assert.Equal(0,control.ChangeCalls);Assert.Empty(shell.ClientPriorityResults);
        Assert.Contains("fresh scan",shell.ClientPriorityStatus,StringComparison.OrdinalIgnoreCase);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PriorityRefusesASelectedClientWhoseIdentityWindowsCouldNotReadAndSaysWhy(bool checkedRow)=>OnUi(async ()=>
    {
        var control=new PriorityControlProbe();var monitor=new PriorityDesktopMonitor(2){Unreadable="other"};
        await using var shell=await ReadyPriority(monitor,control);
        var unreadable=shell.Sessions.First(row=>row.Observation.ProcessId==PriorityDesktopMonitor.UnreadablePid);
        if(checkedRow){shell.Sessions[0].IsChecked=true;unreadable.IsChecked=true;}else shell.SelectedSession=unreadable;
        shell.SetClientsNormalPriority.Execute(null);await shell.SetClientsNormalPriority.Execution;
        Assert.Equal(0,control.ChangeCalls);Assert.Empty(shell.ClientPriorityResults);
        Assert.Equal("Windows could not read the identity of a selected client, for example one run by another Windows user, so nothing was changed. Uncheck or deselect that row, then try again.",
            shell.ClientPriorityStatus);
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
            await control.RestoreEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
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
            await control.RestoreEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
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

    // A pending restore covers every setting Zenith changed (priority, EcoQoS, memory priority, affinity), and Task Manager cannot
    // show or reset memory priority, so Exit's warning names each kind and says how memory priority comes back.
    [Theory]
    [InlineData(1,true,"Windows could not confirm that Zenith restored the original priority, efficiency, core or CPU cap settings of 1 client. "
        +"They last until you change them or the client exits. Review its priority, efficiency mode and affinity in Task Manager. "
        +"Task Manager cannot show or reset memory priority or a CPU cap; the client's own memory priority returns when it restarts, "
        +"and a CPU cap lasts until the client exits (Zenith names such clients when it next starts).")]
    [InlineData(2,true,"Windows could not confirm that Zenith restored the original priority, efficiency, core or CPU cap settings of 2 clients. "
        +"They last until you change them or the clients exit. Review their priority, efficiency mode and affinity in Task Manager. "
        +"Task Manager cannot show or reset memory priority or a CPU cap; each client's own memory priority returns when it restarts, "
        +"and a CPU cap lasts until that client exits (Zenith names such clients when it next starts).")]
    [InlineData(2,false,null)]
    public Task ExitPriorityWarningNamesEverySettingAPendingRestoreCoversAndHowMemoryPriorityReturns(int clients,bool restoreFails,string? expected)=>OnUi(async ()=>
    {
        var control=new PriorityControlProbe{RestoreFails=restoreFails};
        var shell=await ReadyPriority(new PriorityDesktopMonitor(2),control);
        try
        {
            for(int index=0;index<clients;index++)shell.Sessions[index].IsChecked=true;
            shell.SetClientsEfficiencyMode.Execute(null);await shell.SetClientsEfficiencyMode.Execution;
            Assert.Equal(clients,shell.PendingPriorityRestoreCount);
            Assert.Null(shell.ExitPriorityWarning);
        }
        finally{await shell.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));}
        Assert.Equal(restoreFails?clients:0,shell.PendingPriorityRestoreCount);
        if(restoreFails)
            Assert.Equal("Windows could not confirm that every original priority, efficiency, core or CPU cap setting was restored. "
                +"Review those clients in Task Manager; memory priority returns only when a client restarts, and a CPU cap lasts until that client exits.",shell.ClientPriorityStatus);
        Assert.Equal(expected,shell.ExitPriorityWarning);
    });

    [Fact]
    public Task ShellDisposeFinishesCleanupAndKeepsPendingRestoreWhenALaterOwnerDisposalThrows()=>OnUi(async ()=>
    {
        var control=new PriorityControlProbe{RestoreFails=true};var monitor=new PriorityDesktopMonitor(1);var signIn=new FailingSignInOwner();
        var shell=await ReadyPriority(monitor,control,signIn:signIn);shell.SelectedSession=shell.Sessions[0];
        shell.SetClientsBelowNormalPriority.Execute(null);await shell.SetClientsBelowNormalPriority.Execution;
        Assert.Equal(1,shell.PendingPriorityRestoreCount);
        var diagnostics=(ManagerDiagnostics)typeof(ShellViewModel).GetField("_diagnostics",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(shell)!;
        long errorsBefore=diagnostics.GetSnapshot().ErrorCount;

        var escaped=await Record.ExceptionAsync(()=>shell.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Null(escaped);
        Assert.Equal(1,signIn.Disposals);
        // The sign-in owner stops before the monitor, so the monitor's disposal proves the later owners still ran.
        Assert.True(monitor.Disposed,"The desktop monitor was not disposed after the sign-in owner failed to stop.");
        // Exit reads this after DisposeAsync to warn which client priorities Windows could not restore.
        Assert.Equal(1,shell.PendingPriorityRestoreCount);
        var snapshot=diagnostics.GetSnapshot();
        Assert.True(snapshot.ErrorCount>errorsBefore);
        Assert.Contains(snapshot.Events,row=>row.Kind==ManagerEventKind.OperationFailed && row.ErrorType=="InvalidOperationException");
        Assert.Equal(ManagerEventKind.Shutdown,snapshot.Events[^1].Kind);
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

    private async Task<ShellViewModel> ReadyPriority(PriorityDesktopMonitor monitor,PriorityControlProbe? control,bool sample=false,IRobloxSessionSignIn? signIn=null)
    {
        var capture=new PreviewProvider();
        var shell=new ShellViewModel(_root,sample,Dispatcher.CurrentDispatcher,monitor,
            new NoPublicDirectory(),new NoServerDirectory(),previewProvider:capture,signIn:signIn,clientPriority:control);
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
        ClientProcessPriorityAction.Efficiency=>shell.SetClientsEfficiencyMode,
        ClientProcessPriorityAction.SpreadCores=>shell.SpreadClientsOverCores,
        ClientProcessPriorityAction.CapCpu=>shell.CapClientsCpu,
        ClientProcessPriorityAction.RestoreOriginal=>shell.RestoreClientsPriority,
        _=>throw new ArgumentOutOfRangeException(nameof(action))
    };
    private static ClientProcessPriorityBatchResult PriorityReport(PriorityCall call)
    {
        bool restoring=call.Action==ClientProcessPriorityAction.RestoreOriginal;
        var requested=call.Action==ClientProcessPriorityAction.BelowNormal?ClientProcessPriorityClass.BelowNormal:ClientProcessPriorityClass.Normal;
        return new(call.Targets.Select(row=>new ClientProcessPriorityResult(row,call.Action,
            restoring?ClientProcessPriorityStatus.Restored:ClientProcessPriorityStatus.Applied,
            ClientProcessPriorityClass.Normal,requested,requested,!restoring)).ToArray(),false,restoring?0:call.Targets.Count);
    }
    private sealed record PriorityCall(IReadOnlyList<SessionObservation> Targets,ClientProcessPriorityAction Action,CancellationToken Token,int CpuCapPercent=0)
    {internal TaskCompletionSource<ClientProcessPriorityBatchResult> Reply {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);}
    private sealed class PriorityControlProbe:IClientProcessPriorityControl
    {
        private readonly Channel<PriorityCall> _calls=Channel.CreateBounded<PriorityCall>(4);
        private int _pending;
        internal bool DeferChange,DeferRestore,RestoreFails,ChangeCompleted;
        internal int ChangeCalls,RestoreCalls,CheckCalls;
        internal ClientCpuCapLeftovers Leftover=new(0,0,false,false);
        internal CancellationToken RestoreToken;
        internal ConcurrentQueue<string> Order {get;}=new();
        internal TaskCompletionSource DisposeEntered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RestoreEntered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RestoreRelease {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PendingRestoreCount=>_pending;
        public Task<ClientProcessPriorityBatchResult> ChangeAsync(IReadOnlyList<SessionObservation> sessions,ClientProcessPriorityAction action,CancellationToken ct)
        {
            if(action==ClientProcessPriorityAction.CapCpu)throw new ArgumentException("CapCpu goes through CapCpuAsync.",nameof(action));
            return RunAsync(new PriorityCall(Array.AsReadOnly(sessions.ToArray()),action,ct));
        }
        public Task<ClientProcessPriorityBatchResult> CapCpuAsync(IReadOnlyList<SessionObservation> sessions,int percentOfAllCpus,CancellationToken ct)
            =>RunAsync(new PriorityCall(Array.AsReadOnly(sessions.ToArray()),ClientProcessPriorityAction.CapCpu,ct,percentOfAllCpus));
        private async Task<ClientProcessPriorityBatchResult> RunAsync(PriorityCall call)
        {
            ChangeCalls++;
            Assert.True(_calls.Writer.TryWrite(call));
            var result=DeferChange?await call.Reply.Task:PriorityReport(call);
            _pending=result.PendingRestoreCount;ChangeCompleted=true;Order.Enqueue("change-terminal");return result;
        }
        public Task<ClientCpuCapLeftovers> CheckLeftoverCpuCapsAsync(CancellationToken ct){CheckCalls++;return Task.FromResult(Leftover);}
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
    private sealed class FailingSignInOwner:IRobloxSessionSignIn,IAsyncDisposable
    {
        internal int Disposals;
        public Task<RobloxSessionCredential> SignInAsync(Guid profileId,CancellationToken ct)=>throw new InvalidOperationException("No sign-in expected.");
        public ValueTask DisposeAsync(){Disposals++;throw new InvalidOperationException("Synthetic sign-in owner could not stop.");}
    }
    private sealed class PriorityDesktopMonitor(int count):IDesktopMonitor,IDisposable
    {
        internal string? Invalid;
        // Appends the row the real monitor emits for a process it cannot open (for example another Windows user's
        // client): PID only, start time 0, a Problem, and an inventory it reports as incomplete.
        internal string? Unreadable;
        internal const int UnreadablePid=900;
        internal bool Windowless,Disposed;
        public void Dispose()=>Disposed=true;
        private long _revision;
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();var now=DateTimeOffset.UtcNow;
            var at=Invalid=="stale-inventory"?now.AddSeconds(-10):Invalid=="future-inventory"?now.AddSeconds(10):now;
            var rows=Enumerable.Range(1,count).Select(pid=>new SessionObservation(Invalid=="duplicate-pid"?1:pid,
                new DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero).UtcTicks,"Synthetic",Windowless?0:pid+100,"Synthetic",Windowless,!Windowless,
                1,1,0,Invalid=="stale-row"?now.AddSeconds(-10):at)).ToList();
            if(Unreadable is not null)rows.Add(new SessionObservation(Unreadable=="same-pid"?1:UnreadablePid,0,"Unavailable",0,"Roblox",
                false,false,0,0,null,Unreadable=="stale"?now.AddSeconds(-10):at,
                Unreadable=="no-problem"?null:"Process identity or access was unavailable during observation."));
            return Task.FromResult(new DesktopSnapshot(new(now,32*CapacityPolicy.GiB,20*CapacityPolicy.GiB,
                64*CapacityPolicy.GiB,20*CapacityPolicy.GiB,10),rows.ToArray(),Invalid=="problem"?"Synthetic incomplete scan":null,
                at,Invalid=="unversioned"?0:Interlocked.Increment(ref _revision),Invalid!="partial" && Unreadable is null));
        }
        public Task<bool> FocusAsync(SessionObservation session,CancellationToken ct)=>throw new InvalidOperationException("No window mutation expected.");
        public Task<bool> RequestCloseAsync(SessionObservation session,CancellationToken ct)=>throw new InvalidOperationException("No process action expected.");
    }
}
