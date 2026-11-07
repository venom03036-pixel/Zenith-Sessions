using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class PublicAccountPageLoaderTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static PublicAccountPageBinding Binding(int index, long user = 42, long grant = 1) => new(new Guid(index + 1, 0, 0, new byte[8]), user, grant);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static PublicDirectoryResult<PublicRobloxUser> User(long id) => new(PublicDirectoryState.Success, new(id, "Fixture_" + id, "Public fixture", DateTimeOffset.UtcNow), "Fixture only");

    [Fact]
    public async Task OnePumpDeduplicatesUserIdsAndPublishesFrozenImageToEveryExactBinding()
    {
        var directory = new Directory(); await using var presenter = new PublicAccountPresentation(directory);
        var updates = new List<PublicAccountPageUpdate>();
        await using var loader = new PublicAccountPageLoader(presenter, () => true, (update, _) => { updates.Add(update); return Task.CompletedTask; });
        loader.ReplacePage(1, new[] { Binding(0), Binding(1), Binding(2, 99) }); await loader.ActiveOperation.WaitAsync(Deadline);
        Assert.Equal(new long[] { 42, 99 }, directory.Ids); Assert.Equal(1, directory.MaximumActive);
        Assert.Equal(2, updates.Count); Assert.Equal(2, updates[0].Bindings.Count); Assert.Single(updates[1].Bindings);
        Assert.All(updates, update => { Assert.Equal(1, update.PageGeneration); Assert.True(update.Result.Value!.Avatar!.IsFrozen); });
    }

    [Theory]
    [InlineData("too-many")] [InlineData("duplicate")] [InlineData("empty-profile")] [InlineData("unknown-user")] [InlineData("unknown-grant")]
    public async Task InvalidPlansNeverReachTheDirectory(string kind)
    {
        var directory = new Directory(); await using var presenter = new PublicAccountPresentation(directory);
        await using var loader = new PublicAccountPageLoader(presenter, () => true, (_, _) => Task.CompletedTask);
        IReadOnlyList<PublicAccountPageBinding> bindings = kind switch
        {
            "too-many" => Enumerable.Range(0, 101).Select(index => Binding(index)).ToArray(),
            "duplicate" => new[] { Binding(0), Binding(0) },
            "empty-profile" => new[] { Binding(0) with { ProfileId = Guid.Empty } },
            "unknown-user" => new[] { Binding(0, 0) }, _ => new[] { Binding(0, grant: 0) }
        };
        Assert.Throws<ArgumentException>(() => loader.ReplacePage(1, bindings)); Assert.Empty(directory.Ids);
    }

    [Fact]
    public async Task ReplacementsKeepOnlyLatestPageAndJoinAnIgnoringOldProvider()
    {
        var entered = Gate(); var release = Gate(); var directory = new Directory
        { Lookup = async (id, _) => { if (id == 1) { entered.TrySetResult(); await release.Task; } return User(id); } };
        await using var presenter = new PublicAccountPresentation(directory); var updates = new List<PublicAccountPageUpdate>();
        await using var loader = new PublicAccountPageLoader(presenter, () => true, (update, _) => { updates.Add(update); return Task.CompletedTask; });
        loader.ReplacePage(1, new[] { Binding(0, 1) }); var active = loader.ActiveOperation;
        try
        {
            await entered.Task.WaitAsync(Deadline); loader.ReplacePage(2, new[] { Binding(1, 2) }); loader.ReplacePage(3, new[] { Binding(2, 3, 7) });
            Assert.Same(active, loader.ActiveOperation); Assert.False(active.IsCompleted); Assert.Equal(new long[] { 1 }, directory.Ids);
        }
        finally { release.TrySetResult(); }
        await active.WaitAsync(Deadline); Assert.Equal(new long[] { 1, 3 }, directory.Ids);
        var update = Assert.Single(updates); Assert.Equal(3, update.PageGeneration); Assert.Equal(Binding(2, 3, 7), Assert.Single(update.Bindings));
    }

    [Fact]
    public async Task ManualRefreshUsesSameLaneAfterAutomaticOwnerActuallyFinishes()
    {
        var entered = Gate(); var release = Gate(); int first = 0;
        var directory = new Directory { Lookup = async (id, _) =>
        { if (Interlocked.Increment(ref first) == 1) { entered.TrySetResult(); await release.Task; } return User(id); } };
        await using var presenter = new PublicAccountPresentation(directory);
        await using var loader = new PublicAccountPageLoader(presenter, () => true, (_, _) => Task.CompletedTask);
        loader.ReplacePage(1, new[] { Binding(0, 1), Binding(1, 2) });
        Task<PublicDirectoryResult<PublicAccountData>>? manual = null;
        try { await entered.Task.WaitAsync(Deadline); manual = loader.RefreshAsync(1, Binding(1, 2)); Assert.False(manual.IsCompleted); Assert.Equal(new long[] { 1 }, directory.Ids); }
        finally { release.TrySetResult(); }
        var result = await manual!.WaitAsync(Deadline); await loader.ActiveOperation.WaitAsync(Deadline);
        Assert.Equal(PublicDirectoryState.Success, result.State); Assert.Equal(2, result.Value!.UserId); Assert.Equal(1, directory.MaximumActive);
        Assert.Equal(1, directory.Ids.Count(id => id == 2));
    }

    [Fact]
    public async Task HidingRetainsPresenterCacheAndReopeningDoesNotRefetch()
    {
        var directory = new Directory(); await using var presenter = new PublicAccountPresentation(directory);
        await using var loader = new PublicAccountPageLoader(presenter, () => true, (_, _) => Task.CompletedTask);
        loader.ReplacePage(1, new[] { Binding(0) }); await loader.ActiveOperation.WaitAsync(Deadline);
        loader.CancelWork(); Assert.True(presenter.TryGetCached(42, out _)); Assert.Equal(0, directory.Clears);
        loader.ReplacePage(2, new[] { Binding(0) }); await loader.ActiveOperation.WaitAsync(Deadline); Assert.Single(directory.Ids);
    }

    [Fact]
    public async Task DeferredAvatarDoesNotSpinAndCachedPendingEntryDoesNotStarveAnotherUser()
    {
        var directory = new Directory { DeferredAvatar = true }; await using var presenter = new PublicAccountPresentation(directory);
        var updates = new List<PublicAccountPageUpdate>();
        await using var loader = new PublicAccountPageLoader(presenter, () => true, (update, _) => { updates.Add(update); return Task.CompletedTask; });
        loader.ReplacePage(1, new[] { Binding(0, 1), Binding(1, 2) }); await loader.ActiveOperation.WaitAsync(Deadline);
        Assert.Equal(new long[] { 1 }, directory.Ids); Assert.Equal(TimeSpan.FromHours(2), Assert.Single(updates).Result.RetryAfter);
        loader.ReplacePage(2, new[] { Binding(0, 1), Binding(1, 2) }); await loader.ActiveOperation.WaitAsync(Deadline);
        Assert.Equal(new long[] { 1, 2 }, directory.Ids); Assert.Equal(2, updates.Last().UserId);
    }

    [Fact]
    public async Task ReentrantCancelDuringPublicationStopsTheRestOfThePage()
    {
        var directory = new Directory(); await using var presenter = new PublicAccountPresentation(directory);
        PublicAccountPageLoader? loader = null; int updates = 0;
        loader = new(presenter, () => true, (_, _) => { Assert.False(loader!.ActiveOperation.IsCompleted); updates++; loader.CancelWork(); return Task.CompletedTask; });
        await using (loader)
        { loader.ReplacePage(1, new[] { Binding(0, 1), Binding(1, 2) }); await loader.ActiveOperation.WaitAsync(Deadline); Assert.Equal(1, updates); Assert.Equal(new long[] { 1 }, directory.Ids); }
    }

    [Fact]
    public async Task DisposeJoinsActualProviderAndLeavesBorrowedPresenterAlive()
    {
        var entered = Gate(); var release = Gate(); var directory = new Directory { Lookup = async (id, _) => { entered.TrySetResult(); await release.Task; return User(id); } };
        await using var presenter = new PublicAccountPresentation(directory); int updates = 0;
        var loader = new PublicAccountPageLoader(presenter, () => true, (_, _) => { updates++; return Task.CompletedTask; });
        loader.ReplacePage(1, new[] { Binding(0) }); Task? dispose = null;
        try { await entered.Task.WaitAsync(Deadline); dispose = loader.DisposeAsync().AsTask(); Assert.False(dispose.IsCompleted); }
        finally { release.TrySetResult(); await (dispose ?? loader.DisposeAsync().AsTask()).WaitAsync(Deadline); }
        Assert.Equal(0, updates); Assert.Equal(0, directory.Clears); Assert.Equal(PublicDirectoryState.Success, (await presenter.FetchAsync(99, default)).State);
        await loader.DisposeAsync();
    }

    [Fact]
    public async Task ReentrantDisposeFromPublicationTracksTheExistingPumpWithoutSelfJoining()
    {
        var directory = new Directory(); await using var presenter = new PublicAccountPresentation(directory);
        PublicAccountPageLoader? loader = null; Task? disposal = null;
        loader = new(presenter, () => true, (_, _) =>
        { Assert.False(loader!.ActiveOperation.IsCompleted); disposal = loader.DisposeAsync().AsTask(); return Task.CompletedTask; });
        loader.ReplacePage(1, new[] { Binding(0), Binding(1, 99) });
        await loader.ActiveOperation.WaitAsync(Deadline); Assert.NotNull(disposal); await disposal!.WaitAsync(Deadline);
        Assert.Single(directory.Ids); Assert.Equal(0, directory.Clears); await loader.DisposeAsync();
    }

    private sealed class Directory : IPublicRobloxDirectory
    {
        private readonly object _gate = new(); private int _active;
        public List<long> Ids { get; } = []; public int MaximumActive, Clears; public bool DeferredAvatar;
        public Func<long, CancellationToken, Task<PublicDirectoryResult<PublicRobloxUser>>>? Lookup;
        public async Task<PublicDirectoryResult<PublicRobloxUser>> LookupUserAsync(long id, CancellationToken ct)
        {
            lock (_gate) { Ids.Add(id); MaximumActive = Math.Max(MaximumActive, ++_active); }
            try { return Lookup is null ? User(id) : await Lookup(id, ct); } finally { lock (_gate) _active--; }
        }
        public Task<PublicDirectoryResult<PublicRobloxAvatar>> GetAvatarAsync(long id, CancellationToken ct) => Task.FromResult(DeferredAvatar
            ? new PublicDirectoryResult<PublicRobloxAvatar>(PublicDirectoryState.Deferred, null, "Fixture deferred", TimeSpan.FromHours(2))
            : new(PublicDirectoryState.Success, new(id, new Uri("https://tr.rbxcdn.com/fixture.png"), 150, 150, DateTimeOffset.UtcNow), "Fixture avatar"));
        public Task<PublicDirectoryResult<EncodedPublicAvatar>> FetchAvatarAsync(PublicRobloxAvatar avatar, CancellationToken ct)
            => Task.FromResult(new PublicDirectoryResult<EncodedPublicAvatar>(PublicDirectoryState.Success,
                new(PublicAccountPresentationTests.Png(), "image/png", DateTimeOffset.UtcNow), "Fixture PNG"));
        public void ClearMemoryCache() => Clears++;
    }
}
