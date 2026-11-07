using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// Share links are checked with the selected account's saved session, on a click only. Codes never reach text.
public sealed class ShareLinkViewModelTests
{
    private static readonly Guid ProfileA=Guid.Parse("11111111-1111-4111-8111-111111111111");
    private const string ShareCode="4b9c7e0ae7b2f84bb7a7d24f6d0f6c2e",LinkCode="98765432109876543210";
    private static string Link(string type)=>"https://www.roblox.com/share?code="+ShareCode+"&type="+type;
    private static RobloxShareLinkResolution Private()=>new(RobloxShareLinkOutcome.PrivateServer,5,6,7,new ProtectedPrivateServerCode(LinkCode));

    [Fact]
    public Task CheckClearsTheInputBeforeTheRequestAndUsesTheExperience()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var resolver=new Resolver{Hold=true};var store=new Store();Guid? profile=ProfileA;
        await using var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>profile,()=>f.Allowed,games:f.Directory);
        vm.LinkInput=Link("ExperienceDetails");Task check=vm.CheckAsync();await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("",vm.LinkInput);Assert.Equal(ProfileA,resolver.LastProfile);Assert.True(vm.IsBusy);Assert.Contains("Checking",vm.Status);
        resolver.Hold=false;resolver.Release();await check;
        Assert.Equal(5,f.Browser.Destination!.PlaceId);Assert.Null(f.Browser.Destination.JobId);Assert.False(vm.HasPrivateResult);Assert.True(vm.HasResult);
        Assert.Contains("Synthetic details",vm.ResultTitle);Assert.Equal(1,f.Directory.GameRequests);Assert.Equal(1,resolver.Calls);Assert.False(vm.IsBusy);
        Assert.DoesNotContain(ShareCode,vm.Status+vm.ResultTitle+vm.ResultDetail,StringComparison.Ordinal);Assert.Equal(0,f.Servers.Calls);
    });

    [Fact]
    public Task PrivateResultShowsLast4AndDisabledJoin()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var resolver=new Resolver{Response=_=>Private()};var store=new Store();
        await using var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>ProfileA,()=>f.Allowed,games:f.Directory);
        vm.LinkInput=Link("Server");await vm.CheckAsync();
        Assert.True(vm.HasResult);Assert.True(vm.HasPrivateResult);Assert.Contains("Link code …3210",vm.ResultDetail);Assert.Contains("Owner 7",vm.ResultDetail);
        Assert.DoesNotContain("9876543210987",vm.ResultDetail,StringComparison.Ordinal);Assert.Contains("Synthetic details",vm.ResultTitle);
        Assert.False(vm.CanJoinPrivate);Assert.Equal("Joining private servers needs launcher support (coming next)",vm.JoinNote);
        Assert.Null(f.Browser.Destination); // A private invitation is never silently turned into a public game request.
        Assert.Equal(0,f.Servers.Calls);
    });

    [Fact]
    public Task NoSelectedAccountOrNoSessionAsksToSignInWithoutARequest()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var resolver=new Resolver();var store=new Store();Guid? profile=null;
        await using var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>profile,()=>f.Allowed,games:f.Directory);
        vm.LinkInput=Link("Server");await vm.CheckAsync();
        Assert.Equal(0,resolver.Calls);Assert.Equal("Select a signed-in account first. Share links are checked with that account.",vm.Status);Assert.Equal("",vm.LinkInput);
        profile=ProfileA;resolver.Response=_=>new(RobloxShareLinkOutcome.SignInRequired);vm.LinkInput=Link("Server");await vm.CheckAsync();
        Assert.Equal(1,resolver.Calls);Assert.Equal("Sign in with the selected account to check share links.",vm.Status);Assert.False(vm.HasResult);
    });

    [Fact]
    public Task UnparseableInputIsRejectedWithoutARequest()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var resolver=new Resolver();var store=new Store();
        await using var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>ProfileA,()=>f.Allowed,games:f.Directory);
        foreach(string input in new[]{"","   ","not a link","https://www.roblox.com/games/123","https://www.roblox.com/share?code=abc"})
        {vm.LinkInput=input;await vm.CheckAsync();Assert.Equal(0,resolver.Calls);Assert.Equal("Paste a roblox.com/share link.",vm.Status);Assert.Equal(input,vm.LinkInput);}
    });

    [Theory]
    [InlineData(RobloxShareLinkOutcome.Expired,"This share link has expired. Ask for a new one.")]
    [InlineData(RobloxShareLinkOutcome.Invalid,"This share link is not valid for a game or private server.")]
    [InlineData(RobloxShareLinkOutcome.Unavailable,"The share link could not be checked right now. Try again later.")]
    [InlineData(RobloxShareLinkOutcome.Busy,"Another account operation is running. Try again when it finishes.")]
    [InlineData(RobloxShareLinkOutcome.Cancelled,"Share link check cancelled.")]
    public Task OutcomesMapToStatusesWithoutCodes(RobloxShareLinkOutcome outcome,string status)=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var resolver=new Resolver{Response=_=>new(outcome)};var store=new Store();
        await using var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>ProfileA,()=>f.Allowed,games:f.Directory);
        vm.LinkInput=Link("Server");await vm.CheckAsync();
        Assert.Equal(status,vm.Status);Assert.False(vm.HasResult);Assert.Null(f.Browser.Destination);Assert.Equal(0,f.Directory.GameRequests);
    });

    [Fact]
    public Task SaveAsFavoriteStoresAndListsItAndRemoveDeletes()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var resolver=new Resolver{Response=_=>Private()};var store=new Store();
        await using var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>ProfileA,()=>f.Allowed,games:f.Directory);
        await vm.SaveAsFavoriteAsync();Assert.Equal("Check a private server link first.",vm.Status);Assert.Empty(store.Items);
        vm.LinkInput=Link("Server");await vm.CheckAsync();Assert.True(vm.HasPrivateResult);
        vm.SaveName="  Weekend VIP  ";await vm.SaveAsFavoriteAsync();
        var saved=Assert.Single(vm.Saved);Assert.Equal("Weekend VIP",saved.Name);Assert.Equal("3210",saved.Last4);Assert.Equal(LinkCode,store.Codes[saved.Id]);
        Assert.Contains("saved",vm.Status);Assert.Equal("",vm.SaveName);Assert.True(vm.HasPrivateResult); // The result stays visible after saving.
        vm.SaveName="x";await vm.SaveAsFavoriteAsync();Assert.Equal(2,vm.Saved.Count); // Saving twice is allowed; the store owns capacity.
        vm.SelectedSaved=vm.Saved[0];Assert.True(vm.CanRemoveSaved);await vm.RemoveSelectedAsync();
        Assert.Single(vm.Saved);Assert.Single(store.Items);Assert.Null(vm.SelectedSaved);Assert.False(vm.CanRemoveSaved);
    });

    [Fact]
    public Task LoadSavedListsExistingServersAndCancelClearsEverything()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var resolver=new Resolver{Response=_=>Private(),Hold=true};var store=new Store();
        using(var one=new ProtectedPrivateServerCode(LinkCode)) await store.AddAsync("One",5,6,null,one,default);
        using(var two=new ProtectedPrivateServerCode("1111222233334444")) await store.AddAsync("Two",8,9,3,two,default);
        await using var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>ProfileA,()=>f.Allowed,games:f.Directory);
        await vm.LoadSavedAsync();Assert.Equal(new[]{"One","Two"},vm.Saved.Select(saved=>saved.Name).ToArray());Assert.Equal(1,store.Reads);
        vm.LinkInput=Link("Server");Task check=vm.CheckAsync();await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.Cancel();Assert.True(resolver.LastToken.IsCancellationRequested);resolver.Hold=false;resolver.Release();await check;
        Assert.False(vm.HasResult);Assert.Equal("",vm.LinkInput);Assert.Empty(vm.Saved);Assert.False(vm.IsBusy);
        await vm.LoadSavedAsync();Assert.Equal(2,vm.Saved.Count);
    });

    [Fact]
    public Task DisposalJoinsAHeldCheck()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var resolver=new Resolver{Hold=true};var store=new Store();
        var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>ProfileA,()=>f.Allowed,games:f.Directory);
        vm.LinkInput=Link("ExperienceDetails");Task check=vm.CheckAsync();await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task disposal=vm.DisposeAsync().AsTask();Assert.False(disposal.IsCompleted);Assert.True(resolver.LastToken.IsCancellationRequested);
        resolver.Hold=false;resolver.Release();await disposal.WaitAsync(TimeSpan.FromSeconds(5));await check;
        Assert.False(vm.HasResult);Assert.Null(f.Browser.Destination);
    });

    [Fact]
    public Task LeavingThePageCancelsAndFencesAHeldSavedListRead()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var store=new Store{HoldRead=true};
        using(var one=new ProtectedPrivateServerCode(LinkCode)) await store.AddAsync("One",5,6,null,one,default);
        await using var vm=new ShareLinkViewModel(new Resolver(),store,f.Browser,()=>ProfileA,()=>f.Allowed,games:f.Directory);
        Task load=vm.LoadSavedAsync();await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.Cancel();Assert.True(store.LastToken.IsCancellationRequested);
        store.Release();await load;
        Assert.Empty(vm.Saved);Assert.False(vm.IsBusy); // The late list does not reappear on a page the user left.
    });

    [Fact]
    public Task DisposalJoinsAHeldSaveBeforeTheStoreCanBeReleased()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var store=new Store();var resolver=new Resolver{Response=_=>Private()};
        var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>ProfileA,()=>f.Allowed,games:f.Directory);
        vm.LinkInput=Link("Server");await vm.CheckAsync();store.HoldAdd=true;
        Task save=vm.SaveAsFavoriteAsync();await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task disposal=vm.DisposeAsync().AsTask();Assert.False(disposal.IsCompleted);Assert.True(store.LastToken.IsCancellationRequested);
        store.Release();await disposal.WaitAsync(TimeSpan.FromSeconds(5));await save;
        Assert.Empty(vm.Saved);
    });

    [Fact]
    public Task SaveKeepsItsOwnCopyOfTheCodeWhenThePageIsLeft()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var store=new Store();var resolver=new Resolver{Response=_=>Private()};
        await using var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>ProfileA,()=>f.Allowed,games:f.Directory);
        vm.LinkInput=Link("Server");await vm.CheckAsync();store.HoldAdd=true;
        Task save=vm.SaveAsFavoriteAsync();await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.Cancel();Assert.False(vm.HasResult); // Leaving the page disposes the result's code while the store still holds the save.
        store.Release();await save;
        Assert.Equal(LinkCode,Assert.Single(store.Codes).Value);Assert.Empty(vm.Saved);
    });

    [Fact]
    public Task UnexpectedStoreFailureIsReportedNotThrownIntoADiscardedTask()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var store=new Store{ReadFailure=new System.IO.IOException("synthetic lock failure for "+LinkCode)};var failures=new List<Exception>();
        await using var vm=new ShareLinkViewModel(new Resolver(),store,f.Browser,()=>ProfileA,()=>f.Allowed,failures.Add,games:f.Directory);
        await vm.LoadSavedAsync(); // The shell discards this task when the page opens; it must not fault.
        Assert.Equal("Saved private servers could not be read right now. Choose Refresh list to try again.",vm.Status);Assert.False(vm.IsBusy);
        var failure=Assert.Single(failures);Assert.DoesNotContain(LinkCode,failure.ToString(),StringComparison.Ordinal);
    });

    private sealed class Resolver:IRobloxShareLinkResolver
    {
        public int Calls;public Guid LastProfile;public CancellationToken LastToken;public bool Hold;
        public Func<RobloxShareLink,RobloxShareLinkResolution>? Response;
        public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release()=>_release.TrySetResult();
        public async Task<RobloxShareLinkResolution> ResolveShareLinkAsync(Guid profileId,RobloxShareLink link,CancellationToken ct)
        {Calls++;LastProfile=profileId;LastToken=ct;Entered.TrySetResult();if(Hold)await _release.Task;return Response?.Invoke(link)??new(RobloxShareLinkOutcome.Experience,5,6);}
    }
    private sealed class Store:IPrivateServerStore
    {
        public readonly List<SavedPrivateServer> Items=[];public readonly Dictionary<Guid,string> Codes=[];public int Reads;
        public bool HoldRead,HoldAdd;public Exception? ReadFailure;public CancellationToken LastToken;
        public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release()=>_release.TrySetResult();
        // Held operations ignore their token after release, like a store that finished its write: results must be fenced.
        public async Task<IReadOnlyList<SavedPrivateServer>> ReadAsync(CancellationToken ct)
        {
            Reads++;LastToken=ct;Entered.TrySetResult();if(HoldRead) await _release.Task;
            if(ReadFailure is { } failure) throw failure;
            return Items.ToArray();
        }
        public async Task<SavedPrivateServer> AddAsync(string name,long placeId,long universeId,long? ownerUserId,ProtectedPrivateServerCode code,CancellationToken ct)
        {
            LastToken=ct;Entered.TrySetResult();if(HoldAdd) await _release.Task;
            var saved=new SavedPrivateServer(Guid.NewGuid(),SavedPrivateServer.NormalizeName(name),placeId,universeId,ownerUserId,code.Last4,1).Validated();
            string value="";code.Reveal(span=>value=new string(span));Codes[saved.Id]=value;Items.Add(saved);return saved;
        }
        public Task DeleteAsync(Guid id,long revision,CancellationToken ct)
        {int index=Items.FindIndex(item=>item.Id==id);if(index<0 || Items[index].Revision!=revision) throw new PrivateServerConflictException();Items.RemoveAt(index);Codes.Remove(id);return Task.CompletedTask;}
        public Task<ProtectedPrivateServerCode> ReadCodeAsync(Guid id,CancellationToken ct)
            =>Codes.TryGetValue(id,out var code)?Task.FromResult(new ProtectedPrivateServerCode(code)):throw new PrivateServerConflictException();
    }
}
