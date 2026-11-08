using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using Zenith.Sessions.Core;
using Xunit;

namespace Zenith.Sessions.Tests;

// Controlled storage and a Supported memory-only provider. No process or account is opened.
public sealed class LaunchArrivalPublicationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StopAfterConfirmationWaitsForSerialCannotPublishArrival(bool panic)
    {
        await using var fixture = await Fixture.StartAsync();
        var receipt = Assert.Single(fixture.Store.Last.Operations).ProcessReceipt;
        Assert.NotNull(receipt);
        var gate = fixture.Store.BlockNextSave();
        Task<LaunchOperation> holder = fixture.Queue.EnqueueAsync(Profile("Second"), default, new(456, 8));
        Task cancel = Task.CompletedTask;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (!panic)
            {
                fixture.StopObserved = true;
                cancel = fixture.Queue.CancelAsync(fixture.Operation.Intent.OperationId, default);
                await WaitForSerialWaitersAsync(fixture.Queue, 1);
            }
            fixture.Provider.Confirmation.SetResult(true);
            await WaitForSerialWaitersAsync(fixture.Queue, panic ? 1 : 2);
            if (panic) { fixture.StopObserved = true; fixture.Queue.SignalPanic(); }
            gate.Release.SetResult();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
            await cancel.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Queue.WaitForActiveCleanupAsync(default).WaitAsync(TimeSpan.FromSeconds(5));

            var current = fixture.Store.Last.Operations.Single(row => row.Intent == fixture.Operation.Intent);
            Assert.Equal(LaunchState.Uncertain, current.State);
            Assert.Equal(LaunchProblem.CancelledDuringStartup, current.Problem);
            Assert.Equal(receipt, current.ProcessReceipt);
            Assert.True(LaunchDescriptions.HasUnresolvedReservation(current));
            Assert.Equal(panic, fixture.Store.Last.Paused);
            Assert.DoesNotContain(fixture.Publications, value => value.AfterStop && value.Arrived);
            Assert.DoesNotContain(fixture.Store.Writes, snapshot => snapshot.Operations.Any(row => row.State == LaunchState.Arrived));
            Assert.Equal(1, fixture.Provider.LaunchCalls);
        }
        finally
        {
            gate.Release.TrySetResult(); fixture.Provider.Confirmation.TrySetResult(false);
            await holder; await cancel;
        }
    }

    [Fact]
    public async Task PanicDuringFinalArrivalWriteCorrectsDurableStateBeforePublishing()
    {
        await using var fixture = await Fixture.StartAsync();
        var receipt = Assert.Single(fixture.Store.Last.Operations).ProcessReceipt;
        var gate = fixture.Store.BlockNextSave();
        fixture.Provider.Confirmation.SetResult(true);
        try
        {
            var saving = await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(LaunchState.Arrived, Assert.Single(saving.Operations).State);
            fixture.StopObserved = true; fixture.Queue.SignalPanic(); gate.Release.SetResult();
            await fixture.Queue.WaitForActiveCleanupAsync(default).WaitAsync(TimeSpan.FromSeconds(5));

            var current = Assert.Single(fixture.Store.Last.Operations);
            Assert.Equal(LaunchState.Uncertain, current.State);
            Assert.Equal(LaunchProblem.CancelledDuringStartup, current.Problem);
            Assert.Equal(receipt, current.ProcessReceipt); Assert.True(fixture.Store.Last.Paused);
            Assert.DoesNotContain(fixture.Publications, value => value.AfterStop && value.Arrived);
            Assert.Equal(1, fixture.Provider.LaunchCalls);
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Fact]
    public async Task ReentrantStorageStopCannotExposeItsObsoleteArrivalSnapshot()
    {
        await using var fixture = await Fixture.StartAsync();
        bool stopped = false;
        fixture.Store.BeforeSave = snapshot =>
        {
            if (!stopped && snapshot.Operations.Any(row => row.State == LaunchState.Arrived))
            { stopped = true; fixture.StopObserved = true; fixture.Queue.SignalPanic(); }
            return Task.CompletedTask;
        };
        fixture.Provider.Confirmation.SetResult(true);
        await fixture.Queue.WaitForActiveCleanupAsync(default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(stopped);
        var current = Assert.Single(fixture.Store.Last.Operations);
        Assert.Equal(LaunchState.Uncertain, current.State);
        Assert.Equal(LaunchProblem.CancelledDuringStartup, current.Problem);
        Assert.NotNull(current.ProcessReceipt); Assert.True(fixture.Store.Last.Paused);
        Assert.DoesNotContain(fixture.Publications, value => value.AfterStop && value.Arrived);
    }

    [Fact]
    public async Task StopFromArrivalNotificationRetainsUncertaintyAfterTheObserverReturns()
    {
        await using var fixture = await Fixture.StartAsync();
        bool stopped = false, laterObserverSawStaleArrival = false;
        fixture.Queue.Changed += snapshot =>
        {
            if (!stopped && snapshot.Operations.Any(row => row.State == LaunchState.Arrived))
            { stopped = true; fixture.StopObserved = true; fixture.Queue.SignalPanic(); }
        };
        fixture.Queue.Changed += snapshot =>
        { laterObserverSawStaleArrival |= stopped && snapshot.Operations.Any(row => row.State == LaunchState.Arrived); };
        fixture.Provider.Confirmation.SetResult(true);
        await fixture.Queue.WaitForActiveCleanupAsync(default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(stopped); Assert.False(laterObserverSawStaleArrival);
        var current = Assert.Single(fixture.Store.Last.Operations);
        Assert.Equal(LaunchState.Uncertain, current.State);
        Assert.Equal(LaunchProblem.CancelledDuringStartup, current.Problem);
        Assert.NotNull(current.ProcessReceipt); Assert.True(fixture.Store.Last.Paused);
        Assert.DoesNotContain(fixture.Publications, value => value.AfterStop && value.Arrived);
    }

    [Theory]
    [InlineData(LaunchState.Uncertain)]
    [InlineData(LaunchState.ReviewRequired)]
    public async Task LateConfirmationPreservesAnAlreadyReconciledCurrentState(LaunchState state)
    {
        await using var fixture = await Fixture.StartAsync();
        var serial = PrivateField<SemaphoreSlim>(fixture.Queue, "_serial");
        await serial.WaitAsync();
        LaunchOperation preserved;
        try
        {
            var operations = PrivateField<List<LaunchOperation>>(fixture.Queue, "_operations");
            var current = Assert.Single(operations);
            preserved = current with { State = state, Problem = LaunchProblem.Interrupted };
            // Stand in for a newer serialized reconciliation. Its exact receipt and state
            // belong to the current row, which this old confirmation must leave untouched.
            operations[0] = preserved;
            await fixture.Store.SaveAsync(new(false, [preserved]), default);
            fixture.Provider.Confirmation.SetResult(true);
            await WaitForSerialWaitersAsync(fixture.Queue, 1);
        }
        finally { serial.Release(); }
        await fixture.Queue.WaitForActiveCleanupAsync(default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(preserved, Assert.Single(fixture.Store.Last.Operations));
        Assert.DoesNotContain(fixture.Publications, value => value.Arrived);
        Assert.Equal(1, fixture.Provider.LaunchCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnstoppedConfirmationPreservesAcceptanceAndRejectionOutcomes(bool confirmed)
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Provider.Confirmation.SetResult(confirmed);
        await fixture.Queue.WaitForActiveCleanupAsync(default).WaitAsync(TimeSpan.FromSeconds(5));

        var current = Assert.Single(fixture.Store.Last.Operations);
        Assert.Equal(confirmed ? LaunchState.Arrived : LaunchState.Uncertain, current.State);
        Assert.Equal(confirmed ? LaunchProblem.None : LaunchProblem.ArrivalUnverified, current.Problem);
        Assert.NotNull(current.ProcessReceipt); Assert.False(fixture.Store.Last.Paused);
        Assert.Equal(confirmed ? 1 : 0, fixture.Publications.Count(value => value.Arrived));
        Assert.Equal(1, fixture.Provider.LaunchCalls); Assert.Equal(1, fixture.Provider.Confirmations);
    }

    [Fact]
    public async Task ConfirmationTimeoutRetainsItsReasonWhenItCancelsProviderWork()
    {
        await using var fixture = await Fixture.StartAsync();
        var receipt = Assert.Single(fixture.Store.Last.Operations).ProcessReceipt;
        fixture.Provider.Confirmation.SetException(new TimeoutException("Synthetic confirmation deadline"));
        await fixture.Queue.WaitForActiveCleanupAsync(default).WaitAsync(TimeSpan.FromSeconds(5));

        var current = Assert.Single(fixture.Store.Last.Operations);
        Assert.Equal(LaunchState.Uncertain, current.State);
        Assert.Equal(LaunchProblem.TimedOut, current.Problem);
        Assert.Equal(receipt, current.ProcessReceipt); Assert.False(fixture.Store.Last.Paused);
        Assert.DoesNotContain(fixture.Publications, value => value.Arrived);
    }

    [Fact]
    public async Task FailedPostStopCorrectionLeavesRecoveryPausedAndNeverPublishesArrival()
    {
        await using var fixture = await Fixture.StartAsync();
        bool stopped = false;
        fixture.Store.BeforeSave = snapshot =>
        {
            if (snapshot.Operations.Any(row => row.State == LaunchState.Arrived))
            { stopped = true; fixture.StopObserved = true; fixture.Queue.SignalPanic(); }
            else if (stopped && snapshot.Operations.Any(row => row.State == LaunchState.Uncertain))
                throw new IOException("Synthetic correction failure");
            return Task.CompletedTask;
        };
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Queue.Faulted += _ => faulted.TrySetResult();
        fixture.Provider.Confirmation.SetResult(true);
        await faulted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Queue.WaitForActiveCleanupAsync(default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(fixture.Queue.IsHealthy);
        Assert.DoesNotContain(fixture.Publications, value => value.Arrived);
        var durable = Assert.Single(fixture.Store.Last.Operations);
        Assert.Equal(LaunchState.Arrived, durable.State); Assert.NotNull(durable.ProcessReceipt);
        var recovery = new Store { Initial = fixture.Store.Last };
        await using var reopened = NewQueue(recovery, new Provider());
        await reopened.InitializeAsync(default);
        Assert.True(recovery.Last.Paused);
        Assert.Equal(LaunchState.ReviewRequired, Assert.Single(recovery.Last.Operations).State);
    }

    private static AccountProfile Profile(string name = "Arrival fixture")
        => AccountProfile.Create(name) with { Revision = 1, PreferredPlaceId = 42 };
    private static LaunchCoordinator NewQueue(Store store, Provider provider) => new(store, provider,
        (_, _) => Task.FromResult(true), _ => new(true, "Synthetic admission"),
        startupDeadline: TimeSpan.FromSeconds(20), stabilization: TimeSpan.Zero, stagger: TimeSpan.Zero);
    private static T PrivateField<T>(object owner, string name)
        => (T)(owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
            ?? throw new InvalidOperationException("Expected controlled-fixture field is unavailable."));

    private static async Task WaitForSerialWaitersAsync(LaunchCoordinator queue, int minimum)
    {
        var serial = PrivateField<SemaphoreSlim>(queue, "_serial");
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var head = typeof(SemaphoreSlim).GetField("m_asyncHead", fields)
            ?? typeof(SemaphoreSlim).GetField("_asyncHead", fields)
            ?? throw new InvalidOperationException("The controlled serial-wait fixture is unavailable.");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            // Observe the actual wait, rather than guessing how long the confirmation
            // continuation needs. This test-only inspection never changes semaphore state.
            int count = 0; object? waiter = head.GetValue(serial);
            while (waiter is not null && count < minimum)
            {
                count++;
                waiter = waiter.GetType().GetField("Next", fields | BindingFlags.Public)?.GetValue(waiter);
            }
            if (count >= minimum) return;
            await Task.Delay(5, stop.Token);
        }
    }

    private sealed record Publication(bool AfterStop, bool Arrived);
    private sealed class Fixture : IAsyncDisposable
    {
        public Store Store { get; } = new();
        public Provider Provider { get; } = new();
        public LaunchCoordinator Queue { get; }
        public LaunchOperation Operation { get; private set; } = null!;
        private int _stopped;
        public bool StopObserved { get => Volatile.Read(ref _stopped) != 0; set => Volatile.Write(ref _stopped, value ? 1 : 0); }
        public ConcurrentQueue<Publication> Publications { get; } = new();
        private Fixture()
        {
            Queue = NewQueue(Store, Provider);
            Queue.Changed += snapshot => Publications.Enqueue(new(StopObserved,
                snapshot.Operations.Any(row => row.State == LaunchState.Arrived)));
        }
        public static async Task<Fixture> StartAsync()
        {
            var fixture = new Fixture();
            try
            {
                await fixture.Queue.InitializeAsync(default);
                fixture.Operation = await fixture.Queue.EnqueueAsync(Profile(), default, new(123, 7),
                    new(42, "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", RobloxServerKind.Public));
                await fixture.Queue.SetPausedAsync(false, default);
                await fixture.Provider.ConfirmationEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        { Store.ReleaseGates(); Provider.Confirmation.TrySetResult(false); await Queue.DisposeAsync(); }
    }
    private sealed class SaveGate
    {
        public TaskCompletionSource<LaunchQueueSnapshot> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Store : ILaunchQueueStore
    {
        private LaunchQueueSnapshot _last = LaunchQueueSnapshot.Empty;
        private SaveGate? _next;
        private readonly ConcurrentQueue<SaveGate> _gates = new();
        public LaunchQueueSnapshot? Initial;
        public LaunchQueueSnapshot Last => Volatile.Read(ref _last);
        public ConcurrentQueue<LaunchQueueSnapshot> Writes { get; } = new();
        public Func<LaunchQueueSnapshot, Task>? BeforeSave;
        public Task<LaunchQueueSnapshot> LoadAsync(CancellationToken ct) => Task.FromResult(Initial ?? Last);
        public SaveGate BlockNextSave()
        {
            var gate = new SaveGate();
            if (Interlocked.CompareExchange(ref _next, gate, null) is not null)
                throw new InvalidOperationException("Only one controlled storage gate may be armed.");
            _gates.Enqueue(gate); return gate;
        }
        public async Task SaveAsync(LaunchQueueSnapshot snapshot, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var gate = Interlocked.Exchange(ref _next, null);
            if (gate is not null)
            { gate.Entered.SetResult(snapshot); await gate.Release.Task.WaitAsync(ct); }
            if (BeforeSave is { } before) await before(snapshot);
            ct.ThrowIfCancellationRequested(); Volatile.Write(ref _last, snapshot); Writes.Enqueue(snapshot);
        }
        public void ReleaseGates() { foreach (var gate in _gates) gate.Release.TrySetResult(); }
    }
    private sealed class Provider : ILaunchProvider
    {
        private int _launches, _confirmations;
        public int LaunchCalls => Volatile.Read(ref _launches);
        public int Confirmations => Volatile.Read(ref _confirmations);
        public TaskCompletionSource ConfirmationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Confirmation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderCapability AccountLaunch { get; } = new("Synthetic arrival fixture", CapabilityState.Supported, "No native process is started.");
        public Task<ProviderLaunchResult> LaunchAsync(LaunchIntent intent, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref _launches);
            return Task.FromResult(new ProviderLaunchResult(ProviderLaunchStatus.VerifiedArrival, intent.UserId, intent.PlaceId,
                456789, DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks, intent.RequestedJobId));
        }
        public Task<bool> ConfirmArrivalAsync(LaunchIntent intent, ProviderLaunchResult observed, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref _confirmations); ConfirmationEntered.TrySetResult(); return Confirmation.Task; }
    }
}
