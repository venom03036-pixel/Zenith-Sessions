using System.IO;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// Actual view-model composition, synthetic public metadata and PNG only; no server/HTTP/client calls.
public sealed class GameDetailsMediaIntegrationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task UnavailableOptionalIconStillAllowsTheGalleryAndPreservesRequestedSubplace()
    {
        var clock = new GameGalleryViewModelTests.Clock(); await using var gallery = new GameGalleryViewModelTests.Directory(clock);
        var directory = new Games(clock, gallery) { IconUnavailable = true };
        await using var browser = new ServerBrowserViewModel(new Servers());
        await using var vm = new GameDetailsViewModel(directory, browser, () => true, clock: clock);
        browser.UsePreferredPlace(GameGalleryViewModelTests.Place); await vm.LoadAsync().WaitAsync(Deadline);
        Assert.NotNull(vm.Game); Assert.Equal(100, vm.Game.RootPlaceId); Assert.Equal(GameGalleryViewModelTests.Place, vm.Game.RequestedPlaceId);
        Assert.Equal(GameGalleryViewModelTests.Place, browser.Destination!.PlaceId); Assert.Null(vm.Icon); Assert.Equal(0, directory.IconDownloads);
        Assert.NotNull(vm.Gallery.Image); Assert.Equal((GameGalleryViewModelTests.Place, GameGalleryViewModelTests.Universe), Assert.Single(gallery.Lookups));
        Assert.Equal(1, directory.Lookups); Assert.Equal(1, directory.IconDescriptors);
    }

    [Theory]
    [InlineData("descriptor-throws")]
    [InlineData("fetch-throws")]
    [InlineData("invalid-descriptor")]
    public async Task OptionalIconFailurePreservesValidGameAndStillLoadsIndependentGallery(string failure)
    {
        var clock = new GameGalleryViewModelTests.Clock(); await using var gallery = new GameGalleryViewModelTests.Directory(clock);
        var directory = new Games(clock, gallery) { IconFailure = failure }; var failures = new List<Exception>();
        await using var browser = new ServerBrowserViewModel(new Servers());
        await using var vm = new GameDetailsViewModel(directory, browser, () => true, failures.Add, clock);
        browser.UsePreferredPlace(GameGalleryViewModelTests.Place); await vm.LoadAsync().WaitAsync(Deadline);
        Assert.NotNull(vm.Game); Assert.Equal(GameGalleryViewModelTests.Place, vm.Game.RequestedPlaceId); Assert.Null(vm.Icon);
        Assert.NotNull(vm.Gallery.Image); Assert.Single(gallery.Lookups); Assert.Single(gallery.Images);
        Assert.Equal(1, directory.IconDescriptors); Assert.Equal(failure == "fetch-throws" ? 1 : 0, directory.IconDownloads);
        var reported = Assert.Single(failures); Assert.DoesNotContain("private-icon-provider-marker", reported.Message);
        Assert.DoesNotContain("private-icon-provider-marker", vm.Status); Assert.DoesNotContain("private-icon-provider-marker", vm.Gallery.Status);
        Assert.Equal(GameGalleryViewModelTests.Place, browser.Destination!.PlaceId);
    }

    [Fact]
    public async Task GalleryFailureKeepsTheValidatedGameAndSquareIconAndRedactsProviderText()
    {
        var clock = new GameGalleryViewModelTests.Clock(); await using var gallery = new GameGalleryViewModelTests.Directory(clock)
        { Lookup = (_, _, _) => throw new InvalidOperationException("synthetic-private-gallery-provider-marker") };
        var directory = new Games(clock, gallery); var failures = new List<Exception>();
        await using var browser = new ServerBrowserViewModel(new Servers());
        await using var vm = new GameDetailsViewModel(directory, browser, () => true, failures.Add, clock);
        browser.UsePreferredPlace(GameGalleryViewModelTests.Place); await vm.LoadAsync().WaitAsync(Deadline);
        Assert.NotNull(vm.Game); Assert.NotNull(vm.Icon); Assert.Equal(150, vm.Icon.PixelWidth); Assert.True(vm.Icon.IsFrozen);
        Assert.Null(vm.Gallery.Image); Assert.Single(failures);
        Assert.DoesNotContain("synthetic-private-gallery-provider-marker", vm.Status); Assert.DoesNotContain("synthetic-private-gallery-provider-marker", vm.Gallery.Status);
        Assert.DoesNotContain("synthetic-private-gallery-provider-marker", failures[0].Message);
        Assert.Equal(GameGalleryViewModelTests.Place, browser.Destination!.PlaceId);
    }

    [Fact]
    public async Task CancelFromMainBusyNotificationIsOwnedBeforeAnyDirectoryOrGalleryInvocation()
    {
        var clock = new GameGalleryViewModelTests.Clock(); await using var gallery = new GameGalleryViewModelTests.Directory(clock); var directory = new Games(clock, gallery);
        await using var browser = new ServerBrowserViewModel(new Servers());
        await using var vm = new GameDetailsViewModel(directory, browser, () => true, clock: clock);
        bool cancelled = false; Task? owner = null;
        vm.PropertyChanged += (_, args) =>
        {
            if (cancelled || args.PropertyName != nameof(GameDetailsViewModel.IsBusy) || !vm.IsBusy) return;
            cancelled = true; owner = vm.ActiveOperation; vm.CancelWork();
        };
        browser.UsePreferredPlace(GameGalleryViewModelTests.Place); var operation = vm.LoadAsync(); await operation.WaitAsync(Deadline);
        Assert.True(cancelled); Assert.Same(operation, owner); Assert.Equal(0, directory.Lookups); Assert.Empty(gallery.Lookups);
        Assert.Null(vm.Game); Assert.Null(vm.Icon); Assert.Null(vm.Gallery.Image); Assert.False(vm.IsBusy);
        Assert.Contains("cleared", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelFromValidatedGameOrIconNotificationPreventsFurtherRequestsAndSuccessOverwrite(bool whenIcon)
    {
        var clock = new GameGalleryViewModelTests.Clock(); await using var gallery = new GameGalleryViewModelTests.Directory(clock); var directory = new Games(clock, gallery);
        await using var browser = new ServerBrowserViewModel(new Servers());
        await using var vm = new GameDetailsViewModel(directory, browser, () => true, clock: clock);
        bool cancelled = false;
        vm.PropertyChanged += (_, args) =>
        {
            if (cancelled || (whenIcon ? args.PropertyName != nameof(GameDetailsViewModel.Icon) || vm.Icon is null
                : args.PropertyName != nameof(GameDetailsViewModel.Game) || vm.Game is null)) return;
            cancelled = true; vm.CancelWork();
        };
        browser.UsePreferredPlace(GameGalleryViewModelTests.Place); await vm.LoadAsync().WaitAsync(Deadline);
        Assert.True(cancelled); Assert.Equal(1, directory.Lookups); Assert.Equal(whenIcon ? 1 : 0, directory.IconDescriptors);
        Assert.Equal(whenIcon ? 1 : 0, directory.IconDownloads); Assert.Empty(gallery.Lookups);
        Assert.Null(vm.Game); Assert.Null(vm.Icon); Assert.Null(vm.Gallery.Image); Assert.Contains("cleared", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DestinationChangeFencesAndJoinsAnIgnoringGalleryBeforeANewMetadataContextCanLoad()
    {
        var clock = new GameGalleryViewModelTests.Clock(); await using var gallery = new GameGalleryViewModelTests.Directory(clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gallery.Fetch = async (_, _) => { entered.TrySetResult(); await release.Task; return gallery.Encoded(); };
        var directory = new Games(clock, gallery);
        await using var browser = new ServerBrowserViewModel(new Servers());
        var vm = new GameDetailsViewModel(directory, browser, () => true, clock: clock);
        try
        {
            browser.UsePreferredPlace(GameGalleryViewModelTests.Place); var pending = vm.LoadAsync(); await entered.Task.WaitAsync(Deadline);
            browser.UsePreferredPlace(GameGalleryViewModelTests.Place + 1);
            Assert.Null(vm.Game); Assert.Null(vm.Icon); Assert.Null(vm.Gallery.Image); Assert.False(pending.IsCompleted);
            await vm.LoadAsync(); Assert.Equal(1, directory.Lookups);
            release.TrySetResult(); await pending.WaitAsync(Deadline);
            Assert.Null(vm.Gallery.Image); Assert.False(vm.Gallery.HasGallery);
            gallery.Fetch = null; await vm.LoadAsync().WaitAsync(Deadline);
            Assert.Equal(GameGalleryViewModelTests.Place + 1, vm.Game!.RequestedPlaceId);
            Assert.Equal(GameGalleryViewModelTests.Place + 1, gallery.Lookups.Last().Place); Assert.NotNull(vm.Gallery.Image);
        }
        finally { release.TrySetResult(); await vm.DisposeAsync().AsTask().WaitAsync(Deadline); }
    }

    [Fact]
    public async Task OlderCancelNotificationCannotOverwriteAReentrantlyStartedNewLookupStatus()
    {
        var clock = new GameGalleryViewModelTests.Clock(); await using var gallery = new GameGalleryViewModelTests.Directory(clock); var directory = new Games(clock, gallery);
        var release = new TaskCompletionSource<PublicDirectoryResult<PublicRobloxGame>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var browser = new ServerBrowserViewModel(new Servers());
        var vm = new GameDetailsViewModel(directory, browser, () => true, clock: clock); bool started = false; Task? replacement = null;
        try
        {
            browser.UsePreferredPlace(GameGalleryViewModelTests.Place); await vm.LoadAsync().WaitAsync(Deadline);
            directory.Lookup = (_, _) => release.Task;
            vm.PropertyChanged += (_, args) =>
            {
                if (started || args.PropertyName != nameof(GameDetailsViewModel.Game) || vm.Game is not null) return;
                started = true; browser.UsePreferredPlace(GameGalleryViewModelTests.Place + 1); replacement = vm.LoadAsync();
            };
            vm.CancelWork(); Assert.True(started); Assert.NotNull(replacement); Assert.True(vm.IsBusy);
            Assert.Contains("Loading public experience details", vm.Status); Assert.DoesNotContain("cleared", vm.Status, StringComparison.OrdinalIgnoreCase);
            release.TrySetResult(GameGalleryViewModelTests.Success(GameGalleryViewModelTests.Game(clock, GameGalleryViewModelTests.Place + 1)));
            await replacement!.WaitAsync(Deadline); Assert.Equal(GameGalleryViewModelTests.Place + 1, vm.Game!.RequestedPlaceId);
        }
        finally
        {
            release.TrySetResult(GameGalleryViewModelTests.Success(GameGalleryViewModelTests.Game(clock, GameGalleryViewModelTests.Place + 1)));
            await vm.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    private sealed class Servers : IRobloxPublicServerDirectory
    {
        public Task<RobloxPublicServerResult> BrowseAsync(RobloxPublicServerQuery query, CancellationToken ct)
            => throw new InvalidOperationException("These metadata tests must not browse servers.");
    }
    private sealed class Games(GameGalleryViewModelTests.Clock clock, GameGalleryViewModelTests.Directory gallery)
        : IPublicRobloxGameDirectory, IPublicRobloxGameGallery
    {
        public bool IconUnavailable;
        public string? IconFailure;
        public Func<long, CancellationToken, Task<PublicDirectoryResult<PublicRobloxGame>>>? Lookup;
        public int Lookups, IconDescriptors, IconDownloads;
        private readonly byte[] _icon = PublicAccountPresentationTests.Png();
        public Task<PublicDirectoryResult<PublicRobloxGame>> LookupGameAsync(long requestedPlaceId, CancellationToken ct)
        { Lookups++; return Lookup?.Invoke(requestedPlaceId, ct) ?? Task.FromResult(GameGalleryViewModelTests.Success(GameGalleryViewModelTests.Game(clock, requestedPlaceId))); }
        public Task<PublicDirectoryResult<PublicRobloxGameIcon>> GetGameIconAsync(long universeId, CancellationToken ct)
        {
            IconDescriptors++;
            if (IconFailure == "descriptor-throws") throw new IOException("private-icon-provider-marker");
            return Task.FromResult(IconUnavailable ? new(PublicDirectoryState.Unknown, (PublicRobloxGameIcon?)null, "Synthetic unavailable icon")
                : GameGalleryViewModelTests.Success(new PublicRobloxGameIcon(IconFailure == "invalid-descriptor" ? universeId + 1 : universeId,
                    new Uri("https://tr.rbxcdn.com/synthetic-icon.png"), 150, 150, clock.GetUtcNow())));
        }
        public Task<PublicDirectoryResult<EncodedPublicGameIcon>> FetchGameIconAsync(PublicRobloxGameIcon icon, CancellationToken ct)
        { IconDownloads++; if (IconFailure == "fetch-throws") throw new IOException("private-icon-provider-marker");
            return Task.FromResult(GameGalleryViewModelTests.Success(new EncodedPublicGameIcon(_icon, "image/png", clock.GetUtcNow()))); }
        public Task<PublicDirectoryResult<PublicRobloxGameGallery>> GetGameGalleryAsync(long requestedPlaceId, long universeId, CancellationToken ct)
            => gallery.GetGameGalleryAsync(requestedPlaceId, universeId, ct);
        public Task<PublicDirectoryResult<EncodedPublicGameGalleryImage>> FetchGameGalleryImageAsync(PublicRobloxGameGalleryImage image, CancellationToken ct)
            => gallery.FetchGameGalleryImageAsync(image, ct);
        public void ClearMemoryCache() { }
    }
}
