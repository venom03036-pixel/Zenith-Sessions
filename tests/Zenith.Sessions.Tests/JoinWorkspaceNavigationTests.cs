using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientPreviewPresentationTests
{
    private ShellViewModel JoinShell(SearchDirectory directory,PreviewProvider provider,JoinGallery? gallery=null)=>new(_root,false,
        Dispatcher.CurrentDispatcher,new DesktopMonitor(),publicDirectory:directory,serverDirectory:new SearchServers(),gameDirectory:gallery,
        previewProvider:provider);

    [Fact]
    public Task JoinIsOneExplicitSessionsSubrouteAndAutomationRemainsTheMacroWorkspace()=>OnUi(async()=>
    {
        var directory=new SearchDirectory(new());var provider=new PreviewProvider{AutoReady=true};await using var shell=JoinShell(directory,provider);
        await shell.InitializeAsync();shell.SetPreviewWindowVisible(true);Assert.True(shell.IsOverview);
        var changes=new List<string?>();shell.PropertyChanged+=(_,args)=>changes.Add(args.PropertyName);
        shell.Navigate.Execute("Sessions");Assert.True(shell.IsSessions);Assert.True(shell.IsSessionWorkspace);Assert.False(shell.IsJoinGame);
        shell.Navigate.Execute("JoinGame");Assert.True(shell.IsJoinGame);Assert.True(shell.IsSessionWorkspace);Assert.False(shell.IsSessions);Assert.False(shell.IsAutomation);
        Assert.Contains(nameof(shell.IsJoinGame),changes);Assert.Contains(nameof(shell.IsSessionWorkspace),changes);
        shell.Navigate.Execute("Automation");Assert.True(shell.IsAutomation);Assert.False(shell.IsSessionWorkspace);Assert.False(shell.IsJoinGame);
        shell.Navigate.Execute("JoinGame");shell.Navigate.Execute("Sessions");Assert.True(shell.IsSessions);Assert.False(shell.IsJoinGame);
        Assert.Empty(directory.Queries);Assert.Equal(0,directory.GameRequests);Assert.Equal(0,directory.ProfileRequests);Assert.Equal(0,provider.CaptureCalls);
        Assert.Empty(shell.Launches);
    });

    [Fact]
    public Task NavigationRetainsExactDestinationRecipientsAndClientSelectionWithoutHttpOrCapture()=>OnUi(async()=>
    {
        var directory=new SearchDirectory(new());var provider=new PreviewProvider{AutoReady=true};await using var shell=JoinShell(directory,provider);
        await shell.InitializeAsync();shell.SetPreviewWindowVisible(true);await shell.SaveAccountAsync(AccountProfile.Create("Retain profile") with {PreferredPlaceId=19});
        var account=Assert.Single(shell.Accounts);account.IsChecked=true;shell.SelectedAccount=account;
        var client=shell.Sessions[0];client.IsChecked=true;shell.SelectedSession=client;
        const string job="11111111-2222-3333-4444-555555555555";
        shell.ServerBrowser.LinkInput=$"https://www.roblox.com/games/start?placeId=42&gameInstanceId={job}";shell.ServerBrowser.ResolveInput();var destination=shell.ServerBrowser.Destination;
        foreach(string route in new[]{"JoinGame","Automation","Sessions","Overview","JoinGame","Sessions"})shell.Navigate.Execute(route);
        Assert.Same(destination,shell.ServerBrowser.Destination);Assert.Equal(42,destination!.PlaceId);Assert.Equal(job,destination.JobId);
        Assert.Same(account,shell.SelectedAccount);Assert.True(account.IsChecked);Assert.Same(client,shell.SelectedSession);Assert.True(client.IsChecked);
        Assert.Empty(directory.Queries);Assert.Equal(0,directory.GameRequests);Assert.Equal(0,provider.CaptureCalls);Assert.Empty(shell.Launches);
    });

    [Fact]
    public Task PreferredGameOpensJoinWithoutLoadingPublicDataOrChangingTheSavedPreference()=>OnUi(async()=>
    {
        var directory=new SearchDirectory(new());var provider=new PreviewProvider{AutoReady=true};await using var shell=JoinShell(directory,provider);
        await shell.InitializeAsync();await shell.SaveAccountAsync(AccountProfile.Create("Preferred") with {PreferredPlaceId=77});
        var row=Assert.Single(shell.Accounts);shell.SelectedAccount=row;row.IsChecked=true;shell.Navigate.Execute("Accounts");shell.SetPreviewWindowVisible(true);
        shell.UseSelectedPreferredPlace.Execute(null);Assert.True(shell.IsJoinGame);Assert.True(shell.IsSessionWorkspace);Assert.False(shell.IsSessions);
        Assert.Equal(77,shell.ServerBrowser.Destination!.PlaceId);Assert.Equal(77,row.Profile.PreferredPlaceId);Assert.Same(row,shell.SelectedAccount);Assert.True(row.IsChecked);
        Assert.Empty(directory.Queries);Assert.Equal(0,directory.GameRequests);Assert.Equal(0,provider.CaptureCalls);Assert.Empty(shell.Launches);
    });

    [Fact]
    public Task JoiningStopsTheExistingPreviewOwnerAndReturningNeverRestartsIt()=>OnUi(async()=>
    {
        var directory=new SearchDirectory(new());var provider=new PreviewProvider();await using var shell=JoinShell(directory,provider);
        await shell.InitializeAsync();await Refresh(shell);ShowPreviews(shell);shell.SelectedSession=shell.Sessions[0];shell.StartClientPreviews.Execute(null);
        var call=await provider.NextAsync();Assert.True(shell.IsSessions);shell.Navigate.Execute("JoinGame");
        await provider.NextReleaseAsync();await Until(()=>!shell.ClientPreviews.IsRunning && !shell.ClientPreviews.IsReleasing);
        Assert.True(call.Token.IsCancellationRequested);Assert.False(shell.CanStartClientPreviews);Assert.False(shell.CanStartWindowsOverview);Assert.All(shell.ClientPreviews.Rows,row=>Assert.False(row.HasImage));
        int captures=provider.CaptureCalls;shell.Navigate.Execute("Sessions");await Refresh(shell);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(captures,provider.CaptureCalls);Assert.False(shell.ClientPreviews.IsRunning);Assert.Empty(directory.Queries);Assert.Equal(0,directory.GameRequests);
    });

    [Fact]
    public Task OnlyVisibleJoinAllowsNameSearchAndLeavingPreservesItsDraftWithoutAutoResume()=>OnUi(async()=>
    {
        var directory=new SearchDirectory(new()){HoldSearch=true};var provider=new PreviewProvider{AutoReady=true};await using var shell=JoinShell(directory,provider);
        await shell.InitializeAsync();shell.SetPreviewWindowVisible(true);shell.Navigate.Execute("Automation");shell.ServerBrowser.LinkInput="Fisch";
        Assert.False(shell.DestinationSearch.CanSearch);await shell.DestinationSearch.SearchOrUseAsync();Assert.Empty(directory.Queries);
        shell.Navigate.Execute("JoinGame");Assert.True(shell.DestinationSearch.CanSearch);Task search=shell.DestinationSearch.SearchOrUseAsync();
        try
        {
            await directory.SearchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));shell.Navigate.Execute("Sessions");
            Assert.True(directory.SearchToken.IsCancellationRequested);Assert.Equal("Fisch",shell.ServerBrowser.LinkInput);Assert.False(shell.DestinationSearch.CanSearch);
        }
        finally{directory.ReleaseSearch();await search.WaitAsync(TimeSpan.FromSeconds(5));}
        shell.Navigate.Execute("JoinGame");Assert.True(shell.DestinationSearch.CanSearch);Assert.Single(directory.Queries);Assert.Empty(shell.DestinationSearch.Results);Assert.Equal(0,provider.CaptureCalls);
    });

    [Fact]
    public Task GalleryEligibilityMovesToJoinAndNavigationNeverLoadsOrRestoresPictures()=>OnUi(async()=>
    {
        var directory=new SearchDirectory(new());var gallery=new JoinGallery(directory);var provider=new PreviewProvider{AutoReady=true};await using var shell=JoinShell(directory,provider,gallery);
        await shell.InitializeAsync();shell.SetPreviewWindowVisible(true);shell.ServerBrowser.UsePreferredPlace(42);shell.Navigate.Execute("Automation");
        await shell.GameDetails.LoadAsync();Assert.Equal(0,gallery.Calls);Assert.Equal(1,directory.GameRequests);
        shell.Navigate.Execute("JoinGame");Assert.Equal(0,gallery.Calls);await shell.GameDetails.LoadAsync();Assert.Equal(1,gallery.Calls);
        var destination=shell.ServerBrowser.Destination;shell.Navigate.Execute("Sessions");Assert.Null(shell.GameDetails.Gallery.Image);
        shell.Navigate.Execute("JoinGame");Assert.Equal(1,gallery.Calls);Assert.Same(destination,shell.ServerBrowser.Destination);Assert.Equal(0,provider.CaptureCalls);
        shell.SetPreviewWindowVisible(false);await shell.GameDetails.Gallery.LoadAsync(shell.GameDetails.Game!);Assert.Equal(1,gallery.Calls);
    });

    [Fact]
    public Task LockPreventsBothJoinAndBackNavigationAndCannotEnableSearch()=>OnUi(async()=>
    {
        var directory=new SearchDirectory(new());var provider=new PreviewProvider{AutoReady=true};await using var shell=JoinShell(directory,provider);
        await shell.InitializeAsync();shell.SetPreviewWindowVisible(true);shell.Navigate.Execute("Sessions");shell.LockDashboard();shell.Navigate.Execute("JoinGame");Assert.True(shell.IsSessions);
        shell.Unlock.Execute(null);shell.Navigate.Execute("JoinGame");shell.LockDashboard();shell.Navigate.Execute("Sessions");Assert.True(shell.IsJoinGame);Assert.True(shell.IsSessionWorkspace);
        shell.ServerBrowser.LinkInput="Fisch";await shell.DestinationSearch.SearchOrUseAsync();Assert.Empty(directory.Queries);Assert.False(shell.DestinationSearch.CanSearch);Assert.Equal(0,provider.CaptureCalls);
    });

    private sealed class JoinGallery(SearchDirectory directory):IPublicRobloxGameDirectory,IPublicRobloxGameGallery
    {
        public int Calls;
        public Task<PublicDirectoryResult<PublicRobloxGame>> LookupGameAsync(long id,CancellationToken ct)=>directory.LookupGameAsync(id,ct);
        public Task<PublicDirectoryResult<PublicRobloxGameIcon>> GetGameIconAsync(long id,CancellationToken ct)=>directory.GetGameIconAsync(id,ct);
        public Task<PublicDirectoryResult<EncodedPublicGameIcon>> FetchGameIconAsync(PublicRobloxGameIcon image,CancellationToken ct)=>directory.FetchGameIconAsync(image,ct);
        public Task<PublicDirectoryResult<PublicRobloxGameGallery>> GetGameGalleryAsync(long place,long universe,CancellationToken ct)
        {Calls++;return Task.FromResult(SearchTest.Ok(new PublicRobloxGameGallery(place,universe,[],DateTimeOffset.UtcNow)));}
        public Task<PublicDirectoryResult<EncodedPublicGameGalleryImage>> FetchGameGalleryImageAsync(PublicRobloxGameGalleryImage image,CancellationToken ct)=>throw new InvalidOperationException("Unexpected synthetic image request");
        public void ClearMemoryCache(){}
    }
}
