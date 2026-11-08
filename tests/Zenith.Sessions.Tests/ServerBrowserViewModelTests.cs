using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class ServerBrowserViewModelTests
{
    private const long Place=6564161046;
    private static readonly string Job=Guid.Parse("9a073cb7-73b1-42bd-844b-83a09cbec848").ToString("D");
    private static RobloxPublicServer Server(DateTimeOffset? when=null)=>new(Place,Job,1,20,null,null,when??DateTimeOffset.UtcNow);
    [Fact]
    public async Task AnEmptySuccessfulResponseHasAnExplicitEmptyStateAndCannotSelectAServer()
    {
        var directory=new FakeDirectory();await using var vm=new ServerBrowserViewModel(directory);
        Assert.False(vm.HasDestination);Assert.False(vm.CanUseServer);
        vm.UsePreferredPlace(Place);vm.Browse.Execute(null);await vm.Browse.Execution;
        Assert.Empty(vm.Servers);Assert.Contains("No public servers were returned in this response",vm.Status);
        Assert.False(vm.CanUseServer);Assert.Equal(Place,vm.Destination!.PlaceId);
    }
    [Fact]
    public async Task APreviousRowCannotChangeDestinationWhileANewPageIsRunning()
    {
        var directory=new FakeDirectory {Result=new(RobloxPublicServerStatus.Complete,[Server()],null,null,"Synthetic page")};
        await using var vm=new ServerBrowserViewModel(directory);vm.UsePreferredPlace(Place);vm.Browse.Execute(null);await vm.Browse.Execution;
        vm.SelectedServer=Assert.Single(vm.Servers);Assert.True(vm.CanUseServer);
        directory.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);vm.Browse.Execute(null);
        Assert.False(vm.CanUseServer);vm.UseServer.Execute(null);Assert.Null(vm.Destination!.JobId);
        directory.Pending.SetResult(new(RobloxPublicServerStatus.Complete,[],null,null,"Synthetic empty page"));await vm.Browse.Execution;
        Assert.Null(vm.SelectedServer);Assert.False(vm.CanUseServer);
    }
    [Fact]
    public async Task ResolvingARequestedServerNeverContactsTheDirectoryOrRetainsTheOriginalLink()
    {
        var directory=new FakeDirectory();await using var vm=new ServerBrowserViewModel(directory);
        vm.LinkInput=$"https://www.roblox.com/games/start?placeId={Place}&gameInstanceId={Job}";vm.Resolve.Execute(null);
        Assert.Equal(Place,vm.Destination!.PlaceId);Assert.Equal(Job,vm.Destination.JobId);Assert.Equal("",vm.LinkInput);Assert.Empty(directory.Queries);
        vm.LinkInput=$"https://www.roblox.com/games/{Place}?privateServerLinkCode=synthetic-secret";vm.Resolve.Execute(null);
        Assert.Null(vm.Destination);Assert.Equal("",vm.LinkInput);Assert.DoesNotContain("synthetic-secret",vm.Status);Assert.Empty(directory.Queries);
    }
    [Fact]
    public async Task DraftBlocksServerActionsAndDiscardRestoresTheExplicitRequest()
    {
        var directory=new FakeDirectory {Result=new(RobloxPublicServerStatus.PageLimit,[Server()],"next",null,"Synthetic page")};
        await using var vm=new ServerBrowserViewModel(directory);vm.UsePreferredPlace(Place);
        vm.Browse.Execute(null);await vm.Browse.Execution;
        vm.SelectedServer=Assert.Single(vm.Servers);Assert.True(vm.CanUseServer);Assert.True(vm.CanNext);
        vm.LinkInput="  ";
        Assert.True(vm.HasDestination);Assert.True(vm.HasDestinationDraft);Assert.Null(vm.ReadyDestination);
        Assert.False(vm.CanUseServer);Assert.False(vm.CanNext);
        vm.UseServer.Execute(null);vm.Browse.Execute(null);await vm.Browse.Execution;
        vm.Next.Execute(null);await vm.Next.Execution;Assert.Single(directory.Queries);Assert.Null(vm.Destination!.JobId);
        vm.LinkInput="";
        Assert.False(vm.HasDestinationDraft);Assert.True(vm.CanUseDestination);Assert.True(vm.CanUseServer);Assert.True(vm.CanNext);
        vm.UseServer.Execute(null);Assert.Equal(Job,vm.ReadyDestination!.JobId);
    }
    [Theory]
    [InlineData("resolve")]
    [InlineData("preferred")]
    [InlineData("favorite")]
    [InlineData("clear")]
    public async Task DestinationReplacementPublishesAvailabilityAfterTheWholeCommit(string action)
    {
        var directory=new FakeDirectory();await using var vm=new ServerBrowserViewModel(directory);
        vm.UsePreferredPlace(Place);vm.LinkInput="42";
        var changes=new List<long?>();
        vm.PropertyChanged+=(_,args)=>
        {
            if(args.PropertyName is nameof(ServerBrowserViewModel.LinkInput) or nameof(ServerBrowserViewModel.Destination) or nameof(ServerBrowserViewModel.CanUseDestination))
                changes.Add(vm.ReadyDestination?.PlaceId);
        };
        switch(action)
        {
            case "resolve":vm.ResolveInput();break;
            case "preferred":vm.UsePreferredPlace(42);break;
            case "favorite":Assert.True(vm.UseFavoriteDestination(new(Guid.NewGuid(),"Example",42,null,1,Guid.NewGuid())));break;
            case "clear":vm.CancelWork(true);break;
        }
        Assert.Equal(new long?[]{null,null,action=="clear"?null:42L},changes);
        Assert.False(vm.HasDestinationDraft);Assert.Empty(directory.Queries);
    }
    [Fact]
    public async Task BrowsingIsExplicitOnePageAndSelectionRequiresADeliberateUseAction()
    {
        var directory=new FakeDirectory {Result=new(RobloxPublicServerStatus.PageLimit,[Server()],"next-cursor",null,"Synthetic public directory")};
        await using var vm=new ServerBrowserViewModel(directory);vm.UsePreferredPlace(Place);
        Assert.Empty(directory.Queries);vm.Browse.Execute(null);await vm.Browse.Execution;
        var row=Assert.Single(vm.Servers);vm.SelectedServer=row;Assert.Null(vm.Destination!.JobId);
        vm.UseServer.Execute(null);Assert.Equal(Job,vm.Destination.JobId);Assert.True(vm.CanNext);
        vm.Next.Execute(null);await vm.Next.Execution;
        Assert.Equal("next-cursor",directory.Queries[1].Cursor);Assert.All(directory.Queries,q=>{Assert.Equal(25,q.PageSize);Assert.Equal(1,q.MaxPages);});
        Assert.Single(vm.Servers);
    }
    [Fact]
    public async Task QuickJoinNamesTheRequestedServerWheneverTheDestinationHasAJobId()
    {
        var directory=new FakeDirectory {Result=new(RobloxPublicServerStatus.Complete,[Server()],null,null,"Synthetic public directory")};
        await using var vm=new ServerBrowserViewModel(directory);
        var raised=new List<string?>();vm.PropertyChanged+=(_,args)=>raised.Add(args.PropertyName);
        vm.UsePreferredPlace(Place);
        Assert.Equal("Quick join · any server",vm.QuickJoinLabel);Assert.Contains("Roblox picks the server",vm.QuickJoinToolTip);
        // A checked friend invite names the inviter's exact server, so the launch button must not promise any server.
        raised.Clear();vm.UseInvitedServer(Place,Job);
        Assert.Equal("Join requested server",vm.QuickJoinLabel);
        Assert.DoesNotContain("any server",vm.QuickJoinToolTip);Assert.DoesNotContain("Roblox picks the server",vm.QuickJoinToolTip);
        Assert.Contains("requested server",vm.QuickJoinToolTip);Assert.Contains(nameof(vm.QuickJoinLabel),raised);Assert.Contains(nameof(vm.QuickJoinToolTip),raised);
        vm.UsePreferredPlace(Place);Assert.Equal("Quick join · any server",vm.QuickJoinLabel);
        // A pasted gameInstanceId link and a deliberately used public server are the same exact-server request.
        vm.LinkInput=$"https://www.roblox.com/games/start?placeId={Place}&gameInstanceId={Job}";vm.Resolve.Execute(null);
        Assert.Equal("Join requested server",vm.QuickJoinLabel);
        vm.UsePreferredPlace(Place);vm.Browse.Execute(null);await vm.Browse.Execution;vm.SelectedServer=Assert.Single(vm.Servers);
        raised.Clear();vm.UseServer.Execute(null);
        Assert.Equal(Job,vm.Destination!.JobId);Assert.Equal("Join requested server",vm.QuickJoinLabel);Assert.Contains(nameof(vm.QuickJoinLabel),raised);
        vm.CancelWork(true);Assert.Equal("Quick join · any server",vm.QuickJoinLabel);
    }
    [Fact]
    public async Task ChangingDestinationFencesACancellationIgnoringLatePageAndDoesNotOverlapRequests()
    {
        var response=new TaskCompletionSource<RobloxPublicServerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var directory=new FakeDirectory {Pending=response};await using var vm=new ServerBrowserViewModel(directory);vm.UsePreferredPlace(Place);
        vm.Browse.Execute(null);Assert.True(vm.IsBusy);
        vm.UsePreferredPlace(1);vm.Next.Execute(null);await vm.Next.Execution;Assert.Single(directory.Queries);
        response.SetResult(new(RobloxPublicServerStatus.Complete,[Server()],null,null,"Late old page"));await vm.Browse.Execution;
        Assert.Empty(vm.Servers);Assert.Equal(1,vm.Destination!.PlaceId);Assert.NotEqual("Late old page",vm.Status);Assert.False(vm.IsBusy);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SupersedingAResponseDuringPagePublicationCannotAttachItsRowsOrCursorToTheNewPlace(int onSummary)
    {
        var directory=new FakeDirectory {Result=new(RobloxPublicServerStatus.PageLimit,[Server()],"old-cursor",null,"Previous page response")};
        await using var vm=new ServerBrowserViewModel(directory);vm.UsePreferredPlace(Place);
        int updates=0;
        vm.PropertyChanged+=(_,args)=>
        {
            if(args.PropertyName==nameof(ServerBrowserViewModel.PageSummary) && ++updates==onSummary)vm.UsePreferredPlace(42);
        };
        vm.Browse.Execute(null);await vm.Browse.Execution;
        Assert.True(updates>=onSummary);Assert.Equal(42,vm.ReadyDestination!.PlaceId);
        Assert.Empty(vm.Servers);Assert.False(vm.CanNext);Assert.NotEqual("Previous page response",vm.Status);
        vm.Next.Execute(null);await vm.Next.Execution;Assert.Single(directory.Queries);
    }
    [Fact]
    public async Task RateLimitsWithoutRetryHeadersStillPreventAnImmediateRepeat()
    {
        var directory=new FakeDirectory {Result=new(RobloxPublicServerStatus.RateLimited,[],null,null,"Synthetic rate limit")};var clock=new Clock();
        await using var vm=new ServerBrowserViewModel(directory,clock:clock);vm.UsePreferredPlace(Place);vm.Browse.Execute(null);await vm.Browse.Execution;
        vm.Browse.Execute(null);await vm.Browse.Execution;Assert.Single(directory.Queries);
        clock.Advance(TimeSpan.FromSeconds(31));vm.Browse.Execute(null);await vm.Browse.Execution;Assert.Equal(2,directory.Queries.Count);
    }
    [Fact]
    public async Task StaleOrForeignServerRowsCannotRedirectTheRequest()
    {
        var directory=new FakeDirectory {Result=new(RobloxPublicServerStatus.Complete,[Server(DateTimeOffset.UtcNow.AddMinutes(-6))],null,null,"Synthetic")};
        await using var vm=new ServerBrowserViewModel(directory);vm.UsePreferredPlace(Place);vm.Browse.Execute(null);await vm.Browse.Execution;
        Assert.Empty(vm.Servers);Assert.Null(vm.RequestedServerObservation);Assert.Contains("stale",vm.Status);
        vm.UseServer.Execute(null);Assert.Null(vm.Destination!.JobId);
        vm.SelectedServer=new(Server());vm.UseServer.Execute(null);Assert.Null(vm.Destination.JobId);
    }
    [Fact]
    public async Task ALongRequestedRetryPauseIsNotShortenedByThePresentationLayer()
    {
        var directory=new FakeDirectory {Result=new(RobloxPublicServerStatus.RateLimited,[],null,TimeSpan.FromHours(2),"Synthetic long pause")};var clock=new Clock();
        await using var vm=new ServerBrowserViewModel(directory,clock:clock);vm.UsePreferredPlace(Place);vm.Browse.Execute(null);await vm.Browse.Execution;
        clock.Advance(TimeSpan.FromHours(1));vm.Browse.Execute(null);await vm.Browse.Execution;Assert.Single(directory.Queries);
        clock.Advance(TimeSpan.FromHours(1));vm.Browse.Execute(null);await vm.Browse.Execution;Assert.Equal(2,directory.Queries.Count);
    }
    [Fact]
    public async Task InconsistentOversizedPagesAndLockedOperationsCannotPublishOrSendRequests()
    {
        var directory=new FakeDirectory {Result=new(RobloxPublicServerStatus.Complete,Enumerable.Repeat(Server(),26).ToArray(),null,null,"Invalid oversized page")};bool allowed=true;
        await using var vm=new ServerBrowserViewModel(directory,()=>allowed);vm.UsePreferredPlace(Place);vm.Browse.Execute(null);await vm.Browse.Execution;Assert.Empty(vm.Servers);
        allowed=false;vm.LinkInput="1";vm.Resolve.Execute(null);vm.Browse.Execute(null);await vm.Browse.Execution;Assert.Single(directory.Queries);Assert.Equal(Place,vm.Destination!.PlaceId);
        vm.CancelWork(true);Assert.Null(vm.Destination);Assert.Equal("",vm.LinkInput);
    }
    [Fact]
    public async Task PageOrderChoicesAreExplicitAndNeverSendARequestByThemselves()
    {
        var directory=new FakeDirectory();await using var vm=new ServerBrowserViewModel(directory);
        Assert.Equal(ServerOrderChoice.ServerDefault,vm.SelectedServerOrder);Assert.False(vm.HideFull);
        Assert.Equal(Enum.GetValues<ServerOrderChoice>(),vm.ServerOrderChoices.Select(choice=>choice.Value));
        Assert.DoesNotContain(vm.ServerOrderChoices,choice=>choice.Label.Contains("Recommended",StringComparison.OrdinalIgnoreCase));
        foreach(var choice in vm.ServerOrderChoices)
        {
            vm.SelectedServerOrder=choice.Value;vm.HideFull=!vm.HideFull;
            Assert.Empty(directory.Queries);Assert.Empty(vm.Servers);
            Assert.Contains("page",vm.OrderingExplanation,StringComparison.OrdinalIgnoreCase);
        }
        vm.UsePreferredPlace(Place);vm.HideFull=true;vm.SelectedServerOrder=ServerOrderChoice.BestPing;
        Assert.Empty(directory.Queries);Assert.Contains("not a client latency test or a global ranking",vm.OrderingExplanation);
        Assert.Throws<ArgumentOutOfRangeException>(()=>vm.SelectedServerOrder=(ServerOrderChoice)999);
        Assert.Equal(ServerOrderChoice.BestPing,vm.SelectedServerOrder);Assert.Empty(directory.Queries);
    }
    [Fact]
    public async Task PageLocalPingOrderKeepsStableTiesAndEquivalentAscendingChoicesRestoreTheServerOrder()
    {
        var observations=new[] {Observation(1,8,ping:null),Observation(2,3,ping:40),Observation(3,8,ping:0),Observation(4,1,ping:40),Observation(5,3,ping:-1)};
        var directory=new FakeDirectory {Result=new(RobloxPublicServerStatus.PageLimit,observations,"next",null,"Synthetic page")};
        await using var vm=new ServerBrowserViewModel(directory);vm.UsePreferredPlace(Place);vm.Browse.Execute(null);await vm.Browse.Execution;
        var original=vm.Servers.ToArray();var selected=original[1];vm.SelectedServer=selected;
        vm.SelectedServerOrder=ServerOrderChoice.MostEmpty;
        Assert.Equal(original,vm.Servers);Assert.Same(selected,vm.SelectedServer);
        vm.SelectedServerOrder=ServerOrderChoice.BestPing;
        Assert.Equal(new[] {original[2],original[1],original[3],original[0],original[4]},vm.Servers);
        Assert.Equal("0 ms",vm.Servers[0].Ping);Assert.Equal("Unavailable",original[0].Ping);Assert.Equal("Unavailable",original[4].Ping);
        vm.SelectedServerOrder=ServerOrderChoice.ServerDefault;
        Assert.Equal(original,vm.Servers);Assert.Same(selected,vm.SelectedServer);Assert.True(vm.CanNext);Assert.Single(directory.Queries);
        Assert.Null(vm.Destination!.JobId);
    }
    [Fact]
    public async Task RemoteHideFullClearsPageAndCursorWithoutRewritingAnExplicitRequestedTargetOrSendingARequest()
    {
        var directory=new FakeDirectory {Result=new(RobloxPublicServerStatus.PageLimit,
            [Observation(1,20),Observation(2,21),Observation(3,19)],"another-page",null,"Synthetic page")};
        await using var vm=new ServerBrowserViewModel(directory);vm.UsePreferredPlace(Place);vm.Browse.Execute(null);await vm.Browse.Execution;
        var original=vm.Servers.ToArray();vm.SelectedServer=original[0];vm.UseServer.Execute(null);
        var requested=vm.Destination;vm.HideFull=true;
        Assert.Empty(vm.Servers);Assert.Null(vm.SelectedServer);Assert.False(vm.CanUseServer);Assert.False(vm.CanNext);
        Assert.Same(requested,vm.Destination);Assert.Contains("no request was sent",vm.Status);Assert.Single(directory.Queries);
        vm.Next.Execute(null);await vm.Next.Execution;Assert.Single(directory.Queries);
        vm.Browse.Execute(null);await vm.Browse.Execution;
        Assert.Same(requested,vm.Destination);Assert.Equal(original[2].JobId,Assert.Single(vm.Servers).JobId);
        Assert.True(directory.Queries[1].ExcludeFullGames);Assert.Null(directory.Queries[1].Cursor);
        vm.SelectedServer=vm.Servers[0];vm.HideFull=false;
        Assert.Empty(vm.Servers);Assert.Null(vm.SelectedServer);Assert.False(vm.CanNext);Assert.Equal(2,directory.Queries.Count);
        vm.Browse.Execute(null);await vm.Browse.Execution;
        Assert.False(directory.Queries[2].ExcludeFullGames);Assert.Null(directory.Queries[2].Cursor);Assert.Equal(3,vm.Servers.Count);
    }
    [Fact]
    public async Task OccupancyOrderIsRequestedAcrossPagesAndEffectiveOrderChangesRequireAnExplicitFirstPage()
    {
        var directory=new FakeDirectory {Result=new(RobloxPublicServerStatus.PageLimit,[Observation(1,3)],"ascending-next",null,"Synthetic ascending page")};
        await using var vm=new ServerBrowserViewModel(directory);vm.UsePreferredPlace(Place);vm.Browse.Execute(null);await vm.Browse.Execution;
        Assert.Equal(RobloxPublicServerSortOrder.Asc,directory.Queries[0].SortOrder);
        vm.SelectedServer=vm.Servers[0];vm.UseServer.Execute(null);var requested=vm.Destination;
        vm.SelectedServerOrder=ServerOrderChoice.MostFull;
        Assert.Empty(vm.Servers);Assert.Null(vm.SelectedServer);Assert.False(vm.CanNext);Assert.Same(requested,vm.Destination);Assert.Single(directory.Queries);
        vm.Next.Execute(null);await vm.Next.Execution;Assert.Single(directory.Queries);
        directory.Result=new(RobloxPublicServerStatus.PageLimit,[Observation(2,19)],"descending-next",null,"Synthetic descending page");
        vm.Browse.Execute(null);await vm.Browse.Execution;
        Assert.Equal(RobloxPublicServerSortOrder.Desc,directory.Queries[1].SortOrder);Assert.Null(directory.Queries[1].Cursor);
        vm.Next.Execute(null);await vm.Next.Execution;
        Assert.Equal(RobloxPublicServerSortOrder.Desc,directory.Queries[2].SortOrder);Assert.Equal("descending-next",directory.Queries[2].Cursor);
        vm.SelectedServerOrder=ServerOrderChoice.MostEmpty;
        Assert.Empty(vm.Servers);Assert.False(vm.CanNext);Assert.Equal(3,directory.Queries.Count);
        vm.Browse.Execute(null);await vm.Browse.Execution;
        Assert.Equal(RobloxPublicServerSortOrder.Asc,directory.Queries[3].SortOrder);Assert.Null(directory.Queries[3].Cursor);
        vm.SelectedServerOrder=ServerOrderChoice.ServerDefault;
        Assert.Single(vm.Servers);Assert.True(vm.CanNext);Assert.Equal(4,directory.Queries.Count);Assert.Same(requested,vm.Destination);
    }
    [Theory]
    [InlineData(ServerOrderChoice.ServerDefault,ServerOrderChoice.MostFull,false,false)]
    [InlineData(ServerOrderChoice.MostFull,ServerOrderChoice.BestPing,false,false)]
    [InlineData(ServerOrderChoice.ServerDefault,ServerOrderChoice.ServerDefault,false,true)]
    [InlineData(ServerOrderChoice.ServerDefault,ServerOrderChoice.ServerDefault,true,false)]
    public async Task RemoteQueryChangesCancelAndFenceALatePageBeforeAnExplicitRestart(ServerOrderChoice initialOrder,
        ServerOrderChoice nextOrder,bool initialHideFull,bool nextHideFull)
    {
        var directory=new FakeDirectory {Result=new(RobloxPublicServerStatus.PageLimit,[Observation(1,1)],"old-cursor",null,"Synthetic original page")};
        await using var vm=new ServerBrowserViewModel(directory);
        vm.SelectedServerOrder=initialOrder;vm.HideFull=initialHideFull;vm.UsePreferredPlace(Place);
        vm.Browse.Execute(null);await vm.Browse.Execution;vm.SelectedServer=vm.Servers[0];vm.UseServer.Execute(null);var requested=vm.Destination;
        directory.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);vm.Next.Execute(null);
        Assert.Equal("old-cursor",directory.Queries[1].Cursor);
        vm.SelectedServerOrder=nextOrder;vm.HideFull=nextHideFull;
        Assert.True(directory.Tokens[1].IsCancellationRequested);Assert.True(vm.IsBusy);Assert.Empty(vm.Servers);Assert.Null(vm.SelectedServer);
        Assert.False(vm.CanNext);Assert.Same(requested,vm.Destination);Assert.Equal(2,directory.Queries.Count);
        vm.Browse.Execute(null);await vm.Browse.Execution;Assert.Equal(2,directory.Queries.Count);
        directory.Pending.SetResult(new(RobloxPublicServerStatus.PageLimit,[Observation(2,2)],"invalid-for-new-query",null,"Late old query"));await vm.Next.Execution;
        Assert.Empty(vm.Servers);Assert.False(vm.CanNext);Assert.False(vm.IsBusy);Assert.NotEqual("Late old query",vm.Status);
        directory.Pending=null;directory.Result=new(RobloxPublicServerStatus.PageLimit,[Observation(3,3)],"fresh-cursor",null,"Synthetic fresh query");
        vm.Browse.Execute(null);await vm.Browse.Execution;
        var restarted=directory.Queries[2];Assert.Null(restarted.Cursor);Assert.Equal(nextHideFull,restarted.ExcludeFullGames);
        Assert.Equal(nextOrder==ServerOrderChoice.MostFull?RobloxPublicServerSortOrder.Desc:RobloxPublicServerSortOrder.Asc,restarted.SortOrder);
        Assert.Equal(Observation(3,3).JobId,Assert.Single(vm.Servers).JobId);Assert.Same(requested,vm.Destination);
    }
    [Fact]
    public async Task FilteringAnEntirePageKeepsNextUsableWithoutClaimingThereAreNoServers()
    {
        var directory=new FakeDirectory {Result=new(RobloxPublicServerStatus.PageLimit,[Observation(1,20)],"next-page",null,"Synthetic full page")};
        await using var vm=new ServerBrowserViewModel(directory);vm.UsePreferredPlace(Place);vm.HideFull=true;
        vm.Browse.Execute(null);await vm.Browse.Execution;
        Assert.Empty(vm.Servers);Assert.Contains("All 1 loaded observations were full",vm.PageSummary);Assert.True(vm.CanNext);
        directory.Result=new(RobloxPublicServerStatus.Complete,[Observation(2,2)],null,null,"Synthetic next page");
        vm.Next.Execute(null);await vm.Next.Execution;
        Assert.Equal("next-page",directory.Queries[1].Cursor);Assert.Single(vm.Servers);Assert.Contains("Showing 1 of 2 loaded observations",vm.PageSummary);
        Assert.All(directory.Queries,query=>Assert.True(query.ExcludeFullGames));
        vm.HideFull=false;Assert.Empty(vm.Servers);Assert.False(vm.CanNext);Assert.Equal(2,directory.Queries.Count);
    }
    [Fact]
    public async Task SettingsChangedDuringARequestApplyToItsPageWithoutCancellingOrChangingTheRemoteCursor()
    {
        var directory=new FakeDirectory {Pending=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        await using var vm=new ServerBrowserViewModel(directory);vm.UsePreferredPlace(Place);vm.Browse.Execute(null);
        vm.SelectedServerOrder=ServerOrderChoice.BestPing;
        Assert.True(vm.IsBusy);Assert.False(directory.Tokens[0].IsCancellationRequested);Assert.Single(directory.Queries);
        directory.Pending.SetResult(new(RobloxPublicServerStatus.PageLimit,
            [Observation(1,20,ping:1),Observation(2,2,ping:80),Observation(3,1,ping:10)],"remote-asc-next",null,"Synthetic pending page"));
        await vm.Browse.Execution;
        Assert.Equal(new int?[] {1,10,80},vm.Servers.Select(row=>row.Server.Ping));
        vm.SelectedServer=vm.Servers[0];var previous=vm.SelectedServer;
        directory.Pending=null;directory.Result=new(RobloxPublicServerStatus.PageLimit,[Observation(4,20),Observation(5,3,ping:9)],"following",null,"Synthetic second page");
        vm.Next.Execute(null);await vm.Next.Execution;
        // The next page is added to the loaded one, so the selection stays and best ping orders every loaded row; unknown ping is last.
        Assert.Equal("remote-asc-next",directory.Queries[1].Cursor);Assert.Same(previous,vm.SelectedServer);
        Assert.Equal(new[]{1,5,3,2,4}.Select(id=>Observation(id,1).JobId),vm.Servers.Select(row=>row.JobId));
        vm.SelectedServerOrder=ServerOrderChoice.MostEmpty;vm.Browse.Execute(null);await vm.Browse.Execution;
        Assert.Null(directory.Queries[2].Cursor);Assert.All(directory.Queries,query=>{Assert.Equal(25,query.PageSize);Assert.Equal(1,query.MaxPages);});
    }
    [Fact]
    public async Task LocalSettingsCannotRestoreADiscardedPageOrAStaleSelection()
    {
        var clock=new Clock();var directory=new FakeDirectory {Result=new(RobloxPublicServerStatus.PageLimit,[Observation(1,1,when:clock.GetUtcNow())],"old-next",null,"Synthetic page")};
        await using var vm=new ServerBrowserViewModel(directory,clock:clock);vm.UsePreferredPlace(Place);vm.Browse.Execute(null);await vm.Browse.Execution;
        vm.SelectedServer=vm.Servers[0];clock.Advance(TimeSpan.FromMinutes(6));vm.SelectedServerOrder=ServerOrderChoice.BestPing;
        Assert.Null(vm.SelectedServer);Assert.False(vm.CanUseServer);
        directory.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);vm.Next.Execute(null);
        vm.UsePreferredPlace(2);vm.HideFull=true;vm.SelectedServerOrder=ServerOrderChoice.BestPing;
        directory.Pending.SetResult(new(RobloxPublicServerStatus.PageLimit,[Observation(2,2)],"late-cursor",null,"Late old page"));await vm.Next.Execution;
        vm.HideFull=false;Assert.Empty(vm.Servers);Assert.False(vm.CanNext);Assert.Null(vm.SelectedServer);
        Assert.Equal(2,vm.Destination!.PlaceId);Assert.NotEqual("Late old page",vm.Status);
        directory.Pending=null;directory.Result=new(RobloxPublicServerStatus.Complete,[],null,null,"Synthetic new place");
        vm.Browse.Execute(null);await vm.Browse.Execution;Assert.Null(directory.Queries[2].Cursor);Assert.Equal(2,directory.Queries[2].PlaceId);
    }
    [Fact]
    public async Task LocalSettingsCannotBypassTheFullProviderRetryPause()
    {
        var clock=new Clock();var directory=new FakeDirectory {Result=new(RobloxPublicServerStatus.RateLimited,[],null,TimeSpan.FromHours(2),"Synthetic pause")};
        await using var vm=new ServerBrowserViewModel(directory,clock:clock);vm.UsePreferredPlace(Place);vm.Browse.Execute(null);await vm.Browse.Execution;
        foreach(var choice in vm.ServerOrderChoices) {vm.SelectedServerOrder=choice.Value;vm.HideFull=!vm.HideFull;}
        clock.Advance(TimeSpan.FromHours(1));vm.Browse.Execute(null);await vm.Browse.Execution;Assert.Single(directory.Queries);
        clock.Advance(TimeSpan.FromHours(1));vm.Browse.Execute(null);await vm.Browse.Execution;Assert.Equal(2,directory.Queries.Count);
    }
    [Fact]
    public async Task ASuccessfulPageThatUsedUpTheRequestLimitKeepsItsRowsAndPausesNextUntilTheWindowResets()
    {
        var clock=new Clock();
        var directory=new FakeDirectory {Result=new(RobloxPublicServerStatus.PageLimit,[Observation(1,1,when:clock.GetUtcNow())],"next",TimeSpan.FromSeconds(51),
            "Synthetic page. Roblox's request limit for server lists is used up; wait for it to reset before loading more.")};
        await using var vm=new ServerBrowserViewModel(directory,clock:clock);vm.UsePreferredPlace(Place);vm.Browse.Execute(null);await vm.Browse.Execution;
        Assert.Single(vm.Servers);Assert.True(vm.CanNext);Assert.Contains("request limit",vm.Status);
        clock.Advance(TimeSpan.FromSeconds(50));vm.Next.Execute(null);await vm.Next.Execution;
        Assert.Single(directory.Queries);Assert.Single(vm.Servers);Assert.True(vm.CanNext);Assert.Contains("No request was sent",vm.Status);
        directory.Result=new(RobloxPublicServerStatus.Complete,[Observation(2,1,when:clock.GetUtcNow())],null,null,"Synthetic next page");
        clock.Advance(TimeSpan.FromSeconds(2));vm.Next.Execute(null);await vm.Next.Execution;
        Assert.Equal(2,directory.Queries.Count);Assert.Equal("next",directory.Queries[1].Cursor);
        Assert.Equal(new[]{Observation(1,1).JobId,Observation(2,1).JobId},vm.Servers.Select(row=>row.JobId));
    }
    [Theory]
    [InlineData(RobloxPublicServerStatus.Complete)]
    [InlineData(RobloxPublicServerStatus.PageLimit)]
    public async Task AnEmptySuccessfulPageThatUsedUpTheRequestLimitSaysSoBeforeTheNextClick(RobloxPublicServerStatus status)
    {
        var clock=new Clock();
        var directory=new FakeDirectory {Result=new(status,[],status==RobloxPublicServerStatus.PageLimit?"next":null,TimeSpan.FromSeconds(51),"Synthetic explanation")};
        await using var vm=new ServerBrowserViewModel(directory,clock:clock);vm.UsePreferredPlace(Place);vm.Browse.Execute(null);await vm.Browse.Execution;
        Assert.Empty(vm.Servers);Assert.Contains("No public servers were returned in this response",vm.Status);Assert.Contains("request limit",vm.Status);
        clock.Advance(TimeSpan.FromSeconds(50));vm.Browse.Execute(null);await vm.Browse.Execution;Assert.Single(directory.Queries);
        directory.Result=new(RobloxPublicServerStatus.Complete,[],null,null,"Synthetic empty page");
        clock.Advance(TimeSpan.FromSeconds(2));vm.Browse.Execute(null);await vm.Browse.Execution;Assert.Equal(2,directory.Queries.Count);
        Assert.Contains("No public servers were returned in this response",vm.Status);Assert.DoesNotContain("request limit",vm.Status);
    }
    [Fact]
    public async Task MultiplePagesAndLocalPingOrdersRetainAtMostTheNewestHundredObservations()
    {
        var directory=new FakeDirectory();await using var vm=new ServerBrowserViewModel(directory);vm.UsePreferredPlace(Place);
        for(int page=0;page<6;page++)
        {
            directory.Result=new(RobloxPublicServerStatus.PageLimit,Enumerable.Range(1+page*25,25)
                .Select(id=>Observation(id,id%2==0?20:1)).ToArray(),$"page-{page+1}",null,"Synthetic bounded page");
            var command=page==0?vm.Browse:vm.Next;command.Execute(null);await command.Execution;
            int loaded=Math.Min(100,(page+1)*25);
            vm.SelectedServerOrder=ServerOrderChoice.BestPing;Assert.Equal(loaded,vm.Servers.Count);
            vm.SelectedServerOrder=ServerOrderChoice.ServerDefault;Assert.Equal(loaded,vm.Servers.Count);
            // Four 25-row pages fit; each later page drops the oldest rows so memory stays bounded.
            Assert.Equal(Enumerable.Range(Math.Max(1,(page+1)*25-99),loaded).Select(id=>Observation(id,1).JobId),vm.Servers.Select(row=>row.JobId));
            Assert.Equal(page>=4,vm.PageSummary.Contains("only the newest 100 are kept",StringComparison.Ordinal));
        }
        Assert.Equal(6,directory.Queries.Count);vm.CancelWork(true);vm.HideFull=true;vm.HideFull=false;
        Assert.Empty(vm.Servers);Assert.False(vm.CanNext);Assert.Null(vm.SelectedServer);
        Assert.DoesNotContain("newest 100",vm.PageSummary,StringComparison.Ordinal);
    }
    [Fact]
    public async Task LoadNextAddsItsRowsAndARepeatedServerKeepsOnlyItsNewestObservation()
    {
        var first=new RobloxPublicServer(Place,Job,5,20,null,40,DateTimeOffset.UtcNow);
        var directory=new FakeDirectory {Result=new(RobloxPublicServerStatus.PageLimit,[Observation(1,3),first,Observation(2,8)],"page-2",null,"Synthetic first page")};
        await using var vm=new ServerBrowserViewModel(directory);vm.UsePreferredPlace(Place);vm.Browse.Execute(null);await vm.Browse.Execution;
        vm.SelectedServer=vm.Servers[1];
        // Roblox repeats servers across pages as players move; the same GUID in another letter case is the same server.
        var renewed=first with {JobId=Job.ToUpperInvariant(),Playing=7,Fps=59.94};
        directory.Result=new(RobloxPublicServerStatus.Complete,[Observation(3,1),renewed],null,null,"Synthetic second page");
        vm.Next.Execute(null);await vm.Next.Execution;
        Assert.Equal("page-2",directory.Queries[1].Cursor);
        Assert.Equal(new[]{Observation(1,3).JobId,Observation(2,8).JobId,Observation(3,1).JobId,renewed.JobId},vm.Servers.Select(row=>row.JobId));
        var latest=vm.Servers[3];Assert.Same(renewed,latest.Server);Assert.Equal("7 / 20",latest.Occupancy);
        Assert.Same(latest,vm.SelectedServer);Assert.True(vm.CanUseServer);Assert.False(vm.CanNext);
        Assert.Equal("Showing 4 of 4 loaded observations.",vm.PageSummary);
        vm.UseServer.Execute(null);Assert.Equal(7,vm.RequestedServerObservation!.Playing);Assert.Equal(Job,vm.Destination!.JobId);
        // A changed order still discards every loaded page, and Browse starts the list over with one page.
        vm.SelectedServerOrder=ServerOrderChoice.MostFull;Assert.Empty(vm.Servers);Assert.False(vm.CanNext);Assert.Equal(2,directory.Queries.Count);
        directory.Result=new(RobloxPublicServerStatus.PageLimit,[Observation(4,2)],"again",null,"Synthetic restart");
        vm.Browse.Execute(null);await vm.Browse.Execution;
        Assert.Null(directory.Queries[2].Cursor);Assert.Equal(Observation(4,2).JobId,Assert.Single(vm.Servers).JobId);Assert.Null(vm.SelectedServer);
    }
    [Theory]
    [InlineData(5,20,0.25)]
    [InlineData(0,20,0.0)]
    [InlineData(20,20,1.0)]
    [InlineData(21,20,1.0)]
    [InlineData(-1,20,0.0)]
    [InlineData(3,0,0.0)]
    public void ARowsFillMeterIsReportedPlayersOverCapacityClampedToTheBar(int playing,int maxPlayers,double expected)
        =>Assert.Equal(expected,new PublicServerRow(new(Place,Job,playing,maxPlayers,null,null,DateTimeOffset.UtcNow)).FillRatio,6);
    [Fact]
    public void ARowShowsReportedFpsToOneDecimalAndSaysWhenItIsUnavailable()
    {
        Assert.Matches("^59[.,]9$",Row(59.94).Fps);Assert.Matches("^0[.,]0$",Row(0).Fps);
        foreach(double? fps in new double?[]{null,double.NaN,double.PositiveInfinity,-1})Assert.Equal("Unavailable",Row(fps).Fps);
        static PublicServerRow Row(double? fps)=>new(new(Place,Job,1,20,fps,null,DateTimeOffset.UtcNow));
    }
    private static RobloxPublicServer Observation(int id,int playing,int maxPlayers=20,int? ping=null,DateTimeOffset? when=null)
        =>new(Place,$"00000000-0000-0000-0000-{id:D12}",playing,maxPlayers,null,ping,when??DateTimeOffset.UtcNow);
    private sealed class FakeDirectory : IRobloxPublicServerDirectory
    {
        public List<RobloxPublicServerQuery> Queries {get;}=[];
        public List<CancellationToken> Tokens {get;}=[];
        public RobloxPublicServerResult Result=new(RobloxPublicServerStatus.Complete,[],null,null,"Synthetic empty page");
        public TaskCompletionSource<RobloxPublicServerResult>? Pending;
        public Task<RobloxPublicServerResult> BrowseAsync(RobloxPublicServerQuery query,CancellationToken ct)
        {Queries.Add(query);Tokens.Add(ct);return Pending?.Task??Task.FromResult(Result);}
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>_now;
        public void Advance(TimeSpan duration)=>_now+=duration;
    }
}
