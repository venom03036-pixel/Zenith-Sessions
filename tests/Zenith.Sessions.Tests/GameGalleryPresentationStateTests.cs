using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Xunit;
using static Zenith.Sessions.Tests.GameGalleryViewModelTests;

namespace Zenith.Sessions.Tests;

// Exercises the existing owner with synthetic public descriptors and PNG bytes only.
public sealed class GameGalleryPresentationStateTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static void Placeholder(GameGalleryViewModel view, GameGalleryPresentationState state)
    {
        Assert.Equal(state, view.PresentationState); Assert.True(view.ShowPlaceholder); Assert.Null(view.Image);
        Assert.False(string.IsNullOrWhiteSpace(view.EmptyTitle)); Assert.Equal(view.Status, view.EmptyDescription);
    }

    [Fact]
    public async Task InitialAndMissingCapabilityHaveDifferentTruthfulPlaceholders()
    {
        await using var fixture = new Fixture();
        Placeholder(fixture.View, GameGalleryPresentationState.NotLoaded);
        Assert.Contains("Load an experience", fixture.View.EmptyDescription); Assert.Empty(fixture.Directory.Lookups);
        await using var unavailable = new GameGalleryViewModel(null, () => true);
        await unavailable.LoadAsync(Game(fixture.Clock));
        Placeholder(unavailable, GameGalleryPresentationState.Unavailable);
        Assert.Contains("unavailable", unavailable.EmptyTitle, StringComparison.OrdinalIgnoreCase);
        unavailable.CancelWork(); Placeholder(unavailable, GameGalleryPresentationState.Unavailable);
    }

    [Fact]
    public async Task MetadataAndImageRequestsPublishLoadingBeforeARealImageHidesThePlaceholder()
    {
        await using var fixture = new Fixture(); var metadata = fixture.Gate(); var image = fixture.Gate();
        fixture.Directory.Lookup = async (place, universe, _) =>
        { await metadata.Hold(); return Success(fixture.Directory.Gallery(place, universe)); };
        fixture.Directory.Fetch = async (_, _) => { await image.Hold(); return fixture.Directory.Encoded(); };
        Task work = fixture.View.LoadAsync(Game(fixture.Clock)); await metadata.Entered();
        Placeholder(fixture.View, GameGalleryPresentationState.Loading); Assert.True(fixture.View.IsBusy);
        metadata.Open(); await image.Entered(); Placeholder(fixture.View, GameGalleryPresentationState.Loading);
        image.Open(); await work.WaitAsync(Deadline);
        Assert.Equal(GameGalleryPresentationState.Displayed, fixture.View.PresentationState);
        Assert.NotNull(fixture.View.Image); Assert.False(fixture.View.ShowPlaceholder); Assert.False(fixture.View.IsBusy);
        Assert.Single(fixture.Directory.Lookups); Assert.Single(fixture.Directory.Images);
    }

    [Fact]
    public async Task SuccessfulEmptyGalleryStopsShowingTheInitialLoadPromptAndDoesNotRetry()
    {
        await using var fixture = new Fixture(0); await fixture.View.LoadAsync(Game(fixture.Clock));
        Placeholder(fixture.View, GameGalleryPresentationState.Empty);
        Assert.Equal("No public pictures", fixture.View.EmptyTitle); Assert.False(fixture.View.HasGallery);
        Assert.DoesNotContain("Load an experience", fixture.View.EmptyDescription);
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Single(fixture.Directory.Lookups); Assert.Empty(fixture.Directory.Images); Assert.Equal(0, fixture.Clock.Timers);
    }

    [Theory]
    [InlineData(PublicDirectoryState.Unknown)]
    [InlineData(PublicDirectoryState.Failure)]
    [InlineData(PublicDirectoryState.InvalidInput)]
    [InlineData(PublicDirectoryState.Deferred)]
    public async Task MetadataOutcomesProduceUnavailableOrDeferredWithoutProviderTextOrAutomaticRetries(PublicDirectoryState response)
    {
        await using var fixture = new Fixture();
        fixture.Directory.Lookup = (_, _, _) => Task.FromResult(new PublicDirectoryResult<PublicRobloxGameGallery>(response, null,
            "synthetic-secret-provider-marker", response == PublicDirectoryState.Deferred ? TimeSpan.FromSeconds(30) : null));
        await fixture.View.LoadAsync(Game(fixture.Clock));
        var expected = response == PublicDirectoryState.Deferred ? GameGalleryPresentationState.Deferred : GameGalleryPresentationState.Unavailable;
        Placeholder(fixture.View, expected);
        Assert.DoesNotContain("synthetic-secret-provider-marker", fixture.View.EmptyDescription);
        Assert.Empty(fixture.Directory.Images); Assert.Single(fixture.Directory.Lookups);
        if (response == PublicDirectoryState.Deferred)
        { await fixture.View.LoadAsync(Game(fixture.Clock)); Assert.Single(fixture.Directory.Lookups); Placeholder(fixture.View, expected); }
        fixture.Clock.Advance(TimeSpan.FromHours(1)); Assert.Single(fixture.Directory.Lookups); Assert.Equal(0, fixture.Clock.Timers);
    }

    [Theory]
    [InlineData(PublicRobloxGameGalleryKind.Image, PublicRobloxGameGalleryState.Pending, GameGalleryPresentationState.Pending)]
    [InlineData(PublicRobloxGameGalleryKind.Image, PublicRobloxGameGalleryState.Blocked, GameGalleryPresentationState.Blocked)]
    [InlineData(PublicRobloxGameGalleryKind.Image, PublicRobloxGameGalleryState.Unavailable, GameGalleryPresentationState.Unavailable)]
    [InlineData(PublicRobloxGameGalleryKind.Video, PublicRobloxGameGalleryState.Unsupported, GameGalleryPresentationState.Video)]
    [InlineData(PublicRobloxGameGalleryKind.Unsupported, PublicRobloxGameGalleryState.Unsupported, GameGalleryPresentationState.Unsupported)]
    [InlineData(PublicRobloxGameGalleryKind.Image, PublicRobloxGameGalleryState.Unsupported, GameGalleryPresentationState.Unsupported)]
    public async Task ItemPresentationUsesItsActualKindAndStateWithoutFetchingUnsupportedMedia(PublicRobloxGameGalleryKind kind,
        PublicRobloxGameGalleryState state, GameGalleryPresentationState expected)
    {
        await using var fixture = new Fixture();
        var item = Item(fixture.Clock, id: state == PublicRobloxGameGalleryState.Pending ? 101 : null, state: state, kind: kind);
        fixture.Directory.Lookup = (place, universe, _) => Task.FromResult(Success(new PublicRobloxGameGallery(place, universe, [item], fixture.Clock.GetUtcNow())));
        await fixture.View.LoadAsync(Game(fixture.Clock)); Placeholder(fixture.View, expected);
        Assert.Equal("1 / 1", fixture.View.Position); Assert.True(fixture.View.HasGallery); Assert.Empty(fixture.Directory.Images);
        Assert.DoesNotContain("Load an experience", fixture.View.EmptyDescription); Assert.Empty(fixture.Failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BadOrWrongSizeEncodedImagesHaveDecodeFailurePresentationWithoutRelaxingTheDecoder(bool square)
    {
        await using var fixture = new Fixture();
        byte[] bytes = square ? PublicAccountPresentationTests.Png(150) : [1, 2, 3];
        fixture.Directory.Fetch = (_, _) => Task.FromResult(Success(new EncodedPublicGameGalleryImage(bytes, "image/png", fixture.Clock.GetUtcNow())));
        await fixture.View.LoadAsync(Game(fixture.Clock)); Placeholder(fixture.View, GameGalleryPresentationState.DecodeFailed);
        Assert.Equal(0, fixture.View.RetainedPixelBytes); Assert.Single(fixture.Directory.Images);
        Assert.Contains("failed image validation", fixture.View.EmptyDescription);
    }

    [Theory]
    [InlineData(PublicDirectoryState.Unknown)]
    [InlineData(PublicDirectoryState.Failure)]
    [InlineData(PublicDirectoryState.Deferred)]
    public async Task ImageTransportOutcomesAreDistinctFromSuccessfulTransportWithBadPixels(PublicDirectoryState response)
    {
        await using var fixture = new Fixture(2);
        fixture.Directory.Fetch = (_, _) => Task.FromResult(new PublicDirectoryResult<EncodedPublicGameGalleryImage>(response, null,
            "synthetic-secret-provider-marker", response == PublicDirectoryState.Deferred ? TimeSpan.FromSeconds(30) : null));
        await fixture.View.LoadAsync(Game(fixture.Clock));
        var expected = response == PublicDirectoryState.Deferred ? GameGalleryPresentationState.Deferred : GameGalleryPresentationState.Unavailable;
        Placeholder(fixture.View, expected); Assert.True(fixture.View.HasGallery); Assert.Equal(0, fixture.View.RetainedPixelBytes);
        Assert.DoesNotContain("synthetic-secret-provider-marker", fixture.View.EmptyDescription);
        if (response == PublicDirectoryState.Deferred)
        {
            await fixture.View.MoveAsync(1); Placeholder(fixture.View, expected);
            Assert.Single(fixture.Directory.Images); Assert.Equal("1 / 2", fixture.View.Position);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderExceptionsRemainFixedUnavailablePresentation(bool duringImage)
    {
        await using var fixture = new Fixture();
        if (duringImage) fixture.Directory.Fetch = (_, _) => throw new InvalidOperationException("synthetic-secret-provider-marker");
        else fixture.Directory.Lookup = (_, _, _) => throw new InvalidOperationException("synthetic-secret-provider-marker");
        await fixture.View.LoadAsync(Game(fixture.Clock)); Placeholder(fixture.View, GameGalleryPresentationState.Unavailable);
        Assert.Single(fixture.Failures); Assert.DoesNotContain("synthetic-secret-provider-marker", fixture.View.EmptyDescription);
        Assert.DoesNotContain("synthetic-secret-provider-marker", fixture.Failures[0].Message);
    }

    [Fact]
    public async Task CallerCancellationWaitsForActualIgnoringRequestAndCannotPublishItsLatePixels()
    {
        await using var fixture = new Fixture(); var gate = fixture.Gate();
        fixture.Directory.Fetch = async (_, _) => { await gate.Hold(); return fixture.Directory.Encoded(); };
        using var stop = new CancellationTokenSource();
        var work = fixture.View.LoadAsync(Game(fixture.Clock), stop.Token); await gate.Entered();
        stop.Cancel(); Assert.False(work.IsCompleted); Assert.True(fixture.View.IsBusy);
        gate.Open(); await work.WaitAsync(Deadline);
        Placeholder(fixture.View, GameGalleryPresentationState.Cancelled); Assert.False(fixture.View.IsBusy);
        Assert.Single(fixture.Directory.Images); Assert.Equal(0, fixture.View.RetainedPixelBytes);
    }

    [Fact]
    public async Task ExplicitClearWinsOverALateMetadataResultAndOnlyANewLoadCanDisplayPictures()
    {
        await using var fixture = new Fixture(); var gate = fixture.Gate();
        fixture.Directory.Lookup = async (place, universe, _) =>
        { await gate.Hold(); return Success(fixture.Directory.Gallery(place, universe)); };
        var work = fixture.View.LoadAsync(Game(fixture.Clock)); await gate.Entered(); fixture.View.CancelWork();
        Placeholder(fixture.View, GameGalleryPresentationState.Cleared); Assert.False(work.IsCompleted);
        gate.Open(); await work.WaitAsync(Deadline); Placeholder(fixture.View, GameGalleryPresentationState.Cleared);
        Assert.Empty(fixture.Directory.Images); Assert.False(fixture.View.HasGallery);
        fixture.Directory.Lookup = null; await fixture.View.LoadAsync(Game(fixture.Clock, Place + 1, Universe + 1));
        Assert.Equal(GameGalleryPresentationState.Displayed, fixture.View.PresentationState); Assert.False(fixture.View.ShowPlaceholder);
        Assert.Equal(Place + 1, fixture.Directory.Images.Last().RequestedPlaceId);
    }

    [Fact]
    public async Task HiddenGalleryClearsPixelsAndKeepsClearedPresentationAfterLateCompletionAndShowing()
    {
        await using var fixture = new Fixture(); var gate = fixture.Gate();
        fixture.Directory.Fetch = async (_, _) => { await gate.Hold(); return fixture.Directory.Encoded(); };
        var work = fixture.View.LoadAsync(Game(fixture.Clock)); await gate.Entered(); fixture.View.SetVisible(false);
        Placeholder(fixture.View, GameGalleryPresentationState.Cleared); Assert.Equal(0, fixture.View.RetainedPixelBytes);
        gate.Open(); await work.WaitAsync(Deadline); fixture.View.SetVisible(true);
        Placeholder(fixture.View, GameGalleryPresentationState.Cleared);
        Assert.Single(fixture.Directory.Lookups); Assert.Single(fixture.Directory.Images); Assert.False(fixture.View.IsBusy);
    }

    [Theory]
    [InlineData(nameof(GameGalleryViewModel.PresentationState))]
    [InlineData(nameof(GameGalleryViewModel.EmptyTitle))]
    [InlineData(nameof(GameGalleryViewModel.Image))]
    public async Task ReentrantClearFromNewPresentationOrImageNotificationCannotBeOverwritten(string property)
    {
        await using var fixture = new Fixture(); bool cancelled = false; Task? owner = null;
        fixture.View.PropertyChanged += (_, args) =>
        {
            if (cancelled || args.PropertyName != property || (property == nameof(GameGalleryViewModel.Image)
                    ? fixture.View.Image is null : fixture.View.PresentationState != GameGalleryPresentationState.Loading)) return;
            cancelled = true; owner = fixture.View.ActiveOperation; fixture.View.CancelWork();
        };
        var work = fixture.View.LoadAsync(Game(fixture.Clock)); await work.WaitAsync(Deadline);
        Assert.True(cancelled); Assert.Same(work, owner); Placeholder(fixture.View, GameGalleryPresentationState.Cleared);
        Assert.False(fixture.View.HasGallery); Assert.Equal(0, fixture.View.RetainedPixelBytes);
        if (property != nameof(GameGalleryViewModel.Image)) Assert.Empty(fixture.Directory.Lookups);
    }

    [Fact]
    public async Task ReentrantClearOfSuccessfulEmptyOutcomeIsTheFinalPublishedState()
    {
        await using var fixture = new Fixture(0); bool cleared = false;
        fixture.View.PropertyChanged += (_, args) =>
        {
            if (cleared || args.PropertyName != nameof(GameGalleryViewModel.EmptyTitle)
                || fixture.View.PresentationState != GameGalleryPresentationState.Empty) return;
            cleared = true; fixture.View.CancelWork();
        };
        await fixture.View.LoadAsync(Game(fixture.Clock)); Assert.True(cleared);
        Placeholder(fixture.View, GameGalleryPresentationState.Cleared); Assert.Empty(fixture.Directory.Images);
    }

    [Fact]
    public async Task CooldownOutcomePreservesTheExistingImageAndDoesNotCoverItWithAPlaceholder()
    {
        await using var fixture = new Fixture(2);
        fixture.Directory.Fetch = (_, _) => Task.FromResult(fixture.Directory.Encoded() with { RetryAfter = TimeSpan.FromSeconds(30) });
        await fixture.View.LoadAsync(Game(fixture.Clock)); var image = fixture.View.Image; Assert.NotNull(image);
        Assert.Equal(GameGalleryPresentationState.Displayed, fixture.View.PresentationState);
        await fixture.View.MoveAsync(1);
        Assert.Equal(GameGalleryPresentationState.Deferred, fixture.View.PresentationState);
        Assert.Same(image, fixture.View.Image); Assert.False(fixture.View.ShowPlaceholder); Assert.Equal("1 / 2", fixture.View.Position);
        Assert.Single(fixture.Directory.Images); Assert.Equal(0, fixture.Clock.Timers);
    }

    [Fact]
    public async Task DisposalJoinsIgnoringProviderAndItsClearedStateCannotBecomeDisplayedLater()
    {
        await using var fixture = new Fixture(); var gate = fixture.Gate();
        fixture.Directory.Fetch = async (_, _) => { await gate.Hold(); return fixture.Directory.Encoded(); };
        var work = fixture.View.LoadAsync(Game(fixture.Clock)); await gate.Entered();
        var disposal = fixture.View.DisposeAsync().AsTask();
        Placeholder(fixture.View, GameGalleryPresentationState.Cleared); Assert.False(disposal.IsCompleted); Assert.False(work.IsCompleted);
        gate.Open(); await Task.WhenAll(work, disposal).WaitAsync(Deadline);
        Placeholder(fixture.View, GameGalleryPresentationState.Cleared); Assert.False(fixture.Directory.Disposed);
        Assert.Same(disposal, fixture.View.DisposeAsync().AsTask());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public GameGalleryViewModelTests.Clock Clock { get; } = new();
        public GameGalleryViewModelTests.Directory Directory { get; }
        public GameGalleryViewModel View { get; } public List<Exception> Failures { get; } = [];
        private readonly List<Gate> _gates = [];
        public Fixture(int count = 1)
        { Directory = new(Clock, count); View = new(Directory, () => true, Failures.Add, Clock); }
        public Gate Gate() { var gate = new Gate(); _gates.Add(gate); return gate; }
        public async ValueTask DisposeAsync()
        { foreach (var gate in _gates) gate.Open(); await View.DisposeAsync().AsTask().WaitAsync(Deadline); await Directory.DisposeAsync(); }
    }
    private sealed class Gate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously), _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered() => _entered.Task.WaitAsync(Deadline);
        public async Task Hold() { _entered.TrySetResult(); await _release.Task; }
        public void Open() => _release.TrySetResult();
    }
}
