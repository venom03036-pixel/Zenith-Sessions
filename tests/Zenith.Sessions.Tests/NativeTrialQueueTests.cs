using System.Collections.Concurrent;
using System.IO;
using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// In-memory consent, queue and callback mechanics only. No browser, OS inventory, credentials
// or native process is used; every receipt and claimed observation below is synthetic.
public sealed partial class NativeTrialQueueTests
{
    private static AccountProfile Profile(string name = "Synthetic trial") => AccountProfile.Create(name)
        with { Revision = 5, PreferredPlaceId = 1818 };
    private static readonly LaunchAccountBinding Binding = new(123, 7);
    private static readonly RobloxRequestedDestination Destination = new(1818);
    private static LaunchCoordinator Queue(Store store, ILaunchProvider provider,
        Func<IReadOnlyList<LaunchOperation>, IReadOnlySet<Guid>>? observed = null,
        Func<IReadOnlyList<LaunchOperation>, LaunchAdmission>? admission = null) => new(store, provider,
            (_, _) => Task.FromResult(true), admission ?? (_ => new(true, "Synthetic capacity")),
            startupDeadline: TimeSpan.FromSeconds(2), stabilization: TimeSpan.Zero, stagger: TimeSpan.Zero,
            preparationDeadline: TimeSpan.FromSeconds(2), observedReservations: observed);

    [Fact]
    public async Task OrdinaryUnverifiedRequestsRemainBlockedAndCannotBorrowAnotherOperationsTrialConsent()
    {
        var store = new Store(); var provider = new Provider();
        await using var queue = Queue(store, provider); await queue.InitializeAsync(default);
        var ordinary = await queue.EnqueueAsync(Profile("Ordinary"), default, Binding, Destination);
        var trial = await queue.EnqueueTrialAsync(Profile("Explicit trial"), default, Binding, Destination);
        Assert.Equal(LaunchState.Blocked, ordinary.State); Assert.Equal(LaunchProblem.ProviderUnavailable, ordinary.Problem);
        Assert.False(queue.IsTrialOperation(ordinary.Intent.OperationId)); Assert.True(queue.IsTrialOperation(trial.Intent.OperationId));
        Assert.False(await queue.ResumeTrialAsync(ordinary.Intent.OperationId, default));
        Assert.False(await queue.ResumeTrialAsync(Guid.NewGuid(), default));
        Assert.True(await queue.ResumeTrialAsync(trial.Intent.OperationId, default));
        await Until(() => store.Row(trial.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified);
        Assert.Equal(1, provider.Preparations); Assert.Equal(1, provider.Starts); Assert.Equal(0, provider.LegacyCalls);
        Assert.Equal(ordinary, store.Row(ordinary.Intent.OperationId));
        Assert.Equal(trial.Intent.OperationId, Assert.Single(provider.Contexts).OriginalIntent.OperationId);
        Assert.True(store.Row(trial.Intent.OperationId)!.Intent.HasExplicitDestination);
        Assert.Equal(CapabilityState.Unverified, provider.AccountLaunch.State);
    }

    [Theory]
    [InlineData("missing-binding")]
    [InlineData("zero-user")]
    [InlineData("zero-generation")]
    [InlineData("missing-destination")]
    [InlineData("not-preparing")]
    [InlineData("supported")]
    [InlineData("unavailable")]
    public async Task ExplicitTrialRequiresItsExactProviderBindingAndDestinationPreconditions(string invalid)
    {
        var store = new Store();
        ILaunchProvider provider = invalid == "not-preparing" ? new LegacyProvider() : new Provider(
            invalid == "supported" ? CapabilityState.Supported : invalid == "unavailable" ? CapabilityState.Unavailable : CapabilityState.Unverified);
        await using var queue = Queue(store, provider); await queue.InitializeAsync(default);
        var binding = invalid switch { "missing-binding" => null, "zero-user" => new LaunchAccountBinding(0, 7),
            "zero-generation" => new LaunchAccountBinding(123, 0), _ => Binding };
        if (invalid == "supported")
        {
            // Direct saved-account launching is now supported without a historical trial; an optional diagnostic
            // trial still owns exact per-request consent and cannot borrow a general queue resume.
            var request = await queue.EnqueueTrialAsync(Profile(), default, binding!, Destination);
            Assert.True(queue.IsTrialOperation(request.Intent.OperationId));
            Assert.True(await queue.ResumeTrialAsync(request.Intent.OperationId, default));
            await Until(() => store.Row(request.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified);
            Assert.False(((Provider)provider).Contexts.Single().RequiresVerifiedExecutable);
            Assert.Single(store.Last.Operations); return;
        }
        await Assert.ThrowsAsync<LaunchQueueException>(() => queue.EnqueueTrialAsync(Profile(), default, binding!,
            invalid == "missing-destination" ? null! : Destination));
        Assert.Empty(store.Last.Operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseAndPanicRevokeConsentAndResumeCannotRestoreIt(bool panic)
    {
        var store = new Store(); var provider = new Provider();
        await using var queue = Queue(store, provider); await queue.InitializeAsync(default);
        var request = await queue.EnqueueTrialAsync(Profile(), default, Binding, Destination);
        Assert.True(queue.IsTrialOperation(request.Intent.OperationId));
        if (panic) queue.SignalPanic(); else await queue.SetPausedAsync(true, default);
        Assert.False(queue.IsTrialOperation(request.Intent.OperationId));
        Assert.False(await queue.ResumeTrialAsync(request.Intent.OperationId, default));
        await queue.SetPausedAsync(false, default);
        await Until(() => store.Row(request.Intent.OperationId)?.Problem == LaunchProblem.ProviderUnavailable);
        Assert.Equal(0, provider.Preparations); Assert.Equal(0, provider.Starts);
        Assert.False(queue.IsTrialOperation(request.Intent.OperationId));
    }

    [Fact]
    public async Task PanicBetweenTrialResumeCheckAndCommitKeepsTheQueuePausedAndPersistsTheStop()
    {
        var store = new Store(); var provider = new Provider();
        await using var queue = Queue(store, provider); await queue.InitializeAsync(default);
        var request = await queue.EnqueueTrialAsync(Profile(), default, Binding, Destination);
        var writes = new ConcurrentQueue<LaunchQueueSnapshot>();
        store.BeforeSave = snapshot => { writes.Enqueue(snapshot); return Task.CompletedTask; };
        // Panic never waits for the queue lock, so it can land after the consent check and before the resume commits.
        queue.TrialResumeCheckedForTest = queue.SignalPanic;
        Assert.False(await queue.ResumeTrialAsync(request.Intent.OperationId, default));
        queue.TrialResumeCheckedForTest = null;
        // Panic's own wake saves the stop; no write may record the queue as running.
        await Until(() => !writes.IsEmpty);
        Assert.All(writes, snapshot => Assert.True(snapshot.Paused, "A trial resume overwrote Panic's saved pause."));
        Assert.False(queue.IsTrialOperation(request.Intent.OperationId));
        Assert.Equal(LaunchState.Queued, store.Row(request.Intent.OperationId)!.State);
        Assert.Equal(LaunchProblem.None, store.Row(request.Intent.OperationId)!.Problem);
        // The in-memory fence holds too: a fresh trial is saved behind the pause and is not prepared.
        var second = await queue.EnqueueTrialAsync(Profile("Second trial"), default, Binding, Destination);
        Assert.True(writes.Last(snapshot => snapshot.Operations.Any(row => row.Intent.OperationId == second.Intent.OperationId)).Paused,
            "The queue stayed running in memory after Panic.");
        Assert.Equal(0, provider.Preparations); Assert.Equal(0, provider.Starts);
        Assert.True(store.Last.Paused);
    }

    [Fact]
    public async Task RestartRestoresOnlyReviewDataAndNoTrialConsent()
    {
        var store = new Store(); var firstProvider = new Provider(); LaunchOperation request;
        await using (var first = Queue(store, firstProvider))
        {
            await first.InitializeAsync(default);
            request = await first.EnqueueTrialAsync(Profile(), default, Binding, Destination);
            Assert.True(first.IsTrialOperation(request.Intent.OperationId));
        }
        var secondProvider = new Provider(); await using var reopened = Queue(store, secondProvider);
        await reopened.InitializeAsync(default); Assert.False(reopened.IsTrialOperation(request.Intent.OperationId));
        Assert.Equal(LaunchState.Blocked, store.Row(request.Intent.OperationId)!.State);
        Assert.Equal(LaunchProblem.Interrupted, store.Row(request.Intent.OperationId)!.Problem);
        Assert.False(await reopened.ResumeTrialAsync(request.Intent.OperationId, default));
        await reopened.SetPausedAsync(false, default); reopened.Signal();
        Assert.Equal(0, firstProvider.Starts); Assert.Equal(0, secondProvider.Preparations);
        Assert.Equal(LaunchState.Blocked, store.Row(request.Intent.OperationId)!.State);
        Assert.Equal(LaunchProblem.Interrupted, store.Row(request.Intent.OperationId)!.Problem);
    }

    [Fact]
    public async Task RestartKeepsNeverDispatchedRequestsCancellableNonReservingAndDoesNotStallAFreshTrial()
    {
        // Queued and Blocked are always saved before any preparation or provider call, so a
        // saved row still in either state proves no client was started for it.
        var store = new Store(); LaunchOperation ordinary, oldTrial;
        await using (var first = Queue(store, new Provider()))
        {
            await first.InitializeAsync(default);
            ordinary = await first.EnqueueAsync(Profile("Ordinary"), default, Binding, Destination);
            oldTrial = await first.EnqueueTrialAsync(Profile("Old trial"), default, Binding, Destination);
            Assert.Equal(LaunchState.Blocked, ordinary.State); Assert.Equal(LaunchState.Queued, oldTrial.State);
        }
        var provider = new Provider(); await using var reopened = Queue(store, provider);
        await reopened.InitializeAsync(default); Assert.True(store.Last.Paused);
        foreach (var id in new[] { ordinary.Intent.OperationId, oldTrial.Intent.OperationId })
        {
            var recovered = store.Row(id)!;
            Assert.Equal(LaunchState.Blocked, recovered.State); Assert.Equal(LaunchProblem.Interrupted, recovered.Problem);
            Assert.False(LaunchDescriptions.HasUnresolvedReservation(recovered)); Assert.NotNull(recovered.ResourceBudget);
        }
        await reopened.CancelAsync(ordinary.Intent.OperationId, default);
        Assert.Equal(LaunchState.Cancelled, store.Row(ordinary.Intent.OperationId)!.State);
        Assert.False(await reopened.ResumeTrialAsync(oldTrial.Intent.OperationId, default));
        var fresh = await reopened.EnqueueTrialAsync(Profile("Fresh"), default, Binding, Destination);
        Assert.True(await reopened.ResumeTrialAsync(fresh.Intent.OperationId, default));
        await Until(() => store.Row(fresh.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified);
        Assert.Equal(1, provider.Starts); Assert.Equal(1, provider.Preparations);
        Assert.Equal(fresh.Intent.OperationId, Assert.Single(provider.Contexts).OriginalIntent.OperationId);
        var untouched = store.Row(oldTrial.Intent.OperationId)!;
        Assert.Equal(LaunchState.Blocked, untouched.State); Assert.Equal(LaunchProblem.Interrupted, untouched.Problem);
    }

    [Fact]
    public async Task PanicDuringTrialPersistenceCannotPublishReusableConsentOrDispatchTheLateCommit()
    {
        var store = new Store(); var provider = new Provider(); var entered = Gate(); var release = Gate();
        store.BeforeSave = async snapshot =>
        {
            if (snapshot.Operations.Any(row => row.State == LaunchState.Queued))
            { entered.TrySetResult(); await release.Task; }
        };
        await using var queue = Queue(store, provider); await queue.InitializeAsync(default); await queue.SetPausedAsync(false, default);
        var enqueuing = queue.EnqueueTrialAsync(Profile(), default, Binding, Destination);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); queue.SignalPanic(); }
        finally { release.TrySetResult(); }
        var request = await enqueuing.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(LaunchState.Cancelled, request.State); Assert.Equal(request, store.Row(request.Intent.OperationId));
        Assert.True(store.Last.Paused); Assert.False(queue.IsTrialOperation(request.Intent.OperationId));
        await queue.SetPausedAsync(false, default); queue.Signal();
        Assert.Equal(0, provider.Preparations); Assert.Equal(0, provider.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveCleanupWaitOwnsTheLateCancelledHandleUntilCleanupFinishesBeforeAFreshTrial(bool panic)
    {
        var store = new Store(); var provider = new Provider(); var profile = Profile("Cancelled then explicitly retried");
        var preparing = Gate(); var releasePreparation = Gate(); var cleaning = Gate(); var releaseCleanup = Gate();
        Guid oldOperation = Guid.Empty;
        provider.BeforePrepare = async context =>
        {
            if (context.OriginalIntent.OperationId != oldOperation) return;
            preparing.TrySetResult(); await releasePreparation.Task;
        };
        provider.BeforeCleanup = async context =>
        {
            if (context.OriginalIntent.OperationId != oldOperation) return;
            cleaning.TrySetResult(); await releaseCleanup.Task;
        };
        await using var queue = Queue(store, provider); await queue.InitializeAsync(default);
        var old = await queue.EnqueueTrialAsync(profile, default, Binding, Destination); oldOperation = old.Intent.OperationId;
        Assert.True(await queue.ResumeTrialAsync(oldOperation, default)); Task? waiting = null;
        try
        {
            await preparing.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (panic) queue.SignalPanic(); else await queue.CancelAsync(oldOperation, default);
            await Until(() => store.Row(oldOperation)?.State == LaunchState.Cancelled);
            waiting = queue.WaitForActiveCleanupAsync(default);
            Assert.False(waiting.IsCompleted); Assert.Equal(0, provider.Starts);
            releasePreparation.TrySetResult(); await cleaning.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(waiting.IsCompleted); Assert.False(Assert.Single(provider.Handles).Disposed);
            var fresh = await queue.EnqueueTrialAsync(profile, default, Binding, Destination);
            Assert.NotEqual(oldOperation, fresh.Intent.OperationId); Assert.True(queue.IsTrialOperation(fresh.Intent.OperationId));
            Assert.True(await queue.ResumeTrialAsync(fresh.Intent.OperationId, default));
            Assert.Equal(1, provider.Preparations); Assert.False(waiting.IsCompleted);
            releaseCleanup.TrySetResult(); await waiting.WaitAsync(TimeSpan.FromSeconds(3));
            await Until(() => store.Row(fresh.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified);
            Assert.Equal(1, provider.Starts); Assert.Equal(2, provider.Preparations);
            Assert.Equal(LaunchState.Cancelled, store.Row(oldOperation)!.State); Assert.Null(store.Row(oldOperation)!.ProcessReceipt);
            Assert.True(provider.Handles.Single(handle => handle.DispatchIntent.OperationId == oldOperation).Disposed);
            Assert.All(provider.StartedOperations, operation => Assert.Equal(fresh.Intent.OperationId, operation));
        }
        finally
        {
            releasePreparation.TrySetResult(); releaseCleanup.TrySetResult();
            if (waiting is not null) await waiting.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("empty")]
    [InlineData("foreign")]
    [InlineData("null")]
    [InlineData("throw")]
    public async Task ASecondUnverifiedClientRequiresThePriorExactSafeObservation(string observation)
    {
        var store = new Store(); var provider = new Provider(); var secondProfile = Profile("Second trial"); var examined = Gate();
        IReadOnlySet<Guid> Observed(IReadOnlyList<LaunchOperation> rows)
        {
            if (rows.Any(row => row.Intent.ProfileId == secondProfile.Id)) examined.TrySetResult();
            return observation switch
            {
                "exact" => rows.Where(row => row.State == LaunchState.ClientStartedUnverified).Select(row => row.Intent.OperationId).ToHashSet(),
                "foreign" => new HashSet<Guid> { Guid.NewGuid() }, "null" => null!,
                "throw" => throw new IOException("Synthetic observation failure"), _ => new HashSet<Guid>()
            };
        }
        await using var queue = Queue(store, provider, Observed); await queue.InitializeAsync(default);
        var first = await queue.EnqueueTrialAsync(Profile("First trial"), default, Binding, Destination);
        await queue.SetPausedAsync(false, default);
        await Until(() => store.Row(first.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified);
        var second = await queue.EnqueueTrialAsync(secondProfile, default, Binding, Destination);
        await examined.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (observation == "exact")
        {
            await Until(() => store.Row(second.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified);
            Assert.Equal(2, provider.Starts);
        }
        else
        {
            Assert.Equal(1, provider.Starts); Assert.Equal(1, provider.Preparations);
            Assert.Equal(LaunchState.Queued, store.Row(second.Intent.OperationId)!.State);
        }
        Assert.Equal(LaunchState.ClientStartedUnverified, store.Row(first.Intent.OperationId)!.State);
        Assert.True(LaunchDescriptions.HasUnresolvedReservation(store.Row(first.Intent.OperationId)!));
        Assert.DoesNotContain(store.Last.Operations, row => row.State == LaunchState.Arrived);
    }

    [Fact]
    public async Task CurrentNotYetEmittedReservationIsExcludedWhileOtherForecastsRemainAtFinalAndBoundaryAdmission()
    {
        var store = new Store(); var provider = new Provider(); var admissions = new ConcurrentQueue<LaunchOperation[]>();
        LaunchAdmission Admit(IReadOnlyList<LaunchOperation> rows)
        {
            admissions.Enqueue(rows.ToArray());
            return new(rows.Count(LaunchDescriptions.HasUnresolvedReservation) < 2, "Synthetic two-client budget");
        }
        await using var queue = Queue(store, provider, rows => rows.Where(row => row.State == LaunchState.ClientStartedUnverified)
            .Select(row => row.Intent.OperationId).ToHashSet(), Admit);
        await queue.InitializeAsync(default);
        var first = await queue.EnqueueTrialAsync(Profile("First"), default, Binding, Destination);
        await queue.SetPausedAsync(false, default);
        await Until(() => store.Row(first.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified);
        var second = await queue.EnqueueTrialAsync(Profile("Second"), default, Binding, Destination);
        await Until(() => store.Row(second.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified);
        Assert.Equal(2, provider.Starts);
        var secondHandle = provider.Handles.Single(handle => handle.DispatchIntent.OperationId == second.Intent.OperationId);
        Assert.True(secondHandle.BoundaryAdmitted);
        Assert.Contains(admissions, rows => rows.Any(row => row.Intent.OperationId == first.Intent.OperationId
            && row.State == LaunchState.ClientStartedUnverified) && rows.All(row => row.Intent.OperationId != second.Intent.OperationId));
        Assert.DoesNotContain(admissions, rows => rows.Any(row => row.Intent.OperationId == second.Intent.OperationId
            && row.State == LaunchState.Launching));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LosingThePriorSafeObservationDuringPreparationOrAfterReservationPreventsTheNextStart(bool afterReservation)
    {
        var store = new Store(); var provider = new Provider(); var entered = Gate(); var release = Gate(); int safe = 1;
        var secondProfile = Profile("Second");
        IReadOnlySet<Guid> Observed(IReadOnlyList<LaunchOperation> rows) => Volatile.Read(ref safe) == 1
            ? rows.Where(row => row.State == LaunchState.ClientStartedUnverified).Select(row => row.Intent.OperationId).ToHashSet()
            : new HashSet<Guid>();
        provider.BeforePrepare = async context =>
        {
            if (context.OriginalIntent.ProfileId != secondProfile.Id || afterReservation) return;
            entered.TrySetResult(); await release.Task;
        };
        store.BeforeSave = snapshot =>
        {
            if (afterReservation && snapshot.Operations.Any(row => row.Intent.ProfileId == secondProfile.Id && row.State == LaunchState.Launching))
                Volatile.Write(ref safe, 0);
            return Task.CompletedTask;
        };
        await using var queue = Queue(store, provider, Observed); await queue.InitializeAsync(default);
        var first = await queue.EnqueueTrialAsync(Profile("First"), default, Binding, Destination);
        await queue.SetPausedAsync(false, default);
        await Until(() => store.Row(first.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified);
        var second = await queue.EnqueueTrialAsync(secondProfile, default, Binding, Destination);
        try
        {
            if (!afterReservation) { await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); Volatile.Write(ref safe, 0); }
        }
        finally { release.TrySetResult(); }
        await Until(() => provider.Handles.Any(handle => handle.DispatchIntent.OperationId == second.Intent.OperationId && handle.Disposed));
        Assert.Equal(1, provider.Starts);
        Assert.Equal(afterReservation ? LaunchState.Failed : LaunchState.Blocked, store.Row(second.Intent.OperationId)!.State);
        Assert.Equal(LaunchState.ClientStartedUnverified, store.Row(first.Intent.OperationId)!.State);
    }

    [Fact]
    public async Task ATrialCannotPromoteEvenALyingProvidersVerifiedArrivalClaim()
    {
        var store = new Store(); var provider = new Provider { ClaimArrival = true };
        await using var queue = Queue(store, provider); await queue.InitializeAsync(default);
        var request = await queue.EnqueueTrialAsync(Profile(), default, Binding, Destination);
        await queue.SetPausedAsync(false, default);
        await Until(() => store.Row(request.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified);
        Assert.Equal(0, provider.Confirmations); Assert.NotNull(store.Row(request.Intent.OperationId)!.ProcessReceipt);
        Assert.Equal(LaunchProblem.ClientStartedUnverified, store.Row(request.Intent.OperationId)!.Problem);
        Assert.DoesNotContain(store.Last.Operations, row => row.State is LaunchState.Arrived or LaunchState.Stabilizing);
        Assert.Equal(CapabilityState.Unverified, provider.AccountLaunch.State);
    }

    [Fact]
    public async Task LegacyCancelledReceiptNeedsExplicitAcknowledgmentBeforeRequeueingItsProfile()
    {
        var profile = Profile(); var old = Historical(profile, LaunchState.Cancelled, LaunchProblem.None);
        var store = new Store { Last = new(true, [old]) }; var provider = new Provider();
        await using var queue = Queue(store, provider); await queue.InitializeAsync(default);
        Assert.True(LaunchDescriptions.HasUnresolvedReservation(store.Row(old.Intent.OperationId)!));
        await Assert.ThrowsAsync<LaunchQueueException>(() => queue.EnqueueTrialAsync(profile, default, Binding, Destination));
        await queue.CancelAsync(old.Intent.OperationId, default);
        Assert.Equal(LaunchProblem.None, store.Row(old.Intent.OperationId)!.Problem);
        await queue.AcknowledgeClientClosedAsync(old.Intent.OperationId, default);
        var acknowledged = store.Row(old.Intent.OperationId)!;
        Assert.Equal(LaunchState.Cancelled, acknowledged.State); Assert.Equal(LaunchProblem.ClientClosureAcknowledged, acknowledged.Problem);
        Assert.Same(old.ProcessReceipt, acknowledged.ProcessReceipt); Assert.False(LaunchDescriptions.HasUnresolvedReservation(acknowledged));
        var fresh = await queue.EnqueueTrialAsync(profile, default, Binding, Destination);
        await queue.SetPausedAsync(false, default);
        await Until(() => store.Row(fresh.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified);
        Assert.Equal(1, provider.Starts); Assert.Equal(acknowledged, store.Row(old.Intent.OperationId));
    }

    [Fact]
    public async Task UnreleasedTerminalReceiptCannotBeTrimmedWithOrdinaryTerminalHistory()
    {
        var retained = Historical(Profile("Retained"), LaunchState.Cancelled, LaunchProblem.None);
        var history = Enumerable.Range(0, 102).Select(index => Historical(Profile("History " + index), LaunchState.Cancelled,
            LaunchProblem.None) with { ProcessReceipt = null }).Prepend(retained).ToArray();
        var store = new Store { Last = new(true, history) }; var provider = new Provider();
        await using var queue = Queue(store, provider); await queue.InitializeAsync(default);
        await queue.EnqueueTrialAsync(Profile("Fresh"), default, Binding, Destination);
        Assert.Equal(retained, store.Row(retained.Intent.OperationId));
        Assert.Equal(100, store.Last.Operations.Count(row => LaunchDescriptions.Terminal(row.State)
            && !LaunchDescriptions.HasUnresolvedReservation(row)));
    }

    [Theory]
    [InlineData(LaunchState.Queued)] [InlineData(LaunchState.Blocked)] [InlineData(LaunchState.Launching)]
    [InlineData(LaunchState.Stabilizing)] [InlineData(LaunchState.Arrived)] [InlineData(LaunchState.Failed)]
    [InlineData(LaunchState.Uncertain)] [InlineData(LaunchState.ReviewRequired)] [InlineData(LaunchState.Validating)]
    [InlineData(LaunchState.ClientStartedUnverified)] [InlineData(LaunchState.Preparing)]
    public void ClosureAcknowledgmentMarkerIsValidOnlyForCancelledState(LaunchState state)
    {
        var operation = Historical(Profile(), state, LaunchProblem.ClientClosureAcknowledged);
        if (state is LaunchState.Queued or LaunchState.Blocked or LaunchState.Preparing or LaunchState.Validating or LaunchState.Failed)
            operation = operation with { ProcessReceipt = null };
        Assert.Throws<InvalidDataException>(() => LaunchCoordinator.ValidateSnapshot(new(true, [operation])));
    }

    private static LaunchOperation Historical(AccountProfile profile, LaunchState state, LaunchProblem problem)
    {
        var now = DateTimeOffset.UtcNow;
        var intent = new LaunchIntent(Guid.NewGuid(), profile.Id, profile.Revision, Binding.UserId, Destination.PlaceId, 0,
            now, Binding.AuthorizationGeneration, HasExplicitDestination: true);
        return new(intent, state, problem, now, new(600001, now.AddMinutes(-1).UtcTicks, now));
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Until(Func<bool> condition)
    { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4)); while (!condition()) await Task.Delay(5, stop.Token); }
    private sealed class Store : ILaunchQueueStore
    {
        private LaunchQueueSnapshot _last = LaunchQueueSnapshot.Empty;
        public LaunchQueueSnapshot Last { get => Volatile.Read(ref _last); set => Volatile.Write(ref _last, value); }
        public Func<LaunchQueueSnapshot, Task>? BeforeSave;
        public LaunchOperation? Row(Guid id) => Last.Operations.SingleOrDefault(row => row.Intent.OperationId == id);
        public Task<LaunchQueueSnapshot> LoadAsync(CancellationToken ct) => Task.FromResult(Last);
        public async Task SaveAsync(LaunchQueueSnapshot snapshot, CancellationToken ct)
        { if (BeforeSave is not null) await BeforeSave(snapshot); Last = snapshot; }
    }
    private sealed class LegacyProvider : ILaunchProvider
    {
        public ProviderCapability AccountLaunch { get; } = new("Synthetic legacy", CapabilityState.Unverified, "Controlled only");
        public Task<ProviderLaunchResult> LaunchAsync(LaunchIntent intent, CancellationToken ct) => throw new InvalidOperationException("Must not dispatch");
        public Task<bool> ConfirmArrivalAsync(LaunchIntent intent, ProviderLaunchResult observed, CancellationToken ct) => Task.FromResult(false);
    }
    private sealed class Provider(CapabilityState capability = CapabilityState.Unverified) : IPreparingLaunchProvider
    {
        private int _preparations, _starts, _legacy, _confirmations;
        public int Preparations => Volatile.Read(ref _preparations); public int Starts => Volatile.Read(ref _starts);
        public int LegacyCalls => Volatile.Read(ref _legacy); public int Confirmations => Volatile.Read(ref _confirmations);
        public bool ClaimArrival; public Func<LaunchPreparationContext, Task>? BeforePrepare, BeforeCleanup;
        public ConcurrentQueue<LaunchPreparationContext> Contexts = []; public ConcurrentQueue<Handle> Handles = [];
        public ConcurrentQueue<Guid> StartedOperations = [];
        public ProviderCapability AccountLaunch { get; } = new("Synthetic preparing", capability, "Controlled only");
        public async Task<LaunchPreparationResult> PrepareAsync(LaunchPreparationContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _preparations); Contexts.Enqueue(context);
            if (BeforePrepare is not null) await BeforePrepare(context);
            var handle = new Handle(this, context); Handles.Enqueue(handle); return new(LaunchPreparationFailure.None, handle);
        }
        public Task<ProviderLaunchResult> LaunchAsync(LaunchIntent intent, CancellationToken ct)
        { Interlocked.Increment(ref _legacy); throw new InvalidOperationException("Prepared trial must not use legacy dispatch"); }
        public Task<bool> ConfirmArrivalAsync(LaunchIntent intent, ProviderLaunchResult observed, CancellationToken ct)
        { Interlocked.Increment(ref _confirmations); return Task.FromResult(true); }
        public sealed class Handle(Provider provider, LaunchPreparationContext context) : IPreparedAccountLaunch
        {
            private int _disposed;
            public LaunchPreparationContext Context => context;
            public LaunchIntent DispatchIntent { get; } = context.OriginalIntent with
                { ProfileRevision = context.OriginalIntent.ProfileRevision + 1, AuthorizationGeneration = context.OriginalIntent.AuthorizationGeneration + 2 };
            public bool IsExpired => false; public bool Disposed => Volatile.Read(ref _disposed) != 0;
            public bool BoundaryAdmitted;
            public async Task<ProviderLaunchResult> DispatchAsync(LaunchDispatchBoundary boundary, CancellationToken ct)
            {
                if (!boundary.IsCurrent || !await boundary.ValidateAsync(DispatchIntent, ct) || !boundary.IsCurrent)
                    return new(ProviderLaunchStatus.RejectedBeforeStart);
                BoundaryAdmitted = boundary.ReadAdmission().Allowed;
                if (!BoundaryAdmitted) return new(ProviderLaunchStatus.RejectedBeforeStart);
                int ordinal = Interlocked.Increment(ref provider._starts);
                provider.StartedOperations.Enqueue(DispatchIntent.OperationId);
                return new(provider.ClaimArrival ? ProviderLaunchStatus.VerifiedArrival : ProviderLaunchStatus.ClientStartedUnverified,
                    provider.ClaimArrival ? DispatchIntent.UserId : null, provider.ClaimArrival ? DispatchIntent.PlaceId : null,
                    700000 + ordinal, DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks,
                    provider.ClaimArrival ? DispatchIntent.RequestedJobId : null);
            }
            public async ValueTask DisposeAsync()
            {
                if (provider.BeforeCleanup is not null) await provider.BeforeCleanup(Context);
                Interlocked.Exchange(ref _disposed, 1);
            }
        }
    }
}
