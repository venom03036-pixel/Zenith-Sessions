using System.IO;
using System.Windows.Media;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class GameDetailsViewModelTests
{
    private const long Place = 6564161046;
    private static PublicDirectoryResult<T> Success<T>(T value) where T : class => new(PublicDirectoryState.Success, value, "Synthetic public information");

    [Fact]
    public async Task ExplicitLookupPreservesRequestedSubplaceAndPublishesOnlyDetachedFrozenIcon()
    {
        var clock = new Clock(); var directory = new Games(clock);
        await using var browser = new ServerBrowserViewModel(new Servers(), clock: clock);
        await using var vm = new GameDetailsViewModel(directory, browser, () => true, clock: clock);
        browser.UsePreferredPlace(Place);
        Assert.True(vm.CanLoad); Assert.Equal(0, directory.Lookups); Assert.Null(vm.Game);
        await vm.LoadAsync();
        Assert.Equal(1, directory.Lookups); Assert.Equal(1, directory.IconRequests); Assert.Equal(1, directory.ImageRequests);
        Assert.Equal(Place, browser.Destination!.PlaceId); Assert.Null(browser.Destination.JobId);
        Assert.Equal(100, vm.Game!.RootPlaceId); Assert.Equal(3_000_000_000L, vm.Game.Playing);
        Assert.Contains("across the experience", vm.Activity); Assert.Contains("listing server limit", vm.Activity);
        Assert.Contains($"Requested place {Place}", vm.Identifiers); Assert.Contains("root place 100", vm.Identifiers);
        var icon = Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapSource>(vm.Icon);
        Assert.True(icon.IsFrozen); Assert.Equal(PixelFormats.Bgra32, icon.Format);
        Assert.Equal(150, icon.PixelWidth); Assert.Equal(150, icon.PixelHeight);
        byte[] pixels = new byte[90_000]; icon.CopyPixels(pixels, 600, 0);
        Assert.Equal(new byte[] { 30, 20, 10, 255 }, pixels[..4]);
        Assert.Contains("does not verify", vm.Status);
    }

    [Theory]
    [InlineData("place")]
    [InlineData("universe")]
    [InlineData("creator")]
    [InlineData("playing")]
    [InlineData("max")]
    [InlineData("name")]
    [InlineData("surrogate")]
    [InlineData("stale")]
    [InlineData("future")]
    public async Task InvalidMetadataCannotBecomeDisplayedGameInformation(string invalid)
    {
        var clock = new Clock(); var directory = new Games(clock); var game = directory.Game;
        directory.Game = invalid switch
        {
            "place" => game with { RequestedPlaceId = Place + 1 },
            "universe" => game with { UniverseId = 0 },
            "creator" => game with { CreatorKind = (PublicRobloxCreatorKind)999 },
            "playing" => game with { Playing = -1 },
            "max" => game with { MaxPlayers = 0 },
            "name" => game with { Name = "Spoof\u202eTitle" },
            "surrogate" => game with { CreatorName = "\ud800" },
            "stale" => game with { RetrievedUtc = clock.GetUtcNow().AddHours(-1) },
            _ => game with { RetrievedUtc = clock.GetUtcNow().AddMinutes(6) }
        };
        List<Exception> failures = [];
        await using var browser = new ServerBrowserViewModel(new Servers(), clock: clock);
        await using var vm = new GameDetailsViewModel(directory, browser, () => true, failures.Add, clock);
        browser.UsePreferredPlace(Place); await vm.LoadAsync();
        Assert.Null(vm.Game); Assert.Null(vm.Icon); Assert.Single(failures);
        Assert.Equal(0, directory.IconRequests); Assert.Equal(Place, browser.Destination!.PlaceId);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("crc")]
    [InlineData("animated")]
    [InlineData("zlib")]
    [InlineData("dimensions")]
    [InlineData("media")]
    public async Task OptionalIconUsesTheExistingStrictDecoderAndRetainsValidMetadataOnImageFailure(string invalid)
    {
        var clock = new Clock(); var directory = new Games(clock);
        directory.Bytes = invalid switch
        {
            "truncated" => directory.Bytes[..33],
            "animated" => PublicAccountPresentationTests.Png(extraChunk: "acTL"),
            "zlib" => PublicAccountPresentationTests.Png(invalidZlib: true),
            "dimensions" => PublicAccountPresentationTests.Png(width: 151),
            _ => directory.Bytes
        };
        if (invalid == "crc") directory.Bytes[^1] ^= 1;
        if (invalid == "media") directory.MediaType = "image/jpeg";
        await using var browser = new ServerBrowserViewModel(new Servers(), clock: clock);
        await using var vm = new GameDetailsViewModel(directory, browser, () => true, clock: clock);
        browser.UsePreferredPlace(Place); await vm.LoadAsync();
        Assert.NotNull(vm.Game); Assert.Null(vm.Icon); Assert.Contains("failed image validation", vm.Status);
    }

    [Fact]
    public async Task SamePlaceServerSelectionKeepsDetailsButChangedPlaceCancelsLatePublication()
    {
        var clock = new Clock(); var directory = new Games(clock);
        await using var browser = new ServerBrowserViewModel(new Servers(), clock: clock);
        await using var vm = new GameDetailsViewModel(directory, browser, () => true, clock: clock);
        browser.UsePreferredPlace(Place); await vm.LoadAsync(); var game = vm.Game;
        browser.LinkInput = $"https://www.roblox.com/games/start?placeId={Place}&gameInstanceId=11111111-2222-4333-8444-000000000001";
        browser.Resolve.Execute(null); Assert.Same(game, vm.Game);
        directory.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        directory.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = vm.LoadAsync(); Assert.True(vm.IsBusy); Assert.Null(vm.Game);
        await directory.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        browser.UsePreferredPlace(Place + 1); Assert.True(directory.Token.IsCancellationRequested);
        directory.Pending.SetResult(Success(directory.Game)); await pending;
        Assert.Null(vm.Game); Assert.Null(vm.Icon); Assert.Equal(Place + 1, browser.Destination!.PlaceId);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task OnlyOneOwnerRunsAndDisposalWaitsForCancellationIgnoringProvider()
    {
        var clock = new Clock(); var directory = new Games(clock) { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var browser = new ServerBrowserViewModel(new Servers(), clock: clock);
        var vm = new GameDetailsViewModel(directory, browser, () => true, clock: clock);
        browser.UsePreferredPlace(Place); var pending = vm.LoadAsync();
        await directory.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(pending, vm.ActiveOperation); await vm.LoadAsync(); Assert.Equal(1, directory.Lookups);
        var disposing = vm.DisposeAsync().AsTask();
        Assert.True(directory.Token.IsCancellationRequested); Assert.False(disposing.IsCompleted); Assert.False(vm.CanLoad);
        directory.Pending.SetResult(Success(directory.Game)); await disposing; await pending;
        Assert.Null(vm.Game); Assert.Null(vm.Icon); Assert.Equal(0, directory.IconRequests);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task CancelAndCacheFailureStillFenceLateResultsAndRetainTheActualOwner()
    {
        var clock = new Clock(); var directory = new Games(clock) { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously), ThrowOnClear = true };
        List<Exception> failures = [];
        await using var browser = new ServerBrowserViewModel(new Servers(), clock: clock);
        await using var vm = new GameDetailsViewModel(directory, browser, () => true, failures.Add, clock);
        browser.UsePreferredPlace(Place); var pending = vm.LoadAsync();
        await directory.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        vm.CancelWork(clearCache: true); Assert.Single(failures); Assert.True(vm.IsBusy);
        Assert.Same(pending, vm.ActiveOperation); Assert.False(vm.CanLoad);
        directory.Pending.SetResult(Success(directory.Game)); await pending;
        Assert.Null(vm.Game); Assert.False(vm.IsBusy); Assert.True(vm.CanLoad);
    }

    [Fact]
    public async Task ServiceCooldownSurvivesDestinationChangesWithoutAutomaticRetries()
    {
        var clock = new Clock(); var directory = new Games(clock) { DeferFor = TimeSpan.FromMinutes(20) };
        await using var browser = new ServerBrowserViewModel(new Servers(), clock: clock);
        await using var vm = new GameDetailsViewModel(directory, browser, () => true, clock: clock);
        browser.UsePreferredPlace(Place); await vm.LoadAsync(); Assert.Equal(1, directory.Lookups);
        browser.UsePreferredPlace(Place + 1); await vm.LoadAsync(); Assert.Equal(1, directory.Lookups);
        clock.Advance(TimeSpan.FromMinutes(19)); await vm.LoadAsync(); Assert.Equal(1, directory.Lookups);
        clock.Advance(TimeSpan.FromMinutes(1)); directory.DeferFor = null;
        directory.Game = directory.Game with { RequestedPlaceId = Place + 1, RetrievedUtc = clock.GetUtcNow() };
        await vm.LoadAsync(); Assert.Equal(2, directory.Lookups); Assert.NotNull(vm.Game);
    }

    [Fact]
    public async Task UnavailableOrLockedDirectoryDoesNotPretendThatInformationWasLoaded()
    {
        await using var browser = new ServerBrowserViewModel(new Servers());
        await using var missing = new GameDetailsViewModel(null, browser, () => true);
        browser.UsePreferredPlace(Place); await missing.LoadAsync(); Assert.False(missing.CanLoad); Assert.Null(missing.Game);
        var directory = new Games(new Clock());
        await using var locked = new GameDetailsViewModel(directory, browser, () => false);
        await locked.LoadAsync(); Assert.Equal(0, directory.Lookups); Assert.False(locked.CanLoad);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
    private sealed class Servers : IRobloxPublicServerDirectory
    {
        public Task<RobloxPublicServerResult> BrowseAsync(RobloxPublicServerQuery query, CancellationToken ct) =>
            Task.FromResult(new RobloxPublicServerResult(RobloxPublicServerStatus.Complete, [], null, null, "Synthetic only"));
    }
    private sealed class Games(Clock clock) : IPublicRobloxGameDirectory
    {
        public PublicRobloxGame Game = new(Place, 200, 100, "Synthetic experience", 300, "Synthetic creator", PublicRobloxCreatorKind.Group, 3_000_000_000, 20, clock.GetUtcNow());
        public byte[] Bytes = PublicAccountPresentationTests.Png();
        public string MediaType = "image/png";
        public int Lookups, IconRequests, ImageRequests;
        public CancellationToken Token;
        public bool ThrowOnClear;
        public TimeSpan? DeferFor;
        public TaskCompletionSource<PublicDirectoryResult<PublicRobloxGame>>? Pending;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PublicDirectoryResult<PublicRobloxGame>> LookupGameAsync(long place, CancellationToken ct)
        {
            Lookups++; Token = ct;
            Started.TrySetResult();
            return Pending?.Task ?? Task.FromResult(DeferFor is { } delay
                ? new(PublicDirectoryState.Deferred, (PublicRobloxGame?)null, "Synthetic cooldown", delay) : Success(Game));
        }
        public Task<PublicDirectoryResult<PublicRobloxGameIcon>> GetGameIconAsync(long universe, CancellationToken ct)
        { IconRequests++; return Task.FromResult(Success(new PublicRobloxGameIcon(universe, new Uri("https://tr.rbxcdn.com/synthetic.png"), 150, 150, clock.GetUtcNow()))); }
        public Task<PublicDirectoryResult<EncodedPublicGameIcon>> FetchGameIconAsync(PublicRobloxGameIcon icon, CancellationToken ct)
        { ImageRequests++; return Task.FromResult(Success(new EncodedPublicGameIcon(Bytes, MediaType, clock.GetUtcNow()))); }
        public void ClearMemoryCache() { if (ThrowOnClear) throw new IOException("Synthetic cache failure"); }
    }
}
