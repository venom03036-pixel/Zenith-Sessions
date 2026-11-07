using System.Collections.Concurrent;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// Synthetic descriptors/PNG bytes only. No HTTP, native window, credential, client or capture calls.
public sealed class GameGalleryViewModelTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    internal const long Place = 6564161046, Universe = 200;
    internal static PublicRobloxGame Game(TimeProvider clock, long place = Place, long universe = Universe)
        => new(place, universe, 100, "Synthetic experience", 300, "Synthetic creator", PublicRobloxCreatorKind.Group, 20, 10, clock.GetUtcNow());
    internal static PublicRobloxGameGalleryImage Item(TimeProvider clock, int position = 0, long? id = 101,
        PublicRobloxGameGalleryState state = PublicRobloxGameGalleryState.Ready,
        PublicRobloxGameGalleryKind kind = PublicRobloxGameGalleryKind.Image, long place = Place, long universe = Universe)
        => new(place, universe, position, id, kind, state,
            state == PublicRobloxGameGalleryState.Ready ? new Uri($"https://tr.rbxcdn.com/synthetic-{id}.png") : null,
            576, 324, clock.GetUtcNow());
    internal static PublicDirectoryResult<T> Success<T>(T value) where T : class => new(PublicDirectoryState.Success, value, "Synthetic metadata");

    [Fact]
    public async Task ManualNavigationPreservesOrderUsesAtMostTwoCachedPicturesAndNeverStartsRotation()
    {
        await using var fixture = new Fixture(3); var vm = fixture.View;
        Assert.Empty(fixture.Directory.Lookups); await vm.LoadAsync(Game(fixture.Clock));
        var first = vm.Image; Assert.NotNull(first); Assert.Equal("1 / 3", vm.Position); Assert.True(vm.CanBrowse);
        await vm.MoveAsync(1); Assert.Equal("2 / 3", vm.Position);
        await vm.MoveAsync(-1); Assert.Same(first, vm.Image);
        await vm.MoveAsync(1); await vm.MoveAsync(1); Assert.Equal("3 / 3", vm.Position);
        await vm.MoveAsync(-1); await vm.MoveAsync(-1); Assert.Equal("1 / 3", vm.Position);
        Assert.Equal(new long?[] { 101, 102, 103, 101 }, fixture.Directory.Images.Select(item => item.ThumbnailId));
        Assert.Equal(2L * 576 * 324 * 4, vm.RetainedPixelBytes); Assert.All(fixture.Directory.Images, image => Assert.Equal(Place, image.RequestedPlaceId));
        int requests = fixture.Directory.Images.Count; fixture.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(requests, fixture.Directory.Images.Count); Assert.Equal(0, fixture.Clock.Timers); Assert.Contains("not a live", vm.Status);
    }

    [Theory]
    [InlineData("place")]
    [InlineData("universe")]
    [InlineData("duplicate")]
    [InlineData("position")]
    [InlineData("too-many")]
    [InlineData("future-gallery")]
    [InlineData("future-item")]
    [InlineData("stale")]
    public async Task WrongDuplicateOrNoncurrentGalleryContextNeverDownloadsOrPublishesAPicture(string fault)
    {
        await using var fixture = new Fixture(2); var gallery = fixture.Directory.Gallery(Place, Universe);
        fixture.Directory.Lookup = (_, _, _) => Task.FromResult(Success(fault switch
        {
            "place" => gallery with { RequestedPlaceId = Place + 1 }, "universe" => gallery with { UniverseId = Universe + 1 },
            "duplicate" => gallery with { Items = new[] { gallery.Items[0], gallery.Items[1] with { ThumbnailId = gallery.Items[0].ThumbnailId } } },
            "position" => gallery with { Items = new[] { gallery.Items[0] with { Position = 1 } } },
            "too-many" => gallery with { Items = Enumerable.Range(0, 11).Select(index => Item(fixture.Clock, index, 101 + index)).ToArray() },
            "future-gallery" => gallery with { RetrievedUtc = fixture.Clock.GetUtcNow().AddMinutes(6) },
            "future-item" => gallery with { Items = new[] { gallery.Items[0] with { RetrievedUtc = fixture.Clock.GetUtcNow().AddMinutes(6) } } },
            _ => gallery with { RetrievedUtc = fixture.Clock.GetUtcNow().AddMinutes(-31) }
        }));
        await fixture.View.LoadAsync(Game(fixture.Clock));
        Assert.Null(fixture.View.Image); Assert.False(fixture.View.HasGallery); Assert.Empty(fixture.Directory.Images); Assert.Single(fixture.Failures);
        Assert.Equal(0, fixture.View.RetainedPixelBytes);
    }

    [Theory]
    [InlineData(PublicRobloxGameGalleryKind.Image, PublicRobloxGameGalleryState.Pending, "prepared")]
    [InlineData(PublicRobloxGameGalleryKind.Image, PublicRobloxGameGalleryState.Blocked, "not approved")]
    [InlineData(PublicRobloxGameGalleryKind.Image, PublicRobloxGameGalleryState.Unavailable, "unavailable")]
    [InlineData(PublicRobloxGameGalleryKind.Video, PublicRobloxGameGalleryState.Unsupported, "video")]
    [InlineData(PublicRobloxGameGalleryKind.Unsupported, PublicRobloxGameGalleryState.Unsupported, "unsupported")]
    public async Task NonreadyItemsKeepTheirPositionAndNeverFetchExternalMedia(PublicRobloxGameGalleryKind kind, PublicRobloxGameGalleryState state, string status)
    {
        await using var fixture = new Fixture();
        var item = Item(fixture.Clock, id: state == PublicRobloxGameGalleryState.Pending ? 101 : null, state: state, kind: kind);
        fixture.Directory.Lookup = (place, universe, _) => Task.FromResult(Success(new PublicRobloxGameGallery(place, universe, new[] { item }, fixture.Clock.GetUtcNow())));
        await fixture.View.LoadAsync(Game(fixture.Clock));
        Assert.True(fixture.View.HasGallery); Assert.Equal("1 / 1", fixture.View.Position); Assert.Null(fixture.View.Image);
        Assert.Contains(status, fixture.View.Status, StringComparison.OrdinalIgnoreCase); Assert.Empty(fixture.Directory.Images); Assert.Empty(fixture.Failures);
    }

    [Fact]
    public async Task GenuineEmptyGalleryIsDistinctFromAnUnavailableResult()
    {
        await using var fixture = new Fixture(0); await fixture.View.LoadAsync(Game(fixture.Clock));
        Assert.False(fixture.View.HasGallery); Assert.Equal("No pictures", fixture.View.Position); Assert.Contains("no public gallery", fixture.View.Status);
        fixture.Directory.Lookup = (_, _, _) => Task.FromResult(new PublicDirectoryResult<PublicRobloxGameGallery>(PublicDirectoryState.Unknown, null, "raw-provider-marker"));
        await fixture.View.LoadAsync(Game(fixture.Clock)); Assert.Contains("unavailable", fixture.View.Status); Assert.DoesNotContain("raw-provider-marker", fixture.View.Status);
        Assert.Empty(fixture.Directory.Images);
    }

    [Fact]
    public async Task ProviderWithoutOptionalCapabilityReportsUnavailableWithoutWork()
    {
        await using var vm = new GameGalleryViewModel(null, () => true);
        await vm.LoadAsync(Game(TimeProvider.System)); await vm.MoveAsync(1);
        Assert.False(vm.HasGallery); Assert.Null(vm.Image); Assert.False(vm.IsBusy); Assert.False(vm.CanBrowse); Assert.Equal(0, vm.RetainedPixelBytes);
        Assert.Contains("unavailable", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceCooldownBlocksImmediateMetadataOrImageRetryWithoutATimer(bool duringImage)
    {
        await using var fixture = new Fixture(2); var delay = TimeSpan.FromSeconds(30);
        if (duringImage) fixture.Directory.Fetch = (_, _) => Task.FromResult(new PublicDirectoryResult<EncodedPublicGameGalleryImage>(PublicDirectoryState.Deferred, null, "raw", delay));
        else fixture.Directory.Lookup = (_, _, _) => Task.FromResult(new PublicDirectoryResult<PublicRobloxGameGallery>(PublicDirectoryState.Deferred, null, "raw", delay));
        await fixture.View.LoadAsync(Game(fixture.Clock));
        int metadata = fixture.Directory.Lookups.Count, pictures = fixture.Directory.Images.Count;
        await fixture.View.LoadAsync(Game(fixture.Clock)); await fixture.View.MoveAsync(1);
        fixture.Clock.Advance(TimeSpan.FromSeconds(29)); await fixture.View.LoadAsync(Game(fixture.Clock));
        Assert.Equal(metadata, fixture.Directory.Lookups.Count); Assert.Equal(pictures, fixture.Directory.Images.Count); Assert.Equal(0, fixture.Clock.Timers);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1)); fixture.Directory.Lookup = null; fixture.Directory.Fetch = null;
        await fixture.View.LoadAsync(Game(fixture.Clock)); Assert.NotNull(fixture.View.Image);
    }

    [Fact]
    public async Task FutureEncodedPictureCannotBePublishedOrRetained()
    {
        await using var fixture = new Fixture(); fixture.Directory.Fetch = (_, _) => Task.FromResult(Success(
            new EncodedPublicGameGalleryImage(GalleryPngTests.Png(), "image/png", fixture.Clock.GetUtcNow().AddMinutes(6))));
        await fixture.View.LoadAsync(Game(fixture.Clock)); Assert.Null(fixture.View.Image); Assert.Equal(0, fixture.View.RetainedPixelBytes);
        Assert.Contains("failed image validation", fixture.View.Status);
    }

    [Fact]
    public async Task NavigationDuringAnOwnedImageRequestCannotRetargetTheCapturedSlide()
    {
        await using var fixture = new Fixture(3); await fixture.View.LoadAsync(Game(fixture.Clock)); var gate = fixture.NewGate();
        fixture.Directory.Fetch = async (item, _) => { if (item.ThumbnailId == 102) await gate.Hold(); return fixture.Directory.Encoded(); };
        var pending = fixture.View.MoveAsync(1); await gate.Entered();
        await fixture.View.MoveAsync(1); Assert.Equal("2 / 3", fixture.View.Position); Assert.Equal(2, fixture.Directory.Images.Count);
        Assert.Null(fixture.View.Image); Assert.False(pending.IsCompleted);
        gate.Open(); await pending.WaitAsync(Deadline); Assert.NotNull(fixture.View.Image); Assert.Equal(102, fixture.Directory.Images.Last().ThumbnailId);
        await fixture.View.MoveAsync(1); Assert.Equal("3 / 3", fixture.View.Position); Assert.Equal(103, fixture.Directory.Images.Last().ThumbnailId);
    }

    [Fact]
    public async Task CancelledIgnoringProviderStaysOwnedAndCannotRetargetToANewExperience()
    {
        await using var fixture = new Fixture(); var gate = fixture.NewGate();
        fixture.Directory.Lookup = async (place, universe, _) => { await gate.Hold(); return Success(fixture.Directory.Gallery(place, universe)); };
        var pending = fixture.View.LoadAsync(Game(fixture.Clock)); await gate.Entered(); fixture.View.CancelWork();
        Assert.True(fixture.View.IsBusy); Assert.False(pending.IsCompleted);
        await fixture.View.LoadAsync(Game(fixture.Clock, Place + 1)); Assert.Single(fixture.Directory.Lookups);
        gate.Open(); await pending.WaitAsync(Deadline); Assert.Null(fixture.View.Image); Assert.False(fixture.View.HasGallery);
        fixture.Directory.Lookup = null; await fixture.View.LoadAsync(Game(fixture.Clock, Place + 1, Universe + 1));
        Assert.Equal((Place + 1, Universe + 1), fixture.Directory.Lookups.Last()); Assert.Equal(Place + 1, fixture.Directory.Images.Last().RequestedPlaceId);
        Assert.NotNull(fixture.View.Image);
    }

    [Fact]
    public async Task HideClearsCurrentMediaAndFencesALateImageWithoutAutomaticRestartOnShow()
    {
        await using var fixture = new Fixture(); var gate = fixture.NewGate();
        fixture.Directory.Fetch = async (_, _) => { await gate.Hold(); return fixture.Directory.Encoded(); };
        var pending = fixture.View.LoadAsync(Game(fixture.Clock)); await gate.Entered(); fixture.View.SetVisible(false);
        Assert.Null(fixture.View.Image); Assert.False(fixture.View.HasGallery); Assert.Equal(0, fixture.View.RetainedPixelBytes);
        gate.Open(); await pending.WaitAsync(Deadline); await fixture.View.LoadAsync(Game(fixture.Clock)); Assert.Single(fixture.Directory.Images);
        fixture.View.SetVisible(true); Assert.Single(fixture.Directory.Lookups); Assert.Null(fixture.View.Image);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReentrantCancelFromBusyOrReadyImageNotificationCannotBeOverwritten(bool whenReady)
    {
        await using var fixture = new Fixture(); bool cancelled = false; Task? publishedOwner = null;
        fixture.View.PropertyChanged += (_, args) =>
        {
            if (cancelled || (whenReady ? args.PropertyName != nameof(GameGalleryViewModel.Image) || fixture.View.Image is null
                : args.PropertyName != nameof(GameGalleryViewModel.IsBusy) || !fixture.View.IsBusy)) return;
            cancelled = true; publishedOwner = fixture.View.ActiveOperation; fixture.View.CancelWork();
        };
        var pending = fixture.View.LoadAsync(Game(fixture.Clock)); await pending.WaitAsync(Deadline);
        Assert.True(cancelled); Assert.Same(pending, publishedOwner); Assert.Null(fixture.View.Image); Assert.False(fixture.View.HasGallery);
        Assert.Equal(0, fixture.View.RetainedPixelBytes); Assert.Contains("cleared", fixture.View.Status);
        if (!whenReady) Assert.Empty(fixture.Directory.Lookups);
    }

    [Fact]
    public async Task ReentrantDisposalFromBusyNotificationSeesTheNewOwnerBeforeInvokingProvider()
    {
        await using var fixture = new Fixture(); Task? disposal = null;
        fixture.View.PropertyChanged += (_, args) =>
        { if (disposal is null && args.PropertyName == nameof(GameGalleryViewModel.IsBusy) && fixture.View.IsBusy) disposal = fixture.View.DisposeAsync().AsTask(); };
        var active = fixture.View.LoadAsync(Game(fixture.Clock)); Assert.NotNull(disposal);
        await Task.WhenAll(active, disposal!).WaitAsync(Deadline); Assert.Empty(fixture.Directory.Lookups); Assert.Null(fixture.View.Image);
        Assert.False(fixture.Directory.Disposed);
    }

    [Fact]
    public async Task ExpiryCleanupCannotOverwriteANewerReentrantCancelStatusOrReuseCachedPixels()
    {
        await using var fixture = new Fixture(2); await fixture.View.LoadAsync(Game(fixture.Clock));
        Assert.NotNull(fixture.View.Image); fixture.Clock.Advance(TimeSpan.FromMinutes(31)); bool cancelled = false;
        fixture.View.PropertyChanged += (_, args) =>
        {
            if (cancelled || args.PropertyName != nameof(GameGalleryViewModel.Image) || fixture.View.HasGallery) return;
            cancelled = true; fixture.View.CancelWork();
        };
        await fixture.View.MoveAsync(1).WaitAsync(Deadline);
        Assert.True(cancelled); Assert.Null(fixture.View.Image); Assert.False(fixture.View.HasGallery); Assert.Equal(0, fixture.View.RetainedPixelBytes);
        Assert.Contains("cleared", fixture.View.Status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expired", fixture.View.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Single(fixture.Directory.Lookups); Assert.Single(fixture.Directory.Images);
    }

    [Fact]
    public async Task DisposalJoinsIgnoringImageProviderAndBorrowsItsDirectory()
    {
        await using var fixture = new Fixture(); var gate = fixture.NewGate();
        fixture.Directory.Fetch = async (_, _) => { await gate.Hold(); return fixture.Directory.Encoded(); };
        var active = fixture.View.LoadAsync(Game(fixture.Clock)); await gate.Entered(); var disposal = fixture.View.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted); Assert.False(active.IsCompleted); Assert.Null(fixture.View.Image);
        Assert.Same(disposal, fixture.View.DisposeAsync().AsTask()); await fixture.View.LoadAsync(Game(fixture.Clock, Place + 1)); Assert.Single(fixture.Directory.Lookups);
        gate.Open(); await Task.WhenAll(active, disposal).WaitAsync(Deadline);
        Assert.Null(fixture.View.Image); Assert.Equal(0, fixture.View.RetainedPixelBytes); Assert.False(fixture.Directory.Disposed);
    }

    internal sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 6, 20, 0, 0, TimeSpan.Zero);
        public int Timers;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { Timers++; throw new InvalidOperationException("Manual public pictures must not own a timer."); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public Clock Clock { get; } = new(); public Directory Directory { get; }
        public GameGalleryViewModel View { get; } public List<Exception> Failures { get; } = [];
        private readonly List<Gate> _gates = [];
        public Fixture(int count = 1) { Directory = new(Clock, count); View = new(Directory, () => true, Failures.Add, Clock); }
        public Gate NewGate() { var gate = new Gate(); _gates.Add(gate); return gate; }
        public async ValueTask DisposeAsync() { foreach (var gate in _gates) gate.Open(); await View.DisposeAsync().AsTask().WaitAsync(Deadline); await Directory.DisposeAsync(); }
    }
    internal sealed class Directory(Clock clock, int count = 1) : IPublicRobloxGameGallery, IAsyncDisposable
    {
        public readonly ConcurrentQueue<(long Place, long Universe)> Lookups = new(); public readonly ConcurrentQueue<PublicRobloxGameGalleryImage> Images = new();
        public bool Disposed;
        public Func<long, long, CancellationToken, Task<PublicDirectoryResult<PublicRobloxGameGallery>>>? Lookup;
        public Func<PublicRobloxGameGalleryImage, CancellationToken, Task<PublicDirectoryResult<EncodedPublicGameGalleryImage>>>? Fetch;
        private readonly byte[] _png = GalleryPngTests.Png();
        public PublicRobloxGameGallery Gallery(long place, long universe) => new(place, universe,
            Enumerable.Range(0, count).Select(index => Item(clock, index, 101 + index, place: place, universe: universe)).ToArray(), clock.GetUtcNow());
        public PublicDirectoryResult<EncodedPublicGameGalleryImage> Encoded() => Success(new EncodedPublicGameGalleryImage(_png, "image/png", clock.GetUtcNow()));
        public Task<PublicDirectoryResult<PublicRobloxGameGallery>> GetGameGalleryAsync(long place, long universe, CancellationToken ct)
        { Lookups.Enqueue((place, universe)); return Lookup?.Invoke(place, universe, ct) ?? Task.FromResult(Success(Gallery(place, universe))); }
        public Task<PublicDirectoryResult<EncodedPublicGameGalleryImage>> FetchGameGalleryImageAsync(PublicRobloxGameGalleryImage image, CancellationToken ct)
        { Images.Enqueue(image); return Fetch?.Invoke(image, ct) ?? Task.FromResult(Encoded()); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Gate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously), _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered() => _entered.Task.WaitAsync(Deadline);
        public async Task Hold() { _entered.TrySetResult(); await _release.Task; }
        public void Open() => _release.TrySetResult();
    }
}
