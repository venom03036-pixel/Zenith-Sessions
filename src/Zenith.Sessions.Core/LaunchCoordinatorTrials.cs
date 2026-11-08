using System.Collections.Concurrent;

namespace Zenith.Sessions.Core;

public sealed partial class LaunchCoordinator
{
    // Explicit development trial consent is memory-only, tied to this exact request and stop
    // epoch. A saved queue, general Resume, or a provider's Unverified label grants nothing.
    private readonly ConcurrentDictionary<Guid,long> _trialOperations=new();
    public Task<LaunchOperation> EnqueueTrialAsync(AccountProfile profile,CancellationToken ct,LaunchAccountBinding binding,
        RobloxRequestedDestination destination)=>EnqueueOwnedAsync(profile,ct,binding,destination,true);
    public bool IsTrialOperation(Guid operationId)=>_trialOperations.TryGetValue(operationId,out long generation)
        &&generation==Interlocked.Read(ref _generation);
    // Test-only seam: runs after a trial resume passes its consent check and before it commits,
    // the window an unserialized Panic can land in. Production never sets it.
    internal Action? TrialResumeCheckedForTest;
    public async Task<bool> ResumeTrialAsync(Guid operationId,CancellationToken ct)
    {
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireHealthy();ct.ThrowIfCancellationRequested();
            if(!_trialOperations.TryGetValue(operationId,out long generation)||generation!=Interlocked.Read(ref _generation)
                ||!_operations.Any(row=>row.Intent.OperationId==operationId&&row.State==LaunchState.Queued))return false;
            TrialResumeCheckedForTest?.Invoke();
            _pauseReason=LaunchQueuePauseReason.None;_paused=false;
            // Panic does not wait for this owner. Close its check/write race before any await, as
            // TryResumeAsync does; no queue runner can enter the serial boundary until the stop is restored.
            if(generation!=Interlocked.Read(ref _generation)){_paused=true;return false;}
            await PersistAsync(ct).ConfigureAwait(false);
            // A Panic during the write leaves its own wake to save the stop; only an unrevoked resume signals.
            bool accepted=generation==Interlocked.Read(ref _generation)&&!_paused&&!_unhealthy&&!_stop.IsCancellationRequested;
            if(accepted)Signal();
            return accepted;
        }
        finally{_serial.Release();}
    }
    public async Task WaitForActiveCleanupAsync(CancellationToken ct)
    {
        Task completion;
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try{completion=_activeCompletion?.Task??Task.CompletedTask;}
        finally{_serial.Release();}
        await completion.WaitAsync(ct).ConfigureAwait(false);
    }
    private bool ProviderPermits(LaunchIntent intent)=>(intent.PrivateServer is null || IsTrialOperation(intent.OperationId))
        &&(_provider.AccountLaunch.State==CapabilityState.Supported
        ||_provider is IPreparingLaunchProvider&&_provider.AccountLaunch.State==CapabilityState.Unverified&&IsTrialOperation(intent.OperationId));

    // Called only by the serialized queue owner. An exact fresh OS observation may remove
    // the single-start uncertainty barrier, never the resource forecast or account/arrival gate.
    private bool HasUnreconciledStart(IReadOnlyList<LaunchOperation>? snapshot=null)
    {
        var current=snapshot??_operations.ToArray();
        var unresolved=current.Where(row=>row.State!=LaunchState.Arrived&&LaunchDescriptions.HasUnresolvedReservation(row)).ToArray();
        if(unresolved.Length==0)return false;
        IReadOnlySet<Guid>? observed=null;
        try {observed=_observedReservations?.Invoke(current);}
        catch(Exception){return true;}
        return unresolved.Any(row=>row.State!=LaunchState.ClientStartedUnverified||row.ResourceBudget is not {IsValid:true}||row.ProcessReceipt is not {IsValid:true}
            ||observed?.Contains(row.Intent.OperationId)!=true);
    }
}
