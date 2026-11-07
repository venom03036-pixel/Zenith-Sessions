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
        var old=f.Search.Results.ToArray();await f.Directory.ArtFetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
        await f.Directory.ArtFetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
        f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();await f.Directory.ArtFetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second=f.Search.Results.ToArray();f.Search.CancelWork();Assert.True(f.Directory.ArtToken.IsCancellationRequested);
        f.Directory.ReleaseArtFetch();await f.Search.ArtOperation;Assert.All(second,row=>Assert.False(row.HasArt));Assert.True(f.Search.ArtOperation.IsCompleted);
    });

    [Fact]
    public Task DisposalJoinsAHeldPictureDownload()=>SearchTest.OnUi(async()=>
    {
        var f=new SearchTest();f.Directory.HoldArtFetch=true;f.Browser.LinkInput="Fisch";await f.Search.SearchOrUseAsync();
        await f.Directory.ArtFetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));var rows=f.Search.Results.ToArray();
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
}
