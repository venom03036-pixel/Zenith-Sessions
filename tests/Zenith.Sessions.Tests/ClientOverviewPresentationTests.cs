using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientPreviewPresentationTests
{
    [Theory]
    [InlineData(1)] [InlineData(4)] [InlineData(16)] [InlineData(128)]
    public Task OverviewListsEveryObservedClientWithoutStartingCapture(int count)=>OnUi(async()=>
    {
        var clock=new Clock();var provider=new PreviewProvider();
        await using var vm=new ClientPreviewsViewModel(provider,Dispatcher.CurrentDispatcher,()=>true,clock:clock);
        vm.ObserveInventory(Inventory(clock,count));await Until(()=>vm.OverviewRows.Count==count);
        Assert.Equal(0,provider.CaptureCalls);Assert.Empty(vm.Rows);Assert.False(vm.IsRunning);
        Assert.All(vm.OverviewRows,row=>{Assert.Null(row.Image);Assert.Contains("unverified",row.AutomationName);});
    });

    [Fact]
    public Task OverviewUnavailableClientsRemainTilesAndOversizeInventoryIsExplicitlyIncomplete()=>OnUi(async()=>
    {
        var clock=new Clock();var provider=new PreviewProvider();
        await using var vm=new ClientPreviewsViewModel(provider,Dispatcher.CurrentDispatcher,()=>true,clock:clock);
        var inventory=Inventory(clock,129);var rows=inventory.Sessions.ToArray();
        rows[0]=rows[0] with {Window=0};rows[1]=rows[1] with {Minimized=true};
        rows[2]=rows[2] with {Visible=false};rows[3]=rows[3] with {ProcessStartUtcTicks=0};
        vm.ObserveInventory(inventory with {Sessions=rows});await Until(()=>vm.OverviewRows.Count==128);
        Assert.Null(vm.OverviewRows[0].CaptureTarget);Assert.Null(vm.OverviewRows[3].CaptureTarget);
        Assert.Contains("minimized",vm.OverviewRows[1].Status);Assert.Contains("hidden",vm.OverviewRows[2].Status);
        Assert.Contains("incomplete",vm.InventoryStatus);Assert.Equal(0,provider.CaptureCalls);
    });

    [Fact]
    public Task AmbiguousWindowsAndMalformedStartTimesStayUnavailableWithoutFaultingThePresenter()=>OnUi(async()=>
    {
        var clock=new Clock();var provider=new PreviewProvider();
        await using var vm=new ClientPreviewsViewModel(provider,Dispatcher.CurrentDispatcher,()=>true,clock:clock);
        var inventory=Inventory(clock,3);var rows=inventory.Sessions.ToArray();
        rows[1]=rows[1] with {Window=rows[0].Window};rows[2]=rows[2] with {ProcessStartUtcTicks=long.MaxValue};
        vm.ObserveInventory(inventory with {Sessions=rows});await Until(()=>vm.OverviewRows.Count==3);
        Assert.All(vm.OverviewRows,row=>{Assert.Null(row.CaptureTarget);Assert.False(row.CanFocus);});
        vm.StartOverview(clock.GetUtcNow().AddMinutes(1));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.True(vm.IsAvailable);Assert.Equal(0,provider.CaptureCalls);Assert.Contains("incomplete",vm.InventoryStatus);
    });

    [Fact]
    public Task PartialAbsenceRetainsTilesButCompleteAbsenceRemovesSelectionWithoutClaimingExit()=>OnUi(async()=>
    {
        var clock=new Clock();var provider=new PreviewProvider();
        await using var vm=new ClientPreviewsViewModel(provider,Dispatcher.CurrentDispatcher,()=>true,clock:clock);
        var inventory=Inventory(clock,3);vm.ObserveInventory(inventory);await Until(()=>vm.OverviewRows.Count==3);
        var selected=vm.OverviewRows[2];vm.SelectedOverviewRow=selected;
        vm.ObserveInventory(inventory with {Sessions=inventory.Sessions.Take(1).ToArray(),ProcessRevision=2,ProcessInventoryComplete=false});
        await Until(()=>selected.Status.Contains("partial",StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3,vm.OverviewRows.Count);Assert.Same(selected,vm.SelectedOverviewRow);Assert.False(selected.CanFocus);
        Assert.Contains("does not prove exit",vm.InventoryStatus);
        vm.ObserveInventory(inventory with {Sessions=inventory.Sessions.Take(1).ToArray(),ProcessRevision=3});
        await Until(()=>vm.OverviewRows.Count==1);Assert.Null(vm.SelectedOverviewRow);Assert.Equal(0,provider.CaptureCalls);
    });

    [Theory]
    [InlineData("default")] [InlineData("future")] [InlineData("stale")] [InlineData("after-completion")]
    public Task MalformedRowTimesDowngradeCompleteInventoriesAndPreserveAbsentTiles(string malformed)=>OnUi(async()=>
    {
        var clock=new Clock();var provider=new PreviewProvider();
        await using var vm=new ClientPreviewsViewModel(provider,Dispatcher.CurrentDispatcher,()=>true,clock:clock);
        var inventory=Inventory(clock,3);vm.ObserveInventory(inventory);await Until(()=>vm.OverviewRows.Count==3);
        var absent=vm.OverviewRows[2];vm.SelectedOverviewRow=absent;var bad=inventory.Sessions[0];
        bad=bad with {ObservedUtc=malformed switch {"default"=>default,"future"=>clock.GetUtcNow().AddSeconds(1),"stale"=>clock.GetUtcNow().AddSeconds(-11),_=>clock.GetUtcNow()}};
        var completed=malformed=="after-completion"?clock.GetUtcNow().AddSeconds(-1):clock.GetUtcNow();
        vm.ObserveInventory(inventory with {Sessions=new[]{bad,inventory.Sessions[1] with {ObservedUtc=completed}},ProcessRevision=2,ProcessObservedUtc=completed});
        await Until(()=>vm.InventoryStatus.Contains("incomplete") && !vm.OverviewRows[0].CanFocus);
        Assert.Equal(3,vm.OverviewRows.Count);Assert.Contains(absent,vm.OverviewRows);Assert.Same(absent,vm.SelectedOverviewRow);
        Assert.False(absent.CanFocus);Assert.Contains("last-known",absent.Status);Assert.Equal(0,provider.CaptureCalls);
    });

    [Theory]
    [InlineData("start")] [InlineData("window")]
    public Task ChangedOverviewIdentityNeverInheritsTheOldImageOrSelection(string change)=>OnUi(async()=>
    {
        var clock=new Clock();var provider=new PreviewProvider();
        await using var vm=new ClientPreviewsViewModel(provider,Dispatcher.CurrentDispatcher,()=>true,clock:clock);
        var inventory=Inventory(clock,1);vm.ObserveInventory(inventory);await Until(()=>vm.OverviewRows.Count==1);
        vm.StartOverview(clock.GetUtcNow().AddMinutes(3));var call=await provider.NextAsync();
        call.Reply.SetResult(Frames(call,clock.GetUtcNow()));await DriveUntil(clock,()=>vm.OverviewRows[0].HasImage);
        var old=vm.OverviewRows[0];vm.SelectedOverviewRow=old;var row=inventory.Sessions[0];
        row=change=="start"?row with {ProcessStartUtcTicks=row.ProcessStartUtcTicks+1}:row with {Window=row.Window+1};
        vm.ObserveInventory(inventory with {Sessions=new[]{row},ProcessRevision=2});
        await DriveUntil(clock,()=>!ReferenceEquals(vm.OverviewRows[0],old));
        Assert.Null(old.Image);Assert.Null(vm.SelectedOverviewRow);Assert.Null(vm.OverviewRows[0].Image);
    });

    [Fact]
    public Task OverviewRosterGrowthKeepsUnchangedTileImagesAndNeverClaimsPreviewsAreOff()=>OnUi(async()=>
    {
        var clock=new Clock();var provider=new PreviewProvider();
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var vm=new ClientPreviewsViewModel(provider,Dispatcher.CurrentDispatcher,()=>true,clock:clock);
        try
        {
            vm.ObserveInventory(Inventory(clock,2));await Until(()=>vm.OverviewRows.Count==2);
            vm.StartOverview(clock.GetUtcNow().AddMinutes(3));var call=await provider.NextAsync();
            call.Reply.SetResult(Frames(call,clock.GetUtcNow()));await DriveUntil(clock,()=>vm.OverviewRows.All(row=>row.HasImage));
            var first=vm.OverviewRows[0].Image;var second=vm.OverviewRows[1].Image;
            // A third client appears: Core releases the previous sources but keeps both exact frames.
            provider.ReleaseGate=release.Task;vm.ObserveInventory(Inventory(clock,3,2));
            await DriveUntil(clock,()=>vm.IsReleasing && vm.OverviewRows.Count==3);
            Assert.Same(first,vm.OverviewRows[0].Image);Assert.Same(second,vm.OverviewRows[1].Image);
            Assert.Contains("not current",vm.OverviewRows[0].Freshness);
            Assert.DoesNotContain("Previews are off",vm.RefreshSummary);
            release.SetResult();await provider.NextReleaseAsync();
            await DriveUntil(clock,()=>vm.IsRunning);
            Assert.Same(first,vm.OverviewRows[0].Image);Assert.Same(second,vm.OverviewRows[1].Image);
        }
        finally {release.TrySetResult();provider.UnblockAll();}
    });

    [Fact]
    public Task OverviewReusesFrozenBitmapsWhenAnotherBatchChangesTheSnapshot()=>OnUi(async()=>
    {
        var clock=new Clock();var provider=new PreviewProvider();
        await using var vm=new ClientPreviewsViewModel(provider,Dispatcher.CurrentDispatcher,()=>true,clock:clock);
        vm.ObserveInventory(Inventory(clock,8));await Until(()=>vm.OverviewRows.Count==8);
        vm.StartOverview(clock.GetUtcNow().AddMinutes(3));var first=await provider.NextAsync();
        first.Reply.SetResult(Frames(first,clock.GetUtcNow()));await DriveUntil(clock,()=>vm.OverviewRows.Take(4).All(row=>row.HasImage));
        var firstImages=vm.OverviewRows.Take(4).Select(row=>row.Image).ToArray();Assert.All(firstImages,image=>Assert.True(image!.IsFrozen));
        clock.Advance(TimeSpan.FromSeconds(1));var second=await provider.NextAsync();
        Assert.DoesNotContain(second.Targets,target=>first.Targets.Contains(target));
        second.Reply.SetResult(Frames(second,clock.GetUtcNow()));await DriveUntil(clock,()=>vm.OverviewRows.All(row=>row.HasImage));
        for(int i=0;i<4;i++) Assert.Same(firstImages[i],vm.OverviewRows[i].Image);
        Assert.Equal(8,vm.OverviewRows.Count);Assert.Empty(vm.Rows);
    });

    [Fact]
    public Task OverviewRetainsOriginalCaptureAgeThenRemovesExpiredPixelsWithoutRedatingThem()=>OnUi(async()=>
    {
        var clock=new Clock();var provider=new PreviewProvider();
        await using var vm=new ClientPreviewsViewModel(provider,Dispatcher.CurrentDispatcher,()=>true,clock:clock);
        vm.ObserveInventory(Inventory(clock,1));await Until(()=>vm.OverviewRows.Count==1);
        vm.StartOverview(clock.GetUtcNow().AddMinutes(5));var call=await provider.NextAsync();
        var capturedAt=clock.GetUtcNow();
        call.Reply.SetResult(Frames(call,capturedAt));await DriveUntil(clock,()=>vm.OverviewRows[0].HasImage);
        var image=vm.OverviewRows[0].Image;string captured=vm.OverviewRows[0].Captured;
        clock.Advance(TimeSpan.FromSeconds(4));vm.ObserveInventory(Inventory(clock,1,2));
        await DriveUntil(clock,()=>vm.OverviewRows[0].Freshness.Contains("Last-known")
            && vm.OverviewRows[0].ImageAge==$"Captured {(long)(clock.GetUtcNow()-capturedAt).TotalSeconds} s ago.");
        Assert.Same(image,vm.OverviewRows[0].Image);Assert.Equal(captured,vm.OverviewRows[0].Captured);
        // Driving the asynchronous presenter can advance its controlled clock before
        // publication. Check against the original frame time, not scheduling speed.
        long age=(long)(clock.GetUtcNow()-capturedAt).TotalSeconds;
        Assert.True(age>=4);Assert.Equal($"Captured {age} s ago.",vm.OverviewRows[0].ImageAge);
        Assert.Contains("not measured",vm.OverviewRows[0].RefreshRate);
        clock.Advance(TimeSpan.FromSeconds(121));vm.ObserveInventory(Inventory(clock,1,3));
        await DriveUntil(clock,()=>!vm.OverviewRows[0].HasImage);Assert.Single(vm.OverviewRows);
    });

    [Fact]
    public Task StaleProcessInventoryStopsOverviewAndFreshObservationsNeverAutoResume()=>OnUi(async()=>
    {
        var clock=new Clock();var provider=new PreviewProvider();
        await using var vm=new ClientPreviewsViewModel(provider,Dispatcher.CurrentDispatcher,()=>true,clock:clock);
        vm.ObserveInventory(Inventory(clock,1));await Until(()=>vm.OverviewRows.Count==1);
        vm.StartOverview(clock.GetUtcNow().AddMinutes(5));await provider.NextAsync();
        clock.Advance(TimeSpan.FromSeconds(11));await DriveUntil(clock,()=>!vm.IsRunning);
        Assert.Single(vm.OverviewRows);Assert.Null(vm.OverviewRows[0].Image);int calls=provider.CaptureCalls;
        vm.ObserveInventory(Inventory(clock,1,2));vm.RenewResourceLease(clock.GetUtcNow().AddMinutes(1));
        // Fence can publish !IsRunning while source cleanup is still in the presenter's
        // active250ms clock delay. Drive that same controlled clock when new inventory arrives.
        await DriveUntil(clock,()=>vm.OverviewRows[0].CanFocus);Assert.False(vm.IsRunning);Assert.Equal(calls,provider.CaptureCalls);
    });

    [Fact]
    public Task DeferredRefreshMarksRetainedRecentPixelsLastKnownBeforeThreeSeconds()=>OnUi(async()=>
    {
        var clock=new Clock();var provider=new PreviewProvider();
        await using var vm=new ClientPreviewsViewModel(provider,Dispatcher.CurrentDispatcher,()=>true,clock:clock);
        vm.ObserveInventory(Inventory(clock,1));await Until(()=>vm.OverviewRows.Count==1);
        vm.StartOverview(clock.GetUtcNow().AddMinutes(1));var first=await provider.NextAsync();
        first.Reply.SetResult(Frames(first,clock.GetUtcNow()));await DriveUntil(clock,()=>vm.OverviewRows[0].HasImage);
        var image=vm.OverviewRows[0].Image;clock.Advance(TimeSpan.FromSeconds(1));var deferred=await provider.NextAsync();
        deferred.Reply.SetResult(deferred.Targets.Select(target=>new ClientPreviewResult(target,ClientPreviewStatus.Deferred,null,"Untrusted text.")).ToArray());
        await DriveUntil(clock,()=>vm.OverviewRows[0].Freshness.Contains("Last-known"));
        Assert.Same(image,vm.OverviewRows[0].Image);Assert.Contains("not current",vm.OverviewRows[0].Freshness);
        Assert.DoesNotContain("3s",vm.OverviewRows[0].CompactAge);Assert.Contains("Captured",vm.OverviewRows[0].AutomationName);
    });

    [Fact]
    public Task MeasuredOverviewRefreshRequiresTwoDistinctCapturedFrames()=>OnUi(async()=>
    {
        var clock=new Clock();var provider=new PreviewProvider();
        await using var vm=new ClientPreviewsViewModel(provider,Dispatcher.CurrentDispatcher,()=>true,clock:clock);
        vm.ObserveInventory(Inventory(clock,1));await Until(()=>vm.OverviewRows.Count==1);
        vm.StartOverview(clock.GetUtcNow().AddMinutes(1));var first=await provider.NextAsync();
        first.Reply.SetResult(Frames(first,clock.GetUtcNow()));await DriveUntil(clock,()=>vm.OverviewRows[0].HasImage);
        Assert.Contains("not measured",vm.OverviewRows[0].RefreshRate);var original=vm.OverviewRows[0].Image;
        clock.Advance(TimeSpan.FromSeconds(1));var second=await provider.NextAsync();
        second.Reply.SetResult(Frames(second,clock.GetUtcNow()));await DriveUntil(clock,()=>!ReferenceEquals(original,vm.OverviewRows[0].Image));
        Assert.Contains("Measured refresh",vm.OverviewRows[0].RefreshRate);Assert.DoesNotContain("FPS",vm.OverviewRows[0].RefreshRate);
        Assert.True(vm.OverviewRows[0].Image!.IsFrozen);
    });

    [Fact]
    public Task CompositorIntervalMeasuresOneSecondDespiteASixtyOneSecondWallClockJump()=>OnUi(()=>
    {
        var now=new DateTimeOffset(2026,11,6,12,0,0,TimeSpan.Zero);
        var observation=new SessionObservation(1,new DateTime(2026,1,1).Ticks,"Synthetic",101,"Synthetic",false,true,1,1,0,now);
        var row=new ClientOverviewRow(observation);row.Observe(observation,true);
        var target=row.CaptureTarget!;byte[] pixels=[0,0,0,255];
        var image=BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,pixels,4);image.Freeze();
        var first=new ClientPreviewFrame(target,1,1,1,4,pixels,now,1,1,TimeSpan.TicksPerSecond);
        row.Publish(image,first,"Optional image.",now,true);Assert.Contains("not measured",row.RefreshRate);
        var later=now.AddSeconds(61);row.Observe(observation with {ObservedUtc=later},true);
        row.Publish(image,first with {CapturedUtc=later,SurfaceTimestampTicks=2*TimeSpan.TicksPerSecond},"Optional image.",later,true);
        Assert.Contains("1.0 s between images",row.RefreshRate);Assert.Contains("1.00/s",row.RefreshRate);
        Assert.DoesNotContain("61.0",row.RefreshRate);Assert.Equal("0s",row.CompactAge);
        return Task.CompletedTask;
    });

    [Fact]
    public Task PartialAbsenceImmediatelyMarksRetainedRecentPixelsLastKnown()=>OnUi(async()=>
    {
        var clock=new Clock();var provider=new PreviewProvider();
        await using var vm=new ClientPreviewsViewModel(provider,Dispatcher.CurrentDispatcher,()=>true,clock:clock);
        var inventory=Inventory(clock,1);vm.ObserveInventory(inventory);await Until(()=>vm.OverviewRows.Count==1);
        vm.StartOverview(clock.GetUtcNow().AddMinutes(1));var first=await provider.NextAsync();
        first.Reply.SetResult(Frames(first,clock.GetUtcNow()));await DriveUntil(clock,()=>vm.OverviewRows[0].HasImage);
        var image=vm.OverviewRows[0].Image;
        vm.ObserveInventory(inventory with {Sessions=Array.Empty<SessionObservation>(),ProcessRevision=2,ProcessInventoryComplete=false});
        await DriveUntil(clock,()=>!vm.OverviewRows[0].CanFocus);
        Assert.Same(image,vm.OverviewRows[0].Image);Assert.Contains("Last-known",vm.OverviewRows[0].Freshness);Assert.Single(vm.OverviewRows);
        await DriveUntil(clock,()=>!vm.IsReleasing);Assert.Same(image,vm.OverviewRows[0].Image);
        Assert.False(vm.OverviewRows[0].CanFocus);vm.Fence("Explicit stop.");Assert.Null(vm.OverviewRows[0].Image);
    });

    [Fact]
    public Task PartialScanRetainedImageExpiresAfterItsOriginalOneHundredTwentySecondLifetime()=>OnUi(async()=>
    {
        var clock=new Clock();var provider=new PreviewProvider();
        await using var vm=new ClientPreviewsViewModel(provider,Dispatcher.CurrentDispatcher,()=>true,clock:clock);
        var inventory=Inventory(clock,1);vm.ObserveInventory(inventory);await Until(()=>vm.OverviewRows.Count==1);
        vm.StartOverview(clock.GetUtcNow().AddMinutes(5));var first=await provider.NextAsync();
        first.Reply.SetResult(Frames(first,clock.GetUtcNow()));await DriveUntil(clock,()=>vm.OverviewRows[0].HasImage);
        vm.ObserveInventory(Inventory(clock,0,2) with {ProcessInventoryComplete=false});
        await DriveUntil(clock,()=>!vm.OverviewRows[0].CanFocus);Assert.True(vm.OverviewRows[0].HasImage);
        clock.Advance(TimeSpan.FromSeconds(121));vm.ObserveInventory(Inventory(clock,0,3) with {ProcessInventoryComplete=false});
        await DriveUntil(clock,()=>!vm.OverviewRows[0].HasImage);
        Assert.Single(vm.OverviewRows);Assert.False(vm.OverviewRows[0].CanFocus);Assert.Equal("—",vm.OverviewRows[0].CompactAge);
    });

    [Fact]
    public Task ReentrantStopDuringFirstOverviewImageCannotPublishLaterTiles()=>OnUi(async()=>
    {
        var clock=new Clock();var provider=new PreviewProvider();
        await using var vm=new ClientPreviewsViewModel(provider,Dispatcher.CurrentDispatcher,()=>true,clock:clock);
        vm.ObserveInventory(Inventory(clock,4));await Until(()=>vm.OverviewRows.Count==4);
        vm.OverviewRows[0].PropertyChanged+=(_,args)=>{if(args.PropertyName==nameof(ClientOverviewRow.Image) && vm.OverviewRows[0].HasImage) vm.Fence("Controlled stop.");};
        vm.StartOverview(clock.GetUtcNow().AddMinutes(1));var call=await provider.NextAsync();call.Reply.SetResult(Frames(call,clock.GetUtcNow()));
        await DriveUntil(clock,()=>!vm.IsRunning);Assert.All(vm.OverviewRows,row=>Assert.Null(row.Image));
        Assert.Equal("Controlled stop.",vm.Status);
    });

    [Fact]
    public Task OverviewDisposalJoinsTheActualIgnoringCaptureOwner()=>OnUi(async()=>
    {
        var provider=new PreviewProvider {IgnoreCancellation=true};var clock=new Clock();
        var vm=new ClientPreviewsViewModel(provider,Dispatcher.CurrentDispatcher,()=>true,clock:clock);
        CaptureCall? call=null;Task? disposal=null;
        try
        {
            vm.ObserveInventory(Inventory(clock,1));await Until(()=>vm.OverviewRows.Count==1);
            vm.StartOverview(clock.GetUtcNow().AddMinutes(1));call=await provider.NextAsync();
            disposal=vm.DisposeAsync().AsTask();await Task.Delay(15);Assert.False(disposal.IsCompleted);
            call.Reply.TrySetResult(Frames(call,clock.GetUtcNow()));await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(vm.OverviewRows,row=>Assert.Null(row.Image));Assert.Equal(1,provider.CaptureCalls);
        }
        finally {call?.Reply.TrySetResult([]);provider.UnblockAll();await (disposal??vm.DisposeAsync().AsTask());}
    });

    [Theory]
    [InlineData("hide")] [InlineData("tab")] [InlineData("lock")] [InlineData("panic")]
    public Task WindowsShellRouteSharesCaptureOwnerAndContextFences(string context)=>OnUi(async()=>
    {
        var provider=new PreviewProvider {AutoReady=true};var monitor=new DesktopMonitor(16);
        await using var shell=await Ready(monitor,provider);shell.SetPreviewWindowVisible(true);shell.ShowWindowsOverview.Execute(null);
        await Until(()=>shell.ClientPreviews.OverviewRows.Count==16);Assert.True(shell.IsWindows);Assert.Equal(0,provider.CaptureCalls);
        shell.StartWindowsOverview.Execute(null);await provider.NextAsync();await Until(()=>shell.ClientPreviews.OverviewRows.Any(row=>row.HasImage));
        Assert.All(shell.Sessions,row=>Assert.False(row.IsChecked));Assert.Null(shell.SelectedSession);
        switch(context) {case "hide":shell.SetPreviewWindowVisible(false);break;case "tab":shell.Navigate.Execute("Sessions");break;case "lock":shell.LockDashboard();break;default:shell.Panic.Execute(null);break;}
        Assert.All(shell.ClientPreviews.OverviewRows,row=>Assert.Null(row.Image));Assert.False(shell.ClientPreviews.IsRunning);
        await provider.NextReleaseAsync();int calls=provider.CaptureCalls;
        if(shell.IsLocked) shell.Unlock.Execute(null);shell.ShowWindowsOverview.Execute(null);shell.SetPreviewWindowVisible(true);await Refresh(shell);
        Assert.False(shell.ClientPreviews.IsRunning);Assert.Equal(calls,provider.CaptureCalls);
    });

    [Fact]
    public Task OverviewFocusPinsInspectedIdentityAndNeverChecksNativeRecipients()=>OnUi(async()=>
    {
        var monitor=new OverviewFocusMonitor();var provider=new PreviewProvider();
        await using var shell=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,monitor,new NoPublicDirectory(),new NoServerDirectory(),previewProvider:provider);
        try
        {
            await shell.InitializeAsync();await Refresh(shell);shell.SetPreviewWindowVisible(true);shell.ShowWindowsOverview.Execute(null);
            await Until(()=>shell.ClientPreviews.OverviewRows.Count==2);
            var pinned=shell.ClientPreviews.OverviewRows[0];shell.ClientPreviews.SelectedOverviewRow=pinned;
            Assert.True(shell.CanFocusOverviewClient);shell.FocusOverviewClient.Execute(null);
            await monitor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            shell.ClientPreviews.SelectedOverviewRow=shell.ClientPreviews.OverviewRows[1];monitor.Reply.TrySetResult(true);
            await shell.FocusOverviewClient.Execution;
            Assert.Equal(pinned.ProcessId,monitor.Pinned!.ProcessId);Assert.Equal(pinned.ProcessStartUtcTicks,monitor.Pinned.ProcessStartUtcTicks);Assert.Equal(pinned.Window,monitor.Pinned.Window);
            Assert.All(shell.Sessions,row=>Assert.False(row.IsChecked));Assert.Equal(0,provider.CaptureCalls);
        }
        finally {monitor.Reply.TrySetResult(false);}
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task LockOrPanicDuringIgnoringFocusPreservesStopNoticeAndJoinsOwnership(bool panic)=>OnUi(async()=>
    {
        var monitor=new OverviewFocusMonitor();var provider=new PreviewProvider();
        var shell=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,monitor,new NoPublicDirectory(),new NoServerDirectory(),previewProvider:provider);
        Task? disposal=null;
        try
        {
            await shell.InitializeAsync();await Refresh(shell);shell.SetPreviewWindowVisible(true);shell.ShowWindowsOverview.Execute(null);
            await Until(()=>shell.ClientPreviews.OverviewRows.Count==2);shell.ClientPreviews.SelectedOverviewRow=shell.ClientPreviews.OverviewRows[0];
            shell.FocusOverviewClient.Execute(null);await monitor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if(panic) shell.Panic.Execute(null);else shell.LockDashboard();string stopNotice=shell.Notice;
            Assert.True(monitor.Token.IsCancellationRequested);disposal=shell.DisposeAsync().AsTask();
            await Task.Delay(15);Assert.False(disposal.IsCompleted);monitor.Reply.TrySetResult(true);
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(stopNotice,shell.Notice);
        }
        finally {monitor.Reply.TrySetResult(false);await (disposal??shell.DisposeAsync().AsTask());}
    });

    [Fact]
    public Task SampleOverviewCannotCaptureOrFocusEvenWhenCommandsAreExecutedDirectly()=>OnUi(async()=>
    {
        var provider=new PreviewProvider();var monitor=new DesktopMonitor();
        await using var shell=await Ready(monitor,provider,true);shell.ShowWindowsOverview.Execute(null);shell.SetPreviewWindowVisible(true);
        await Until(()=>shell.ClientPreviews.OverviewRows.Count>0);shell.ClientPreviews.SelectedOverviewRow=shell.ClientPreviews.OverviewRows[0];
        shell.StartWindowsOverview.Execute(null);shell.FocusOverviewClient.Execute(null);await shell.FocusOverviewClient.Execution;
        Assert.False(shell.CanStartWindowsOverview);Assert.False(shell.CanFocusOverviewClient);Assert.Equal(0,provider.CaptureCalls);
    });

    private sealed class OverviewFocusMonitor:IDesktopMonitor
    {
        private long _revision;
        public SessionObservation? Pinned;public CancellationToken Token;
        public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Reply {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DesktopSnapshot> SampleAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();var now=DateTimeOffset.UtcNow;
            var rows=Enumerable.Range(1,2).Select(pid=>new SessionObservation(pid,new DateTime(2026,1,1).Ticks,"Synthetic",pid+100,"Synthetic",false,true,1,1,0,now)).ToArray();
            return Task.FromResult(new DesktopSnapshot(new(now,32*CapacityPolicy.GiB,20*CapacityPolicy.GiB,64*CapacityPolicy.GiB,20*CapacityPolicy.GiB,10),rows,ProcessObservedUtc:now,ProcessRevision:Interlocked.Increment(ref _revision),ProcessInventoryComplete:true));
        }
        public Task<bool> FocusAsync(SessionObservation session,CancellationToken ct)
        {Pinned=session;Token=ct;Entered.TrySetResult();return Reply.Task;}
        public Task<bool> RequestCloseAsync(SessionObservation session,CancellationToken ct)=>throw new InvalidOperationException("No close operation expected.");
    }

    private static DesktopSnapshot Inventory(Clock clock,int count,long revision=1)
    {
        var now=clock.GetUtcNow();var rows=Enumerable.Range(1,count).Select(pid=>new SessionObservation(pid,new DateTime(2026,1,1).Ticks,"Synthetic",pid+100,"Not an account",false,true,1,1,0,now)).ToArray();
        return new(new(now,32*CapacityPolicy.GiB,20*CapacityPolicy.GiB,64*CapacityPolicy.GiB,20*CapacityPolicy.GiB,10),rows,ProcessObservedUtc:now,ProcessRevision:revision,ProcessInventoryComplete:true);
    }
    private static IReadOnlyList<ClientPreviewResult> Frames(CaptureCall call,DateTimeOffset at)=>call.Targets.Select(target=>
        new ClientPreviewResult(target,ClientPreviewStatus.Ready,new(target,call.Generation,320,180,1280,new byte[230400],at,1920,1080,call.Sequence*TimeSpan.TicksPerSecond),"Untrusted provider text.")).ToArray();
}
