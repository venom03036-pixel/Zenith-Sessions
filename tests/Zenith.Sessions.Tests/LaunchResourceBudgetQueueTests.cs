using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// Memory queue, local callbacks, synthetic provider receipts only. No process, disk, account,
// browser, resource sampler, network or native launch runs in these coordinator fixtures.
public sealed class LaunchResourceBudgetQueueTests
{
    private const ulong GiB = CapacityPolicy.GiB;
    private const string Secret = "synthetic-resolver-secret-never-reported";
    private static LaunchResourceBudget Budget(ulong physical = 2, ulong commit = 3) => new(physical * GiB, commit * GiB);
    private static AccountProfile Profile(string label, int priority = 0) => AccountProfile.Create(label) with { Revision = 3, PreferredPlaceId = 42, Priority = priority };
    private static readonly LaunchAccountBinding Binding = new(456, 7);
    private static readonly RobloxRequestedDestination Destination = new(42);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static IReadOnlySet<Guid> Fresh(IReadOnlyList<LaunchOperation> rows)
        => rows.Where(row => row.State == LaunchState.ClientStartedUnverified).Select(row => row.Intent.OperationId).ToHashSet();
    private static LaunchCoordinator Queue(Store store, Provider provider, Func<LaunchIntent, LaunchResourceBudget>? resolver = null,
        Func<LaunchOperation, IReadOnlyList<LaunchOperation>, LaunchAdmission>? admission = null,
        Func<LaunchIntent, CancellationToken, Task<bool>>? validate = null,
        Func<IReadOnlyList<LaunchOperation>, LaunchAdmission>? legacyAdmission = null)
        => new(store, provider, validate ?? ((_, _) => Task.FromResult(true)), legacyAdmission ?? (_ => new(true, "Synthetic resources")),
            stabilization: TimeSpan.Zero, stagger: TimeSpan.Zero, observedReservations: Fresh,
            budgetResolver: resolver, candidateAdmission: admission);

    [Theory]
    [InlineData(0UL, 1UL)]
    [InlineData(1UL, 0UL)]
    [InlineData(1099511627777UL, 1UL)]
    [InlineData(1UL, 1099511627777UL)]
    public void ZeroOrExcessiveByteForecastsAreRejected(ulong physical, ulong commit)
    {
        var budget = new LaunchResourceBudget(physical, commit); Assert.False(budget.IsValid);
        Assert.Throws<ArgumentException>(() => budget.Validated());
    }

    [Theory]
    [InlineData(.1, .2)]
    [InlineData(2.5, 3)]
    [InlineData(1024, 1024)]
    public void SettingsConversionUsesExactCeilingBytesWithinTheValidatedBounds(double physical, double commit)
    {
        var budget = LaunchResourceBudget.FromSettings(new(NewClientPhysicalGiB: physical, NewClientCommitGiB: commit));
        Assert.Equal((ulong)Math.Ceiling(physical * GiB), budget.PhysicalBytes);
        Assert.Equal((ulong)Math.Ceiling(commit * GiB), budget.CommitBytes); Assert.True(budget.IsValid); Assert.Same(budget, budget.Validated());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(budget));
        Assert.Equal(new[] { "CommitBytes", "PhysicalBytes" }, json.RootElement.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void InvalidSettingsCannotBecomeAResourceForecast()
    {
        Assert.Throws<ArgumentNullException>(() => LaunchResourceBudget.FromSettings(null!));
        Assert.Throws<ArgumentException>(() => LaunchResourceBudget.FromSettings(new(NewClientPhysicalGiB: double.NaN)));
        Assert.Throws<ArgumentException>(() => LaunchResourceBudget.FromSettings(new(NewClientCommitGiB: 1024.1)));
        Assert.Equal(1024UL * GiB, LaunchResourceBudget.MaximumBytes);
    }

    [Fact]
    public async Task DefaultEnqueuePinsTheProvisionalForecastBeforeItsFirstSave()
    {
        var store = new Store(); var provider = new Provider(); await using var queue = Queue(store, provider); await queue.InitializeAsync(default);
        LaunchOperation? published = null; store.BeforeSave = snapshot =>
        { if (snapshot.Operations.Count != 0) published = snapshot.Operations.Single(); return Task.CompletedTask; };
        var operation = await queue.EnqueueAsync(Profile("Default"), default, Binding);
        Assert.Equal(LaunchResourceBudget.FromSettings(new()), operation.ResourceBudget); Assert.Equal(operation, published);
        Assert.Equal(operation.ResourceBudget, store.Row(operation.Intent.OperationId)!.ResourceBudget); Assert.Empty(provider.Started);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("zero-physical")]
    [InlineData("zero-commit")]
    [InlineData("large-physical")]
    [InlineData("large-commit")]
    [InlineData("throw")]
    public async Task UnavailableResolversDoNotMutateOrPersistTheQueueAndNeverForwardTheirMessages(string failure)
    {
        var store = new Store(); var provider = new Provider(); int calls = 0; bool repaired = false; var faults = new List<Exception>();
        await using var queue = Queue(store, provider, _ =>
        {
            calls++; if (repaired) return Budget();
            return failure switch
            {
                "null" => null!, "zero-physical" => new(0, GiB), "zero-commit" => new(GiB, 0),
                "large-physical" => new(LaunchResourceBudget.MaximumBytes + 1, GiB), "large-commit" => new(GiB, LaunchResourceBudget.MaximumBytes + 1),
                _ => throw new IOException(Secret)
            };
        });
        queue.Faulted += faults.Add; await queue.InitializeAsync(default); int saves = store.Saves;
        var error = await Assert.ThrowsAsync<LaunchQueueException>(() => queue.EnqueueAsync(Profile("Rejected"), default, Binding));
        Assert.DoesNotContain(Secret, error.ToString()); Assert.Null(error.InnerException); Assert.Equal(saves, store.Saves);
        Assert.Empty(store.Last.Operations); Assert.Empty(provider.Started); Assert.Empty(faults); Assert.True(queue.IsHealthy);
        repaired = true; var accepted = await queue.EnqueueAsync(Profile("Fresh"), default, Binding);
        Assert.Equal(Budget(), accepted.ResourceBudget); Assert.Equal(2, calls); Assert.Single(store.Last.Operations);
    }

    [Fact]
    public async Task CancellationRaisedByAResolverRemainsCancellationWithNoCommittedRequest()
    {
        var store = new Store(); var provider = new Provider(); using var stop = new CancellationTokenSource();
        await using var queue = Queue(store, provider, _ => { stop.Cancel(); throw new IOException(Secret); }); await queue.InitializeAsync(default);
        int saves = store.Saves;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.EnqueueAsync(Profile("Cancelled"), stop.Token, Binding));
        Assert.Equal(saves, store.Saves); Assert.Empty(store.Last.Operations); Assert.Empty(provider.Started);
    }

    [Fact]
    public async Task AResolverCannotCommitThroughPanicAndUnavailableProvidersStillPinMetadataBudgets()
    {
        var store = new Store(); var provider = new Provider(); LaunchCoordinator? owner = null;
        await using (var queue = Queue(store, provider, _ => { owner!.SignalPanic(); return Budget(); }))
        {
            owner = queue; await queue.InitializeAsync(default);
            await Assert.ThrowsAsync<LaunchQueueException>(() => queue.EnqueueAsync(Profile("Fenced"), default, Binding));
            Assert.Empty(store.Last.Operations); Assert.Empty(provider.Started);
        }
        var blockedStore = new Store(); var blockedProvider = new Provider { Available = false };
        await using var blocked = Queue(blockedStore, blockedProvider, _ => Budget(8, 9)); await blocked.InitializeAsync(default);
        var row = await blocked.EnqueueAsync(Profile("Unavailable"), default, Binding);
        Assert.Equal(LaunchState.Blocked, row.State); Assert.Equal(LaunchProblem.ProviderUnavailable, row.Problem); Assert.Equal(Budget(8, 9), row.ResourceBudget);
    }

    [Fact]
    public async Task ExplicitTargetAndChangedSettingsOnlyAffectTheNextNewRequest()
    {
        var store = new Store(); var provider = new Provider(); var settings = new CapacitySettings(NewClientPhysicalGiB: 8, NewClientCommitGiB: 9);
        var seen = new List<LaunchIntent>();
        await using var queue = Queue(store, provider, intent => { seen.Add(intent); return LaunchResourceBudget.FromSettings(settings); });
        await queue.InitializeAsync(default); var profile = Profile("Pinned");
        var first = await queue.EnqueueAsync(profile, default, Binding, new(99)); settings = new(NewClientPhysicalGiB: .1, NewClientCommitGiB: .2);
        profile = profile with { PreferredPlaceId = 123, Revision = 4 };
        var second = await queue.EnqueueAsync(Profile("Later"), default, Binding, Destination);
        Assert.Equal(99, seen[0].PlaceId); Assert.Equal(42, seen[1].PlaceId); Assert.Equal(Budget(8, 9), first.ResourceBudget);
        Assert.Equal(first.ResourceBudget, store.Row(first.Intent.OperationId)!.ResourceBudget);
        Assert.Equal(LaunchResourceBudget.FromSettings(settings), second.ResourceBudget); Assert.Equal(2, seen.Count);
        await queue.CancelAsync(first.Intent.OperationId, default); Assert.Equal(first.ResourceBudget, store.Row(first.Intent.OperationId)!.ResourceBudget);
        Assert.Equal(99, store.Row(first.Intent.OperationId)!.Intent.PlaceId); Assert.Empty(provider.Started);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MixedCandidatesUseTheirOwnForecastAndRetainEveryOtherPossibleClientBudget(bool heavyPhysical)
    {
        var store = new Store(); var provider = new Provider(); var heavy = heavyPhysical ? Budget(7, 1) : Budget(1, 7); bool cheaperPolicy = false;
        var calls = new ConcurrentQueue<(LaunchOperation Candidate, LaunchOperation[] Remaining)>();
        await using var queue = Queue(store, provider, intent => cheaperPolicy || intent.PlaceId != 99 ? Budget(1, 1) : heavy,
            (candidate, remaining) =>
            {
                calls.Enqueue((candidate, remaining.ToArray())); Assert.DoesNotContain(remaining, row => row.Intent.OperationId == candidate.Intent.OperationId);
                ulong physical = 0, commit = 0;
                foreach (var row in remaining.Where(LaunchDescriptions.HasUnresolvedReservation))
                { physical += row.ResourceBudget!.PhysicalBytes; commit += row.ResourceBudget.CommitBytes; }
                return new(candidate.ResourceBudget!.PhysicalBytes <= 6 * GiB - physical && candidate.ResourceBudget.CommitBytes <= 6 * GiB - commit, "Synthetic mixed forecast");
            }, legacyAdmission: _ => throw new InvalidOperationException("The candidate-aware policy must be used."));
        await queue.InitializeAsync(default);
        var light = await queue.EnqueueAsync(Profile("Light", 100), default, Binding, Destination);
        var large = await queue.EnqueueAsync(Profile("Large"), default, Binding, new(99)); await queue.SetPausedAsync(false, default);
        await Until(() => store.Row(large.Intent.OperationId)?.State == LaunchState.Blocked); await queue.WaitForActiveCleanupAsync(default);
        Assert.Equal(light.Intent.OperationId, Assert.Single(provider.Started).OperationId); Assert.Equal(heavy, store.Row(large.Intent.OperationId)!.ResourceBudget);
        Assert.Contains(calls, call => call.Candidate.Intent.OperationId == large.Intent.OperationId && call.Remaining.Any(row => row.Intent.OperationId == light.Intent.OperationId && row.ResourceBudget == Budget(1, 1)));
        cheaperPolicy = true; var later = await queue.EnqueueAsync(Profile("New light", 100), default, Binding, new(99)); queue.Signal();
        await Until(() => provider.Started.Count == 2); await queue.WaitForActiveCleanupAsync(default);
        Assert.Equal(Budget(1, 1), later.ResourceBudget); Assert.DoesNotContain(provider.Started, intent => intent.OperationId == large.Intent.OperationId);
        Assert.Equal(heavy, store.Row(large.Intent.OperationId)!.ResourceBudget);
    }

    [Fact]
    public async Task PreflightAndPostValidationUseTheActualCurrentCandidateWithoutDoubleChargingIt()
    {
        var store = new Store(); var provider = new Provider(); var seen = new ConcurrentQueue<LaunchOperation>();
        await using var queue = Queue(store, provider, _ => Budget(6, 6), (candidate, remaining) =>
        {
            seen.Enqueue(candidate); Assert.Empty(remaining); Assert.Equal(Budget(6, 6), candidate.ResourceBudget);
            return new(candidate.ResourceBudget!.PhysicalBytes <= 6 * GiB && candidate.ResourceBudget.CommitBytes <= 6 * GiB, "Exact candidate fit");
        });
        await queue.InitializeAsync(default); var row = await queue.EnqueueAsync(Profile("Exact fit"), default, Binding); await queue.SetPausedAsync(false, default);
        await Until(() => store.Row(row.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified); await queue.WaitForActiveCleanupAsync(default);
        Assert.Equal(new[] { LaunchState.Queued, LaunchState.Validating }, seen.Select(candidate => candidate.State));
        Assert.Single(provider.Started); Assert.Equal(row.ResourceBudget, store.Row(row.Intent.OperationId)!.ResourceBudget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedPreparationAndFinalBoundaryKeepPinnedBudgetAndAdoptedIntent(bool denyAtBoundary)
    {
        var store = new Store(); var provider = new PreparingProvider { PreparationRelease = Gate(), BoundaryRelease = Gate(), AdvanceIdentity = true };
        var policy = Budget(8, 9); bool freshAdmission = true; int resolutions = 0; var seen = new ConcurrentQueue<LaunchOperation>();
        await using var queue = Queue(store, provider, _ => { resolutions++; return policy; }, (candidate, remaining) =>
        {
            seen.Enqueue(candidate); Assert.DoesNotContain(remaining, row => row.Intent.OperationId == candidate.Intent.OperationId);
            return new(freshAdmission, "Synthetic current resources");
        });
        await queue.InitializeAsync(default); var first = await queue.EnqueueTrialAsync(Profile("Prepared"), default, Binding, Destination);
        Assert.True(await queue.ResumeTrialAsync(first.Intent.OperationId, default));
        try
        {
            await provider.PreparationEntered.Task.WaitAsync(TimeSpan.FromSeconds(3)); policy = Budget(1, 1);
            var waiting = store.Row(first.Intent.OperationId)!; Assert.Equal(LaunchState.Preparing, waiting.State);
            Assert.False(LaunchDescriptions.NeedsReservation(waiting.State)); Assert.Equal(Budget(8, 9), waiting.ResourceBudget);
        }
        finally { provider.PreparationRelease.TrySetResult(); }
        try
        {
            await provider.BoundaryEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var adopted = store.Row(first.Intent.OperationId)!; Assert.Equal(LaunchState.Launching, adopted.State);
            Assert.Equal(first.Intent.ProfileRevision + 1, adopted.Intent.ProfileRevision); Assert.Equal(first.Intent.AuthorizationGeneration + 2, adopted.Intent.AuthorizationGeneration);
            Assert.Equal(first.ResourceBudget, adopted.ResourceBudget); freshAdmission = !denyAtBoundary;
        }
        finally { provider.BoundaryRelease.TrySetResult(); }
        await Until(() => store.Row(first.Intent.OperationId)?.State is LaunchState.ClientStartedUnverified or LaunchState.Failed);
        await queue.WaitForActiveCleanupAsync(default);
        var final = store.Row(first.Intent.OperationId)!; Assert.Equal(Budget(8, 9), final.ResourceBudget); Assert.Equal(1, resolutions);
        Assert.Equal(denyAtBoundary ? 0 : 1, provider.Started.Count); Assert.Equal(1, provider.Disposals); Assert.Equal(0, provider.LegacyCalls);
        Assert.Equal(new[] { LaunchState.Queued, LaunchState.Preparing, LaunchState.Launching }, seen.Select(row => row.State));
        Assert.All(seen, row => Assert.Equal(first.ResourceBudget, row.ResourceBudget));
        Assert.Equal(first.Intent, seen.First().Intent); Assert.All(seen.Skip(1), row => Assert.Equal(final.Intent, row.Intent));
        Assert.NotEqual(LaunchState.Arrived, final.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullOrThrowingCandidateAdmissionFailsClosedWithoutRawMessages(bool throws)
    {
        var store = new Store(); var provider = new Provider(); var faults = new List<Exception>();
        await using var queue = Queue(store, provider, _ => Budget(), (_, _) => throws ? throw new IOException(Secret) : null!);
        queue.Faulted += faults.Add; await queue.InitializeAsync(default); var row = await queue.EnqueueAsync(Profile("Held"), default, Binding);
        await queue.SetPausedAsync(false, default); await Until(() => store.Row(row.Intent.OperationId)?.State == LaunchState.Blocked);
        Assert.Empty(provider.Started); Assert.Empty(faults); Assert.True(queue.IsHealthy); Assert.Equal(row.ResourceBudget, store.Row(row.Intent.OperationId)!.ResourceBudget);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(store.Last));
    }

    [Fact]
    public async Task ExistingAdmissionCallbackRemainsSupportedWhenNoCandidatePolicyIsInstalled()
    {
        var store = new Store(); var provider = new Provider(); int calls = 0;
        await using var queue = Queue(store, provider, legacyAdmission: remaining => { calls++; Assert.Empty(remaining); return new(true, "Legacy local admission"); });
        await queue.InitializeAsync(default); var row = await queue.EnqueueAsync(Profile("Legacy callback"), default, Binding); await queue.SetPausedAsync(false, default);
        await Until(() => store.Row(row.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified); await queue.WaitForActiveCleanupAsync(default);
        Assert.Equal(2, calls); Assert.Single(provider.Started); Assert.NotNull(store.Row(row.Intent.OperationId)!.ResourceBudget);
    }

    [Fact]
    public async Task PanicDuringEnqueuePersistenceKeepsThePinnedBudgetOnItsDurableCancelledRow()
    {
        var store = new Store(); var provider = new Provider(); var entered = Gate(); var release = Gate();
        await using var queue = Queue(store, provider, _ => Budget(8, 9)); await queue.InitializeAsync(default);
        store.BeforeSave = async snapshot => { if (snapshot.Operations.Any(row => row.State == LaunchState.Queued)) { entered.TrySetResult(); await release.Task; } };
        var adding = queue.EnqueueAsync(Profile("Interrupted"), default, Binding);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); queue.SignalPanic(); Assert.False(adding.IsCompleted); }
        finally { release.TrySetResult(); }
        var cancelled = await adding.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(LaunchState.Cancelled, cancelled.State);
        Assert.Equal(Budget(8, 9), cancelled.ResourceBudget); Assert.Equal(cancelled, store.Row(cancelled.Intent.OperationId)); Assert.True(store.Last.Paused); Assert.Empty(provider.Started);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateProcessReceiptAfterCancellationRetainsTheOriginalForecastAndUncertainReservation(bool panic)
    {
        var store = new Store(); var provider = new Provider { Reply = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var queue = Queue(store, provider, _ => Budget(8, 9)); await queue.InitializeAsync(default);
        var row = await queue.EnqueueAsync(Profile("Late receipt"), default, Binding); await queue.SetPausedAsync(false, default);
        try
        {
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); if (panic) queue.SignalPanic(); else await queue.CancelAsync(row.Intent.OperationId, default);
            await Until(() => store.Row(row.Intent.OperationId)?.State == LaunchState.Uncertain);
            Assert.Equal(row.ResourceBudget, store.Row(row.Intent.OperationId)!.ResourceBudget);
        }
        finally { provider.Reply.TrySetResult(Provider.Result()); }
        await queue.WaitForActiveCleanupAsync(default); var retained = store.Row(row.Intent.OperationId)!;
        Assert.NotNull(retained.ProcessReceipt); Assert.Equal(LaunchState.Uncertain, retained.State); Assert.Equal(Budget(8, 9), retained.ResourceBudget);
        Assert.True(LaunchDescriptions.HasUnresolvedReservation(retained)); Assert.Single(provider.Started); Assert.Equal(0, provider.Confirmations);
    }

    [Fact]
    public async Task StopAndResumePreserveQueuedCostsEvenWhenTheResolverPolicyChanges()
    {
        var store = new Store(); var provider = new Provider(); var policy = Budget(8, 9); bool healthy = false; int calls = 0;
        await using var queue = Queue(store, provider, _ => { calls++; return policy; }, (_, _) => new(healthy, "Synthetic pressure"));
        await queue.InitializeAsync(default); var row = await queue.EnqueueAsync(Profile("Pinned"), default, Binding); await queue.SetPausedAsync(false, default);
        await Until(() => store.Row(row.Intent.OperationId)?.State == LaunchState.Blocked); queue.SignalPanic(); policy = Budget(1, 1); healthy = true;
        await queue.SetPausedAsync(false, default); await Until(() => store.Row(row.Intent.OperationId)?.State == LaunchState.ClientStartedUnverified);
        await queue.WaitForActiveCleanupAsync(default); Assert.Equal(1, calls); Assert.Equal(Budget(8, 9), store.Row(row.Intent.OperationId)!.ResourceBudget);
    }

    [Fact]
    public async Task RestartRetainsPinnedForecastsWithoutResolvingAgainOrAutomaticallyStartingRecoveredWork()
    {
        var store = new Store(); var provider = new Provider(); LaunchOperation row;
        await using (var queue = Queue(store, provider, _ => Budget(8, 9)))
        { await queue.InitializeAsync(default); row = await queue.EnqueueAsync(Profile("Recovered"), default, Binding); }
        var later = new Provider(); int calls = 0;
        await using var reopened = Queue(store, later, _ => { calls++; throw new IOException(Secret); }); await reopened.InitializeAsync(default);
        Assert.True(store.Last.Paused); Assert.Equal(LaunchState.Blocked, store.Row(row.Intent.OperationId)!.State);
        Assert.Equal(LaunchProblem.Interrupted, store.Row(row.Intent.OperationId)!.Problem);
        Assert.Equal(row.ResourceBudget, store.Row(row.Intent.OperationId)!.ResourceBudget); await reopened.SetPausedAsync(false, default); await Settle();
        Assert.Empty(later.Started); Assert.Equal(0, calls); Assert.Equal(Budget(8, 9), store.Row(row.Intent.OperationId)!.ResourceBudget);
    }

    [Fact]
    public async Task LegacyUnknownForecastsNeverGainReconciliationProofFromPositiveObservedOperationIds()
    {
        var intent = new LaunchIntent(Guid.NewGuid(), Guid.NewGuid(), 1, 456, 42, 0, DateTimeOffset.UtcNow, 7);
        var receipt = new LaunchProcessReceipt(890001, DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks, DateTimeOffset.UtcNow);
        var legacy = new LaunchOperation(intent, LaunchState.ClientStartedUnverified, LaunchProblem.ClientStartedUnverified, receipt.RecordedUtc, receipt);
        var store = new Store(new(false, [legacy])); var provider = new Provider(); int resolutions = 0;
        await using var queue = new LaunchCoordinator(store, provider, (_, _) => Task.FromResult(true), _ => new(true, "Synthetic resources"),
            stabilization: TimeSpan.Zero, stagger: TimeSpan.Zero,
            observedReservations: rows => rows.Select(row => row.Intent.OperationId).ToHashSet(),
            budgetResolver: _ => { resolutions++; return Budget(); }); await queue.InitializeAsync(default);
        Assert.Null(store.Row(intent.OperationId)!.ResourceBudget); Assert.Equal(LaunchState.ReviewRequired, store.Row(intent.OperationId)!.State);
        var fresh = await queue.EnqueueAsync(Profile("Held new request"), default, Binding); await queue.SetPausedAsync(false, default); await Settle();
        Assert.Empty(provider.Started); Assert.Equal(1, resolutions); Assert.Null(store.Row(intent.OperationId)!.ResourceBudget);
        Assert.Equal(LaunchState.Queued, store.Row(fresh.Intent.OperationId)!.State); Assert.NotNull(fresh.ResourceBudget);
    }

    // CAP-14-03: a restart keeps a receipted, budgeted started client as that started client, so
    // the same fresh exact PID/creation-time observation that clears the single-start barrier
    // within a session still clears it afterwards. Absence, or an uncertain start, keeps it held.
    [Theory]
    [InlineData(LaunchState.ClientStartedUnverified, true, true)]
    [InlineData(LaunchState.ClientStartedUnverified, false, false)]
    [InlineData(LaunchState.Uncertain, true, false)]
    public async Task RecoveredStartedClientClearsTheSingleStartBarrierOnlyWhenExactlyObserved(LaunchState saved, bool observed, bool starts)
    {
        var at = DateTimeOffset.UtcNow;
        var intent = new LaunchIntent(Guid.NewGuid(), Guid.NewGuid(), 1, 456, 42, 0, at.AddMinutes(-2), 7);
        var receipt = new LaunchProcessReceipt(890777, at.AddMinutes(-1).UtcTicks, at);
        var problem = saved == LaunchState.ClientStartedUnverified ? LaunchProblem.ClientStartedUnverified : LaunchProblem.ArrivalUnverified;
        var recovered = new LaunchOperation(intent, saved, problem, at, receipt, Budget(4, 5));
        var store = new Store(new(false, [recovered])); var provider = new Provider();
        await using var queue = new LaunchCoordinator(store, provider, (_, _) => Task.FromResult(true), _ => new(true, "Synthetic resources"),
            stabilization: TimeSpan.Zero, stagger: TimeSpan.Zero,
            observedReservations: _ => observed ? new HashSet<Guid> { intent.OperationId } : new HashSet<Guid>(),
            budgetResolver: _ => Budget());
        await queue.InitializeAsync(default);
        Assert.True(store.Last.Paused); Assert.Empty(provider.Started);
        await Assert.ThrowsAsync<LaunchQueueException>(() => queue.CancelAsync(intent.OperationId, default));
        var fresh = await queue.EnqueueAsync(Profile("Next request"), default, Binding); await queue.SetPausedAsync(false, default);
        if (starts) await Task.WhenAny(provider.Entered.Task, Task.Delay(TimeSpan.FromSeconds(4))); else await Settle();
        await queue.WaitForActiveCleanupAsync(default);
        if (starts) Assert.Equal(fresh.Intent.OperationId, Assert.Single(provider.Started).OperationId);
        else { Assert.Empty(provider.Started); Assert.Equal(LaunchState.Queued, store.Row(fresh.Intent.OperationId)!.State); }
        var row = store.Row(intent.OperationId)!;
        Assert.Equal(saved == LaunchState.ClientStartedUnverified ? saved : LaunchState.ReviewRequired, row.State);
        Assert.Equal(saved == LaunchState.ClientStartedUnverified ? problem : LaunchProblem.Interrupted, row.Problem);
        Assert.Equal(receipt, row.ProcessReceipt); Assert.Equal(Budget(4, 5), row.ResourceBudget);
        Assert.True(LaunchDescriptions.HasUnresolvedReservation(row));
    }

    [Theory]
    [InlineData(0UL, 1UL)]
    [InlineData(1UL, 0UL)]
    [InlineData(1099511627777UL, 1UL)]
    [InlineData(1UL, 1099511627777UL)]
    public async Task InvalidPersistedBudgetFailsRecoveryWithoutOverwritingTheStoredSnapshot(ulong physical, ulong commit)
    {
        var intent = new LaunchIntent(Guid.NewGuid(), Guid.NewGuid(), 1, 456, 42, 0, DateTimeOffset.UtcNow, 7);
        var invalid = new LaunchOperation(intent, LaunchState.Queued, LaunchProblem.None, DateTimeOffset.UtcNow, ResourceBudget: new(physical, commit));
        var snapshot = new LaunchQueueSnapshot(false, [invalid]); Assert.Throws<InvalidDataException>(() => LaunchCoordinator.ValidateSnapshot(snapshot));
        var store = new Store(snapshot); var provider = new Provider(); await using var queue = Queue(store, provider);
        await Assert.ThrowsAsync<InvalidDataException>(() => queue.InitializeAsync(default)); Assert.Same(snapshot, store.Last); Assert.Equal(0, store.Saves); Assert.Empty(provider.Started);
        LaunchCoordinator.ValidateSnapshot(snapshot with { Operations = [invalid with { ResourceBudget = null }] });
    }

    private static Task Settle() => Task.Delay(25);
    private static async Task Until(Func<bool> condition)
    { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4)); while (!condition()) await Task.Delay(5, stop.Token); }
    private sealed class Store(LaunchQueueSnapshot? initial = null) : ILaunchQueueStore
    {
        private LaunchQueueSnapshot _last = initial ?? LaunchQueueSnapshot.Empty; public int Saves;
        public LaunchQueueSnapshot Last => Volatile.Read(ref _last); public Func<LaunchQueueSnapshot, Task>? BeforeSave;
        public LaunchOperation? Row(Guid id) => Last.Operations.FirstOrDefault(row => row.Intent.OperationId == id);
        public Task<LaunchQueueSnapshot> LoadAsync(CancellationToken ct) => Task.FromResult(Last);
        public async Task SaveAsync(LaunchQueueSnapshot snapshot, CancellationToken ct)
        { if (BeforeSave is not null) await BeforeSave(snapshot); Interlocked.Increment(ref Saves); Volatile.Write(ref _last, snapshot with { Operations = snapshot.Operations.ToArray() }); }
    }
    private class Provider : ILaunchProvider
    {
        public bool Available = true; public readonly ConcurrentQueue<LaunchIntent> Started = new(); public int Confirmations;
        public TaskCompletionSource Entered { get; } = Gate(); public TaskCompletionSource<ProviderLaunchResult>? Reply;
        public virtual ProviderCapability AccountLaunch => new("Synthetic", Available ? CapabilityState.Supported : CapabilityState.Unavailable, "No native capability proof");
        public static ProviderLaunchResult Result() => new(ProviderLaunchStatus.ClientStartedUnverified, ProcessId: 890001, ProcessStartUtcTicks: DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks);
        public Task<ProviderLaunchResult> Emit(LaunchIntent intent) { Started.Enqueue(intent); Entered.TrySetResult(); return Reply?.Task ?? Task.FromResult(Result()); }
        public virtual Task<ProviderLaunchResult> LaunchAsync(LaunchIntent intent, CancellationToken ct) => Emit(intent);
        public Task<bool> ConfirmArrivalAsync(LaunchIntent intent, ProviderLaunchResult result, CancellationToken ct) { Confirmations++; return Task.FromResult(false); }
    }
    private sealed class PreparingProvider : Provider, IPreparingLaunchProvider
    {
        public TaskCompletionSource? PreparationRelease, BoundaryRelease; public bool AdvanceIdentity; public int Disposals, LegacyCalls;
        public TaskCompletionSource PreparationEntered { get; } = Gate(); public TaskCompletionSource BoundaryEntered { get; } = Gate();
        public override ProviderCapability AccountLaunch => new("Synthetic trial", CapabilityState.Unverified, "No native capability proof");
        public override Task<ProviderLaunchResult> LaunchAsync(LaunchIntent intent, CancellationToken ct) { LegacyCalls++; throw new InvalidOperationException("Legacy dispatch forbidden"); }
        public async Task<LaunchPreparationResult> PrepareAsync(LaunchPreparationContext context, CancellationToken ct)
        { PreparationEntered.TrySetResult(); if (PreparationRelease is not null) await PreparationRelease.Task; return new(LaunchPreparationFailure.None, new Prepared(this, context)); }
        private sealed class Prepared(PreparingProvider provider, LaunchPreparationContext context) : IPreparedAccountLaunch
        {
            private int _closed; public LaunchPreparationContext Context => context;
            public LaunchIntent DispatchIntent => provider.AdvanceIdentity ? context.OriginalIntent with { ProfileRevision = context.OriginalIntent.ProfileRevision + 1,
                AuthorizationGeneration = context.OriginalIntent.AuthorizationGeneration + 2 } : context.OriginalIntent;
            public bool IsExpired => Volatile.Read(ref _closed) != 0;
            public async Task<ProviderLaunchResult> DispatchAsync(LaunchDispatchBoundary boundary, CancellationToken ct)
            {
                provider.BoundaryEntered.TrySetResult(); if (provider.BoundaryRelease is not null) await provider.BoundaryRelease.Task;
                if (!await boundary.ValidateAsync(DispatchIntent, ct) || !boundary.IsCurrent || !boundary.ReadAdmission().Allowed) return new(ProviderLaunchStatus.RejectedBeforeStart);
                return await provider.Emit(DispatchIntent);
            }
            public ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _closed, 1) == 0) Interlocked.Increment(ref provider.Disposals); return ValueTask.CompletedTask; }
        }
    }
}
