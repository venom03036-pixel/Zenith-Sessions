using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class OverviewWorkspaceIntegrationTests:IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-overview-ui-"+Guid.NewGuid().ToString("N"));
    [Fact]
    public Task FullLibraryTotalSurvivesPagingAndFilteringAndSameCountReloadIsDated()=>OnUi(async()=>
    {
        await Task.Run(async()=>
        {
            using var repository=new AccountRepository(_root);await repository.InitializeAsync(default);
            await repository.CreateManyAsync(Enumerable.Range(0,10_000).Select(index=>AccountProfile.Create($"Profile {index:D5}")).ToArray(),default);
        });
        await using var shell=Create(new Monitor(),false);Assert.Null(shell.Overview.SavedProfiles);
        await shell.InitializeAsync();Assert.True(shell.IsReady,shell.Notice);Assert.Equal(100,shell.Accounts.Count);Assert.Equal(10_000,shell.Overview.SavedProfiles);
        var loaded=shell.Overview.LibraryLoadedUtc;Assert.NotNull(loaded);
        shell.Search="Profile 09999";await Until(()=>shell.Accounts.Count==1 && shell.Overview.LibraryLoadedUtc>loaded);
        Assert.Equal(10_000,shell.Overview.SavedProfiles);Assert.Equal("Profile 09999",Assert.Single(shell.Accounts).Profile.Label);
        var filtered=shell.Overview.LibraryLoadedUtc;shell.Refresh.Execute(null);await shell.Refresh.Execution;
        Assert.True(shell.Overview.LibraryLoadedUtc>filtered);Assert.Equal(10_000,shell.Overview.SavedProfiles);
    });
    [Fact]
    public Task MonitorFailureIsExplicitAndManualRefreshDoesNotPretendToRestartTheLoop()=>OnUi(async()=>
    {
        var monitor=new Monitor {Fail=true};await using var shell=Create(monitor);
        await shell.InitializeAsync();await Until(()=>shell.Overview.ResourceCaption.Contains("monitoring stopped"));
        Assert.Null(shell.Overview.ObservedProcesses);monitor.Fail=false;
        shell.Refresh.Execute(null);await shell.Refresh.Execution;
        Assert.Contains("monitoring stopped",shell.Overview.ResourceCaption);Assert.Contains("does not restart",shell.Overview.ResourceSummary);
        Assert.Equal(8,shell.Overview.ObservedProcesses);int calls=monitor.Calls;
        await Task.Delay(1100);Assert.Equal(calls,monitor.Calls);
    });
    [Fact]
    public Task PresentationFaultDoesNotStopTheSafetySampler()=>OnUi(async()=>
    {
        var monitor=new Monitor();await using var shell=Create(monitor);
        await shell.InitializeAsync();await Until(()=>shell.Overview.ObservedProcesses==8);
        int thrown=0,presented=0;bool faulting=true;
        // ProcessInventoryStatus is assigned inside the sampler's presentation step and carries each sample's time;
        // its handler stands in for any display defect.
        shell.PropertyChanged+=(_,e)=>
        {
            if(e.PropertyName!=nameof(ShellViewModel.ProcessInventoryStatus)) return;
            if(faulting) {thrown++;throw new InvalidOperationException("Synthetic presentation fault.");}
            presented++;
        };
        int Reports()=>shell.Events.Count(text=>text.Contains("Display update failed",StringComparison.Ordinal));
        await Until(()=>thrown>=1);int calls=monitor.Calls;
        await Task.Delay(2500);
        Assert.True(monitor.Calls>calls+1,$"Sampling stopped after a presentation fault ({calls} -> {monitor.Calls} calls).");
        Assert.DoesNotContain("monitoring stopped",shell.Overview.ResourceCaption);Assert.Equal(8,shell.Overview.ObservedProcesses);
        Assert.True(thrown>=2,"The repeating fault was not retried on a later sample.");
        // A repeating fault is surfaced once, not every second, and the notice says what still works.
        Assert.Equal(1,Reports());Assert.Contains("Monitoring and launch safety checks continue",shell.Notice);
        faulting=false;await Until(()=>presented>=1);
        faulting=true;int before=thrown;await Until(()=>thrown>before && Reports()==2);
    });
    [Fact]
    public Task QueueStorageFailureMasksCachedCountsAndPreservesInterruptedMetadata()=>OnUi(async()=>
    {
        await using var shell=Create(new Monitor());await shell.InitializeAsync();await Until(()=>shell.Overview.QueuedRequests==0);
        string pending=Path.Combine(_root,"launch-queue.json.tmp");Assert.False(File.Exists(pending));byte[] original=Encoding.UTF8.GetBytes("Synthetic interrupted metadata.");
        await File.WriteAllBytesAsync(pending,original);
        try
        {
            shell.ToggleQueuePause.Execute(null);await shell.ToggleQueuePause.Execution;
            await Until(()=>!shell.QueueReady && shell.Overview.QueuedRequests is null);
            Assert.Contains("unavailable",shell.Overview.QueueCaption,StringComparison.OrdinalIgnoreCase);Assert.Equal(original,await File.ReadAllBytesAsync(pending));
        }
        finally {File.Delete(pending);}
    });
    [Fact]
    public Task QueueSnapshotReceivedBeforeReadinessUpdatesTheFooterWhenReadinessCompletes()=>OnUi(async()=>
    {
        await using var shell=Create(new Monitor());
        var snapshot=new LaunchQueueSnapshot(true,[OverviewWorkspaceProjectionTests.Operation(LaunchState.Queued),OverviewWorkspaceProjectionTests.Operation(LaunchState.Blocked)]);
        Invoke(shell,"QueueChanged",snapshot);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(shell.QueueReady);Assert.Contains("Opening",shell.QueueStatus);Assert.Null(shell.Overview.QueuedRequests);
        // Deterministically complete the real readiness setter after its captured snapshot,
        // without opening services or depending on timing between startup continuations.
        typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.QueueReady))!.SetValue(shell,true);
        Assert.StartsWith("Paused",shell.QueueStatus);Assert.Contains("1 queued",shell.QueueStatus);Assert.Contains("1 blocked",shell.QueueStatus);
        Assert.DoesNotContain("Opening",shell.QueueStatus);Assert.Equal(1,shell.Overview.QueuedRequests);Assert.StartsWith("Queue paused",shell.Overview.QueueCaption);
    });
    [Fact]
    public Task FooterCountsCapacityWaitingRowsApartFromBlocksThatNeedAction()=>OnUi(async()=>
    {
        await using var shell=Create(new Monitor());
        var waiting=OverviewWorkspaceProjectionTests.Operation(LaunchState.Blocked) with {Problem=LaunchProblem.InsufficientCapacity};
        Invoke(shell,"QueueChanged",new LaunchQueueSnapshot(true,[waiting]));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.QueueReady))!.SetValue(shell,true);
        Assert.Contains("0 queued · 1 waiting for capacity · ",shell.QueueStatus);Assert.Contains(" · 0 blocked · ",shell.QueueStatus);
        Assert.Equal(0,shell.Overview.QueuedRequests);
        Assert.Contains("1 waiting for capacity",shell.Overview.QueueSummary);Assert.Contains(" · 0 blocked · ",shell.Overview.QueueSummary);
    });
    [Fact]
    public Task QueuePausedByRepeatedRefusalsSaysWhyOnceAndKeepsTheFooterCounts()=>OnUi(async()=>
    {
        await using var shell=Create(new Monitor());
        var queued=OverviewWorkspaceProjectionTests.Operation(LaunchState.Queued);
        string reason=LaunchDescriptions.ExplainPause(LaunchQueuePauseReason.RepeatedRefusals);
        Invoke(shell,"QueueChanged",new LaunchQueueSnapshot(true,[queued]) {PauseReason=LaunchQueuePauseReason.RepeatedRefusals});
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.QueueReady))!.SetValue(shell,true);
        Assert.Equal(reason,shell.Notice);Assert.Single(shell.Events,line=>line.EndsWith(reason,StringComparison.Ordinal));
        Assert.Contains("refused",reason);Assert.Contains("stay queued",reason);
        Assert.StartsWith("Paused · 1 queued",shell.QueueStatus);Assert.Equal("Resume queue",shell.QueuePauseLabel);
        // A later save with the same reason does not repeat the event; a resume clears it for next time.
        Invoke(shell,"QueueChanged",new LaunchQueueSnapshot(true,[queued]) {PauseReason=LaunchQueuePauseReason.RepeatedRefusals});
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Single(shell.Events,line=>line.EndsWith(reason,StringComparison.Ordinal));
        Invoke(shell,"QueueChanged",new LaunchQueueSnapshot(false,[queued]));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Invoke(shell,"QueueChanged",new LaunchQueueSnapshot(true,[queued]) {PauseReason=LaunchQueuePauseReason.RepeatedRefusals});
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(2,shell.Events.Count(line=>line.EndsWith(reason,StringComparison.Ordinal)));
    });
    [Fact]
    public Task RefusalBackoffPacingIsNamedAsALongerWaitAfterARefusedStart()=>OnUi(async()=>
    {
        await using var shell=Create(new Monitor());
        var queue=Field<LaunchCoordinator>(shell,"_launchQueue");
        typeof(LaunchCoordinator).GetMethod("BeginPacing",BindingFlags.NonPublic|BindingFlags.Instance)!
            .Invoke(queue,[Guid.NewGuid(),TimeSpan.FromSeconds(4.5),LaunchPacingReason.RefusalBackoff]);
        typeof(ShellViewModel).GetMethod("RefreshLaunchEvidence",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(shell,null);
        Assert.StartsWith("Waiting 5 s before the next start because the last start was refused.",shell.LaunchPacingStatus);
        Assert.Contains("up to 1 minute",shell.LaunchPacingStatus);
        await Task.CompletedTask;
    });
    [Fact]
    public Task PanicFencesOldQueueCallbacksAndSupersedesAnActuallyAwaitedResume()=>OnUi(async()=>
    {
        await using var shell=Create(new Monitor());await shell.InitializeAsync();await Until(()=>shell.Overview.QueuedRequests==0);
        var queue=Field<LaunchCoordinator>(shell,"_launchQueue");var serial=Field<SemaphoreSlim>(queue,"_serial");
        var emissions=new ConcurrentQueue<LaunchQueueSnapshot>();queue.Changed+=emissions.Enqueue;
        bool held=await serial.WaitAsync(TimeSpan.FromSeconds(5));Assert.True(held);
        try
        {
            shell.ToggleQueuePause.Execute(null);Task resume=shell.ToggleQueuePause.Execution;Assert.False(resume.IsCompleted);
            shell.Panic.Execute(null);Assert.Contains("paused",shell.Overview.QueueCaption,StringComparison.OrdinalIgnoreCase);
            // Offer an old unpaused UI publication while the real queue owner is still gated.
            Invoke(shell,"QueueChanged",new LaunchQueueSnapshot(false,[]));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("Resume queue",shell.QueuePauseLabel);Assert.StartsWith("Paused",shell.QueueStatus);Assert.Contains("paused",shell.Overview.QueueCaption,StringComparison.OrdinalIgnoreCase);
            serial.Release();held=false;await resume.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(()=>!emissions.IsEmpty);Assert.All(emissions,snapshot=>Assert.True(snapshot.Paused));
            Assert.True(Field<bool>(queue,"_paused"));Assert.Equal("Resume queue",shell.QueuePauseLabel);Assert.Contains("paused",shell.Overview.QueueCaption,StringComparison.OrdinalIgnoreCase);
            while(emissions.TryDequeue(out _)) { }
            shell.ToggleQueuePause.Execute(null);await shell.ToggleQueuePause.Execution;
            await Until(()=>shell.Overview.QueueCaption.StartsWith("Queue active",StringComparison.Ordinal));
            Assert.False(Field<bool>(queue,"_paused"));Assert.Contains(emissions,snapshot=>!snapshot.Paused);Assert.Equal("Pause queue",shell.QueuePauseLabel);
        }
        finally {if(held) serial.Release();queue.Changed-=emissions.Enqueue;}
    });
    [Fact]
    public Task OverviewNavigationAndQueueExpansionDoNotResumeCaptureOrDispatchAndRespectLock()=>OnUi(async()=>
    {
        var directory=new NoDirectory();var preview=new NoPreview();await using var shell=Create(new Monitor(),true,directory,preview);
        await shell.InitializeAsync();await Until(()=>shell.Overview.QueuedRequests==0);shell.SetPreviewWindowVisible(true);
        shell.ShowWindowsOverview.Execute(null);Assert.True(shell.IsWindows);Assert.False(shell.ClientPreviews.IsRunning);
        shell.Navigate.Execute("Overview");shell.OpenOverviewQueue.Execute(null);Assert.True(shell.QueueExpanded);Assert.Equal("Resume queue",shell.QueuePauseLabel);
        shell.QueueExpanded=false;shell.LockDashboard();shell.OpenOverviewQueue.Execute(null);Assert.False(shell.QueueExpanded);
        Assert.Equal(0,directory.Calls);Assert.Equal(0,preview.Calls);Assert.Empty(shell.Launches);
    });
    [Fact]
    public Task SuccessfulPauseUpdatesItsNextToggleIntentBeforeBackgroundQueuePublication()=>OnUi(async()=>
    {
        await using var shell=Create(new Monitor());await shell.InitializeAsync();await Until(()=>shell.Overview.QueuedRequests==0);
        shell.ToggleQueuePause.Execute(null);await shell.ToggleQueuePause.Execution;await Until(()=>shell.Overview.QueueCaption.StartsWith("Queue active",StringComparison.Ordinal));
        var queue=Field<LaunchCoordinator>(shell,"_launchQueue");
        // Hold the coalesced background publication, while the actual queue/store owner runs.
        SetField(shell,"_queueUiScheduled",1);
        try
        {
            shell.ToggleQueuePause.Execute(null);await shell.ToggleQueuePause.Execution;
            Assert.True(Field<bool>(queue,"_paused"));Assert.Equal("Resume queue",shell.QueuePauseLabel);
            Assert.StartsWith("Paused",shell.QueueStatus);Assert.Contains("paused",shell.Overview.QueueCaption,StringComparison.OrdinalIgnoreCase);
            shell.ToggleQueuePause.Execute(null);await shell.ToggleQueuePause.Execution;
            Assert.False(Field<bool>(queue,"_paused"));Assert.Equal("Pause queue",shell.QueuePauseLabel);Assert.StartsWith("Queue active",shell.Overview.QueueCaption);
        }
        finally
        {
            SetField(shell,"_queueUiScheduled",0);Invoke(shell,"QueueChanged",Field<LaunchQueueSnapshot>(shell,"_latestQueue"));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    });
    private ShellViewModel Create(Monitor monitor,bool sample=true,NoDirectory? directory=null,NoPreview? preview=null)=>new(_root,sample,Dispatcher.CurrentDispatcher,monitor,directory??new(),new NoServers(),previewProvider:preview??new());
    private static T Field<T>(object target,string name)=>(T)(target.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(target)!);
    private static void SetField(object target,string name,object value)=>target.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(target,value);
    private static void Invoke(object target,string name,object value)=>target.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(target,[value]);
    private static Task OnUi(Func<Task> action)=>OverviewWorkspacePresentationTests.OnSta(action);
    private static async Task Until(Func<bool> condition)
    {var elapsed=Stopwatch.StartNew();while(!condition()){if(elapsed.Elapsed>TimeSpan.FromSeconds(6)) throw new TimeoutException("The owned Overview fixture did not publish the expected state.");await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);await Task.Delay(5);}}
    private sealed class Monitor:IDesktopMonitor
    {
        public bool Fail;public int Calls;private long _revision;
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();Interlocked.Increment(ref Calls);if(Fail) throw new IOException("Synthetic monitor failure.");var now=DateTimeOffset.UtcNow;
            var rows=Enumerable.Range(1,8).Select(pid=>new SessionObservation(pid,new DateTime(2026,1,1).Ticks,"Synthetic",0,"Synthetic",false,true,1,1,0,now)).ToArray();
            return Task.FromResult(new DesktopSnapshot(new(now,32*CapacityPolicy.GiB,20*CapacityPolicy.GiB,64*CapacityPolicy.GiB,20*CapacityPolicy.GiB,10),rows,ProcessObservedUtc:now,ProcessRevision:Interlocked.Increment(ref _revision),ProcessInventoryComplete:true));
        }
        public Task<bool> FocusAsync(SessionObservation row,CancellationToken ct)=>throw new InvalidOperationException("No native focus expected.");
        public Task<bool> RequestCloseAsync(SessionObservation row,CancellationToken ct)=>throw new InvalidOperationException("No native close expected.");
    }
    private sealed class NoDirectory:IPublicRobloxDirectory
    {
        public int Calls;
        public Task<PublicDirectoryResult<PublicRobloxUser>> LookupUserAsync(long id,CancellationToken ct){Calls++;throw new InvalidOperationException("No network expected.");}
        public Task<PublicDirectoryResult<PublicRobloxAvatar>> GetAvatarAsync(long id,CancellationToken ct){Calls++;throw new InvalidOperationException("No network expected.");}
        public Task<PublicDirectoryResult<EncodedPublicAvatar>> FetchAvatarAsync(PublicRobloxAvatar avatar,CancellationToken ct){Calls++;throw new InvalidOperationException("No network expected.");}
        public void ClearMemoryCache() { }
    }
    private sealed class NoServers:IRobloxPublicServerDirectory
    {public Task<RobloxPublicServerResult> BrowseAsync(RobloxPublicServerQuery query,CancellationToken ct)=>throw new InvalidOperationException("No network expected.");}
    private sealed class NoPreview:IClientPreviewProvider
    {
        public int Calls;
        public Task<IReadOnlyList<ClientPreviewResult>> CaptureAsync(IReadOnlyList<ClientPreviewTarget> targets,long generation,CancellationToken ct){Calls++;throw new InvalidOperationException("No capture expected.");}
        public Task ReleaseAsync(CancellationToken ct)=>Task.CompletedTask;public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    public void Dispose()
    {
        string full=Path.GetFullPath(_root);if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-overview-ui-",StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture root.");
        if(Directory.Exists(full)) Directory.Delete(full,true);
    }
}
