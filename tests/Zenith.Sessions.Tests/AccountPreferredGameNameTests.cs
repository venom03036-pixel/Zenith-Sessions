using System.Net.Http;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

/// <summary>
/// Accounts → "Preferred game" is named by the public game directory, like Join a game, never by a built-in table.
/// Only the selected row asks, under the automatic headshot rule; anything else keeps "Place {id}".
/// </summary>
public sealed partial class DirectoryIntegrationTests
{
    private const long FischPlace=16732694052,ColoseumPlace=6564161046,OtherPlace=920587237;

    [Theory]
    [InlineData(FischPlace,"Fisch")]
    [InlineData(ColoseumPlace,"Coloseum (renamed)")]
    public Task SelectedAccountPreferredGameShowsTheDirectoryNameAndKeepsItAcrossReloads(long place,string name)=>OnUi(async ()=>
    {
        var games=new PreferredGames();games.Names[place]=name;
        await using var vm=await OpenAccountsAsync(games);
        await vm.SaveAccountAsync(AccountProfile.Create("Preferred") with {PreferredPlaceId=place});await vm.PreferredGameNameOperation;
        var row=Assert.Single(vm.Accounts);Assert.Same(row,vm.SelectedAccount);
        Assert.Equal($"{name} · Place {place}",row.Destination);Assert.Equal(new[]{place},games.Places);
        vm.Refresh.Execute(null);await vm.Refresh.Execution;await vm.PreferredGameNameOperation;
        Assert.NotSame(row,vm.SelectedAccount);Assert.Equal($"{name} · Place {place}",vm.SelectedAccount!.Destination);
        Assert.Single(games.Places); // The reloaded row reuses the name; no second request.
    });

    [Theory]
    [InlineData("offline by default")]
    [InlineData("directory failure")]
    [InlineData("directory throws")]
    public Task PreferredGameStaysAPlaceNumberWhenNoNameIsFound(string mode)=>OnUi(async ()=>
    {
        var games=new PreferredGames{State=mode=="directory failure"?PublicDirectoryState.Failure:PublicDirectoryState.Success,Throw=mode=="directory throws"};
        games.Names[ColoseumPlace]="Coloseum";
        await using var vm=await OpenAccountsAsync(games,automatic:mode!="offline by default");
        await vm.SaveAccountAsync(AccountProfile.Create("Preferred") with {PreferredPlaceId=ColoseumPlace});await vm.PreferredGameNameOperation;
        Assert.Equal($"Place {ColoseumPlace}",vm.SelectedAccount!.Destination);
        Assert.DoesNotContain("could not finish",vm.Notice); // A missing name is not a failed operation.
        Assert.Equal(mode=="offline by default"?0:1,games.Places.Count);
        Assert.Equal("No game selected",new AccountRow(AccountProfile.Create("No game")).Destination);
    });

    [Fact]
    public Task PreferredGameIsNamedOnlyWhileTheAccountsPageIsVisible()=>OnUi(async ()=>
    {
        var games=new PreferredGames();games.Names[FischPlace]="Fisch";
        await using var vm=await OpenAccountsAsync(games);
        vm.Navigate.Execute("Overview");
        await vm.SaveAccountAsync(AccountProfile.Create("Preferred") with {PreferredPlaceId=FischPlace});await vm.PreferredGameNameOperation;
        Assert.Equal($"Place {FischPlace}",vm.SelectedAccount!.Destination);Assert.Empty(games.Places);
        vm.Navigate.Execute("Accounts");await vm.PreferredGameNameOperation;
        Assert.Equal($"Fisch · Place {FischPlace}",vm.SelectedAccount!.Destination);Assert.Equal(new[]{FischPlace},games.Places);
    });

    [Fact]
    public Task LateNameFromAnEarlierSelectionNeverLabelsTheNewSelection()=>OnUi(async ()=>
    {
        var games=new PreferredGames();var first=games.Hold(FischPlace);var second=games.Hold(OtherPlace);
        await using var vm=await OpenAccountsAsync(games);
        try
        {
            await vm.SaveAccountAsync(AccountProfile.Create("First") with {PreferredPlaceId=FischPlace});
            await vm.SaveAccountAsync(AccountProfile.Create("Second") with {PreferredPlaceId=OtherPlace});
            Assert.Equal(new[]{FischPlace,OtherPlace},games.Places);Assert.True(games.Tokens[0].IsCancellationRequested);
            first.SetResult(games.Found(FischPlace,"Fisch"));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var selected=vm.SelectedAccount!;Assert.Equal("Second",selected.Alias);Assert.Equal($"Place {OtherPlace}",selected.Destination);
            second.SetResult(games.Found(OtherPlace,"Synthetic obby"));await vm.PreferredGameNameOperation;
            Assert.Equal($"Synthetic obby · Place {OtherPlace}",selected.Destination);
            var earlier=vm.Accounts.Single(row=>row.Alias=="First");vm.SelectedAccount=earlier;await vm.PreferredGameNameOperation;
            Assert.Equal(new[]{FischPlace,OtherPlace,FischPlace},games.Places); // The cancelled answer was not kept, so it is asked again.
            Assert.Equal($"Fisch · Place {FischPlace}",earlier.Destination);
        }
        finally {first.TrySetResult(games.Missing());second.TrySetResult(games.Missing());}
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LockAndPanicForgetShownNamesAndDoNotAskAgain(bool panic)=>OnUi(async ()=>
    {
        var games=new PreferredGames();games.Names[FischPlace]="Fisch";
        await using var vm=await OpenAccountsAsync(games);
        await vm.SaveAccountAsync(AccountProfile.Create("Preferred") with {PreferredPlaceId=FischPlace});await vm.PreferredGameNameOperation;
        var row=vm.SelectedAccount!;Assert.Equal($"Fisch · Place {FischPlace}",row.Destination);
        if(panic) vm.Panic.Execute(null); else vm.LockDashboard();
        Assert.Equal($"Place {FischPlace}",row.Destination);
        vm.SelectedAccount=null;vm.SelectedAccount=row;await vm.PreferredGameNameOperation;
        Assert.Equal($"Place {FischPlace}",row.Destination);Assert.Single(games.Places);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LateNameAfterLockOrPanicIsNeverShown(bool panic)=>OnUi(async ()=>
    {
        var games=new PreferredGames();var pending=games.Hold(FischPlace);
        await using var vm=await OpenAccountsAsync(games);
        try
        {
            await vm.SaveAccountAsync(AccountProfile.Create("Preferred") with {PreferredPlaceId=FischPlace});
            Assert.Single(games.Places);
            if(panic) vm.Panic.Execute(null); else vm.LockDashboard();
            Assert.True(games.Tokens[0].IsCancellationRequested);
            pending.SetResult(games.Found(FischPlace,"Fisch"));await vm.PreferredGameNameOperation;
            Assert.Equal($"Place {FischPlace}",vm.SelectedAccount!.Destination);Assert.Single(games.Places);
        }
        finally {pending.TrySetResult(games.Missing());}
    });

    private async Task<ShellViewModel> OpenAccountsAsync(PreferredGames games,bool automatic=true)
    {
        var vm=new ShellViewModel(_root,false,Dispatcher.CurrentDispatcher,new Monitor(),new PublicDirectory(),new Servers(),
            gameDirectory:games,enableAutomaticPublicImages:automatic);
        try {await vm.InitializeAsync();vm.Navigate.Execute("Accounts");vm.SetPreviewWindowVisible(true);return vm;}
        catch {await vm.DisposeAsync();throw;}
    }

    private sealed class PreferredGames : IPublicRobloxGameDirectory
    {
        public readonly Dictionary<long,string> Names=new();
        private readonly Dictionary<long,TaskCompletionSource<PublicDirectoryResult<PublicRobloxGame>>> _held=new();
        public readonly List<long> Places=new();
        public readonly List<CancellationToken> Tokens=new();
        public PublicDirectoryState State=PublicDirectoryState.Success;
        public bool Throw;
        public TaskCompletionSource<PublicDirectoryResult<PublicRobloxGame>> Hold(long place)=>_held[place]=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PublicDirectoryResult<PublicRobloxGame> Found(long place,string name)=>new(PublicDirectoryState.Success,
            new(place,place+1,place,name,3,"Synthetic creator",PublicRobloxCreatorKind.User,1,20,DateTimeOffset.UtcNow),"Synthetic game");
        public PublicDirectoryResult<PublicRobloxGame> Missing()=>new(PublicDirectoryState.Unknown,null,"Synthetic: no public game information.");
        public Task<PublicDirectoryResult<PublicRobloxGame>> LookupGameAsync(long place,CancellationToken ct)
        {
            Places.Add(place);Tokens.Add(ct);
            if(Throw) throw new HttpRequestException("Synthetic network failure.");
            if(_held.TryGetValue(place,out var held)) return held.Task; // Ignores ct on purpose: a late answer must still be fenced out.
            if(State!=PublicDirectoryState.Success) return Task.FromResult(new PublicDirectoryResult<PublicRobloxGame>(State,null,"Synthetic failure."));
            return Task.FromResult(Names.TryGetValue(place,out var name)?Found(place,name):Missing());
        }
        public Task<PublicDirectoryResult<PublicRobloxGameIcon>> GetGameIconAsync(long universe,CancellationToken ct)=>throw new InvalidOperationException("Preferred game names never load icons.");
        public Task<PublicDirectoryResult<EncodedPublicGameIcon>> FetchGameIconAsync(PublicRobloxGameIcon icon,CancellationToken ct)=>throw new InvalidOperationException("Preferred game names never load icons.");
        public void ClearMemoryCache() { }
    }
}
