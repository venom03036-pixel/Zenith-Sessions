using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

/// <summary>Synthetic source ownership/monotonic scheduling only. No window, D3D or WGC call.</summary>
public sealed class WindowsClientPreviewOverviewTests
{
    [Fact]
    public async Task FourLargeSourcesEachGetAnAttemptInsteadOfTheFirstPermanentlyOwningTheBudget()
    {
        var cache = new ClientPreviewSourceCache<OwnedSource>(); var clock = new Clock();
        var targets = Targets(4); var sources = new List<OwnedSource>();
        var batch = await WindowsClientPreviewProvider.RunOverviewBatchAsync(targets, target =>
        {
            Assert.True(cache.MakeRoom(new(3840, 2160), default));
            var source = new OwnedSource(); sources.Add(source); cache.Add(target, new(3840, 2160), source);
            Assert.Equal(1, cache.Count); clock.Advance(TimeSpan.FromMilliseconds(20));
            return Task.FromResult(Result(target, ClientPreviewStatus.NoFrame));
        }, clock, default);
        Assert.Equal(4, batch.AttemptedCount); Assert.Equal(targets, batch.Results.Select(value => value.Target));
        Assert.All(sources.Take(3), value => Assert.Equal(1, value.Disposed));
        Assert.Equal(0, sources[3].Disposed); cache.ReleaseAll(); Assert.Equal(1, sources[3].Disposed);
    }

    [Fact]
    public void CacheEvictsTheLeastRecentlyUsedSourceAndNeverExceedsFour()
    {
        var cache = new ClientPreviewSourceCache<OwnedSource>(); var targets = Targets(5);
        var sources = Enumerable.Range(0, 5).Select(_ => new OwnedSource()).ToArray();
        for (int index = 0; index < 4; index++) cache.Add(targets[index], new(320, 180), sources[index]);
        Assert.True(cache.TryGet(targets[0], out var first)); Assert.Same(sources[0], first);
        Assert.False(cache.Fits(new(320, 180))); Assert.True(cache.MakeRoom(new(320, 180), default));
        cache.Add(targets[4], new(320, 180), sources[4]);
        Assert.Equal(4, cache.Count); Assert.Equal(1, sources[1].Disposed);
        Assert.Equal(0, sources[0].Disposed); Assert.DoesNotContain(targets[1], cache.Keys); cache.ReleaseAll();
    }

    [Fact]
    public void AdmissionEvictsAsManyCompletedSourcesAsTheSharedByteBudgetRequires()
    {
        var cache = new ClientPreviewSourceCache<OwnedSource>(); var targets = Targets(4);
        var sources = Enumerable.Range(0, 4).Select(_ => new OwnedSource()).ToArray();
        for (int index = 0; index < 3; index++) cache.Add(targets[index], new(1920, 1080), sources[index]);
        Assert.True(cache.MakeRoom(new(3840, 2160), default));
        Assert.Equal(0, cache.Count); Assert.All(sources.Take(3), source => Assert.Equal(1, source.Disposed));
        cache.Add(targets[3], new(3840, 2160), sources[3]); Assert.Equal(1, cache.Count); cache.ReleaseAll();
    }

    [Fact]
    public void IndividuallyImpossibleSourceDoesNotEvictHealthySources()
    {
        var cache = new ClientPreviewSourceCache<OwnedSource>(); var source = new OwnedSource();
        cache.Add(Target(1), new(320, 180), source);
        Assert.False(cache.MakeRoom(new(8192, 8192), default));
        Assert.False(cache.MakeRoom(new(0, 100), default));
        Assert.Equal(1, cache.Count); Assert.Equal(0, source.Disposed); cache.ReleaseAll();
    }

    [Fact]
    public void CancelledAdmissionCannotEvictOrTransferOwnership()
    {
        var cache = new ClientPreviewSourceCache<OwnedSource>(); var source = new OwnedSource();
        cache.Add(Target(1), new(3840, 2160), source);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => cache.MakeRoom(new(3840, 2160), stop.Token));
        Assert.Equal(1, cache.Count); Assert.Equal(0, source.Disposed); cache.ReleaseAll();
    }

    [Fact]
    public void FailedEvictionRetainsSourceReservationAndQuarantinesFurtherAdmissionUntilRelease()
    {
        var cache = new ClientPreviewSourceCache<OwnedSource>(); var target = Target(1);
        var source = new OwnedSource { FailuresRemaining = 1 }; cache.Add(target, new(3840, 2160), source);
        Assert.Throws<ClientPreviewException>(() => cache.MakeRoom(new(3840, 2160), default));
        Assert.True(cache.Quarantined); Assert.Equal(1, cache.Count); Assert.Contains(target, cache.Keys);
        Assert.Throws<ClientPreviewException>(() => cache.RequireUsable());
        Assert.Throws<ClientPreviewException>(() => cache.Fits(new(1, 1)));
        Assert.Throws<ClientPreviewException>(() => cache.MakeRoom(new(1, 1), default));
        cache.ReleaseAll(); Assert.False(cache.Quarantined); Assert.Equal(0, cache.Count);
        Assert.Equal(2, source.DisposeAttempts); Assert.Equal(1, source.Disposed);
        Assert.True(cache.MakeRoom(new(3840, 2160), default));
    }

    [Fact]
    public void CleanupKeepsFailedResourcesButStillReleasesOtherOwnedEntries()
    {
        var cache = new ClientPreviewSourceCache<OwnedSource>();
        var bad = new OwnedSource { FailuresRemaining = 1 }; var good = new OwnedSource();
        cache.Add(Target(1), new(320, 180), bad); cache.Add(Target(2), new(320, 180), good);
        Assert.Throws<ClientPreviewException>(() => cache.ReleaseAll());
        Assert.True(cache.Quarantined); Assert.Equal(1, cache.Count); Assert.Equal(1, good.Disposed);
        Assert.Equal(Target(1), Assert.Single(cache.Keys));
        cache.ReleaseAll(); Assert.False(cache.Quarantined); Assert.Equal(0, cache.Count); Assert.Equal(1, bad.Disposed);
    }

    [Fact]
    public async Task EvictionWaitsForThePreviousActualAttemptToReturnItsBorrowedResource()
    {
        var cache = new ClientPreviewSourceCache<OwnedSource>(); var targets = Targets(2); var clock = new Clock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new OwnedSource(); int visited = 0;
        var task = WindowsClientPreviewProvider.RunOverviewBatchAsync(targets, async target =>
        {
            visited++;
            if (target == targets[0])
            {
                cache.Add(target, new(3840, 2160), first); first.Borrowed = true; entered.SetResult();
                try { await terminal.Task; }
                finally { first.Borrowed = false; }
            }
            else
            {
                Assert.True(cache.MakeRoom(new(3840, 2160), default));
                cache.Add(target, new(3840, 2160), new OwnedSource());
            }
            return Result(target, ClientPreviewStatus.NoFrame);
        }, clock, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(task.IsCompleted);
        Assert.Equal(1, visited); Assert.Equal(0, first.DisposeAttempts);
        terminal.SetResult(); var batch = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, batch.AttemptedCount); Assert.Equal(1, first.Disposed); cache.ReleaseAll();
    }

    [Fact]
    public async Task SoftDeadlineReturnsOnlyTheActualAttemptedPrefix()
    {
        var targets = Targets(4); var clock = new Clock();
        var batch = await WindowsClientPreviewProvider.RunOverviewBatchAsync(targets, target =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(500)); return Task.FromResult(Result(target, ClientPreviewStatus.NoFrame));
        }, clock, default);
        Assert.Equal(2, batch.AttemptedCount); Assert.Equal(targets.Take(2), batch.Results.Select(value => value.Target));
    }

    [Fact]
    public async Task LongActualAttemptIsJoinedWithoutTimeoutCleanupOrInventingUnvisitedRows()
    {
        var targets = Targets(4); var clock = new Clock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new TaskCompletionSource<ClientPreviewResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var task = WindowsClientPreviewProvider.RunOverviewBatchAsync(targets, target =>
        {
            calls++; entered.SetResult(); return terminal.Task;
        }, clock, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(task.IsCompleted); Assert.Equal(1, calls);
        terminal.SetResult(Result(targets[0], ClientPreviewStatus.NoFrame));
        var batch = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, batch.AttemptedCount); Assert.Equal(targets[0], Assert.Single(batch.Results).Target);
    }

    [Fact]
    public async Task CancellationDoesNotAbandonAnAlreadyIssuedAttemptAndStopsItsTail()
    {
        var targets = Targets(4); var clock = new Clock(); using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new TaskCompletionSource<ClientPreviewResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var task = WindowsClientPreviewProvider.RunOverviewBatchAsync(targets, target =>
        {
            calls++; entered.SetResult(); return terminal.Task;
        }, clock, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel(); Assert.False(task.IsCompleted);
        terminal.SetResult(Result(targets[0], ClientPreviewStatus.Cancelled));
        var batch = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls); Assert.Equal(1, batch.AttemptedCount);
        Assert.Equal(ClientPreviewStatus.Cancelled, Assert.Single(batch.Results).Status);
    }

    [Fact]
    public async Task TypedUnavailableResultsAreStillAttemptsAndCannotPinTheFirstPosition()
    {
        var targets = Targets(4); ClientPreviewStatus[] states = [ClientPreviewStatus.Minimized, ClientPreviewStatus.Hidden, ClientPreviewStatus.Deferred, ClientPreviewStatus.Unsupported];
        int index = 0;
        var batch = await WindowsClientPreviewProvider.RunOverviewBatchAsync(targets,
            target => Task.FromResult(Result(target, states[index++])), new Clock(), default);
        Assert.Equal(4, batch.AttemptedCount); Assert.Equal(states, batch.Results.Select(value => value.Status));
    }

    [Fact]
    public async Task WallClockChangesDoNotExtendTheMonotonicInterTargetBudget()
    {
        var targets = Targets(4); var clock = new Clock();
        var batch = await WindowsClientPreviewProvider.RunOverviewBatchAsync(targets, target =>
        {
            clock.Advance(TimeSpan.FromSeconds(1)); clock.Utc = clock.Utc.AddYears(-1);
            return Task.FromResult(Result(target, ClientPreviewStatus.NoFrame));
        }, clock, default);
        Assert.Equal(1, batch.AttemptedCount);
    }

    [Fact]
    public async Task CancellationBeforeAdmissionReturnsZeroProgressWithoutCallingTheAdapter()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel(); int calls = 0;
        var batch = await WindowsClientPreviewProvider.RunOverviewBatchAsync(Targets(4), target =>
        {
            calls++; return Task.FromResult(Result(target, ClientPreviewStatus.NoFrame));
        }, new Clock(), stop.Token);
        Assert.Equal(0, calls); Assert.Equal(0, batch.AttemptedCount); Assert.Empty(batch.Results);
    }

    [Fact]
    public async Task InternalBatchRunnerRejectsMoreThanFourBeforeCallingTheAdapter()
    {
        int calls = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => WindowsClientPreviewProvider.RunOverviewBatchAsync(Targets(5), target =>
        {
            calls++; return Task.FromResult(Result(target, ClientPreviewStatus.NoFrame));
        }, new Clock(), default));
        Assert.Equal(0, calls);
    }

    private static ClientPreviewTarget Target(int id) => new(new(null, id, DateTimeOffset.UnixEpoch.AddSeconds(id).UtcTicks), (nint)id);
    private static ClientPreviewTarget[] Targets(int count) => Enumerable.Range(1, count).Select(Target).ToArray();
    private static ClientPreviewResult Result(ClientPreviewTarget target, ClientPreviewStatus status)
        => new(target, status, null, ClientPreviewMessages.For(status));
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public DateTimeOffset Utc { get; set; } = new(2026, 11, 6, 16, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan elapsed) { _ticks += elapsed.Ticks; Utc += elapsed; }
    }
    private sealed class OwnedSource : IDisposable
    {
        public bool Borrowed { get; set; }
        public int FailuresRemaining { get; set; }
        public int DisposeAttempts { get; private set; }
        public int Disposed { get; private set; }
        public void Dispose()
        {
            DisposeAttempts++; Assert.False(Borrowed);
            if (FailuresRemaining-- > 0) throw new InvalidOperationException("Synthetic disposal failure.");
            Disposed++;
        }
    }
}
