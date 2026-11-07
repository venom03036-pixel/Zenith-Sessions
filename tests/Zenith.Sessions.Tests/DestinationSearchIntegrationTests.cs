using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    private ShellViewModel SearchShell(SearchDirectory directory)=>new(_root,false,Dispatcher.CurrentDispatcher,new Monitor(),
        publicDirectory:directory,serverDirectory:new SearchServers(),gameDirectory:directory);
    private static void ShowSearch(ShellViewModel shell){shell.Navigate.Execute("JoinGame");shell.SetPreviewWindowVisible(true);}

    [Fact]
    public Task SearchAvailabilityRequiresActualReadinessAndVisibleUnlockedJoinGame()=>OnUi(async()=>
    {
        var directory=new SearchDirectory(new());await using var shell=SearchShell(directory);ShowSearch(shell);shell.ServerBrowser.LinkInput="Fisch";
        shell.DestinationSearch.SearchOrUse.Execute(null);await shell.DestinationSearch.SearchOrUse.Execution;Assert.Empty(directory.Queries);
        await shell.InitializeAsync();shell.SetPreviewWindowVisible(false);Assert.False(shell.DestinationSearch.CanSearch);
        await shell.DestinationSearch.SearchOrUseAsync();Assert.Empty(directory.Queries);
        ShowSearch(shell);Assert.True(shell.DestinationSearch.CanSearch);shell.LockDashboard();
        shell.DestinationSearch.SearchOrUse.Execute(null);await shell.DestinationSearch.SearchOrUse.Execution;Assert.Empty(directory.Queries);Assert.Equal("",shell.ServerBrowser.LinkInput);
    });

    [Fact]
    public Task SearchAndChooseUseExistingDetailsWithoutChangingProfilesRecipientsOrQueue()=>OnUi(async()=>
    {
        var directory=new SearchDirectory(new());await using var shell=SearchShell(directory);await shell.InitializeAsync();
        await shell.SaveAccountAsync(AccountProfile.Create("Retain metadata") with {Notes="Keep notes",PreferredPlaceId=909,Group="Family"});
        var row=Assert.Single(shell.Accounts);row.IsChecked=true;shell.SelectedAccount=row;var original=row.Profile;
        ShowSearch(shell);shell.ServerBrowser.UsePreferredPlace(909);shell.ServerBrowser.LinkInput="Fisch";
        await shell.DestinationSearch.SearchOrUseAsync();Assert.Equal(909,shell.ServerBrowser.Destination!.PlaceId);Assert.Null(shell.DestinationSearch.SelectedResult);
        shell.DestinationSearch.SelectedResult=shell.DestinationSearch.Results[0];await shell.DestinationSearch.UseSelectedAsync();
        Assert.Equal(101,shell.ServerBrowser.Destination!.PlaceId);Assert.Equal(101,shell.GameDetails.Game!.RequestedPlaceId);Assert.Equal(1,directory.GameRequests);
        Assert.Same(row,shell.SelectedAccount);Assert.True(row.IsChecked);Assert.Empty(shell.Launches);Assert.Equal(0,directory.ProfileRequests);
        using var repository=new AccountRepository(_root);Assert.Equal(original,await repository.FindAsync(original.Id,default));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LockAndPanicClearTheDraftAndFenceLateUncooperativeSearchWithoutOverwritingStopNotice(bool panic)=>OnUi(async()=>
    {
        var directory=new SearchDirectory(new()){HoldSearch=true};await using var shell=SearchShell(directory);await shell.InitializeAsync();ShowSearch(shell);
        shell.ServerBrowser.UsePreferredPlace(42);shell.ServerBrowser.LinkInput="Fisch";shell.DestinationSearch.SearchOrUse.Execute(null);
        Task active=shell.DestinationSearch.SearchOrUse.Execution;string? stopped=null;
        try
        {
            await directory.SearchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));if(panic)shell.Panic.Execute(null);else shell.LockDashboard();stopped=shell.Notice;
            Assert.True(directory.SearchToken.IsCancellationRequested);Assert.False(active.IsCompleted);Assert.Empty(shell.DestinationSearch.Results);
            Assert.Equal("",shell.ServerBrowser.LinkInput);Assert.False(shell.DestinationSearch.CanSearch);
        }
        finally{directory.ReleaseSearch();await active.WaitAsync(TimeSpan.FromSeconds(5));}
        Assert.Equal(stopped,shell.Notice);Assert.Empty(shell.DestinationSearch.Results);Assert.Equal(0,directory.GameRequests);Assert.Empty(shell.Launches);
        if(panic)Assert.Equal(42,shell.ServerBrowser.Destination!.PlaceId);else Assert.Null(shell.ServerBrowser.Destination);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task HidingOrLeavingThePageCancelsWorkButRetainsDraftWithoutAutomaticRestart(bool tab)=>OnUi(async()=>
    {
        var directory=new SearchDirectory(new()){HoldSearch=true};await using var shell=SearchShell(directory);await shell.InitializeAsync();ShowSearch(shell);
        shell.ServerBrowser.LinkInput="Fisch";Task active=shell.DestinationSearch.SearchOrUseAsync();
        try
        {
            await directory.SearchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));if(tab)shell.Navigate.Execute("Accounts");else shell.SetPreviewWindowVisible(false);
            Assert.True(directory.SearchToken.IsCancellationRequested);Assert.False(shell.DestinationSearch.CanSearch);Assert.Equal("Fisch",shell.ServerBrowser.LinkInput);
        }
        finally{directory.ReleaseSearch();await active.WaitAsync(TimeSpan.FromSeconds(5));}
        ShowSearch(shell);Assert.Single(directory.Queries);Assert.Empty(shell.DestinationSearch.Results);Assert.True(shell.DestinationSearch.CanSearch);
        Assert.Null(shell.ServerBrowser.Destination);Assert.Equal(0,directory.GameRequests);
    });

    // Minimizing Zenith or closing it to the tray only hides the window; Join a game is still open. A finished page keeps its
    // cards, Show more progress, pictures and status, and showing the window again sends no search or picture request.
    [Fact]
    public Task HidingAfterACompletedSearchKeepsResultsAndDoesNotSearchAgain()=>OnUi(async()=>
    {
        var directory=new SearchDirectory(new());directory.SearchResponse=q=>SearchTest.Ok(directory.Page(q,30));
        await using var shell=SearchShell(directory);await shell.InitializeAsync();ShowSearch(shell);var search=shell.DestinationSearch;
        shell.ServerBrowser.LinkInput="Fisch";await search.SearchOrUseAsync();await search.ArtOperation;search.ShowMore.Execute(null);await search.ArtOperation;
        var rows=search.Results.ToArray();var shown=search.VisibleResults.ToArray();string status=search.Status,summary=search.PageSummary;
        int batches=directory.ArtBatches.Count,fetches=directory.ArtFetches;Assert.Equal(24,shown.Length);Assert.All(shown,row=>Assert.True(row.HasArt));
        shell.SetPreviewWindowVisible(false);
        Assert.False(search.CanSearch);Assert.Equal(rows,search.Results);Assert.Equal(shown,search.VisibleResults);Assert.All(shown,row=>Assert.True(row.HasArt));
        Assert.Equal(status,search.Status);
        shell.SetPreviewWindowVisible(true);await search.ArtOperation;
        Assert.True(search.CanSearch);Assert.Equal(rows,search.Results);Assert.Equal(shown,search.VisibleResults);Assert.All(shown,row=>Assert.True(row.HasArt));
        Assert.True(search.CanShowMore);Assert.Equal(status,search.Status);Assert.Equal(summary,search.PageSummary);
        Assert.Single(directory.Queries);Assert.Equal(0,directory.GameRequests);Assert.Equal(batches,directory.ArtBatches.Count);Assert.Equal(fetches,directory.ArtFetches);
    });

    // Pictures are not fetched while the window is hidden; showing it again asks only for the cards still without one.
    [Fact]
    public Task HidingStopsPictureDownloadsAndShowingAgainAsksOnlyForTheMissingOnes()=>OnUi(async()=>
    {
        var directory=new SearchDirectory(new()){HoldArtFetch=true};directory.SearchResponse=q=>SearchTest.Ok(directory.Page(q,3));
        await using var shell=SearchShell(directory);await shell.InitializeAsync();ShowSearch(shell);var search=shell.DestinationSearch;
        shell.ServerBrowser.LinkInput="Fisch";await search.SearchOrUseAsync();await directory.ArtFetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var rows=search.VisibleResults.ToArray();
        shell.SetPreviewWindowVisible(false);Assert.True(directory.ArtToken.IsCancellationRequested);
        directory.HoldArtFetch=false;directory.ReleaseArtFetch();await search.ArtOperation;
        Assert.Equal(rows,search.VisibleResults);Assert.All(rows,row=>Assert.False(row.HasArt));Assert.Equal(1,directory.ArtFetches);
        shell.SetPreviewWindowVisible(true);await search.ArtOperation;
        Assert.All(rows,row=>Assert.True(row.HasArt));Assert.Equal(2,directory.ArtBatches.Count);Assert.Equal(4,directory.ArtFetches);Assert.Single(directory.Queries);
    });

    // A page kept while hidden is still cleared by leaving Join a game or Lock, exactly as when the window is shown.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LeavingThePageOrLockingWhileHiddenStillClearsTheKeptPage(bool lockDashboard)=>OnUi(async()=>
    {
        var directory=new SearchDirectory(new());await using var shell=SearchShell(directory);await shell.InitializeAsync();ShowSearch(shell);
        shell.ServerBrowser.LinkInput="Fisch";await shell.DestinationSearch.SearchOrUseAsync();await shell.DestinationSearch.ArtOperation;
        var rows=shell.DestinationSearch.Results.ToArray();Assert.NotEmpty(rows);
        shell.SetPreviewWindowVisible(false);Assert.Equal(rows,shell.DestinationSearch.Results);
        if(lockDashboard)shell.LockDashboard();else shell.Navigate.Execute("Accounts");
        Assert.Empty(shell.DestinationSearch.Results);Assert.Empty(shell.DestinationSearch.VisibleResults);Assert.All(rows,row=>Assert.False(row.HasArt));
        if(lockDashboard)shell.Unlock.Execute(null);
        ShowSearch(shell);Assert.Empty(shell.DestinationSearch.Results);Assert.Single(directory.Queries);
    });

    [Fact]
    public Task PanicDuringSearchOwnedDetailsJoinsItAndNeverPublishesLateGameOrSuccess()=>OnUi(async()=>
    {
        var directory=new SearchDirectory(new()){HoldGame=true};await using var shell=SearchShell(directory);await shell.InitializeAsync();ShowSearch(shell);
        shell.ServerBrowser.LinkInput="42";Task active=shell.DestinationSearch.SearchOrUseAsync();string? stopped=null;
        try
        {
            await directory.GameEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));shell.Panic.Execute(null);stopped=shell.Notice;
            Assert.True(directory.GameToken.IsCancellationRequested);Assert.False(active.IsCompleted);Assert.Null(shell.GameDetails.Game);
        }
        finally{directory.ReleaseGame();await active.WaitAsync(TimeSpan.FromSeconds(5));}
        Assert.Equal(stopped,shell.Notice);Assert.Null(shell.GameDetails.Game);Assert.Equal("",shell.ServerBrowser.LinkInput);Assert.Empty(shell.Launches);
        Assert.Empty(directory.Queries);Assert.Equal(42,shell.ServerBrowser.Destination!.PlaceId);
    });

    [Fact]
    public Task ShutdownOwnsTheActualIgnoredCancellationBeforeClosingBorrowedDetailsAndBrowser()=>OnUi(async()=>
    {
        var directory=new SearchDirectory(new()){HoldSearch=true};var shell=SearchShell(directory);await shell.InitializeAsync();ShowSearch(shell);
        shell.ServerBrowser.LinkInput="Fisch";Task active=shell.DestinationSearch.SearchOrUseAsync();Task? disposal=null;
        try
        {
            await directory.SearchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));disposal=shell.DisposeAsync().AsTask();
            Assert.True(directory.SearchToken.IsCancellationRequested);Assert.False(active.IsCompleted);Assert.False(disposal.IsCompleted);
        }
        finally {directory.ReleaseSearch();await active.WaitAsync(TimeSpan.FromSeconds(5));if(disposal is not null)await disposal.WaitAsync(TimeSpan.FromSeconds(10));else await shell.DisposeAsync();}
        Assert.Empty(shell.DestinationSearch.Results);Assert.Equal(0,directory.GameRequests);Assert.Equal(0,directory.ProfileRequests);
    });
}
