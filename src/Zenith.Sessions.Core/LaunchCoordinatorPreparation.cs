namespace Zenith.Sessions.Core;

public sealed partial class LaunchCoordinator
{
    private async Task PrepareAndStartAsync(LaunchOperation chosen,IPreparingLaunchProvider provider,long generation,
        TimeSpan stabilization,TimeSpan stagger,CancellationToken ct)
    {
        var context=new LaunchPreparationContext(chosen.Intent,!IsTrialOperation(chosen.Intent.OperationId));
        IPreparedAccountLaunch? prepared=null;bool dispatchInvoked=false,preparationFailureReported=false;
        try
        {
            LaunchPreparationResult? result=null;LaunchProblem problem=LaunchProblem.None;
            try
            {
                _activeStop!.Token.ThrowIfCancellationRequested();
                _preparationWork=provider.PrepareAsync(context,_activeStop.Token);
                result=await _preparationWork.WaitAsync(_preparationDeadline,_time,_activeStop.Token).ConfigureAwait(false);
                prepared=result?.Prepared;
                problem=result is {Failure:LaunchPreparationFailure.None,Prepared:not null}?LaunchProblem.None:result?.Failure switch
                {
                    LaunchPreparationFailure.Expired=>LaunchProblem.PreparationExpired,
                    LaunchPreparationFailure.CleanupFailed=>LaunchProblem.PreparationCleanupFailed,
                    _=>LaunchProblem.PreparationRejected
                };
            }
            catch(TimeoutException){problem=LaunchProblem.PreparationTimedOut;CancelActive();}
            catch(OperationCanceledException){problem=LaunchProblem.CancelledDuringStartup;}
            catch(Exception){problem=LaunchProblem.PreparationRejected;ReportPreparationFault();preparationFailureReported=true;}
            if(PreparationStopped(generation))
            {
                // A timeout cancels its worker too, but remains a visible timeout rather than
                // being rewritten as a manual cancellation by that cancellation's own token.
                bool timedOut=problem==LaunchProblem.PreparationTimedOut;
                await ChangeAsync(chosen,timedOut?LaunchState.Failed:LaunchState.Cancelled,
                    timedOut?problem:LaunchProblem.None).ConfigureAwait(false);return;
            }
            if(problem!=LaunchProblem.None)
            {
                // Only the provider's own Rejected answer counts toward the refusal streak; expiry,
                // cleanup failure and faults the coordinator catches itself do not. The native provider
                // also answers Rejected for its own local faults, a busy provider, an untrusted client and
                // a page the user closed or cancelled. This coordinator's own stop or timeout ends the wait
                // with an exception instead, so it is never counted.
                if(result?.Failure==LaunchPreparationFailure.Rejected)await FailRefusedPreparationAsync(chosen,generation,stagger).ConfigureAwait(false);
                else await ChangeAsync(chosen,LaunchState.Failed,problem).ConfigureAwait(false);
                return;
            }

            bool validHandle=false;LaunchIntent? finalIntent=null;
            try {finalIntent=prepared?.DispatchIntent;validHandle=prepared is not null&&ReferenceEquals(prepared.Context,context)&&IsAllowedPreparedIntent(chosen.Intent,finalIntent);}
            catch(Exception){ReportPreparationFault();}
            if(!validHandle)
            {await ChangeAsync(chosen,LaunchState.Failed,LaunchProblem.PreparationRejected).ConfigureAwait(false);return;}
            if(prepared!.IsExpired)
            {await ChangeAsync(chosen,LaunchState.Failed,LaunchProblem.PreparationExpired).ConfigureAwait(false);return;}

            bool adopted=false;
            await _serial.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if(PreparationStopped(generation))await UpdateAsync(chosen,LaunchState.Cancelled,LaunchProblem.None,CancellationToken.None).ConfigureAwait(false);
                else
                {
                    int index=_operations.FindIndex(o=>o.Intent.OperationId==chosen.Intent.OperationId);
                    if(index<0||_operations[index].Intent!=chosen.Intent||_operations[index].State!=LaunchState.Preparing)
                        throw new LaunchQueueException("The owned preparation no longer matches its queue request.");
                    // The provider receives one queue-created context. Only its explicit owned
                    // receipt may change the two allowed fields; no current-grant auto-adoption.
                    chosen=_operations[index] with{Intent=finalIntent!,UpdatedUtc=_time.GetUtcNow()};
                    _operations[index]=chosen;await PersistAsync(CancellationToken.None).ConfigureAwait(false);adopted=true;
                }
            }
            finally{_serial.Release();}
            if(!adopted)return;

            bool valid=false;problem=LaunchProblem.None;
            try
            {
                _validationWork=_validate(chosen.Intent,_activeStop!.Token);
                valid=await _validationWork.WaitAsync(_startupDeadline,_time,_activeStop.Token).ConfigureAwait(false);
            }
            catch(TimeoutException){problem=LaunchProblem.PreparationTimedOut;CancelActive();}
            catch(OperationCanceledException){problem=LaunchProblem.CancelledDuringStartup;}
            catch(Exception){problem=LaunchProblem.ProfileChanged;ReportPreparationFault();}
            if(!valid||problem!=LaunchProblem.None||PreparationStopped(generation))
            {
                bool timedOut=problem==LaunchProblem.PreparationTimedOut;
                bool cancelled=!timedOut&&PreparationStopped(generation);
                await ChangeAsync(chosen,cancelled?LaunchState.Cancelled:LaunchState.Failed,cancelled?LaunchProblem.None:
                    problem==LaunchProblem.None?LaunchProblem.ProfileChanged:problem).ConfigureAwait(false);return;
            }
            bool dispatch=false;
            await _serial.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if(PreparationStopped(generation))await UpdateAsync(chosen,LaunchState.Cancelled,LaunchProblem.None,CancellationToken.None).ConfigureAwait(false);
                else if(prepared.IsExpired)await UpdateAsync(chosen,LaunchState.Failed,LaunchProblem.PreparationExpired,CancellationToken.None).ConfigureAwait(false);
                else if(!ProviderPermits(chosen.Intent.OperationId))await UpdateAsync(chosen,LaunchState.Blocked,LaunchProblem.ProviderUnavailable,CancellationToken.None).ConfigureAwait(false);
                else if(AdmissionForStart(_operations,chosen.Intent.OperationId) is {Allowed:false} admission)
                    await UpdateAsync(chosen,LaunchState.Blocked,LaunchProblem.InsufficientCapacity,CancellationToken.None,hold:admission.Hold).ConfigureAwait(false);
                else{await UpdateAsync(chosen,LaunchState.Launching,LaunchProblem.None,CancellationToken.None).ConfigureAwait(false);dispatch=true;}
            }
            finally{_serial.Release();}
            if(!dispatch)return;
            dispatchInvoked=true;
            await DispatchOwnedAsync(chosen,generation,stabilization,stagger,ct,prepared).ConfigureAwait(false);
        }
        catch(Exception)
        {
            // Provider properties/validation can throw arbitrary secret-bearing text too.
            ReportPreparationFault();
            if(!_unhealthy)await ChangeAsync(chosen,dispatchInvoked?LaunchState.Uncertain:LaunchState.Failed,
                dispatchInvoked?LaunchProblem.ProviderFailure:LaunchProblem.PreparationRejected).ConfigureAwait(false);
        }
        finally
        {
            // WaitAsync is only an observation deadline. The actual task and any late Ready
            // handle stay owned and cannot be adopted/dispatched by a later queue operation.
            LaunchPreparationResult? actual=null;
            if(_preparationWork is not null)
                try{actual=await _preparationWork.ConfigureAwait(false);}
                catch(OperationCanceledException){}
                catch(Exception){if(!preparationFailureReported)ReportPreparationFault();}
            bool cleanupFailed=actual?.Failure==LaunchPreparationFailure.CleanupFailed;
            prepared??=actual?.Prepared;
            if(_validationWork is not null)await DrainAsync(_validationWork,CancellationToken.None).ConfigureAwait(false);
            if(dispatchInvoked)
            {
                await DrainAsync(_providerWork,CancellationToken.None).ConfigureAwait(false);
                if(_providerIntent is { } intent)await CaptureLateReceiptAsync(intent,_providerWork).ConfigureAwait(false);
            }
            try{if(prepared is not null)await prepared.DisposeAsync().ConfigureAwait(false);}
            catch(Exception){cleanupFailed=true;}
            if(cleanupFailed)
            {
                _unhealthy=true;_paused=true;CancelActive();ReportPreparationFault();
                try{await ChangeAsync(chosen,dispatchInvoked?LaunchState.Uncertain:LaunchState.Failed,LaunchProblem.PreparationCleanupFailed).ConfigureAwait(false);}
                catch(Exception){ReportPreparationFault();}
            }
            _preparationWork=null;EndActive();if(!_unhealthy&&!_paused&&!_stop.IsCancellationRequested)Signal();
        }
    }

    private async Task FailRefusedPreparationAsync(LaunchOperation chosen,long generation,TimeSpan stagger)
    {
        await _serial.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // A stop that lands while this waits for the lane wins: the refusal is not counted.
            if(!PreparationStopped(generation))RecordRefusalOwned(chosen.Intent.OperationId,stagger);
            await UpdateAsync(chosen,LaunchState.Failed,LaunchProblem.PreparationRejected,CancellationToken.None).ConfigureAwait(false);
        }
        finally{_serial.Release();}
    }
    private bool PreparationStopped(long generation)=>_paused||_unhealthy||_stop.IsCancellationRequested
        ||generation!=Interlocked.Read(ref _generation)||_activeStop?.IsCancellationRequested!=false;
    internal static bool IsAllowedPreparedIntent(LaunchIntent original,LaunchIntent? final)
    {
        if(final is null||original.ProfileRevision<0||original.AuthorizationGeneration<=0
            ||final.ProfileRevision<original.ProfileRevision||final.ProfileRevision-original.ProfileRevision>1
            ||final.AuthorizationGeneration<original.AuthorizationGeneration)return false;
        long delta=final.AuthorizationGeneration-original.AuthorizationGeneration;
        return (delta is 0 or 2)&&HasValidDestination(final)&&final==(original with
            {ProfileRevision=final.ProfileRevision,AuthorizationGeneration=final.AuthorizationGeneration});
    }
    private void ReportPreparationFault()=>Faulted?.Invoke(new LaunchQueueException(
        "Launch preparation or its cleanup failed. Protected request details were withheld; review the operation before retrying."));
}
