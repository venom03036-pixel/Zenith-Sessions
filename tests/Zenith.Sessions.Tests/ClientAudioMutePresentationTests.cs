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
        var control=new SoundControlProbe{NoSoundFirst=true};var monitor=new SoundDesktopMonitor{Count=1};
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
        Assert.True(File.ReadAllText(Path.Combine(_root,"settings.json")).Contains("\"MuteArrivingClients\":true"));
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
    public Task ArrivalMuteKeepsWaitingForANewClientThroughAScanThatCouldNotReadIt()=>OnUi(async ()=>
    {
        var control=new SoundControlProbe{NoSoundFirst=true};var monitor=new SoundDesktopMonitor{Count=1};
        await using var shell=await ReadySound(monitor,control);
        shell.MuteArrivingClients=true;shell.SaveSettings.Execute(null);await shell.SaveSettings.Execution;
        monitor.Count=2;await Refresh(shell);
        Assert.Equal(2,Assert.Single((await control.NextAsync()).Targets).ProcessId);
        await Until(()=>!shell.IsChangingClientSound);
        Assert.Empty(shell.ClientSoundResults); // No sound yet: it waits.
        // A scan that cannot read it neither tries it nor gives up on it.
        monitor.Unreadable=2;await Refresh(shell);
        Assert.Equal(1,control.ChangeCalls);
        monitor.Unreadable=0;await Refresh(shell);
        Assert.Equal(2,Assert.Single((await control.NextAsync()).Targets).ProcessId);
        await Until(()=>shell.ClientSoundResults.Count==1);
        Assert.Equal(ClientAudioMuteStatus.Applied,shell.ClientSoundResults[0].Status);
        Assert.Contains("1 of 1 new clients muted",shell.ClientSoundStatus);
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

    private async Task<ShellViewModel> ReadySound(SoundDesktopMonitor monitor,IClientAudioMuteControl? control,bool sample=false)
    {
        var shell=new ShellViewModel(_root,sample,Dispatcher.CurrentDispatcher,monitor,new NoPublicDirectory(),new NoServerDirectory(),
            previewProvider:new PreviewProvider(),clientPriority:new PriorityControlProbe(),clientSound:control);
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
        internal bool Defer,NoSoundFirst;
        internal int ChangeCalls,CancelCalls,AlreadyMutedPid;
        public int PendingRestoreCount=>0;
        public async Task<ClientAudioMuteBatchResult> ChangeAsync(IReadOnlyList<SessionObservation> sessions,ClientAudioMuteAction action,CancellationToken ct)
        {
            ChangeCalls++;var call=new SoundCall(Array.AsReadOnly(sessions.ToArray()),action,ct);
            Assert.True(_calls.Writer.TryWrite(call));
            if(Defer)return await call.Reply.Task;
            return SoundReport(call,NoSoundFirst && ChangeCalls==1?ClientAudioMuteStatus.NoAudioSession:ClientAudioMuteStatus.Applied,AlreadyMutedPid);
        }
        internal Task<SoundCall> NextAsync()=>_calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
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
