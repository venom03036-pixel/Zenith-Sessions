namespace Zenith.Sessions.Core;

public enum LaunchState { Queued,Blocked,Launching,Stabilizing,Arrived,Failed,Uncertain,Cancelled,ReviewRequired,Validating,ClientStartedUnverified,Preparing }
public enum LaunchProblem { None,ProviderUnavailable,IdentityUnverified,InsufficientCapacity,StartupRejected,ArrivalUnverified,TimedOut,CancelledDuringStartup,Interrupted,ProfileChanged,ProviderFailure,ClientStartedUnverified,PreparationRejected,PreparationTimedOut,PreparationExpired,PreparationCleanupFailed,PreparationInterrupted,ClientClosureAcknowledged }
// Cached binding data is not authorization. The dispatch validator reopens the
// selected protected record and must match both identity and grant generation.
public sealed record LaunchAccountBinding(long UserId,long AuthorizationGeneration);
// PlaceId is the frozen effective target. Explicit targets never fall back to a changed profile
// preference; only nonsecret place/job values are persisted, never the original link or access codes.
public sealed record LaunchIntent(Guid OperationId,Guid ProfileId,long ProfileRevision,long UserId,long PlaceId,int Priority,DateTimeOffset RequestedUtc,
    long AuthorizationGeneration=0,string? RequestedJobId=null,RobloxServerKind RequestedServerKind=RobloxServerKind.Unknown,bool HasExplicitDestination=false);
/// <summary>
/// A provider-reported process instance only, never account, arrival or current liveness proof.
/// RecordedUtc is when the coordinator retained the receipt, not a fresh process observation.
/// Providers must use an actual process handle/creation identity, never nearest-start timing.
/// BrowserTrackerId, when present, is the validated tracker the native request carried, kept without the
/// request URI. It is not a secret, unlike the ticket, and only corroborates which client log belongs to
/// this launch (a client can restart under a new PID): it proves neither the account nor arrival.
/// It is written only when present, and a queue that holds one is saved as format 8.
/// </summary>
public sealed record LaunchProcessReceipt(int ProcessId,long ProcessStartUtcTicks,DateTimeOffset RecordedUtc,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    long? BrowserTrackerId=null)
{
    /// <summary>The largest tracker the request grammar accepts: 2^53 - 1, the largest integer JavaScript holds exactly.</summary>
    public const long MaximumBrowserTrackerId=9_007_199_254_740_991;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid=>ProcessId>0 && ProcessStartUtcTicks>=DateTime.FromFileTimeUtc(0).Ticks
        && ProcessStartUtcTicks<=DateTime.MaxValue.Ticks && RecordedUtc!=default && ProcessStartUtcTicks<=RecordedUtc.UtcTicks
        && BrowserTrackerId is null or (>0 and <=MaximumBrowserTrackerId);
}
public sealed record LaunchOperation(LaunchIntent Intent,LaunchState State,LaunchProblem Problem,DateTimeOffset UpdatedUtc,
    LaunchProcessReceipt? ProcessReceipt=null,LaunchResourceBudget? ResourceBudget=null)
{
    // Why a waiting row (Blocked, InsufficientCapacity) waits. Never written to the queue file, so the v7 format stays
    // readable by earlier builds; a restart turns every waiting row into a review anyway. Other states ignore it.
    [System.Text.Json.Serialization.JsonIgnore]
    public LaunchHoldReason HoldReason { get; init; }
}
/// <summary>Why the queue paused itself. None covers a user pause, Panic and a restart.</summary>
public enum LaunchQueuePauseReason { None,RepeatedRefusals }
public sealed record LaunchQueueSnapshot(bool Paused,IReadOnlyList<LaunchOperation> Operations)
{
    public static LaunchQueueSnapshot Empty { get; }=new(true,[]);
    // Published with each saved snapshot but never written to the queue file: the v7 format stays
    // readable by earlier builds, and a restart always pauses and recovers never-started rows anyway.
    [System.Text.Json.Serialization.JsonIgnore]
    public LaunchQueuePauseReason PauseReason { get; init; }
}
/// <summary>Hold says why a refused start waits; the queue keeps it on the waiting row for its explanation.</summary>
public sealed record LaunchAdmission(bool Allowed,string Explanation,LaunchHoldReason Hold=LaunchHoldReason.ResourceHeadroom);
public sealed class LaunchQueueException(string message) : Exception(message);
public enum ProviderLaunchStatus { RejectedBeforeStart,Uncertain,VerifiedArrival,ClientStartedUnverified }
// BrowserTrackerId is the non-secret tracker the native request carried; it joins the process receipt only.
public sealed record ProviderLaunchResult(ProviderLaunchStatus Status,long? ObservedUserId=null,long? ObservedPlaceId=null,
    int? ProcessId=null,long? ProcessStartUtcTicks=null,string? ObservedJobId=null,long? BrowserTrackerId=null);

public interface ILaunchProvider
{
    ProviderCapability AccountLaunch { get; }
    // Full, disappeared, inaccessible, or unknown targets cannot produce VerifiedArrival merely
    // because a request/link/HTTP operation succeeded. Return an explicit rejection or uncertainty.
    // Return ClientStartedUnverified with the actual PID/creation pair once startup is known,
    // without waiting indefinitely for an unavailable account/arrival observation. No URI/ticket
    // or reusable credential belongs in this result. Cancellation after creation may still return
    // the receipt: it cannot undo the stop or grant account/arrival authority.
    Task<ProviderLaunchResult> LaunchAsync(LaunchIntent intent,CancellationToken ct);
    // Confirm fresh current account + place + requested job after stabilization. A server-list
    // observation, process start, or old destination observation is not an arrival acknowledgment.
    Task<bool> ConfirmArrivalAsync(LaunchIntent intent,ProviderLaunchResult observed,CancellationToken ct);
}
public interface ILaunchQueueStore
{
    Task<LaunchQueueSnapshot> LoadAsync(CancellationToken ct);
    Task SaveAsync(LaunchQueueSnapshot snapshot,CancellationToken ct);
}

public sealed class UnavailableLaunchProvider : ILaunchProvider
{
    public ProviderCapability AccountLaunch { get; }=new("Native account launch",CapabilityState.Unavailable,
        "A compatible account-selecting native launch provider has not been verified. API linking is not native sign-in.");
    public Task<ProviderLaunchResult> LaunchAsync(LaunchIntent intent,CancellationToken ct)
    { ct.ThrowIfCancellationRequested();return Task.FromResult(new ProviderLaunchResult(ProviderLaunchStatus.RejectedBeforeStart)); }
    public Task<bool> ConfirmArrivalAsync(LaunchIntent intent,ProviderLaunchResult observed,CancellationToken ct)
    { ct.ThrowIfCancellationRequested();return Task.FromResult(false); }
}

public static class LaunchDescriptions
{
    public static string Explain(LaunchProblem problem)=>problem switch
    {
        LaunchProblem.ProviderUnavailable=>"Native account launch provider unavailable; no client was started.",
        LaunchProblem.IdentityUnverified=>"Link and verify the intended account before launching.",
        LaunchProblem.InsufficientCapacity=>"Waiting for fresh measurements and sufficient resource headroom.",
        LaunchProblem.StartupRejected=>"The provider rejected startup before a client was started.",
        LaunchProblem.ArrivalUnverified=>"The correct account and destination were not verified. Reconcile before retrying.",
        LaunchProblem.TimedOut=>"Startup exceeded its deadline. A client may exist; reconcile before retrying.",
        LaunchProblem.CancelledDuringStartup=>"Startup was cancelled. A client may exist; reconcile before retrying.",
        LaunchProblem.Interrupted=>"Recovered queue requires review. Nothing resumed automatically.",
        LaunchProblem.ProfileChanged=>"The saved profile or verified account changed; create a fresh launch request.",
        LaunchProblem.ProviderFailure=>"Provider failed with an uncertain outcome. Reconcile before retrying.",
        LaunchProblem.ClientStartedUnverified=>"A client process was reported started. Its account, current liveness and destination are unverified; review before retrying.",
        LaunchProblem.PreparationRejected=>"Account or destination preparation failed before native startup. Review the saved sign-in and create a fresh request.",
        LaunchProblem.PreparationTimedOut=>"Preparation exceeded its deadline before native startup. Its actual worker must finish cleanup before another attempt.",
        LaunchProblem.PreparationExpired=>"The temporary launch request expired before native startup. Prepare a fresh request.",
        LaunchProblem.PreparationCleanupFailed=>"Launch preparation cleanup could not be confirmed. Preserve the data and close Zenith before retrying.",
        LaunchProblem.PreparationInterrupted=>"Recovered preparation needs review. No client was started by this phase; cancel it and create a fresh request.",
        LaunchProblem.ClientClosureAcknowledged=>"You confirmed the possible client is closed. Its historical receipt is retained; current Windows observations remain separate.",
        _=>""
    };
    /// <summary>A row's explanation. Interrupted means two things: on a Blocked row it was recovered from a
    /// Queued or Blocked save made before any validation, preparation or provider call, so it never started a
    /// client and Cancel is its only action; on a ReviewRequired row a client may exist and Review applies.</summary>
    public static string Explain(LaunchState state,LaunchProblem problem)=>state==LaunchState.Blocked && problem==LaunchProblem.Interrupted
        ?"This request never started a client. Cancel it and queue again if it is still needed.":Explain(problem);
    public static string ExplainPause(LaunchQueuePauseReason reason)=>reason switch
    {
        // Cause-neutral: a provider's Rejected answer also covers local faults, a busy provider, an
        // untrusted client and a closed trial page, so the queue cannot name one cause.
        LaunchQueuePauseReason.RepeatedRefusals=>"Queue paused: two starts in a row ended before any client started. The remaining requests stay queued. Check each failed request's details in the launch queue (sign-in, client trust, resources), then resume. Until a client starts or 10 minutes pass, one more such start pauses the queue again.",
        _=>""
    };
    public static bool Terminal(LaunchState state)=>state is LaunchState.Failed or LaunchState.Cancelled;
    /// <summary>A request held only for resource headroom. The runner re-selects it beside Queued rows and
    /// starts it on its own once admission allows; every other Blocked row needs the user first.</summary>
    public static bool AwaitsCapacity(LaunchOperation operation)=>operation.State==LaunchState.Blocked && operation.Problem==LaunchProblem.InsufficientCapacity;
    /// <summary>A waiting row held because starting the selected player would let Roblox's update close running clients.</summary>
    public static bool AwaitsClientUpdate(LaunchOperation operation)=>AwaitsCapacity(operation) && operation.HoldReason==LaunchHoldReason.ClientUpdate;
    /// <summary>The row's explanation: its problem's, or the update hold's for a row waiting on a Roblox update.</summary>
    public static string Explain(LaunchOperation operation)=>AwaitsClientUpdate(operation)?ClientUpdateHold.Explanation:Explain(operation.State,operation.Problem);
    public static bool NeedsReservation(LaunchState state)=>state is LaunchState.Validating or LaunchState.Launching or LaunchState.Stabilizing or LaunchState.Uncertain or LaunchState.ReviewRequired or LaunchState.ClientStartedUnverified;
    public static bool HasUnresolvedReservation(LaunchOperation operation)=>NeedsReservation(operation.State)
        || operation.ProcessReceipt is {IsValid:true} && !(operation.State==LaunchState.Cancelled && operation.Problem==LaunchProblem.ClientClosureAcknowledged);
}

/// <summary>One deterministic queue owner. Platform adapters supply identity, admission and native launch.</summary>
public sealed partial class LaunchCoordinator : IAsyncDisposable
{
    private const int MaximumPendingOperations = 1000;
    private readonly ILaunchQueueStore _store;
    private readonly ILaunchProvider _provider;
    private readonly Func<LaunchIntent,CancellationToken,Task<bool>> _validate;
    private readonly Func<IReadOnlyList<LaunchOperation>,LaunchAdmission> _admit;
    private readonly Func<IReadOnlyList<LaunchOperation>,IReadOnlySet<Guid>>? _observedReservations;
    private readonly TimeProvider _time;
    private readonly TimeSpan _startupDeadline,_stabilization,_stagger,_preparationDeadline;
    private ManagerPerformancePolicy? _performance;
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private readonly System.Threading.Channels.Channel<byte> _wake=System.Threading.Channels.Channel.CreateBounded<byte>(new System.Threading.Channels.BoundedChannelOptions(1) { FullMode=System.Threading.Channels.BoundedChannelFullMode.DropWrite,SingleReader=true });
    private readonly List<LaunchOperation> _operations=[];
    private CancellationTokenSource? _activeStop;
    private Task<ProviderLaunchResult>? _providerWork;
    private LaunchIntent? _providerIntent;
    private Task<bool>? _validationWork;
    private Task<LaunchPreparationResult>? _preparationWork;
    private TaskCompletionSource? _activeCompletion;
    private Task _runner=Task.CompletedTask;
    private Task? _dispose;
    private volatile bool _paused=true,_unhealthy;
    private bool _initialized;
    private long _generation;
    private long _persistedStopGeneration;
    public event Action<LaunchQueueSnapshot>? Changed;
    public event Action<Exception>? Faulted;
    public string ProviderExplanation=>_provider.AccountLaunch.Explanation;
    public bool IsHealthy=>_initialized && !_unhealthy && !_stop.IsCancellationRequested;
    /// <summary>True once InitializeAsync has opened the queue. InitializeAsync never opens it a
    /// second time, so an opened queue that stops after a fault reopens only after a restart.</summary>
    public bool Opened=>_initialized;
    // Immutable publication does not resume a paused/stopped queue or change an active start.
    public void ConfigurePerformance(ManagerPerformancePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Volatile.Write(ref _performance,policy.Validated());Signal();
    }
    public LaunchCoordinator(ILaunchQueueStore store,ILaunchProvider provider,
        Func<LaunchIntent,CancellationToken,Task<bool>> validate,Func<IReadOnlyList<LaunchOperation>,LaunchAdmission> admit,
        TimeProvider? time=null,TimeSpan? startupDeadline=null,TimeSpan? stabilization=null,TimeSpan? stagger=null,TimeSpan? preparationDeadline=null,
        Func<IReadOnlyList<LaunchOperation>,IReadOnlySet<Guid>>? observedReservations=null,
        Func<LaunchIntent,LaunchResourceBudget>? budgetResolver=null,
        Func<LaunchOperation,IReadOnlyList<LaunchOperation>,LaunchAdmission>? candidateAdmission=null,
        Func<TimeSpan>? refusalJitter=null)
    {
        _store=store;_provider=provider;_validate=validate;_admit=admit;_time=time??TimeProvider.System;_observedReservations=observedReservations;
        _budgetResolver=budgetResolver;_candidateAdmission=candidateAdmission;_refusalJitter=refusalJitter??RandomRefusalJitter;
        _startupDeadline=startupDeadline??TimeSpan.FromSeconds(90);_stabilization=stabilization??TimeSpan.FromSeconds(20);_stagger=stagger??TimeSpan.FromSeconds(2);
        _preparationDeadline=preparationDeadline??TimeSpan.FromMinutes(4);
        if(_startupDeadline<=TimeSpan.Zero || _stabilization<TimeSpan.Zero || _stagger<TimeSpan.Zero || _preparationDeadline<=TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(startupDeadline));
        if(_stabilization>MaximumPacingStabilization)throw new ArgumentOutOfRangeException(nameof(stabilization));
        if(_stagger>MaximumPacingStagger)throw new ArgumentOutOfRangeException(nameof(stagger));
    }
    public async Task InitializeAsync(CancellationToken ct)
    {
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(_initialized) return;
            // A failed startup never started the runner, so nothing else owns these rows or this
            // fault. Rebuild from disk: an explicit retry must neither append the saved rows a
            // second time nor inherit the failed attempt's storage fault.
            _operations.Clear();_unhealthy=false;
            var saved=await _store.LoadAsync(ct).ConfigureAwait(false);ValidateSnapshot(saved);
            foreach(var operation in saved.Operations) _operations.Add(RecoverAfterRestart(operation,_time.GetUtcNow()));
            _paused=true;_pauseReason=LaunchQueuePauseReason.None;await PersistAsync(ct).ConfigureAwait(false);_initialized=true;_runner=RunAsync(_stop.Token);
        }
        finally {_serial.Release();}
    }
    // Nothing resumes automatically. Only a phase that may have reached native startup becomes
    // a reserving possible client that needs closure review.
    private static LaunchOperation RecoverAfterRestart(LaunchOperation operation,DateTimeOffset now)
    {
        if(LaunchDescriptions.Terminal(operation.State)) return operation;
        if(operation.State==LaunchState.Preparing || operation.State==LaunchState.Blocked&&operation.Problem==LaunchProblem.PreparationInterrupted)
            return operation with { State=LaunchState.Blocked,Problem=LaunchProblem.PreparationInterrupted,UpdatedUtc=now };
        // Queued and Blocked are saved before any validation, preparation or provider call and
        // can never carry a receipt, so such a row never started a client. Keep it cancellable,
        // non-reserving and never auto-selected. A backup generation, which a lost primary may
        // have moved on, reaches here as ReviewRequired from the store instead.
        if(operation.State is LaunchState.Queued or LaunchState.Blocked)
            return operation with { State=LaunchState.Blocked,Problem=LaunchProblem.Interrupted,UpdatedUtc=now };
        // A started client with its exact PID/creation receipt and pinned budget is already a
        // reserving, reviewable possible client. Keep it as saved so the same fresh exact
        // observation that clears the single-start barrier within a session can reconcile it
        // after a restart too (CAP-14-03). This establishes no account or arrival, and the
        // queue still restarts paused. Uncertain and legacy unbudgeted rows still need review.
        if(operation is {State:LaunchState.ClientStartedUnverified,ProcessReceipt.IsValid:true,ResourceBudget.IsValid:true})
            return operation;
        return operation with { State=LaunchState.ReviewRequired,Problem=LaunchProblem.Interrupted,UpdatedUtc=now };
    }
    public Task<LaunchOperation> EnqueueAsync(AccountProfile profile,CancellationToken ct,LaunchAccountBinding? binding=null,
        RobloxRequestedDestination? requestedDestination=null)=>EnqueueOwnedAsync(profile,ct,binding,requestedDestination,false);
    private async Task<LaunchOperation> EnqueueOwnedAsync(AccountProfile profile,CancellationToken ct,LaunchAccountBinding? binding,
        RobloxRequestedDestination? requestedDestination,bool trial)
    {
        long generation=Interlocked.Read(ref _generation);
        profile.Validated();await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireHealthy();
            if(trial && (_provider is not IPreparingLaunchProvider || _provider.AccountLaunch.State!=CapabilityState.Unverified
                || binding is not {UserId:>0,AuthorizationGeneration:>0} || requestedDestination is null))
                throw new LaunchQueueException("A native trial needs an unverified preparing provider, a saved account binding and an explicit destination.");
            if(generation!=Interlocked.Read(ref _generation)) throw new LaunchQueueException("The launch queue was stopped while this request was waiting. Choose a fresh request after reviewing the stop.");
            ct.ThrowIfCancellationRequested();
            if(_operations.Any(o=>o.Intent.ProfileId==profile.Id && IsPendingOrUnresolved(o))) throw new LaunchQueueException("This account already has pending or unresolved launch work.");
            if(_operations.Count(IsPendingOrUnresolved)>=MaximumPendingOperations) throw new LaunchQueueException("The pending launch queue is full. Existing operations were preserved.");
            if(binding is { UserId:<=0 } or { AuthorizationGeneration:<=0 }) throw new ArgumentException("A launch binding must identify a positive verified user and authorization generation.");
            // Copy only validated nonsecret values. The original link and opaque access/share codes
            // never enter the queue; later profile edits or UI selections cannot retarget this request.
            var destination=requestedDestination is null?null:new RobloxRequestedDestination(requestedDestination.PlaceId,requestedDestination.JobId,requestedDestination.ServerKind);
            var now=_time.GetUtcNow();var intent=new LaunchIntent(Guid.NewGuid(),profile.Id,profile.Revision,binding?.UserId??0,
                destination?.PlaceId??profile.PreferredPlaceId??0,profile.Priority,now,binding?.AuthorizationGeneration??0,
                destination?.JobId,destination?.ServerKind??RobloxServerKind.Unknown,destination is not null);
            var operation=CreateQueuedOperation(intent,trial,ct);
            if(generation!=Interlocked.Read(ref _generation)) throw new LaunchQueueException("The launch queue was stopped before this request could be committed. Choose a fresh request after reviewing the stop.");
            ct.ThrowIfCancellationRequested();
            TrimHistory();
            if(trial)_trialOperations[intent.OperationId]=generation;
            // Once the row is added, own its write through durable completion (as EnqueueBatchAsync
            // does). A caller stop during the write is a user stop, not a storage fault: cancelling
            // the save would leave memory ahead of disk and mark the whole queue unhealthy.
            int index=_operations.Count;_operations.Add(operation);await PersistAsync(CancellationToken.None).ConfigureAwait(false);
            // A stop is immediate even while queue storage owns the serial lane, so durably fence
            // the just-created intent before releasing the lane or waking a dispatcher. Existing
            // queued work is preserved.
            if(generation!=Interlocked.Read(ref _generation) || ct.IsCancellationRequested || _stop.IsCancellationRequested)
            {
                operation=operation with { State=LaunchState.Cancelled,Problem=LaunchProblem.None,UpdatedUtc=_time.GetUtcNow() };
                _operations[index]=operation;
                await PersistAsync(CancellationToken.None).ConfigureAwait(false);
            }
            Signal();return operation;
        }
        finally {_serial.Release();}
    }

    private LaunchOperation CreateQueuedOperation(LaunchIntent intent,bool trial,CancellationToken ct)
    {
        LaunchProblem issue=!trial && _provider.AccountLaunch.State!=CapabilityState.Supported?LaunchProblem.ProviderUnavailable:
            intent.UserId<=0 || intent.AuthorizationGeneration<=0 || intent.PlaceId<=0?LaunchProblem.IdentityUnverified:LaunchProblem.None;
        return new(intent,issue==LaunchProblem.None?LaunchState.Queued:LaunchState.Blocked,issue,intent.RequestedUtc,
            ResourceBudget:ResolveResourceBudget(intent,ct));
    }

    private static bool IsPendingOrUnresolved(LaunchOperation operation)
        =>!LaunchDescriptions.Terminal(operation.State)||LaunchDescriptions.HasUnresolvedReservation(operation);
    public async Task SetPausedAsync(bool paused,CancellationToken ct)
    {
        if(!paused) { await TryResumeAsync(ct).ConfigureAwait(false);return; }
        // Cancel first, before waiting for disk I/O or the queue lock.
        Interlocked.Increment(ref _generation);CancelActive();
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try { RequireHealthy();_pauseReason=LaunchQueuePauseReason.None;_paused=true;await PersistAsync(ct).ConfigureAwait(false);Signal(); }
        finally {_serial.Release();}
    }
    /// <summary>An explicit resume cannot overtake a stop issued after this call began.</summary>
    public async Task<bool> TryResumeAsync(CancellationToken ct)
    {
        long generation=Interlocked.Read(ref _generation);
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireHealthy();ct.ThrowIfCancellationRequested();
            if(generation!=Interlocked.Read(ref _generation)) return false;
            _pauseReason=LaunchQueuePauseReason.None;_paused=false;
            // Panic does not wait for this owner. Close its check/write race before any await;
            // no queue runner can enter the serial boundary until the stop is restored.
            if(generation!=Interlocked.Read(ref _generation)) {_paused=true;return false;}
            await PersistAsync(ct).ConfigureAwait(false);
            // A write may finish its pre-Panic snapshot. Panic's existing wake persists the
            // stopped state; that stale publication cannot accept or signal this resume.
            bool accepted=generation==Interlocked.Read(ref _generation)&&!_paused&&!_unhealthy&&!_stop.IsCancellationRequested;
            if(accepted) Signal();
            return accepted;
        }
        finally {_serial.Release();}
    }
    public void SignalPanic()
    { Interlocked.Increment(ref _generation);_pauseReason=LaunchQueuePauseReason.None;_paused=true;CancelActive();Signal(); }
    public async Task InvalidateAuthorizationAsync(Guid profileId,CancellationToken ct)
    {
        if(profileId==Guid.Empty) throw new ArgumentException("A saved profile is required.");
        SignalPanic();
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireHealthy();
            for(int i=0;i<_operations.Count;i++)
                if(_operations[i].Intent.ProfileId==profileId && _operations[i].State is LaunchState.Queued or LaunchState.Blocked)
                    _operations[i]=_operations[i] with { State=LaunchState.Cancelled,Problem=LaunchProblem.ProfileChanged,UpdatedUtc=_time.GetUtcNow() };
            await PersistAsync(ct).ConfigureAwait(false);
        }
        finally {_serial.Release();}
    }
    private void CancelActive()
    {
        var active=Volatile.Read(ref _activeStop);
        try {active?.Cancel();}
        catch(ObjectDisposedException) when(!ReferenceEquals(active,Volatile.Read(ref _activeStop))) { /* The fenced session already finished. */ }
    }
    private void EndActive()=>Interlocked.Exchange(ref _activeStop,null)?.Dispose();
    public void Signal()=>_wake.Writer.TryWrite(0);
    public async Task CancelAsync(Guid id,CancellationToken ct)
    {
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireHealthy();int index=_operations.FindIndex(o=>o.Intent.OperationId==id);if(index<0) throw new KeyNotFoundException("Launch operation was not found.");
            var operation=_operations[index];
            if(operation.State is LaunchState.Preparing or LaunchState.Validating or LaunchState.Launching or LaunchState.Stabilizing) {CancelActive();return;}
            if(operation.State is LaunchState.Uncertain or LaunchState.ReviewRequired or LaunchState.Arrived or LaunchState.ClientStartedUnverified) throw new LaunchQueueException("Review this possible client before dismissing its launch reservation.");
            if(LaunchDescriptions.Terminal(operation.State)) return;
            _operations[index]=operation with { State=LaunchState.Cancelled,Problem=LaunchProblem.None,UpdatedUtc=_time.GetUtcNow() };await PersistAsync(ct).ConfigureAwait(false);
        }
        finally {_serial.Release();}
    }
    public async Task AcknowledgeClientClosedAsync(Guid id,CancellationToken ct)
    {
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireHealthy();
            if(_providerWork is { IsCompleted:false } || _validationWork is { IsCompleted:false } || _preparationWork is { IsCompleted:false }) throw new LaunchQueueException("The previous provider operation is still stopping. Keep its ownership until it stops.");
            var operation=_operations.FirstOrDefault(o=>o.Intent.OperationId==id)??throw new KeyNotFoundException("Launch operation was not found.");
            if(operation.State is not (LaunchState.Uncertain or LaunchState.ReviewRequired or LaunchState.Arrived or LaunchState.ClientStartedUnverified)
                && !(LaunchDescriptions.Terminal(operation.State)&&LaunchDescriptions.HasUnresolvedReservation(operation))) throw new LaunchQueueException("This operation does not need a client-closure review.");
            // Application must obtain the user's explicit confirmation first. This
            // is recorded manual reconciliation, never an inferred successful exit.
            await UpdateAsync(operation,LaunchState.Cancelled,LaunchProblem.ClientClosureAcknowledged,ct).ConfigureAwait(false);Signal();
        }
        finally {_serial.Release();}
    }
    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while(await _wake.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while(_wake.Reader.TryRead(out _)) { }
                await PersistPendingPauseAsync(ct).ConfigureAwait(false);
                try{await StartNextAsync(ct).ConfigureAwait(false);}
                finally{Interlocked.Exchange(ref _activeCompletion,null)?.TrySetResult();}
            }
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
        catch(Exception ex) {_paused=true;_unhealthy=true;Faulted?.Invoke(ex);}
    }
    private async Task PersistPendingPauseAsync(CancellationToken ct)
    {
        if(!_paused || Interlocked.Read(ref _generation)==Interlocked.Read(ref _persistedStopGeneration)) return;
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(_paused && Interlocked.Read(ref _generation)!=Interlocked.Read(ref _persistedStopGeneration))
                await PersistAsync(ct).ConfigureAwait(false);
        }
        finally {_serial.Release();}
    }
    private async Task StartNextAsync(CancellationToken ct)
    {
        LaunchOperation? chosen=null;long generation=Interlocked.Read(ref _generation);
        TimeSpan stabilization=_stabilization,stagger=_stagger;
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(_paused || _unhealthy || !ReadPacing().Ready || HasUnreconciledStart()) return;
            var policy=Volatile.Read(ref _performance);
            if(policy is not null) {stabilization=TimeSpan.FromMilliseconds(policy.StabilizationMs);stagger=TimeSpan.FromMilliseconds(policy.StaggerMs);}
            chosen=_operations.Where(o=>o.State==LaunchState.Queued || LaunchDescriptions.AwaitsCapacity(o))
                .OrderByDescending(o=>o.Intent.Priority+Math.Max(0,(_time.GetUtcNow()-o.Intent.RequestedUtc).TotalMinutes)
                    +(policy is {MainAccountProfileId:{ } main} && main==o.Intent.ProfileId?policy.MainAccountPriorityBoost:0))
                .ThenBy(o=>o.Intent.RequestedUtc).ThenBy(o=>o.Intent.OperationId).FirstOrDefault();
            if(chosen is null) return;
            if(!ProviderPermits(chosen.Intent.OperationId)) { await UpdateAsync(chosen,LaunchState.Blocked,LaunchProblem.ProviderUnavailable,ct).ConfigureAwait(false);return; }
            if(chosen.Intent.UserId<=0 || chosen.Intent.AuthorizationGeneration<=0) { await UpdateAsync(chosen,LaunchState.Blocked,LaunchProblem.IdentityUnverified,ct).ConfigureAwait(false);return; }
            chosen=await PinLegacyResourceBudgetAsync(chosen,generation,ct).ConfigureAwait(false);
            if(chosen is null)return;
            // A cheap fresh preflight is not a reservation. Preparing remains excluded from
            // NeedsReservation while a user-owned page may wait; reserve only at Launching.
            // A row already waiting keeps its saved state: the Shell signals after every monitor sample, and
            // re-saving an unchanged hold would fsync and republish once a second. Only a changed reason is saved.
            var admission=AdmissionForStart(_operations,chosen.Intent.OperationId);
            if(!admission.Allowed)
            {
                if(!LaunchDescriptions.AwaitsCapacity(chosen) || chosen.HoldReason!=admission.Hold)
                    await UpdateAsync(chosen,LaunchState.Blocked,LaunchProblem.InsufficientCapacity,ct,hold:admission.Hold).ConfigureAwait(false);
                return;
            }
            if(_paused || generation!=Interlocked.Read(ref _generation)) return;
            _activeStop=CancellationTokenSource.CreateLinkedTokenSource(ct);
            _activeCompletion=new(TaskCreationOptions.RunContinuationsAsynchronously);
            _preparationWork=null;_providerWork=null;_providerIntent=null;_validationWork=null;
            await UpdateAsync(chosen,_provider is IPreparingLaunchProvider?LaunchState.Preparing:LaunchState.Validating,LaunchProblem.None,ct).ConfigureAwait(false);
        }
        finally {_serial.Release();}
        if(chosen is null) return;
        if(_provider is IPreparingLaunchProvider preparing)
        {await PrepareAndStartAsync(chosen,preparing,generation,stabilization,stagger,ct).ConfigureAwait(false);return;}
        bool valid=false;LaunchProblem validationProblem=LaunchProblem.None;
        try
        {
            _validationWork=_validate(chosen.Intent,_activeStop!.Token);
            valid=await _validationWork.WaitAsync(_startupDeadline,_time,_activeStop.Token).ConfigureAwait(false);
        }
        catch(TimeoutException) {validationProblem=LaunchProblem.TimedOut;CancelActive();}
        catch(OperationCanceledException) {validationProblem=LaunchProblem.CancelledDuringStartup;}
        catch(Exception ex) {validationProblem=LaunchProblem.ProfileChanged;Faulted?.Invoke(ex);}
        if(!valid || validationProblem!=LaunchProblem.None || _paused || generation!=Interlocked.Read(ref _generation))
        {
            bool cancelled=validationProblem==LaunchProblem.CancelledDuringStartup || _paused || generation!=Interlocked.Read(ref _generation);
            await ChangeAsync(chosen,cancelled?LaunchState.Cancelled:LaunchState.Failed,
                cancelled?LaunchProblem.None:validationProblem==LaunchProblem.None?LaunchProblem.ProfileChanged:validationProblem).ConfigureAwait(false);
            if(!await DrainAsync(_validationWork,ct).ConfigureAwait(false)) return;
            EndActive();Signal();return;
        }
        // Authorization may have waited on storage/network. Recheck resources and
        // the stop fence immediately before committing to native startup.
        bool dispatch=false;
        await _serial.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if(_paused || generation!=Interlocked.Read(ref _generation) || _activeStop!.IsCancellationRequested)
                await UpdateAsync(chosen,LaunchState.Cancelled,LaunchProblem.None,CancellationToken.None).ConfigureAwait(false);
            else if(AdmissionForStart(_operations,chosen.Intent.OperationId) is {Allowed:false} admission)
                await UpdateAsync(chosen,LaunchState.Blocked,LaunchProblem.InsufficientCapacity,CancellationToken.None,hold:admission.Hold).ConfigureAwait(false);
            else { await UpdateAsync(chosen,LaunchState.Launching,LaunchProblem.None,CancellationToken.None).ConfigureAwait(false);dispatch=true; }
        }
        finally {_serial.Release();}
        if(!dispatch) {EndActive();return;}
        await DispatchOwnedAsync(chosen,generation,stabilization,stagger,ct).ConfigureAwait(false);
    }
    private async Task DispatchOwnedAsync(LaunchOperation chosen,long generation,TimeSpan stabilization,TimeSpan stagger,
        CancellationToken ct,IPreparedAccountLaunch? prepared=null)
    {
        // Preparation may have waited while the monotonic clock became unavailable. It must
        // not bypass the same gate simply because its queue reservation is already present.
        if(!ReadPacing().Ready)
        {await ChangeAsync(chosen,LaunchState.Blocked,LaunchProblem.InsufficientCapacity).ConfigureAwait(false);EndActive();return;}
        LaunchDispatchBoundary? boundary=null;
        if(prepared is not null)
        {
            LaunchOperation[] reserved;
            await _serial.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try {reserved=_operations.ToArray();}
            finally {_serial.Release();}
            var activeToken=_activeStop!.Token;
            boundary=new(chosen.Intent,_validate,()=>!_paused&&!_unhealthy&&!activeToken.IsCancellationRequested
                &&generation==Interlocked.Read(ref _generation)&&!_stop.IsCancellationRequested&&ProviderPermits(chosen.Intent.OperationId)
                &&ReadPacing().Ready,()=>AdmissionForStart(reserved,chosen.Intent.OperationId));
        }
        using var closeBoundary=boundary;
        ProviderLaunchResult? result=null;LaunchProblem problem=LaunchProblem.None;bool unverifiedPacing=false,refusalPacing=false;
        try
        {
            _activeStop!.Token.ThrowIfCancellationRequested();
            _providerIntent=chosen.Intent;
            _providerWork=prepared is null?_provider.LaunchAsync(chosen.Intent,_activeStop!.Token):prepared.DispatchAsync(boundary!,_activeStop!.Token);
            result=await _providerWork.WaitAsync(_startupDeadline,_time,_activeStop.Token).ConfigureAwait(false);
            if(_trialOperations.ContainsKey(chosen.Intent.OperationId)&&result?.Status==ProviderLaunchStatus.VerifiedArrival)
                result=result with{Status=ProviderLaunchStatus.ClientStartedUnverified,ObservedUserId=null,ObservedPlaceId=null,ObservedJobId=null};
        }
        catch(TimeoutException) {problem=LaunchProblem.TimedOut;CancelActive();}
        catch(OperationCanceledException) {problem=LaunchProblem.CancelledDuringStartup;}
        catch(Exception) {problem=LaunchProblem.ProviderFailure;ReportProviderFault();}
        await _serial.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            bool hasReceipt=RecordReceiptOwned(chosen.Intent,result);
            if(_paused || generation!=Interlocked.Read(ref _generation) || problem==LaunchProblem.None && _activeStop!.IsCancellationRequested) problem=LaunchProblem.CancelledDuringStartup;
            if(problem!=LaunchProblem.None || result?.Status==ProviderLaunchStatus.Uncertain)
                await UpdateAsync(chosen,LaunchState.Uncertain,problem==LaunchProblem.None?LaunchProblem.ArrivalUnverified:problem,CancellationToken.None).ConfigureAwait(false);
            else if(result?.Status==ProviderLaunchStatus.RejectedBeforeStart)
            {
                bool processClaim=result.ProcessId is not null || result.ProcessStartUtcTicks is not null;
                // Any rejection with no process claim counts, whatever its cause (provider refusal or a
                // local boundary denial): it lengthens the next gap, and a second in a row pauses.
                if(!processClaim) {RecordRefusalOwned(chosen.Intent.OperationId,stagger);refusalPacing=true;}
                await UpdateAsync(chosen,processClaim?LaunchState.Uncertain:LaunchState.Failed,
                    processClaim?LaunchProblem.ProviderFailure:LaunchProblem.StartupRejected,CancellationToken.None).ConfigureAwait(false);
            }
            else if(result?.Status==ProviderLaunchStatus.ClientStartedUnverified)
            {
                ForgetRefusalsOwned();
                if(hasReceipt)
                {
                    BeginPacing(chosen.Intent.OperationId,stabilization>=stagger?stabilization:stagger,
                        stabilization>=stagger?LaunchPacingReason.StartupStabilization:LaunchPacingReason.BetweenStarts);
                    unverifiedPacing=true;
                }
                await UpdateAsync(chosen,hasReceipt?LaunchState.ClientStartedUnverified:LaunchState.Uncertain,
                    hasReceipt?LaunchProblem.ClientStartedUnverified:LaunchProblem.ArrivalUnverified,CancellationToken.None).ConfigureAwait(false);
            }
            else if(hasReceipt && MatchesRequestedArrival(chosen.Intent,result))
            {
                ForgetRefusalsOwned();
                await UpdateAsync(chosen,LaunchState.Stabilizing,LaunchProblem.None,CancellationToken.None).ConfigureAwait(false);
            }
            else await UpdateAsync(chosen,LaunchState.Uncertain,LaunchProblem.ArrivalUnverified,CancellationToken.None).ConfigureAwait(false);
        }
        finally {_serial.Release();}
        // A provider that ignores cancellation retains the lane until its work ends.
        if(!await DrainAsync(_providerWork,ct).ConfigureAwait(false)) return;
        await CaptureLateReceiptAsync(chosen.Intent,_providerWork).ConfigureAwait(false);
        _providerWork=null;_providerIntent=null;
        bool stabilizing;
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try {stabilizing=_operations.Any(o=>o.Intent.OperationId==chosen.Intent.OperationId && o.State==LaunchState.Stabilizing);}
        finally {_serial.Release();}
        if(stabilizing)
        {
            bool confirmed=false;
            var arrivalOwner=_activeStop!;
            try
            {
                BeginPacing(chosen.Intent.OperationId,stabilization,LaunchPacingReason.StartupStabilization);
                await Task.Delay(stabilization,_time,arrivalOwner.Token).ConfigureAwait(false);
                _validationWork=_provider.ConfirmArrivalAsync(chosen.Intent,result!,arrivalOwner.Token);
                confirmed=await _validationWork.WaitAsync(_startupDeadline,_time,arrivalOwner.Token).ConfigureAwait(false);
            }
            catch(OperationCanceledException) {problem=LaunchProblem.CancelledDuringStartup;}
            catch(TimeoutException) {problem=LaunchProblem.TimedOut;CancelActive();}
            catch(Exception) {problem=LaunchProblem.ProviderFailure;ReportProviderFault();}
            confirmed=confirmed && MatchesRequestedArrival(chosen.Intent,result);
            await CompleteArrivalAsync(chosen,generation,arrivalOwner,confirmed,problem).ConfigureAwait(false);
            if(!await DrainAsync(_validationWork,ct).ConfigureAwait(false)) return;
        }
        EndActive();if(!unverifiedPacing&&!refusalPacing)BeginPacing(chosen.Intent.OperationId,stagger,LaunchPacingReason.BetweenStarts);Signal();
    }
    private async Task CompleteArrivalAsync(LaunchOperation operation,long generation,CancellationTokenSource owner,
        bool confirmed,LaunchProblem problem)
    {
        await _serial.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            int index=_operations.FindIndex(row=>row.Intent.OperationId==operation.Intent.OperationId);
            if(index<0 || _operations[index].Intent!=operation.Intent || _operations[index].State!=LaunchState.Stabilizing
                || !ReferenceEquals(owner,Volatile.Read(ref _activeStop))) return;
            // A confirmation timeout also cancels provider work. Retain that cause unless
            // a separate queue stop superseded this operation.
            bool Stopped()=>_paused || _unhealthy || _stop.IsCancellationRequested || generation!=Interlocked.Read(ref _generation)
                || problem==LaunchProblem.None && owner.IsCancellationRequested;
            if(Stopped()) problem=LaunchProblem.CancelledDuringStartup;
            if(!confirmed || problem!=LaunchProblem.None)
            {
                await UpdateAsync(operation,LaunchState.Uncertain,problem==LaunchProblem.None?LaunchProblem.ArrivalUnverified:problem,
                    CancellationToken.None).ConfigureAwait(false);
                return;
            }
            await UpdateAsync(operation,LaunchState.Arrived,LaunchProblem.None,CancellationToken.None,notify:false).ConfigureAwait(false);
            // A stop may arrive during storage or inside an observer. Check before each
            // notification, then persist uncertainty before releasing the queue owner.
            PublishWhileCurrent(Stopped);
            if(Stopped())
                await UpdateAsync(operation,LaunchState.Uncertain,LaunchProblem.CancelledDuringStartup,
                    CancellationToken.None).ConfigureAwait(false);
        }
        finally {_serial.Release();}
    }
    private async Task<bool> DrainAsync(Task? work,CancellationToken ct)
    {
        if(work is null) return true;
        if(work.IsCompleted) {if(work.IsFaulted) _=work.Exception;return true;}
        try {await work.WaitAsync(ct).ConfigureAwait(false);return true;}
        catch(OperationCanceledException) when(ct.IsCancellationRequested) {return false;}
        catch(OperationCanceledException) {return true;}
        catch(Exception) {ReportProviderFault();return true;}
    }
    private void ReportProviderFault()=>Faulted?.Invoke(new LaunchQueueException("The launch provider failed. Diagnostic details were withheld to protect handoff material; reconcile before retrying."));
    // Caller owns _serial. A receipt is a fact about this one provider task's frozen intent;
    // it never changes launch state, releases a reservation or establishes account identity.
    private bool RecordReceiptOwned(LaunchIntent expected,ProviderLaunchResult? result)
    {
        if(result?.ProcessId is not { } pid || result.ProcessStartUtcTicks is not { } start) return false;
        // An out-of-range tracker is dropped, never allowed to cost the process receipt itself.
        long? tracker=result.BrowserTrackerId is >0 and <=LaunchProcessReceipt.MaximumBrowserTrackerId?result.BrowserTrackerId:null;
        var receipt=new LaunchProcessReceipt(pid,start,_time.GetUtcNow(),tracker);
        if(!receipt.IsValid) return false;
        int index=_operations.FindIndex(o=>o.Intent.OperationId==expected.OperationId);
        if(index<0 || _operations[index].Intent!=expected) return false;
        var current=_operations[index];
        if(current.State is LaunchState.Queued or LaunchState.Blocked or LaunchState.Preparing or LaunchState.Validating or LaunchState.Failed) return false;
        if(current.ProcessReceipt is { } existing)
            return existing.ProcessId==pid && existing.ProcessStartUtcTicks==start;
        _operations[index]=current with {ProcessReceipt=receipt,UpdatedUtc=receipt.RecordedUtc};
        return true;
    }
    private async Task CaptureLateReceiptAsync(LaunchIntent expected,Task<ProviderLaunchResult>? work)
    {
        if(work is not {IsCompletedSuccessfully:true}) return;
        var result=work.Result;
        await _serial.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            int index=_operations.FindIndex(o=>o.Intent.OperationId==expected.OperationId);
            if(index<0 || _operations[index].Intent!=expected) return;
            var before=_operations[index];
            // Preserve current Cancelled/Uncertain/ReviewRequired state and the current pause
            // generation. The old chosen operation must not overwrite newer durable changes.
            if(RecordReceiptOwned(expected,result) && _operations[index]!=before)
                await PersistAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally {_serial.Release();}
    }
    private async Task ChangeAsync(LaunchOperation operation,LaunchState state,LaunchProblem problem)
    {
        await _serial.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try {await UpdateAsync(operation,state,problem,CancellationToken.None).ConfigureAwait(false);}
        finally {_serial.Release();}
    }
    // Every transition sets the hold reason: only a refused start passes one, so a row leaving a wait never keeps it.
    private async Task UpdateAsync(LaunchOperation operation,LaunchState state,LaunchProblem problem,CancellationToken ct,bool notify=true,
        LaunchHoldReason hold=LaunchHoldReason.ResourceHeadroom)
    {
        int index=_operations.FindIndex(o=>o.Intent.OperationId==operation.Intent.OperationId);
        if(index<0) throw new InvalidOperationException("The owned launch operation disappeared.");
        var current=_operations[index];
        if(current.Intent!=operation.Intent) throw new InvalidOperationException("The owned launch intent changed.");
        var updated=_time.GetUtcNow();
        if(current.ProcessReceipt is { } receipt && receipt.RecordedUtc>updated) updated=receipt.RecordedUtc;
        _operations[index]=current with { State=state,Problem=problem,UpdatedUtc=updated,HoldReason=hold };await PersistAsync(ct,notify).ConfigureAwait(false);
    }
    private async Task PersistAsync(CancellationToken ct,bool notify=true)
    {
        long stopGeneration=Interlocked.Read(ref _generation);
        var snapshot=Snapshot();
        try
        {
            await _store.SaveAsync(snapshot,ct).ConfigureAwait(false);
            if(snapshot.Paused) Interlocked.Exchange(ref _persistedStopGeneration,stopGeneration);
            if(notify) Changed?.Invoke(snapshot);
        }
        catch(Exception ex) {_unhealthy=true;_paused=true;CancelActive();Faulted?.Invoke(ex);throw;}
    }
    private void PublishWhileCurrent(Func<bool> stopped)
    {
        if(Changed is not { } handlers || stopped()) return;
        var snapshot=Snapshot();
        try
        {
            foreach(Action<LaunchQueueSnapshot> observer in handlers.GetInvocationList())
            {
                if(stopped()) break;
                observer(snapshot);
            }
        }
        catch(Exception ex) {_unhealthy=true;_paused=true;CancelActive();Faulted?.Invoke(ex);throw;}
    }
    private LaunchQueueSnapshot Snapshot()=>new(_paused,_operations.ToArray()) { PauseReason=_paused?_pauseReason:LaunchQueuePauseReason.None };
    private void RequireHealthy()
    { if(!_initialized || _unhealthy || _stop.IsCancellationRequested) throw new LaunchQueueException("The launch queue needs recovery or has stopped. Existing queue files were preserved."); }
    private void TrimHistory()
    {
        int remove = _operations.Count(operation => !IsPendingOrUnresolved(operation)) - 100;
        if (remove <= 0) return;
        var expired = _operations.Where(operation => !IsPendingOrUnresolved(operation)).OrderBy(operation => operation.UpdatedUtc)
            .Take(remove).Select(operation => operation.Intent.OperationId).ToHashSet();
        _operations.RemoveAll(operation => expired.Contains(operation.Intent.OperationId));
        foreach (Guid operation in expired) _trialOperations.TryRemove(operation, out _);
    }
    public static void ValidateSnapshot(LaunchQueueSnapshot snapshot)
    {
        if(snapshot.Operations is null || snapshot.Operations.Count>1101 || snapshot.Operations.Any(o=>o is null || o.Intent is null)
            || snapshot.Operations.Select(o=>o.Intent.OperationId).Distinct().Count()!=snapshot.Operations.Count) throw new InvalidDataException("The saved launch queue is invalid; preserve it for recovery.");
        foreach(var o in snapshot.Operations)
            if(o.Intent.OperationId==Guid.Empty || o.Intent.ProfileId==Guid.Empty || o.Intent.ProfileRevision<0 || o.Intent.UserId<0 || o.Intent.PlaceId<0 || o.Intent.AuthorizationGeneration<0 || o.Intent.Priority is <0 or >100
                || !HasValidDestination(o.Intent,requirePlace:false) || !Enum.IsDefined(o.State) || !Enum.IsDefined(o.Problem)
                || o.ResourceBudget is {IsValid:false}
                || o.State==LaunchState.ClientStartedUnverified && o.ProcessReceipt is null
                || o.Problem==LaunchProblem.ClientClosureAcknowledged && o.State!=LaunchState.Cancelled
                || o.ProcessReceipt is { } receipt && (!receipt.IsValid || receipt.RecordedUtc>o.UpdatedUtc
                    || o.State is LaunchState.Queued or LaunchState.Blocked or LaunchState.Preparing or LaunchState.Validating or LaunchState.Failed))
                throw new InvalidDataException("The saved launch queue contains an invalid operation.");
    }
    public static bool HasValidDestination(LaunchIntent intent,bool requirePlace=true)
    {
        if(intent.PlaceId<0 || (requirePlace || intent.HasExplicitDestination) && intent.PlaceId<=0
            || !Enum.IsDefined(intent.RequestedServerKind)) return false;
        if(!intent.HasExplicitDestination) return intent.RequestedJobId is null && intent.RequestedServerKind==RobloxServerKind.Unknown;
        if(intent.RequestedJobId is null) return intent.RequestedServerKind==RobloxServerKind.Unknown;
        return RobloxRequestedDestination.TryNormalizeJobId(intent.RequestedJobId,out var normalized) && intent.RequestedJobId==normalized;
    }
    private static bool MatchesRequestedArrival(LaunchIntent intent,ProviderLaunchResult? observed) => observed is { Status:ProviderLaunchStatus.VerifiedArrival,ProcessId:>0,ProcessStartUtcTicks:>0 }
        && observed.ObservedUserId==intent.UserId && observed.ObservedPlaceId==intent.PlaceId
        && (intent.RequestedJobId is null || RobloxRequestedDestination.TryNormalizeJobId(observed.ObservedJobId,out var normalized) && normalized==intent.RequestedJobId);
    public ValueTask DisposeAsync()=>new(_dispose??=StopAsync());
    private async Task StopAsync()
    {
        SignalPanic();_stop.Cancel();_wake.Writer.TryComplete();
        bool finished=false;
        try
        {
            // Preparation owns browser/native cleanup that has no hard-cancel contract. Its
            // observation timeout must not complete disposal before the actual runner exits.
            if(_provider is IPreparingLaunchProvider)await _runner.ConfigureAwait(false);
            else await _runner.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            foreach(var work in new Task?[] {_providerWork,_validationWork})
            {
                if(work is not { IsCompleted:false }) continue;
                try {await work.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);}
                catch(OperationCanceledException) { }
                catch(Exception ex) when(ex is not TimeoutException) {ReportProviderFault();}
            }
            if(_providerIntent is { } intent) await CaptureLateReceiptAsync(intent,_providerWork).ConfigureAwait(false);
            // A caller may still own a queue write after the runner exits. Let that owner finish
            // its stop correction. Other callers can still enter the lane to receive the closed state.
            await _serial.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            _serial.Release();
            finished=true;
        }
        catch(OperationCanceledException) { }
        catch(TimeoutException) { throw new InvalidOperationException("A native launch provider has not stopped; preserve the unresolved operation and restart before launching again."); }
        finally {if(finished) {EndActive();_stop.Dispose();}}
    }
}
