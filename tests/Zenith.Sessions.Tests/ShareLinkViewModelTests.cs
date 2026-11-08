using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// Share links are checked with the selected account's saved session, on a click only. Codes never reach text.
public sealed class ShareLinkViewModelTests
{
    private static readonly Guid ProfileA=Guid.Parse("11111111-1111-4111-8111-111111111111"),ProfileB=Guid.Parse("22222222-2222-4222-8222-222222222222");
    private const string ShareCode="4b9c7e0ae7b2f84bb7a7d24f6d0f6c2e",LinkCode="98765432109876543210",Job="0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string AccountChanged="Share link check stopped because the selected account changed. Paste the link again to check it with the selected account.";
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
    public Task ALateGameResultKeepsTheDestinationAndDraftChosenWhileTheCheckRan()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var resolver=new Resolver{Hold=true};var store=new Store();
        await using var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>ProfileA,()=>f.Allowed,games:f.Directory);
        vm.LinkInput=Link("ExperienceDetails");Task check=vm.CheckAsync();await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Browser.UsePreferredPlace(99);f.Browser.LinkInput="Adopt"; // The user went back to step 1, picked another game and started typing.
        resolver.Hold=false;resolver.Release();await check;
        Assert.Equal(99,f.Browser.Destination!.PlaceId);Assert.Equal("Adopt",f.Browser.LinkInput);
        Assert.True(vm.HasResult);Assert.False(vm.IsBusy);
        Assert.DoesNotContain("The game is the requested destination",vm.Status,StringComparison.Ordinal);
        Assert.Contains("newer choice was kept",vm.Status,StringComparison.Ordinal);Assert.Contains("not applied",vm.ResultDetail,StringComparison.Ordinal);
    });

    [Fact]
    public Task ALateGameResultKeepsAFindBoxDraftTypedWhileTheCheckRan()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var resolver=new Resolver{Hold=true};var store=new Store();
        await using var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>ProfileA,()=>f.Allowed,games:f.Directory);
        vm.LinkInput=Link("ExperienceDetails");Task check=vm.CheckAsync();await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Browser.LinkInput="Adopt";
        resolver.Hold=false;resolver.Release();await check;
        Assert.Equal("Adopt",f.Browser.LinkInput);Assert.Null(f.Browser.Destination);Assert.True(vm.HasResult);
        Assert.Contains("newer choice was kept",vm.Status,StringComparison.Ordinal);
    });

    [Fact]
    public Task SelectingAnotherAccountCancelsTheRunningCheckAndItsResultIsNeverShown()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();RobloxShareLinkResolution? late=null;var store=new Store();Guid? profile=ProfileA;
        var resolver=new Resolver{Hold=true,Response=_=>late=Private()};
        await using var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>profile,()=>f.Allowed,games:f.Directory);
        vm.LinkInput=Link("Server");Task check=vm.CheckAsync();await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            vm.SelectedAccountChanged(); // The same account's row was reloaded: the check goes on.
            profile=null;vm.SelectedAccountChanged();profile=ProfileA;vm.SelectedAccountChanged(); // A list reload empties the selection for a moment.
            Assert.False(resolver.LastToken.IsCancellationRequested);Assert.Contains("Checking",vm.Status);
            profile=ProfileB;vm.SelectedAccountChanged();
            Assert.True(resolver.LastToken.IsCancellationRequested);Assert.Equal(AccountChanged,vm.Status);
        }
        finally {resolver.Hold=false;resolver.Release();await check.WaitAsync(TimeSpan.FromSeconds(5));} // The resolver answers anyway, after its token was cancelled.
        Assert.False(vm.HasResult);Assert.False(vm.IsBusy);Assert.Equal(AccountChanged,vm.Status);Assert.Equal("",vm.ResultDetail);
        Assert.Equal(0,late!.PrivateServerCode!.Length);Assert.Equal(0,f.Directory.GameRequests); // The late code was disposed, never shown.
        vm.LinkInput=Link("Server");await vm.CheckAsync(); // Checking again uses the account selected now.
        Assert.Equal(ProfileB,resolver.LastProfile);Assert.True(vm.HasPrivateResult);
    });

    [Fact]
    public Task AResultIsNotShownWhenItsAccountIsNoLongerSelected()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();RobloxShareLinkResolution? late=null;var store=new Store();Guid? profile=ProfileA;
        var resolver=new Resolver{Hold=true,Response=_=>late=Private()};
        await using var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>profile,()=>f.Allowed,games:f.Directory);
        vm.LinkInput=Link("Server");Task check=vm.CheckAsync();await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        profile=null; // The account was filtered out of the list, so nothing is selected when the answer arrives.
        resolver.Hold=false;resolver.Release();await check;
        Assert.False(vm.HasResult);Assert.False(vm.IsBusy);Assert.Equal(AccountChanged,vm.Status);Assert.Equal(0,late!.PrivateServerCode!.Length);
    });

    [Fact]
    public Task SelectingAnotherAccountWhileTheGameIsNamedDisposesTheCheckedCode()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();RobloxShareLinkResolution? late=null;var store=new Store();Guid? profile=ProfileA;var games=new CancellableGames();
        var resolver=new Resolver{Response=_=>late=Private()};
        await using var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>profile,()=>f.Allowed,games:games);
        vm.LinkInput=Link("Server");Task check=vm.CheckAsync();await games.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        profile=ProfileB;vm.SelectedAccountChanged();
        Assert.True(games.LastToken.IsCancellationRequested);await check.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.HasResult);Assert.False(vm.IsBusy);Assert.Equal(AccountChanged,vm.Status);
        Assert.Equal(0,late!.PrivateServerCode!.Length); // Cancelled mid-lookup, the checked code is still zeroed, not left to the finalizer.
    });

    [Fact]
    public Task RoutedLinkWhileACheckIsRunningSaysItWasNotChecked()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var resolver=new Resolver{Hold=true,Response=_=>Private()};var store=new Store();
        await using var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>ProfileA,()=>f.Allowed,games:f.Directory);
        vm.LinkInput=Link("Server");Task first=vm.CheckAsync();await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            // The Find box hands over a second link while the first is still being checked.
            Assert.True(RobloxShareLinkParser.TryParse(Link("ExperienceDetails"),out var second));await vm.CheckAsync(second!);
            Assert.Equal(1,resolver.Calls);Assert.Equal(0,second!.Code.Length); // Not checked, and its code was zeroed.
            Assert.Contains("was not checked",vm.Status,StringComparison.Ordinal);Assert.DoesNotContain("Checking the share link",vm.Status,StringComparison.Ordinal);
        }
        finally {resolver.Hold=false;resolver.Release();await first.WaitAsync(TimeSpan.FromSeconds(5));}
        // The first link's answer appears, and still says the pasted link was not the one checked, so it is not saved by mistake.
        Assert.True(vm.HasPrivateResult);Assert.Null(f.Browser.Destination);
        Assert.Contains("Private server link checked",vm.Status,StringComparison.Ordinal);Assert.Contains("was not checked",vm.Status,StringComparison.Ordinal);
        Assert.DoesNotContain(ShareCode,vm.Status,StringComparison.Ordinal);
        vm.LinkInput=Link("Server");await vm.CheckAsync(); // A later check of its own says nothing about the earlier refusal.
        Assert.Equal(2,resolver.Calls);Assert.DoesNotContain("was not checked",vm.Status,StringComparison.Ordinal);
    });

    [Fact]
    public Task RoutedLinkWhileACancelledCheckIsStoppingSaysItWasNotChecked()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var resolver=new Resolver{Hold=true};var store=new Store();
        await using var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>ProfileA,()=>f.Allowed,games:f.Directory);
        vm.LinkInput=Link("Server");Task first=vm.CheckAsync();await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            vm.Cancel(); // Leaving the page; this resolver ignores its token, so the check is still stopping when the user comes back.
            Assert.True(RobloxShareLinkParser.TryParse(Link("ExperienceDetails"),out var second));await vm.CheckAsync(second!);
            Assert.Equal(1,resolver.Calls);Assert.Equal(0,second!.Code.Length);Assert.Contains("was not checked",vm.Status,StringComparison.Ordinal);
        }
        finally {resolver.Hold=false;resolver.Release();await first.WaitAsync(TimeSpan.FromSeconds(5));}
        Assert.False(vm.HasResult);Assert.False(vm.IsBusy);Assert.Null(f.Browser.Destination);Assert.Contains("was not checked",vm.Status,StringComparison.Ordinal);
        Assert.True(RobloxShareLinkParser.TryParse(Link("ExperienceDetails"),out var again));await vm.CheckAsync(again!); // Pasting it again now checks it.
        Assert.Equal(2,resolver.Calls);Assert.Equal(5,f.Browser.Destination!.PlaceId);
    });

    [Fact]
    public Task RoutedLinkIsRefusedSilentlyWhenTheDirectoriesAreLocked()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var resolver=new Resolver{Hold=true};var store=new Store();
        await using var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>ProfileA,()=>f.Allowed,games:f.Directory);
        vm.LinkInput=Link("Server");Task first=vm.CheckAsync();await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            f.Allowed=false;vm.Cancel(); // Lock or Panic: nothing new is checked and no refusal is shown over the stopped page.
            string before=vm.Status;
            Assert.True(RobloxShareLinkParser.TryParse(Link("ExperienceDetails"),out var second));await vm.CheckAsync(second!);
            Assert.Equal(1,resolver.Calls);Assert.Equal(0,second!.Code.Length);Assert.Equal(before,vm.Status);
        }
        finally {resolver.Hold=false;resolver.Release();await first.WaitAsync(TimeSpan.FromSeconds(5));}
        Assert.DoesNotContain("was not checked",vm.Status,StringComparison.Ordinal);
    });

    [Fact]
    public Task FindBoxDoesNotClaimACheckForALinkThePrivateTabRefused()=>SearchTest.OnUi(async()=>
    {
        const string Sent="Share link sent to Browse servers → Private server. That tab shows whether it was checked. Its code was not searched.";
        await using var f=new SearchTest();var resolver=new Resolver{Hold=true,Response=_=>Private()};var store=new Store();
        await using var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>ProfileA,()=>f.Allowed,games:f.Directory);
        await using var find=new DestinationSearchViewModel(f.Directory,f.Browser,f.Details,()=>f.Allowed,clock:f.Clock,shareLinkHandler:link=>vm.CheckAsync(link));
        vm.LinkInput=Link("Server");Task first=vm.CheckAsync();await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            // A second link pasted in the Find box while the first is still being checked: the Private server tab refuses it.
            f.Browser.LinkInput=Link("ExperienceDetails");await find.SearchOrUseAsync();
            Assert.Equal(1,resolver.Calls);Assert.Equal("",f.Browser.LinkInput);Assert.Contains("was not checked",vm.Status,StringComparison.Ordinal);
            // Back on the Find step, the status points at the tab that owns the answer instead of saying a check is under way.
            Assert.Equal(Sent,find.Status);Assert.DoesNotContain(ShareCode,find.Status,StringComparison.Ordinal);
        }
        finally {resolver.Hold=false;resolver.Release();await first.WaitAsync(TimeSpan.FromSeconds(5));}
        Assert.True(vm.HasPrivateResult);Assert.Null(f.Browser.Destination);
        Assert.Equal(Sent,find.Status); // Still true once the earlier check has finished (a private result leaves the destination alone).
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
    public Task UniverseOnlyLinksUseTheRootPlaceAndCanBeSaved()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var store=new Store();var rootPlaces=new RootPlaces(6,100);
        var resolver=new Resolver{Response=_=>new(RobloxShareLinkOutcome.Experience,null,6)};
        await using var vm=new ShareLinkViewModel(new RootPlaceShareLinkResolver(resolver,rootPlaces),store,f.Browser,()=>ProfileA,()=>f.Allowed,games:f.Directory);
        vm.LinkInput=Link("ExperienceDetails");await vm.CheckAsync();
        Assert.True(vm.HasResult);Assert.Equal(100,f.Browser.Destination!.PlaceId);Assert.Contains("Place 100",vm.ResultTitle);Assert.Equal(1,rootPlaces.Calls);
        resolver.Response=_=>new(RobloxShareLinkOutcome.PrivateServer,null,6,null,new ProtectedPrivateServerCode(LinkCode));
        vm.LinkInput=Link("Server");await vm.CheckAsync();Assert.True(vm.HasPrivateResult);Assert.Contains("Place 100",vm.ResultTitle);
        await vm.SaveAsFavoriteAsync();
        var saved=Assert.Single(store.Items);Assert.Equal(100,saved.PlaceId);Assert.Equal(6,saved.UniverseId);Assert.Equal(LinkCode,store.Codes[saved.Id]);
    });

    // A friend's invite names their server: it becomes the requested server (place + job), never a claim of arrival.
    [Theory]
    [InlineData("ExperienceInvite")]
    [InlineData("NotificationExperienceInvite")]
    public Task AnInviteToAServerMakesThatServerTheRequestedDestination(string type)=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var store=new Store();
        var resolver=new Resolver{Response=_=>new(RobloxShareLinkOutcome.ServerInstance,5,null,jobId:Job)};
        await using var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>ProfileA,()=>f.Allowed,games:f.Directory);
        vm.LinkInput=Link(type);await vm.CheckAsync();
        Assert.Equal(1,resolver.Calls);Assert.True(vm.HasResult);Assert.False(vm.HasPrivateResult);Assert.False(vm.CanSave);
        Assert.Equal(5,f.Browser.Destination!.PlaceId);Assert.Equal(Job,f.Browser.Destination.JobId);Assert.Contains("Synthetic details",vm.ResultTitle);
        Assert.Equal("Server invite. The inviter's server is now the requested destination for the accounts you launch; nothing has launched.",vm.ResultDetail);
        Assert.Equal("Invite checked. The inviter's server is the requested destination; Roblox decides whether it still has room.",vm.Status);
        Assert.DoesNotContain(ShareCode,vm.Status+vm.ResultTitle+vm.ResultDetail,StringComparison.Ordinal);Assert.Equal(0,f.Servers.Calls);
    });

    [Fact]
    public Task AnInviteWithoutAUsableServerOpensItsGameAndSaysSo()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var store=new Store();var resolver=new Resolver{Response=_=>new(RobloxShareLinkOutcome.Experience,5)};
        await using var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>ProfileA,()=>f.Allowed,games:f.Directory);
        vm.LinkInput=Link("ExperienceInvite");await vm.CheckAsync();
        Assert.True(vm.HasResult);Assert.Equal(5,f.Browser.Destination!.PlaceId);Assert.Null(f.Browser.Destination.JobId);
        Assert.Equal("Game invite. It no longer names a server you can join, so the game is the requested destination; nothing has launched.",vm.ResultDetail);
        Assert.Equal("Invite checked. Its server cannot be joined, so the game is the requested destination; choose Quick join or Browse servers.",vm.Status);
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
    public Task SignInRequiredAsksTheShellToReloadTheCheckedAccount()=>SearchTest.OnUi(async()=>
    {
        // The service marks a rejected saved session as needing sign-in; the account row must show it without a manual refresh.
        await using var f=new SearchTest();var resolver=new Resolver{Response=_=>new(RobloxShareLinkOutcome.SignInRequired)};var store=new Store();
        var rejected=new List<Guid>();var failures=new List<Exception>();Exception? reloadFailure=null;
        await using var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>ProfileA,()=>f.Allowed,failures.Add,games:f.Directory,
            sessionRejected:(profile,ct)=>{rejected.Add(profile);return reloadFailure is null?Task.CompletedTask:Task.FromException(reloadFailure);});
        vm.LinkInput=Link("Server");await vm.CheckAsync();
        Assert.Equal(new[]{ProfileA},rejected);Assert.Equal("Sign in with the selected account to check share links.",vm.Status);Assert.False(vm.IsBusy);
        resolver.Response=_=>new(RobloxShareLinkOutcome.Experience,5,6);vm.LinkInput=Link("ExperienceDetails");await vm.CheckAsync();
        resolver.Response=_=>new(RobloxShareLinkOutcome.Unavailable);vm.LinkInput=Link("Server");await vm.CheckAsync();
        Assert.Single(rejected);Assert.Empty(failures); // Only a sign-in answer reloads the account.
        reloadFailure=new System.IO.IOException("Synthetic library read failure "+LinkCode);resolver.Response=_=>new(RobloxShareLinkOutcome.SignInRequired);
        vm.LinkInput=Link("Server");await vm.CheckAsync();
        Assert.Equal(2,rejected.Count);Assert.Equal("Sign in with the selected account to check share links.",vm.Status);Assert.False(vm.IsBusy);
        var failure=Assert.Single(failures);Assert.DoesNotContain(LinkCode,failure.ToString(),StringComparison.Ordinal);
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
        resolver.Response=_=>new(RobloxShareLinkOutcome.PrivateServer,5,6,7,new ProtectedPrivateServerCode("1111222233334444"));
        vm.LinkInput=Link("Server");await vm.CheckAsync();vm.SaveName="x";await vm.SaveAsFavoriteAsync();Assert.Equal(2,vm.Saved.Count); // Another link to the same game is another server.
        vm.SelectedSaved=vm.Saved[0];Assert.True(vm.CanRemoveSaved);await vm.RemoveSelectedAsync();
        Assert.Single(vm.Saved);Assert.Single(store.Items);Assert.Null(vm.SelectedSaved);Assert.False(vm.CanRemoveSaved);
    });

    [Fact]
    public Task SavingTheSameServerAgainAddsNothingAndSelectsTheSavedRow()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var resolver=new Resolver{Response=_=>Private()};var store=new Store();var failures=new List<Exception>();
        await using var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>ProfileA,()=>f.Allowed,failures.Add,games:f.Directory);
        vm.LinkInput=Link("Server");await vm.CheckAsync();vm.SaveName="Weekend VIP";await vm.SaveAsFavoriteAsync();var saved=Assert.Single(vm.Saved);
        vm.LinkInput=Link("Server");await vm.CheckAsync();vm.SaveName="Again";await vm.SaveAsFavoriteAsync(); // The same link, checked again under another name.
        Assert.Equal("This private server is already saved as “Weekend VIP”. Nothing was added.",vm.Status);
        Assert.Same(saved,Assert.Single(vm.Saved));Assert.Single(store.Items);Assert.Same(saved,vm.SelectedSaved);Assert.True(vm.CanRemoveSaved);
        Assert.Equal("Again",vm.SaveName);Assert.Empty(failures);Assert.False(vm.IsBusy);Assert.True(vm.HasPrivateResult);
    });

    [Fact]
    public Task SavedServersShowTheCheckedGameNameNotThePlaceId()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var resolver=new Resolver{Response=_=>Private()};var store=new Store();
        await using var vm=new ShareLinkViewModel(resolver,store,f.Browser,()=>ProfileA,()=>f.Allowed,games:f.Directory);
        vm.LinkInput=Link("Server");await vm.CheckAsync();vm.SaveName="Weekend VIP";await vm.SaveAsFavoriteAsync();
        var saved=Assert.Single(vm.Saved);Assert.Equal("Weekend VIP",saved.Name);
        Assert.Equal("Synthetic details",saved.GameName);Assert.Equal("Synthetic details",saved.GameLabel);
        await vm.LoadSavedAsync();Assert.Equal("Synthetic details",Assert.Single(vm.Saved).GameLabel);
        Assert.Equal(1,f.Directory.GameRequests); // The name is kept with the server; listing never looks it up again.
        // When the check could not name the game, the list still says which place it is.
        var unnamedStore=new Store();
        await using var unnamed=new ShareLinkViewModel(resolver,unnamedStore,f.Browser,()=>ProfileA,()=>f.Allowed);
        unnamed.LinkInput=Link("Server");await unnamed.CheckAsync();await unnamed.SaveAsFavoriteAsync();
        var plain=Assert.Single(unnamed.Saved);Assert.Null(plain.GameName);Assert.Equal("Place 5",plain.GameLabel);
    });

    [Fact]
    public Task LoadSavedListsExistingServersAndCancelClearsEverything()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();var resolver=new Resolver{Response=_=>Private(),Hold=true};var store=new Store();
        using(var one=new ProtectedPrivateServerCode(LinkCode)) await store.AddAsync("One",5,6,null,null,one,default);
        using(var two=new ProtectedPrivateServerCode("1111222233334444")) await store.AddAsync("Two",8,9,3,null,two,default);
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
        using(var one=new ProtectedPrivateServerCode(LinkCode)) await store.AddAsync("One",5,6,null,null,one,default);
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
    private sealed class RootPlaces(long universeId,long rootPlaceId):IPublicRobloxGameBatchDirectory
    {
        public int Calls;
        public Task<PublicDirectoryResult<IReadOnlyList<PublicRobloxGame>>> LookupGamesAsync(IReadOnlyList<long> universeIds,CancellationToken ct)
        {
            Calls++;Assert.Equal(new[]{universeId},universeIds.ToArray());
            IReadOnlyList<PublicRobloxGame> games=[new(rootPlaceId,universeId,rootPlaceId,"Synthetic root",7,"Creator",PublicRobloxCreatorKind.User,3,20,DateTimeOffset.UnixEpoch)];
            return Task.FromResult(SearchTest.Ok(games));
        }
    }
    // A game lookup that honours its token, like the real directory: cancelling it ends the lookup with OperationCanceledException.
    private sealed class CancellableGames:IPublicRobloxGameDirectory
    {
        public CancellationToken LastToken;
        public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PublicDirectoryResult<PublicRobloxGame>> LookupGameAsync(long requestedPlaceId,CancellationToken ct)
        {LastToken=ct;Entered.TrySetResult();await Task.Delay(Timeout.Infinite,ct);throw new InvalidOperationException("A cancelled synthetic lookup never answers.");}
        public Task<PublicDirectoryResult<PublicRobloxGameIcon>> GetGameIconAsync(long universeId,CancellationToken ct)=>throw new InvalidOperationException("Unexpected synthetic icon request");
        public Task<PublicDirectoryResult<EncodedPublicGameIcon>> FetchGameIconAsync(PublicRobloxGameIcon icon,CancellationToken ct)=>throw new InvalidOperationException("Unexpected synthetic icon download");
        public void ClearMemoryCache(){}
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
        public async Task<SavedPrivateServer> AddAsync(string name,long placeId,long universeId,long? ownerUserId,string? gameName,ProtectedPrivateServerCode code,CancellationToken ct)
        {
            LastToken=ct;Entered.TrySetResult();if(HoldAdd) await _release.Task;
            var saved=new SavedPrivateServer(Guid.NewGuid(),SavedPrivateServer.NormalizeName(name),placeId,universeId,ownerUserId,code.Last4,1,gameName).Validated();
            string value="";code.Reveal(span=>value=new string(span));
            if(Items.Find(item=>Codes[item.Id]==value) is { } existing) throw new PrivateServerDuplicateException(existing); // Like the real store.
            Codes[saved.Id]=value;Items.Add(saved);return saved;
        }
        public Task DeleteAsync(Guid id,long revision,CancellationToken ct)
        {int index=Items.FindIndex(item=>item.Id==id);if(index<0 || Items[index].Revision!=revision) throw new PrivateServerConflictException();Items.RemoveAt(index);Codes.Remove(id);return Task.CompletedTask;}
        public Task<ProtectedPrivateServerCode> ReadCodeAsync(Guid id,CancellationToken ct)
            =>Codes.TryGetValue(id,out var code)?Task.FromResult(new ProtectedPrivateServerCode(code)):throw new PrivateServerConflictException();
    }
}
