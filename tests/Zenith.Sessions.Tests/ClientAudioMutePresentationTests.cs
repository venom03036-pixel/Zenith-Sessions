using System.IO;
using System.Threading.Channels;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

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

    [Theory]
    [InlineData("panic")]
    [InlineData("lock")]
    [InlineData("cancel")]
    public Task SoundStopCancelsTheActiveChangeAndDropsWaitingArrivals(string stop)=>OnUi(async ()=>
    {
        var control=new SoundControlProbe{Defer=true};var monitor=new SoundDesktopMonitor{Count=1};
        await using var shell=await ReadySound(monitor,control);
        shell.MuteArrivingClients=true;shell.SaveSettings.Execute(null);await shell.SaveSettings.Execution;
        monitor.Count=2;await Refresh(shell);
        var call=await control.NextAsync();
        Assert.True(shell.IsChangingClientSound);Assert.False(shell.CanChangeClientSound);
        if(stop=="lock")shell.LockDashboard();else if(stop=="panic")shell.Panic.Execute(null);else shell.CancelClientSound.Execute(null);
        Assert.True(call.Token.IsCancellationRequested);Assert.Equal(1,control.CancelCalls);
        call.Reply.TrySetResult(SoundReport(call,ClientAudioMuteStatus.NoAudioSession));
        await Until(()=>!shell.IsChangingClientSound);
        if(stop=="lock")shell.Unlock.Execute(null);
        await Refresh(shell);await Refresh(shell);
        Assert.Equal(1,control.ChangeCalls); // The waiting arrival was dropped; only later clients are watched.
    });

    [Fact]
    public Task SoundSampleRefusesTheDefaultNativeControlEvenWhenCalledDirectly()=>OnUi(async ()=>
    {
        await using var shell=await ReadySound(new SoundDesktopMonitor{Count=1},null,sample:true);shell.SelectedSession=shell.Sessions[0];
        Assert.False(shell.CanChangeClientSound);
        shell.MuteClients.Execute(null);await shell.MuteClients.Execution;
        Assert.Empty(shell.ClientSoundResults);Assert.Equal(0,shell.PendingSoundRestoreCount);
    });

    private async Task<ShellViewModel> ReadySound(SoundDesktopMonitor monitor,SoundControlProbe? control,bool sample=false)
    {
        var shell=new ShellViewModel(_root,sample,Dispatcher.CurrentDispatcher,monitor,new NoPublicDirectory(),new NoServerDirectory(),
            previewProvider:new PreviewProvider(),clientPriority:new PriorityControlProbe(),clientSound:control);
        try{await shell.InitializeAsync();await Refresh(shell);Assert.True(shell.IsReady,shell.Notice);return shell;}
        catch{await shell.DisposeAsync();throw;}
    }
    private static ClientAudioMuteBatchResult SoundReport(SoundCall call,ClientAudioMuteStatus status)=>new(call.Targets.Select(row=>
        new ClientAudioMuteResult(row,call.Action,status,false,status==ClientAudioMuteStatus.Applied?call.Action==ClientAudioMuteAction.Mute:false,false)).ToArray(),false,0);
    private sealed record SoundCall(IReadOnlyList<SessionObservation> Targets,ClientAudioMuteAction Action,CancellationToken Token)
    {internal TaskCompletionSource<ClientAudioMuteBatchResult> Reply {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);}
    private sealed class SoundControlProbe:IClientAudioMuteControl
    {
        private readonly Channel<SoundCall> _calls=Channel.CreateBounded<SoundCall>(8);
        internal bool Defer,NoSoundFirst;
        internal int ChangeCalls,CancelCalls;
        public int PendingRestoreCount=>0;
        public async Task<ClientAudioMuteBatchResult> ChangeAsync(IReadOnlyList<SessionObservation> sessions,ClientAudioMuteAction action,CancellationToken ct)
        {
            ChangeCalls++;var call=new SoundCall(Array.AsReadOnly(sessions.ToArray()),action,ct);
            Assert.True(_calls.Writer.TryWrite(call));
            if(Defer)return await call.Reply.Task;
            return SoundReport(call,NoSoundFirst && ChangeCalls==1?ClientAudioMuteStatus.NoAudioSession:ClientAudioMuteStatus.Applied);
        }
        internal Task<SoundCall> NextAsync()=>_calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public void CancelOperations()=>CancelCalls++;
        public Task<ClientAudioMuteBatchResult> RestoreAllAsync(CancellationToken ct)=>Task.FromResult(new ClientAudioMuteBatchResult([],false,0));
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    /// <summary>A complete, fresh inventory of <see cref="Count"/> synthetic clients; raising Count makes a new client appear.</summary>
    private sealed class SoundDesktopMonitor:IDesktopMonitor
    {
        internal volatile int Count;
        private long _revision;
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();var now=DateTimeOffset.UtcNow;
            var rows=Enumerable.Range(1,Count).Select(pid=>new SessionObservation(pid,new DateTimeOffset(2026,1,1,0,0,pid,TimeSpan.Zero).UtcTicks,
                "Synthetic",pid+100,"Synthetic",false,true,1,1,0,now)).ToArray();
            return Task.FromResult(new DesktopSnapshot(new(now,32*CapacityPolicy.GiB,20*CapacityPolicy.GiB,64*CapacityPolicy.GiB,20*CapacityPolicy.GiB,10),
                rows,null,now,Interlocked.Increment(ref _revision),true));
        }
        public Task<bool> FocusAsync(SessionObservation session,CancellationToken ct)=>throw new InvalidOperationException("No window mutation expected.");
        public Task<bool> RequestCloseAsync(SessionObservation session,CancellationToken ct)=>throw new InvalidOperationException("No process action expected.");
    }
}
