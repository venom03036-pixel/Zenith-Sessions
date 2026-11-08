namespace Zenith.Sessions.Core;

public enum NativeHandoffAttemptStatus
{ Busy, NotAuthorized, RequestChanged, Expired, AdmissionDenied, Cancelled, NotStarted, StartedUnverified, Uncertain }
/// <summary>BrowserTrackerId is the candidate's non-secret tracker, set only beside a process receipt.</summary>
public sealed record NativeHandoffAttempt(NativeHandoffAttemptStatus Status, LaunchProcessReceipt? ProcessReceipt = null, long? BrowserTrackerId = null);

/// <summary>
/// Final preparation/dispatch plumbing, not an enabled or verified Roblox launch provider.
/// Exactly one tracked operation; callers supply the actual scheduler admission and current
/// request validation. No browser/network/publisher check belongs inside the vault callback.
/// </summary>
public sealed class NativeHandoffCoordinator(IAuthorizationDispatchVault vault) : IAsyncDisposable
{
    private readonly IAuthorizationDispatchVault _vault = vault ?? throw new ArgumentNullException(nameof(vault));
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Owner? _active;
    private bool _disposed;
    private Task? _dispose;

    /// <summary>
    /// An accepted operation takes ownership of candidate and clears it on every terminal path.
    /// Busy does not take ownership. The caller owns prepared and must keep it alive until this
    /// actual task finishes, then dispose it. Observation timeouts do not end ownership.
    /// </summary>
    public Task<NativeHandoffAttempt> DispatchAsync(NativeHandoffCandidate candidate, AuthorizationWriteSnapshot finalGrant,
        LaunchIntent intent, IPreparedNativeHandoffStart prepared,
        Func<LaunchIntent, CancellationToken, Task<bool>> validateRequest, Func<bool> requestStillCurrent,
        Func<LaunchAdmission> readAdmission, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate); ArgumentNullException.ThrowIfNull(finalGrant);
        ArgumentNullException.ThrowIfNull(intent); ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(validateRequest); ArgumentNullException.ThrowIfNull(requestStillCurrent);
        ArgumentNullException.ThrowIfNull(readAdmission); ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not null) return Task.FromResult(new NativeHandoffAttempt(NativeHandoffAttemptStatus.Busy));
            var owner = new Owner(CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token)); _active = owner;
            // Native synchronous work is never performed on the calling UI thread. This task is
            // tracked until actual completion even if cancellation cannot interrupt a native API.
            return Task.Run(() => RunAsync(owner, candidate, finalGrant, intent, prepared, validateRequest, requestStillCurrent, readAdmission), CancellationToken.None);
        }
    }

    private async Task<NativeHandoffAttempt> RunAsync(Owner owner, NativeHandoffCandidate candidate,
        AuthorizationWriteSnapshot finalGrant, LaunchIntent intent, IPreparedNativeHandoffStart prepared,
        Func<LaunchIntent, CancellationToken, Task<bool>> validateRequest, Func<bool> requestStillCurrent, Func<LaunchAdmission> readAdmission)
    {
        bool entered = false, callbackInvoked = false; NativeHandoffDispatchResult? observed = null;
        NativeHandoffAttemptStatus? refusal = null;
        long tracker = candidate.BrowserTrackerId;
        CancellationToken ct = owner.Stop.Token;
        var closePreparationOnCancellation = ct.Register(candidate.Context.Dispose);
        try
        {
            ct.ThrowIfCancellationRequested();
            var context = candidate.Context;
            if (!candidate.MatchesFinalGrant(finalGrant) || intent.OperationId != context.LaunchOperationId
                || intent.ProfileId != context.ProfileId || intent.UserId != context.ExpectedUserId
                || intent.AuthorizationGeneration != finalGrant.Record?.Generation
                || intent.PlaceId != context.Destination.PlaceId || intent.RequestedJobId != context.Destination.JobId
                || intent.RequestedServerKind != context.Destination.ServerKind || !LaunchCoordinator.HasValidDestination(intent))
                return new(NativeHandoffAttemptStatus.NotAuthorized);
            if (candidate.IsConsumed) return new(NativeHandoffAttemptStatus.NotStarted);
            if (candidate.Expired()) return new(NativeHandoffAttemptStatus.Expired);
            if (!await validateRequest(intent, ct).ConfigureAwait(false)) return new(NativeHandoffAttemptStatus.RequestChanged);
            ct.ThrowIfCancellationRequested();
            var result = await _vault.TryDispatchCurrentAsync(finalGrant, () =>
            {
                callbackInvoked = true;
                // Only pure/current cached admission and operation fences are called here.
                // The scheduler must already own its reservation; this class does not allocate it.
                if (ct.IsCancellationRequested) { refusal = NativeHandoffAttemptStatus.Cancelled; return new(NativeHandoffDispatchOutcome.NotStarted); }
                try
                { if (!requestStillCurrent()) { refusal = NativeHandoffAttemptStatus.RequestChanged; return new(NativeHandoffDispatchOutcome.NotStarted); } }
                catch (Exception) { refusal = NativeHandoffAttemptStatus.RequestChanged; return new(NativeHandoffDispatchOutcome.NotStarted); }
                if (candidate.Expired()) { refusal = NativeHandoffAttemptStatus.Expired; return new(NativeHandoffDispatchOutcome.NotStarted); }
                try
                { if (!readAdmission().Allowed) { refusal = NativeHandoffAttemptStatus.AdmissionDenied; return new(NativeHandoffDispatchOutcome.NotStarted); } }
                catch (Exception) { refusal = NativeHandoffAttemptStatus.AdmissionDenied; return new(NativeHandoffDispatchOutcome.NotStarted); }
                if (candidate.Expired()) { refusal = NativeHandoffAttemptStatus.Expired; return new(NativeHandoffDispatchOutcome.NotStarted); }
                ct.ThrowIfCancellationRequested();
                observed = candidate.Invoke(prepared, ct, ref entered);
                return observed;
            }, ct).ConfigureAwait(false);
            if (result.Status == AuthorizationDispatchStatus.NotAuthorized)
                return entered ? Uncertain(observed, tracker) : new(NativeHandoffAttemptStatus.NotAuthorized);
            if (result.Status != AuthorizationDispatchStatus.CallbackInvoked || !callbackInvoked)
                return entered ? Uncertain(observed, tracker) : new(NativeHandoffAttemptStatus.NotStarted);
            if (refusal.HasValue) return new(refusal.Value);
            if (!entered && ct.IsCancellationRequested) return new(NativeHandoffAttemptStatus.Cancelled);
            if (!entered && observed is null) return new(NativeHandoffAttemptStatus.NotStarted);
            var local = NativeHandoffDispatchResult.Sanitize(observed);
            if (local != NativeHandoffDispatchResult.Sanitize(result.Dispatch)) return Uncertain(local, tracker);
            return Describe(local, tracker);
        }
        catch (OperationCanceledException)
        { return entered ? Uncertain(observed, tracker) : new(NativeHandoffAttemptStatus.Cancelled); }
        catch (Exception)
        {
            // Delegate, filesystem and native exceptions can contain sensitive handoff values.
            // No raw message, inner exception or credential is returned or recorded.
            return entered ? Uncertain(observed, tracker) : new(NativeHandoffAttemptStatus.NotStarted);
        }
        finally
        {
            closePreparationOnCancellation.Dispose();
            candidate.Dispose();
            lock (_gate) { if (ReferenceEquals(_active, owner)) _active = null; owner.Stop.Dispose(); }
            owner.Completion.TrySetResult();
        }
    }

    // The original receipt is passed on unchanged; the tracker of the request it started travels beside it.
    private static NativeHandoffAttempt Describe(NativeHandoffDispatchResult result, long tracker) => result.Outcome switch
    {
        NativeHandoffDispatchOutcome.NotStarted => new(NativeHandoffAttemptStatus.NotStarted),
        NativeHandoffDispatchOutcome.StartedUnverified => new(NativeHandoffAttemptStatus.StartedUnverified, result.ProcessReceipt,
            result.ProcessReceipt is null ? null : tracker),
        _ => Uncertain(result, tracker)
    };
    private static NativeHandoffAttempt Uncertain(NativeHandoffDispatchResult? result, long tracker)
        => result?.ProcessReceipt is { IsValid: true } receipt ? new(NativeHandoffAttemptStatus.Uncertain, receipt, tracker)
            : new(NativeHandoffAttemptStatus.Uncertain);

    public void CancelPending()
    { lock (_gate) { if (!_disposed) _active?.Stop.Cancel(); } }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_dispose is null)
            {
                _disposed = true; _lifetime.Cancel(); _active?.Stop.Cancel();
                _dispose = FinishDisposeAsync(_active?.Completion.Task ?? Task.CompletedTask);
            }
            return new(_dispose);
        }
    }
    private async Task FinishDisposeAsync(Task actualOwner)
    { await actualOwner.ConfigureAwait(false); _lifetime.Dispose(); }
    private sealed class Owner(CancellationTokenSource stop)
    {
        public CancellationTokenSource Stop { get; } = stop;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
