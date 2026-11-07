using System.Collections.Concurrent;
using System.Threading.Channels;
using Zenith.Sessions.Core;
using Xunit;

namespace Zenith.Sessions.Tests;

// Owned memory storage and a Supported synthetic provider only. No native process, account or input.
public sealed class LaunchResumeStopTests
{
    private static AccountProfile Profile() => AccountProfile.Create("Synthetic resume fixture")
        with { Revision = 1, PreferredPlaceId = 1818 };
    private static LaunchCoordinator Queue(Store store, Provider provider) => new(store, provider,
        (_, _) => Task.FromResult(true), _ => new(true, "Synthetic resource admission"),
        stabilization: TimeSpan.Zero, stagger: TimeSpan.Zero);

    [Fact]
    public async Task ResumeWaitingForSerialCannotOvertakePanicAndAFreshManualResumeStillWorks()
    {
        var store = new Store(); var provider = new Provider();
        await using var queue = Queue(store, provider); await queue.InitializeAsync(default);
        var operation = await queue.EnqueueAsync(Profile(), default, new(123, 7));
        var gate = store.BlockNextSave();
        Task holder = queue.SetPausedAsync(true, default);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<bool> obsolete = queue.TryResumeAsync(default);
        try
        {
            Assert.False(obsolete.IsCompleted);
            queue.SignalPanic();
            gate.Release.SetResult();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(await obsolete.WaitAsync(TimeSpan.FromSeconds(5)));
            var heldWrite = await gate.Committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await store.NextWriteAsync(write => write.Sequence > heldWrite.Sequence && write.Snapshot.Paused);
            Assert.All(store.Writes, write => Assert.True(write.Snapshot.Paused));
            Assert.Equal(0, provider.LaunchCalls);
            Assert.Equal(LaunchState.Queued, Assert.Single(store.Last.Operations).State);

            Assert.True(await queue.TryResumeAsync(default));
            var dispatched = await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(operation.Intent, dispatched); Assert.Equal(1, provider.LaunchCalls);
            await queue.WaitForActiveCleanupAsync(default);
        }
        finally
        {
            gate.Release.TrySetResult();
            await holder; await obsolete;
        }
    }

    [Fact]
    public async Task LegacySetPausedResumeWrapperAlsoRejectsAStopThatArrivedWhileWaiting()
    {
        var store = new Store(); var provider = new Provider();
        await using var queue = Queue(store, provider); await queue.InitializeAsync(default);
        await queue.EnqueueAsync(Profile(), default, new(123, 7));
        var gate = store.BlockNextSave(); Task holder = queue.SetPausedAsync(true, default);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task obsolete = queue.SetPausedAsync(false, default);
        try
        {
            Assert.False(obsolete.IsCompleted); queue.SignalPanic(); gate.Release.SetResult();
            await Task.WhenAll(holder, obsolete).WaitAsync(TimeSpan.FromSeconds(5));
            var heldWrite = await gate.Committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await store.NextWriteAsync(write => write.Sequence > heldWrite.Sequence && write.Snapshot.Paused);
            Assert.All(store.Writes, write => Assert.True(write.Snapshot.Paused));
            Assert.Equal(0, provider.LaunchCalls);
            Assert.Equal(LaunchState.Queued, Assert.Single(store.Last.Operations).State);
        }
        finally { gate.Release.TrySetResult(); await Task.WhenAll(holder, obsolete); }
    }

    [Fact]
    public async Task PanicDuringResumePersistenceRejectsAcceptanceAndTheExistingRunnerPersistsTheStop()
    {
        var store = new Store(); var provider = new Provider();
        await using var queue = Queue(store, provider); await queue.InitializeAsync(default);
        await queue.EnqueueAsync(Profile(), default, new(123, 7));
        var gate = store.BlockNextSave(); Task<bool> resume = queue.TryResumeAsync(default);
        try
        {
            var saving = await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(saving.Paused); Assert.False(resume.IsCompleted);
            queue.SignalPanic(); Assert.Equal(0, provider.LaunchCalls);
            gate.Release.SetResult();
            Assert.False(await resume.WaitAsync(TimeSpan.FromSeconds(5)));
            var resumedWrite = await gate.Committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(resumedWrite.Snapshot.Paused); // The already-owned write is joined, then the newer stop is persisted.
            await store.NextWriteAsync(write => write.Sequence > resumedWrite.Sequence && write.Snapshot.Paused);
            Assert.True(store.Last.Paused); Assert.Equal(0, provider.LaunchCalls);
            Assert.Equal(LaunchState.Queued, Assert.Single(store.Last.Operations).State);
        }
        finally { gate.Release.TrySetResult(); await resume; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledResumeBeforeSerialOwnershipCannotMutateOrDispatch(bool alreadyCancelled)
    {
        var store = new Store(); var provider = new Provider();
        await using var queue = Queue(store, provider); await queue.InitializeAsync(default);
        await queue.EnqueueAsync(Profile(), default, new(123, 7));
        var gate = store.BlockNextSave(); Task holder = queue.SetPausedAsync(true, default);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var stop = new CancellationTokenSource();
        if (alreadyCancelled) stop.Cancel();
        Task<bool> resume = queue.TryResumeAsync(stop.Token);
        try
        {
            if (!alreadyCancelled) { Assert.False(resume.IsCompleted); stop.Cancel(); }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resume);
            Assert.Equal(0, provider.LaunchCalls); Assert.True(queue.IsHealthy);
            gate.Release.SetResult(); await holder.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(store.Last.Paused);
            Assert.All(store.Writes, write => Assert.True(write.Snapshot.Paused));
        }
        finally
        {
            gate.Release.TrySetResult(); await holder;
            try { await resume; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task CancellationHonoredByResumeStorageLeavesQueuePausedAndUnhealthyWithoutDispatch()
    {
        var store = new Store(); var provider = new Provider();
        await using var queue = Queue(store, provider); await queue.InitializeAsync(default);
        await queue.EnqueueAsync(Profile(), default, new(123, 7));
        using var stop = new CancellationTokenSource();
        var gate = store.BlockNextSave(); Task<bool> resume = queue.TryResumeAsync(stop.Token);
        try
        {
            var saving = await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(saving.Paused);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resume);
            Assert.False(queue.IsHealthy); Assert.True(store.Last.Paused); Assert.Equal(0, provider.LaunchCalls);
            Assert.All(store.Writes, write => Assert.True(write.Snapshot.Paused));
            await Assert.ThrowsAsync<LaunchQueueException>(() => queue.TryResumeAsync(default));
        }
        finally
        {
            gate.Release.TrySetResult();
            try { await resume; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task NewerNormalPauseAlsoSupersedesAnAlreadyWaitingResume()
    {
        var store = new Store(); var provider = new Provider();
        await using var queue = Queue(store, provider); await queue.InitializeAsync(default);
        await queue.EnqueueAsync(Profile(), default, new(123, 7));
        var gate = store.BlockNextSave(); Task holder = queue.SetPausedAsync(true, default);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<bool> obsolete = queue.TryResumeAsync(default);
        Task newerPause = queue.SetPausedAsync(true, default);
        try
        {
            Assert.False(obsolete.IsCompleted); Assert.False(newerPause.IsCompleted);
            gate.Release.SetResult(); await holder.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(await obsolete.WaitAsync(TimeSpan.FromSeconds(5)));
            await newerPause.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(store.Last.Paused); Assert.Equal(0, provider.LaunchCalls);
            Assert.All(store.Writes, write => Assert.True(write.Snapshot.Paused));
        }
        finally { gate.Release.TrySetResult(); await holder; await obsolete; await newerPause; }
    }

    [Fact]
    public async Task AcceptedResumeAndNormalPauseRetainTheirExistingPublicBehavior()
    {
        var store = new Store(); var provider = new Provider();
        await using var queue = Queue(store, provider); await queue.InitializeAsync(default);
        Assert.True(await queue.TryResumeAsync(default)); Assert.False(store.Last.Paused);
        await queue.SetPausedAsync(true, default); Assert.True(store.Last.Paused);
        await queue.SetPausedAsync(false, default); Assert.False(store.Last.Paused);
        await queue.SetPausedAsync(true, default); Assert.True(store.Last.Paused);
        Assert.True(queue.IsHealthy); Assert.Equal(0, provider.LaunchCalls);
    }

    private sealed record Write(int Sequence, LaunchQueueSnapshot Snapshot);
    private sealed class SaveGate
    {
        public TaskCompletionSource<LaunchQueueSnapshot> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Write> Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Store : ILaunchQueueStore
    {
        private LaunchQueueSnapshot _last = LaunchQueueSnapshot.Empty;
        private SaveGate? _next;
        private int _sequence;
        private readonly Channel<Write> _published = Channel.CreateBounded<Write>(64);
        public ConcurrentQueue<Write> Writes { get; } = new();
        public LaunchQueueSnapshot Last => Volatile.Read(ref _last);
        public SaveGate BlockNextSave()
        {
            var gate = new SaveGate();
            if (Interlocked.CompareExchange(ref _next, gate, null) is not null)
                throw new InvalidOperationException("Only one synthetic storage gate may be armed.");
            return gate;
        }
        public Task<LaunchQueueSnapshot> LoadAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(Last); }
        public async Task SaveAsync(LaunchQueueSnapshot snapshot, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var gate = Interlocked.Exchange(ref _next, null);
            if (gate is not null)
            {
                gate.Entered.SetResult(snapshot);
                await gate.Release.Task.WaitAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            Volatile.Write(ref _last, snapshot);
            var write = new Write(Interlocked.Increment(ref _sequence), snapshot);
            Writes.Enqueue(write); _published.Writer.TryWrite(write); gate?.Committed.SetResult(write);
        }
        public async Task<Write> NextWriteAsync(Func<Write, bool> accept)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var write = await _published.Reader.ReadAsync(stop.Token);
                if (accept(write)) return write;
            }
        }
    }
    private sealed class Provider : ILaunchProvider
    {
        private int _calls;
        public int LaunchCalls => Volatile.Read(ref _calls);
        public TaskCompletionSource<LaunchIntent> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderCapability AccountLaunch { get; } = new("Synthetic resume provider", CapabilityState.Supported,
            "Memory-only fixture; no native action.");
        public Task<ProviderLaunchResult> LaunchAsync(LaunchIntent intent, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref _calls); Entered.TrySetResult(intent);
            return Task.FromResult(new ProviderLaunchResult(ProviderLaunchStatus.RejectedBeforeStart));
        }
        public Task<bool> ConfirmArrivalAsync(LaunchIntent intent, ProviderLaunchResult observed, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(false); }
    }
}
