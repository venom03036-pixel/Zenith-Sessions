using System.Threading.Channels;
using Zenith.Sessions.Core;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class ClientPreviewCoordinatorTests
{
    private static ClientPreviewTarget Target(int id = 1) => new(new(null, id, new DateTime(2026, 1, 1).Ticks), (nint)(100 + id));
    private static ClientPreviewFrame Frame(CaptureCall call, ManualClock clock, long surface = 1) => new(call.Targets[0], call.Generation,
        320, 180, 1280, new byte[230400], clock.GetUtcNow(), 1920, 1080, surface);
    private static ClientPreviewResult Ready(ClientPreviewFrame frame) => new(frame.Target, ClientPreviewStatus.Ready, frame, "untrusted-provider-marker");
    private static async Task ReplyAsync(ClientPreviewCoordinator coordinator, CaptureCall call, params ClientPreviewResult[] results)
    {
        Task active = coordinator.ActiveOperation;
        call.Reply.TrySetResult(results);
        await active.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task IdleIsOptInAndUnchangedSnapshotsKeepTheirReference()
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var coordinator = new ClientPreviewCoordinator(provider, clock);
        var idle = coordinator.GetSnapshot();
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Same(idle, coordinator.GetSnapshot());
        Assert.False(idle.Running); Assert.False(idle.Stopping); Assert.Empty(idle.Results);
        Assert.Equal(0, provider.CaptureCalls); Assert.Equal(0, clock.ActiveTimers);
        coordinator.Start([Target()]);
        var call = await provider.NextAsync();
        var frame = Frame(call, clock);
        await ReplyAsync(coordinator, call, Ready(frame));
        var ready = coordinator.GetSnapshot();
        Assert.True(ready.Running); Assert.False(ready.Stopping);
        Assert.Equal(call.Generation, ready.Generation);
        Assert.Equal(ClientPreviewStatus.Ready, Assert.Single(ready.Results).Status);
        Assert.Same(ready, coordinator.GetSnapshot());
        Assert.DoesNotContain("untrusted-provider-marker", ready.Results[0].Explanation);
        await coordinator.StopAsync();
        Assert.False(coordinator.GetSnapshot().Running); Assert.Empty(coordinator.GetSnapshot().Results);
    }

    [Fact]
    public async Task AcceptedPixelsAreDetachedAndPublicationsAreBoundedToFour()
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var coordinator = new ClientPreviewCoordinator(provider, clock);
        var targets = Enumerable.Range(1, 4).Select(Target).ToArray();
        coordinator.Start(targets);
        var call = await provider.NextAsync();
        targets[0] = Target(99); // Caller-owned selection array cannot change the request.
        Assert.Equal(Target(), call.Targets[0]);
        byte[] pixels = new byte[230400]; pixels[0] = 42;
        var results = call.Targets.Select((target, index) => Ready(Frame(call, clock, index + 1)
            with { Target = target, Pixels = pixels })).ToArray();
        await ReplyAsync(coordinator, call, results);
        pixels[0] = 0;
        var snapshot = coordinator.GetSnapshot();
        Assert.Equal(4, snapshot.Results.Count);
        Assert.All(snapshot.Results, result => Assert.Equal(42, result.Frame!.Pixels.Span[0]));
        Assert.Equal(4L * ClientPreviewLimits.MaximumFrameBytes, snapshot.Results.Sum(result => (long)result.Frame!.Pixels.Length));
        await coordinator.StopAsync();
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("too-many")]
    [InlineData("duplicate")]
    [InlineData("zero-window")]
    [InlineData("bad-process")]
    public async Task InvalidSelectionsCannotCreateCaptureWork(string kind)
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var coordinator = new ClientPreviewCoordinator(provider, clock);
        ClientPreviewTarget[] targets = kind switch
        {
            "empty" => [], "too-many" => Enumerable.Range(1, 5).Select(Target).ToArray(),
            "duplicate" => [Target(), Target()], "zero-window" => [Target() with { Window = 0 }],
            _ => [Target() with { Process = new(null, 1, 0) }]
        };
        Assert.ThrowsAny<ArgumentException>(() => coordinator.Start(targets));
        Assert.Equal(0, provider.CaptureCalls);
        Assert.False(coordinator.GetSnapshot().Running);
    }

    [Fact]
    public async Task DelayStartsAfterCompletionAndClockJumpsNeverCatchUpMissedBatches()
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var coordinator = new ClientPreviewCoordinator(provider, clock);
        coordinator.Start([Target()]);
        var first = await provider.NextAsync();
        clock.Advance(TimeSpan.FromHours(1)); // A slow capture owns the lane; no overlapping calls.
        Assert.Equal(1, provider.CaptureCalls);
        await ReplyAsync(coordinator, first, Ready(Frame(first, clock)));
        await clock.NextTimerAsync();
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(provider.TryNext(out _)); Assert.Equal(1, provider.CaptureCalls);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var second = await provider.NextAsync();
        Assert.Equal(first.Generation, second.Generation);
        await ReplyAsync(coordinator, second, Ready(Frame(second, clock, 2)));
        await clock.NextTimerAsync();
        clock.Advance(TimeSpan.FromHours(1));
        var third = await provider.NextAsync();
        Assert.Equal(3, provider.CaptureCalls); Assert.Equal(1, provider.MaximumConcurrent);
        await ReplyAsync(coordinator, third, Ready(Frame(third, clock, 3)));
        await clock.NextTimerAsync();
        Assert.False(provider.TryNext(out _));
        await coordinator.StopAsync();
    }

    [Theory]
    [InlineData("window")]
    [InlineData("process-start")]
    [InlineData("selection")]
    public async Task ChangedStableContextStopsAndNeverSilentlyResumes(string change)
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var coordinator = new ClientPreviewCoordinator(provider, clock);
        var target = Target(); coordinator.Start([target]);
        var first = await provider.NextAsync();
        await ReplyAsync(coordinator, first, Ready(Frame(first, clock)));
        await clock.NextTimerAsync();
        ClientPreviewTarget changed = change switch
        {
            "window" => target with { Window = 777 },
            "process-start" => target with { Process = target.Process with { ProcessStartUtcTicks = target.Process.ProcessStartUtcTicks + 1 } },
            _ => Target(2)
        };
        coordinator.UpdateTargets([changed]);
        Assert.Empty(coordinator.GetSnapshot().Results); Assert.False(coordinator.GetSnapshot().Running);
        await coordinator.StopAsync();
        coordinator.UpdateTargets([target]); clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, provider.CaptureCalls); Assert.Equal(0, clock.ActiveTimers);
        coordinator.Start([target]);
        var restarted = await provider.NextAsync();
        Assert.True(restarted.Generation > first.Generation);
        await ReplyAsync(coordinator, restarted, Ready(Frame(restarted, clock)));
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task StopClearsImmediatelyButAwaitsAnIgnoringCaptureAndActualRelease()
    {
        var clock = new ManualClock(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider { IgnoreCancellation = true, ReleaseGate = release.Task };
        var coordinator = new ClientPreviewCoordinator(provider, clock);
        CaptureCall? call = null;
        try
        {
            coordinator.Start([Target()]); call = await provider.NextAsync();
            Task owner = coordinator.ActiveOperation;
            Task stopped = coordinator.StopAsync();
            var stopping = coordinator.GetSnapshot();
            Assert.Empty(stopping.Results); Assert.False(stopping.Running); Assert.True(stopping.Stopping);
            Assert.True(call.Token.IsCancellationRequested);
            Assert.False(stopped.IsCompleted); Assert.False(owner.IsCompleted);
            Assert.Equal(0, provider.ReleaseCalls);
            call.Reply.TrySetResult([Ready(Frame(call, clock))]);
            await owner.WaitAsync(TimeSpan.FromSeconds(5)); await provider.NextReleaseAsync();
            Assert.False(stopped.IsCompleted); Assert.True(coordinator.GetSnapshot().Stopping);
            Assert.False(coordinator.Completion.IsCompleted);
            release.SetResult(); await stopped.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(coordinator.GetSnapshot().Stopping); Assert.Empty(coordinator.GetSnapshot().Results);
        }
        finally
        {
            call?.Reply.TrySetResult([]); release.TrySetResult(); await coordinator.DisposeAsync();
        }
        Assert.Equal(1, provider.DisposeCalls); Assert.False(provider.OverlappedLifecycle);
    }

    [Fact]
    public async Task ReplacedStartIsLatestOnlyAndCannotReuseOldSourceBeforeCleanup()
    {
        var clock = new ManualClock(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider { IgnoreCancellation = true, ReleaseGate = release.Task };
        var coordinator = new ClientPreviewCoordinator(provider, clock);
        CaptureCall? first = null, latest = null;
        try
        {
            coordinator.Start([Target()]); first = await provider.NextAsync();
            for (int id = 2; id <= 1002; id++) coordinator.Start([Target(id)]);
            Assert.Empty(coordinator.GetSnapshot().Results.Where(result => result.Frame is not null));
            Assert.True(coordinator.GetSnapshot().Stopping);
            first.Reply.TrySetResult([Ready(Frame(first, clock))]);
            await provider.NextReleaseAsync();
            Assert.Equal(1, provider.CaptureCalls);
            release.SetResult();
            await clock.NextTimerAsync(); clock.Advance(TimeSpan.FromSeconds(1));
            latest = await provider.NextAsync();
            Assert.Equal(Target(1002), Assert.Single(latest.Targets));
            Assert.True(latest.Generation > first.Generation); Assert.Equal(2, provider.CaptureCalls);
            await ReplyAsync(coordinator, latest, Ready(Frame(latest, clock)));
            Assert.Equal(Target(1002), Assert.Single(coordinator.GetSnapshot().Results).Target);
            await coordinator.StopAsync();
        }
        finally
        {
            first?.Reply.TrySetResult([]); latest?.Reply.TrySetResult([]); release.TrySetResult(); await coordinator.DisposeAsync();
        }
        Assert.False(provider.OverlappedLifecycle);
    }

    [Fact]
    public async Task StuckNextCaptureCannotKeepPreviouslyPublishedPixelsFresh()
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var coordinator = new ClientPreviewCoordinator(provider, clock);
        coordinator.Start([Target()]); var first = await provider.NextAsync();
        await ReplyAsync(coordinator, first, Ready(Frame(first, clock)));
        var ready = coordinator.GetSnapshot();
        await clock.NextTimerAsync(); clock.Advance(TimeSpan.FromSeconds(1));
        var stuck = await provider.NextAsync();
        clock.Advance(TimeSpan.FromSeconds(3));
        var expired = coordinator.GetSnapshot();
        Assert.NotSame(ready, expired); Assert.True(expired.Revision > ready.Revision);
        Assert.Null(Assert.Single(expired.Results).Frame);
        Assert.Equal(ClientPreviewStatus.NoFrame, expired.Results[0].Status);
        Assert.Same(expired, coordinator.GetSnapshot());
        Assert.Equal(2, provider.CaptureCalls);
        await coordinator.StopAsync();
        Assert.True(stuck.Token.IsCancellationRequested);
    }

    public static IEnumerable<object[]> InvalidFrames()
    {
        foreach (string kind in new[] { "foreign-target", "frame-target", "generation", "width", "height", "stride", "bytes", "old",
            "future", "source-zero", "source-area", "upscale", "aspect", "surface-zero", "missing-frame", "duplicate", "foreign-row", "bad-status", "nonready-frame" })
            yield return new object[] { kind };
    }

    [Theory]
    [MemberData(nameof(InvalidFrames))]
    public async Task UnsafeOrMismatchedProviderResultsNeverPublishPixelsOrFreeText(string kind)
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var coordinator = new ClientPreviewCoordinator(provider, clock);
        coordinator.Start([Target()]); var call = await provider.NextAsync();
        var frame = Frame(call, clock);
        frame = kind switch
        {
            "frame-target" => frame with { Target = Target(2) }, "generation" => frame with { Generation = call.Generation - 1 },
            "width" => frame with { Width = 321 }, "height" => frame with { Height = 181 }, "stride" => frame with { Stride = 1281 },
            "bytes" => frame with { Pixels = new byte[230399] }, "old" => frame with { CapturedUtc = clock.GetUtcNow().AddSeconds(-4) },
            "future" => frame with { CapturedUtc = clock.GetUtcNow().AddSeconds(1) }, "source-zero" => frame with { SourceWidth = 0 },
            "source-area" => frame with { SourceWidth = 8192, SourceHeight = 8192 }, "upscale" => frame with { SourceWidth = 160, SourceHeight = 90 },
            "aspect" => frame with { SourceWidth = 1920, SourceHeight = 1920 }, "surface-zero" => frame with { SurfaceTimestampTicks = 0 },
            _ => frame
        };
        var result = Ready(frame);
        if (kind is "foreign-target" or "foreign-row") result = result with { Target = Target(2) };
        if (kind == "missing-frame") result = result with { Frame = null };
        if (kind == "bad-status") result = result with { Status = (ClientPreviewStatus)999 };
        if (kind == "nonready-frame") result = result with { Status = ClientPreviewStatus.Hidden };
        await ReplyAsync(coordinator, call, kind == "duplicate" ? [result, result] : [result]);
        var snapshot = coordinator.GetSnapshot();
        Assert.Equal(Target(), Assert.Single(snapshot.Results).Target);
        Assert.Null(snapshot.Results[0].Frame); Assert.Equal(ClientPreviewStatus.Failed, snapshot.Results[0].Status);
        Assert.DoesNotContain("untrusted-provider-marker", snapshot.Results[0].Explanation);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task VariableAspectPreservedFrameAndMissingResultsAreHandledHonestly()
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var coordinator = new ClientPreviewCoordinator(provider, clock);
        coordinator.Start([Target(), Target(2)]); var call = await provider.NextAsync();
        var frame = Frame(call, clock) with { Width = 100, Height = 100, Stride = 400, Pixels = new byte[40000], SourceWidth = 100, SourceHeight = 100 };
        await ReplyAsync(coordinator, call, Ready(frame));
        var results = coordinator.GetSnapshot().Results;
        Assert.Equal(ClientPreviewStatus.Ready, results[0].Status); Assert.Equal(100, results[0].Frame!.Width);
        Assert.Equal(ClientPreviewStatus.NoFrame, results[1].Status); Assert.Null(results[1].Frame);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task SameSurfaceTimestampCannotRenewOldPixelsEvenWithNewWallTimestamp()
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var coordinator = new ClientPreviewCoordinator(provider, clock);
        coordinator.Start([Target()]); var first = await provider.NextAsync();
        await ReplyAsync(coordinator, first, Ready(Frame(first, clock, 10)));
        for (int iteration = 0; iteration < 2; iteration++)
        {
            await clock.NextTimerAsync(); clock.Advance(TimeSpan.FromSeconds(1));
            var repeated = await provider.NextAsync(); await ReplyAsync(coordinator, repeated, Ready(Frame(repeated, clock, 10)));
            Assert.Equal(ClientPreviewStatus.NoFrame, Assert.Single(coordinator.GetSnapshot().Results).Status);
            Assert.Null(coordinator.GetSnapshot().Results[0].Frame);
        }
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task EligibilityExpiryClearsStuckOwnerAndRenewalCannotSilentlyResumeIt()
    {
        var clock = new ManualClock(); var provider = new Provider { IgnoreCancellation = true };
        var coordinator = new ClientPreviewCoordinator(provider, clock); CaptureCall? first = null, restarted = null;
        try
        {
            coordinator.Start([Target()], clock.GetUtcNow().AddSeconds(3));
            first = await provider.NextAsync();
            clock.Advance(TimeSpan.FromSeconds(3));
            var expired = coordinator.GetSnapshot();
            Assert.False(expired.Running); Assert.True(expired.Stopping); Assert.Empty(expired.Results);
            Assert.True(first.Token.IsCancellationRequested);
            coordinator.RenewEligibility(clock.GetUtcNow().AddSeconds(3));
            Assert.Same(expired, coordinator.GetSnapshot());
            Task stopped = coordinator.StopAsync(); Assert.False(stopped.IsCompleted);
            first.Reply.SetResult([Ready(Frame(first, clock))]);
            await stopped.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromHours(1)); coordinator.RenewEligibility(clock.GetUtcNow().AddSeconds(3));
            Assert.Equal(1, provider.CaptureCalls); Assert.Equal(0, clock.ActiveTimers);
            coordinator.Start([Target()], clock.GetUtcNow().AddSeconds(3));
            restarted = await provider.NextAsync();
            Assert.True(restarted.Generation > first.Generation);
            await ReplyAsync(coordinator, restarted, Ready(Frame(restarted, clock)));
            await coordinator.StopAsync();
        }
        finally
        {
            first?.Reply.TrySetResult([]); restarted?.Reply.TrySetResult([]); await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task FreshResourceLeaseRenewsOnlyTheAlreadyActiveRequest()
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var coordinator = new ClientPreviewCoordinator(provider, clock);
        coordinator.Start([Target()], clock.GetUtcNow().AddSeconds(3));
        var first = await provider.NextAsync(); await ReplyAsync(coordinator, first, Ready(Frame(first, clock)));
        await clock.NextTimerAsync(); clock.Advance(TimeSpan.FromSeconds(1));
        var second = await provider.NextAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        var beforeRenewal = coordinator.GetSnapshot();
        coordinator.RenewEligibility(clock.GetUtcNow().AddSeconds(3));
        Assert.Same(beforeRenewal, coordinator.GetSnapshot());
        await ReplyAsync(coordinator, second, Ready(Frame(second, clock, 2)));
        await clock.NextTimerAsync(); clock.Advance(TimeSpan.FromSeconds(1));
        var third = await provider.NextAsync();
        Assert.Equal(first.Generation, third.Generation); Assert.True(coordinator.GetSnapshot().Running);
        await coordinator.StopAsync(); Assert.True(third.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task EligibilityDeadlineInterruptsActiveDelayWithoutWaitingForAnotherCaptureOrSnapshotRead()
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var coordinator = new ClientPreviewCoordinator(provider, clock);
        coordinator.Start([Target()], clock.GetUtcNow().AddMilliseconds(500));
        var call = await provider.NextAsync(); await ReplyAsync(coordinator, call, Ready(Frame(call, clock)));
        await clock.NextTimerAsync(); clock.Advance(TimeSpan.FromMilliseconds(500));
        await provider.NextReleaseAsync(); await coordinator.StopAsync();
        Assert.Equal(1, provider.CaptureCalls); Assert.False(coordinator.GetSnapshot().Running);
        Assert.Empty(coordinator.GetSnapshot().Results); Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task LeaseExpiryBeforePublicationRejectsFreshButNoLongerEligiblePixels()
    {
        var clock = new ManualClock(); var provider = new Provider();
        await using var coordinator = new ClientPreviewCoordinator(provider, clock);
        coordinator.Start([Target()], clock.GetUtcNow().AddSeconds(3)); var call = await provider.NextAsync();
        clock.Advance(TimeSpan.FromSeconds(4));
        await ReplyAsync(coordinator, call, Ready(Frame(call, clock)));
        Assert.Empty(coordinator.GetSnapshot().Results); Assert.False(coordinator.GetSnapshot().Running);
        await coordinator.StopAsync();
        coordinator.Start([Target()], clock.GetUtcNow());
        Assert.False(coordinator.GetSnapshot().Running); Assert.Equal(1, provider.CaptureCalls);
    }

    [Fact]
    public async Task StopWaitCancellationDoesNotAbandonProviderAndDisposeJoinsActualOwnerIdempotently()
    {
        var clock = new ManualClock(); var provider = new Provider { IgnoreCancellation = true };
        var coordinator = new ClientPreviewCoordinator(provider, clock); CaptureCall? call = null;
        try
        {
            coordinator.Start([Target()]); call = await provider.NextAsync();
            using var stop = new CancellationTokenSource(); var waiting = coordinator.StopAsync(stop.Token); stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Task disposing = coordinator.DisposeAsync().AsTask();
            Assert.Same(disposing, coordinator.DisposeAsync().AsTask());
            Assert.False(disposing.IsCompleted); Assert.Equal(0, provider.DisposeCalls);
            call.Reply.SetResult([Ready(Frame(call, clock))]);
            await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(coordinator.Completion.IsCompleted); Assert.Equal(1, provider.DisposeCalls);
            Assert.Empty(coordinator.GetSnapshot().Results); Assert.False(coordinator.GetSnapshot().Stopping);
            Assert.Throws<ObjectDisposedException>(() => coordinator.Start([Target()]));
        }
        finally { call?.Reply.TrySetResult([]); await coordinator.DisposeAsync(); }
        Assert.False(provider.OverlappedLifecycle);
    }

    [Fact]
    public async Task ProviderFailuresAndCleanupFailureExposeOnlyFixedMessages()
    {
        var clock = new ManualClock(); var provider = new Provider { FailRelease = true };
        await using var coordinator = new ClientPreviewCoordinator(provider, clock);
        coordinator.Start([Target()]); var call = await provider.NextAsync(); Task active = coordinator.ActiveOperation;
        call.Reply.SetException(new InvalidOperationException("untrusted-provider-marker")); await active.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ClientPreviewStatus.Failed, Assert.Single(coordinator.GetSnapshot().Results).Status);
        Assert.DoesNotContain("untrusted-provider-marker", coordinator.GetSnapshot().Explanation);
        await Assert.ThrowsAsync<ClientPreviewException>(() => coordinator.StopAsync());
        Assert.True(coordinator.GetSnapshot().Stopping); Assert.Empty(coordinator.GetSnapshot().Results);
        Assert.DoesNotContain("untrusted-provider-marker", coordinator.GetSnapshot().Explanation);
    }

    private sealed record CaptureCall(IReadOnlyList<ClientPreviewTarget> Targets, long Generation, CancellationToken Token)
    {
        public TaskCompletionSource<IReadOnlyList<ClientPreviewResult>> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Provider : IClientPreviewProvider
    {
        private readonly Channel<CaptureCall> _calls = Channel.CreateBounded<CaptureCall>(16);
        private readonly Channel<bool> _releases = Channel.CreateBounded<bool>(16);
        private int _captures, _active, _releasing, _releaseCalls, _disposeCalls, _maximum;
        public bool IgnoreCancellation, FailRelease;
        public Task? ReleaseGate;
        public bool OverlappedLifecycle;
        public int CaptureCalls => Volatile.Read(ref _captures);
        public int ReleaseCalls => Volatile.Read(ref _releaseCalls);
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public int MaximumConcurrent => Volatile.Read(ref _maximum);
        public async Task<IReadOnlyList<ClientPreviewResult>> CaptureAsync(IReadOnlyList<ClientPreviewTarget> targets, long generation, CancellationToken ct)
        {
            int active = Interlocked.Increment(ref _active); _maximum = Math.Max(_maximum, active);
            if (active > 1 || Volatile.Read(ref _releasing) != 0) OverlappedLifecycle = true;
            Interlocked.Increment(ref _captures);
            var call = new CaptureCall(targets.ToArray(), generation, ct); _calls.Writer.TryWrite(call);
            try { return IgnoreCancellation ? await call.Reply.Task : await call.Reply.Task.WaitAsync(ct); }
            finally { Interlocked.Decrement(ref _active); }
        }
        public Task<CaptureCall> NextAsync() => _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public bool TryNext(out CaptureCall? call) => _calls.Reader.TryRead(out call);
        public Task<bool> NextReleaseAsync() => _releases.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public async Task ReleaseAsync(CancellationToken ct)
        {
            if (Volatile.Read(ref _active) != 0 || Interlocked.Increment(ref _releasing) != 1) OverlappedLifecycle = true;
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
        private readonly Channel<bool> _created = Channel.CreateBounded<bool>(32);
        private DateTimeOffset _utc = new(2026, 11, 5, 18, 0, 0, TimeSpan.Zero);
        private long _ticks;
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _utc; }
        public override long GetTimestamp() { lock (_gate) return _ticks; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public int ActiveTimers { get { lock (_gate) return _timers.Count(timer => timer.Due is not null); } }
        public Task<bool> NextTimerAsync() => _created.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
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
