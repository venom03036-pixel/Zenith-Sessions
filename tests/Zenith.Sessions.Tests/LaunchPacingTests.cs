using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// Controlled monotonic time, synthetic receipts, memory queue and observation/admission callbacks.
// No process, account, browser, files, native timers or external endpoint is involved.
public sealed class LaunchPacingTests
{
    private static AccountProfile Profile(string label) => AccountProfile.Create(label) with { Revision = 5, PreferredPlaceId = 1818 };
    private static readonly LaunchAccountBinding Binding = new(123, 7);
    private static readonly RobloxRequestedDestination Destination = new(1818);
    private static IReadOnlySet<Guid> Fresh(IReadOnlyList<LaunchOperation> rows)
        => rows.Where(row => row.State == LaunchState.ClientStartedUnverified).Select(row => row.Intent.OperationId).ToHashSet();
    private static LaunchCoordinator Queue(Store store, Provider provider, Clock clock, int stabilization = 20, int stagger = 2,
        Func<IReadOnlyList<LaunchOperation>, IReadOnlySet<Guid>>? observed = null,
        Func<IReadOnlyList<LaunchOperation>, LaunchAdmission>? admission = null)
        => new(store, provider, (_, _) => Task.FromResult(true), admission ?? (_ => new(true, "Synthetic healthy resources")), clock,
            startupDeadline: TimeSpan.FromMinutes(1), stabilization: TimeSpan.FromSeconds(stabilization), stagger: TimeSpan.FromSeconds(stagger),
            observedReservations: observed ?? Fresh);

    [Fact]
    public async Task DefaultUnverifiedReceiptWaitsTwentySecondsWithoutArrivalOrANewTimer()
    {
        var store = new Store(); var clock = new Clock(); var provider = new Provider(clock);
        await using var queue = Queue(store, provider, clock); await queue.InitializeAsync(default);
        var first = await queue.EnqueueAsync(Profile("First"), default, Binding, Destination); await queue.SetPausedAsync(false, default);
        await Until(() => store.Row(first.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified); await queue.WaitForActiveCleanupAsync(default);
        var pacing = queue.ReadPacing(); Assert.Equal(first.Intent.OperationId, pacing.OperationId);
        Assert.Equal(TimeSpan.FromSeconds(20), pacing.Remaining); Assert.False(pacing.Ready); Assert.Equal(LaunchPacingReason.StartupStabilization, pacing.Reason);
        Assert.Equal(0, clock.TimersCreated); Assert.Equal(0, provider.Confirmations);
        var second = await queue.EnqueueAsync(Profile("Second"), default, Binding, Destination);
        clock.Advance(TimeSpan.FromSeconds(19)); queue.Signal(); await Settle(); Assert.Single(provider.Starts);
        Assert.Equal(TimeSpan.FromSeconds(1), queue.ReadPacing().Remaining); Assert.Equal(LaunchState.Queued, store.Row(second.Intent.OperationId)!.State);
        clock.Advance(TimeSpan.FromSeconds(1)); queue.Signal(); await Until(() => provider.Starts.Count == 2);
        Assert.Equal(LaunchState.ClientStartedUnverified, store.Row(first.Intent.OperationId)!.State);
        Assert.True(LaunchDescriptions.HasUnresolvedReservation(store.Row(first.Intent.OperationId)!));
        Assert.DoesNotContain(store.Last.Operations, row => row.State == LaunchState.Arrived); Assert.Equal(0, provider.Confirmations);
    }

    [Theory]
    [InlineData(0, 0, 0, LaunchPacingReason.None)]
    [InlineData(0, 2, 2, LaunchPacingReason.BetweenStarts)]
    [InlineData(20, 2, 20, LaunchPacingReason.StartupStabilization)]
    [InlineData(20, 30, 30, LaunchPacingReason.BetweenStarts)]
    [InlineData(120, 30, 120, LaunchPacingReason.StartupStabilization)]
    public async Task UnverifiedPacingUsesTheMaximumOfItsCapturedPolicy(int stabilization, int stagger, int expected, LaunchPacingReason reason)
    {
        var store = new Store(); var clock = new Clock(); var provider = new Provider(clock);
        await using var queue = Queue(store, provider, clock, stabilization, stagger); await queue.InitializeAsync(default);
        var first = await queue.EnqueueAsync(Profile("Only"), default, Binding, Destination); await queue.SetPausedAsync(false, default);
        await Until(() => store.Row(first.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified); await queue.WaitForActiveCleanupAsync(default);
        var pacing = queue.ReadPacing(); Assert.Equal(TimeSpan.FromSeconds(expected), pacing.Remaining); Assert.Equal(expected == 0, pacing.Ready);
        Assert.Equal(reason, pacing.Reason); Assert.Equal(first.Intent.OperationId, pacing.OperationId); Assert.Equal(0, clock.TimersCreated);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task UtcJumpsCannotShortenOrExtendTheMonotonicWarmup(int direction)
    {
        var store = new Store(); var clock = new Clock(); var provider = new Provider(clock);
        await using var queue = Queue(store, provider, clock); await queue.InitializeAsync(default);
        var first = await queue.EnqueueAsync(Profile("First"), default, Binding, Destination); await queue.SetPausedAsync(false, default);
        await Until(() => store.Row(first.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified); await queue.WaitForActiveCleanupAsync(default);
        clock.JumpUtc(TimeSpan.FromDays(direction)); Assert.Equal(TimeSpan.FromSeconds(20), queue.ReadPacing().Remaining);
        await queue.EnqueueAsync(Profile("Second"), default, Binding, Destination); queue.Signal(); await Settle(); Assert.Single(provider.Starts);
        clock.Advance(TimeSpan.FromSeconds(20)); Assert.True(queue.ReadPacing().Ready); queue.Signal(); await Until(() => provider.Starts.Count == 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidMonotonicObservationLatchesClockUnavailableEvenAfterRecovery(bool throws)
    {
        var store = new Store(); var clock = new Clock(); var provider = new Provider(clock);
        await using var queue = Queue(store, provider, clock); await queue.InitializeAsync(default);
        var first = await queue.EnqueueAsync(Profile("First"), default, Binding, Destination); await queue.SetPausedAsync(false, default);
        await Until(() => store.Row(first.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified); await queue.WaitForActiveCleanupAsync(default);
        if (throws) clock.ThrowTimestamp = true; else clock.Advance(TimeSpan.FromTicks(-1));
        var failed = queue.ReadPacing(); Assert.Equal(LaunchPacingReason.ClockUnavailable, failed.Reason); Assert.False(failed.Ready);
        Assert.Equal(first.Intent.OperationId, failed.OperationId); Assert.Equal(TimeSpan.FromSeconds(20), failed.Remaining);
        clock.ThrowTimestamp = false; clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(failed, queue.ReadPacing()); await queue.EnqueueAsync(Profile("Held"), default, Binding, Destination); queue.Signal(); await Settle();
        Assert.Single(provider.Starts); Assert.True(queue.IsHealthy); Assert.DoesNotContain("Synthetic timestamp failure", JsonSerializer.Serialize(failed));
    }

    [Fact]
    public async Task ClockFailureAfterRequestValidationStillPreventsTheFinalProviderCall()
    {
        var store = new Store(); var clock = new Clock(); var provider = new Provider(clock); int validations = 0;
        await using var queue = new LaunchCoordinator(store, provider, (_, _) =>
        {
            if (Interlocked.Increment(ref validations) == 2) clock.Advance(TimeSpan.FromSeconds(-21));
            return Task.FromResult(true);
        }, _ => new(true, "Synthetic admission"), clock, stabilization: TimeSpan.FromSeconds(20), stagger: TimeSpan.FromSeconds(2), observedReservations: Fresh);
        await queue.InitializeAsync(default);
        var first = await queue.EnqueueAsync(Profile("First"), default, Binding, Destination); await queue.SetPausedAsync(false, default);
        await Until(() => store.Row(first.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified); await queue.WaitForActiveCleanupAsync(default);
        var second = await queue.EnqueueAsync(Profile("Second"), default, Binding, Destination); clock.Advance(TimeSpan.FromSeconds(20)); queue.Signal();
        await Until(() => store.Row(second.Intent.OperationId)?.State == LaunchState.Blocked); await queue.WaitForActiveCleanupAsync(default);
        Assert.Equal(2, validations); Assert.Single(provider.Starts); Assert.Null(store.Row(second.Intent.OperationId)!.ProcessReceipt);
        Assert.Equal(LaunchPacingReason.ClockUnavailable, queue.ReadPacing().Reason); Assert.False(queue.ReadPacing().Ready);
    }

    [Fact]
    public async Task ClockFailureDuringPreparedBoundaryValidationPreventsEmissionEvenAfterElapsedTimeRecovers()
    {
        var store = new Store(); var clock = new Clock(); var provider = new PreparingProvider(clock); int validations = 0;
        var boundaryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBoundary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new LaunchCoordinator(store, provider, async (_, ct) =>
        {
            if (Interlocked.Increment(ref validations) == 4)
            {
                boundaryEntered.TrySetResult();
                await releaseBoundary.Task.WaitAsync(ct);
            }
            return true;
        }, _ => new(true, "Synthetic admission"), clock, stabilization: TimeSpan.FromSeconds(20),
            stagger: TimeSpan.FromSeconds(2), observedReservations: Fresh);
        await queue.InitializeAsync(default);
        var first = await queue.EnqueueTrialAsync(Profile("First trial"), default, Binding, Destination);
        Assert.True(await queue.ResumeTrialAsync(first.Intent.OperationId, default));
        await Until(() => store.Row(first.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified);
        await queue.WaitForActiveCleanupAsync(default);
        var second = await queue.EnqueueTrialAsync(Profile("Second trial"), default, Binding, Destination);
        // Grant resume while pacing still holds the worker in Queued. Advancing first
        // permits the already-unpaused worker to admit this operation before Resume checks it.
        Assert.True(await queue.ResumeTrialAsync(second.Intent.OperationId, default));
        clock.Advance(TimeSpan.FromSeconds(20)); Assert.True(queue.ReadPacing().Ready); queue.Signal();
        try
        {
            await boundaryEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Single(provider.Starts);
            clock.Advance(TimeSpan.FromSeconds(-21));
            var failed = queue.ReadPacing();
            Assert.Equal(LaunchPacingReason.ClockUnavailable, failed.Reason); Assert.False(failed.Ready);
            // Pass the warm-up deadline without also firing the independent90s startup
            // timeout. That would test a different race and hide the final-fence result.
            clock.Advance(TimeSpan.FromSeconds(42));
            Assert.Equal(failed, queue.ReadPacing());
        }
        finally { releaseBoundary.TrySetResult(); }
        await Until(() => store.Row(second.Intent.OperationId)?.State is LaunchState.Failed or LaunchState.ClientStartedUnverified);
        await queue.WaitForActiveCleanupAsync(default);
        Assert.Equal(4, validations); Assert.Single(provider.Starts);
        Assert.Equal(LaunchState.Failed, store.Row(second.Intent.OperationId)!.State);
        Assert.Equal(LaunchProblem.StartupRejected, store.Row(second.Intent.OperationId)!.Problem);
        Assert.Null(store.Row(second.Intent.OperationId)!.ProcessReceipt);
        Assert.Equal(LaunchState.ClientStartedUnverified, store.Row(first.Intent.OperationId)!.State);
        Assert.True(LaunchDescriptions.HasUnresolvedReservation(store.Row(first.Intent.OperationId)!));
        var third = await queue.EnqueueTrialAsync(Profile("Held fresh trial"), default, Binding, Destination);
        Assert.True(await queue.ResumeTrialAsync(third.Intent.OperationId, default));
        clock.Advance(TimeSpan.FromMinutes(2)); queue.Signal(); await Settle();
        Assert.Single(provider.Starts); Assert.Equal(4, validations);
        Assert.Equal(LaunchState.Queued, store.Row(third.Intent.OperationId)!.State);
        Assert.Equal(LaunchPacingReason.ClockUnavailable, queue.ReadPacing().Reason); Assert.False(queue.ReadPacing().Ready);
        Assert.DoesNotContain(store.Last.Operations, row => row.State == LaunchState.Arrived);
        Assert.Equal(0, provider.LegacyCalls); Assert.Equal(0, provider.Confirmations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterPerformanceEditsDoNotChangeThePolicyCapturedForAnExistingStart(bool editDuringDispatch)
    {
        var store = new Store(); var clock = new Clock(); var provider = new Provider(clock);
        TaskCompletionSource<ProviderLaunchResult>? release = editDuringDispatch ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        if (release is not null) provider.FirstReply = release;
        await using var queue = Queue(store, provider, clock); await queue.InitializeAsync(default);
        if (!editDuringDispatch) queue.ConfigurePerformance(Custom(120000, 30000));
        var first = await queue.EnqueueAsync(Profile("First"), default, Binding, Destination); await queue.SetPausedAsync(false, default);
        if (release is not null)
        {
            try { await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); queue.ConfigurePerformance(Custom(120000, 30000)); }
            finally { release.TrySetResult(provider.Result(first.Intent)); }
        }
        await Until(() => store.Row(first.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified); await queue.WaitForActiveCleanupAsync(default);
        if (!editDuringDispatch) queue.ConfigurePerformance(new ManagerPerformanceSettings(ManagerPerformanceProfile.HighPerformance).Resolve());
        int oldSeconds = editDuringDispatch ? 20 : 120, nextSeconds = editDuringDispatch ? 120 : 20;
        Assert.Equal(TimeSpan.FromSeconds(oldSeconds), queue.ReadPacing().Remaining);
        await queue.EnqueueAsync(Profile("Second"), default, Binding, Destination); clock.Advance(TimeSpan.FromSeconds(oldSeconds - 1)); queue.Signal(); await Settle(); Assert.Single(provider.Starts);
        clock.Advance(TimeSpan.FromSeconds(1)); queue.Signal(); await Until(() => provider.Starts.Count == 2); await queue.WaitForActiveCleanupAsync(default);
        Assert.Equal(TimeSpan.FromSeconds(nextSeconds), queue.ReadPacing().Remaining);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("stale")]
    [InlineData("throw")]
    [InlineData("admission")]
    public async Task ElapsedWarmupStillRequiresFreshExactObservationsAndHealthyAdmission(string failure)
    {
        var store = new Store(); var clock = new Clock(); var provider = new Provider(clock); bool refreshed = false;
        long observedAt = clock.GetTimestamp();
        IReadOnlySet<Guid> Observed(IReadOnlyList<LaunchOperation> rows)
        {
            if (refreshed) return Fresh(rows);
            return failure switch
            {
                "foreign" => new HashSet<Guid> { Guid.NewGuid() },
                "stale" when clock.GetElapsedTime(observedAt) <= TimeSpan.FromSeconds(3) => Fresh(rows),
                "admission" => Fresh(rows), "throw" => throw new IOException("Synthetic observation failure"), _ => new HashSet<Guid>()
            };
        }
        await using var queue = Queue(store, provider, clock, observed: Observed,
            admission: _ => new(refreshed || failure != "admission" || provider.Starts.Count == 0, "Synthetic resource state"));
        await queue.InitializeAsync(default);
        var first = await queue.EnqueueAsync(Profile("First"), default, Binding, Destination); await queue.SetPausedAsync(false, default);
        await Until(() => store.Row(first.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified); await queue.WaitForActiveCleanupAsync(default);
        await queue.EnqueueAsync(Profile("Second"), default, Binding, Destination); clock.Advance(TimeSpan.FromSeconds(20)); queue.Signal(); await Settle();
        Assert.True(queue.ReadPacing().Ready); Assert.Single(provider.Starts);
        refreshed = true; observedAt = clock.GetTimestamp(); queue.Signal(); await Until(() => provider.Starts.Count == 2);
        Assert.Equal(LaunchState.ClientStartedUnverified, store.Row(first.Intent.OperationId)!.State); Assert.Equal(0, provider.Confirmations);
    }

    [Fact]
    public async Task WarmupDoesNotReconcileAnUncertainReceiptEvenWithAnExactProcessObservation()
    {
        var store = new Store(); var clock = new Clock(); var provider = new Provider(clock) { Status = ProviderLaunchStatus.Uncertain };
        await using var queue = Queue(store, provider, clock, observed: rows => rows.Select(row => row.Intent.OperationId).ToHashSet()); await queue.InitializeAsync(default);
        var first = await queue.EnqueueAsync(Profile("Uncertain"), default, Binding, Destination); await queue.SetPausedAsync(false, default);
        await Until(() => store.Row(first.Intent.OperationId)?.State == LaunchState.Uncertain); await queue.WaitForActiveCleanupAsync(default);
        await queue.EnqueueAsync(Profile("Second"), default, Binding, Destination); clock.Advance(TimeSpan.FromMinutes(2)); queue.Signal(); await Settle(); Assert.Single(provider.Starts);
        Assert.True(LaunchDescriptions.HasUnresolvedReservation(store.Row(first.Intent.OperationId)!));
        await queue.AcknowledgeClientClosedAsync(first.Intent.OperationId, default); queue.Signal(); await Until(() => provider.Starts.Count == 2);
    }

    [Fact]
    public async Task StopAndResumeDoNotResetOrSkipAnAlreadyCapturedWarmup()
    {
        var store = new Store(); var clock = new Clock(); var provider = new Provider(clock);
        await using var queue = Queue(store, provider, clock); await queue.InitializeAsync(default);
        var first = await queue.EnqueueAsync(Profile("First"), default, Binding, Destination); await queue.SetPausedAsync(false, default);
        await Until(() => store.Row(first.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified); await queue.WaitForActiveCleanupAsync(default);
        await queue.EnqueueAsync(Profile("Second"), default, Binding, Destination); queue.SignalPanic(); clock.Advance(TimeSpan.FromSeconds(5));
        await queue.SetPausedAsync(false, default); Assert.Equal(TimeSpan.FromSeconds(15), queue.ReadPacing().Remaining); queue.Signal(); await Settle(); Assert.Single(provider.Starts);
        clock.Advance(TimeSpan.FromSeconds(15)); queue.Signal(); await Until(() => provider.Starts.Count == 2);
    }

    [Fact]
    public async Task RestartDoesNotPersistPacingOrAutomaticallyResumeRecoveredRequests()
    {
        var store = new Store(); var clock = new Clock(); var provider = new Provider(clock); LaunchOperation first, second;
        await using (var queue = Queue(store, provider, clock))
        {
            await queue.InitializeAsync(default); first = await queue.EnqueueAsync(Profile("First"), default, Binding, Destination); await queue.SetPausedAsync(false, default);
            await Until(() => store.Row(first.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified); await queue.WaitForActiveCleanupAsync(default);
            second = await queue.EnqueueAsync(Profile("Second"), default, Binding, Destination); Assert.False(queue.ReadPacing().Ready);
            Assert.DoesNotContain("Remaining", JsonSerializer.Serialize(store.Last)); Assert.DoesNotContain("Pacing", JsonSerializer.Serialize(store.Last));
        }
        var reopenedProvider = new Provider(clock); await using var reopened = Queue(store, reopenedProvider, clock); await reopened.InitializeAsync(default);
        Assert.True(reopened.ReadPacing().Ready); Assert.Null(reopened.ReadPacing().OperationId); Assert.True(store.Last.Paused);
        clock.Advance(TimeSpan.FromMinutes(2)); reopened.Signal(); await Settle(); Assert.Empty(reopenedProvider.Starts);
        // The started client needs closure review; the never-started request stays a cancellable recovered row.
        Assert.Equal(LaunchState.ReviewRequired, store.Row(first.Intent.OperationId)!.State);
        Assert.Equal(LaunchState.Blocked, store.Row(second.Intent.OperationId)!.State);
        Assert.Equal(LaunchProblem.Interrupted, store.Row(second.Intent.OperationId)!.Problem);
    }

    [Fact]
    public async Task SupportedVerifiedArrivalStillAwaitsStabilizationThenASeparateMonotonicStagger()
    {
        var store = new Store(); var clock = new Clock(); var provider = new Provider(clock) { Status = ProviderLaunchStatus.VerifiedArrival };
        await using var queue = Queue(store, provider, clock, 5, 2); await queue.InitializeAsync(default);
        var first = await queue.EnqueueAsync(Profile("First") with { Priority = 100 }, default, Binding, Destination); await queue.EnqueueAsync(Profile("Second"), default, Binding, Destination);
        await queue.SetPausedAsync(false, default); await Until(() => store.Row(first.Intent.OperationId)?.State == LaunchState.Stabilizing && clock.ActiveTimers > 0);
        Assert.Equal(LaunchPacingReason.StartupStabilization, queue.ReadPacing().Reason); Assert.Equal(TimeSpan.FromSeconds(5), queue.ReadPacing().Remaining);
        Assert.Equal(0, provider.Confirmations); clock.Advance(TimeSpan.FromSeconds(5));
        await Until(() => store.Row(first.Intent.OperationId)?.State == LaunchState.Arrived); await queue.WaitForActiveCleanupAsync(default);
        Assert.Equal(1, provider.Confirmations); Assert.Single(provider.Starts); Assert.Equal(LaunchPacingReason.BetweenStarts, queue.ReadPacing().Reason);
        Assert.Equal(TimeSpan.FromSeconds(2), queue.ReadPacing().Remaining);
        clock.Advance(TimeSpan.FromSeconds(1)); clock.JumpUtc(TimeSpan.FromDays(1)); queue.Signal(); await Settle(); Assert.Single(provider.Starts);
        Assert.Equal(TimeSpan.FromSeconds(1), queue.ReadPacing().Remaining); clock.Advance(TimeSpan.FromSeconds(1)); queue.Signal(); await Until(() => provider.Starts.Count == 2);
    }

    [Fact]
    public async Task PreparedTrialReceiptsUseTheSameWarmupWithoutPromotingCapabilityOrArrival()
    {
        var store = new Store(); var clock = new Clock(); var provider = new PreparingProvider(clock);
        await using var queue = Queue(store, provider, clock); await queue.InitializeAsync(default);
        var first = await queue.EnqueueTrialAsync(Profile("First trial"), default, Binding, Destination); Assert.True(await queue.ResumeTrialAsync(first.Intent.OperationId, default));
        await Until(() => store.Row(first.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified); await queue.WaitForActiveCleanupAsync(default);
        var second = await queue.EnqueueTrialAsync(Profile("Second trial"), default, Binding, Destination);
        Assert.True(await queue.ResumeTrialAsync(second.Intent.OperationId, default)); clock.Advance(TimeSpan.FromSeconds(19)); queue.Signal(); await Settle(); Assert.Single(provider.Starts);
        clock.Advance(TimeSpan.FromSeconds(1)); queue.Signal(); await Until(() => provider.Starts.Count == 2);
        Assert.Equal(CapabilityState.Unverified, provider.AccountLaunch.State); Assert.Equal(0, provider.LegacyCalls); Assert.Equal(0, provider.Confirmations);
        Assert.DoesNotContain(store.Last.Operations, row => row.State == LaunchState.Arrived);
    }

    [Fact]
    public async Task SnapshotReadersObserveBoundedImmutableNonsecretTimingOnly()
    {
        var store = new Store(); var clock = new Clock(); var provider = new Provider(clock);
        await using var queue = Queue(store, provider, clock); await queue.InitializeAsync(default); Assert.True(queue.ReadPacing().Ready);
        var first = await queue.EnqueueAsync(Profile("Only"), default, Binding, Destination); await queue.SetPausedAsync(false, default);
        await Until(() => store.Row(first.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified); await queue.WaitForActiveCleanupAsync(default);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int index = 0; index < 100; index++)
            { var snapshot = queue.ReadPacing(); Assert.Equal(first.Intent.OperationId, snapshot.OperationId); Assert.InRange(snapshot.Remaining, TimeSpan.Zero, TimeSpan.FromSeconds(20)); Assert.False(snapshot.Ready); }
        })));
        string json = JsonSerializer.Serialize(queue.ReadPacing());
        foreach (string field in new[] { "ProcessId", "UserId", "PlaceId", "Credential", "SessionCookie" }) Assert.DoesNotContain(field, json);
        Assert.Equal(0, clock.TimersCreated);
    }

    [Theory]
    [InlineData(121, 0)]
    [InlineData(0, 31)]
    [InlineData(-1, 2)]
    [InlineData(20, -1)]
    public void ExcessiveOrNegativePacingDurationsAreRejectedWithoutStartingWork(int stabilization, int stagger)
    {
        var clock = new Clock(); var provider = new Provider(clock);
        Assert.Throws<ArgumentOutOfRangeException>(() => Queue(new(), provider, clock, stabilization, stagger)); Assert.Empty(provider.Starts);
    }

    private static ManagerPerformancePolicy Custom(int stabilization, int stagger) => new(ManagerPerformanceProfile.Custom, 1000, 1000, stagger, stabilization);
    private static Task Settle() => Task.Delay(25);
    private static async Task Until(Func<bool> condition)
    { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4)); while (!condition()) await Task.Delay(5, stop.Token); }
    private sealed class Store : ILaunchQueueStore
    {
        private LaunchQueueSnapshot _last = LaunchQueueSnapshot.Empty;
        public LaunchQueueSnapshot Last => Volatile.Read(ref _last);
        public LaunchOperation? Row(Guid id) => Last.Operations.FirstOrDefault(row => row.Intent.OperationId == id);
        public Task<LaunchQueueSnapshot> LoadAsync(CancellationToken ct) => Task.FromResult(Last);
        public Task SaveAsync(LaunchQueueSnapshot snapshot, CancellationToken ct)
        { Volatile.Write(ref _last, snapshot with { Operations = snapshot.Operations.ToArray() }); return Task.CompletedTask; }
    }
    private class Provider(Clock clock) : ILaunchProvider
    {
        public readonly ConcurrentQueue<LaunchIntent> Starts = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ProviderLaunchResult>? FirstReply;
        public ProviderLaunchStatus Status = ProviderLaunchStatus.ClientStartedUnverified;
        public int Confirmations;
        public virtual ProviderCapability AccountLaunch { get; } = new("Synthetic", CapabilityState.Supported, "Memory-only fixture");
        public ProviderLaunchResult Result(LaunchIntent intent) => new(Status,
            Status == ProviderLaunchStatus.VerifiedArrival ? intent.UserId : null, Status == ProviderLaunchStatus.VerifiedArrival ? intent.PlaceId : null,
            12345 + Starts.Count, clock.GetUtcNow().AddMinutes(-1).UtcTicks, Status == ProviderLaunchStatus.VerifiedArrival ? intent.RequestedJobId : null);
        protected Task<ProviderLaunchResult> Emit(LaunchIntent intent)
        { Starts.Enqueue(intent); Entered.TrySetResult(); return Starts.Count == 1 && FirstReply is not null ? FirstReply.Task : Task.FromResult(Result(intent)); }
        public virtual Task<ProviderLaunchResult> LaunchAsync(LaunchIntent intent, CancellationToken ct) => Emit(intent);
        public Task<bool> ConfirmArrivalAsync(LaunchIntent intent, ProviderLaunchResult observed, CancellationToken ct)
        { Interlocked.Increment(ref Confirmations); return Task.FromResult(true); }
    }
    private sealed class PreparingProvider(Clock clock) : Provider(clock), IPreparingLaunchProvider
    {
        public int LegacyCalls;
        public override ProviderCapability AccountLaunch { get; } = new("Synthetic trial", CapabilityState.Unverified, "No native capability proof");
        public override Task<ProviderLaunchResult> LaunchAsync(LaunchIntent intent, CancellationToken ct) { LegacyCalls++; return Task.FromResult(new ProviderLaunchResult(ProviderLaunchStatus.RejectedBeforeStart)); }
        public Task<LaunchPreparationResult> PrepareAsync(LaunchPreparationContext context, CancellationToken ct)
            => Task.FromResult(new LaunchPreparationResult(LaunchPreparationFailure.None, new Handle(this, context)));
        private sealed class Handle(PreparingProvider owner, LaunchPreparationContext context) : IPreparedAccountLaunch
        {
            private bool _closed;
            public LaunchPreparationContext Context { get; } = context;
            public LaunchIntent DispatchIntent => Context.OriginalIntent;
            public bool IsExpired => _closed;
            public async Task<ProviderLaunchResult> DispatchAsync(LaunchDispatchBoundary boundary, CancellationToken ct)
            {
                if (_closed || !await boundary.ValidateAsync(DispatchIntent, ct) || !boundary.IsCurrent || !boundary.ReadAdmission().Allowed)
                    return new(ProviderLaunchStatus.RejectedBeforeStart);
                _closed = true; return await owner.Emit(DispatchIntent);
            }
            public ValueTask DisposeAsync() { _closed = true; return ValueTask.CompletedTask; }
        }
    }
    private sealed class Clock : TimeProvider
    {
        private readonly object _gate = new(); private readonly List<Timer> _timers = [];
        private DateTimeOffset _utc = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        private long _timestamp = TimeSpan.FromHours(1).Ticks;
        public bool ThrowTimestamp;
        public int TimersCreated;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _utc; }
        public override long GetTimestamp() { lock (_gate) { if (ThrowTimestamp) throw new IOException("Synthetic timestamp failure"); return _timestamp; } }
        public int ActiveTimers { get { lock (_gate) return _timers.Count(timer => timer.Due is not null); } }
        public void JumpUtc(TimeSpan delta) { lock (_gate) _utc += delta; }
        public void Advance(TimeSpan delta)
        {
            Timer[] due;
            lock (_gate)
            { _timestamp += delta.Ticks; due = _timers.Where(timer => timer.Due <= _timestamp).ToArray(); foreach (var timer in due) timer.Due = null; }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new Timer(this, callback, state); lock (_gate) { TimersCreated++; _timers.Add(timer); timer.Change(dueTime, period); } return timer; }
        private sealed class Timer(Clock owner, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback; public object? State { get; } = state; public long? Due;
            public bool Change(TimeSpan dueTime, TimeSpan period) { lock (owner._gate) { Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._timestamp + dueTime.Ticks; return true; } }
            public void Dispose() { lock (owner._gate) { Due = null; owner._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
