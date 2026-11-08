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

    [Fact]
    public async Task HundredUserPageReadsHeadshotDescriptorsInTwoBatchedRequests()
    {
        var directory = new BatchDirectory(); await using var presenter = new PublicAccountPresentation(directory);
        var updates = new List<PublicAccountPageUpdate>();
        await using var loader = new PublicAccountPageLoader(presenter, () => true, (update, _) => { updates.Add(update); return Task.CompletedTask; });
        var page = Enumerable.Range(0, PublicAccountPageLoader.MaximumBindings).Select(index => Binding(index, 1000 + index)).ToArray();
        loader.ReplacePage(1, page); await loader.ActiveOperation.WaitAsync(Deadline);
        // 100 accounts: 2 descriptor requests of 50 (the batch limit) instead of 100 single ones; profile and picture stay per user.
        Assert.Equal(new[] { 50, 50 }, directory.Batches.Select(batch => batch.Length).ToArray());
        Assert.Equal(page.Select(binding => binding.UserId), directory.Batches.SelectMany(batch => batch));
        Assert.Equal(0, directory.SingleDescriptorCalls);
        Assert.Equal(100, directory.Ids.Count); Assert.Equal(100, directory.Downloads); Assert.Equal(1, directory.MaximumActive);
        Assert.Equal(100, updates.Count); Assert.All(updates, update => Assert.NotNull(update.Result.Value!.Avatar));
    }

    [Fact]
    public async Task MixedHeadshotStatesFromOneBatchPublishEveryUserWithoutSingleRequests()
    {
        var directory = new BatchDirectory();
        directory.States[2] = PublicRobloxThumbnailState.Pending; directory.States[3] = PublicRobloxThumbnailState.Blocked;
        await using var presenter = new PublicAccountPresentation(directory); var updates = new List<PublicAccountPageUpdate>();
        await using var loader = new PublicAccountPageLoader(presenter, () => true, (update, _) => { updates.Add(update); return Task.CompletedTask; });
        loader.ReplacePage(1, new[] { Binding(0, 1), Binding(1, 2), Binding(2, 3), Binding(3, 4) }); await loader.ActiveOperation.WaitAsync(Deadline);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, Assert.Single(directory.Batches)); Assert.Equal(0, directory.SingleDescriptorCalls);
        // A picture Roblox is still preparing keeps its placeholder but does not stop the rest of the page.
        Assert.Equal(new long[] { 1, 2, 3, 4 }, updates.Select(update => update.UserId).ToArray());
        Assert.NotNull(updates[0].Result.Value!.Avatar);
        Assert.Equal(PublicDirectoryState.Deferred, updates[1].Result.Value!.AvatarState); Assert.Null(updates[1].Result.Value!.Avatar);
        Assert.Equal(PublicDirectoryState.Unknown, updates[2].Result.Value!.AvatarState); Assert.Null(updates[2].Result.Value!.Avatar);
        Assert.NotNull(updates[3].Result.Value!.Avatar); Assert.Equal(2, directory.Downloads);
    }

    [Fact]
    public async Task FailedBatchFallsBackToSingleDescriptorRequests()
    {
        var directory = new BatchDirectory { BatchState = PublicDirectoryState.Failure };
        await using var presenter = new PublicAccountPresentation(directory); var updates = new List<PublicAccountPageUpdate>();
        await using var loader = new PublicAccountPageLoader(presenter, () => true, (update, _) => { updates.Add(update); return Task.CompletedTask; });
        loader.ReplacePage(1, new[] { Binding(0, 1), Binding(1, 2) }); await loader.ActiveOperation.WaitAsync(Deadline);
        Assert.Single(directory.Batches); Assert.Equal(2, directory.SingleDescriptorCalls);
        Assert.Equal(2, updates.Count); Assert.All(updates, update => Assert.NotNull(update.Result.Value!.Avatar));
    }

    [Fact]
    public async Task DeferredBatchStopsThePageWithoutPerUserRequestsOrRetries()
    {
        var directory = new BatchDirectory { BatchState = PublicDirectoryState.Deferred };
        await using var presenter = new PublicAccountPresentation(directory); var updates = new List<PublicAccountPageUpdate>();
        await using var loader = new PublicAccountPageLoader(presenter, () => true, (update, _) => { updates.Add(update); return Task.CompletedTask; });
        loader.ReplacePage(1, new[] { Binding(0, 1), Binding(1, 2) }); await loader.ActiveOperation.WaitAsync(Deadline);
        Assert.Single(directory.Batches); Assert.Empty(directory.Ids); Assert.Equal(0, directory.SingleDescriptorCalls); Assert.Empty(updates);
        loader.ReplacePage(2, new[] { Binding(0, 1), Binding(1, 2) }); await loader.ActiveOperation.WaitAsync(Deadline);
        Assert.Equal(2, directory.Batches.Count); Assert.Empty(directory.Ids); Assert.Empty(updates);
    }

    [Fact]
    public async Task CancelWorkCancelsAnInFlightBatchAndPublishesNothing()
    {
        var entered = Gate(); var directory = new BatchDirectory
        { BatchWait = async ct => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        await using var presenter = new PublicAccountPresentation(directory); int updates = 0;
        await using var loader = new PublicAccountPageLoader(presenter, () => true, (_, _) => { updates++; return Task.CompletedTask; });
        loader.ReplacePage(1, new[] { Binding(0, 1), Binding(1, 2) }); await entered.Task.WaitAsync(Deadline);
        loader.CancelWork(); await loader.ActiveOperation.WaitAsync(Deadline);
        Assert.True(directory.BatchCanceled); Assert.Empty(directory.Ids); Assert.Equal(0, updates);
    }

    [Fact]
    public async Task CachedUsersAreLeftOutOfTheNextBatch()
    {
        var directory = new BatchDirectory(); await using var presenter = new PublicAccountPresentation(directory);
        await using var loader = new PublicAccountPageLoader(presenter, () => true, (_, _) => Task.CompletedTask);
        loader.ReplacePage(1, new[] { Binding(0, 1), Binding(1, 2) }); await loader.ActiveOperation.WaitAsync(Deadline);
        loader.ReplacePage(2, new[] { Binding(0, 1), Binding(1, 2), Binding(2, 3) }); await loader.ActiveOperation.WaitAsync(Deadline);
        Assert.Equal(2, directory.Batches.Count); Assert.Equal(new long[] { 1, 2 }, directory.Batches[0]); Assert.Equal(new long[] { 3 }, directory.Batches[1]);
        Assert.Equal(new long[] { 1, 2, 3 }, directory.Ids); Assert.Equal(0, directory.SingleDescriptorCalls);
    }

    [Fact]
    public async Task HundredUserPageOverTheRealTransportCostsTwoDescriptorRequests()
    {
        // Every request ends at this owned synthetic handler. No Roblox contact, credentials or launches.
        byte[] png = PublicAccountPresentationTests.Png(); var counts = new Dictionary<string, int>(); var gate = new object();
        using var handler = new Handler(request =>
        {
            Uri uri = request.RequestUri!;
            lock (gate) counts[uri.Host] = counts.GetValueOrDefault(uri.Host) + 1;
            if (uri.Host == "users.roblox.com")
            {
                string id = uri.Segments[^1];
                return Json("{\"id\":" + id + ",\"name\":\"Fixture_" + id + "\",\"displayName\":\"Public fixture\"}");
            }
            if (uri.Host == "thumbnails.roblox.com")
            {
                string[] ids = uri.Query.Split('&')[0]["?userIds=".Length..].Split(',');
                return Json("{\"data\":[" + string.Join(",", ids.Select(id => "{\"targetId\":" + id + ",\"state\":\"Completed\",\"imageUrl\":\"https://tr.rbxcdn.com/30DAY-AvatarHeadshot-SYNTHETIC"
                    + id + "-Png/150/150/AvatarHeadshot/Png/noFilter\",\"version\":\"TN3\"}")) + "]}");
            }
            var content = new System.Net.Http.ByteArrayContent(png); content.Headers.ContentType = new("image/png");
            return new(System.Net.HttpStatusCode.OK) { Content = content };
        });
        using var http = new System.Net.Http.HttpClient(handler);
        await using var presenter = new PublicAccountPresentation(new Zenith.Sessions.Windows.RobloxPublicDirectory(http));
        var updates = new List<PublicAccountPageUpdate>();
        await using var loader = new PublicAccountPageLoader(presenter, () => true, (update, _) => { updates.Add(update); return Task.CompletedTask; });
        loader.ReplacePage(1, Enumerable.Range(0, PublicAccountPageLoader.MaximumBindings).Select(index => Binding(index, 1000 + index)).ToArray());
        await loader.ActiveOperation.WaitAsync(TimeSpan.FromSeconds(30));
        // Before batching this page cost 300 requests (100 profiles, 100 descriptors, 100 pictures); now 202.
        Assert.Equal(2, counts["thumbnails.roblox.com"]); Assert.Equal(100, counts["users.roblox.com"]); Assert.Equal(100, counts["tr.rbxcdn.com"]);
        Assert.Equal(100, updates.Count); Assert.All(updates, update => Assert.NotNull(update.Result.Value!.Avatar));
    }

    private static System.Net.Http.HttpResponseMessage Json(string json) => new(System.Net.HttpStatusCode.OK)
    { Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> handle) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(handle(request)); }
    }

    // Counts every request kind so request-count claims are measured, not inferred.
    private sealed class BatchDirectory : IPublicRobloxDirectory, IPublicRobloxAvatarBatchDirectory
    {
        private readonly object _gate = new(); private int _active;
        public List<long> Ids { get; } = []; public List<long[]> Batches { get; } = [];
        public Dictionary<long, PublicRobloxThumbnailState> States { get; } = [];
        public int MaximumActive, SingleDescriptorCalls, Downloads; public PublicDirectoryState BatchState = PublicDirectoryState.Success;
        public Func<CancellationToken, Task>? BatchWait; public bool BatchCanceled;
        private static PublicRobloxAvatar Avatar(long id) => new(id, new Uri("https://tr.rbxcdn.com/fixture" + id + ".png"), 150, 150, DateTimeOffset.UtcNow);
        private int Enter() { lock (_gate) return MaximumActive = Math.Max(MaximumActive, ++_active); }
        private void Leave() { lock (_gate) _active--; }
        public Task<PublicDirectoryResult<PublicRobloxUser>> LookupUserAsync(long id, CancellationToken ct)
        { Enter(); try { lock (_gate) Ids.Add(id); return Task.FromResult(User(id)); } finally { Leave(); } }
        public Task<PublicDirectoryResult<PublicRobloxAvatar>> GetAvatarAsync(long id, CancellationToken ct)
        {
            Enter(); try { lock (_gate) SingleDescriptorCalls++; return Task.FromResult(new PublicDirectoryResult<PublicRobloxAvatar>(PublicDirectoryState.Success, Avatar(id), "Fixture avatar")); }
            finally { Leave(); }
        }
        public async Task<PublicDirectoryResult<IReadOnlyList<PublicRobloxAvatarBatchItem>>> GetAvatarsAsync(IReadOnlyList<long> userIds, CancellationToken ct)
        {
            Enter();
            try
            {
                lock (_gate) Batches.Add(userIds.ToArray());
                Assert.InRange(userIds.Count, 1, PublicRobloxAvatarBatchLimits.MaximumBatch);
                if (BatchWait is not null)
                    try { await BatchWait(ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { BatchCanceled = true; throw; }
                if (BatchState != PublicDirectoryState.Success)
                    return new PublicDirectoryResult<IReadOnlyList<PublicRobloxAvatarBatchItem>>(BatchState, null, "Fixture batch", TimeSpan.FromSeconds(30));
                IReadOnlyList<PublicRobloxAvatarBatchItem> items = userIds.Select(id =>
                {
                    var state = States.GetValueOrDefault(id, PublicRobloxThumbnailState.Ready);
                    return new PublicRobloxAvatarBatchItem(id, state, state == PublicRobloxThumbnailState.Ready ? Avatar(id) : null);
                }).ToArray();
                return new PublicDirectoryResult<IReadOnlyList<PublicRobloxAvatarBatchItem>>(PublicDirectoryState.Success, items, "Fixture batch");
            }
            finally { Leave(); }
        }
        public Task<PublicDirectoryResult<EncodedPublicAvatar>> FetchAvatarAsync(PublicRobloxAvatar avatar, CancellationToken ct)
        {
            Enter();
            try
            {
                lock (_gate) Downloads++;
                return Task.FromResult(new PublicDirectoryResult<EncodedPublicAvatar>(PublicDirectoryState.Success,
                    new(PublicAccountPresentationTests.Png(), "image/png", DateTimeOffset.UtcNow), "Fixture PNG"));
            }
            finally { Leave(); }
        }
        public void ClearMemoryCache() { }
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
