using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// The selected account's public Roblox favorites: one page per visit, no sign-in, fenced by account and generation.
public sealed class RobloxFavoriteGamesViewModelTests
{
    private static readonly Guid ProfileA=Guid.Parse("11111111-1111-4111-8111-111111111111"),ProfileB=Guid.Parse("22222222-2222-4222-8222-222222222222");

    [Fact]
    public Task LoadsOncePerVisitForTheSelectedAccount()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var directory=new FavoritesDirectory(25);await using var vm=new RobloxFavoriteGamesViewModel(directory,f.Browser,()=>f.Allowed);
        vm.SetAccount(ProfileA,156,3,"Main");await vm.LoadAsync();await vm.LoadAsync();
        Assert.Equal(1,directory.Requests);Assert.Equal(156,directory.LastUserId);
        Assert.Equal(6,vm.Chips.Count);Assert.Equal(19,vm.MoreCount);Assert.True(vm.HasMore);Assert.Equal("+19 more",vm.MoreLabel);
        Assert.Equal("On Roblox · Main",vm.HeaderLabel);Assert.Contains("25",vm.Status);Assert.Equal("Favorite 1",vm.Chips[0].Name);
        vm.ShowAll.Execute(null);Assert.Equal(25,vm.Chips.Count);Assert.Equal("Show fewer",vm.MoreLabel);
        vm.ShowAll.Execute(null);Assert.Equal(6,vm.Chips.Count);Assert.Equal(1,directory.Requests);
        vm.Refresh.Execute(null);await vm.Refresh.Execution;Assert.Equal(2,directory.Requests); // Only an explicit refresh asks again.
    });

    [Fact]
    public Task AccountWithoutPlayerIdShowsSignInMessageAndMakesNoRequest()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var directory=new FavoritesDirectory(3);await using var vm=new RobloxFavoriteGamesViewModel(directory,f.Browser,()=>f.Allowed);
        await vm.LoadAsync();Assert.Equal(0,directory.Requests);Assert.Equal("Select an account to show its Roblox favorites.",vm.Status);Assert.Equal("On Roblox",vm.HeaderLabel);
        vm.SetAccount(ProfileA,null,null,"Main");await vm.LoadAsync();
        Assert.Equal(0,directory.Requests);Assert.Equal("Sign in to show this account's Roblox favorites",vm.Status);Assert.Empty(vm.Chips);Assert.Equal("On Roblox · Main",vm.HeaderLabel);
        vm.Refresh.Execute(null);await vm.Refresh.Execution;Assert.Equal(0,directory.Requests);
    });

    [Fact]
    public Task AccountSwitchDropsTheOldAccountsFavorites()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var directory=new FavoritesDirectory(3){Hold=true};await using var vm=new RobloxFavoriteGamesViewModel(directory,f.Browser,()=>f.Allowed);
        vm.SetAccount(ProfileA,156,3,"A");Task first=vm.LoadAsync();await directory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Loading favorites…",vm.Status);
        vm.SetAccount(ProfileB,157,1,"B");Assert.True(directory.LastToken.IsCancellationRequested);Assert.Empty(vm.Chips);
        directory.Hold=false;directory.Release();await first;Assert.Empty(vm.Chips); // The late page for A is fenced out.
        await vm.LoadAsync();Assert.Equal(2,directory.Requests);Assert.Equal(157,directory.LastUserId);Assert.Equal(3,vm.Chips.Count);Assert.Equal("On Roblox · B",vm.HeaderLabel);
        vm.SetAccount(ProfileB,157,1,"B");Assert.Equal(3,vm.Chips.Count); // Re-selecting the same account keeps its page.
    });

    [Fact]
    public Task SwitchingAccountWhileTheOldPageLoadsStartsTheNewAccountsRequest()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var directory=new FavoritesDirectory(3){Hold=true};await using var vm=new RobloxFavoriteGamesViewModel(directory,f.Browser,()=>f.Allowed);
        vm.SetAccount(ProfileA,156,3,"A");Task first=vm.LoadAsync();await directory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.SetAccount(ProfileB,157,1,"B");Task second=vm.LoadAsync(); // The shell does not wait for A's request to unwind.
        try
        {
            Assert.Equal(2,directory.Requests);Assert.Equal(157,directory.LastUserId);Assert.True(vm.IsBusy);Assert.Equal("Loading favorites…",vm.Status);
            directory.ReleaseFirst();await first;
            Assert.True(vm.IsBusy);Assert.Equal("Loading favorites…",vm.Status);Assert.Empty(vm.Chips); // A's late finish neither publishes nor ends B's load.
        }
        finally {directory.Hold=false;directory.Release();}
        await second;await vm.ActiveOperation;
        Assert.False(vm.IsBusy);Assert.Equal(3,vm.Chips.Count);Assert.All(vm.Chips,chip=>Assert.Equal(157*1000,chip.Game.RootPlaceId/1000*1000));
    });

    [Fact]
    public Task ChipUseSetsThePlaceWithoutLaunching()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var directory=new FavoritesDirectory(3);await using var vm=new RobloxFavoriteGamesViewModel(directory,f.Browser,()=>f.Allowed);
        vm.SetAccount(ProfileA,156,3,"A");await vm.LoadAsync();var chip=vm.Chips[1];
        vm.Use.Execute(chip);Assert.Equal(chip.Game.RootPlaceId,f.Browser.Destination!.PlaceId);Assert.Null(f.Browser.Destination.JobId);
        Assert.Equal(0,f.Servers.Calls);Assert.Equal(0,f.Directory.GameRequests);Assert.Equal(1,directory.Requests);
        vm.Use.Execute(new RobloxFavoriteChip(new(999,9999,"Foreign")));Assert.Equal(chip.Game.RootPlaceId,f.Browser.Destination!.PlaceId); // Only listed chips are used.
    });

    [Fact]
    public Task LeavingThePageCancelsAndClears()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var directory=new FavoritesDirectory(3){Hold=true};await using var vm=new RobloxFavoriteGamesViewModel(directory,f.Browser,()=>f.Allowed);
        vm.SetAccount(ProfileA,156,3,"A");Task first=vm.LoadAsync();await directory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.Cancel();Assert.True(directory.LastToken.IsCancellationRequested);directory.Hold=false;directory.Release();await first;Assert.Empty(vm.Chips);
        await vm.LoadAsync();Assert.Equal(2,directory.Requests);Assert.Equal(3,vm.Chips.Count); // A new visit loads again.
        vm.Cancel();Assert.Empty(vm.Chips);Assert.False(vm.HasMore);
    });

    [Fact]
    public Task UnavailableOrRestrictedShowsAStatusWithoutAutomaticRetry()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var directory=new FavoritesDirectory(3){Response=new(PublicDirectoryState.Unknown,null,"synthetic-provider-text")};
        await using var vm=new RobloxFavoriteGamesViewModel(directory,f.Browser,()=>f.Allowed);
        vm.SetAccount(ProfileA,156,3,"A");await vm.LoadAsync();Assert.Empty(vm.Chips);Assert.Equal("Favorites unavailable",vm.Status);Assert.DoesNotContain("synthetic",vm.Status);
        await vm.LoadAsync();Assert.Equal(1,directory.Requests); // Same visit: no automatic retry.
        vm.Refresh.Execute(null);await vm.Refresh.Execution;Assert.Equal(2,directory.Requests);
        directory.Response=new(PublicDirectoryState.Deferred,null,"raw",TimeSpan.FromMinutes(1));vm.Refresh.Execute(null);await vm.Refresh.Execution;
        Assert.Equal("Favorites deferred · wait for the Roblox cooldown",vm.Status);Assert.Empty(vm.Chips);
    });

    [Fact]
    public Task MissingDirectoryIsASilentPlaceholderAndDisposalJoinsAHeldRequest()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();
        await using(var none=new RobloxFavoriteGamesViewModel(null,f.Browser,()=>f.Allowed))
        {none.SetAccount(ProfileA,156,3,"A");await none.LoadAsync();Assert.Empty(none.Chips);Assert.Equal("Roblox favorites are unavailable in this build.",none.Status);}
        var directory=new FavoritesDirectory(3){Hold=true};var vm=new RobloxFavoriteGamesViewModel(directory,f.Browser,()=>f.Allowed);
        vm.SetAccount(ProfileA,156,3,"A");Task first=vm.LoadAsync();await directory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task disposal=vm.DisposeAsync().AsTask();Assert.False(disposal.IsCompleted);Assert.True(directory.LastToken.IsCancellationRequested);
        directory.Hold=false;directory.Release();await disposal.WaitAsync(TimeSpan.FromSeconds(5));await first;Assert.Empty(vm.Chips);
    });

    private sealed class FavoritesDirectory(int count):IPublicRobloxFavoriteGamesDirectory
    {
        public int Requests;public long LastUserId;public CancellationToken LastToken;public bool Hold;
        public PublicDirectoryResult<IReadOnlyList<PublicRobloxFavoriteGame>>? Response;
        public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<TaskCompletionSource> _holds=[];
        public void Release() {foreach(var hold in _holds) hold.TrySetResult();}
        public void ReleaseFirst()=>_holds[0].TrySetResult();
        // A held page returns normally after release even when its token was cancelled: publication must be fenced.
        public async Task<PublicDirectoryResult<IReadOnlyList<PublicRobloxFavoriteGame>>> GetFavoriteGamesAsync(long userId,CancellationToken ct)
        {
            Requests++;LastUserId=userId;LastToken=ct;Entered.TrySetResult();
            if(Hold) {var hold=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);_holds.Add(hold);await hold.Task;}
            IReadOnlyList<PublicRobloxFavoriteGame> items=Enumerable.Range(1,count).Select(i=>new PublicRobloxFavoriteGame(userId*100+i,userId*1000+i,"Favorite "+i)).ToArray();
            return Response??SearchTest.Ok(items);
        }
    }
}
