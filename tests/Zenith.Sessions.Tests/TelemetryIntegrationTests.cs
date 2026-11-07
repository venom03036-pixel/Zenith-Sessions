using System.IO;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class TelemetryIntegrationTests:IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-telemetry-integration-"+Guid.NewGuid().ToString("N"));
    [Fact]
    public Task HistorySelectionIsDiagnosticAndStopsPreviewWithoutRetargeting() => OnUi(async()=>
    {
        var provider=new PreviewProvider();var monitor=new Monitor();
        await using var shell=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,monitor,previewProvider:provider);
        await shell.InitializeAsync();Assert.True(shell.IsReady,shell.Notice);
        shell.Refresh.Execute(null);await shell.Refresh.Execution; // Join the initial async sample before selecting its row.
        shell.Navigate.Execute("Sessions");shell.SelectedSession=Assert.Single(shell.Sessions);
        var selected=shell.SelectedSession;
        shell.ShowClientPreviewMode.Execute(null);shell.SetPreviewWindowVisible(true);
        Assert.True(shell.CanStartClientPreviews);
        shell.StartClientPreviews.Execute(null);await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        shell.ShowClientHistoryMode.Execute(null);
        Assert.True(shell.IsClientHistoryMode);Assert.False(shell.IsClientPreviewMode);Assert.False(shell.IsClientTableMode);
        Assert.False(shell.ClientPreviews.IsRunning);Assert.Same(selected,shell.SelectedSession);
        await provider.Released.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Single(shell.Telemetry.ClientCpuPoints);Assert.Equal(25,Assert.Single(shell.Telemetry.ClientCpuPoints).Value);
        shell.ShowClientTableMode.Execute(null);
        Assert.True(shell.IsClientTableMode);Assert.False(shell.IsClientHistoryMode);Assert.False(shell.IsClientPreviewMode);
        Assert.Same(selected,shell.SelectedSession);Assert.Equal(1,provider.Captures);
    });

    [Fact]
    public Task CachedDesktopRefreshDoesNotRenewOrRebuildHistory() => OnUi(async()=>
    {
        var provider=new PreviewProvider();var monitor=new Monitor();
        await using var shell=new ShellViewModel(_root,true,Dispatcher.CurrentDispatcher,monitor,previewProvider:provider);
        await shell.InitializeAsync();Assert.True(shell.IsReady,shell.Notice);
        shell.Refresh.Execute(null);await shell.Refresh.Execution;
        shell.Navigate.Execute("Sessions");shell.SelectedSession=Assert.Single(shell.Sessions);shell.ShowClientHistoryMode.Execute(null);
        var cpu=shell.Telemetry.CpuPoints;var client=shell.Telemetry.ClientCpuPoints;
        Assert.Single(cpu);Assert.Single(client);
        shell.Refresh.Execute(null);await shell.Refresh.Execution;
        Assert.Same(cpu,shell.Telemetry.CpuPoints);Assert.Same(client,shell.Telemetry.ClientCpuPoints);
        Assert.Equal(monitor.Frame.Resources.ObservedUtc,cpu[0].ObservedUtc);Assert.Equal(0,provider.Captures);
    });

    private sealed class Monitor:IDesktopMonitor
    {
        private readonly Lazy<DesktopSnapshot> _frame=new(CreateFrame);
        public DesktopSnapshot Frame=>_frame.Value;
        private static DesktopSnapshot CreateFrame()
        {
            var at=DateTimeOffset.UtcNow;
            // Freeze one actual sample, not the time before16-profile startup. Subsequent
            // calls deliberately return the exact cached object and original timestamps.
            return new(new(at,32*CapacityPolicy.GiB,20*CapacityPolicy.GiB,64*CapacityPolicy.GiB,20*CapacityPolicy.GiB,10),
                [new(77,at.AddMinutes(-10).UtcTicks,"Synthetic",123,"Synthetic",false,true,1024,2048,25,at,
                    IoRates:new(at,1024,2048,0,1,2,0))],ProcessObservedUtc:at,ProcessRevision:1,ProcessInventoryComplete:true);
        }
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct) {ct.ThrowIfCancellationRequested();return Task.FromResult(Frame);}
        public Task<bool> FocusAsync(SessionObservation s,CancellationToken ct)=>throw new InvalidOperationException("No desktop action expected.");
        public Task<bool> RequestCloseAsync(SessionObservation s,CancellationToken ct)=>throw new InvalidOperationException("No desktop action expected.");
    }
    private sealed class PreviewProvider:IClientPreviewProvider
    {
        public readonly TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously),Released=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Captures;
        public async Task<IReadOnlyList<ClientPreviewResult>> CaptureAsync(IReadOnlyList<ClientPreviewTarget> targets,long generation,CancellationToken ct)
        {Interlocked.Increment(ref Captures);Started.TrySetResult();await Task.Delay(Timeout.InfiniteTimeSpan,ct);return [];}
        public Task ReleaseAsync(CancellationToken ct) {Released.TrySetResult();return Task.CompletedTask;}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private static Task OnUi(Func<Task> action)
    {
        var completed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async()=>
            {try{await action();completed.TrySetResult();}catch(Exception error){completed.TrySetException(error);}finally{dispatcher.InvokeShutdown();}});
            Dispatcher.Run();
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
    public void Dispose() {if(Directory.Exists(_root)) Directory.Delete(_root,true);}
}
