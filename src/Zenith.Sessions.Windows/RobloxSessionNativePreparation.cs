using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed partial class RobloxSessionLinkService
{
    public async Task<NativeHandoffPreparationResult> PrepareNativeHandoffAsync(LaunchIntent intent,
        IRobloxNativeHandoffCapture browser, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent); ArgumentNullException.ThrowIfNull(browser); ct.ThrowIfCancellationRequested();
        if (intent.OperationId == Guid.Empty || intent.ProfileId == Guid.Empty || intent.UserId <= 0
            || intent.AuthorizationGeneration <= 0 || intent.ProfileRevision < 0 || !LaunchCoordinator.HasValidDestination(intent))
            return new(NativeHandoffPreparationStatus.RequestChanged);
        Operation operation;
        try { operation = Begin(intent.ProfileId, SavedSessionOperationKind.NativeLaunchPreparation, ct); }
        // Both refusals happen before any grant is reserved; a row being removed is reported as such, not as another preparation.
        catch (AccountLinkException refused) { return new(refused.ProfileRemoved ? NativeHandoffPreparationStatus.ProfileRemoved : NativeHandoffPreparationStatus.Busy); }
        Guid pendingOwner = Guid.NewGuid(); bool pendingIssued = false, success = false;
        CancellationTokenRegistration preparationCancellation = default;
        NativeHandoffContext? context = null; NativeHandoffCandidate? candidate = null;
        PrivateServerLaunchLease? privateLease = null;
        NativeBrowserPreparationFacts? browserFacts = null;
        NativeHandoffPreparationResult result = new(NativeHandoffPreparationStatus.Failed);
        try
        {
            result = await PrepareOwnedAsync().ConfigureAwait(false);
            Ensure(operation);
            success = result.Status == NativeHandoffPreparationStatus.Ready;
        }
        catch (HandoffInspectionException error)
        { result = new(error.Outcome == RobloxHandoffProbeOutcome.IdentityMismatch ? NativeHandoffPreparationStatus.IdentityMismatch : NativeHandoffPreparationStatus.AuthorizationChanged); }
        catch (OperationCanceledException) { result = new(NativeHandoffPreparationStatus.Cancelled); }
        catch (RobloxSessionException error)
        { result = new(error.Failure is RobloxSessionFailure.InvalidSession or RobloxSessionFailure.Challenge ? NativeHandoffPreparationStatus.SignInRequired : NativeHandoffPreparationStatus.Failed); }
        catch (AccountLinkException) { result = new(NativeHandoffPreparationStatus.AuthorizationChanged); }
        catch (PrivateServerConflictException) { result = new(NativeHandoffPreparationStatus.RequestChanged); }
        catch (Exception)
        {
            // Browser, HTTP, filesystem and provider exceptions may contain protected values.
            // Never forward their text, inner exception or a raw handoff into diagnostics.
            result = new(NativeHandoffPreparationStatus.Failed);
        }
        finally
        {
            try
            {
                if (!success)
                {
                    candidate?.Dispose(); context?.Dispose();
                    bool fenced = true;
                    if (context is not null) fenced = await FenceFailedSessionMutationAsync(intent.ProfileId, context.OperationId).ConfigureAwait(false);
                    if (pendingIssued) fenced &= await FenceFailedSessionMutationAsync(intent.ProfileId, pendingOwner).ConfigureAwait(false);
                    if (!fenced) result = new(NativeHandoffPreparationStatus.CleanupFailed);
                }
            }
            finally { privateLease?.Dispose(); preparationCancellation.Dispose(); Complete(operation); }
        }
        return result with { BrowserFacts = browserFacts };

        async Task<NativeHandoffPreparationResult> PrepareOwnedAsync()
        {
            if (vault is not ISnapshotAuthorizationVault conditional) return new(NativeHandoffPreparationStatus.AuthorizationChanged);
            var profile = await profiles.FindAsync(intent.ProfileId, operation.Token).ConfigureAwait(false); Ensure(operation);
            if (profile is null || profile.Revision != intent.ProfileRevision
                || !intent.HasExplicitDestination && profile.PreferredPlaceId != intent.PlaceId)
                return new(NativeHandoffPreparationStatus.RequestChanged);
            var snapshot = await ReadSnapshotAsync(operation).ConfigureAwait(false);
            var previous = snapshot.Record;
            if (previous is null || previous.Provider != RobloxSessionIdentity.Provider || previous.Health != AuthorizationHealth.Linked
                || previous.ExpiresUtc <= DateTimeOffset.UtcNow || !AuthorizationIdentity.HasVerifiedContext(previous.Provider, previous.Issuer, previous.Subject, previous.VerifiedUserId))
                return new(NativeHandoffPreparationStatus.SignInRequired);
            if (previous.VerifiedUserId != intent.UserId || previous.Generation != intent.AuthorizationGeneration
                || snapshot.HighestAllocated != previous.Generation || snapshot.LastCommitted != previous.Generation
                || snapshot.NextGeneration >= long.MaxValue - 1)
                return new(NativeHandoffPreparationStatus.AuthorizationChanged);
            // Refused before the pending grant is committed, so the saved sign-in stays Linked. CleanupFailed
            // keeps its status so the configured provider still poisons itself; a storage refusal does not.
            if (browser.Refusal is { } refused)
                return new(refused switch
                {
                    RobloxHandoffProbeOutcome.CleanupFailed => NativeHandoffPreparationStatus.CleanupFailed,
                    RobloxHandoffProbeOutcome.Cancelled => NativeHandoffPreparationStatus.Cancelled,
                    RobloxHandoffProbeOutcome.StorageUnavailable => NativeHandoffPreparationStatus.StorageUnavailable,
                    _ => NativeHandoffPreparationStatus.Busy
                });
            var pending = previous with { Generation = snapshot.NextGeneration, Health = AuthorizationHealth.SignInRequired };
            if (intent.PrivateServer is { } reference)
            {
                if (privateServers is null || browser is SelectableNativeHandoffCapture { Mode: not NativeLaunchMode.OfficialPage })
                    return new(NativeHandoffPreparationStatus.RequestChanged);
                privateLease = await privateServers.AcquireForLaunchAsync(reference, intent.PlaceId, operation.Token).ConfigureAwait(false);
                Ensure(operation);
            }
            context = new(intent.OperationId, intent.ProfileId, intent.UserId, pending.Generation,
                new(intent.PlaceId, intent.RequestedJobId, intent.RequestedServerKind, intent.PrivateServer), privateLease);
            privateLease = null; // The context owns the lease until dispatch or cancellation.
            pendingIssued = true;
            RequireMutation(await conditional.TryCommitSnapshotAsync(snapshot, pending, pendingOwner, operation.Token).ConfigureAwait(false), pending.Generation);
            Ensure(operation);
            preparationCancellation = operation.Token.Register(context.Dispose);
            using var credential = new RobloxSessionCredential(previous.SessionCookie,
                previous.ExpiresUtc == DateTimeOffset.MaxValue ? null : previous.ExpiresUtc);
            using var captured = await browser.CaptureAsync(context, credential, operation.Token).ConfigureAwait(false);
            // Record only closed enums before cancellation can throw; the capture itself is disposed
            // by this owner and never retained as diagnostic history.
            try
            { if (captured is not null) browserFacts = new NativeBrowserPreparationFacts(captured.Outcome, captured.ShapeStatus, DateTimeOffset.UtcNow, captured.Mode).Validated(); }
            catch (Exception) { /* Diagnostic capture cannot change preparation, cleanup or grant decisions. */ }
            Ensure(operation);
            if (captured is null) return new(NativeHandoffPreparationStatus.CaptureRejected);
            if (intent.PrivateServer is not null && (captured.Mode != NativeLaunchMode.OfficialPage || captured.Outcome != RobloxHandoffProbeOutcome.CapturedAndPrevented))
                return new(NativeHandoffPreparationStatus.CaptureRejected);
            // A captured official-page request and a request built from an issued ticket (R-api-7) are prepared the same way.
            if (captured.Outcome is not (RobloxHandoffProbeOutcome.CapturedAndPrevented or RobloxHandoffProbeOutcome.TicketIssued))
                return new(captured.Outcome switch
                {
                    RobloxHandoffProbeOutcome.CleanupFailed => NativeHandoffPreparationStatus.CleanupFailed,
                    RobloxHandoffProbeOutcome.SignInRequired => NativeHandoffPreparationStatus.SignInRequired,
                    RobloxHandoffProbeOutcome.Cancelled => NativeHandoffPreparationStatus.Cancelled,
                    _ => NativeHandoffPreparationStatus.CaptureRejected
                });
            if (captured.Candidate is not { IsConsumed: false } owned || !ReferenceEquals(owned.Context, context))
                return new(NativeHandoffPreparationStatus.CaptureRejected);
            if (owned.IsExpired) return new(NativeHandoffPreparationStatus.Expired);
            if (captured.CurrentCredential is not { } current || current.ExpiresUtc is { } expiry && expiry <= DateTimeOffset.UtcNow)
                return new(NativeHandoffPreparationStatus.SignInRequired);
            if (await profiles.FindAsync(intent.ProfileId, operation.Token).ConfigureAwait(false) != profile)
                return new(NativeHandoffPreparationStatus.RequestChanged);
            if (!await IsPendingInspectionAsync(pending, operation).ConfigureAwait(false))
                return new(NativeHandoffPreparationStatus.AuthorizationChanged);
            // Reuse same-player/rotation/duplicate checks and the existing protected projection
            // transaction. The new context, not a caller-supplied ID, owns this final mutation.
            var identity = await VerifyAndSave(operation, current, pending, handoffInspection: true,
                inspectionMutationId: context.OperationId).ConfigureAwait(false);
            Ensure(operation);
            var finalGrant = await ReadSnapshotAsync(operation).ConfigureAwait(false);
            var finalRecord = finalGrant.Record;
            if (finalRecord is null || finalGrant.HighestMutationId != context.OperationId
                || finalRecord.Generation != pending.Generation + 1 || finalGrant.HighestAllocated != finalRecord.Generation
                || finalGrant.LastCommitted != finalRecord.Generation || finalRecord.Health != AuthorizationHealth.Linked
                || !SameIdentity(finalRecord, identity)) return new(NativeHandoffPreparationStatus.AuthorizationChanged);
            var finalProfile = await profiles.FindAsync(intent.ProfileId, operation.Token).ConfigureAwait(false); Ensure(operation);
            // Projection may update only verified name/ID and its single optimistic revision.
            // An unrelated edit (including edit-then-revert) never silently rebases this request.
            bool projected = projection is not null && (profile.Username != identity.Username || profile.RobloxUserId != identity.UserId);
            if (finalProfile is null || finalProfile.Revision != profile.Revision + (projected ? 1 : 0)
                || finalProfile != (profile with { Username = finalProfile.Username, RobloxUserId = finalProfile.RobloxUserId,
                    Revision = finalProfile.Revision, UpdatedUtc = finalProfile.UpdatedUtc })
                || projected && (finalProfile.Username != identity.Username || finalProfile.RobloxUserId != identity.UserId))
                return new(NativeHandoffPreparationStatus.RequestChanged);
            if (owned.IsExpired) return new(NativeHandoffPreparationStatus.Expired);
            Ensure(operation);
            candidate = captured.TakeCandidate()!;
            var finalIntent = intent with { ProfileRevision = finalProfile.Revision, AuthorizationGeneration = finalRecord.Generation };
            return new(NativeHandoffPreparationStatus.Ready, new(candidate, finalGrant, finalIntent));
        }
    }
}
