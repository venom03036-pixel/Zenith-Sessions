using System.Globalization;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// One owned saved-session operation and one bounded waiting disconnect. Never launches a client. <paramref name="failed"/>
/// receives only the type of an expected failure that the service absorbs without changing its answer, or of a fence of its
/// own interrupted write that could not run (never its message). <paramref name="launchTickets"/> is the direct launch-ticket
/// client (R-api-7) whose kept anti-forgery tokens a disconnect forgets, like the share-link client's; it is borrowed, not owned.
/// </summary>
public sealed partial class RobloxSessionLinkService(IAccountRepository profiles, IAccountCredentialVault vault,
    IRobloxSessionClient client, IRobloxSessionSignIn signIn, AccountIdentityProjection? projection = null, IRobloxShareLinkClient? shareLinks = null,
    Action<Type>? failed = null, IRobloxLaunchTicketClient? launchTickets = null, IRobloxAccountDataClient? accountData = null)
    : IRobloxSessionLinkService, IRobloxNativeHandoffPreparer
{
    private readonly object _state = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Operation? _active;
    private TaskCompletionSource? _disconnect;
    private bool _disposed;
    private Task? _disposeTask;

    public async Task<AddedRobloxAccount> AddAsync(CancellationToken ct)
    {
        // The isolated browser needs an owner identity before any metadata exists. A new
        // profile is published only after the authenticated identity is completely verified.
        // If that player already has a saved Roblox session, this same owned operation moves to
        // that row and refreshes its sign-in instead of discarding the finished sign-in.
        var operation = Begin(Guid.NewGuid(), SavedSessionOperationKind.AddAccount, ct); Guid created = operation.ProfileId; bool succeeded = false;
        try
        {
            var snapshot = await ReadSnapshotAsync(operation).ConfigureAwait(false);
            if (snapshot.Record is not null || snapshot.HighestAllocated != 0 || snapshot.LastCommitted != 0)
                throw AuthorizationChanged();
            using var credential = await signIn.SignInAsync(operation.ProfileId, operation.Token).ConfigureAwait(false);
            Ensure(operation);
            var identity = await VerifyAndSave(operation, credential, null, writeSnapshot: snapshot,
                createProfile: true).ConfigureAwait(false);
            Ensure(operation); succeeded = true; return new(operation.ProfileId, identity, Refreshed: operation.ProfileId != created);
        }
        finally { await CompleteMutationAsync(operation, succeeded).ConfigureAwait(false); }
    }

    public async Task<LinkedIdentity> LinkAsync(Guid profileId, CancellationToken ct)
    {
        var operation = Begin(profileId, SavedSessionOperationKind.SignIn, ct); bool succeeded = false;
        try
        {
            await RequireProfile(profileId, operation.Token).ConfigureAwait(false);
            var snapshot = await ReadSnapshotAsync(operation).ConfigureAwait(false);
            var previous = snapshot.Record?.Validated();
            Ensure(operation); Compatible(previous);
            using var credential = await signIn.SignInAsync(profileId, operation.Token).ConfigureAwait(false);
            Ensure(operation);
            var identity = await VerifyAndSave(operation, credential, previous, writeSnapshot: snapshot).ConfigureAwait(false);
            succeeded = true; return identity;
        }
        finally { await CompleteMutationAsync(operation, succeeded).ConfigureAwait(false); }
    }

    public async Task<LinkedIdentity> CheckSavedAsync(Guid profileId, CancellationToken ct)
    {
        var operation = Begin(profileId, SavedSessionOperationKind.SavedSignInCheck, ct); bool succeeded = false;
        try
        {
            await RequireProfile(profileId, operation.Token).ConfigureAwait(false);
            var snapshot = await ReadSnapshotAsync(operation).ConfigureAwait(false);
            var previous = snapshot.Record?.Validated()
                ?? throw new AccountLinkException("This profile has no saved Roblox session. Sign in first.");
            Ensure(operation); Compatible(previous);
            if (previous.Health != AuthorizationHealth.Linked)
                throw new AccountLinkException("This saved session needs a new sign-in; an uncertain or invalid session is not reused.");
            // A response can rotate the credential even if its body, connection or cancellation then fails.
            // Fence old authority durably before contacting Roblox. Retain its protected material for
            // recovery, but require explicit sign-in after any interrupted check rather than replay it.
            // Only a check Roblox provably never processed (NotProcessed) undoes its own fence.
            long pendingGeneration = snapshot.NextGeneration; var pendingMutationId = Guid.NewGuid();
            var pending = previous with { Generation = pendingGeneration, Health = AuthorizationHealth.SignInRequired };
            var reserved = await ((ISnapshotAuthorizationVault)vault).TryCommitSnapshotAsync(snapshot, pending, pendingMutationId, operation.Token).ConfigureAwait(false);
            Ensure(operation); RequireMutation(reserved, pendingGeneration);
            var pendingSnapshot = await ReadSnapshotAsync(operation).ConfigureAwait(false);
            if (pendingSnapshot.Record != pending || pendingSnapshot.HighestAllocated != pendingGeneration
                || pendingSnapshot.LastCommitted != pendingGeneration || pendingSnapshot.HighestMutationId != pendingMutationId)
                throw AuthorizationChanged();
            if (previous.ExpiresUtc <= DateTimeOffset.UtcNow)
                throw new AccountLinkException("This saved Roblox session has expired. Sign in again.");
            using var credential = new RobloxSessionCredential(previous.SessionCookie, previous.ExpiresUtc);
            try
            {
                var identity = await VerifyAndSave(operation, credential, pending, writeSnapshot: pendingSnapshot).ConfigureAwait(false);
                succeeded = true; return identity;
            }
            catch (RobloxSessionException error) when (error.Failure == RobloxSessionFailure.NotProcessed)
            {
                // The saved session itself never reached Roblox, or was turned away without a replacement
                // (VerifyAndSave reports a refused replacement check as Network), so it was not rotated.
                // Restore it only if this check's own fence is still the current record.
                Ensure(operation);
                var restored = previous with { Generation = pendingSnapshot.NextGeneration };
                operation.FinalMutationIssued = true; // Cleanup may fence only this exact write.
                var receipt = await ((ISnapshotAuthorizationVault)vault).TryCommitSnapshotAsync(pendingSnapshot, restored,
                    operation.FinalMutationId, operation.Token).ConfigureAwait(false);
                Ensure(operation);
                // Another operation replaced this check's fence, so its newer record is kept. Roblox still never
                // processed the check: report that, so a caller treats it as unreachable or busy, not as a sign-in answer.
                if (receipt.Status == AuthorizationMutationStatus.Conflict)
                    throw new RobloxSessionException(RobloxSessionFailure.NotProcessed,
                        "Roblox could not be reached or was busy, so the saved sign-in was not checked. Another operation changed it meanwhile, and that newer sign-in was kept.");
                RequireMutation(receipt, restored.Generation);
                succeeded = true;
                throw new RobloxSessionException(RobloxSessionFailure.NotProcessed,
                    "Roblox could not be reached or was busy, so the saved sign-in was not checked. It was left unchanged; check it again later.");
            }
            catch (RobloxSessionException error) when (error.Failure is RobloxSessionFailure.Network or RobloxSessionFailure.TimedOut)
            {
                Ensure(operation);
                // The protected bytes are retained, but the earlier durable fence intentionally
                // prevents reuse. State that recovery requirement without forwarding response text.
                throw new RobloxSessionException(error.Failure, error.Failure == RobloxSessionFailure.TimedOut
                    ? "The saved session check timed out. It is marked as needing sign-in. Sign in again before using this account."
                    : "Roblox could not confirm the saved session. It is marked as needing sign-in. Sign in again before using this account.");
            }
        }
        finally { await CompleteMutationAsync(operation, succeeded).ConfigureAwait(false); }
    }

    public async Task<RobloxHandoffProbeResult> InspectHandoffAsync(Guid profileId, long placeId, IRobloxHandoffProbe probe, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(probe);
        if (profileId == Guid.Empty || placeId <= 0) return new(RobloxHandoffProbeOutcome.ContextRejected);
        Operation operation;
        try { operation = Begin(profileId, SavedSessionOperationKind.HandoffInspection, ct); }
        // Both refusals happen before any grant is reserved; a row being removed is reported as such, not as another operation.
        catch (AccountLinkException refused) { return new(refused.ProfileRemoved ? RobloxHandoffProbeOutcome.ProfileRemoved : RobloxHandoffProbeOutcome.Busy); }
        AuthorizationRecord? pending = null;
        var inspectionMutationId = Guid.NewGuid();
        RobloxHandoffProbeResult? result = null;
        bool failureFenced = true, cancellationEscaping = false;
        try
        {
            try { result = await InspectOwnedAsync().ConfigureAwait(false); }
            catch (HandoffInspectionException error) { Ensure(operation); result = new(error.Outcome); }
            catch (OperationCanceledException) { Ensure(operation); result = new(RobloxHandoffProbeOutcome.Cancelled); }
            catch (RobloxSessionException error)
            {
                Ensure(operation);
                result = new(error.Failure switch
                {
                    RobloxSessionFailure.InvalidSession or RobloxSessionFailure.Challenge => RobloxHandoffProbeOutcome.SignInRequired,
                    RobloxSessionFailure.TimedOut => RobloxHandoffProbeOutcome.TimedOut,
                    _ => RobloxHandoffProbeOutcome.Failed
                });
            }
            catch (AccountLinkException) { Ensure(operation); result = new(RobloxHandoffProbeOutcome.AuthorizationChanged); }
            catch (Exception) { Ensure(operation); result = new(RobloxHandoffProbeOutcome.Failed); }
        }
        catch (OperationCanceledException) { cancellationEscaping = true; throw; }
        finally
        {
            try
            {
                if (pending is not null && result?.Outcome != RobloxHandoffProbeOutcome.CapturedAndPrevented)
                    failureFenced = await FenceFailedSessionMutationAsync(pending.ProfileId, inspectionMutationId).ConfigureAwait(false);
                if (!failureFenced && cancellationEscaping)
                    throw new AccountLinkException("The cancelled handoff inspection could not confirm its authorization cleanup. Protected records were preserved; review the saved sign-in before retrying.");
            }
            finally { Complete(operation); }
        }
        return failureFenced ? result! : new(RobloxHandoffProbeOutcome.CleanupFailed);

        async Task<RobloxHandoffProbeResult> InspectOwnedAsync()
        {
            if (vault is not IConditionalAuthorizationVault conditional) return new(RobloxHandoffProbeOutcome.AuthorizationChanged);
            var profile = await profiles.FindAsync(profileId, operation.Token).ConfigureAwait(false); Ensure(operation);
            if (profile is null) return new(RobloxHandoffProbeOutcome.ContextRejected);
            var previous = await vault.ReadAsync(profileId, operation.Token).ConfigureAwait(false); Ensure(operation);
            if (previous is null) return new(RobloxHandoffProbeOutcome.SignInRequired);
            if (previous.ProfileId != profileId) return new(RobloxHandoffProbeOutcome.ContextRejected);
            try { previous = previous.Validated(); }
            catch (ArgumentException) { return new(RobloxHandoffProbeOutcome.SignInRequired); }
            if (previous.Provider != RobloxSessionIdentity.Provider || previous.Health != AuthorizationHealth.Linked)
                return new(RobloxHandoffProbeOutcome.SignInRequired);
            // A probe that would refuse never receives the cookie, so nothing can rotate: answer before
            // reserving. Every probe caller is serialized here, so a later refusal still fails closed below.
            if (probe.Refusal is { } refused) return new(refused);
            var pendingTemplate = previous with { Health = AuthorizationHealth.SignInRequired };
            var reservation = await conditional.TryReplaceNextAsync(previous, pendingTemplate, Guid.NewGuid(), operation.Token).ConfigureAwait(false);
            Ensure(operation);
            if (reservation.Status != AuthorizationMutationStatus.Applied || reservation.CommittedGeneration is not { } generation
                || !IsSuccessor(previous.Generation, generation)) return new(RobloxHandoffProbeOutcome.AuthorizationChanged);
            pending = pendingTemplate with { Generation = generation };
            if (previous.ExpiresUtc <= DateTimeOffset.UtcNow) return new(RobloxHandoffProbeOutcome.SignInRequired);
            using var credential = new RobloxSessionCredential(previous.SessionCookie,
                previous.ExpiresUtc == DateTimeOffset.MaxValue ? null : previous.ExpiresUtc);
            var context = new RobloxHandoffProbeContext(profileId, previous.VerifiedUserId, generation, placeId).Validated();
            RobloxHandoffProbeCapture capture;
            try { capture = await probe.InspectAsync(context, credential, operation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { Ensure(operation); return new(RobloxHandoffProbeOutcome.Cancelled); }
            catch (Exception)
            {
                // A browser/native exception may contain an emitted URI or cookie. Never attach
                // it as an inner exception or forward arbitrary provider text to presentation.
                Ensure(operation); return new(RobloxHandoffProbeOutcome.Failed);
            }
            using (capture)
            {
                Ensure(operation);
                if (capture is null) return new(RobloxHandoffProbeOutcome.Failed);
                if (capture.Result.Outcome != RobloxHandoffProbeOutcome.CapturedAndPrevented) return capture.Result;
                if (capture.UpdatedCredential is not { } current || current.ExpiresUtc is { } expiry && expiry <= DateTimeOffset.UtcNow)
                    return new(RobloxHandoffProbeOutcome.SignInRequired);
                if (!await IsPendingInspectionAsync(pending, operation).ConfigureAwait(false)) return new(RobloxHandoffProbeOutcome.AuthorizationChanged);
                await VerifyAndSave(operation, current, pending, handoffInspection: true,
                    inspectionMutationId: inspectionMutationId).ConfigureAwait(false); Ensure(operation);
                return capture.Result;
            }
        }
    }

    private async Task<bool> IsPendingInspectionAsync(AuthorizationRecord pending, Operation operation)
    {
        var current = await vault.ReadAsync(pending.ProfileId, operation.Token).ConfigureAwait(false); Ensure(operation);
        // An allocator reservation clears the reusable material on a stale vault read even if its
        // replacement has not committed. Do not skip that invalidation by allocating beyond it.
        return current == pending;
    }
    /// <summary>False when the fence could not run. <paramref name="absorbed"/> then receives only the type of the failure, never its text.</summary>
    private async Task<bool> FenceFailedSessionMutationAsync(Guid profileId, Guid mutationId, Action<Type>? absorbed = null)
    {
        // Failure can occur after Linked bytes commit but before projection/acknowledgment. Keep
        // this safety write under the same owner even when its user-facing token was cancelled.
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            if (vault is not IConditionalAuthorizationVault conditional) return false;
            // A reservation may have reached disk even if its caller never received a receipt.
            // The operation's random ownership ID is the authority for cleanup; a later foreign
            // write clears that ownership, so stale cleanup becomes a successful no-op.
            await conditional.TryFenceOwnedMutationAsync(profileId, mutationId, safety.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) { absorbed?.Invoke(error.GetType()); return false; }
    }
    private static bool IsSuccessor(long previous, long next) => previous < long.MaxValue && next == previous + 1;

    private async Task<LinkedIdentity> VerifyAndSave(Operation operation, RobloxSessionCredential credential, AuthorizationRecord? previous,
        bool handoffInspection = false, Guid? inspectionMutationId = null, AuthorizationWriteSnapshot? writeSnapshot = null,
        bool createProfile = false)
    {
        using var first = await client.ValidateAsync(credential, operation.Token).ConfigureAwait(false);
        Ensure(operation); ValidateIdentity(first.Identity);
        if (previous is not null && !SameIdentity(previous, first.Identity))
        {
            if (handoffInspection) throw new HandoffInspectionException(RobloxHandoffProbeOutcome.IdentityMismatch);
            throw new AccountLinkException("This sign-in belongs to a different Roblox account. Disconnect the existing session explicitly or create another profile.");
        }
        RobloxSessionValidation? second = null;
        try
        {
            var saved = credential; var identity = first.Identity;
            if (first.RotatedCredential is { } candidate)
            {
                // Roblox already replaced the original, so a refused replacement check is not grounds
                // to restore anything: report it as the ambiguous outcome it is for the saved record.
                try { second = await client.ValidateAsync(candidate, operation.Token).ConfigureAwait(false); }
                catch (RobloxSessionException error) when (error.Failure == RobloxSessionFailure.NotProcessed)
                { throw new RobloxSessionException(RobloxSessionFailure.Network, "Roblox replaced the session but could not be reached to verify the replacement. No replacement was saved; sign in again."); }
                Ensure(operation); ValidateIdentity(second.Identity);
                if (second.Identity.UserId != first.Identity.UserId || second.Identity.Issuer != first.Identity.Issuer || second.Identity.Subject != first.Identity.Subject)
                {
                    if (handoffInspection) throw new HandoffInspectionException(RobloxHandoffProbeOutcome.IdentityMismatch);
                    throw new AccountLinkException("The replacement session did not verify as the same Roblox account. Sign in again.");
                }
                if (second.RotatedCredential is not null)
                    throw new AccountLinkException("Roblox replaced the session again while it was being verified. No unverified replacement was saved; sign in again.");
                saved = candidate; identity = second.Identity;
            }
            if (await Unique(operation.ProfileId, identity, operation.Token, createProfile).ConfigureAwait(false) is { } savedProfileId)
            {
                // The credential is still owned here, so the refresh happens in this operation and is
                // never held across a prompt. Only the protected sign-in and last-known identity change.
                writeSnapshot = await AdoptSavedSessionAsync(operation, savedProfileId, identity).ConfigureAwait(false);
                createProfile = false;
            }
            if (!createProfile) await RequireProfile(operation.ProfileId, operation.Token).ConfigureAwait(false);
            Ensure(operation);
            if (saved.ExpiresUtc is { } expiry && expiry <= DateTimeOffset.UtcNow)
                throw new AccountLinkException("This Roblox session expired before it could be saved. Sign in again.");
            if (handoffInspection && (previous is null || !await IsPendingInspectionAsync(previous, operation).ConfigureAwait(false)))
                throw new HandoffInspectionException(RobloxHandoffProbeOutcome.AuthorizationChanged);
            long generation;
            if (handoffInspection)
            {
                if (previous!.Generation == long.MaxValue || inspectionMutationId is null || inspectionMutationId == Guid.Empty
                    || vault is not IConditionalAuthorizationVault)
                    throw new HandoffInspectionException(RobloxHandoffProbeOutcome.AuthorizationChanged);
                generation = previous.Generation + 1;
            }
            else
            {
                if (writeSnapshot is null || writeSnapshot.ProfileId != operation.ProfileId || vault is not ISnapshotAuthorizationVault)
                    throw AuthorizationChanged();
                generation = writeSnapshot.NextGeneration;
            }
            Ensure(operation);
            var record = new AuthorizationRecord(operation.ProfileId, RobloxSessionIdentity.Provider, identity.UserId, generation,
                "", "", saved.ExpiresUtc ?? DateTimeOffset.MaxValue, identity.Username, AuthorizationHealth.Linked,
                RobloxSessionIdentity.Issuer, identity.Subject, SessionCookie: saved.CopyValue()).Validated();
            if (createProfile)
            {
                var profile = (AccountProfile.Create(identity.Username) with
                { Id = operation.ProfileId, Username = identity.Username, RobloxUserId = identity.UserId }).Validated();
                Ensure(operation);
                // Insert-only metadata: an interrupted add is identifiable and recoverable, never
                // grounds to delete a profile or to overwrite another operation's newer grant.
                await profiles.CreateManyAsync([profile], operation.Token).ConfigureAwait(false);
                Ensure(operation);
            }
            if (projection is not null)
            {
                await projection.PrepareAsync(Metadata(record), operation.Token).ConfigureAwait(false); Ensure(operation);
            }
            if (handoffInspection)
            {
                var receipt = await ((IConditionalAuthorizationVault)vault).TryReplaceNextAsync(previous!,
                    record with { Generation = previous!.Generation }, inspectionMutationId!.Value, operation.Token).ConfigureAwait(false);
                Ensure(operation);
                if (receipt.Status != AuthorizationMutationStatus.Applied || receipt.CommittedGeneration != generation)
                    throw new HandoffInspectionException(RobloxHandoffProbeOutcome.AuthorizationChanged);
            }
            else
            {
                // Mark before awaiting: the store may reserve or commit before its caller can
                // receive the result. Cleanup can invalidate only this exact operation owner.
                operation.FinalMutationIssued = true;
                var receipt = await ((ISnapshotAuthorizationVault)vault).TryCommitSnapshotAsync(writeSnapshot!, record,
                    operation.FinalMutationId, operation.Token).ConfigureAwait(false);
                Ensure(operation); RequireMutation(receipt, generation);
            }
            if (projection is not null)
            {
                await projection.CompleteAsync(record.ProfileId, record.Generation, operation.Token).ConfigureAwait(false); Ensure(operation);
            }
            return identity;
        }
        finally { second?.Dispose(); }
    }

    public async Task DisconnectAsync(Guid profileId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Operation? active; TaskCompletionSource completion;
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_disconnect is not null) throw new AccountLinkException("Another saved-session disconnect is in progress.");
            _disconnect = completion = new(TaskCreationOptions.RunContinuationsAsynchronously); active = _active;
            if (active is not null && (active.ProfileId == profileId || active.RelatedProfileId == profileId)) active.Invalidated = true;
        }
        shareLinks?.ForgetTokens(profileId); // Sign-out: no anti-forgery token kept for this profile outlives its saved session.
        launchTickets?.ForgetTokens(profileId);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        try
        {
            if (active is not null && (active.ProfileId == profileId || active.RelatedProfileId == profileId)) active.Cancel();
            // An uncooperative browser/network/storage owner is awaited to actual completion.
            if (active is not null) await active.Completion.Task.WaitAsync(stop.Token).ConfigureAwait(false);
            stop.Token.ThrowIfCancellationRequested();
            if (vault is not ISnapshotAuthorizationVault conditional) throw AuthorizationChanged();
            var snapshot = await conditional.ReadWriteSnapshotAsync(profileId, stop.Token).ConfigureAwait(false);
            stop.Token.ThrowIfCancellationRequested();
            if (snapshot.ProfileId != profileId) throw AuthorizationChanged();
            RequireMutation(await conditional.TryDeleteSnapshotAsync(snapshot, stop.Token).ConfigureAwait(false), snapshot.NextGeneration);
            if (projection is not null) await projection.DiscardAsync(profileId, snapshot.NextGeneration, stop.Token).ConfigureAwait(false);
            // Local removal only. No OAuth revocation or global Roblox logout endpoint is called.
        }
        finally { lock (_state) _disconnect = null; completion.TrySetResult(); }
    }

    /// <summary>
    /// Refuses a verified player saved on another profile. When adding, returns that profile instead
    /// if it holds this player's saved Roblox session, so the add refreshes it rather than duplicating it.
    /// </summary>
    private async Task<Guid?> Unique(Guid profileId, LinkedIdentity identity, CancellationToken ct, bool addingAccount = false)
    {
        if (vault is not IRobloxAuthorizationIdentityReader metadata)
            throw new AccountLinkException("The protected store cannot safely check duplicate Roblox identities. No session was saved.");
        var existing = await metadata.FindByRobloxUserIdAsync(identity.UserId, ct).ConfigureAwait(false);
        if (existing is null || existing.ProfileId == profileId) return null;
        // An older authorization type is never converted here; it must be removed explicitly first.
        if (addingAccount && existing.Provider == RobloxSessionIdentity.Provider) return existing.ProfileId;
        throw new AccountLinkException(addingAccount
            ? "This Roblox account is already saved in your library with an older type of sign-in, so it was not refreshed. Select its row, choose Remove saved sign-in, then choose Sign in with Roblox."
            : "This Roblox account is already saved on another profile. Disconnect its existing session before saving it here.");
    }
    /// <summary>
    /// Moves this owned add to the saved row of the same verified player. The move happens under the
    /// state gate, so a later Disconnect, Cancel, Dispose or profile removal for that row reaches this operation; like
    /// Begin, it refuses while a disconnect is waiting, and it refuses a row that is being removed. The commit stays
    /// conditional on the row's snapshot.
    /// </summary>
    private async Task<AuthorizationWriteSnapshot> AdoptSavedSessionAsync(Operation operation, Guid profileId, LinkedIdentity identity)
    {
        operation.Token.ThrowIfCancellationRequested();
        lock (_state)
        {
            if (_disposed || operation.Invalidated || !ReferenceEquals(_active, operation)) throw new OperationCanceledException(operation.Token);
            if (_disconnect is not null)
                throw new AccountLinkException("A saved sign-in is being removed, so this account's saved sign-in was not refreshed. Add the account again after that finishes.");
            // A removal holds its rows from before it decides whether an operation owns one until they are gone (BeginProfileRemovalAsync).
            if (_removing.ContainsKey(profileId))
                throw new AccountLinkException("This account is being removed from your library, so its saved sign-in was not refreshed. Add the account again after the removal finishes.") { ProfileRemoved = true };
            operation.ProfileId = profileId;
        }
        var snapshot = await ReadSnapshotAsync(operation).ConfigureAwait(false);
        // The identity index named this row; its own record must still hold this player's Roblox session.
        if (snapshot.Record?.Validated() is not { } saved || !SameIdentity(saved, identity))
            throw AuthorizationChanged();
        return snapshot;
    }
    private async Task RequireProfile(Guid profileId, CancellationToken ct)
    {
        if (await profiles.FindAsync(profileId, ct).ConfigureAwait(false) is null)
            throw new AccountLinkException("This saved profile no longer exists. No session was attached.") { ProfileRemoved = true };
    }
    private async Task<AuthorizationWriteSnapshot> ReadSnapshotAsync(Operation operation)
    {
        if (vault is not ISnapshotAuthorizationVault conditional) throw new AccountLinkException("The protected store cannot atomically preserve this saved sign-in. Update Zenith before signing in again; existing files were preserved.");
        var snapshot = await conditional.ReadWriteSnapshotAsync(operation.ProfileId, operation.Token).ConfigureAwait(false); Ensure(operation);
        if (snapshot.ProfileId != operation.ProfileId || snapshot.Record is { } record && record.ProfileId != operation.ProfileId)
            throw AuthorizationChanged();
        return snapshot;
    }
    private static AccountLinkException AuthorizationChanged() => new("This saved sign-in changed in another operation. Reload the account and try again; the newer authorization was preserved.");
    private static void RequireMutation(AuthorizationMutationResult result, long expectedGeneration)
    { if (result.Status != AuthorizationMutationStatus.Applied || result.CommittedGeneration != expectedGeneration) throw AuthorizationChanged(); }
    private async Task CompleteMutationAsync(Operation operation, bool succeeded)
    {
        try
        {
            if (!succeeded && operation.FinalMutationIssued
                && !await FenceFailedSessionMutationAsync(operation.ProfileId, operation.FinalMutationId).ConfigureAwait(false))
                throw new AccountLinkException("The interrupted sign-in could not confirm its authorization cleanup. Protected records were preserved; review the saved sign-in before retrying.");
        }
        finally { Complete(operation); }
    }
    private static void Compatible(AuthorizationRecord? record)
    {
        if (record is not null && record.Provider != RobloxSessionIdentity.Provider)
            throw new AccountLinkException("This profile retains an older authorization type. Disconnect it explicitly before saving a Roblox session; it was not converted or changed.");
    }
    private static void ValidateIdentity(LinkedIdentity identity)
    {
        if (identity is null || identity.UserId <= 0 || identity.Issuer != RobloxSessionIdentity.Issuer
            || identity.Subject != identity.UserId.ToString(CultureInfo.InvariantCulture) || identity.Username is not { Length: > 0 and <= 20 }
            || !PublicRobloxUsername.HasValidCharacters(identity.Username))
            throw new AccountLinkException("The Roblox session identity is incomplete or inconsistent. No replacement session was saved.");
    }
    private static bool SameIdentity(AuthorizationRecord record, LinkedIdentity identity) => record.Provider == RobloxSessionIdentity.Provider
        && record.VerifiedUserId == identity.UserId && record.Issuer == identity.Issuer && record.Subject == identity.Subject;
    private static AuthorizationMetadata Metadata(AuthorizationRecord record) => new(record.ProfileId, record.ProfileId.ToString("N"), record.Provider,
        record.Issuer, record.Subject, record.VerifiedUserId, record.VerifiedUsername, record.Generation, record.Health, record.ExpiresUtc, DateTimeOffset.UtcNow);
    private Operation Begin(Guid profileId, SavedSessionOperationKind kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); if (profileId == Guid.Empty) throw new ArgumentException("Choose a saved profile.");
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // A removal holds its rows until they and their saved sign-ins are gone (BeginProfileRemovalAsync): nothing starts on one.
            if (_removing.ContainsKey(profileId))
                throw new AccountLinkException("This account is being removed from your library. Its saved sign-in was not used or changed.") { ProfileRemoved = true };
            if (_active is not null || _disconnect is not null)
                throw new AccountLinkException("Another saved-session operation is in progress. Finish or cancel it first.") { OperationInProgress = true };
            return _active = new(profileId, kind, CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token));
        }
    }
    private void Ensure(Operation operation)
    {
        operation.Token.ThrowIfCancellationRequested();
        lock (_state) if (_disposed || operation.Invalidated || !ReferenceEquals(_active, operation)) throw new OperationCanceledException(operation.Token);
    }
    private void Complete(Operation operation)
    { lock (_state) if (ReferenceEquals(_active, operation)) _active = null; operation.Dispose(); operation.Completion.TrySetResult(); }
    public void Cancel()
    {
        Operation? active; lock (_state) { active = _active; if (active is not null) active.Invalidated = true; }
        active?.Cancel();
    }
    public ValueTask DisposeAsync()
    {
        lock (_state)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true; if (_active is not null) _active.Invalidated = true;
            return new(_disposeTask = StopAsync(_active?.Completion.Task, _disconnect?.Task));
        }
    }
    private async Task StopAsync(Task? active, Task? disconnect)
    {
        // Do not invoke arbitrary cancellation callbacks while the state gate is held.
        await Task.Yield();
        _shutdown.Cancel();
        if (active is not null) await active.ConfigureAwait(false);
        if (disconnect is not null) await disconnect.ConfigureAwait(false);
        _shutdown.Dispose();
    }
    private sealed class Operation(Guid profileId, SavedSessionOperationKind kind, CancellationTokenSource stop) : IDisposable
    {
        private int _finished;
        // Changed only under the service state gate, by AdoptSavedSessionAsync.
        public Guid ProfileId { get; set; } = profileId;
        public Guid? RelatedProfileId { get; set; }
        public SavedSessionOperationKind Kind { get; } = kind;
        public CancellationToken Token { get; } = stop.Token;
        public bool Invalidated;
        public Guid FinalMutationId { get; } = Guid.NewGuid();
        public bool FinalMutationIssued;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Cancel() { try { stop.Cancel(); } catch (ObjectDisposedException) when (Volatile.Read(ref _finished) != 0) { } }
        public void Dispose() { Volatile.Write(ref _finished, 1); stop.Dispose(); }
    }
    private sealed class HandoffInspectionException(RobloxHandoffProbeOutcome outcome) : Exception("The diagnostic handoff authorization binding could not be preserved.")
    { public RobloxHandoffProbeOutcome Outcome { get; } = outcome; }
}
