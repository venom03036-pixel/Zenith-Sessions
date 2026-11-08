using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// Card pictures ride on the shared search fixture: a synthetic art directory, no Roblox contact, no launches.
public sealed class GameCardArtLoaderTests
{
    [Fact]
    public Task VisibleCardsGetPicturesFromOneBatchAndSequentialDownloads()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Directory.SearchResponse=q=>SearchTest.Ok(f.Directory.Page(q,30));
        f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();await f.Search.ArtOperation;
        Assert.Single(f.Directory.ArtBatches);Assert.Equal(12,f.Directory.ArtBatches[0].Length);Assert.Equal(12,f.Directory.ArtFetches);
        Assert.Equal(1,f.Directory.MaxConcurrentArtFetches);
        Assert.All(f.Search.VisibleResults,row=>{Assert.True(row.HasArt);var pixels=Assert.IsAssignableFrom<BitmapSource>(row.Art);Assert.Equal(384,pixels.PixelWidth);Assert.True(pixels.IsFrozen);});
        Assert.All(f.Search.Results.Skip(12),row=>Assert.False(row.HasArt)); // Hidden cards never download.
        f.Search.ShowMore.Execute(null);await f.Search.ArtOperation;
        Assert.Equal(2,f.Directory.ArtBatches.Count);Assert.Equal(24,f.Directory.ArtFetches);Assert.All(f.Search.VisibleResults,row=>Assert.True(row.HasArt));
    });

    [Fact]
    public Task NewSearchDropsPicturesFromThePreviousGeneration()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Directory.HoldArtFetch=true;f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();
        var old=f.Search.Results.ToArray();await f.Directory.ArtFetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        f.Browser.LinkInput="Fisch again";Task second=f.Search.SearchOrUseAsync();
        Assert.True(f.Directory.ArtToken.IsCancellationRequested);
        f.Directory.HoldArtFetch=false;f.Directory.ReleaseArtFetch();await second;await f.Search.ArtOperation;
        Assert.All(old,row=>Assert.False(row.HasArt));Assert.Equal(2,f.Search.VisibleResults.Count);Assert.All(f.Search.VisibleResults,row=>Assert.True(row.HasArt));
        Assert.Equal(2,f.Directory.ArtBatches.Count);
    });

    [Fact]
    public Task ArtDownloadsOneAtATimeAndYieldsToDetails()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Directory.SearchResponse=q=>SearchTest.Ok(f.Directory.Page(q,12));
        f.Directory.HoldArtFetch=true;f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();
        await f.Directory.ArtFetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await f.Search.UseCardAsync(f.Search.VisibleResults[3]); // Details load completes while a picture download is held.
        Assert.Equal(2004,f.Browser.Destination!.PlaceId);Assert.Equal(1,f.Directory.GameRequests);Assert.Equal(2004,f.Details.Game!.RequestedPlaceId);
        Assert.True(f.Search.VisibleResults[3].IsCurrent);Assert.Equal(1,f.Directory.ActiveArtFetches);Assert.False(f.Directory.ArtToken.IsCancellationRequested);
        f.Directory.HoldArtFetch=false;f.Directory.ReleaseArtFetch();await f.Search.ArtOperation;
        Assert.Equal(1,f.Directory.MaxConcurrentArtFetches);Assert.Equal(12,f.Directory.ArtFetches);Assert.All(f.Search.VisibleResults,row=>Assert.True(row.HasArt));
    });

    [Fact]
    public Task CancelWorkClearsPicturesAndCancelsAHeldDownload()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();await f.Search.ArtOperation;
        var first=f.Search.Results.ToArray();Assert.All(first,row=>Assert.True(row.HasArt));
        f.Search.CancelWork();Assert.All(first,row=>Assert.False(row.HasArt));Assert.Empty(f.Search.VisibleResults);
        f.Directory.HoldArtFetch=true;f.Directory.ArtFetchEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();await f.Directory.ArtFetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var second=f.Search.Results.ToArray();f.Search.CancelWork();Assert.True(f.Directory.ArtToken.IsCancellationRequested);
        f.Directory.ReleaseArtFetch();await f.Search.ArtOperation;Assert.All(second,row=>Assert.False(row.HasArt));Assert.True(f.Search.ArtOperation.IsCompleted);
    });

    [Fact]
    public Task DisposalJoinsAHeldPictureDownload()=>SearchTest.OnUi(async()=>
    {
        var f=new SearchTest();f.Directory.HoldArtFetch=true;f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();
        await f.Directory.ArtFetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));var rows=f.Search.Results.ToArray();
        Task disposal=f.DisposeAsync().AsTask();Assert.False(disposal.IsCompleted);Assert.True(f.Directory.ArtToken.IsCancellationRequested);
        f.Directory.ReleaseArtFetch();await disposal.WaitAsync(TimeSpan.FromSeconds(5));Assert.All(rows,row=>Assert.False(row.HasArt));
    });

    [Fact]
    public Task MissingArtDirectoryLeavesPlaceholders()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest(art:false);f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();await f.Search.ArtOperation;
        Assert.Equal(2,f.Search.VisibleResults.Count);Assert.All(f.Search.VisibleResults,row=>Assert.False(row.HasArt));Assert.Empty(f.Directory.ArtBatches);
    });

    [Fact]
    public Task PendingBlockedAndUnavailablePicturesStayPlaceholdersWithoutRetries()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Directory.SearchResponse=q=>SearchTest.Ok(f.Directory.Page(q,3));
        f.Directory.ArtState=id=>id switch {1001=>PublicRobloxThumbnailState.Pending,1002=>PublicRobloxThumbnailState.Blocked,_=>PublicRobloxThumbnailState.Ready};
        f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();await f.Search.ArtOperation;
        Assert.False(f.Search.VisibleResults[0].HasArt);Assert.False(f.Search.VisibleResults[1].HasArt);Assert.True(f.Search.VisibleResults[2].HasArt);
        Assert.Equal(1,f.Directory.ArtFetches);
        f.Search.SelectedSort="Rating";await f.Search.ArtOperation; // Re-sorting never re-requests attempted pictures.
        Assert.Equal(1,f.Directory.ArtFetches);Assert.Single(f.Directory.ArtBatches);
    });

    // The shared two-slot gate turns a third concurrent lookup down locally (Deferred, no RetryAfter). That is not a
    // Roblox cooldown: the cards stay askable, so the next sort, page or Show more asks again.
    [Fact]
    public Task BusyArtBatchLeavesCardsRetryableOnTheNextTrigger()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Directory.SearchResponse=q=>SearchTest.Ok(f.Directory.Page(q,3));
        f.Directory.ArtBatchResponses.Enqueue(new(PublicDirectoryState.Deferred,null,"synthetic busy"));
        f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();await f.Search.ArtOperation;
        Assert.Single(f.Directory.ArtBatches);Assert.Equal(0,f.Directory.ArtFetches);Assert.All(f.Search.VisibleResults,row=>Assert.False(row.HasArt));
        f.Search.SelectedSort="Rating";await f.Search.ArtOperation;
        Assert.Equal(2,f.Directory.ArtBatches.Count);Assert.Equal(3,f.Directory.ArtFetches);Assert.All(f.Search.VisibleResults,row=>Assert.True(row.HasArt));
    });

    [Fact]
    public Task BusyPictureDownloadEndsTheRunAndLeavesTheRestRetryable()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Directory.SearchResponse=q=>SearchTest.Ok(f.Directory.Page(q,3));
        f.Directory.ArtFetchResponses.Enqueue(new(PublicDirectoryState.Deferred,null,"synthetic busy"));
        f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();await f.Search.ArtOperation;
        Assert.Equal(1,f.Directory.ArtFetches); // The run stops at the first refusal instead of sending more into a full gate.
        Assert.All(f.Search.VisibleResults,row=>Assert.False(row.HasArt));
        f.Search.SelectedSort="Rating";await f.Search.ArtOperation;
        Assert.Equal(2,f.Directory.ArtBatches.Count);Assert.Equal(4,f.Directory.ArtFetches);Assert.All(f.Search.VisibleResults,row=>Assert.True(row.HasArt));
    });

    [Fact]
    public Task ProviderPauseKeepsPlaceholdersWithoutRetries()=>SearchTest.OnUi(async()=>
    {
        await using var f=new SearchTest();f.Directory.SearchResponse=q=>SearchTest.Ok(f.Directory.Page(q,3));
        f.Directory.ArtBatchResponses.Enqueue(new(PublicDirectoryState.Deferred,null,"synthetic pause",TimeSpan.FromSeconds(30)));
        f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();await f.Search.ArtOperation;
        f.Search.SelectedSort="Rating";await f.Search.ArtOperation; // A Roblox cooldown is never re-asked by a re-sort.
        Assert.Single(f.Directory.ArtBatches);Assert.Equal(0,f.Directory.ArtFetches);Assert.All(f.Search.VisibleResults,row=>Assert.False(row.HasArt));
    });

    // A run fenced after a download, while its picture decodes off the UI thread, never showed that card. The card must
    // stay askable, or the next trigger (Show more, sort, showing the window again) leaves its placeholder for good.
    [Fact]
    public Task APictureFencedWhileDecodingStaysAskable()=>SearchTest.OnUi(async()=>
    {
        var directory=new SearchDirectory(new SearchClock());
        await using var loader=new GameCardArtLoader(directory,_=>{});
        var row=new PublicGameSearchRow(new PublicRobloxGameSearchResult(1001,2001,"Game 1"));
        Task first=loader.LoadAsync([row],1); // The synthetic batch and download answer at once, so this run is now decoding.
        Assert.Equal(1,directory.ArtFetches);Assert.False(first.IsCompleted);
        loader.Cancel();await first;Assert.False(row.HasArt);
        await loader.LoadAsync([row],2);
        Assert.Equal(2,directory.ArtFetches);Assert.True(row.HasArt);
    });

    // The running page never reads Active (only tests do), so a finished run must let go of its rows and their
    // decoded pictures on its own; otherwise every search, sort and "Show more" keeps its cards alive until disposal.
    [Fact]
    public Task FinishedRunsReleaseTheirRowsWithoutAnyoneReadingActive()=>SearchTest.OnUi(async()=>
    {
        var directory=new SearchDirectory(new SearchClock());
        await using var loader=new GameCardArtLoader(directory,_=>{});
        var rows=new List<WeakReference>();
        for(long generation=1;generation<=3;generation++)
        {
            var started=Start(loader,generation);await started.Work;rows.Add(started.Row);
            Assert.True(started.Work.IsCompletedSuccessfully);
        }
        Assert.Equal(3,directory.ArtFetches);
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        Assert.All(rows,row=>Assert.False(row.IsAlive,"A finished picture run still holds its card row (and decoded picture) alive."));
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task Work,WeakReference Row) Start(GameCardArtLoader loader,long generation)
    {
        var row=new PublicGameSearchRow(new PublicRobloxGameSearchResult(1000+generation,2000+generation,"Game "+generation));
        return (loader.LoadAsync([row],generation),new WeakReference(row));
    }
}
