using System.IO;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class FavoriteDestinationsViewModelTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-favorites-vm-"+Guid.NewGuid().ToString("N"));
    private static FavoriteDestination Saved(string name="Saved destination")=>new(Guid.NewGuid(),name,123,null,1,Guid.NewGuid());
    private static FavoriteDestinationSnapshot Snapshot(params FavoriteDestination[] items)=>new(10,Array.AsReadOnly(items));
    [Fact]
    public async Task SaveRenameUseAndDeleteAreExplicitDurableMetadataActionsWithoutPublicLookupOrLaunch()
    {
        using var store=new FavoriteDestinationsStore(_root);var directory=new CountingDirectory();
        await using var browser=new ServerBrowserViewModel(directory);await using var vm=new FavoriteDestinationsViewModel(store,browser);
        await vm.LoadAsync();Assert.Empty(vm.Favorites);
        const string job="12345678-1234-4234-8234-123456789abc";
        browser.LinkInput="https://www.roblox.com/games/start?placeId=123&gameInstanceId="+job;browser.ResolveInput();
        Assert.Equal("",vm.NameInput);Assert.Equal("",browser.LinkInput);
        await vm.SaveCurrentDestinationAsync("友達 👑 — Weekend!");
        var first=Assert.Single(vm.Favorites);Assert.Equal(first,vm.SelectedFavorite);Assert.Equal(job,first.JobId);Assert.Equal(0,directory.Calls);
        var requested=browser.Destination;await vm.RenameSelectedAsync("Renamed destination (2)");var renamed=Assert.Single(vm.Favorites);
        Assert.Equal(first.Id,renamed.Id);Assert.Equal(2,renamed.Revision);Assert.Same(requested,browser.Destination);
        browser.UsePreferredPlace(456);vm.UseSelectedFavorite();Assert.Equal(123,browser.Destination!.PlaceId);Assert.Equal(job,browser.Destination.JobId);
        Assert.Equal(RobloxServerKind.Unknown,browser.Destination.ServerKind);Assert.Contains("request",vm.Status);Assert.Equal(0,directory.Calls);
        requested=browser.Destination;await vm.DeleteSelectedAsync();Assert.Empty(vm.Favorites);Assert.Null(vm.SelectedFavorite);
        Assert.Same(requested,browser.Destination);Assert.Empty((await store.LoadAsync(default)).Destinations);Assert.Equal(0,directory.Calls);
    }
    [Fact]
    public async Task UseFavoriteCommandSelectsTheGivenFavoriteWithoutLookup()
    {
        using var store=new FavoriteDestinationsStore(_root);var directory=new CountingDirectory();
        await using var browser=new ServerBrowserViewModel(directory);await using var vm=new FavoriteDestinationsViewModel(store,browser);
        await vm.LoadAsync();browser.UsePreferredPlace(123);await vm.SaveCurrentDestinationAsync("First");browser.UsePreferredPlace(456);await vm.SaveCurrentDestinationAsync("Second");
        var first=vm.Favorites.Single(favorite=>favorite.Name=="First");browser.UsePreferredPlace(789);
        vm.UseFavorite.Execute(first);Assert.Equal(123,browser.Destination!.PlaceId);Assert.Same(first,vm.SelectedFavorite);Assert.Equal(0,directory.Calls);
        vm.UseFavorite.Execute(new FavoriteDestination(Guid.NewGuid(),"Foreign",999,null,1,Guid.NewGuid()));
        Assert.Equal(123,browser.Destination!.PlaceId);Assert.Same(first,vm.SelectedFavorite); // Only listed favorites are used.
    }
    [Fact]
    public async Task FavoriteSaveUsesOnlyTheResolvedDestination()
    {
        using var store=new FavoriteDestinationsStore(_root);var directory=new CountingDirectory();
        await using var browser=new ServerBrowserViewModel(directory);await using var vm=new FavoriteDestinationsViewModel(store,browser);
        await vm.LoadAsync();browser.UsePreferredPlace(123);browser.LinkInput="456";
        await vm.SaveCurrentDestinationAsync("Pending");Assert.Empty(vm.Favorites);Assert.Contains("pending",vm.Status);
        browser.ResolveInput();await vm.SaveCurrentDestinationAsync("Reviewed");
        Assert.Equal(456,Assert.Single(vm.Favorites).PlaceId);Assert.Equal(0,directory.Calls);
    }
    [Fact]
    public async Task PrivateOrOpaqueLinksAndInvalidNamesCannotBeStoredOrCopiedAutomatically()
    {
        using var store=new FavoriteDestinationsStore(_root);var directory=new CountingDirectory();
        await using var browser=new ServerBrowserViewModel(directory);await using var vm=new FavoriteDestinationsViewModel(store,browser);
        await vm.LoadAsync();browser.LinkInput="https://www.roblox.com/games/123?privateServerLinkCode=secret-marker";browser.ResolveInput();
        await vm.SaveCurrentDestinationAsync("Do not store a private code");Assert.Empty(vm.Favorites);Assert.Equal("",vm.NameInput);
        browser.UsePreferredPlace(123);await vm.SaveCurrentDestinationAsync("https://roblox.com/games/123?accessCode=secret-marker");
        Assert.Empty(vm.Favorites);Assert.DoesNotContain("secret-marker",vm.Status);Assert.Equal(0,directory.Calls);
        Assert.DoesNotContain("secret-marker",await File.ReadAllTextAsync(store.Path));Assert.False(File.Exists(store.Path+".bak"));
    }
    [Fact]
    public async Task UsingAFavoriteCancelsTheOldPublicBrowseWithoutStartingAnotherRequest()
    {
        var saved=Saved("Requested game");var store=new GatedStore {Current=Snapshot(saved)};var directory=new PendingDirectory();
        await using var browser=new ServerBrowserViewModel(directory);await using var vm=new FavoriteDestinationsViewModel(store,browser);
        await vm.LoadAsync();vm.SelectedFavorite=Assert.Single(vm.Favorites);browser.UsePreferredPlace(456);browser.Browse.Execute(null);
        try
        {
            vm.UseSelectedFavorite();Assert.True(directory.Token.IsCancellationRequested);Assert.Equal(1,directory.Calls);
            Assert.Equal(saved.PlaceId,browser.Destination!.PlaceId);Assert.Null(browser.Destination.JobId);
            directory.Pending.SetResult(new(RobloxPublicServerStatus.Complete,[],null,null,"Late previous place"));await browser.Browse.Execution;
            Assert.Empty(browser.Servers);Assert.False(browser.CanNext);Assert.NotEqual("Late previous place",browser.Status);Assert.Equal(1,directory.Calls);
        }
        finally {directory.Pending.TrySetResult(new(RobloxPublicServerStatus.Complete,[],null,null,"Released fixture"));}
    }
    [Fact]
    public async Task ACanUseChangeDuringAwaitPreventsLateListPublicationAndFavoriteUse()
    {
        var initial=Saved("Initial");var newer=Saved("Newer");var store=new GatedStore {Current=Snapshot(initial)};bool allowed=true;
        var directory=new CountingDirectory();await using var browser=new ServerBrowserViewModel(directory,()=>allowed);
        await using var vm=new FavoriteDestinationsViewModel(store,browser,()=>allowed);await vm.LoadAsync();vm.SelectedFavorite=initial;
        store.PendingLoad=new(TaskCreationOptions.RunContinuationsAsynchronously);var loading=vm.LoadAsync();
        try
        {
            Assert.True(vm.IsBusy);allowed=false;store.Current=Snapshot(newer);store.PendingLoad.SetResult(store.Current);await loading;
        }
        finally {store.PendingLoad.TrySetResult(Snapshot(newer));}
        Assert.Equal(initial,Assert.Single(vm.Favorites));Assert.False(vm.CanUseFavorite);vm.UseSelectedFavorite();Assert.Null(browser.Destination);
        allowed=true;store.PendingLoad=null;await vm.LoadAsync();Assert.Equal(newer,Assert.Single(vm.Favorites));Assert.Null(vm.SelectedFavorite);
        Assert.Equal(0,directory.Calls);
    }
    [Fact]
    public async Task CancelFencesTheOldLoadAndOverlappingSaveIsRejectedUntilOwnedWorkStops()
    {
        var old=Saved("Old");var store=new GatedStore {Current=Snapshot(old)};var directory=new CountingDirectory();
        await using var browser=new ServerBrowserViewModel(directory);await using var vm=new FavoriteDestinationsViewModel(store,browser);
        browser.UsePreferredPlace(123);store.PendingLoad=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var loading=vm.LoadAsync();
        try
        {
            vm.CancelWork();Assert.True(store.LastToken.IsCancellationRequested);Assert.Empty(vm.Favorites);
            await vm.SaveCurrentDestinationAsync("New");Assert.Equal(0,store.Saves);Assert.True(vm.IsBusy);
            store.PendingLoad.SetResult(Snapshot(old));await loading;Assert.Empty(vm.Favorites);Assert.False(vm.IsBusy);
        }
        finally {store.PendingLoad.TrySetResult(Snapshot(old));}
        store.PendingLoad=null;await vm.SaveCurrentDestinationAsync("New");Assert.Equal(1,store.Saves);
        Assert.Equal(2,vm.Favorites.Count);Assert.Equal("New",vm.SelectedFavorite!.Name);Assert.Equal(0,directory.Calls);
    }
    [Fact]
    public async Task DisposeKeepsOwnershipUntilACancellationIgnoringStoreActuallyFinishes()
    {
        var store=new GatedStore {PendingSave=new(TaskCreationOptions.RunContinuationsAsynchronously)};var directory=new CountingDirectory();
        await using var browser=new ServerBrowserViewModel(directory);var vm=new FavoriteDestinationsViewModel(store,browser);browser.UsePreferredPlace(123);
        Task saving=vm.SaveCurrentDestinationAsync("Pending");
        try
        {
            Assert.True(vm.IsBusy);Assert.Equal(1,store.Saves);
            Task disposing=vm.DisposeAsync().AsTask();Assert.False(disposing.IsCompleted);Assert.False(store.Disposed);Assert.True(store.LastToken.IsCancellationRequested);
            Assert.Same(disposing,vm.DisposeAsync().AsTask());Assert.Empty(vm.Favorites);
            store.PendingSave.SetResult(Saved("Late result"));await saving.WaitAsync(TimeSpan.FromSeconds(5));await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(vm.Favorites);Assert.Null(vm.SelectedFavorite);Assert.False(vm.IsBusy);Assert.False(store.Disposed);
            int loads=store.Loads;await vm.LoadAsync();await vm.SaveCurrentDestinationAsync("After disposal");Assert.Equal(loads,store.Loads);Assert.Equal(1,store.Saves);
            Assert.Equal(0,directory.Calls);
        }
        finally
        {
            store.PendingSave.TrySetResult(Saved("Released fixture"));await vm.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));store.Dispose();
        }
        Assert.True(store.Disposed);
    }
    [Fact]
    public async Task ExternalCancellationOfACancellationIgnoringLoadHasAnExplicitStateWithoutPublishing()
    {
        var store=new GatedStore {PendingLoad=new(TaskCreationOptions.RunContinuationsAsynchronously)};var directory=new CountingDirectory();
        await using var browser=new ServerBrowserViewModel(directory);await using var vm=new FavoriteDestinationsViewModel(store,browser);
        using var stop=new CancellationTokenSource();var loading=vm.LoadAsync(stop.Token);stop.Cancel();store.PendingLoad.SetResult(Snapshot(Saved()));await loading;
        Assert.Empty(vm.Favorites);Assert.False(vm.IsBusy);Assert.Contains("cancelled",vm.Status);Assert.Equal(0,directory.Calls);
    }
    [Fact]
    public async Task CommandsAreAwaitableAndErrorsNeverEchoRawStoreMessages()
    {
        var store=new GatedStore {PendingLoad=new(TaskCreationOptions.RunContinuationsAsynchronously)};var directory=new CountingDirectory();
        await using var browser=new ServerBrowserViewModel(directory);await using var vm=new FavoriteDestinationsViewModel(store,browser);
        vm.Refresh.Execute(null);
        try
        {
            Assert.False(vm.Refresh.Execution.IsCompleted);Assert.False(vm.ActiveOperation.IsCompleted);
            store.PendingLoad.SetResult(Snapshot(Saved()));await vm.Refresh.Execution;Assert.Single(vm.Favorites);
        }
        finally {store.PendingLoad.TrySetResult(Snapshot());}
        store.PendingLoad=null;store.LoadFailure=new InvalidDataException("raw secret-marker exception");await vm.LoadAsync();
        Assert.DoesNotContain("secret-marker",vm.Status);Assert.Single(vm.Favorites);Assert.Contains("preserved",vm.Status);
        store.LoadFailure=null;store.Current=Snapshot();browser.UsePreferredPlace(123);
        store.SaveFailure=new FavoriteDestinationCapacityException();await vm.SaveCurrentDestinationAsync("No room");Assert.Contains("saved accounts are not limited",vm.Status);
        store.SaveFailure=new FavoriteDestinationConflictException();await vm.SaveCurrentDestinationAsync("Stale");Assert.Contains("Refresh",vm.Status);
        store.SaveFailure=new FavoriteDestinationStorageException(FavoriteDestinationStorageProblem.UnsupportedVersion,"secret-marker");
        await vm.SaveCurrentDestinationAsync("Unsupported");Assert.Contains("unsupported",vm.Status);Assert.DoesNotContain("secret-marker",vm.Status);
    }
    [Fact]
    public async Task UnexpectedOversizedSnapshotsAndForeignSelectionsCannotPublishOrRedirect()
    {
        var saved=Saved();var store=new GatedStore {Current=Snapshot(saved)};var directory=new CountingDirectory();
        await using var browser=new ServerBrowserViewModel(directory);await using var vm=new FavoriteDestinationsViewModel(store,browser);await vm.LoadAsync();
        store.Current=Snapshot(Enumerable.Range(0,FavoriteDestination.MaximumCount+1).Select(i=>Saved("Item "+i)).ToArray());await vm.LoadAsync();
        Assert.Equal(saved,Assert.Single(vm.Favorites));vm.SelectedFavorite=Saved("Foreign");Assert.False(vm.CanUseFavorite);vm.UseSelectedFavorite();Assert.Null(browser.Destination);
        Assert.Equal(0,directory.Calls);
    }
    private sealed class CountingDirectory : IRobloxPublicServerDirectory
    {
        public int Calls;
        public Task<RobloxPublicServerResult> BrowseAsync(RobloxPublicServerQuery query,CancellationToken ct)
        {Calls++;throw new InvalidOperationException("Favorites must not contact a public directory.");}
    }
    private sealed class PendingDirectory : IRobloxPublicServerDirectory
    {
        public TaskCompletionSource<RobloxPublicServerResult> Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token;
        public int Calls;
        public Task<RobloxPublicServerResult> BrowseAsync(RobloxPublicServerQuery query,CancellationToken ct)
        {Calls++;Token=ct;return Pending.Task;}
    }
    private sealed class GatedStore : IFavoriteDestinationStore,IDisposable
    {
        public FavoriteDestinationSnapshot Current=Snapshot();
        public TaskCompletionSource<FavoriteDestinationSnapshot>? PendingLoad;
        public TaskCompletionSource<FavoriteDestination>? PendingSave;
        public CancellationToken LastToken;
        public Exception? LoadFailure,SaveFailure;
        public int Loads,Saves;
        public bool Disposed;
        public Task<FavoriteDestinationSnapshot> LoadAsync(CancellationToken ct)
        {ObjectDisposedException.ThrowIf(Disposed,this);Loads++;LastToken=ct;return LoadFailure is { } failure?Task.FromException<FavoriteDestinationSnapshot>(failure):PendingLoad?.Task??Task.FromResult(Current);}
        public async Task<FavoriteDestination> SaveAsync(FavoriteDestination destination,CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(Disposed,this);Saves++;LastToken=ct;if(SaveFailure is { } failure) throw failure;
            var saved=PendingSave is { } pending?await pending.Task:destination with {Revision=destination.Revision+1,ChangeId=Guid.NewGuid()};
            ObjectDisposedException.ThrowIf(Disposed,this);
            Current=new(Current.Revision+1,Array.AsReadOnly(Current.Destinations.Where(item=>item.Id!=saved.Id).Append(saved).ToArray()));return saved;
        }
        public Task DeleteAsync(Guid id,long expectedRevision,Guid expectedChangeId,CancellationToken ct)
        {ObjectDisposedException.ThrowIf(Disposed,this);ct.ThrowIfCancellationRequested();Current=new(Current.Revision+1,Array.AsReadOnly(Current.Destinations.Where(item=>item.Id!=id).ToArray()));return Task.CompletedTask;}
        public void Dispose()=>Disposed=true;
    }
    public void Dispose()
    {
        string resolved=Path.GetFullPath(_root),temporary=Path.GetFullPath(Path.GetTempPath());
        if(Path.GetDirectoryName(resolved)!=temporary.TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(resolved).StartsWith("zenith-favorites-vm-",StringComparison.Ordinal))
            throw new InvalidOperationException("The owned favorites fixture path is invalid.");
        if(Directory.Exists(resolved)) Directory.Delete(resolved,true);
    }
}
