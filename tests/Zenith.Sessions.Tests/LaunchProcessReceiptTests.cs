using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

/// <summary>All process identities/provider results below are fabricated. No process is started or inspected.</summary>
public sealed class LaunchProcessReceiptTests
{
    private static readonly DateTimeOffset Now=new(2026,11,5,12,0,0,TimeSpan.Zero);
    private const string Job="12345678-1234-4234-8234-123456789abc";
    private static AccountProfile Profile(string label="Receipt fixture")=>AccountProfile.Create(label) with {Revision=1,PreferredPlaceId=42};
    private static ProviderLaunchResult Started(ProviderLaunchStatus status=ProviderLaunchStatus.ClientStartedUnverified)
        =>new(status,999,777,5454,Now.AddMinutes(-1).UtcTicks,"22345678-1234-4234-8234-123456789abc");
    private static LaunchCoordinator Create(MemoryStore store,Provider provider,ManualClock clock)=>new(store,provider,
        (_,_)=>Task.FromResult(true),_=>new(true,"Synthetic admission"),clock,TimeSpan.FromSeconds(10),TimeSpan.Zero,TimeSpan.Zero);
    private static async Task Until(Func<bool> predicate)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while(!predicate()) await Task.Delay(5,deadline.Token);
    }

    [Fact]
    public async Task StartedClientIsReviewableAndReservedWithoutAccountOrArrivalVerification()
    {
        var clock=new ManualClock();var store=new MemoryStore();var provider=new Provider((_,_)=>Task.FromResult(Started()));
        await using var queue=Create(store,provider,clock);await queue.InitializeAsync(default);
        var requested=await queue.EnqueueAsync(Profile(),default,new(123,7),new(42,Job,RobloxServerKind.Public));
        await queue.SetPausedAsync(false,default);
        await Until(()=>store.Last.Operations.Any(o=>o.State==LaunchState.ClientStartedUnverified));
        var recorded=Assert.Single(store.Last.Operations);Assert.Equal(requested.Intent,recorded.Intent);
        Assert.Equal(LaunchProblem.ClientStartedUnverified,recorded.Problem);Assert.True(LaunchDescriptions.NeedsReservation(recorded.State));
        Assert.False(LaunchDescriptions.Terminal(recorded.State));Assert.Equal(0,provider.Confirmations);
        Assert.Equal(new LaunchProcessReceipt(5454,Now.AddMinutes(-1).UtcTicks,Now),recorded.ProcessReceipt);
        Assert.Contains("unverified",LaunchDescriptions.Explain(recorded.Problem));
        await Assert.ThrowsAsync<LaunchQueueException>(()=>queue.CancelAsync(recorded.Intent.OperationId,default));
        await Assert.ThrowsAsync<LaunchQueueException>(()=>queue.EnqueueAsync(Profile() with {Id=recorded.Intent.ProfileId},default,new(123,7)));
        await queue.AcknowledgeClientClosedAsync(recorded.Intent.OperationId,default);
        var reviewed=Assert.Single(store.Last.Operations);Assert.Equal(LaunchState.Cancelled,reviewed.State);
        Assert.Equal(recorded.ProcessReceipt,reviewed.ProcessReceipt);Assert.Single(provider.Calls);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task LateStartAfterPanicOrCancelKeepsStopStateAndOnlyAttachesToItsOriginalIntent(bool panic)
    {
        var clock=new ManualClock();var store=new MemoryStore();var release=new TaskCompletionSource<ProviderLaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider=new Provider((_,_)=>release.Task);
        await using var queue=Create(store,provider,clock);await queue.InitializeAsync(default);
        var first=await queue.EnqueueAsync(Profile("First"),default,new(123,7),new(42,Job,RobloxServerKind.Public));
        await queue.SetPausedAsync(false,default);await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
        var second=await queue.EnqueueAsync(Profile("Second"),default,new(456,8));
        try
        {
            if(panic) queue.SignalPanic();else await queue.CancelAsync(first.Intent.OperationId,default);
            await Until(()=>store.Last.Operations.Any(o=>o.Intent.OperationId==first.Intent.OperationId && o.State==LaunchState.Uncertain));
            clock.Advance(TimeSpan.FromSeconds(2));release.TrySetResult(Started(ProviderLaunchStatus.VerifiedArrival) with
                {ObservedUserId=first.Intent.UserId,ObservedPlaceId=first.Intent.PlaceId,ObservedJobId=first.Intent.RequestedJobId});
            await Until(()=>store.Last.Operations.Any(o=>o.Intent.OperationId==first.Intent.OperationId && o.ProcessReceipt is not null));
            var original=store.Last.Operations.Single(o=>o.Intent.OperationId==first.Intent.OperationId);
            var untouched=store.Last.Operations.Single(o=>o.Intent.OperationId==second.Intent.OperationId);
            Assert.Equal(LaunchState.Uncertain,original.State);Assert.Equal(LaunchProblem.CancelledDuringStartup,original.Problem);
            Assert.Equal(first.Intent,original.Intent);Assert.Equal(clock.GetUtcNow(),original.ProcessReceipt!.RecordedUtc);
            Assert.Equal(second.Intent,untouched.Intent);Assert.Equal(LaunchState.Queued,untouched.State);Assert.Null(untouched.ProcessReceipt);
            Assert.Equal(panic,store.Last.Paused);Assert.Single(provider.Calls);Assert.Equal(0,provider.Confirmations);
            Assert.DoesNotContain(store.Last.Operations,o=>o.State==LaunchState.Arrived);
        }
        finally {release.TrySetResult(new(ProviderLaunchStatus.Uncertain));}
    }

    [Fact]
    public async Task LateStartAfterDeterministicTimeoutIsDurableUncertaintyAndNotReplay()
    {
        using var root=new TemporaryRoot();using var durable=new LaunchQueueStore(root.Path);
        var clock=new ManualClock();var store=new MemoryStore(durable);var release=new TaskCompletionSource<ProviderLaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider=new Provider((_,_)=>release.Task);
        await using(var queue=Create(store,provider,clock))
        {
            await queue.InitializeAsync(default);var first=await queue.EnqueueAsync(Profile(),default,new(123,7));
            await queue.SetPausedAsync(false,default);await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            try
            {
                await Until(()=>clock.ActiveTimers>0);clock.Advance(TimeSpan.FromSeconds(11));
                await Until(()=>store.Last.Operations.Any(o=>o.Problem==LaunchProblem.TimedOut));
                await Assert.ThrowsAsync<LaunchQueueException>(()=>queue.AcknowledgeClientClosedAsync(first.Intent.OperationId,default));
                release.TrySetResult(Started(ProviderLaunchStatus.VerifiedArrival) with
                    {ObservedUserId=first.Intent.UserId,ObservedPlaceId=first.Intent.PlaceId,ObservedJobId=first.Intent.RequestedJobId});
                await Until(()=>store.Last.Operations.Any(o=>o.ProcessReceipt is not null));
                var row=Assert.Single(store.Last.Operations);Assert.Equal(LaunchState.Uncertain,row.State);Assert.Equal(LaunchProblem.TimedOut,row.Problem);
                Assert.Equal(clock.GetUtcNow(),row.ProcessReceipt!.RecordedUtc);Assert.Equal(0,provider.Confirmations);
            }
            finally {release.TrySetResult(new(ProviderLaunchStatus.Uncertain));}
        }
        var neverReplay=new Provider((_,_)=>throw new InvalidOperationException("A restored receipt must never launch."));
        using var reopenedDisk=new LaunchQueueStore(root.Path);var recoveredCache=new MemoryStore(reopenedDisk);
        await using var reopened=Create(recoveredCache,neverReplay,clock);await reopened.InitializeAsync(default);
        var recovered=Assert.Single(recoveredCache.Last.Operations);Assert.True(recoveredCache.Last.Paused);Assert.Equal(LaunchState.ReviewRequired,recovered.State);
        Assert.NotNull(recovered.ProcessReceipt);await reopened.SetPausedAsync(false,default);Assert.Empty(neverReplay.Calls);
    }

    [Theory]
    [InlineData(0,0)] [InlineData(5,1)] [InlineData(5,long.MaxValue)]
    public async Task InvalidStartedIdentityBecomesUncertainWithoutPersistingAPartialReceipt(int pid,long ticks)
    {
        var clock=new ManualClock();var store=new MemoryStore();var provider=new Provider((_,_)=>Task.FromResult(
            new ProviderLaunchResult(ProviderLaunchStatus.ClientStartedUnverified,ProcessId:pid,ProcessStartUtcTicks:ticks)));
        await using var queue=Create(store,provider,clock);await queue.InitializeAsync(default);
        await queue.EnqueueAsync(Profile(),default,new(123,7));await queue.SetPausedAsync(false,default);
        await Until(()=>store.Last.Operations.Any(o=>o.State==LaunchState.Uncertain));
        Assert.Null(Assert.Single(store.Last.Operations).ProcessReceipt);Assert.Equal(0,provider.Confirmations);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RejectionWithAProcessClaimCannotProveNoClientStarted(bool validPair)
    {
        var clock=new ManualClock();var store=new MemoryStore();var provider=new Provider((_,_)=>Task.FromResult(
            new ProviderLaunchResult(ProviderLaunchStatus.RejectedBeforeStart,ProcessId:5454,ProcessStartUtcTicks:validPair?Now.AddSeconds(-1).UtcTicks:null)));
        await using var queue=Create(store,provider,clock);await queue.InitializeAsync(default);
        await queue.EnqueueAsync(Profile(),default,new(123,7));await queue.SetPausedAsync(false,default);
        await Until(()=>store.Last.Operations.Any(o=>o.State==LaunchState.Uncertain));
        var row=Assert.Single(store.Last.Operations);Assert.Equal(LaunchProblem.ProviderFailure,row.Problem);
        Assert.Equal(validPair,row.ProcessReceipt is not null);Assert.Equal(0,provider.Confirmations);
    }

    [Theory]
    [InlineData(1)] [InlineData(long.MaxValue)]
    public async Task ClaimedArrivalWithInvalidProcessCreationCannotReachConfirmation(long ticks)
    {
        var clock=new ManualClock();var store=new MemoryStore();
        var provider=new Provider((intent,_)=>Task.FromResult(new ProviderLaunchResult(
            ProviderLaunchStatus.VerifiedArrival,intent.UserId,intent.PlaceId,5454,ticks,intent.RequestedJobId)))
            {ConfirmationResult=true};
        await using var queue=Create(store,provider,clock);await queue.InitializeAsync(default);
        await queue.EnqueueAsync(Profile(),default,new(123,7),new(42,Job,RobloxServerKind.Public));
        await queue.SetPausedAsync(false,default);
        await Until(()=>store.Last.Operations.Any(o=>o.State is LaunchState.Arrived or LaunchState.Uncertain));
        var operation=Assert.Single(store.Last.Operations);
        Assert.Equal(LaunchState.Uncertain,operation.State);Assert.Null(operation.ProcessReceipt);
        Assert.Equal(LaunchProblem.ArrivalUnverified,operation.Problem);
        Assert.Equal(0,provider.Confirmations);Assert.True(LaunchDescriptions.NeedsReservation(operation.State));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ProviderExceptionsCannotExposeHandoffMaterialThroughFaultEvents(bool duringConfirmation)
    {
        const string secret="synthetic-handoff-ticket-do-not-log";
        var clock=new ManualClock();var store=new MemoryStore();var errors=new ConcurrentQueue<Exception>();
        var provider=new Provider((intent,_)=>duringConfirmation
            ?Task.FromResult(new ProviderLaunchResult(ProviderLaunchStatus.VerifiedArrival,intent.UserId,intent.PlaceId,5454,Now.AddSeconds(-1).UtcTicks))
            :Task.FromException<ProviderLaunchResult>(new InvalidOperationException(secret)))
            {ConfirmationError=duringConfirmation?new InvalidOperationException(secret):null};
        await using var queue=Create(store,provider,clock);queue.Faulted+=errors.Enqueue;await queue.InitializeAsync(default);
        await queue.EnqueueAsync(Profile(),default,new(123,7));await queue.SetPausedAsync(false,default);
        await Until(()=>store.Last.Operations.Any(o=>o.State==LaunchState.Uncertain));
        Assert.NotEmpty(errors);Assert.All(errors,e=>Assert.DoesNotContain(secret,e.ToString()));
        Assert.DoesNotContain(secret,JsonSerializer.Serialize(store.Last));
    }

    [Fact]
    public async Task CurrentVersionPersistsOnlyProcessIdentityAndRestartRequiresReview()
    {
        using var root=new TemporaryRoot();var intent=new LaunchIntent(Guid.NewGuid(),Guid.NewGuid(),1,123,42,0,Now,7,Job,RobloxServerKind.Public,true);
        var receipt=new LaunchProcessReceipt(5454,Now.AddSeconds(-1).UtcTicks,Now);
        var snapshot=new LaunchQueueSnapshot(false,[new(intent,LaunchState.ClientStartedUnverified,LaunchProblem.ClientStartedUnverified,Now,receipt)]);
        using(var store=new LaunchQueueStore(root.Path)) await store.SaveAsync(snapshot,default);
        using var reopenedStore=new LaunchQueueStore(root.Path);var read=await reopenedStore.LoadAsync(default);Assert.Equal(snapshot.Operations,read.Operations);
        using var json=JsonDocument.Parse(await File.ReadAllTextAsync(root.File));Assert.Equal(7,json.RootElement.GetProperty("Version").GetInt32());
        var receiptJson=json.RootElement.GetProperty("Queue").GetProperty("Operations")[0].GetProperty("ProcessReceipt");
        Assert.Equal(new[]{"ProcessId","ProcessStartUtcTicks","RecordedUtc"},receiptJson.EnumerateObject().Select(p=>p.Name));
        var provider=new Provider((_,_)=>throw new InvalidOperationException("Do not replay."));
        await using var queue=new LaunchCoordinator(reopenedStore,provider,(_,_)=>Task.FromResult(true),_=>new(true,"Fixture"),new ManualClock());
        await queue.InitializeAsync(default);read=await reopenedStore.LoadAsync(default);
        Assert.True(read.Paused);Assert.Equal(LaunchState.ReviewRequired,Assert.Single(read.Operations).State);
        Assert.Equal(receipt,Assert.Single(read.Operations).ProcessReceipt);Assert.Empty(provider.Calls);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task LegacyQueueReadsStayReviewPausedAndRetainNonsecretTargetBinding(int version)
    {
        using var root=new TemporaryRoot();Directory.CreateDirectory(root.Path);
        var intent=new LaunchIntent(Guid.NewGuid(),Guid.NewGuid(),1,123,42,0,Now,7,Job,RobloxServerKind.Public,true);
        var snapshot=new LaunchQueueSnapshot(false,[new(intent,LaunchState.Launching,LaunchProblem.None,Now)]);
        var node=JsonNode.Parse(JsonSerializer.Serialize(new{Version=version,Queue=snapshot}))!;
        node["Queue"]!["Operations"]![0]!.AsObject().Remove("ProcessReceipt");
        await File.WriteAllTextAsync(root.File,node.ToJsonString());
        using var store=new LaunchQueueStore(root.Path);var read=await store.LoadAsync(default);
        Assert.True(read.Paused);var operation=Assert.Single(read.Operations);
        Assert.Equal(LaunchState.ReviewRequired,operation.State);Assert.Equal(LaunchProblem.Interrupted,operation.Problem);
        Assert.Equal(intent,operation.Intent);Assert.Null(operation.ProcessReceipt);
    }

    [Fact]
    public async Task UnknownFutureSchemaCannotFallBackToOlderBackupOrBeOverwritten()
    {
        using var root=new TemporaryRoot();Directory.CreateDirectory(root.Path);using var store=new LaunchQueueStore(root.Path);
        await store.SaveAsync(LaunchQueueSnapshot.Empty,default);await store.SaveAsync(LaunchQueueSnapshot.Empty,default);
        const string future="{\"Version\":99,\"Queue\":{\"FutureOnlyField\":true}}";
        await File.WriteAllTextAsync(root.File,future);
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.LoadAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.SaveAsync(LaunchQueueSnapshot.Empty,default));
        Assert.Equal(future,await File.ReadAllTextAsync(root.File));Assert.True(File.Exists(root.File+".bak"));Assert.False(File.Exists(root.File+".tmp"));
    }

    [Fact]
    public void ReceiptValidationDoesNotInferCreationFromOperationTimingOrAccountMetadata()
    {
        Assert.True(new LaunchProcessReceipt(1,DateTime.FromFileTimeUtc(0).Ticks,Now).IsValid);
        Assert.True(new LaunchProcessReceipt(1,Now.AddYears(-1).UtcTicks,Now).IsValid); // No nearest-start heuristic.
        foreach(var receipt in new[]{new LaunchProcessReceipt(0,Now.UtcTicks,Now),new LaunchProcessReceipt(1,1,Now),new LaunchProcessReceipt(1,long.MaxValue,Now),new LaunchProcessReceipt(1,Now.AddSeconds(1).UtcTicks,Now)})
            Assert.False(receipt.IsValid);
        Assert.Equal(9,(int)LaunchState.Validating);Assert.Equal(10,(int)LaunchState.ClientStartedUnverified);
        Assert.Equal(2,(int)ProviderLaunchStatus.VerifiedArrival);Assert.Equal(3,(int)ProviderLaunchStatus.ClientStartedUnverified);
    }

    [Fact]
    public async Task InvalidOrContradictoryReceiptCannotReplaceExistingDurableQueue()
    {
        using var root=new TemporaryRoot();using var store=new LaunchQueueStore(root.Path);
        await store.SaveAsync(LaunchQueueSnapshot.Empty,default);
        var intent=new LaunchIntent(Guid.NewGuid(),Guid.NewGuid(),1,123,42,0,Now,7);
        var valid=new LaunchProcessReceipt(5454,Now.AddSeconds(-1).UtcTicks,Now);
        foreach(var operation in new[]{
            new LaunchOperation(intent,LaunchState.ClientStartedUnverified,LaunchProblem.ClientStartedUnverified,Now),
            new LaunchOperation(intent,LaunchState.ClientStartedUnverified,LaunchProblem.ClientStartedUnverified,Now,valid with {ProcessId=0}),
            new LaunchOperation(intent,LaunchState.ClientStartedUnverified,LaunchProblem.ClientStartedUnverified,Now,valid with {ProcessStartUtcTicks=Now.AddSeconds(1).UtcTicks}),
            new LaunchOperation(intent,LaunchState.ClientStartedUnverified,LaunchProblem.ClientStartedUnverified,Now.AddSeconds(-1),valid),
            new LaunchOperation(intent,LaunchState.Failed,LaunchProblem.StartupRejected,Now,valid)})
            await Assert.ThrowsAsync<InvalidDataException>(()=>store.SaveAsync(new(false,[operation]),default));
        Assert.Empty((await store.LoadAsync(default)).Operations);
    }

    private sealed class Provider(Func<LaunchIntent,CancellationToken,Task<ProviderLaunchResult>> work) : ILaunchProvider
    {
        public readonly ConcurrentQueue<LaunchIntent> Calls=new();
        public readonly TaskCompletionSource<LaunchIntent> Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Confirmations;public Exception? ConfirmationError;public bool ConfirmationResult;
        public ProviderCapability AccountLaunch {get;}=new("Receipt fixture",CapabilityState.Supported,"No process or network actions.");
        public Task<ProviderLaunchResult> LaunchAsync(LaunchIntent intent,CancellationToken ct)
        {Calls.Enqueue(intent);Entered.TrySetResult(intent);return work(intent,ct);}
        public Task<bool> ConfirmArrivalAsync(LaunchIntent intent,ProviderLaunchResult result,CancellationToken ct)
        {Interlocked.Increment(ref Confirmations);return ConfirmationError is { } error?Task.FromException<bool>(error):Task.FromResult(ConfirmationResult);}
    }
    private sealed class MemoryStore(ILaunchQueueStore? backing=null) : ILaunchQueueStore
    {
        private LaunchQueueSnapshot _last=LaunchQueueSnapshot.Empty;
        public LaunchQueueSnapshot Last {get=>Volatile.Read(ref _last);set=>Volatile.Write(ref _last,value);}
        public async Task<LaunchQueueSnapshot> LoadAsync(CancellationToken ct)
        {ct.ThrowIfCancellationRequested();if(backing is not null)Last=await backing.LoadAsync(ct);return Last;}
        public async Task SaveAsync(LaunchQueueSnapshot value,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();LaunchCoordinator.ValidateSnapshot(value);if(backing is not null)await backing.SaveAsync(value,ct);Last=value;}
    }
    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate=new();private readonly List<ClockTimer> _timers=[];private DateTimeOffset _now=Now;
        public override DateTimeOffset GetUtcNow() {lock(_gate)return _now;}
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override long GetTimestamp() {lock(_gate)return _now.UtcTicks;}
        public int ActiveTimers {get {lock(_gate)return _timers.Count(t=>t.Due is not null);}}
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan due,TimeSpan period)
        {var timer=new ClockTimer(this,callback,state);lock(_gate){_timers.Add(timer);timer.Change(due,period);}return timer;}
        public void Advance(TimeSpan elapsed)
        {
            ClockTimer[] due;lock(_gate){_now+=elapsed;due=_timers.Where(t=>t.Due is { } at && at<=_now).ToArray();foreach(var timer in due)timer.Due=null;}
            foreach(var timer in due)timer.Callback(timer.State);
        }
        private sealed class ClockTimer(ManualClock owner,TimerCallback callback,object? state) : ITimer
        {
            public readonly TimerCallback Callback=callback;public readonly object? State=state;public DateTimeOffset? Due;
            public bool Change(TimeSpan due,TimeSpan period){lock(owner._gate){Due=due==Timeout.InfiniteTimeSpan?null:owner._now+due;return true;}}
            public void Dispose(){lock(owner._gate){Due=null;owner._timers.Remove(this);}}
            public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
        }
    }
    private sealed class TemporaryRoot : IDisposable
    {
        public string Path {get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"zenith-receipt-fixture-"+Guid.NewGuid().ToString("N"));
        public string File=>System.IO.Path.Combine(Path,"launch-queue.json");
        public void Dispose()
        {
            string full=System.IO.Path.GetFullPath(Path);
            if(System.IO.Path.GetDirectoryName(full)!=System.IO.Path.GetTempPath().TrimEnd(System.IO.Path.DirectorySeparatorChar)
                || !System.IO.Path.GetFileName(full).StartsWith("zenith-receipt-fixture-",StringComparison.Ordinal))throw new InvalidOperationException("Unexpected fixture directory.");
            if(Directory.Exists(full))Directory.Delete(full,true);
        }
    }
}
