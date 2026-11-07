using System.Threading.Channels;
using Zenith.Sessions.Core;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class ClientOverviewCoordinatorTests
{
    private static ClientPreviewTarget Target(int id = 1) => new(new(null, id, new DateTime(2026, 1, 1).Ticks), (nint)(1000 + id));
    private static ClientPreviewResult Status(ClientPreviewTarget target, ClientPreviewStatus status)
        => new(target, status, null, "untrusted-provider-marker");
    private static ClientPreviewResult Picture(Call call, ManualClock clock, int index = 0, long surface = 1,
        byte[]? pixels = null) => new(call.Targets[index], ClientPreviewStatus.Ready,
            new(call.Targets[index], call.Generation, 320, 180, 1280, pixels ?? new byte[230400],
                clock.GetUtcNow(), 1920, 1080, surface), "untrusted-provider-marker");
    private static async Task Reply(ClientPreviewCoordinator owner, Call call, int attempted, params ClientPreviewResult[] results)
    {
        Task actual = owner.ActiveOperation;
        call.Reply.TrySetResult(new(attempted, results));
        await actual.WaitAsync(TimeSpan.FromSeconds(5));
    }
    private static async Task Advance(ManualClock clock, TimeSpan? amount = null)
    {
        await clock.NextTimerAsync();
        clock.Advance(amount ?? TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(128)]
    public async Task CompletePrefixesVisitEveryRosterEntryInBoundedFourTargetBatches(int count)
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var owner = new ClientPreviewCoordinator(provider, clock);
        var roster = Enumerable.Range(1, count).Select(Target).ToArray();
        owner.StartOverview(roster);
        Assert.Equal(count, owner.GetSnapshot().Results.Count);
        var visited = new List<ClientPreviewTarget>();
        int batches = (count + 3) / 4;
        for (int batch = 0; batch < batches; batch++)
        {
            var call = await provider.NextAsync();
            Assert.True(call.IsOverview); Assert.InRange(call.Targets.Count, 1, 4);
            visited.AddRange(call.Targets);
            var rows = call.Targets.Select((_, index) => Picture(call, clock, index)).ToArray();
            await Reply(owner, call, rows.Length, rows);
            if (batch + 1 < batches) await Advance(clock);
        }
        var snapshot = owner.GetSnapshot();
        Assert.Equal(ClientPreviewMode.Overview, snapshot.Mode);
        Assert.Equal(roster, visited);
        Assert.Equal(count, snapshot.Results.Count);
        Assert.All(snapshot.Results, row => Assert.NotNull(row.Frame));
        Assert.Equal(count * (long)ClientPreviewLimits.MaximumFrameBytes,
            snapshot.Results.Sum(row => (long)row.Frame!.Pixels.Length));
        Assert.True(snapshot.Results.Sum(row => (long)row.Frame!.Pixels.Length) <= ClientPreviewLimits.MaximumOverviewPixelBytes);
        Assert.Equal(1, provider.MaximumConcurrent);
        await owner.StopAsync();
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task WindowlessRosterEntriesStayVisibleAndNeverReachTheProvider()
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var owner = new ClientPreviewCoordinator(provider, clock);
        var first = Target() with { Window = 0 }; var second = Target(2) with { Window = 0 };
        owner.StartOverview([first, Target(3), second, Target(4)]);
        var call = await provider.NextAsync();
        Assert.Equal(new[] { Target(3), Target(4) }, call.Targets);
        await Reply(owner, call, 2, Picture(call, clock), Picture(call, clock, 1));
        var rows = owner.GetSnapshot().Results;
        Assert.Equal(4, rows.Count);
        Assert.All(rows.Where(row => row.Target.Window == 0), row =>
        { Assert.Equal(ClientPreviewStatus.InvalidClient, row.Status); Assert.Null(row.Frame); });
        Assert.Equal(2, rows.Count(row => row.Status == ClientPreviewStatus.Ready));
        await owner.StopAsync();
    }

    [Theory]
    [InlineData("129")]
    [InlineData("duplicate-process")]
    [InlineData("duplicate-window")]
    [InlineData("invalid-process")]
    public async Task InvalidOverviewRosterCannotCreateCaptureWork(string kind)
    {
        var provider = new Provider();
        await using var owner = new ClientPreviewCoordinator(provider);
        ClientPreviewTarget[] roster = kind switch
        {
            "129" => Enumerable.Range(1, 129).Select(Target).ToArray(),
            "duplicate-process" => [Target(), Target() with { Window = 999 }],
            "duplicate-window" => [Target(), Target(2) with { Window = Target().Window }],
            _ => [Target() with { Process = new(null, 1, 0) }]
        };
        Assert.ThrowsAny<ArgumentException>(() => owner.StartOverview(roster));
        Assert.Equal(0, provider.CaptureCalls); Assert.False(owner.GetSnapshot().Running);
    }

    [Fact]
    public async Task ShortAttemptedPrefixStartsNextBatchAtTheUnvisitedTarget()
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var owner = new ClientPreviewCoordinator(provider, clock);
        owner.StartOverview(Enumerable.Range(1, 8).Select(Target).ToArray());
        var first = await provider.NextAsync();
        Assert.Equal(new[] { Target(), Target(2), Target(3), Target(4) }, first.Targets);
        await Reply(owner, first, 1, Picture(first, clock));
        await Advance(clock);
        var second = await provider.NextAsync();
        Assert.Equal(new[] { Target(2), Target(3), Target(4), Target(5) }, second.Targets);
        await Reply(owner, second, 2, Status(Target(2), ClientPreviewStatus.Deferred), Status(Target(3), ClientPreviewStatus.Minimized));
        await Advance(clock);
        var third = await provider.NextAsync();
        Assert.Equal(Target(4), third.Targets[0]);
        Assert.Equal(ClientPreviewStatus.Ready, owner.GetSnapshot().Results[0].Status);
        await Reply(owner, third, 0);
        await owner.StopAsync();
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("exception")]
    [InlineData("negative")]
    [InlineData("oversized")]
    [InlineData("count-mismatch")]
    [InlineData("foreign")]
    [InlineData("out-of-order")]
    public async Task NoProgressOrInvalidCoverageRotatesOneWithoutInventingAnAttempt(string kind)
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var owner = new ClientPreviewCoordinator(provider, clock);
        owner.StartOverview(Enumerable.Range(1, 8).Select(Target).ToArray());
        var first = await provider.NextAsync();
        if (kind == "exception")
        {
            Task actual = owner.ActiveOperation;
            first.Reply.SetException(new InvalidOperationException("untrusted-provider-marker"));
            await actual.WaitAsync(TimeSpan.FromSeconds(5));
        }
        else
        {
            ClientPreviewBatchResult response = kind switch
            {
                "negative" => new ClientPreviewBatchResult(-1, []),
                "oversized" => new(5, first.Targets.Select(target => Status(target, ClientPreviewStatus.NoFrame)).ToArray()),
                "count-mismatch" => new(2, [Picture(first, clock)]),
                "foreign" => new(1, [Status(Target(99), ClientPreviewStatus.NoFrame)]),
                "out-of-order" => new(2, [Status(Target(2), ClientPreviewStatus.NoFrame), Status(Target(), ClientPreviewStatus.NoFrame)]),
                _ => new(0, [])
            };
            await Reply(owner, first, response.AttemptedCount, response.Results.ToArray());
        }
        Assert.All(owner.GetSnapshot().Results, row => Assert.Null(row.Frame));
        Assert.DoesNotContain("untrusted-provider-marker", owner.GetSnapshot().Explanation);
        await clock.NextTimerAsync(); clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(1, provider.CaptureCalls);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var second = await provider.NextAsync();
        Assert.Equal(Target(2), second.Targets[0]);
        await Reply(owner, second, 0);
        await owner.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyProvidersUseConservativeProgressOnlyWhenCoverageIsAmbiguous(bool deferred)
    {
        var clock = new ManualClock(); var backend = new Provider(); var provider = new LegacyProvider(backend);
        await using var owner = new ClientPreviewCoordinator(provider, clock);
        owner.StartOverview(Enumerable.Range(1, 8).Select(Target).ToArray());
        var first = await backend.NextAsync(); Assert.False(first.IsOverview);
        var rows = first.Targets.Select(target => Status(target, deferred ? ClientPreviewStatus.Deferred : ClientPreviewStatus.NoFrame)).ToArray();
        await Reply(owner, first, 4, rows);
        await Advance(clock);
        var second = await backend.NextAsync();
        Assert.Equal(Target(deferred ? 2 : 5), second.Targets[0]);
        await Reply(owner, second, 0);
        await owner.StopAsync();
    }

    [Fact]
    public async Task OverviewPicturesBecomeStaleWithoutRedatingAndExpireAfterTheRetentionBound()
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var owner = new ClientPreviewCoordinator(provider, clock);
        owner.StartOverview([Target()]); var first = await provider.NextAsync();
        byte[] pixels = new byte[230400]; pixels[0] = 42;
        await Reply(owner, first, 1, Picture(first, clock, pixels: pixels));
        pixels[0] = 0;
        var original = Assert.Single(owner.GetSnapshot().Results).Frame!;
        Assert.Equal(42, original.Pixels.Span[0]);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(ClientPreviewStatus.Ready, Assert.Single(owner.GetSnapshot().Results).Status);
        clock.Advance(TimeSpan.FromTicks(1));
        var stale = owner.GetSnapshot();
        Assert.Equal(ClientPreviewStatus.Stale, Assert.Single(stale.Results).Status);
        Assert.Same(original, stale.Results[0].Frame);
        Assert.Equal(original.CapturedUtc, stale.Results[0].Frame!.CapturedUtc);
        Assert.Same(stale, owner.GetSnapshot());
        clock.Advance(TimeSpan.FromSeconds(117));
        Assert.Null(Assert.Single(owner.GetSnapshot().Results).Frame);
        Assert.Equal(ClientPreviewStatus.NoFrame, owner.GetSnapshot().Results[0].Status);
        await owner.StopAsync();
    }

    [Theory]
    [InlineData(ClientPreviewStatus.Deferred, true)]
    [InlineData(ClientPreviewStatus.NoFrame, true)]
    [InlineData(ClientPreviewStatus.Minimized, false)]
    [InlineData(ClientPreviewStatus.Hidden, false)]
    [InlineData(ClientPreviewStatus.InvalidClient, false)]
    [InlineData(ClientPreviewStatus.Unsupported, false)]
    public async Task RefreshResultRetainsOnlyAllowedLastKnownPictures(ClientPreviewStatus status, bool retained)
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var owner = new ClientPreviewCoordinator(provider, clock);
        owner.StartOverview([Target()]); var first = await provider.NextAsync();
        await Reply(owner, first, 1, Picture(first, clock));
        var original = owner.GetSnapshot().Results[0].Frame;
        await Advance(clock); var second = await provider.NextAsync();
        await Reply(owner, second, 1, Status(Target(), status));
        var row = Assert.Single(owner.GetSnapshot().Results);
        Assert.Equal(retained ? ClientPreviewStatus.Stale : status, row.Status);
        if (retained) Assert.Same(original, row.Frame); else Assert.Null(row.Frame);
        await owner.StopAsync();
    }

    [Fact]
    public async Task RepeatedSurfaceAcrossRotationCannotRenewAnOldPicture()
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var owner = new ClientPreviewCoordinator(provider, clock);
        owner.StartOverview(Enumerable.Range(1, 5).Select(Target).ToArray());
        var first = await provider.NextAsync();
        await Reply(owner, first, 4, first.Targets.Select((_, index) => Picture(first, clock, index, 10)).ToArray());
        var original = owner.GetSnapshot().Results[0].Frame;
        await Advance(clock); var second = await provider.NextAsync();
        Assert.Equal(new[] { Target(5), Target(), Target(2), Target(3) }, second.Targets);
        await Reply(owner, second, 2, Picture(second, clock, 0, 10), Picture(second, clock, 1, 10));
        var row = owner.GetSnapshot().Results[0];
        Assert.Equal(ClientPreviewStatus.Stale, row.Status); Assert.Same(original, row.Frame);
        Assert.NotEqual(clock.GetUtcNow(), row.Frame!.CapturedUtc);
        await owner.StopAsync();
    }

    [Theory]
    [InlineData("provider-stale")]
    [InlineData("old-ready")]
    [InlineData("future-ready")]
    [InlineData("wrong-generation")]
    public async Task ProviderCannotIntroduceHistoricalOrForeignPictures(string kind)
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var owner = new ClientPreviewCoordinator(provider, clock);
        owner.StartOverview([Target()]); var call = await provider.NextAsync();
        var result = Picture(call, clock);
        result = kind switch
        {
            "provider-stale" => result with { Status = ClientPreviewStatus.Stale },
            "old-ready" => result with { Frame = result.Frame! with { CapturedUtc = clock.GetUtcNow().AddSeconds(-4) } },
            "future-ready" => result with { Frame = result.Frame! with { CapturedUtc = clock.GetUtcNow().AddSeconds(1) } },
            _ => result with { Frame = result.Frame! with { Generation = call.Generation + 1 } }
        };
        await Reply(owner, call, 1, result);
        Assert.Null(Assert.Single(owner.GetSnapshot().Results).Frame);
        await owner.StopAsync();
    }

    [Fact]
    public async Task EmptyAndWindowlessRostersWaitAndInactiveUpdatesCannotRestartWork()
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var owner = new ClientPreviewCoordinator(provider, clock);
        owner.UpdateOverviewTargets([Target()]);
        Assert.False(owner.GetSnapshot().Running); Assert.Equal(0, provider.CaptureCalls);
        owner.StartOverview([]);
        Assert.True(owner.GetSnapshot().Running); Assert.Empty(owner.GetSnapshot().Results);
        owner.UpdateOverviewTargets([Target() with { Window = 0 }]);
        Assert.Equal(ClientPreviewStatus.InvalidClient, Assert.Single(owner.GetSnapshot().Results).Status);
        Assert.Equal(0, provider.CaptureCalls); Assert.Equal(0, clock.ActiveTimers);
        owner.UpdateOverviewTargets([Target()]);
        var call = await provider.NextAsync();
        await Reply(owner, call, 1, Picture(call, clock));
        await owner.StopAsync();
        owner.UpdateOverviewTargets([Target(2)]); owner.RenewEligibility(clock.GetUtcNow().AddSeconds(3));
        Assert.False(owner.GetSnapshot().Running); Assert.Empty(owner.GetSnapshot().Results);
        Assert.Equal(1, provider.CaptureCalls);
    }

    [Fact]
    public async Task ExpiredEmptyRosterCannotAdmitTargetsOrRenewItself()
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var owner = new ClientPreviewCoordinator(provider, clock);
        owner.StartOverview([], clock.GetUtcNow().AddSeconds(3));
        await Advance(clock, TimeSpan.FromSeconds(3));
        // A subsequent update must not turn expired consent into a new capture request.
        owner.UpdateOverviewTargets([Target()]);
        Assert.False(owner.GetSnapshot().Running); Assert.Empty(owner.GetSnapshot().Results);
        owner.RenewEligibility(clock.GetUtcNow().AddSeconds(3));
        Assert.Equal(0, provider.CaptureCalls);
    }

    [Fact]
    public async Task UnchangedRosterAndOrderOnlyChangesPreserveGenerationAndTheFairCursor()
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var owner = new ClientPreviewCoordinator(provider, clock);
        var roster = Enumerable.Range(1, 8).Select(Target).ToArray();
        owner.StartOverview(roster); var first = await provider.NextAsync();
        await Reply(owner, first, 4, first.Targets.Select(target => Status(target, ClientPreviewStatus.NoFrame)).ToArray());
        long generation = owner.GetSnapshot().Generation;
        owner.UpdateOverviewTargets(roster.Reverse().ToArray());
        Assert.Equal(generation, owner.GetSnapshot().Generation); Assert.Equal(0, provider.ReleaseCalls);
        await Advance(clock); var second = await provider.NextAsync();
        Assert.Equal(Target(5), second.Targets[0]); Assert.Equal(first.Generation, second.Generation);
        await Reply(owner, second, 0);
        await owner.StopAsync();
    }

    [Fact]
    public async Task RosterReplacementRetainsExactKeysAndNextIdentityButNeverRemovedPictures()
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var owner = new ClientPreviewCoordinator(provider, clock);
        owner.StartOverview(Enumerable.Range(1, 5).Select(Target).ToArray());
        var first = await provider.NextAsync();
        await Reply(owner, first, 4, first.Targets.Select((_, index) => Picture(first, clock, index)).ToArray());
        var original = owner.GetSnapshot().Results[0].Frame;
        ClientPreviewTarget[] replacement = [Target(), Target(3), Target(5), Target(6)];
        owner.UpdateOverviewTargets(replacement); replacement[0] = Target(99);
        var changed = owner.GetSnapshot();
        Assert.Equal(new[] { Target(), Target(3), Target(5), Target(6) }, changed.Results.Select(row => row.Target));
        Assert.Same(original, changed.Results[0].Frame);
        Assert.Equal(first.Generation, changed.Results[0].Frame!.Generation);
        Assert.True(changed.Generation > first.Generation);
        await provider.NextReleaseAsync();
        await Advance(clock); var second = await provider.NextAsync();
        Assert.Equal(Target(5), second.Targets[0]);
        await Reply(owner, second, 0);
        await owner.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateOldCaptureCannotPublishAcrossProcessOrWindowReplacement(bool replaceProcess)
    {
        var clock = new ManualClock(); var provider = new Provider { IgnoreCancellation = true };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new ClientPreviewCoordinator(provider, clock);
        Call? old = null;
        try
        {
            owner.StartOverview([Target(), Target(2)]); old = await provider.NextAsync();
            var changed = replaceProcess ? Target() with { Process = new(null, 1, Target().Process.ProcessStartUtcTicks + 1) }
                : Target() with { Window = 9999 };
            provider.ReleaseGate = release.Task;
            owner.UpdateOverviewTargets([changed, Target(2)]);
            Assert.True(old.Token.IsCancellationRequested);
            Assert.True(owner.GetSnapshot().Stopping); Assert.All(owner.GetSnapshot().Results, row => Assert.Null(row.Frame));
            provider.IgnoreCancellation = false;
            await Reply(owner, old, 2, Picture(old, clock), Picture(old, clock, 1));
            await provider.NextReleaseAsync();
            Assert.Equal(1, provider.CaptureCalls);
            Assert.All(owner.GetSnapshot().Results, row => Assert.Null(row.Frame));
            release.SetResult();
            await Advance(clock); var current = await provider.NextAsync();
            Assert.True(current.Generation > old.Generation); Assert.Equal(changed, current.Targets[0]);
            await Reply(owner, current, 2, Picture(current, clock), Picture(current, clock, 1));
            Assert.DoesNotContain(owner.GetSnapshot().Results, row => row.Target == Target());
            Assert.All(owner.GetSnapshot().Results, row => Assert.Equal(current.Generation, row.Frame!.Generation));
            await owner.StopAsync();
        }
        finally
        {
            old?.Reply.TrySetResult(new(0, [])); release.TrySetResult();
            await owner.DisposeAsync();
        }
        Assert.False(provider.OverlappedLifecycle);
    }

    [Fact]
    public async Task LeaseExpiryStopAndDisposeJoinTheActualIgnoringCaptureAndRelease()
    {
        var clock = new ManualClock(); var provider = new Provider { IgnoreCancellation = true };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.ReleaseGate = release.Task;
        var owner = new ClientPreviewCoordinator(provider, clock);
        Call? call = null; Task? stop = null; Task? dispose = null;
        try
        {
            owner.StartOverview(Enumerable.Range(1, 16).Select(Target).ToArray(), clock.GetUtcNow().AddSeconds(3));
            call = await provider.NextAsync();
            clock.Advance(TimeSpan.FromSeconds(4));
            Assert.False(owner.GetSnapshot().Running); Assert.Empty(owner.GetSnapshot().Results);
            Assert.True(call.Token.IsCancellationRequested);
            owner.UpdateOverviewTargets([Target(99)]); owner.RenewEligibility(clock.GetUtcNow().AddSeconds(3));
            stop = owner.StopAsync(); dispose = owner.DisposeAsync().AsTask();
            Assert.False(stop.IsCompleted); Assert.False(dispose.IsCompleted);
            call.Reply.SetResult(new(1, [Picture(call, clock)]));
            await provider.NextReleaseAsync();
            Assert.False(stop.IsCompleted); Assert.False(dispose.IsCompleted); Assert.Equal(1, provider.CaptureCalls);
            release.SetResult();
            await Task.WhenAll(stop, dispose).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(owner.Completion.IsCompleted); Assert.Equal(1, provider.DisposeCalls);
            Assert.Empty(owner.GetSnapshot().Results); Assert.False(provider.OverlappedLifecycle);
        }
        finally
        {
            call?.Reply.TrySetResult(new(0, [])); release.TrySetResult();
            await owner.DisposeAsync();
            if (stop is not null) await stop;
        }
    }

    [Fact]
    public async Task FailedCleanupQuarantinesReplacementOverviewUntilActualReleaseSucceeds()
    {
        var clock = new ManualClock(); var provider = new Provider { FailRelease = true };
        await using var owner = new ClientPreviewCoordinator(provider, clock);
        owner.StartOverview([Target()]); var first = await provider.NextAsync();
        await Reply(owner, first, 1, Picture(first, clock));
        await Assert.ThrowsAsync<ClientPreviewException>(() => owner.StopAsync());
        await provider.NextReleaseAsync();
        Assert.True(owner.GetSnapshot().Stopping); Assert.Empty(owner.GetSnapshot().Results);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.ReleaseGate = release.Task;
        try
        {
            owner.StartOverview([Target(2)]);
            await provider.NextReleaseAsync();
            Assert.Equal(1, provider.CaptureCalls); Assert.True(owner.GetSnapshot().Stopping);
            provider.FailRelease = false; release.SetResult();
            await Advance(clock); var second = await provider.NextAsync();
            Assert.Equal(Target(2), second.Targets[0]); Assert.Equal(2, provider.CaptureCalls);
            await Reply(owner, second, 0);
            await owner.StopAsync();
        }
        finally { release.TrySetResult(); }
        Assert.False(provider.OverlappedLifecycle);
    }

    [Fact]
    public async Task ModeSwitchKeepsLegacyCaptureAndExpirySemantics()
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var owner = new ClientPreviewCoordinator(provider, clock);
        owner.StartOverview([Target()]); var first = await provider.NextAsync();
        await Reply(owner, first, 1, Picture(first, clock));
        owner.Start([Target(2)]);
        Assert.Equal(ClientPreviewMode.Selected, owner.GetSnapshot().Mode);
        Assert.Null(Assert.Single(owner.GetSnapshot().Results).Frame);
        await provider.NextReleaseAsync(); await Advance(clock);
        var selected = await provider.NextAsync(); Assert.False(selected.IsOverview);
        await Reply(owner, selected, 1, Picture(selected, clock));
        clock.Advance(TimeSpan.FromSeconds(3).Add(TimeSpan.FromTicks(1)));
        Assert.Equal(ClientPreviewStatus.NoFrame, Assert.Single(owner.GetSnapshot().Results).Status);
        Assert.Null(owner.GetSnapshot().Results[0].Frame);
        owner.UpdateTargets([Target(3)]);
        Assert.False(owner.GetSnapshot().Running); Assert.Empty(owner.GetSnapshot().Results);
        await owner.StopAsync();
    }

    private sealed record Call(IReadOnlyList<ClientPreviewTarget> Targets, long Generation, CancellationToken Token, bool IsOverview)
    {
        public TaskCompletionSource<ClientPreviewBatchResult> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class LegacyProvider(Provider backend) : IClientPreviewProvider
    {
        public Task<IReadOnlyList<ClientPreviewResult>> CaptureAsync(IReadOnlyList<ClientPreviewTarget> targets, long generation, CancellationToken ct)
            => backend.CaptureAsync(targets, generation, ct);
        public Task ReleaseAsync(CancellationToken ct) => backend.ReleaseAsync(ct);
        public ValueTask DisposeAsync() => backend.DisposeAsync();
    }
    private sealed class Provider : IClientPreviewBatchProvider
    {
        private readonly Channel<Call> _calls = Channel.CreateBounded<Call>(16);
        private readonly Channel<bool> _releases = Channel.CreateBounded<bool>(16);
        private int _active, _releasing, _captures, _releaseCalls, _disposeCalls, _maximum;
        public bool IgnoreCancellation, FailRelease, OverlappedLifecycle;
        public Task? ReleaseGate;
        public int CaptureCalls => Volatile.Read(ref _captures);
        public int ReleaseCalls => Volatile.Read(ref _releaseCalls);
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public int MaximumConcurrent => Volatile.Read(ref _maximum);
        public async Task<IReadOnlyList<ClientPreviewResult>> CaptureAsync(IReadOnlyList<ClientPreviewTarget> targets, long generation, CancellationToken ct)
            => (await Capture(targets, generation, false, ct)).Results;
        public Task<ClientPreviewBatchResult> CaptureBatchAsync(IReadOnlyList<ClientPreviewTarget> targets, long generation, CancellationToken ct)
            => Capture(targets, generation, true, ct);
        private async Task<ClientPreviewBatchResult> Capture(IReadOnlyList<ClientPreviewTarget> targets, long generation, bool overview, CancellationToken ct)
        {
            int active = Interlocked.Increment(ref _active); _maximum = Math.Max(_maximum, active);
            if (active > 1 || Volatile.Read(ref _releasing) != 0) OverlappedLifecycle = true;
            Interlocked.Increment(ref _captures);
            var call = new Call(targets.ToArray(), generation, ct, overview); _calls.Writer.TryWrite(call);
            try { return IgnoreCancellation ? await call.Reply.Task : await call.Reply.Task.WaitAsync(ct); }
            finally { Interlocked.Decrement(ref _active); }
        }
        public Task<Call> NextAsync() => _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public Task<bool> NextReleaseAsync() => _releases.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public async Task ReleaseAsync(CancellationToken ct)
        {
            int releasing = Interlocked.Increment(ref _releasing);
            if (Volatile.Read(ref _active) != 0 || releasing != 1) OverlappedLifecycle = true;
            Interlocked.Increment(ref _releaseCalls); _releases.Writer.TryWrite(true);
            try
            {
                if (ReleaseGate is { } gate) await gate;
                if (FailRelease) throw new InvalidOperationException("untrusted-provider-marker");
            }
            finally { Interlocked.Decrement(ref _releasing); }
        }
        public ValueTask DisposeAsync()
        {
            if (Volatile.Read(ref _active) != 0 || Volatile.Read(ref _releasing) != 0) OverlappedLifecycle = true;
            Interlocked.Increment(ref _disposeCalls); return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new(); private readonly List<Timer> _timers = [];
        private readonly Channel<bool> _created = Channel.CreateBounded<bool>(256);
        private DateTimeOffset _utc = new(2026, 11, 6, 18, 0, 0, TimeSpan.Zero);
        private long _ticks;
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _utc; }
        public override long GetTimestamp() { lock (_gate) return _ticks; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public int ActiveTimers { get { lock (_gate) return _timers.Count(timer => timer.Due is not null); } }
        public async Task NextTimerAsync()
        {
            while (true)
            {
                lock (_gate) if (_timers.Any(timer => timer.Due is not null)) return;
                await _created.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            lock (_gate) { _timers.Add(timer); timer.Change(dueTime, period); }
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            List<Timer> ready;
            lock (_gate)
            {
                _utc += duration; _ticks += duration.Ticks;
                ready = _timers.Where(timer => timer.Due is { } due && due <= _ticks).ToList();
                foreach (var timer in ready) timer.Due = timer.Period > TimeSpan.Zero ? _ticks + timer.Period.Ticks : null;
            }
            foreach (var timer in ready) timer.Callback(timer.State);
        }
        private sealed class Timer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback; public object? State { get; } = state;
            public long? Due; public TimeSpan Period;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._ticks + dueTime.Ticks; Period = period;
                    if (Due is not null) owner._created.Writer.TryWrite(true);
                    return true;
                }
            }
            public void Dispose() { lock (owner._gate) { Due = null; owner._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
