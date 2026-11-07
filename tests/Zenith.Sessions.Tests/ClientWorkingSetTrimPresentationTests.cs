using System.Threading.Channels;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientPreviewPresentationTests
{
    [Fact]
    public Task TrimNowPassesOnlyThePinnedCheckedProcessesAndShowsEachBeforeAndAfterResult()=>OnUi(async ()=>
    {
        var control=new TrimControlProbe();var monitor=new PriorityDesktopMonitor(3);
        await using var shell=await ReadyTrim(monitor,control);
        shell.SelectedSession=shell.Sessions[1];shell.Sessions[0].IsChecked=true;shell.Sessions[2].IsChecked=true;
        var expected=new[]{shell.Sessions[0].Observation,shell.Sessions[2].Observation};
        Assert.True(shell.CanTrimClients);
        shell.TrimClientsNow.Execute(null);await shell.TrimClientsNow.Execution;
        var call=await control.NextAsync();
        Assert.Equal(expected,call.Targets);
        Assert.Equal(expected.Select(row=>row.InstanceKey),shell.ClientTrimResults.Select(row=>row.Session.InstanceKey));
        Assert.All(shell.ClientTrimResults,row=>Assert.Contains("800 MiB before, 40 MiB about 10 s after",row.Explanation));
        Assert.Equal("2 of 2 clients trimmed. Commit is unchanged by a trim, and any benefit is unmeasured.",shell.ClientTrimStatus);
        Assert.False(shell.IsTrimmingClients);Assert.True(shell.CanTrimClients);
    });

    [Fact]
    public Task TrimRefusesAStaleSelectedInstanceWithoutCallingTheControl()=>OnUi(async ()=>
    {
        var control=new TrimControlProbe();var monitor=new PriorityDesktopMonitor(1);
        await using var shell=await ReadyTrim(monitor,control);
        var original=shell.Sessions[0];
        original.Update(original.Observation with{ProcessStartUtcTicks=original.Observation.ProcessStartUtcTicks-1});
        shell.SelectedSession=original;
        shell.TrimClientsNow.Execute(null);await shell.TrimClientsNow.Execution;
        Assert.Equal(0,control.TrimCalls);Assert.Empty(shell.ClientTrimResults);
        Assert.Contains("fresh scan",shell.ClientTrimStatus,StringComparison.OrdinalIgnoreCase);
    });

    [Theory]
    [InlineData("panic")]
    [InlineData("lock")]
    [InlineData("cancel")]
    public Task TrimStopCancelsTheActiveTrimAndKeepsItsReportedResults(string stop)=>OnUi(async ()=>
    {
        var control=new TrimControlProbe{Defer=true};var monitor=new PriorityDesktopMonitor(1);
        await using var shell=await ReadyTrim(monitor,control);shell.SelectedSession=shell.Sessions[0];
        shell.TrimClientsNow.Execute(null);var call=await control.NextAsync();
        Assert.True(shell.IsTrimmingClients);Assert.False(shell.CanTrimClients);
        if(stop=="lock")shell.LockDashboard();else if(stop=="panic")shell.Panic.Execute(null);else shell.CancelClientTrim.Execute(null);
        Assert.True(call.Token.IsCancellationRequested);Assert.Equal(1,control.CancelCalls);
        call.Reply.TrySetResult(TrimReport(call,cancelled:true));await shell.TrimClientsNow.Execution;
        Assert.Contains("No after reading",Assert.Single(shell.ClientTrimResults).Explanation);
        Assert.False(shell.IsTrimmingClients);
        Assert.Contains("stopped",shell.ClientTrimStatus,StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public Task TrimSampleRefusesTheDefaultNativeControlEvenWhenCalledDirectly()=>OnUi(async ()=>
    {
        await using var shell=await ReadyTrim(new PriorityDesktopMonitor(1),null,sample:true);shell.SelectedSession=shell.Sessions[0];
        Assert.False(shell.CanTrimClients);
        shell.TrimClientsNow.Execute(null);await shell.TrimClientsNow.Execution;
        Assert.Empty(shell.ClientTrimResults);
    });

    private async Task<ShellViewModel> ReadyTrim(PriorityDesktopMonitor monitor,TrimControlProbe? control,bool sample=false)
    {
        var shell=new ShellViewModel(_root,sample,Dispatcher.CurrentDispatcher,monitor,new NoPublicDirectory(),new NoServerDirectory(),
            previewProvider:new PreviewProvider(),clientPriority:new PriorityControlProbe(),clientTrim:control);
        try{await shell.InitializeAsync();await Refresh(shell);Assert.True(shell.IsReady,shell.Notice);return shell;}
        catch{await shell.DisposeAsync();throw;}
    }
    private static ClientWorkingSetTrimBatchResult TrimReport(TrimCall call,bool cancelled)=>new(call.Targets.Select(row=>
        new ClientWorkingSetTrimResult(row,ClientWorkingSetTrimStatus.Trimmed,new(800L<<20,1000L<<20),cancelled?null:new(40L<<20,1000L<<20))).ToArray(),cancelled);
    private sealed record TrimCall(IReadOnlyList<SessionObservation> Targets,CancellationToken Token)
    {internal TaskCompletionSource<ClientWorkingSetTrimBatchResult> Reply {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);}
    private sealed class TrimControlProbe:IClientWorkingSetTrimControl
    {
        private readonly Channel<TrimCall> _calls=Channel.CreateBounded<TrimCall>(4);
        internal bool Defer;
        internal int TrimCalls,CancelCalls;
        public async Task<ClientWorkingSetTrimBatchResult> TrimAsync(IReadOnlyList<SessionObservation> sessions,CancellationToken ct)
        {
            TrimCalls++;var call=new TrimCall(Array.AsReadOnly(sessions.ToArray()),ct);
            Assert.True(_calls.Writer.TryWrite(call));
            return Defer?await call.Reply.Task:TrimReport(call,false);
        }
        internal Task<TrimCall> NextAsync()=>_calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public void CancelOperations()=>CancelCalls++;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
