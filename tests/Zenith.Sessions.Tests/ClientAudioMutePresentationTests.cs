using System.IO;
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
    [InlineData(ClientAudioMuteAction.Mute)]
    [InlineData(ClientAudioMuteAction.Unmute)]
    public Task SoundCommandsPassOnlyThePinnedCheckedClientsAndShowEachResult(ClientAudioMuteAction action)=>OnUi(async ()=>
    {
        var control=new SoundControlProbe();var monitor=new SoundDesktopMonitor{Count=3};
        await using var shell=await ReadySound(monitor,control);
        shell.SelectedSession=shell.Sessions[1];shell.Sessions[0].IsChecked=true;shell.Sessions[2].IsChecked=true;
        var expected=new[]{shell.Sessions[0].Observation,shell.Sessions[2].Observation};
        var command=action==ClientAudioMuteAction.Mute?shell.MuteClients:shell.UnmuteClients;
        Assert.True(shell.CanChangeClientSound);
        command.Execute(null);await command.Execution;
        var call=await control.NextAsync();
        Assert.Equal(action,call.Action);Assert.Equal(expected,call.Targets);
        Assert.Equal(expected.Select(row=>row.InstanceKey),shell.ClientSoundResults.Select(row=>row.Session.InstanceKey));
        Assert.Equal("2 of 2 sound outcomes confirmed. 0 muted by Zenith and not yet unmuted.",shell.ClientSoundStatus);
        Assert.False(shell.IsChangingClientSound);Assert.True(shell.CanChangeClientSound);
    });

    [Fact]
    public Task ArrivalMuteIsOffByDefaultSoANewClientIsNotTouched()=>OnUi(async ()=>
    {
        var control=new SoundControlProbe();var monitor=new SoundDesktopMonitor{Count=1};
        await using var shell=await ReadySound(monitor,control);
        Assert.False(shell.MuteArrivingClients);
        monitor.Count=2;await Refresh(shell);await Refresh(shell);
        Assert.Equal(0,control.ChangeCalls);Assert.Empty(shell.ClientSoundResults);
    });

    [Fact]
    public Task ArrivalMuteMutesOnlyAClientThatAppearsAfterItIsSavedAndWaitsForItsSound()=>OnUi(async ()=>
    {
        var control=new SoundControlProbe{NoSoundCalls=1};var monitor=new SoundDesktopMonitor{Count=1};
        await using var shell=await ReadySound(monitor,control);
        shell.MuteArrivingClients=true;shell.SaveSettings.Execute(null);await shell.SaveSettings.Execution;
        await Refresh(shell);
        Assert.Equal(0,control.ChangeCalls); // The client already running is left alone.

        monitor.Count=2;await Refresh(shell);
        var first=await control.NextAsync();
        Assert.Equal(ClientAudioMuteAction.Mute,first.Action);
        Assert.Equal(2,Assert.Single(first.Targets).ProcessId);
        await Until(()=>!shell.IsChangingClientSound);
        Assert.Empty(shell.ClientSoundResults); // No sound yet: it waits for a later scan instead of reporting.

        await Refresh(shell);
        var second=await control.NextAsync();
        Assert.Equal(2,Assert.Single(second.Targets).ProcessId);
        await Until(()=>shell.ClientSoundResults.Count==1);
        Assert.Equal(ClientAudioMuteStatus.Applied,shell.ClientSoundResults[0].Status);
        Assert.Contains("1 of 1 new clients muted",shell.ClientSoundStatus);

        await Refresh(shell);await Refresh(shell);
        Assert.Equal(2,control.ChangeCalls); // Muted once; never again.
        Assert.Contains("\"MuteArrivingClients\":true",File.ReadAllText(Path.Combine(_root,"settings.json")));
    });

    [Fact]
    public Task ArrivalMuteLeavesARunningClientAloneWhenOneScanCouldNotReadIt()=>OnUi(async ()=>
    {
        var control=new SoundControlProbe();var monitor=new SoundDesktopMonitor{Count=2};
        await using var shell=await ReadySound(monitor,control);
        shell.MuteArrivingClients=true;shell.SaveSettings.Execute(null);await shell.SaveSettings.Execution;
        await Refresh(shell);
        // One scan cannot read client 2, which keeps running; the next scans read it again.
        monitor.Unreadable=2;await Refresh(shell);
        monitor.Unreadable=0;await Refresh(shell);await Refresh(shell);
        Assert.Equal(0,control.ChangeCalls); // The same running client is not an arrival.
        Assert.Empty(shell.ClientSoundResults);
        // A client that really appears afterwards is still muted.
        monitor.Count=3;await Refresh(shell);
        Assert.Equal(3,Assert.Single((await control.NextAsync()).Targets).ProcessId);
    });

    [Fact]
    public Task ArrivalMuteLeavesAloneAClientTheFirstScanCouldNotRead()=>OnUi(async ()=>
    {
        // Zenith starts with the preference already saved on, and its first scan cannot read client 2, which is already running.
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root,"settings.json"),"""{"Version":1,"MuteArrivingClients":true}""");
        var control=new SoundControlProbe();var monitor=new SoundDesktopMonitor{Count=2,Unreadable=2};
        await using var shell=await ReadySound(monitor,control);
        Assert.True(shell.MuteArrivingClients);
        monitor.Unreadable=0;await Refresh(shell);await Refresh(shell);
        Assert.Equal(0,control.ChangeCalls); // It was running before Zenith started, so it is not an arrival.
        Assert.Empty(shell.ClientSoundResults);
        // A client that really appears afterwards is still muted.
        monitor.Count=3;await Refresh(shell);
        Assert.Equal(3,Assert.Single((await control.NextAsync()).Targets).ProcessId);
    });

    [Fact]
    public Task ArrivalMuteLeavesAloneAClientThatAppearedUnreadBeforeItWasSavedOn()=>OnUi(async ()=>
    {
        var control=new SoundControlProbe();var monitor=new SoundDesktopMonitor{Count=1};
        await using var shell=await ReadySound(monitor,control);
        // Client 2 appears while the preference is off, and no scan can read it before the preference is saved on.
        monitor.Count=2;monitor.Unreadable=2;await Refresh(shell);
        shell.MuteArrivingClients=true;shell.SaveSettings.Execute(null);await shell.SaveSettings.Execution;
        await Refresh(shell);
        monitor.Unreadable=0;await Refresh(shell);await Refresh(shell);
        Assert.Equal(0,control.ChangeCalls); // It was already running when the preference was saved on.
        Assert.Empty(shell.ClientSoundResults);
        // A client that appears while watching is still muted once a scan reads it, even when the first scan to list it cannot.
        monitor.Count=3;monitor.Unreadable=3;await Refresh(shell);
        Assert.Equal(0,control.ChangeCalls);
        monitor.Unreadable=0;await Refresh(shell);
        Assert.Equal(3,Assert.Single((await control.NextAsync()).Targets).ProcessId);
        await Until(()=>shell.ClientSoundResults.Count==1);
        Assert.Equal(3,shell.ClientSoundResults[0].Session.ProcessId);
    });

    [Fact]
    public Task ArrivalMuteKeepsWaitingForANewClientThroughAScanThatCouldNotReadIt()=>OnUi(async ()=>
    {
        // Windows never lists this client's sound, so every try says "no sound yet" and only the 2-minute window, on the shell's
        // clock, can end the wait. The shell's own sampling loop scans too, so no step here depends on how many tries a phase made.
        var clock=new Clock();var control=new SoundControlProbe{NoSoundCalls=int.MaxValue};var monitor=new SoundDesktopMonitor{Count=1};
        await using var shell=await ReadySound(monitor,control,clock:clock);
        shell.MuteArrivingClients=true;shell.SaveSettings.Execute(null);await shell.SaveSettings.Execution;
        monitor.Count=2;await Refresh(shell); // First seen now.
        Assert.Equal(2,Assert.Single((await control.NextAsync()).Targets).ProcessId);
        await Until(()=>!shell.IsChangingClientSound);
        monitor.Unreadable=2;
        Assert.Empty(shell.ClientSoundResults); // No sound yet: it waits.
        // A scan that cannot read it neither tries it nor gives up on it.
        int tries=control.ChangeCalls;await Refresh(shell);await Refresh(shell);
        Assert.Equal(tries,control.ChangeCalls);
        // It kept its first-seen time through that scan: at the very end of its window it is tried again and still waits...
        clock.Advance(ShellViewModel.ArrivalMuteWindow);
        monitor.Unreadable=0;await Refresh(shell);
        Assert.Equal(2,Assert.Single((await control.NextAsync()).Targets).ProcessId);
        await Until(()=>!shell.IsChangingClientSound);
        Assert.Empty(shell.ClientSoundResults);
        // ...and one second past it the next "no sound yet" is final: the client is reported as never muted. Had the unreadable scan
        // dropped it and the next scan listed it as new again, its window would have restarted and it would still be waiting here.
        clock.Advance(TimeSpan.FromSeconds(1));
        await Refresh(shell);
        await Until(()=>shell.ClientSoundResults.Count==1);
        Assert.Equal(ClientAudioMuteStatus.NoAudioSession,shell.ClientSoundResults[0].Status);
        Assert.Equal(2,shell.ClientSoundResults[0].Session.ProcessId);
        Assert.Equal("0 of 1 new clients muted as they appeared. Unmute or Exit turns the sound Zenith muted on again.",shell.ClientSoundStatus);
        tries=control.ChangeCalls;await Refresh(shell);await Refresh(shell);
        Assert.Equal(tries,control.ChangeCalls); // Given up on: never tried again.
    });

    [Theory]
    [InlineData("panic")]
    [InlineData("lock")]
    [InlineData("cancel")]
    public Task SoundStopCancelsTheActiveChangeAndPausesArrivalMutingUntilItIsResumed(string stop)=>OnUi(async ()=>
    {
        var control=new SoundControlProbe{Defer=true};var monitor=new SoundDesktopMonitor{Count=1};
        await using var shell=await ReadySound(monitor,control);
        shell.MuteArrivingClients=true;shell.SaveSettings.Execute(null);await shell.SaveSettings.Execution;
        Assert.False(shell.IsArrivalMutePaused);Assert.False(shell.CanResumeArrivalMute);Assert.Equal("",shell.ArrivalMuteStatus);
        monitor.Count=2;await Refresh(shell);
        var call=await control.NextAsync();
        Assert.True(shell.IsChangingClientSound);Assert.False(shell.CanChangeClientSound);
        if(stop=="lock")shell.LockDashboard();else if(stop=="panic")shell.Panic.Execute(null);else shell.CancelClientSound.Execute(null);
        Assert.True(call.Token.IsCancellationRequested);Assert.Equal(1,control.CancelCalls);
        call.Reply.TrySetResult(SoundReport(call,ClientAudioMuteStatus.NoAudioSession));
        Assert.True(shell.IsArrivalMutePaused);Assert.Contains("paused",shell.ArrivalMuteStatus);
        await Until(()=>!shell.IsChangingClientSound);
        if(stop=="lock"){Assert.False(shell.CanResumeArrivalMute);shell.Unlock.Execute(null);}
        control.Defer=false;
        // Neither the waiting arrival nor a client that appears after the stop is muted: no automatic restart (Rule 11).
        monitor.Count=3;await Refresh(shell);await Refresh(shell);
        Assert.Equal(1,control.ChangeCalls);Assert.True(shell.IsArrivalMutePaused);
        // Saving the unchanged preference is not a restart either.
        shell.SaveSettings.Execute(null);await shell.SaveSettings.Execution;
        monitor.Count=4;await Refresh(shell);
        Assert.Equal(1,control.ChangeCalls);Assert.True(shell.IsArrivalMutePaused);

        Assert.True(shell.CanResumeArrivalMute);
        shell.ResumeArrivalMute.Execute(null);
        Assert.False(shell.IsArrivalMutePaused);Assert.Equal("",shell.ArrivalMuteStatus);
        await Refresh(shell);
        Assert.Equal(1,control.ChangeCalls); // Clients that appeared while paused are already running, so they are left alone.
        monitor.Count=5;await Refresh(shell);
        Assert.Equal(5,Assert.Single((await control.NextAsync()).Targets).ProcessId);
    });

    [Fact]
    public Task AClickDuringAnArrivalRetryIsKeptAndReachesWindowsWhenTheRetryEnds()=>OnUi(()=>WithArrivalRetryRunning(async (shell,control,retry)=>
    {
        // The retry leaves Mute and Unmute on, so a click made now is kept instead of dropped.
        Assert.True(shell.CanChangeClientSound);Assert.True(shell.IsChangingClientSound);
        shell.MuteClients.Execute(null);
        Assert.False(shell.CanChangeClientSound);
        // The click waits for the retry, so Windows still sees one sound operation at a time.
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(1,control.ChangeCalls);
        retry.Reply.TrySetResult(SoundReport(retry,ClientAudioMuteStatus.Applied));
        var click=await control.NextAsync();
        Assert.Equal(ClientAudioMuteAction.Mute,click.Action);Assert.Equal(1,Assert.Single(click.Targets).ProcessId);
        click.Reply.TrySetResult(SoundReport(click,ClientAudioMuteStatus.Applied));
        await shell.MuteClients.Execution;
        // The list shows the clicked operation's own results, not mixed with the retry's, but the retry's report is kept in the
        // status: the user is still told that a new client was muted while the click waited.
        var row=Assert.Single(shell.ClientSoundResults);
        Assert.Equal(1,row.Session.ProcessId);Assert.Equal(ClientAudioMuteStatus.Applied,row.Status);
        Assert.Equal("1 of 1 sound outcomes confirmed. 0 muted by Zenith and not yet unmuted. While this change waited, 1 of 1 new clients "
            +"muted as they appeared. Unmute or Exit turns the sound Zenith muted on again.",shell.ClientSoundStatus);
        Assert.False(shell.IsChangingClientSound);Assert.True(shell.CanChangeClientSound);
        // The retry's client was muted, so it is not tried again.
        await Refresh(shell);
        Assert.Equal(2,control.ChangeCalls);
        // The kept report is shown once: the next click's status is its own.
        shell.MuteClients.Execute(null);
        var again=await control.NextAsync();
        again.Reply.TrySetResult(SoundReport(again,ClientAudioMuteStatus.Applied));
        await shell.MuteClients.Execution;
        Assert.Equal("1 of 1 sound outcomes confirmed. 0 muted by Zenith and not yet unmuted.",shell.ClientSoundStatus);
    }));

    [Fact]
    public Task AClickStoppedAfterTheArrivalRetryMutedANewClientStillReportsThatMute()=>OnUi(()=>WithArrivalRetryRunning(async (shell,control,retry)=>
    {
        shell.MuteClients.Execute(null);
        retry.Reply.TrySetResult(SoundReport(retry,ClientAudioMuteStatus.Applied));
        var click=await control.NextAsync();
        Assert.Equal(1,Assert.Single(click.Targets).ProcessId);
        // Cancel sound work stops the click once it has reached Windows; the new client's mute, already final, is still reported.
        shell.CancelClientSound.Execute(null);
        Assert.True(click.Token.IsCancellationRequested);
        click.Reply.TrySetResult(SoundReport(click,ClientAudioMuteStatus.Cancelled));
        await shell.MuteClients.Execution;
        var row=Assert.Single(shell.ClientSoundResults);
        Assert.Equal(1,row.Session.ProcessId);Assert.Equal(ClientAudioMuteStatus.Cancelled,row.Status);
        Assert.Equal("Sound work stopped. Review the individual results below. While this change waited, 1 of 1 new clients muted as they "
            +"appeared. Unmute or Exit turns the sound Zenith muted on again.",shell.ClientSoundStatus);
        Assert.False(shell.IsChangingClientSound);Assert.True(shell.IsArrivalMutePaused);
    }));

    [Fact]
    public Task PanicStopsAClickThatIsWaitingForAnArrivalRetry()=>OnUi(()=>WithArrivalRetryRunning(async (shell,control,retry)=>
    {
        shell.MuteClients.Execute(null);
        shell.Panic.Execute(null);
        Assert.True(retry.Token.IsCancellationRequested);
        retry.Reply.TrySetResult(SoundReport(retry,ClientAudioMuteStatus.Applied));
        await shell.MuteClients.Execution;
        Assert.Equal(1,control.ChangeCalls); // The waiting click never reaches Windows after Panic (Rule 11).
        Assert.Empty(shell.ClientSoundResults);
        Assert.Equal("Sound work cancelled. A change Windows already accepted stays until Unmute or Exit.",shell.ClientSoundStatus);
        Assert.False(shell.IsChangingClientSound);Assert.True(shell.IsArrivalMutePaused);
    }));

    /// <summary>Arrival muting saved on, client 1 selected, and the arrival retry for new client 2 held inside the control.</summary>
    private async Task WithArrivalRetryRunning(Func<ShellViewModel,SoundControlProbe,SoundCall,Task> test)
    {
        var control=new SoundControlProbe{Defer=true};var monitor=new SoundDesktopMonitor{Count=1};
        await using var shell=await ReadySound(monitor,control);
        try
        {
            shell.MuteArrivingClients=true;shell.SaveSettings.Execute(null);await shell.SaveSettings.Execution;
            shell.SelectedSession=shell.Sessions.Single(row=>row.Observation.ProcessId==1);
            monitor.Count=2;await Refresh(shell);
            var retry=await control.NextAsync();
            Assert.Equal(2,Assert.Single(retry.Targets).ProcessId);
            await test(shell,control,retry);
        }
        finally{control.ReleaseHeld();} // A failed assertion must not leave Exit waiting for a held reply.
    }

    [Fact]
    public Task SavingArrivalMuteOnAfterItWasOffStartsItAgainAfterAStop()=>OnUi(async ()=>
    {
        var control=new SoundControlProbe();var monitor=new SoundDesktopMonitor{Count=1};
        await using var shell=await ReadySound(monitor,control);
        shell.MuteArrivingClients=true;shell.SaveSettings.Execute(null);await shell.SaveSettings.Execution;
        shell.Panic.Execute(null);
        Assert.True(shell.IsArrivalMutePaused);
        shell.MuteArrivingClients=false;shell.SaveSettings.Execute(null);await shell.SaveSettings.Execution;
        Assert.False(shell.IsArrivalMutePaused);Assert.Equal("",shell.ArrivalMuteStatus);
        shell.MuteArrivingClients=true;shell.SaveSettings.Execute(null);await shell.SaveSettings.Execution;
        Assert.False(shell.IsArrivalMutePaused);
        await Refresh(shell);
        monitor.Count=2;await Refresh(shell);
        Assert.Equal(2,Assert.Single((await control.NextAsync()).Targets).ProcessId);
    });

    [Fact]
    public Task ArrivalStatusCountsOnlyTheClientsZenithWillUnmute()=>OnUi(async ()=>
    {
        var control=new SoundControlProbe{AlreadyMutedPid=3};var monitor=new SoundDesktopMonitor{Count=1};
        await using var shell=await ReadySound(monitor,control);
        shell.MuteArrivingClients=true;shell.SaveSettings.Execute(null);await shell.SaveSettings.Execution;
        monitor.Count=3;await Refresh(shell);
        Assert.Equal(2,(await control.NextAsync()).Targets.Count);
        await Until(()=>shell.ClientSoundResults.Count==2);
        Assert.Equal("1 of 2 new clients muted as they appeared. Unmute or Exit turns the sound Zenith muted on again. "
            +"1 was already muted and stays muted on Exit.",shell.ClientSoundStatus);
    });

    [Fact]
    public Task ExitKeepsTheCountOfMutedClientsWindowsDidNotConfirmUnmutedForItsWarning()=>OnUi(async ()=>
    {
        // The real control over a fake Core Audio: its Exit unmute is refused, so its dispose throws ClientAudioMuteCleanupException.
        var native=new ClientAudioMuteTests.Native();native.Add(1,false);
        var monitor=new SoundDesktopMonitor{Count=1};
        native.Clients[1].Start=SoundDesktopMonitor.Start(1);
        var control=new WindowsClientAudioMuteControl(new NativeClientAudioMuteAdapter(native));
        var shell=await ReadySound(monitor,control);
        try
        {
            shell.SelectedSession=shell.Sessions[0];
            shell.MuteClients.Execute(null);await shell.MuteClients.Execution;
            Assert.Equal(ClientAudioMuteStatus.Applied,Assert.Single(shell.ClientSoundResults).Status);
            Assert.Equal(1,shell.PendingSoundRestoreCount);
            Assert.Null(shell.ExitSoundWarning);
            native.DenySet=true;
        }
        finally{await shell.DisposeAsync();}
        Assert.Equal(new[]{true},native.Clients[1].Sessions);
        Assert.Equal(1,shell.PendingSoundRestoreCount);
        Assert.Equal("Windows could not confirm that 1 client Zenith muted was unmuted. Turn its sound on in Volume mixer. "
            +"Until then, Windows may start later clients of the same program muted.",shell.ExitSoundWarning);
    });

    [Fact]
    public Task SoundSampleRefusesTheDefaultNativeControlEvenWhenCalledDirectly()=>OnUi(async ()=>
    {
        await using var shell=await ReadySound(new SoundDesktopMonitor{Count=1},null,sample:true);shell.SelectedSession=shell.Sessions[0];
        Assert.False(shell.CanChangeClientSound);
        shell.MuteClients.Execute(null);await shell.MuteClients.Execution;
        Assert.Empty(shell.ClientSoundResults);Assert.Equal(0,shell.PendingSoundRestoreCount);
    });

    private async Task<ShellViewModel> ReadySound(SoundDesktopMonitor monitor,IClientAudioMuteControl? control,bool sample=false,TimeProvider? clock=null)
    {
        var shell=new ShellViewModel(_root,sample,Dispatcher.CurrentDispatcher,monitor,new NoPublicDirectory(),new NoServerDirectory(),
            previewProvider:new PreviewProvider(),clientPriority:new PriorityControlProbe(),clientSound:control,idleClock:clock);
        try{await shell.InitializeAsync();await Refresh(shell);Assert.True(shell.IsReady,shell.Notice);return shell;}
        catch{await shell.DisposeAsync();throw;}
    }
    private static ClientAudioMuteBatchResult SoundReport(SoundCall call,ClientAudioMuteStatus status,int alreadyMutedPid=0)=>new(call.Targets.Select(row=>
        row.ProcessId==alreadyMutedPid?new ClientAudioMuteResult(row,call.Action,ClientAudioMuteStatus.Unchanged,true,true,false)
        :new ClientAudioMuteResult(row,call.Action,status,false,status==ClientAudioMuteStatus.Applied&&call.Action==ClientAudioMuteAction.Mute,
            status==ClientAudioMuteStatus.Applied&&call.Action==ClientAudioMuteAction.Mute)).ToArray(),false,0);
    private sealed record SoundCall(IReadOnlyList<SessionObservation> Targets,ClientAudioMuteAction Action,CancellationToken Token)
    {internal TaskCompletionSource<ClientAudioMuteBatchResult> Reply {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);}
    private sealed class SoundControlProbe:IClientAudioMuteControl
    {
        private readonly Channel<SoundCall> _calls=Channel.CreateBounded<SoundCall>(8);
        private readonly List<SoundCall> _held=[];
        internal bool Defer;
        /// <summary>The first this many calls report that Windows lists no sound for the clients yet; later calls apply.</summary>
        internal int NoSoundCalls;
        internal int ChangeCalls,CancelCalls,AlreadyMutedPid;
        public int PendingRestoreCount=>0;
        public async Task<ClientAudioMuteBatchResult> ChangeAsync(IReadOnlyList<SessionObservation> sessions,ClientAudioMuteAction action,CancellationToken ct)
        {
            ChangeCalls++;var call=new SoundCall(Array.AsReadOnly(sessions.ToArray()),action,ct);
            Assert.True(_calls.Writer.TryWrite(call));
            if(Defer){_held.Add(call);return await call.Reply.Task;}
            return SoundReport(call,ChangeCalls<=NoSoundCalls?ClientAudioMuteStatus.NoAudioSession:ClientAudioMuteStatus.Applied,AlreadyMutedPid);
        }
        internal Task<SoundCall> NextAsync()=>_calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        /// <summary>Answers every held call still waiting, as stopped, so a test that failed early can still exit.</summary>
        internal void ReleaseHeld(){foreach(var call in _held)call.Reply.TrySetResult(SoundReport(call,ClientAudioMuteStatus.Cancelled));}
        public void CancelOperations()=>CancelCalls++;
        public Task<ClientAudioMuteBatchResult> RestoreAllAsync(CancellationToken ct)=>Task.FromResult(new ClientAudioMuteBatchResult([],false,0));
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    /// <summary>
    /// A complete, fresh inventory of <see cref="Count"/> synthetic clients; raising Count makes a new client appear. A nonzero
    /// <see cref="Unreadable"/> PID is reported as WindowsDesktopMonitor reports a client it could not read: a row with a Problem
    /// and no start time, in an incomplete scan.
    /// </summary>
    private sealed class SoundDesktopMonitor:IDesktopMonitor
    {
        internal volatile int Count,Unreadable;
        private long _revision;
        internal static long Start(int pid)=>new DateTimeOffset(2026,1,1,0,0,pid,TimeSpan.Zero).UtcTicks;
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();var now=DateTimeOffset.UtcNow;int unreadable=Unreadable;
            var rows=Enumerable.Range(1,Count).Select(pid=>pid==unreadable
                ?new SessionObservation(pid,0,"Unavailable",0,"Roblox",false,false,0,0,null,now,
                    "Process identity or access was unavailable during observation.")
                :new SessionObservation(pid,Start(pid),"Synthetic",pid+100,"Synthetic",false,true,1,1,0,now)).ToArray();
            return Task.FromResult(new DesktopSnapshot(new(now,32*CapacityPolicy.GiB,20*CapacityPolicy.GiB,64*CapacityPolicy.GiB,20*CapacityPolicy.GiB,10),
                rows,null,now,Interlocked.Increment(ref _revision),unreadable==0));
        }
        public Task<bool> FocusAsync(SessionObservation session,CancellationToken ct)=>throw new InvalidOperationException("No window mutation expected.");
        public Task<bool> RequestCloseAsync(SessionObservation session,CancellationToken ct)=>throw new InvalidOperationException("No process action expected.");
    }
}
