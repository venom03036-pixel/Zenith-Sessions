using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// Controlled queue lifecycle only. No browser, account, native process or real capacity claims.
public sealed class LaunchPreparationQueueTests
{
    private const string Secret="synthetic-ticket-must-never-be-reported";
    private static AccountProfile Profile()=>AccountProfile.Create("Synthetic prepared account") with{Revision=10,PreferredPlaceId=1818};
    private static LaunchCoordinator Queue(Store store,Provider provider,Func<LaunchIntent,CancellationToken,Task<bool>>? validate=null,
        Func<IReadOnlyList<LaunchOperation>,LaunchAdmission>? admit=null,TimeProvider? clock=null)
        =>new(store,provider,validate??((_,_)=>Task.FromResult(true)),admit??(_=>new(true,"Synthetic capacity")),clock,
            startupDeadline:TimeSpan.FromSeconds(1),stabilization:TimeSpan.Zero,stagger:TimeSpan.Zero,preparationDeadline:TimeSpan.FromSeconds(1));

    [Fact]
    public async Task PreparationAndFinalValidationHoldNoReservationThenOnlyTheOwnedFinalIntentIsDispatched()
    {
        var store=new Store();var provider=new Provider();var capture=Gate();var captured=Gate();var validation=Gate();var validating=Gate();
        provider.Prepare=async(context,_)=>{captured.TrySetResult();await capture.Task;return provider.Ready(context);};
        await using var queue=Queue(store,provider,async(intent,_)=>{Assert.Equal(9,intent.AuthorizationGeneration);validating.TrySetResult();await validation.Task;return true;});
        await queue.InitializeAsync(default);var request=await queue.EnqueueAsync(Profile(),default,new(123,7),new(1818,SyntheticHandoff.Job,RobloxServerKind.Public));
        await queue.SetPausedAsync(false,default);
        try
        {
            await captured.Task.WaitAsync(TimeSpan.FromSeconds(3));var row=Assert.Single(store.Last.Operations);
            Assert.Equal(LaunchState.Preparing,row.State);Assert.False(LaunchDescriptions.NeedsReservation(row.State));Assert.Equal(7,row.Intent.AuthorizationGeneration);
            capture.TrySetResult();await validating.Task.WaitAsync(TimeSpan.FromSeconds(3));
            row=Assert.Single(store.Last.Operations);Assert.Equal(LaunchState.Preparing,row.State);Assert.False(LaunchDescriptions.NeedsReservation(row.State));
            Assert.Equal(9,row.Intent.AuthorizationGeneration);Assert.Equal(11,row.Intent.ProfileRevision);Assert.Equal(request.Intent.OperationId,row.Intent.OperationId);
        }
        finally{capture.TrySetResult();validation.TrySetResult();}
        await Until(()=>provider.Last?.Disposed==1);
        var final=Assert.Single(store.Last.Operations);Assert.Equal(LaunchState.ClientStartedUnverified,final.State);
        Assert.Equal(SyntheticHandoff.Job,final.Intent.RequestedJobId);Assert.Equal(1,provider.Last!.Starts);Assert.Equal(0,provider.LegacyCalls);
        Assert.Contains(store.Saves,s=>s.Operations.Any(o=>o.State==LaunchState.Launching&&LaunchDescriptions.NeedsReservation(o.State)));
        Assert.NotNull(final.ProcessReceipt);Assert.False(provider.Last.Boundary!.IsCurrent);
    }

    [Theory]
    [InlineData("operation")] [InlineData("profile")] [InlineData("user")] [InlineData("place")]
    [InlineData("job")] [InlineData("priority")] [InlineData("time")] [InlineData("explicit")]
    [InlineData("revision-back")] [InlineData("revision-jump")] [InlineData("generation-back")] [InlineData("generation-one")]
    [InlineData("generation-jump")] [InlineData("context")]
    public async Task NoOtherFieldOrUnownedPreparationMayRebaseAQueuedRequest(string change)
    {
        var store=new Store();var provider=new Provider();provider.Prepare=(context,_)=>Task.FromResult(Change());
        LaunchPreparationResult Change()
        {
            var context=provider.Context!;var result=provider.Ready(context);var handle=provider.Last!;var original=handle.DispatchIntent;
            handle.Intent=change switch
            {
                "operation"=>original with{OperationId=Guid.NewGuid()},"profile"=>original with{ProfileId=Guid.NewGuid()},
                "user"=>original with{UserId=999},"place"=>original with{PlaceId=999},"job"=>original with{RequestedJobId=SyntheticHandoff.Job,RequestedServerKind=RobloxServerKind.Public},
                "priority"=>original with{Priority=90},"time"=>original with{RequestedUtc=original.RequestedUtc.AddSeconds(1)},
                "explicit"=>original with{HasExplicitDestination=true},"revision-back"=>original with{ProfileRevision=9},
                "revision-jump"=>original with{ProfileRevision=12},"generation-back"=>original with{AuthorizationGeneration=6},
                "generation-one"=>original with{AuthorizationGeneration=8},"generation-jump"=>original with{AuthorizationGeneration=10},_=>original
            };
            if(change=="context")handle.Owner=new(context.OriginalIntent);return result;
        }
        await using var queue=Queue(store,provider);await queue.InitializeAsync(default);var request=await queue.EnqueueAsync(Profile(),default,new(123,7));await queue.SetPausedAsync(false,default);
        await Until(()=>provider.Last?.Disposed==1);var row=Assert.Single(store.Last.Operations);
        Assert.Equal(LaunchState.Failed,row.State);Assert.Equal(LaunchProblem.PreparationRejected,row.Problem);Assert.Equal(request.Intent,row.Intent);
        Assert.Equal(0,provider.Last!.Starts);Assert.False(LaunchDescriptions.NeedsReservation(row.State));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExpiryBeforeOrAfterFinalValidationNeverInvokesNativeDispatch(bool afterValidation)
    {
        var store=new Store();var provider=new Provider();provider.Prepare=(context,_)=>{var result=provider.Ready(context);provider.Last!.Expired=!afterValidation;return Task.FromResult(result);};
        await using var queue=Queue(store,provider,(_,_)=>{provider.Last!.Expired=true;return Task.FromResult(true);});
        await queue.InitializeAsync(default);await queue.EnqueueAsync(Profile(),default,new(123,7));await queue.SetPausedAsync(false,default);
        await Until(()=>provider.Last?.Disposed==1);Assert.Equal(LaunchProblem.PreparationExpired,Assert.Single(store.Last.Operations).Problem);Assert.Equal(0,provider.Last!.Starts);
    }

    [Fact]
    public async Task APreparedIntentIsSnapshottedOnceRatherThanRereadDuringAdoption()
    {
        var store=new Store();var provider=new Provider();provider.Prepare=(context,_)=>{var result=provider.Ready(context);provider.Last!.ThrowOnSecondIntentRead=true;return Task.FromResult(result);};
        await using var queue=Queue(store,provider);await queue.InitializeAsync(default);await queue.EnqueueAsync(Profile(),default,new(123,7));await queue.SetPausedAsync(false,default);
        await Until(()=>provider.Last?.Disposed==1);Assert.Equal(1,provider.Last!.Starts);Assert.Equal(1,provider.Last.IntentReads);
        Assert.Equal(9,Assert.Single(store.Last.Operations).Intent.AuthorizationGeneration);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FinalValidationOrCapacityRefusalDiscardsTheEphemeralHandleWithoutStarting(bool capacity)
    {
        var store=new Store();var provider=new Provider();int admissions=0;
        await using var queue=Queue(store,provider,(_,_)=>Task.FromResult(capacity),_=>new(++admissions==1||!capacity,"Synthetic fresh capacity"));
        await queue.InitializeAsync(default);await queue.EnqueueAsync(Profile(),default,new(123,7));await queue.SetPausedAsync(false,default);
        await Until(()=>provider.Last?.Disposed==1);Assert.Equal(0,provider.Last!.Starts);
        var row=Assert.Single(store.Last.Operations);Assert.Equal(capacity?LaunchState.Blocked:LaunchState.Failed,row.State);
        Assert.Equal(capacity?LaunchProblem.InsufficientCapacity:LaunchProblem.ProfileChanged,row.Problem);Assert.False(LaunchDescriptions.NeedsReservation(row.State));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ManualStopOrPanicOwnsAndDisposesALateReadyHandleBeforeAnotherPreparation(bool panic)
    {
        var store=new Store();var provider=new Provider();var entered=Gate();var release=Gate();
        provider.Prepare=async(context,_)=>{entered.TrySetResult();await release.Task;return provider.Ready(context);};
        await using var queue=Queue(store,provider);await queue.InitializeAsync(default);var request=await queue.EnqueueAsync(Profile(),default,new(123,7));await queue.SetPausedAsync(false,default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));if(panic)queue.SignalPanic();else await queue.CancelAsync(request.Intent.OperationId,default);
            await Until(()=>store.Last.Operations.Any(o=>o.State==LaunchState.Cancelled));
            Assert.Equal(1,provider.Preparations);Assert.Null(provider.Last);Assert.False(LaunchDescriptions.NeedsReservation(Assert.Single(store.Last.Operations).State));
            await Assert.ThrowsAsync<LaunchQueueException>(()=>queue.AcknowledgeClientClosedAsync(request.Intent.OperationId,default));
        }
        finally{release.TrySetResult();}
        await Until(()=>provider.Last?.Disposed==1);Assert.Equal(0,provider.Last!.Starts);Assert.Equal(7,Assert.Single(store.Last.Operations).Intent.AuthorizationGeneration);
        Assert.Equal(panic,store.Last.Paused);
    }

    [Fact]
    public async Task PreparationTimeoutPublishesAPreStartFailureButStillJoinsAndDisposesTheActualLateResult()
    {
        var store=new Store();var provider=new Provider();var clock=new Clock();var entered=Gate();var release=Gate();
        provider.Prepare=async(context,_)=>{entered.TrySetResult();await release.Task;return provider.Ready(context);};
        await using var queue=Queue(store,provider,clock:clock);await queue.InitializeAsync(default);await queue.EnqueueAsync(Profile(),default,new(123,7));await queue.SetPausedAsync(false,default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));await Until(()=>clock.Timers>0);clock.Advance(TimeSpan.FromSeconds(2));
            await Until(()=>store.Last.Operations.Any(o=>o.State==LaunchState.Failed));
            var row=Assert.Single(store.Last.Operations);Assert.Equal(LaunchProblem.PreparationTimedOut,row.Problem);Assert.False(LaunchDescriptions.NeedsReservation(row.State));
            await queue.EnqueueAsync(Profile(),default,new(124,7));queue.Signal();Assert.Equal(1,provider.Preparations);
        }
        finally{queue.SignalPanic();release.TrySetResult();}
        await Until(()=>provider.Last?.Disposed==1);Assert.Equal(0,provider.Last!.Starts);Assert.Equal(1,provider.Preparations);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RecoveringAnInterruptedPreparationNeverClaimsAClientReservationOrReplaysIt(bool alreadyRecovered)
    {
        var store=new Store();var provider=new Provider();var profile=Profile();var intent=new LaunchIntent(Guid.NewGuid(),profile.Id,10,123,1818,0,DateTimeOffset.UtcNow,7);
        store.Last=new(false,[new(intent,alreadyRecovered?LaunchState.Blocked:LaunchState.Preparing,alreadyRecovered?LaunchProblem.PreparationInterrupted:LaunchProblem.None,DateTimeOffset.UtcNow)]);
        await using var queue=Queue(store,provider);await queue.InitializeAsync(default);Assert.True(store.Last.Paused);
        var row=Assert.Single(store.Last.Operations);Assert.Equal(LaunchState.Blocked,row.State);Assert.Equal(LaunchProblem.PreparationInterrupted,row.Problem);
        Assert.False(LaunchDescriptions.NeedsReservation(row.State));await queue.SetPausedAsync(false,default);queue.Signal();Assert.Equal(0,provider.Preparations);
        await queue.CancelAsync(intent.OperationId,default);Assert.Equal(LaunchState.Cancelled,Assert.Single(store.Last.Operations).State);
    }

    [Fact]
    public async Task ALateReportedCleanupFailureOverridesCancellationAndBlocksFurtherQueueUse()
    {
        var store=new Store();var provider=new Provider();var entered=Gate();var release=Gate();
        provider.Prepare=async(_,_)=>{entered.TrySetResult();await release.Task;return new(LaunchPreparationFailure.CleanupFailed);};
        await using var queue=Queue(store,provider);await queue.InitializeAsync(default);var request=await queue.EnqueueAsync(Profile(),default,new(123,7));await queue.SetPausedAsync(false,default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));await queue.CancelAsync(request.Intent.OperationId,default);
            await Until(()=>store.Last.Operations.Any(o=>o.State==LaunchState.Cancelled));
        }
        finally{release.TrySetResult();}
        await Until(()=>!queue.IsHealthy&&store.Last.Paused&&store.Last.Operations.Any(o=>o.Problem==LaunchProblem.PreparationCleanupFailed));
        var row=Assert.Single(store.Last.Operations);Assert.Equal(LaunchState.Failed,row.State);Assert.False(LaunchDescriptions.NeedsReservation(row.State));
        await Assert.ThrowsAsync<LaunchQueueException>(()=>queue.EnqueueAsync(Profile(),default,new(124,7)));Assert.Equal(1,provider.Preparations);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PreparingAndItsRecoveredReviewExposeCancelRatherThanClientClosureReview(bool recovered)
    {
        var profile=Profile();var intent=new LaunchIntent(Guid.NewGuid(),profile.Id,10,123,1818,0,DateTimeOffset.UtcNow,7);
        var row=new Zenith.Sessions.App.LaunchRow(new(intent,recovered?LaunchState.Blocked:LaunchState.Preparing,
            recovered?LaunchProblem.PreparationInterrupted:LaunchProblem.None,DateTimeOffset.UtcNow));
        Assert.Equal(recovered?"Review preparation":"Preparing account",row.State);Assert.True(row.CanCancel);Assert.False(row.CanReview);
        Assert.Null(row.Operation.ProcessReceipt);Assert.False(LaunchDescriptions.NeedsReservation(row.Operation.State));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FinalBoundarySeesStopOrCapacityChangesAfterTheReservationWasSaved(bool panic)
    {
        var store=new Store();var provider=new Provider();LaunchCoordinator? owner=null;bool deny=false;
        store.BeforeSave=s=>{if(s.Operations.Any(o=>o.State==LaunchState.Launching)){if(panic)owner!.SignalPanic();else deny=true;}return Task.CompletedTask;};
        await using var queue=Queue(store,provider,admit:_=>new(!deny,"Synthetic changed resource check"));owner=queue;
        await queue.InitializeAsync(default);await queue.EnqueueAsync(Profile(),default,new(123,7));await queue.SetPausedAsync(false,default);
        await Until(()=>provider.Last?.Disposed==1);Assert.Equal(0,provider.Last!.Starts);Assert.False(provider.Last.Boundary?.IsCurrent??false);
    }

    [Fact]
    public async Task ADispatchThatCompletesAfterStopRetainsItsOriginalReceiptAndReservation()
    {
        var store=new Store();var provider=new Provider();var entered=Gate();var release=Gate();
        provider.Prepare=(context,_)=>{var result=provider.Ready(context);provider.Last!.Run=async(_,_)=>{entered.TrySetResult();await release.Task;return Receipt();};return Task.FromResult(result);};
        await using var queue=Queue(store,provider);await queue.InitializeAsync(default);await queue.EnqueueAsync(Profile(),default,new(123,7));await queue.SetPausedAsync(false,default);
        try{await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));queue.SignalPanic();await Until(()=>store.Last.Operations.Any(o=>o.State==LaunchState.Uncertain));}
        finally{release.TrySetResult();}
        await Until(()=>provider.Last?.Disposed==1);var row=Assert.Single(store.Last.Operations);
        Assert.Equal(LaunchState.Uncertain,row.State);Assert.True(LaunchDescriptions.NeedsReservation(row.State));Assert.NotNull(row.ProcessReceipt);
        Assert.Equal(9,row.Intent.AuthorizationGeneration);Assert.Equal(11,row.Intent.ProfileRevision);Assert.True(store.Last.Paused);
    }

    [Theory]
    [InlineData("prepare")] [InlineData("property")] [InlineData("validation")] [InlineData("cleanup")]
    public async Task ArbitraryPreparationErrorsAreRedactedAndCannotEnableAnotherLaunch(string at)
    {
        var store=new Store();var provider=new Provider();var errors=new ConcurrentQueue<Exception>();
        provider.Prepare=(context,_)=>
        {
            if(at=="prepare")throw new IOException(Secret);var result=provider.Ready(context);
            if(at=="property")provider.Last!.PropertyError=true;
            if(at=="cleanup"){provider.Last!.Expired=true;provider.Last.OnDispose=()=>throw new IOException(Secret);}return Task.FromResult(result);
        };
        await using var queue=Queue(store,provider,(_,_)=>at=="validation"?throw new IOException(Secret):Task.FromResult(true));queue.Faulted+=errors.Enqueue;
        await queue.InitializeAsync(default);await queue.EnqueueAsync(Profile(),default,new(123,7));await queue.SetPausedAsync(false,default);
        await Until(()=>store.Last.Operations.Any(o=>o.State==LaunchState.Failed));if(provider.Last is not null)await Until(()=>provider.Last.Disposed==1);
        Assert.All(errors,e=>Assert.DoesNotContain(Secret,e.ToString()));Assert.DoesNotContain(Secret,JsonSerializer.Serialize(store.Last));
        if(at=="cleanup"){await Until(()=>!queue.IsHealthy&&store.Last.Paused);Assert.True(store.Last.Paused);}
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ShutdownKeepsTheSameActualOwnerBeyondTheOldFiveSecondObservationLimit(bool cleanup)
    {
        var store=new Store();var provider=new Provider();var entered=Gate();var release=Gate();
        provider.Prepare=async(context,_)=>
        {
            if(!cleanup){entered.TrySetResult();await release.Task;}
            var result=provider.Ready(context);
            if(cleanup){provider.Last!.Expired=true;provider.Last.OnDispose=async()=>{entered.TrySetResult();await release.Task;};}
            return result;
        };
        var queue=Queue(store,provider);await queue.InitializeAsync(default);await queue.EnqueueAsync(Profile(),default,new(123,7));await queue.SetPausedAsync(false,default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));var shutdown=queue.DisposeAsync().AsTask();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5.3));Assert.False(shutdown.IsCompleted);Assert.Same(shutdown,queue.DisposeAsync().AsTask());
        }
        finally{release.TrySetResult();await shutdown.WaitAsync(TimeSpan.FromSeconds(4));}
        Assert.Equal(1,provider.Last!.Disposed);Assert.Equal(0,provider.Last.Starts);
    }

    private static TaskCompletionSource Gate()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Until(Func<bool> condition)
    {using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(4));while(!condition())await Task.Delay(5,deadline.Token);}
    private static ProviderLaunchResult Receipt()=>new(ProviderLaunchStatus.ClientStartedUnverified,ProcessId:987654,ProcessStartUtcTicks:DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks);
    private sealed class Store:ILaunchQueueStore
    {
        private LaunchQueueSnapshot _last=LaunchQueueSnapshot.Empty;public LaunchQueueSnapshot Last{get=>Volatile.Read(ref _last);set=>Volatile.Write(ref _last,value);}
        public ConcurrentQueue<LaunchQueueSnapshot> Saves=[];public Func<LaunchQueueSnapshot,Task>? BeforeSave;
        public Task<LaunchQueueSnapshot> LoadAsync(CancellationToken ct)=>Task.FromResult(Last);
        public async Task SaveAsync(LaunchQueueSnapshot snapshot,CancellationToken ct){if(BeforeSave is not null)await BeforeSave(snapshot);Last=snapshot;Saves.Enqueue(snapshot);}
    }
    private sealed class Provider:IPreparingLaunchProvider
    {
        public ProviderCapability AccountLaunch{get;}=new("Synthetic queue provider",CapabilityState.Supported,"Controlled test only");
        public Func<LaunchPreparationContext,CancellationToken,Task<LaunchPreparationResult>>? Prepare;
        public Handle? Last;public LaunchPreparationContext? Context;public int Preparations,LegacyCalls;
        public Task<LaunchPreparationResult> PrepareAsync(LaunchPreparationContext context,CancellationToken ct)
        {Preparations++;Context=context;return Prepare?.Invoke(context,ct)??Task.FromResult(Ready(context));}
        public LaunchPreparationResult Ready(LaunchPreparationContext context)
        {Last=new(context,context.OriginalIntent with{ProfileRevision=context.OriginalIntent.ProfileRevision+1,AuthorizationGeneration=context.OriginalIntent.AuthorizationGeneration+2});return new(LaunchPreparationFailure.None,Last);}
        public Task<ProviderLaunchResult> LaunchAsync(LaunchIntent intent,CancellationToken ct){LegacyCalls++;throw new InvalidOperationException("Legacy dispatch must not run.");}
        public Task<bool> ConfirmArrivalAsync(LaunchIntent intent,ProviderLaunchResult result,CancellationToken ct)=>Task.FromResult(false);
    }
    private sealed class Handle(LaunchPreparationContext owner,LaunchIntent intent):IPreparedAccountLaunch
    {
        public LaunchPreparationContext Owner=owner;public LaunchIntent Intent=intent;public bool Expired,PropertyError,ThrowOnSecondIntentRead;public int Starts,Disposed,IntentReads;
        public LaunchPreparationContext Context=>Owner;
        public LaunchIntent DispatchIntent{get{IntentReads++;return PropertyError||ThrowOnSecondIntentRead&&IntentReads>1?throw new IOException(Secret):Intent;}}
        public bool IsExpired=>Expired;public LaunchDispatchBoundary? Boundary;
        public Func<LaunchDispatchBoundary,CancellationToken,Task<ProviderLaunchResult>>? Run;public Func<Task>? OnDispose;
        public async Task<ProviderLaunchResult> DispatchAsync(LaunchDispatchBoundary boundary,CancellationToken ct)
        {
            Boundary=boundary;if(!boundary.IsCurrent||!boundary.ReadAdmission().Allowed||!await boundary.ValidateAsync(Intent,ct))return new(ProviderLaunchStatus.RejectedBeforeStart);
            Starts++;return Run is null?Receipt():await Run(boundary,ct);
        }
        public async ValueTask DisposeAsync(){Interlocked.Increment(ref Disposed);if(OnDispose is not null)await OnDispose();}
    }
    private sealed class Clock:TimeProvider
    {
        private readonly object _gate=new();private DateTimeOffset _now=DateTimeOffset.UtcNow;private readonly List<Timer> _timers=[];
        public override DateTimeOffset GetUtcNow(){lock(_gate)return _now;}public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override long GetTimestamp(){lock(_gate)return _now.UtcTicks;}public int Timers{get{lock(_gate)return _timers.Count(t=>t.Due is not null);}}
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
        {var timer=new Timer(this,callback,state);lock(_gate){_timers.Add(timer);timer.Change(dueTime,period);}return timer;}
        public void Advance(TimeSpan delta)
        {Timer[] due;lock(_gate){_now+=delta;due=_timers.Where(t=>t.Due<=_now).ToArray();foreach(var timer in due)timer.Due=null;}foreach(var timer in due)timer.Callback(timer.State);}
        private sealed class Timer(Clock owner,TimerCallback callback,object? state):ITimer
        {
            public TimerCallback Callback=callback;public object? State=state;public DateTimeOffset? Due;
            public bool Change(TimeSpan dueTime,TimeSpan period){lock(owner._gate){Due=dueTime==Timeout.InfiniteTimeSpan?null:owner._now+dueTime;return true;}}
            public void Dispose(){lock(owner._gate){Due=null;owner._timers.Remove(this);}}public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
        }
    }
}
