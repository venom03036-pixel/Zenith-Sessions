using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class DestinationSearchViewModelTests
{
    [Fact]
    public void RowLabelsFormatPlayingAndRating()
    {
        var row=new PublicGameSearchRow(new(1,2,"Fisch",7,"Fischling",85_200,false,true,920,80));
        Assert.Equal("by Fischling",row.CreatorLine);Assert.True(row.IsCreatorVerified);
        Assert.Equal("85.2K playing",row.PlayingLabel);Assert.Equal("92% liked",row.RatingLabel);Assert.True(row.HasRating);
        Assert.Equal("120 playing",new PublicGameSearchRow(new(1,2,"G",Playing:120)).PlayingLabel);
        Assert.Equal("1.2M playing",new PublicGameSearchRow(new(1,2,"G",Playing:1_234_567)).PlayingLabel);
        Assert.Equal("85K playing",new PublicGameSearchRow(new(1,2,"G",Playing:85_000)).PlayingLabel);
        var unknown=new PublicGameSearchRow(new(1,2,"G",UpVotes:0,DownVotes:0));
        Assert.Null(unknown.RatingLabel);Assert.False(unknown.HasRating);Assert.False(unknown.IsCreatorVerified);
        Assert.Equal("Player count unavailable",unknown.PlayingLabel);Assert.Equal("Creator unavailable",unknown.CreatorLine);
    }

    [Fact]
    public Task FirstTwelveResultsAreVisibleAndShowMoreRevealsTheNextTwelve()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Directory.SearchResponse=q=>SearchTest.Ok(f.Directory.Page(q,30));
        f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();
        Assert.Equal(30,f.Search.Results.Count);Assert.Equal(12,f.Search.VisibleResults.Count);Assert.True(f.Search.CanShowMore);
        Assert.Same(f.Search.Results[0],f.Search.VisibleResults[0]);Assert.Same(f.Search.Results[11],f.Search.VisibleResults[11]);
        f.Search.ShowMore.Execute(null);Assert.Equal(24,f.Search.VisibleResults.Count);Assert.True(f.Search.CanShowMore);
        f.Search.ShowMore.Execute(null);Assert.Equal(30,f.Search.VisibleResults.Count);Assert.False(f.Search.CanShowMore);
        f.Search.ShowMore.Execute(null);Assert.Equal(30,f.Search.VisibleResults.Count);
        Assert.Single(f.Directory.Queries); // Show more is local to the loaded page.
        f.Search.CancelWork();Assert.Empty(f.Search.VisibleResults);Assert.False(f.Search.CanShowMore);
    });

    [Fact]
    public Task SortIsLocalAndStable()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();
        f.Directory.SearchResponse=q=>SearchTest.Ok(new PublicRobloxGameSearchPage(q,[
            new(1,11,"A",Playing:50,UpVotes:90,DownVotes:10),new(2,12,"B",Playing:500),new(3,13,"C",Playing:50,UpVotes:50,DownVotes:50),
            new(4,14,"D"),new(5,15,"E",Playing:500,UpVotes:99,DownVotes:1)],null,f.Clock.Utc));
        f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();
        Assert.Equal(new[]{"Relevance","Playing now","Rating"},f.Search.SortOptions);Assert.Equal("Relevance",f.Search.SelectedSort);
        Assert.Equal(new[]{"A","B","C","D","E"},Names(f.Search.VisibleResults));
        f.Search.SelectedSort="Playing now";Assert.Equal(new[]{"B","E","A","C","D"},Names(f.Search.VisibleResults)); // Ties keep server order; unknown last.
        f.Search.SelectedSort="Rating";Assert.Equal(new[]{"E","A","C","B","D"},Names(f.Search.VisibleResults));
        f.Search.SelectedSort="Not an option";Assert.Equal("Rating",f.Search.SelectedSort);
        f.Search.SelectedSort="Relevance";Assert.Equal(new[]{"A","B","C","D","E"},Names(f.Search.VisibleResults));
        Assert.Single(f.Directory.Queries);Assert.Equal(5,f.Search.Results.Count);
    });
    private static string[] Names(IEnumerable<PublicGameSearchRow> rows)=>rows.Select(r=>r.Name).ToArray();

    [Fact]
    public Task ClickingACardUsesItWithoutQueueingAndKeepsTheGrid()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();
        var card=f.Search.VisibleResults[1];await f.Search.UseCardAsync(card);
        Assert.Equal(102,f.Browser.Destination!.PlaceId);Assert.Null(f.Browser.Destination.JobId);Assert.Equal(1,f.Directory.GameRequests);
        Assert.True(card.IsCurrent);Assert.False(f.Search.VisibleResults[0].IsCurrent);
        Assert.Equal(2,f.Search.Results.Count);Assert.Equal(2,f.Search.VisibleResults.Count);Assert.Equal("",f.Browser.LinkInput);
        Assert.Equal(0,f.Servers.Calls);Assert.Contains("selected",f.Search.Status);
        await f.Search.UseCardAsync(f.Search.VisibleResults[0]);Assert.Equal(101,f.Browser.Destination!.PlaceId);
        Assert.True(f.Search.VisibleResults[0].IsCurrent);Assert.False(card.IsCurrent);Assert.Equal(2,f.Search.Results.Count);Assert.Equal(2,f.Directory.GameRequests);
        // A row that is not on the current page is never used.
        await f.Search.UseCardAsync(new PublicGameSearchRow(new(9,99,"Foreign")));Assert.Equal(101,f.Browser.Destination!.PlaceId);Assert.Equal(2,f.Directory.GameRequests);
    });

    // Clicking a card already uses it; there is no separate select-then-use step to describe.
    [Fact]
    public Task ResultsCaptionSaysChoosingACardUsesIt()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();
        Assert.Contains("2 public results on this page",f.Search.PageSummary);Assert.Contains("Choose a card to use it.",f.Search.PageSummary);
        Assert.DoesNotContain("Select one",f.Search.PageSummary);
    });

    [Fact]
    public Task ShareLinkInFindBoxIsRoutedAndCleared()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Browser.UsePreferredPlace(19);
        f.Browser.LinkInput="https://www.roblox.com/share?code=4b9c7e0ae7b2f84bb7a7d24f6d0f6c2e&type=Server";await f.Search.SearchOrUseAsync();
        Assert.Equal(1,f.ShareLinksRouted);var routed=f.RoutedShareLink!;Assert.Equal(RobloxShareLinkType.Server,routed.Type);Assert.Equal(32,routed.Code.Length);
        Assert.Equal("",f.Browser.LinkInput);Assert.Empty(f.Directory.Queries);Assert.Equal(0,f.Directory.GameRequests);Assert.Equal(19,f.Browser.Destination!.PlaceId);
        Assert.Empty(f.Search.Results);Assert.DoesNotContain("4b9c7e0a",f.Search.Status);Assert.Contains("Private server",f.Search.Status);
        // Without a handler the old behaviour stands: the code is discarded and nothing is searched or requested.
        await using var bare=new DestinationSearchViewModel(f.Directory,f.Browser,f.Details,()=>f.Allowed,clock:f.Clock);
        f.Browser.LinkInput="https://www.roblox.com/share?code=4b9c7e0ae7b2f84bb7a7d24f6d0f6c2e&type=Server";await bare.SearchOrUseAsync();
        Assert.Equal(1,f.ShareLinksRouted);Assert.Equal("",f.Browser.LinkInput);Assert.Empty(f.Directory.Queries);Assert.Equal(0,f.Directory.GameRequests);Assert.DoesNotContain("4b9c7e0a",bare.Status);
    });

    [Fact]
    public Task ExplicitNameSearchKeepsTheOldRequestUntilARealPageRowIsChosen()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Browser.UsePreferredPlace(19);f.Browser.LinkInput="Fisch";
        Assert.Empty(f.Directory.Queries);Assert.Equal(19,f.Browser.Destination!.PlaceId);
        await f.Search.SearchOrUseAsync();Assert.Single(f.Directory.Queries);Assert.Null(f.Search.SelectedResult);
        Assert.Equal(19,f.Browser.Destination.PlaceId);Assert.Equal(0,f.Directory.GameRequests);
        await f.Search.UseCardAsync(f.Search.Results[1]);Assert.Same(f.Search.Results[1],f.Search.SelectedResult);
        Assert.Equal(102,f.Browser.Destination!.PlaceId);Assert.Null(f.Browser.Destination.JobId);
        Assert.Equal(1,f.Directory.GameRequests);Assert.Equal(102,f.Details.Game!.RequestedPlaceId);Assert.Equal("",f.Browser.LinkInput);
        // Using a result keeps the page: the chosen card is marked current so the grid can show it.
        Assert.Equal(2,f.Search.Results.Count);Assert.True(f.Search.Results[1].IsCurrent);Assert.False(f.Search.Results[0].IsCurrent);
        Assert.Contains("unverified",f.Search.Status);Assert.Equal(0,f.Servers.Calls);
    });

    [Theory]
    [InlineData("42",42,null)]
    [InlineData("https://www.roblox.com/games/43/Subplace",43,null)]
    [InlineData("https://www.roblox.com/games/start?placeId=44&gameInstanceId=11111111-2222-3333-4444-555555555555",44,"11111111-2222-3333-4444-555555555555")]
    public Task DirectUsePreservesTheRequestedSubplaceAndJobAndLoadsExistingDetails(string input,long place,string? job)=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Browser.LinkInput=input;await f.Search.SearchOrUseAsync();
        Assert.Empty(f.Directory.Queries);Assert.Equal(place,f.Browser.Destination!.PlaceId);Assert.Equal(job,f.Browser.Destination.JobId);
        Assert.Equal(1,f.Directory.GameRequests);Assert.Equal(place,f.Details.Game!.RequestedPlaceId);Assert.Equal(100,f.Details.Game.RootPlaceId);
        Assert.Empty(f.Search.Results);Assert.Equal(0,f.Servers.Calls);
    });

    [Theory]
    [InlineData("https://www.roblox.com/share?code=synthetic-private&type=Server")]
    [InlineData("https://www.roblox.com/games/42?privateServerLinkCode=synthetic-private")]
    [InlineData("roblox://placeId=42&accessCode=synthetic-reserved")]
    [InlineData("https://evil.invalid/games/42")]
    [InlineData("https://www.roblox.com/games/%GG")]
    [InlineData("9223372036854775808")]
    [InlineData("cookie:synthetic-secret")]
    [InlineData("private code synthetic-private")]
    [InlineData("12345678-1234-4234-8234-123456789abc")]
    public Task RejectedNameSyntaxUsesOnlyThePureParserAndNeverFallsBackToNameHttp(string input)=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Browser.LinkInput=input;await f.Search.SearchOrUseAsync();
        Assert.Empty(f.Directory.Queries);Assert.Null(f.Browser.Destination);Assert.Equal(0,f.Directory.GameRequests);Assert.Equal("",f.Browser.LinkInput);
        Assert.Empty(f.Search.Results);Assert.Null(f.Search.SelectedResult);Assert.DoesNotContain("synthetic-",f.Search.Status);
    });

    [Fact]
    public Task ChangingInputOrExternalDestinationInvalidatesTheWholePageWithoutAnotherRequest()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();var old=f.Search.Results[0];
        f.Browser.LinkInput="A different name";await f.Search.UseCardAsync(old);
        Assert.Null(f.Search.SelectedResult);Assert.Null(f.Browser.Destination);Assert.Empty(f.Search.Results);Assert.Single(f.Directory.Queries);
        await f.Search.SearchOrUseAsync();await f.Search.UseCardAsync(f.Search.Results[0]);Assert.NotNull(f.Search.SelectedResult);f.Browser.UsePreferredPlace(789);
        Assert.Empty(f.Search.Results);Assert.Null(f.Search.SelectedResult);Assert.Equal(789,f.Browser.Destination!.PlaceId);Assert.Equal(2,f.Directory.Queries.Count);
    });

    [Theory]
    [InlineData("query")]
    [InlineData("session")]
    [InlineData("stale")]
    [InlineData("future")]
    public Task UnrelatedOrStalePublicPagesCannotBecomeSelectable(string fault)=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Directory.SearchResponse=(query)=>
        {
            var page=f.Directory.Page(query);
            return SearchTest.Ok(fault switch {"query"=>page with {Query=query with {Text="Other"}},"session"=>page with {Query=query with {SessionId=Guid.NewGuid()}},
                "stale"=>page with {RetrievedUtc=f.Clock.Utc.AddMinutes(-6)},_=>page with {RetrievedUtc=f.Clock.Utc.AddSeconds(2)}});
        };
        f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();Assert.Empty(f.Search.Results);Assert.Null(f.Search.SelectedResult);
        Assert.Contains("could not finish",f.Search.Status);Assert.Equal(0,f.Directory.GameRequests);
    });

    [Fact]
    public Task PagingBindsTheOriginalSessionAndStopsACycleWhileKeepingTheLastValidPage()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Directory.SearchResponse=q=>SearchTest.Ok(f.Directory.Page(q,q.PageToken switch {null=>"A","A"=>"B",_=>"A"}));
        f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();await f.Search.NextAsync();await f.Search.NextAsync();
        Assert.Equal(new string?[]{null,"A","B"},f.Directory.Queries.Select(q=>q.PageToken));
        Assert.Single(f.Directory.Queries.Select(q=>q.SessionId).Distinct());Assert.Equal(2,f.Search.Results.Count);Assert.Null(f.Search.SelectedResult);
        Assert.False(f.Search.CanNext);Assert.Contains("repeated",f.Search.PageSummary);await f.Search.NextAsync();Assert.Equal(3,f.Directory.Queries.Count);
        await f.Search.SearchOrUseAsync();Assert.True(f.Search.CanNext);Assert.NotEqual(f.Directory.Queries[0].SessionId,f.Directory.Queries[^1].SessionId);
    });

    [Fact]
    public Task ManualPageBudgetIsBoundedAndRefiningStartsANewIndependentSession()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Directory.SearchResponse=q=>SearchTest.Ok(f.Directory.Page(q,"page"+(f.Directory.Queries.Count+1)));
        f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();for(int i=1;i<10;i++)await f.Search.NextAsync();
        Assert.False(f.Search.CanNext);Assert.Contains("ten-page",f.Search.Status);Assert.Equal(10,f.Directory.Queries.Count);Assert.Equal(2,f.Search.Results.Count);
        await f.Search.NextAsync();Assert.Equal(10,f.Directory.Queries.Count);f.Browser.LinkInput="Fisch adventure";await f.Search.SearchOrUseAsync();Assert.True(f.Search.CanNext);
    });

    [Fact]
    public Task LongProviderCooldownSendsNoRequestBeforeTheFullDeadline()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Directory.SearchResponse=_=>new(PublicDirectoryState.Deferred,null,"Raw provider text must not be displayed",TimeSpan.FromHours(2));
        f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();f.Clock.Utc=f.Clock.Utc.AddMinutes(119);await f.Search.SearchOrUseAsync();
        Assert.Single(f.Directory.Queries);Assert.DoesNotContain("Raw provider",f.Search.Status);
        f.Clock.Utc=f.Clock.Utc.AddMinutes(1);await f.Search.SearchOrUseAsync();Assert.Equal(2,f.Directory.Queries.Count);
    });

    [Fact]
    public Task CallerCancellationStopsItsStatusAndReenablesTheActualBoundButtons()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Directory.HoldSearch=true;f.Browser.LinkInput="Fisch";
        var search=new Button{DataContext=f.Search};search.SetBinding(UIElement.IsEnabledProperty,new Binding(nameof(f.Search.CanSearch)));
        var next=new Button{DataContext=f.Search};next.SetBinding(UIElement.IsEnabledProperty,new Binding(nameof(f.Search.CanNext)));
        Assert.True(search.IsEnabled);using var stop=new CancellationTokenSource();Task active=f.Search.SearchOrUseAsync(stop.Token);
        try {await f.Directory.SearchEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));Assert.False(search.IsEnabled);stop.Cancel();}
        finally {f.Directory.ReleaseSearch();await active.WaitAsync(TimeSpan.FromSeconds(5));}
        await Dispatcher.Yield(DispatcherPriority.DataBind);Assert.False(f.Search.IsBusy);Assert.True(search.IsEnabled);Assert.False(next.IsEnabled);Assert.Contains("stopped",f.Search.Status);
        f.Directory.HoldSearch=false;f.Directory.SearchResponse=q=>SearchTest.Ok(f.Directory.Page(q,"next"));await f.Search.SearchOrUseAsync();
        await Dispatcher.Yield(DispatcherPriority.DataBind);Assert.True(search.IsEnabled);Assert.True(next.IsEnabled);
        // A card on the new page uses its game through the command the card button is bound to (UseCard, the card as parameter).
        Assert.True(f.Search.UseCard.CanExecute(f.Search.VisibleResults[0]));f.Search.UseCard.Execute(f.Search.VisibleResults[0]);await f.Search.UseCard.Execution;
        Assert.Equal(101,f.Browser.Destination!.PlaceId);Assert.True(f.Search.VisibleResults[0].IsCurrent);
    });

    [Fact]
    public Task IgnoredCancellationRemainsOwnedAndLateSearchCannotReplaceNewInputOrKeepDisposalFromJoining()=>SearchTest.OnUi(async()=>
    {
        var f=new SearchTest();f.Directory.HoldSearch=true;f.Browser.LinkInput="Fisch";Task active=f.Search.SearchOrUseAsync();Task? disposal=null;
        try
        {
            await f.Directory.SearchEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));f.Browser.LinkInput="https://www.roblox.com/games/42";
            await f.Search.SearchOrUseAsync();Assert.Single(f.Directory.Queries);Assert.True(f.Directory.SearchToken.IsCancellationRequested);Assert.True(f.Search.IsBusy);
            disposal=f.DisposeAsync().AsTask();Assert.False(disposal.IsCompleted);Assert.False(active.IsCompleted);
        }
        finally {f.Directory.ReleaseSearch();await active.WaitAsync(TimeSpan.FromSeconds(5));if(disposal is not null)await disposal.WaitAsync(TimeSpan.FromSeconds(5));else await f.DisposeAsync();}
        Assert.Empty(f.Search.Results);Assert.Null(f.Browser.Destination);Assert.Equal(0,f.Directory.GameRequests);
    });

    [Fact]
    public Task ReentrantCancelDuringPublicationStopsRemainingRowsAndDetails()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Search.Results.CollectionChanged+=(_,_)=>{if(f.Search.Results.Count>0)f.Search.CancelWork();};
        f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();Assert.Empty(f.Search.Results);Assert.Equal("",f.Browser.LinkInput);
        Assert.Null(f.Browser.Destination);Assert.Equal(0,f.Directory.GameRequests);Assert.Contains("stopped",f.Search.Status);Assert.False(f.Search.IsBusy);
    });

    [Fact]
    public Task ExpiredSelectionAndEmptyOrFailurePagesNeverChooseADestination()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();var card=f.Search.Results[0];
        f.Clock.Utc=f.Clock.Utc.AddMinutes(6);await f.Search.UseCardAsync(card);Assert.Null(f.Browser.Destination);Assert.Empty(f.Search.Results);
        Assert.Null(f.Search.SelectedResult);Assert.Contains("expired",f.Search.Status);
        f.Directory.SearchResponse=q=>SearchTest.Ok(f.Directory.Page(q) with {Items=[]});await f.Search.SearchOrUseAsync();Assert.Contains("No public",f.Search.Status);
        f.Directory.SearchResponse=_=>new(PublicDirectoryState.Failure,null,"synthetic-sensitive-response");await f.Search.SearchOrUseAsync();Assert.Empty(f.Search.Results);
        Assert.DoesNotContain("synthetic-sensitive",f.Search.Status);Assert.Null(f.Browser.Destination);Assert.Equal(0,f.Directory.GameRequests);
    });

    [Fact]
    public Task ExternalDestinationChangeDuringOwnedDetailsFencesAllLatePublication()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Directory.HoldGame=true;f.Browser.LinkInput="42";Task active=f.Search.SearchOrUseAsync();
        try {await f.Directory.GameEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));f.Browser.UsePreferredPlace(77);Assert.True(f.Directory.GameToken.IsCancellationRequested);}
        finally {f.Directory.ReleaseGame();await active.WaitAsync(TimeSpan.FromSeconds(5));}
        Assert.Equal(77,f.Browser.Destination!.PlaceId);Assert.Null(f.Details.Game);Assert.Contains("changed",f.Search.Status);Assert.Empty(f.Search.Results);
    });

    [Fact]
    public Task CancellingAWaitForBorrowedManualDetailsDoesNotCancelOrJoinThatUnrelatedRequest()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Directory.HoldGame=true;f.Browser.UsePreferredPlace(42);Task manual=f.Details.LoadAsync();
        try
        {
            await f.Directory.GameEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));f.Browser.LinkInput="42";Task use=f.Search.SearchOrUseAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);Assert.True(f.Search.IsBusy);f.Search.CancelWork();
            await use.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(f.Directory.GameToken.IsCancellationRequested);Assert.False(manual.IsCompleted);
            Assert.Equal(1,f.Directory.GameRequests);Assert.Equal(42,f.Browser.Destination!.PlaceId);Assert.False(f.Search.IsBusy);
        }
        finally {f.Directory.ReleaseGame();await manual.WaitAsync(TimeSpan.FromSeconds(5));}
        Assert.Equal(42,f.Details.Game!.RequestedPlaceId);
    });
}

internal sealed class SearchTest:IAsyncDisposable
{
    public readonly SearchClock Clock=new();
    public readonly SearchDirectory Directory;
    public readonly SearchServers Servers=new();
    public readonly ServerBrowserViewModel Browser;
    public readonly GameDetailsViewModel Details;
    public readonly DestinationSearchViewModel Search;
    public bool Allowed=true;
    public int ShareLinksRouted;public RobloxShareLink? RoutedShareLink;
    public SearchTest(bool art=true)
    {
        Directory=new(Clock);Browser=new(Servers,()=>Allowed,Clock);Details=new(Directory,Browser,()=>Allowed,clock:Clock);
        Search=new(Directory,Browser,Details,()=>Allowed,clock:Clock,art:art?Directory:null,shareLinkHandler:link=>{ShareLinksRouted++;RoutedShareLink?.Dispose();RoutedShareLink=link;return Task.CompletedTask;});
    }
    public async ValueTask DisposeAsync(){await Search.DisposeAsync();await Details.DisposeAsync();await Browser.DisposeAsync();RoutedShareLink?.Dispose();}
    public static PublicDirectoryResult<T> Ok<T>(T value) where T:class=>new(PublicDirectoryState.Success,value,"Synthetic public information");
    public static Task OnUi(Func<Task> action)=>OverviewWorkspacePresentationTests.OnSta(action);
}

internal sealed class SearchClock:TimeProvider
{public DateTimeOffset Utc=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Utc;}

internal sealed class SearchServers:IRobloxPublicServerDirectory
{
    public int Calls;
    public Task<RobloxPublicServerResult> BrowseAsync(RobloxPublicServerQuery query,CancellationToken ct)
    {Calls++;throw new InvalidOperationException("Unexpected synthetic server HTTP request");}
}

internal sealed class SearchDirectory(SearchClock clock):IPublicRobloxDirectory,IPublicRobloxGameDirectory,IPublicRobloxGameSearchDirectory,IPublicRobloxGameArtDirectory
{
    private static readonly byte[] CardPng=PublicAccountPresentationTests.Png(width:384,height:216);
    public readonly List<long[]> ArtBatches=[];
    public bool HoldArtFetch;
    public int ArtFetches,ActiveArtFetches,MaxConcurrentArtFetches;
    public CancellationToken ArtToken;
    public Func<long,PublicRobloxThumbnailState>? ArtState;
    public TaskCompletionSource ArtFetchEntered {get;set;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _artRelease=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void ReleaseArtFetch()=>_artRelease.TrySetResult();
    // One-shot answers, used in order before the synthetic success: a local busy refusal or a provider pause.
    public readonly Queue<PublicDirectoryResult<IReadOnlyList<PublicRobloxGameThumbnail>>> ArtBatchResponses=new();
    public readonly Queue<PublicDirectoryResult<EncodedPublicGameIcon>> ArtFetchResponses=new();
    public Task<PublicDirectoryResult<IReadOnlyList<PublicRobloxGameThumbnail>>> GetGameThumbnailsAsync(IReadOnlyList<long> universeIds,CancellationToken ct)
    {
        ArtBatches.Add(universeIds.ToArray());
        if(ArtBatchResponses.TryDequeue(out var answer)) return Task.FromResult(answer);
        IReadOnlyList<PublicRobloxGameThumbnail> items=universeIds.Select(id=>{var state=ArtState?.Invoke(id)??PublicRobloxThumbnailState.Ready;
            return new PublicRobloxGameThumbnail(id,state==PublicRobloxThumbnailState.Ready?new Uri("https://tr.rbxcdn.com/synthetic/"+id+".png"):null,state,clock.Utc);}).ToArray();
        return Task.FromResult(SearchTest.Ok(items));
    }
    // A held download returns normally after release even when its token was cancelled: publication must be fenced, not just cancelled.
    public async Task<PublicDirectoryResult<EncodedPublicGameIcon>> FetchGameThumbnailAsync(PublicRobloxGameThumbnail thumbnail,CancellationToken ct)
    {
        ArtFetches++;ArtToken=ct;int active=++ActiveArtFetches;if(active>MaxConcurrentArtFetches)MaxConcurrentArtFetches=active;ArtFetchEntered.TrySetResult();
        try {if(HoldArtFetch)await _artRelease.Task;if(ArtFetchResponses.TryDequeue(out var answer))return answer;return SearchTest.Ok(new EncodedPublicGameIcon(CardPng,"image/png",clock.Utc));}
        finally {ActiveArtFetches--;}
    }
    public readonly List<PublicRobloxGameSearchQuery> Queries=[];
    public Func<PublicRobloxGameSearchQuery,PublicDirectoryResult<PublicRobloxGameSearchPage>>? SearchResponse;
    public bool HoldSearch,HoldGame;
    public int GameRequests,ProfileRequests;
    public CancellationToken SearchToken,GameToken;
    public TaskCompletionSource SearchEntered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource GameEntered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _searchRelease=new(TaskCreationOptions.RunContinuationsAsynchronously),_gameRelease=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void ReleaseSearch()=>_searchRelease.TrySetResult();public void ReleaseGame()=>_gameRelease.TrySetResult();
    public PublicRobloxGameSearchPage Page(PublicRobloxGameSearchQuery query,string? next=null)=>new(query,[new(201,101,"Synthetic experience",null,null,null),new(202,102,"Second experience",7,"Public creator",17,true)],next,clock.Utc);
    public PublicRobloxGameSearchPage Page(PublicRobloxGameSearchQuery query,int count,string? next=null)=>new(query,Enumerable.Range(1,count).Select(i=>new PublicRobloxGameSearchResult(1000+i,2000+i,"Game "+i)).ToArray(),next,clock.Utc);
    public async Task<PublicDirectoryResult<PublicRobloxGameSearchPage>> SearchGamesAsync(PublicRobloxGameSearchQuery query,CancellationToken ct)
    {Queries.Add(query);SearchToken=ct;SearchEntered.TrySetResult();if(HoldSearch)await _searchRelease.Task;return SearchResponse?.Invoke(query)??SearchTest.Ok(Page(query));}
    public async Task<PublicDirectoryResult<PublicRobloxGame>> LookupGameAsync(long requestedPlaceId,CancellationToken ct)
    {GameRequests++;GameToken=ct;GameEntered.TrySetResult();if(HoldGame)await _gameRelease.Task;return SearchTest.Ok(new PublicRobloxGame(requestedPlaceId,201,100,"Synthetic details",7,"Creator",PublicRobloxCreatorKind.User,17,20,clock.Utc));}
    public Task<PublicDirectoryResult<PublicRobloxGameIcon>> GetGameIconAsync(long universeId,CancellationToken ct)=>Task.FromResult(new PublicDirectoryResult<PublicRobloxGameIcon>(PublicDirectoryState.Unknown,null,"Synthetic optional image unavailable"));
    public Task<PublicDirectoryResult<EncodedPublicGameIcon>> FetchGameIconAsync(PublicRobloxGameIcon icon,CancellationToken ct)=>throw new InvalidOperationException("Unexpected synthetic icon download");
    public Task<PublicDirectoryResult<PublicRobloxUser>> LookupUserAsync(long userId,CancellationToken ct){ProfileRequests++;throw new InvalidOperationException("Unexpected synthetic identity request");}
    public Task<PublicDirectoryResult<PublicRobloxAvatar>> GetAvatarAsync(long userId,CancellationToken ct)=>throw new InvalidOperationException("Unexpected synthetic avatar request");
    public Task<PublicDirectoryResult<EncodedPublicAvatar>> FetchAvatarAsync(PublicRobloxAvatar avatar,CancellationToken ct)=>throw new InvalidOperationException("Unexpected synthetic avatar download");
    public void ClearMemoryCache(){}
}
