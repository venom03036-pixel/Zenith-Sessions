using System.IO;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class LaunchCoordinatorTests
{
    private const string RequestedJob="12345678-1234-4234-8234-123456789abc";
    private const string DifferentJob="22345678-1234-4234-8234-123456789abc";
    private static AccountProfile Profile(string label="Synthetic")=>AccountProfile.Create(label) with { Revision=1,RobloxUserId=123,PreferredPlaceId=6564161046 };
    private static LaunchCoordinator Create(MemoryStore store,ILaunchProvider provider,Func<LaunchIntent,CancellationToken,Task<bool>>? validate=null,
        Func<IReadOnlyList<LaunchOperation>,LaunchAdmission>? admission=null,TimeSpan? timeout=null)=>
        new(store,provider,validate??((_,_)=>Task.FromResult(true)),admission??(_=>new(true,"Synthetic capacity only")),
            startupDeadline:timeout??TimeSpan.FromSeconds(2),stabilization:TimeSpan.Zero,stagger:TimeSpan.Zero);
    private static async Task WaitFor(Func<bool> predicate)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while(!predicate()) await Task.Delay(5,deadline.Token);
    }
    [Fact]
    public async Task UnavailableProviderNeverSpawnsOrReportsRunning()
    {
        var store=new MemoryStore();await using var queue=Create(store,new UnavailableLaunchProvider());await queue.InitializeAsync(default);
        var row=await queue.EnqueueAsync(Profile(),default,new(123,1));await queue.SetPausedAsync(false,default);
        Assert.Equal(LaunchState.Blocked,row.State);Assert.Equal(LaunchProblem.ProviderUnavailable,row.Problem);
        Assert.DoesNotContain(store.Last.Operations,o=>o.State is LaunchState.Launching or LaunchState.Arrived);
        await queue.CancelAsync(row.Intent.OperationId,default);Assert.Equal(LaunchState.Cancelled,Assert.Single(store.Last.Operations).State);
    }
    [Fact]
    public async Task DuplicateConcurrentRequestsProduceOnePendingOperation()
    {
        var store=new MemoryStore();await using var queue=Create(store,new UnavailableLaunchProvider());await queue.InitializeAsync(default);var profile=Profile();
        var attempts=await Task.WhenAll(Enumerable.Range(0,40).Select(async _=> { try {await queue.EnqueueAsync(profile,default,new(123,1));return true;}catch(LaunchQueueException) {return false;} }));
        Assert.Equal(1,attempts.Count(x=>x));Assert.Single(store.Last.Operations);
    }
    [Fact]
    public async Task ReadyAccountsFollowPriorityAndRemainIndependent()
    {
        var store=new MemoryStore();var provider=new SyntheticProvider();await using var queue=Create(store,provider);await queue.InitializeAsync(default);
        var low=Profile("Low");var high=Profile("High") with { Priority=100 };
        await queue.EnqueueAsync(low,default,new(123,1));await queue.EnqueueAsync(high,default,new(123,1));await queue.SetPausedAsync(false,default);
        await WaitFor(()=>store.Last.Operations.Count(o=>o.State==LaunchState.Arrived)==2);
        Assert.Equal(new[]{high.Id,low.Id},provider.Started.ToArray());
        await Assert.ThrowsAsync<LaunchQueueException>(()=>queue.EnqueueAsync(high,default,new(123,1)));
    }
    [Fact]
    public async Task MemoryHoldDoesNotCallProviderAndFreshSignalCanAdmitLater()
    {
        var store=new MemoryStore();var provider=new SyntheticProvider();bool allowed=false;
        await using var queue=Create(store,provider,admission:_=>new(Volatile.Read(ref allowed),"Synthetic resource hold"));await queue.InitializeAsync(default);
        await queue.EnqueueAsync(Profile(),default,new(123,1));await queue.SetPausedAsync(false,default);
        await WaitFor(()=>store.Last.Operations.Any(o=>o.Problem==LaunchProblem.InsufficientCapacity));Assert.Empty(provider.Started);
        Volatile.Write(ref allowed,true);queue.Signal();await WaitFor(()=>store.Last.Operations.Any(o=>o.State==LaunchState.Arrived));Assert.Single(provider.Started);
    }
    [Fact]
    public async Task CapacityHoldDoesNotRewriteAnUnchangedBlockedRowOnEachSignal()
    {
        // The Shell signals the queue after every monitor sample. A request that is still
        // waiting for headroom must not be re-saved (fsync + backup rotation) or re-published.
        var store=new MemoryStore();var provider=new SyntheticProvider();bool allowed=false;int admissions=0,saves=0,changes=0;
        store.BeforeSave=(_,_)=>{Interlocked.Increment(ref saves);return Task.CompletedTask;};
        await using var queue=Create(store,provider,admission:_=>{Interlocked.Increment(ref admissions);return new(Volatile.Read(ref allowed),"Synthetic resource hold");});
        queue.Changed+=_=>Interlocked.Increment(ref changes);
        await queue.InitializeAsync(default);
        await queue.EnqueueAsync(Profile(),default,new(123,1));await queue.SetPausedAsync(false,default);
        await WaitFor(()=>store.Last.Operations.Any(o=>o.Problem==LaunchProblem.InsufficientCapacity));
        await queue.WaitForActiveCleanupAsync(default);
        int savesBefore=Volatile.Read(ref saves),changesBefore=Volatile.Read(ref changes);
        var blocked=Assert.Single(store.Last.Operations);
        Assert.Equal(LaunchState.Blocked,blocked.State);
        for(int i=0;i<3;i++)
        {
            int seen=Volatile.Read(ref admissions);queue.Signal();
            await WaitFor(()=>Volatile.Read(ref admissions)>seen);
            await queue.WaitForActiveCleanupAsync(default);
        }
        Assert.Equal(savesBefore,Volatile.Read(ref saves));
        Assert.Equal(changesBefore,Volatile.Read(ref changes));
        Assert.Equal(blocked.UpdatedUtc,Assert.Single(store.Last.Operations).UpdatedUtc);
        Assert.Empty(provider.Started);
        Volatile.Write(ref allowed,true);queue.Signal();
        await WaitFor(()=>store.Last.Operations.Any(o=>o.State==LaunchState.Arrived));Assert.Single(provider.Started);
    }
    [Fact]
    public async Task WrongAccountCannotBecomeAnArrivedClient()
    {
        var store=new MemoryStore();var provider=new SyntheticProvider { WrongIdentity=true };await using var queue=Create(store,provider);await queue.InitializeAsync(default);
        var row=await queue.EnqueueAsync(Profile(),default,new(123,1));await queue.SetPausedAsync(false,default);
        await WaitFor(()=>store.Last.Operations.Any(o=>o.State==LaunchState.Uncertain));Assert.Equal(LaunchProblem.ArrivalUnverified,Assert.Single(store.Last.Operations).Problem);
        await Assert.ThrowsAsync<LaunchQueueException>(()=>queue.CancelAsync(row.Intent.OperationId,default));
        await queue.AcknowledgeClientClosedAsync(row.Intent.OperationId,default);Assert.Equal(LaunchState.Cancelled,Assert.Single(store.Last.Operations).State);
    }
    [Fact]
    public async Task FailedStabilizationConfirmationIsNotReportedAsArrival()
    {
        var store=new MemoryStore();var provider=new SyntheticProvider { Confirmation=false };await using var queue=Create(store,provider);await queue.InitializeAsync(default);
        await queue.EnqueueAsync(Profile(),default,new(123,1));await queue.SetPausedAsync(false,default);await WaitFor(()=>store.Last.Operations.Any(o=>o.State==LaunchState.Uncertain));
        Assert.DoesNotContain(store.Last.Operations,o=>o.State==LaunchState.Arrived);
    }
    [Fact]
    public async Task PanicDuringValidationFencesTheDelayedCallbackBeforeAnyNativeLaunch()
    {
        var store=new MemoryStore();var provider=new SyntheticProvider();var validating=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue=Create(store,provider,validate:(_,_)=> {validating.TrySetResult();return release.Task;});await queue.InitializeAsync(default);
        await queue.EnqueueAsync(Profile(),default,new(123,1));await queue.SetPausedAsync(false,default);await validating.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.SignalPanic();await WaitFor(()=>store.Last.Operations.Any(o=>o.State==LaunchState.Cancelled));release.TrySetResult(true);
        Assert.Empty(provider.Started);Assert.True(store.Last.Paused);
    }
    [Fact]
    public async Task TimedOutUncooperativeProviderRetainsReservationAndBlocksAnotherLaunch()
    {
        var store=new MemoryStore();var provider=new SyntheticProvider { Delayed=new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var queue=Create(store,provider,timeout:TimeSpan.FromMilliseconds(80));await queue.InitializeAsync(default);
        var first=await queue.EnqueueAsync(Profile("First"),default,new(123,1));await queue.SetPausedAsync(false,default);
        try
        {
            // Same-clock queued requests use the documented deterministic GUID tie-breaker.
            // Establish which request owns the uncooperative provider before adding its rival.
            await WaitFor(()=>provider.StartedIntents.Any(intent=>intent.OperationId==first.Intent.OperationId));
            await queue.EnqueueAsync(Profile("Second"),default,new(123,1));
            await WaitFor(()=>store.Last.Operations.Any(o=>o.State==LaunchState.Uncertain));Assert.Single(provider.Started);
            Assert.Equal(LaunchProblem.TimedOut,store.Last.Operations.First(o=>o.Intent.OperationId==first.Intent.OperationId).Problem);
            await Assert.ThrowsAsync<LaunchQueueException>(()=>queue.AcknowledgeClientClosedAsync(first.Intent.OperationId,default));
        }
        finally {provider.Delayed.TrySetResult(new(ProviderLaunchStatus.Uncertain));}
    }
    [Fact]
    public async Task CrashRecoveryAlwaysRequiresReviewAndNeverReplaysSavedIntent()
    {
        var store=new MemoryStore();var p=Profile();var i=new LaunchIntent(Guid.NewGuid(),p.Id,1,123,6564161046,0,DateTimeOffset.UtcNow);
        store.Last=new(false,[new(i,LaunchState.Launching,LaunchProblem.None,DateTimeOffset.UtcNow)]);
        var provider=new SyntheticProvider();await using var queue=Create(store,provider);await queue.InitializeAsync(default);await queue.SetPausedAsync(false,default);queue.Signal();
        Assert.Equal(LaunchState.ReviewRequired,Assert.Single(store.Last.Operations).State);Assert.Empty(provider.Started);
    }
    [Fact]
    public async Task FailedPersistenceStopsFurtherQueueChangesInsteadOfReportingSuccess()
    {
        var store=new MemoryStore();await using var queue=Create(store,new SyntheticProvider());await queue.InitializeAsync(default);store.Fail=true;
        await Assert.ThrowsAsync<IOException>(()=>queue.EnqueueAsync(Profile(),default,new(123,1)));Assert.Empty(store.Last.Operations);
        await Assert.ThrowsAsync<LaunchQueueException>(()=>queue.EnqueueAsync(Profile(),default,new(123,1)));
    }
    [Fact]
    public async Task EnteredProfileIdentityAloneNeverAuthorizesNativeStartup()
    {
        var store=new MemoryStore();var provider=new SyntheticProvider();await using var queue=Create(store,provider);await queue.InitializeAsync(default);
        var row=await queue.EnqueueAsync(Profile(),default);await queue.SetPausedAsync(false,default);
        Assert.Equal(LaunchState.Blocked,row.State);Assert.Equal(LaunchProblem.IdentityUnverified,row.Problem);Assert.Empty(provider.Started);
    }
    [Fact]
    public async Task ReplacedGrantInvalidatesItsQueuedRequestBeforeProviderDispatch()
    {
        var store=new MemoryStore();var provider=new SyntheticProvider();long currentGeneration=2;
        await using var queue=Create(store,provider,validate:(intent,_)=>Task.FromResult(intent.AuthorizationGeneration==currentGeneration));await queue.InitializeAsync(default);
        var row=await queue.EnqueueAsync(Profile(),default,new(123,1));Assert.Equal(1,row.Intent.AuthorizationGeneration);
        await queue.SetPausedAsync(false,default);await WaitFor(()=>store.Last.Operations.Any(o=>o.State==LaunchState.Failed));
        Assert.Equal(LaunchProblem.ProfileChanged,Assert.Single(store.Last.Operations).Problem);Assert.Empty(provider.Started);
    }
    [Fact]
    public async Task ResourcesAreRecheckedAfterAuthorizationWaitsBeforeNativeStart()
    {
        var store=new MemoryStore();var provider=new SyntheticProvider();bool allowed=true;
        await using var queue=Create(store,provider,validate:(_,_)=> {allowed=false;return Task.FromResult(true);},admission:_=>new(allowed,"Synthetic pressure changed"));
        await queue.InitializeAsync(default);await queue.EnqueueAsync(Profile(),default,new(123,1));await queue.SetPausedAsync(false,default);
        await WaitFor(()=>store.Last.Operations.Any(o=>o.Problem==LaunchProblem.InsufficientCapacity));Assert.Empty(provider.Started);
    }
    [Fact]
    public async Task DisconnectDurablyCancelsPendingRequestsAndDoesNotResumeThemOnRelink()
    {
        var store=new MemoryStore();var provider=new SyntheticProvider();await using var queue=Create(store,provider);await queue.InitializeAsync(default);
        var profile=Profile();var old=await queue.EnqueueAsync(profile,default,new(123,1));
        await queue.InvalidateAuthorizationAsync(profile.Id,default);
        Assert.True(store.Last.Paused);Assert.Equal(LaunchState.Cancelled,Assert.Single(store.Last.Operations).State);
        var next=await queue.EnqueueAsync(profile,default,new(123,3));await queue.SetPausedAsync(false,default);
        await WaitFor(()=>store.Last.Operations.Any(o=>o.Intent.OperationId==next.Intent.OperationId && o.State==LaunchState.Arrived));
        Assert.Equal(LaunchState.Cancelled,store.Last.Operations.Single(o=>o.Intent.OperationId==old.Intent.OperationId).State);Assert.Single(provider.Started);
    }
    [Fact]
    public async Task AgingEventuallyOutranksNewHighPriorityRequestsInsteadOfCappingForever()
    {
        var store=new MemoryStore();var provider=new SyntheticProvider();var clock=new SyntheticClock();
        await using var queue=new LaunchCoordinator(store,provider,(_,_)=>Task.FromResult(true),_=>new(true,"Synthetic"),clock,
            startupDeadline:TimeSpan.FromSeconds(2),stabilization:TimeSpan.Zero,stagger:TimeSpan.Zero);
        await queue.InitializeAsync(default);var oldest=Profile("Old low priority");
        await queue.EnqueueAsync(oldest,default,new(123,1));clock.Advance(TimeSpan.FromMinutes(102));
        await queue.EnqueueAsync(Profile("New high priority") with { Priority=100 },default,new(123,1));
        clock.Advance(TimeSpan.FromMinutes(1));await queue.SetPausedAsync(false,default);
        await WaitFor(()=>store.Last.Operations.Count(o=>o.State==LaunchState.Arrived)==2);
        Assert.Equal(oldest.Id,provider.Started.First());
    }
    [Fact]
    public async Task ExplicitPlaceAndServerAreFrozenPerRequestAndOverrideThePreferredPlace()
    {
        var store=new MemoryStore();var provider=new SyntheticProvider();await using var queue=Create(store,provider);await queue.InitializeAsync(default);
        var profile=Profile();var destination=new RobloxRequestedDestination(42,RequestedJob,RobloxServerKind.Public);
        var row=await queue.EnqueueAsync(profile,default,new(123,7),destination);
        destination=new(999,DifferentJob);profile=profile with { PreferredPlaceId=111 };
        Assert.Equal(42,row.Intent.PlaceId);Assert.Equal(RequestedJob,row.Intent.RequestedJobId);
        Assert.True(row.Intent.HasExplicitDestination);Assert.Equal(RobloxServerKind.Public,row.Intent.RequestedServerKind);
        await queue.SetPausedAsync(false,default);await WaitFor(()=>store.Last.Operations.Any(o=>o.State==LaunchState.Arrived));
        var dispatched=Assert.Single(provider.StartedIntents);
        Assert.Equal(row.Intent,dispatched);Assert.Equal(42,dispatched.PlaceId);Assert.Equal(RequestedJob,dispatched.RequestedJobId);
        Assert.Equal(1,provider.Confirmations);
    }
    [Fact]
    public async Task ExplicitPlaceNeedsNoPreferredPlaceAndDoesNotModifyTheProfile()
    {
        var store=new MemoryStore();var provider=new SyntheticProvider();await using var queue=Create(store,provider);await queue.InitializeAsync(default);
        var profile=Profile() with { PreferredPlaceId=null };
        var row=await queue.EnqueueAsync(profile,default,new(123,7),new(42));
        Assert.Equal(LaunchState.Queued,row.State);Assert.Equal(42,row.Intent.PlaceId);Assert.Null(row.Intent.RequestedJobId);
        Assert.Null(profile.PreferredPlaceId);Assert.True(row.Intent.HasExplicitDestination);
        await queue.SetPausedAsync(false,default);await WaitFor(()=>store.Last.Operations.Any(o=>o.State==LaunchState.Arrived));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AClaimedArrivalAtTheWrongOrUnknownJobRemainsUncertain(bool missingJob)
    {
        var store=new MemoryStore();var provider=new SyntheticProvider { MissingJob=missingJob,ForcedObservedJobId=DifferentJob };
        await using var queue=Create(store,provider);await queue.InitializeAsync(default);
        await queue.EnqueueAsync(Profile(),default,new(123,7),new(42,RequestedJob));await queue.SetPausedAsync(false,default);
        await WaitFor(()=>store.Last.Operations.Any(o=>o.State==LaunchState.Uncertain));
        Assert.Equal(LaunchProblem.ArrivalUnverified,Assert.Single(store.Last.Operations).Problem);
        Assert.DoesNotContain(store.Last.Operations,o=>o.State==LaunchState.Arrived);Assert.Equal(0,provider.Confirmations);
    }
    [Fact]
    public async Task UncertainDestinationAccessCannotBecomeSuccessEvenWithMatchingAdvisoryIds()
    {
        var store=new MemoryStore();var provider=new SyntheticProvider { ResultStatus=ProviderLaunchStatus.Uncertain };
        await using var queue=Create(store,provider);await queue.InitializeAsync(default);
        await queue.EnqueueAsync(Profile(),default,new(123,7),new(42,RequestedJob));await queue.SetPausedAsync(false,default);
        await WaitFor(()=>store.Last.Operations.Any(o=>o.State==LaunchState.Uncertain));
        Assert.DoesNotContain(store.Last.Operations,o=>o.State==LaunchState.Arrived);Assert.Equal(0,provider.Confirmations);
    }
    [Fact]
    public async Task PanicDuringExactServerConfirmationCannotTurnALateAcknowledgmentIntoArrival()
    {
        var store=new MemoryStore();var provider=new SyntheticProvider { DelayedConfirmation=new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var queue=Create(store,provider);await queue.InitializeAsync(default);
        var row=await queue.EnqueueAsync(Profile(),default,new(123,7),new(42,RequestedJob));await queue.SetPausedAsync(false,default);
        try
        {
            await WaitFor(()=>provider.Confirmations==1);queue.SignalPanic();
            await WaitFor(()=>store.Last.Operations.Any(o=>o.State==LaunchState.Uncertain));
            provider.DelayedConfirmation.TrySetResult(true);
            Assert.Equal(LaunchProblem.CancelledDuringStartup,Assert.Single(store.Last.Operations).Problem);
            Assert.Equal(row.Intent,Assert.Single(store.Last.Operations).Intent);
            Assert.DoesNotContain(store.Last.Operations,o=>o.State==LaunchState.Arrived);
        }
        finally {provider.DelayedConfirmation.TrySetResult(true);}
    }
    [Fact]
    public async Task PanicDuringEnqueuePersistenceDurablyCancelsTheNewIntentBeforeDispatchCanSeeIt()
    {
        var store=new MemoryStore();var provider=new SyntheticProvider();await using var queue=Create(store,provider);await queue.InitializeAsync(default);
        await queue.SetPausedAsync(false,default);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeSave=async (snapshot,_)=>
        {
            if(snapshot.Operations.Any(o=>o.State==LaunchState.Queued)) {started.TrySetResult();await release.Task;}
        };
        var profile=Profile("Stopped during persistence");
        var pending=queue.EnqueueAsync(profile,default,new(123,1),new(42,RequestedJob));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));queue.SignalPanic();release.TrySetResult();
            var cancelled=await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(LaunchState.Cancelled,cancelled.State);Assert.Equal(cancelled,Assert.Single(store.Last.Operations));Assert.True(store.Last.Paused);
            Assert.Empty(provider.Started);Assert.True(queue.IsHealthy);
            await queue.SetPausedAsync(false,default);
            var fresh=Profile("Fresh request after explicit restart");await queue.EnqueueAsync(fresh,default,new(123,1));
            await WaitFor(()=>store.Last.Operations.Any(o=>o.Intent.ProfileId==fresh.Id && o.State==LaunchState.Arrived));
            Assert.Equal(fresh.Id,Assert.Single(provider.Started));
            Assert.Equal(LaunchState.Cancelled,store.Last.Operations.Single(o=>o.Intent.ProfileId==profile.Id).State);
        }
        finally {release.TrySetResult();}
    }
    [Fact]
    public async Task PanicRejectsAnEnqueueAlreadyWaitingForTheSerialLaneWithoutCreatingAnotherIntent()
    {
        var store=new MemoryStore();var provider=new SyntheticProvider();await using var queue=Create(store,provider);await queue.InitializeAsync(default);
        await queue.SetPausedAsync(false,default);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeSave=async (snapshot,_)=>
        {
            if(snapshot.Operations.Any(o=>o.State==LaunchState.Queued)) {started.TrySetResult();await release.Task;}
        };
        var first=queue.EnqueueAsync(Profile("Owns the serial lane"),default,new(123,1));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var waiting=queue.EnqueueAsync(Profile("Waiting before panic"),default,new(123,1));
            Assert.False(waiting.IsCompleted);queue.SignalPanic();release.TrySetResult();
            Assert.Equal(LaunchState.Cancelled,(await first.WaitAsync(TimeSpan.FromSeconds(2))).State);
            await Assert.ThrowsAsync<LaunchQueueException>(()=>waiting);
            Assert.Equal(LaunchState.Cancelled,Assert.Single(store.Last.Operations).State);
            await queue.SetPausedAsync(false,default);Assert.Empty(provider.Started);
        }
        finally {release.TrySetResult();}
    }
    [Fact]
    public async Task AStorageWriteThatFinishesAfterCallerCancellationReturnsADurablyCancelledIntent()
    {
        var store=new MemoryStore();var provider=new SyntheticProvider();await using var queue=Create(store,provider);await queue.InitializeAsync(default);
        await queue.SetPausedAsync(false,default);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeSave=async (snapshot,_)=>
        {
            if(snapshot.Operations.Any(o=>o.State==LaunchState.Queued)) {started.TrySetResult();await release.Task;}
        };
        using var stop=new CancellationTokenSource();var pending=queue.EnqueueAsync(Profile(),stop.Token,new(123,1));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));stop.Cancel();release.TrySetResult();
            var row=await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(LaunchState.Cancelled,row.State);Assert.Equal(row,Assert.Single(store.Last.Operations));Assert.True(queue.IsHealthy);
            Assert.Empty(provider.Started);
        }
        finally {release.TrySetResult();}
    }
    [Fact]
    public async Task FailureToPersistThePostPanicCancellationStopsTheQueueAndRecoveryRequiresReview()
    {
        var store=new MemoryStore();var provider=new SyntheticProvider();
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using(var queue=Create(store,provider))
        {
            await queue.InitializeAsync(default);
            await queue.SetPausedAsync(false,default);
            store.BeforeSave=async (snapshot,_)=>
            {
                if(snapshot.Operations.Any(o=>o.State==LaunchState.Cancelled)) throw new IOException("Synthetic cancellation cleanup failure");
                if(snapshot.Operations.Any(o=>o.State==LaunchState.Queued)) {started.TrySetResult();await release.Task;}
            };
            var pending=queue.EnqueueAsync(Profile(),default,new(123,1));
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(2));queue.SignalPanic();release.TrySetResult();
                await Assert.ThrowsAsync<IOException>(()=>pending);Assert.False(queue.IsHealthy);Assert.Empty(provider.Started);
            }
            finally {release.TrySetResult();}
        }
        store.BeforeSave=null;
        await using var recovered=Create(store,provider);await recovered.InitializeAsync(default);
        var recoveredRow=Assert.Single(store.Last.Operations);
        Assert.True(store.Last.Paused);Assert.Equal(LaunchState.Blocked,recoveredRow.State);Assert.Equal(LaunchProblem.Interrupted,recoveredRow.Problem);Assert.Empty(provider.Started);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MainAccountLaunchBoostIsTargetedAndCannotStarveSufficientlyOldWork(bool oldWork)
    {
        var store=new MemoryStore();var provider=new SyntheticProvider();var clock=new SyntheticClock();
        var main=Profile("Designated main");var other=Profile("Other") with {Priority=oldWork?0:50};
        await using var queue=new LaunchCoordinator(store,provider,(_,_)=>Task.FromResult(true),_=>new(true,"Synthetic capacity"),clock);
        queue.ConfigurePerformance(new ManagerPerformanceSettings(ManagerPerformanceProfile.MainAccountPriority,main.Id).Resolve());
        await queue.InitializeAsync(default);await queue.EnqueueAsync(other,default,new(123,1));
        if(oldWork) clock.Advance(TimeSpan.FromMinutes(201));
        await queue.EnqueueAsync(main,default,new(123,1));await queue.SetPausedAsync(false,default);
        await WaitFor(()=>provider.Started.Count==1);
        Assert.Equal(oldWork?other.Id:main.Id,provider.Started.Single());
        Assert.All(store.Last.Operations,o=>Assert.Equal(o.Intent.ProfileId==other.Id?other.Priority:main.Priority,o.Intent.Priority));
        queue.SignalPanic();await WaitFor(()=>store.Last.Paused);
    }
    [Fact]
    public async Task SavingANewProfileDoesNotShortenAnActiveStabilizationOrResumePanic()
    {
        var store=new MemoryStore();var provider=new SyntheticProvider();var clock=new TimerClock();var main=Profile("Main");
        await using var queue=new LaunchCoordinator(store,provider,(_,_)=>Task.FromResult(true),_=>new(true,"Synthetic capacity"),clock);
        queue.ConfigurePerformance(new ManagerPerformanceSettings(ManagerPerformanceProfile.MainAccountPriority,main.Id).Resolve());
        await queue.InitializeAsync(default);await queue.EnqueueAsync(main,default,new(123,1));await queue.EnqueueAsync(Profile("Next"),default,new(123,1));
        await queue.SetPausedAsync(false,default);await WaitFor(()=>clock.ActiveTimers>0 && store.Last.Operations.Any(o=>o.State==LaunchState.Stabilizing));
        queue.ConfigurePerformance(new ManagerPerformanceSettings().Resolve());clock.Advance(TimeSpan.FromSeconds(24));
        Assert.Equal(0,Volatile.Read(ref provider.Confirmations));Assert.Single(provider.Started);
        clock.Advance(TimeSpan.FromSeconds(1));await WaitFor(()=>store.Last.Operations.Any(o=>o.State==LaunchState.Arrived));
        queue.SignalPanic();queue.ConfigurePerformance(new ManagerPerformanceSettings(ManagerPerformanceProfile.HighPerformance).Resolve());
        clock.Advance(TimeSpan.FromMinutes(1));queue.Signal();await WaitFor(()=>store.Last.Paused);
        Assert.Single(provider.Started);Assert.True(store.Last.Paused);
    }
    private sealed class TimerClock : TimeProvider
    {
        private readonly object _gate=new();private readonly List<ManualTimer> _timers=[];
        private DateTimeOffset _now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() {lock(_gate) return _now;}
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override long GetTimestamp() {lock(_gate) return _now.UtcTicks;}
        public int ActiveTimers {get {lock(_gate) return _timers.Count(t=>t.Due is not null);}}
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
        {
            var timer=new ManualTimer(this,callback,state);lock(_gate) {_timers.Add(timer);timer.Change(dueTime,period);}return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            List<ManualTimer> due;
            lock(_gate)
            {
                _now+=elapsed;due=_timers.Where(t=>t.Due is { } when && when<=_now).ToList();
                foreach(var timer in due) timer.Due=timer.Period>TimeSpan.Zero?_now+timer.Period:null;
            }
            foreach(var timer in due) timer.Callback(timer.State);
        }
        private sealed class ManualTimer(TimerClock owner,TimerCallback callback,object? state) : ITimer
        {
            public TimerCallback Callback {get;}=callback;public object? State {get;}=state;
            public DateTimeOffset? Due;public TimeSpan Period;
            public bool Change(TimeSpan dueTime,TimeSpan period) {lock(owner._gate) {Due=dueTime==Timeout.InfiniteTimeSpan?null:owner._now+dueTime;Period=period;return true;}}
            public void Dispose() {lock(owner._gate) {Due=null;owner._timers.Remove(this);}}
            public ValueTask DisposeAsync() {Dispose();return ValueTask.CompletedTask;}
        }
    }
    private sealed class SyntheticClock : TimeProvider
    {
        private DateTimeOffset _now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>_now;
        public void Advance(TimeSpan duration)=>_now+=duration;
    }
    private sealed class MemoryStore : ILaunchQueueStore
    {
        private LaunchQueueSnapshot _last=LaunchQueueSnapshot.Empty;
        public LaunchQueueSnapshot Last {get=>Volatile.Read(ref _last);set=>Volatile.Write(ref _last,value);}
        public bool Fail;
        public Func<LaunchQueueSnapshot,CancellationToken,Task>? BeforeSave;
        public Task<LaunchQueueSnapshot> LoadAsync(CancellationToken ct) {ct.ThrowIfCancellationRequested();return Task.FromResult(Last);}
        public async Task SaveAsync(LaunchQueueSnapshot snapshot,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();if(Fail) throw new IOException("Synthetic write failure");if(BeforeSave is not null) await BeforeSave(snapshot,ct);Last=snapshot;}
    }
    private sealed class SyntheticProvider : ILaunchProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<Guid> Started {get;}=new();
        public System.Collections.Concurrent.ConcurrentQueue<LaunchIntent> StartedIntents {get;}=new();
        public bool WrongIdentity,Confirmation=true;
        public bool MissingJob;public string? ForcedObservedJobId;public ProviderLaunchStatus ResultStatus=ProviderLaunchStatus.VerifiedArrival;
        public int Confirmations;
        public TaskCompletionSource<ProviderLaunchResult>? Delayed;
        public TaskCompletionSource<bool>? DelayedConfirmation;
        public ProviderCapability AccountLaunch {get;}=new("Synthetic tests only",CapabilityState.Supported,"No game or native process is launched.");
        public Task<ProviderLaunchResult> LaunchAsync(LaunchIntent i,CancellationToken ct)
        // A synthetic Windows process creation time must still have valid UTC tick semantics.
        {ct.ThrowIfCancellationRequested();Started.Enqueue(i.ProfileId);StartedIntents.Enqueue(i);return Delayed?.Task??Task.FromResult(new ProviderLaunchResult(ResultStatus,WrongIdentity?999:i.UserId,i.PlaceId,123,i.RequestedUtc.AddSeconds(-1).UtcTicks,MissingJob?null:ForcedObservedJobId??i.RequestedJobId));}
        public Task<bool> ConfirmArrivalAsync(LaunchIntent intent,ProviderLaunchResult observed,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();Interlocked.Increment(ref Confirmations);return DelayedConfirmation?.Task??Task.FromResult(Confirmation);}
    }
}
