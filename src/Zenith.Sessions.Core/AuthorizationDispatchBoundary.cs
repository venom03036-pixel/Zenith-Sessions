namespace Zenith.Sessions.Core;

public enum NativeHandoffDispatchOutcome { NotStarted, StartedUnverified, Uncertain }

/// <summary>
/// Non-secret facts from one synchronous final native dispatch. A process receipt is never
/// account identity, current liveness or arrival proof. No handoff URI/value belongs here.
/// </summary>
public sealed record NativeHandoffDispatchResult(NativeHandoffDispatchOutcome Outcome, LaunchProcessReceipt? ProcessReceipt = null)
{
    /// <summary>Fixed uncertainty for malformed provider facts; a valid original receipt is never replaced or renewed.</summary>
    public static NativeHandoffDispatchResult Sanitize(NativeHandoffDispatchResult? result)
    {
        LaunchProcessReceipt? receipt = result?.ProcessReceipt is { IsValid: true } valid ? valid : null;
        NativeHandoffDispatchOutcome outcome = result?.Outcome ?? NativeHandoffDispatchOutcome.Uncertain;
        if (!Enum.IsDefined(outcome) || result?.ProcessReceipt is not null && receipt is null
            || outcome == NativeHandoffDispatchOutcome.StartedUnverified && receipt is null
            || outcome == NativeHandoffDispatchOutcome.NotStarted && receipt is not null)
            outcome = NativeHandoffDispatchOutcome.Uncertain;
        return new(outcome, receipt);
    }
}

public enum AuthorizationDispatchStatus { NotAuthorized, CallbackInvoked }

/// <summary>CallbackInvoked means only that the owned callback ran, including an uncertain/failed attempt.</summary>
public sealed record AuthorizationDispatchResult(AuthorizationDispatchStatus Status, NativeHandoffDispatchResult? Dispatch = null);

public interface IAuthorizationDispatchVault
{
    // Compares the exact selected protected snapshot and current usable saved-session grant
    // under the actual vault process lease. A cached snapshot alone never authorizes dispatch.
    // Invalid comparison arguments/busy/storage errors retain the existing vault error contract.
    // A stale, absent, expired, legacy, non-session or non-Linked grant returns NotAuthorized.
    //
    // dispatch must perform ONLY the final synchronous native start, at most once. All browser,
    // network, full executable verification/hashing and grant preparation finish before entering;
    // the start may make its final bounded held-file/path identity recheck immediately before dispatch;
    // no async work, nested vault access, update/save or subsequent follow-up runs in this lease.
    // The boundary itself never starts a process or enables an unavailable native provider.
    //
    // Cancellation is checked immediately BEFORE invocation. Once invoked, a synchronous native
    // call cannot be forcibly cancelled: the actual callback remains owned until it returns.
    // Callers must await that task before disposing its resources; cancellation/timeout never
    // erases an actual returned process receipt or proves that a native start was prevented.
    // Thrown/null/undefined/inconsistent callback outcomes become fixed Uncertain results;
    // only valid original process receipts are retained, without inventing or renewing identity.
    Task<AuthorizationDispatchResult> TryDispatchCurrentAsync(AuthorizationWriteSnapshot expected,
        Func<NativeHandoffDispatchResult> dispatch, CancellationToken ct);
}
