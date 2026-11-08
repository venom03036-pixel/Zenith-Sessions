using System.Collections.Concurrent;
using System.IO;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class LaunchBatchQueueTests
{
    private const long Place = 110829983956014;
    private const string Job = "12345678-1234-4234-8234-123456789abc";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly RobloxRequestedDestination Target = new(Place, Job, RobloxServerKind.Public);
    private static AccountProfile Profile() => AccountProfile.Create("Synthetic account") with { Revision = 3, PreferredPlaceId = 42 };
    private static LaunchAccountBinding Binding(int account = 1) => new(5_000_000_000 + account, 2);
    private static LaunchCoordinator Queue(ILaunchQueueStore store, Provider provider,
        Func<LaunchIntent, CancellationToken, Task<bool>>? validate = null, Func<LaunchIntent, LaunchResourceBudget>? budget = null)
        => new(store, provider, validate ?? ((_, _) => Task.FromResult(true)), _ => new(true, "Synthetic"),
            stabilization: TimeSpan.Zero, stagger: TimeSpan.Zero, budgetResolver: budget);

    [Fact]
    public async Task SixteenAccountsShareOneDurableExactLinkRequestAndReopenForReview()
    {
        string root = Path.Combine(Path.GetTempPath(), "ZenithLaunchBatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var disk = new LaunchQueueStore(root);
            var store = new Store(disk);
            var provider = new Provider();
            await using var queue = Queue(store, provider);
            await queue.InitializeAsync(default);
            var accounts = Enumerable.Range(1, 16).Select(index => (Profile(), (LaunchAccountBinding?)Binding(index))).ToArray();
            var expected = accounts.ToArray();
            var parsed = RobloxDestinationParser.Parse($"https://www.roblox.com/games/start?placeId={Place}&gameInstanceId={Job}");
            var destination = Assert.IsType<RobloxRequestedDestination>(parsed.RequestedDestination);
            var review = queue.CaptureBatchReview(destination, accounts);
            accounts[0] = (Profile(), Binding(88));
            int before = store.Writes.Count;
            var rows = await queue.EnqueueBatchAsync(review);
            Assert.Equal(before + 1, store.Writes.Count);
            Assert.Equal(expected.Select(row => row.Item1.Id), rows.Select(row => row.ProfileId));
            var saved = await disk.LoadAsync(default);
            Assert.Equal(16, saved.Operations.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                var operation = Assert.IsType<LaunchOperation>(rows[i].Operation);
                Assert.Equal(LaunchBatchDisposition.Recorded, rows[i].Disposition);
                Assert.Equal(operation, saved.Operations[i]);
                Assert.Equal(LaunchState.Queued, operation.State);
                Assert.Equal(expected[i].Item1.Revision, operation.Intent.ProfileRevision);
                Assert.Equal(expected[i].Item2!.UserId, operation.Intent.UserId);
                Assert.Equal(expected[i].Item2!.AuthorizationGeneration, operation.Intent.AuthorizationGeneration);
                Assert.Equal(Place, operation.Intent.PlaceId);
                Assert.Equal(Job, operation.Intent.RequestedJobId);
                Assert.True(operation.Intent.HasExplicitDestination);
            }
            Assert.Empty(provider.Calls);
            Assert.DoesNotContain("https://", await File.ReadAllTextAsync(Path.Combine(root, "launch-queue.json")));
            await queue.DisposeAsync();

            using var reopenedDisk = new LaunchQueueStore(root);
            var reopenedStore = new Store(reopenedDisk);
            await using var reopened = Queue(reopenedStore, provider);
            await reopened.InitializeAsync(default);
            Assert.True(reopenedStore.Last.Paused);
            Assert.All(reopenedStore.Last.Operations, row =>
            { Assert.Equal(LaunchState.Blocked, row.State); Assert.Equal(LaunchProblem.Interrupted, row.Problem); });
            Assert.Empty(provider.Calls);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ReviewReportsPerAccountSkipsAndRecordsTheOthersTogether()
    {
        var store = new Store();
        var provider = new Provider();
        var occupied = Profile();
        var healthy = Profile();
        var withoutBinding = Profile() with { RobloxUserId = 999 };
        var missingBudget = Profile();
        await using var queue = Queue(store, provider, budget: intent => intent.ProfileId == missingBudget.Id
            ? throw new InvalidOperationException("SYNTHETIC PRIVATE POLICY DETAILS") : LaunchResourceBudget.FromSettings(new()));
        await queue.InitializeAsync(default);
        var existing = await queue.EnqueueAsync(occupied, default, Binding(4));
        var review = queue.CaptureBatchReview(Target,
            [(occupied, Binding(4)), (healthy, Binding()), (withoutBinding, null), (missingBudget, Binding(2)), (healthy, Binding())]);
        int before = store.Writes.Count;
        var result = await queue.EnqueueBatchAsync(review);
        Assert.Equal(before + 1, store.Writes.Count);
        Assert.Equal(new[] { occupied.Id, healthy.Id, withoutBinding.Id, missingBudget.Id }, result.Select(row => row.ProfileId));
        Assert.Equal(LaunchBatchDisposition.AlreadyPending, result[0].Disposition);
        Assert.Null(result[0].Operation);
        Assert.Equal(LaunchState.Queued, result[1].Operation?.State);
        Assert.Equal(LaunchProblem.IdentityUnverified, result[2].Operation?.Problem);
        Assert.Equal(LaunchBatchDisposition.ResourceBudgetUnavailable, result[3].Disposition);
        Assert.Null(result[3].Operation);
        Assert.Equal(existing, store.Last.Operations[0]);
        Assert.Equal(3, store.Last.Operations.Count);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task ReviewsAreOneUseQueueOwnedAndFencedByPanic()
    {
        var store = new Store();
        var otherStore = new Store();
        var provider = new Provider();
        var profile = Profile();
        await using var first = Queue(store, provider);
        await using var other = Queue(otherStore, provider);
        await first.InitializeAsync(default);
        await other.InitializeAsync(default);
        var review = first.CaptureBatchReview(Target, [(profile, Binding())]);
        await Assert.ThrowsAsync<LaunchQueueException>(() => other.EnqueueBatchAsync(review));
        Assert.Empty(otherStore.Last.Operations);
        var operation = Assert.Single(await first.EnqueueBatchAsync(review)).Operation!;
        await first.CancelAsync(operation.Intent.OperationId, default);
        await Assert.ThrowsAsync<LaunchQueueException>(() => first.EnqueueBatchAsync(review));
        var obsolete = first.CaptureBatchReview(Target, [(profile, Binding())]);
        first.SignalPanic();
        await Assert.ThrowsAsync<LaunchQueueException>(() => first.EnqueueBatchAsync(obsolete));
        Assert.Equal(LaunchState.Cancelled, Assert.Single(store.Last.Operations).State);
        Assert.Throws<ArgumentException>(() => first.CaptureBatchReview(Target,
            [(profile, Binding()), (profile with { Revision = 4 }, Binding())]));
    }

    [Fact]
    public async Task PendingQueueLimitAccountsForEveryAcceptedMemberWithoutReplacingOldRows()
    {
        var profiles = Enumerable.Range(0, 999).Select(_ => Profile()).ToArray();
        var now = DateTimeOffset.UtcNow;
        var seed = new LaunchQueueSnapshot(true, profiles.Select(profile => new LaunchOperation(
            new(Guid.NewGuid(), profile.Id, profile.Revision, 0, 42, 0, now), LaunchState.Queued, LaunchProblem.None, now)).ToArray());
        var store = new Store(seed: seed);
        await using var queue = Queue(store, new Provider());
        await queue.InitializeAsync(default);
        var newAccount = Profile();
        var beyondLimit = Profile();
        var review = queue.CaptureBatchReview(Target, [(profiles[0], null), (newAccount, Binding()), (beyondLimit, Binding(2))]);
        var result = await queue.EnqueueBatchAsync(review);
        Assert.Equal(new[] { LaunchBatchDisposition.AlreadyPending, LaunchBatchDisposition.Recorded, LaunchBatchDisposition.QueueFull },
            result.Select(row => row.Disposition));
        Assert.Equal(1000, store.Last.Operations.Count);
        Assert.Equal(seed.Operations.Select(o => o.Intent.OperationId), store.Last.Operations.Take(999).Select(o => o.Intent.OperationId));
        Assert.Equal(newAccount.Id, store.Last.Operations[^1].Intent.ProfileId);
        LaunchCoordinator.ValidateSnapshot(store.Last);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PanicOrCallerCancellationDuringDurableSaveCancelsTheBatchBeforePublication(bool callerCancellation)
    {
        var store = new Store();
        var provider = new Provider();
        await using var queue = Queue(store, provider);
        await queue.InitializeAsync(default);
        var old = await queue.EnqueueAsync(Profile(), default);
        Assert.True(await queue.TryResumeAsync(default));
        var visible = new ConcurrentQueue<LaunchQueueSnapshot>();
        queue.Changed += visible.Enqueue;
        using var cancel = new CancellationTokenSource();
        var gate = store.HoldNextWrite();
        var review = queue.CaptureBatchReview(Target, [(Profile(), Binding()), (Profile(), Binding(2))]);
        var work = queue.EnqueueBatchAsync(review, cancel.Token);
        try
        {
            var inFlight = await gate.Entered.Task.WaitAsync(Deadline);
            Assert.Equal(2, inFlight.Operations.Count(operation => operation.State == LaunchState.Queued));
            if (callerCancellation) cancel.Cancel(); else queue.SignalPanic();
            Assert.False(work.IsCompleted);
            Assert.Empty(provider.Calls);
        }
        finally { gate.Release.TrySetResult(); }

        var rows = await work.WaitAsync(Deadline);
        Assert.All(rows, row => Assert.Equal(LaunchState.Cancelled, row.Operation?.State));
        Assert.NotEmpty(visible);
        Assert.All(visible, snapshot => Assert.DoesNotContain(snapshot.Operations, operation => operation.State == LaunchState.Queued));
        Assert.Equal(old, store.Last.Operations[0]);
        Assert.Empty(provider.Calls);
        Assert.True(queue.IsHealthy);
    }

    [Fact]
    public async Task PanicFromAnObserverPreventsLaterObserversFromSeeingAQueuedBatch()
    {
        var store = new Store();
        var provider = new Provider();
        await using var queue = Queue(store, provider);
        await queue.InitializeAsync(default);
        queue.Changed += snapshot =>
        {
            if (snapshot.Operations.Any(operation => operation.State == LaunchState.Queued)) queue.SignalPanic();
        };
        var visible = new ConcurrentQueue<LaunchQueueSnapshot>();
        queue.Changed += visible.Enqueue;
        var review = queue.CaptureBatchReview(Target, [(Profile(), Binding()), (Profile(), Binding(2))]);
        var rows = await queue.EnqueueBatchAsync(review);
        Assert.All(rows, row => Assert.Equal(LaunchState.Cancelled, row.Operation?.State));
        Assert.NotEmpty(visible);
        Assert.All(visible, snapshot => Assert.All(snapshot.Operations, operation => Assert.Equal(LaunchState.Cancelled, operation.State)));
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task ShutdownWaitsForTheOwnedWriteAndItsDurableStopCorrection()
    {
        var store = new Store();
        var provider = new Provider();
        await using var queue = Queue(store, provider);
        await queue.InitializeAsync(default);
        var review = queue.CaptureBatchReview(Target, [(Profile(), Binding()), (Profile(), Binding(2))]);
        var gate = store.HoldNextWrite();
        var work = queue.EnqueueBatchAsync(review);
        Task shutdown;
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            shutdown = queue.DisposeAsync().AsTask();
            Assert.False(shutdown.IsCompleted);
            Assert.False(work.IsCompleted);
        }
        finally { gate.Release.TrySetResult(); }

        Assert.All(await work.WaitAsync(Deadline), row => Assert.Equal(LaunchState.Cancelled, row.Operation?.State));
        await shutdown.WaitAsync(Deadline);
        Assert.True(store.Last.Paused);
        Assert.All(store.Last.Operations, row => Assert.Equal(LaunchState.Cancelled, row.State));
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task SingleRequestShutdownAlsoWaitsForTheOwnedWrite()
    {
        var store = new Store();
        var provider = new Provider();
        await using var queue = Queue(store, provider);
        await queue.InitializeAsync(default);
        var gate = store.HoldNextWrite();
        var work = queue.EnqueueAsync(Profile(), default, Binding());
        Task shutdown;
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            shutdown = queue.DisposeAsync().AsTask();
            await Assert.ThrowsAsync<TimeoutException>(() => shutdown.WaitAsync(TimeSpan.FromMilliseconds(150)));
        }
        finally { gate.Release.TrySetResult(); }

        Assert.Equal(LaunchState.Cancelled, (await work.WaitAsync(Deadline)).State);
        await shutdown.WaitAsync(Deadline);
        Assert.Equal(LaunchState.Cancelled, Assert.Single(store.Last.Operations).State);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task FailedBatchWritePreservesTheLastDurableQueueAndPublishesNoAcceptance()
    {
        var store = new Store();
        var provider = new Provider();
        await using var queue = Queue(store, provider);
        await queue.InitializeAsync(default);
        var old = await queue.EnqueueAsync(Profile(), default);
        var visible = new ConcurrentQueue<LaunchQueueSnapshot>();
        queue.Changed += visible.Enqueue;
        store.FailWrites = true;
        var review = queue.CaptureBatchReview(Target, [(Profile(), Binding()), (Profile(), Binding(2))]);
        await Assert.ThrowsAsync<IOException>(() => queue.EnqueueBatchAsync(review));
        Assert.Equal(old, Assert.Single(store.Last.Operations));
        Assert.Empty(visible);
        Assert.False(queue.IsHealthy);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task EveryRecipientStillRequiresItsPinnedAuthorizationAndProviderCapability()
    {
        var store = new Store();
        var provider = new Provider();
        var changed = Profile();
        var stillAuthorized = Profile();
        await using var queue = Queue(store, provider, (intent, _) => Task.FromResult(intent.ProfileId == stillAuthorized.Id));
        await queue.InitializeAsync(default);
        var review = queue.CaptureBatchReview(Target, [(changed, Binding()), (stillAuthorized, Binding(2))]);
        var rows = await queue.EnqueueBatchAsync(review);
        Assert.True(await queue.TryResumeAsync(default));
        using var timeout = new CancellationTokenSource(Deadline);
        while (store.Last.Operations.Any(operation => !LaunchDescriptions.Terminal(operation.State)))
            await Task.Delay(10, timeout.Token);
        Assert.Equal(LaunchProblem.ProfileChanged, store.Last.Operations.Single(row => row.Intent.ProfileId == changed.Id).Problem);
        var emitted = Assert.Single(provider.Calls);
        Assert.Equal(rows[1].Operation!.Intent, emitted);
        Assert.Equal(Job, emitted.RequestedJobId);

        var unsupportedStore = new Store();
        var unsupported = new Provider(CapabilityState.Unverified);
        await using var second = Queue(unsupportedStore, unsupported);
        await second.InitializeAsync(default);
        var blocked = await second.EnqueueBatchAsync(second.CaptureBatchReview(Target, [(Profile(), Binding())]));
        Assert.Equal(LaunchProblem.ProviderUnavailable, Assert.Single(blocked).Operation?.Problem);
        Assert.True(await second.TryResumeAsync(default));
        Assert.Empty(unsupported.Calls);
    }

    [Fact]
    public async Task RetryPinsTheRejectedServerAndTheFreshBindingWithoutChangingSuccessfulMembers()
    {
        var failed = Profile();
        var arrived = Profile();
        var store = new Store();
        var provider = new Provider(result: intent => intent.ProfileId == arrived.Id
            ? new(ProviderLaunchStatus.VerifiedArrival, intent.UserId, intent.PlaceId, 42, DateTime.UtcNow.Ticks, intent.RequestedJobId)
            : new(ProviderLaunchStatus.RejectedBeforeStart));
        var validated = new ConcurrentQueue<LaunchIntent>();
        await using var queue = Queue(store, provider, (intent, _) =>
        {
            validated.Enqueue(intent);
            return Task.FromResult(true);
        });
        await queue.InitializeAsync(default);
        await queue.EnqueueBatchAsync(queue.CaptureBatchReview(Target, [(failed, Binding()), (arrived, Binding(2))]));
        Assert.True(await queue.TryResumeAsync(default));
        using var timeout = new CancellationTokenSource(Deadline);
        while (store.Last.Operations[0].State != LaunchState.Failed || store.Last.Operations[1].State != LaunchState.Arrived)
            await Task.Delay(10, timeout.Token);
        await queue.SetPausedAsync(true, default);
        var history = store.Last.Operations[0];
        var successful = store.Last.Operations[1];
        var current = failed with { Revision = failed.Revision + 2, PreferredPlaceId = 123456, Priority = 80 };
        var fresh = Binding() with { AuthorizationGeneration = 9 };
        var review = queue.CaptureBatchRetryReview([(history, current, fresh)]);
        Assert.Equal(history.Intent.OperationId, Assert.Single(review.Recipients).RetryOfOperationId);
        Assert.Equal(Place, review.Destination.PlaceId);
        Assert.Equal(Job, review.Destination.JobId);
        Assert.Equal(RobloxServerKind.Public, review.Destination.ServerKind);
        int writes = store.Writes.Count;
        var row = Assert.Single(await queue.EnqueueBatchAsync(review));
        Assert.Equal(LaunchBatchDisposition.Recorded, row.Disposition);
        var retry = Assert.IsType<LaunchOperation>(row.Operation);
        Assert.NotEqual(history.Intent.OperationId, retry.Intent.OperationId);
        Assert.Equal(history.Intent with
        {
            OperationId = retry.Intent.OperationId,
            RequestedUtc = retry.Intent.RequestedUtc,
            ProfileRevision = current.Revision,
            Priority = current.Priority,
            AuthorizationGeneration = fresh.AuthorizationGeneration
        }, retry.Intent);
        Assert.Equal(writes + 1, store.Writes.Count);
        Assert.Equal(new[] { history, successful, retry }, store.Last.Operations);
        Assert.True(store.Last.Paused);
        Assert.Equal(2, provider.Calls.Count);
        Assert.True(await queue.TryResumeAsync(default));
        while (store.Last.Operations[^1].State != LaunchState.Failed)
            await Task.Delay(10, timeout.Token);
        Assert.Equal(retry.Intent, validated.Last());
        Assert.Equal(retry.Intent, provider.Calls.Last());
        Assert.Equal(3, provider.Calls.Count);
        Assert.Equal(successful, store.Last.Operations[1]);
    }

    [Theory]
    [InlineData(LaunchState.Uncertain, LaunchProblem.TimedOut)]
    [InlineData(LaunchState.ClientStartedUnverified, LaunchProblem.ClientStartedUnverified)]
    [InlineData(LaunchState.ReviewRequired, LaunchProblem.Interrupted)]
    [InlineData(LaunchState.Cancelled, LaunchProblem.ClientClosureAcknowledged)]
    [InlineData(LaunchState.Arrived, LaunchProblem.None)]
    [InlineData(LaunchState.Failed, LaunchProblem.ProfileChanged)]
    [InlineData(LaunchState.Failed, LaunchProblem.PreparationCleanupFailed)]
    [InlineData(LaunchState.Failed, LaunchProblem.PreparationTimedOut)]
    [InlineData(LaunchState.Failed, LaunchProblem.PreparationInterrupted)]
    [InlineData(LaunchState.Failed, LaunchProblem.CancelledDuringStartup)]
    [InlineData(LaunchState.Uncertain, LaunchProblem.PreparationRejected)]
    [InlineData(LaunchState.ReviewRequired, LaunchProblem.PreparationExpired)]
    [InlineData(LaunchState.Cancelled, LaunchProblem.None)]
    public async Task RetryRequiresProofThatTheRequestedStartWasRejected(LaunchState state, LaunchProblem problem)
    {
        var profile = Profile();
        var previous = Rejected(profile) with { State = state, Problem = problem };
        var store = new Store();
        await using var queue = Queue(store, new Provider());
        Assert.Throws<ArgumentException>(() => queue.CaptureBatchRetryReview([(previous, profile, Binding())]));
        Assert.Throws<ArgumentException>(() => queue.CaptureBatchRetryReview([(PlaceOnly(previous), profile, Binding())]));
        Assert.Empty(store.Writes);
    }

    [Theory]
    [InlineData(LaunchProblem.PreparationRejected)]
    [InlineData(LaunchProblem.PreparationExpired)]
    public async Task RetryAcceptsAPreparationFailureRecordedBeforeAnyClientStarted(LaunchProblem problem)
    {
        var profile = Profile();
        var prior = Rejected(profile) with { Problem = problem };
        var store = new Store(seed: new(true, [prior]));
        var provider = new Provider();
        await using var queue = Queue(store, provider);
        await queue.InitializeAsync(default);
        var review = queue.CaptureBatchRetryReview([(prior, profile, Binding())]);
        Assert.Equal(prior.Intent.OperationId, Assert.Single(review.Recipients).RetryOfOperationId);
        Assert.Equal(Job, review.Destination.JobId);
        var row = Assert.Single(await queue.EnqueueBatchAsync(review));
        Assert.Equal(LaunchBatchDisposition.Recorded, row.Disposition);
        var retry = Assert.IsType<LaunchOperation>(row.Operation);
        Assert.Equal(LaunchState.Queued, retry.State);
        Assert.Equal(prior.Intent with { OperationId = retry.Intent.OperationId, RequestedUtc = retry.Intent.RequestedUtc }, retry.Intent);
        Assert.Equal(new[] { prior, retry }, store.Last.Operations);
        Assert.True(store.Last.Paused);
        Assert.Empty(provider.Calls);

        var withProcess = prior with { ProcessReceipt = new(42, prior.UpdatedUtc.UtcTicks, prior.UpdatedUtc) };
        Assert.Throws<ArgumentException>(() => queue.CaptureBatchRetryReview([(withProcess, profile, Binding())]));
    }

    [Fact]
    public async Task RetryKeepsAPlaceOnlyRequestPlaceOnlyWhenEveryAccountSharesThatPlace()
    {
        var first = Profile();
        var second = Profile();
        var rejected = PlaceOnly(Rejected(first));
        var expired = PlaceOnly(Rejected(second, Binding(2)) with { Problem = LaunchProblem.PreparationExpired });
        var store = new Store(seed: new(true, [rejected, expired]));
        var provider = new Provider();
        await using var queue = Queue(store, provider);
        await queue.InitializeAsync(default);
        var review = queue.CaptureBatchRetryReview([(rejected, first, Binding()), (expired, second, Binding(2))]);
        Assert.Equal(Place, review.Destination.PlaceId);
        Assert.Null(review.Destination.JobId);
        Assert.Equal(RobloxServerKind.Unknown, review.Destination.ServerKind);
        Assert.Equal(new Guid?[] { rejected.Intent.OperationId, expired.Intent.OperationId }, review.Recipients.Select(member => member.RetryOfOperationId));
        int writes = store.Writes.Count;
        var rows = await queue.EnqueueBatchAsync(review);
        Assert.Equal(writes + 1, store.Writes.Count);
        Assert.All(rows, row => Assert.Equal(LaunchBatchDisposition.Recorded, row.Disposition));
        Assert.All(rows, row =>
        {
            var intent = Assert.IsType<LaunchOperation>(row.Operation).Intent;
            Assert.Equal(Place, intent.PlaceId);
            Assert.Null(intent.RequestedJobId);
            Assert.Equal(RobloxServerKind.Unknown, intent.RequestedServerKind);
            Assert.True(intent.HasExplicitDestination);
        });
        Assert.Equal(4, store.Last.Operations.Count);
        Assert.True(store.Last.Paused);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task RetryRejectsIdentityChangesMixedDestinationsAndProfileDefaultPlaces()
    {
        var account = Profile();
        var other = Profile();
        var history = Rejected(account);
        var otherHistory = Rejected(other, Binding(2));
        var wrongJob = otherHistory with
        {
            Intent = otherHistory.Intent with { RequestedJobId = "23456789-1234-4234-8234-123456789abc" }
        };
        var withoutJob = PlaceOnly(history);
        var otherWithoutJob = PlaceOnly(otherHistory);
        var otherPlace = otherWithoutJob with { Intent = otherWithoutJob.Intent with { PlaceId = 123456 } };
        var profileDefault = withoutJob with { Intent = withoutJob.Intent with { HasExplicitDestination = false } };
        var withProcess = history with { ProcessReceipt = new(42, history.UpdatedUtc.UtcTicks, history.UpdatedUtc) };
        var placeOnlyWithProcess = PlaceOnly(withProcess);
        await using var queue = Queue(new Store(), new Provider());
        Assert.Throws<ArgumentException>(() => queue.CaptureBatchRetryReview([(history, other, Binding())]));
        Assert.Throws<ArgumentException>(() => queue.CaptureBatchRetryReview([(history, account, Binding(2))]));
        Assert.Throws<ArgumentException>(() => queue.CaptureBatchRetryReview([(history, account, Binding()), (wrongJob, other, Binding(2))]));
        // A place-only request and an exact server, or two different places, are two destinations.
        Assert.Throws<ArgumentException>(() => queue.CaptureBatchRetryReview([(history, account, Binding()), (otherWithoutJob, other, Binding(2))]));
        Assert.Throws<ArgumentException>(() => queue.CaptureBatchRetryReview([(withoutJob, account, Binding()), (otherPlace, other, Binding(2))]));
        // A place taken from the profile's preference was never a reviewed destination.
        Assert.Throws<ArgumentException>(() => queue.CaptureBatchRetryReview([(profileDefault, account, Binding())]));
        Assert.Throws<ArgumentException>(() => queue.CaptureBatchRetryReview([(withProcess, account, Binding())]));
        Assert.Throws<ArgumentException>(() => queue.CaptureBatchRetryReview([(placeOnlyWithProcess, account, Binding())]));
    }

    [Fact]
    public async Task RetryRechecksRecordedHistoryAndReportsStaleRowsWithoutPreventingOtherRetries()
    {
        var first = Profile();
        var second = Profile();
        var third = Profile();
        var old = Rejected(first);
        var valid = Rejected(second, Binding(2));
        var invented = Rejected(third, Binding(3));
        var store = new Store(seed: new(true, [old, valid]));
        await using var queue = Queue(store, new Provider());
        await queue.InitializeAsync(default);
        var review = queue.CaptureBatchRetryReview([(old, first, Binding()), (valid, second, Binding(2)), (invented, third, Binding(3))]);
        var competing = queue.CaptureBatchRetryReview([(old, first, Binding())]);
        var altered = old with { Intent = old.Intent with { PlaceId = 123456 } };
        var forged = queue.CaptureBatchRetryReview([(altered, first, Binding())]);
        var rejected = Assert.Single(await queue.EnqueueBatchAsync(forged));
        Assert.Equal(LaunchBatchDisposition.RetryNoLongerEligible, rejected.Disposition);
        Assert.Null(rejected.Operation);
        var next = Assert.Single(await queue.EnqueueBatchAsync(competing)).Operation!;
        await queue.CancelAsync(next.Intent.OperationId, default);
        int writes = store.Writes.Count;
        var rows = await queue.EnqueueBatchAsync(review);
        Assert.Equal(new[] { LaunchBatchDisposition.RetryNoLongerEligible, LaunchBatchDisposition.Recorded, LaunchBatchDisposition.RetryNoLongerEligible },
            rows.Select(row => row.Disposition));
        Assert.Null(rows[0].Operation);
        Assert.Null(rows[2].Operation);
        Assert.Equal(writes + 1, store.Writes.Count);
        await Assert.ThrowsAsync<LaunchQueueException>(() => queue.EnqueueBatchAsync(review));
    }

    [Fact]
    public async Task RetryCannotDispatchWithStaleAuthorizationOrAnUnverifiedProvider()
    {
        var profile = Profile();
        var prior = Rejected(profile);
        var provider = new Provider();
        var store = new Store(seed: new(true, [prior]));
        await using var queue = Queue(store, provider, (_, _) => Task.FromResult(false));
        await queue.InitializeAsync(default);
        await queue.EnqueueBatchAsync(queue.CaptureBatchRetryReview([(prior, profile, Binding())]));
        Assert.True(await queue.TryResumeAsync(default));
        using var timeout = new CancellationTokenSource(Deadline);
        while (store.Last.Operations[^1].State != LaunchState.Failed)
            await Task.Delay(10, timeout.Token);
        Assert.Equal(LaunchProblem.ProfileChanged, store.Last.Operations[^1].Problem);
        Assert.Empty(provider.Calls);

        var unavailable = new Provider(CapabilityState.Unverified);
        var other = new Store(seed: new(true, [prior]));
        await using var gated = Queue(other, unavailable);
        await gated.InitializeAsync(default);
        var row = Assert.Single(await gated.EnqueueBatchAsync(gated.CaptureBatchRetryReview([(prior, profile, Binding())])));
        Assert.Equal(LaunchProblem.ProviderUnavailable, row.Operation?.Problem);
        Assert.Empty(unavailable.Calls);
    }

    [Fact]
    public async Task RetryCapturedBeforePanicRequiresAReviewAfterIt()
    {
        var profile = Profile();
        var prior = Rejected(profile);
        var store = new Store(seed: new(true, [prior]));
        await using var queue = Queue(store, new Provider());
        await queue.InitializeAsync(default);
        var review = queue.CaptureBatchRetryReview([(prior, profile, Binding())]);
        queue.SignalPanic();
        await Assert.ThrowsAsync<LaunchQueueException>(() => queue.EnqueueBatchAsync(review));
        Assert.Equal(prior, Assert.Single(store.Last.Operations));
    }

    [Fact]
    public async Task ReopenedQueueKeepsTheNewerRequestAheadOfItsRejectedPredecessor()
    {
        string root = Path.Combine(Path.GetTempPath(), "ZenithLaunchRetry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var profile = Profile();
            var prior = Rejected(profile);
            using (var disk = new LaunchQueueStore(root))
            {
                await disk.SaveAsync(new(true, [prior]), default);
                await using var queue = Queue(disk, new Provider());
                await queue.InitializeAsync(default);
                var retry = Assert.Single(await queue.EnqueueBatchAsync(queue.CaptureBatchRetryReview([(prior, profile, Binding())])));
                await queue.CancelAsync(retry.Operation!.Intent.OperationId, default);
            }
            using var reopened = new LaunchQueueStore(root);
            var provider = new Provider();
            await using var next = Queue(reopened, provider);
            await next.InitializeAsync(default);
            var loaded = await reopened.LoadAsync(default);
            Assert.Equal(prior, loaded.Operations[0]);
            Assert.Equal(LaunchState.Cancelled, loaded.Operations[1].State);
            var row = Assert.Single(await next.EnqueueBatchAsync(next.CaptureBatchRetryReview([(prior, profile, Binding())])));
            Assert.Equal(LaunchBatchDisposition.RetryNoLongerEligible, row.Disposition);
            Assert.Null(row.Operation);
            Assert.Equal(2, (await reopened.LoadAsync(default)).Operations.Count);
            Assert.Empty(provider.Calls);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ASpreadReviewFreezesItsOwnServerForEachAccountAndThePlaceForTheRest()
    {
        var store = new Store();
        var provider = new Provider();
        await using var queue = Queue(store, provider);
        await queue.InitializeAsync(default);
        var now = DateTimeOffset.UtcNow;
        string[] jobs = ["21111111-2222-4333-8444-000000000001", "21111111-2222-4333-8444-000000000002", "21111111-2222-4333-8444-000000000003"];
        var servers = new List<RobloxPublicServer>
        {
            new(Place, jobs[0], 3, 10, null, null, now), new(Place, jobs[1], 9, 10, null, null, now), new(Place, jobs[2], 0, 10, null, null, now),
        };
        var (first, second, third) = (Profile(), Profile(), Profile());
        // The repeated account is one recipient, so it takes one server, not two.
        var review = queue.CaptureSpreadBatchReview(Place, servers,
            [(first, Binding()), (second, Binding(2)), (first, Binding()), (third, Binding(3))], new Random(11));
        servers.Clear(); // Captured, not read again when the review is submitted.
        Assert.Equal(3, review.Recipients.Count);
        var spread = Assert.IsType<ServerSpread>(review.Spread);
        Assert.Equal(2, spread.SpreadCount); Assert.Equal(1, spread.PlaceOnlyCount);
        Assert.Equal(Place, review.Destination.PlaceId); Assert.Null(review.Destination.JobId);
        Assert.Equal(spread.Destinations, review.Recipients.Select(member => member.Destination));

        int before = store.Writes.Count;
        var rows = await queue.EnqueueBatchAsync(review);
        Assert.Equal(before + 1, store.Writes.Count);
        Assert.Equal(new[] { first.Id, second.Id, third.Id }, rows.Select(row => row.ProfileId));
        var intents = rows.Select(row => Assert.IsType<LaunchOperation>(row.Operation).Intent).ToArray();
        Assert.All(intents, intent => { Assert.Equal(Place, intent.PlaceId); Assert.True(intent.HasExplicitDestination); });
        Assert.Equal(spread.Destinations.Select(destination => destination.JobId), intents.Select(intent => intent.RequestedJobId));
        var pinned = intents.Where(intent => intent.RequestedJobId is not null).ToArray();
        Assert.Equal(2, pinned.Select(intent => intent.RequestedJobId).Distinct().Count());
        Assert.All(pinned, intent => { Assert.Contains(intent.RequestedJobId, new[] { jobs[0], jobs[2] }); Assert.Equal(RobloxServerKind.Public, intent.RequestedServerKind); });
        Assert.Equal(RobloxServerKind.Unknown, Assert.Single(intents, intent => intent.RequestedJobId is null).RequestedServerKind);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task AnOrdinaryReviewFreezesTheOneDestinationForEveryAccount()
    {
        await using var queue = Queue(new Store(), new Provider());
        await queue.InitializeAsync(default);
        var review = queue.CaptureBatchReview(Target, [(Profile(), Binding()), (Profile(), Binding(2))]);
        Assert.Null(review.Spread);
        Assert.All(review.Recipients, member => Assert.Same(Target, member.Destination));
        Assert.Throws<ArgumentNullException>(() => queue.CaptureSpreadBatchReview(Place, null!, [(Profile(), Binding())], new Random(1)));
        Assert.Throws<ArgumentNullException>(() => queue.CaptureSpreadBatchReview(Place, [], [(Profile(), Binding())], null!));
    }

    private static LaunchOperation Rejected(AccountProfile profile, LaunchAccountBinding? binding = null)
    {
        binding ??= Binding();
        var now = DateTimeOffset.UtcNow;
        return new(new(Guid.NewGuid(), profile.Id, profile.Revision, binding.UserId, Place, profile.Priority, now,
            binding.AuthorizationGeneration, Job, RobloxServerKind.Public, true), LaunchState.Failed, LaunchProblem.StartupRejected, now);
    }

    private static LaunchOperation PlaceOnly(LaunchOperation operation)
        => operation with { Intent = operation.Intent with { RequestedJobId = null, RequestedServerKind = RobloxServerKind.Unknown } };

    private sealed class Provider(CapabilityState capability = CapabilityState.Supported,
        Func<LaunchIntent, ProviderLaunchResult>? result = null) : ILaunchProvider
    {
        public ProviderCapability AccountLaunch { get; } = new("Synthetic", capability, "Fixture only");
        public ConcurrentQueue<LaunchIntent> Calls { get; } = new();
        public Task<ProviderLaunchResult> LaunchAsync(LaunchIntent intent, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Enqueue(intent);
            return Task.FromResult(result?.Invoke(intent) ?? new ProviderLaunchResult(ProviderLaunchStatus.RejectedBeforeStart));
        }
        public Task<bool> ConfirmArrivalAsync(LaunchIntent intent, ProviderLaunchResult observed, CancellationToken ct)
            => Task.FromResult(observed.Status == ProviderLaunchStatus.VerifiedArrival);
    }

    private sealed class Store(ILaunchQueueStore? disk = null, LaunchQueueSnapshot? seed = null) : ILaunchQueueStore
    {
        private LaunchQueueSnapshot _last = seed ?? LaunchQueueSnapshot.Empty;
        private WriteGate? _gate;
        public LaunchQueueSnapshot Last => Volatile.Read(ref _last);
        public ConcurrentQueue<LaunchQueueSnapshot> Writes { get; } = new();
        public bool FailWrites { get; set; }
        public WriteGate HoldNextWrite()
        {
            var gate = new WriteGate();
            Assert.Null(Interlocked.Exchange(ref _gate, gate));
            return gate;
        }
        public Task<LaunchQueueSnapshot> LoadAsync(CancellationToken ct) => disk?.LoadAsync(ct) ?? Task.FromResult(Last);
        public async Task SaveAsync(LaunchQueueSnapshot snapshot, CancellationToken ct)
        {
            if (FailWrites) throw new IOException("Synthetic queue store failure.");
            if (Interlocked.Exchange(ref _gate, null) is { } gate)
            {
                gate.Entered.TrySetResult(snapshot);
                await gate.Release.Task.WaitAsync(Deadline);
            }
            if (disk is not null) await disk.SaveAsync(snapshot, ct);
            Volatile.Write(ref _last, snapshot);
            Writes.Enqueue(snapshot);
        }
    }

    private sealed class WriteGate
    {
        public TaskCompletionSource<LaunchQueueSnapshot> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
