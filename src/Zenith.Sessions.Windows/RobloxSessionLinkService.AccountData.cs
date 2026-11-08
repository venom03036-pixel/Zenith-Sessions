using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed partial class RobloxSessionLinkService : IRobloxAccountDataService
{
    /// <summary>
    /// One reviewed account and one fixed endpoint. Fence before sending; revalidate the same identity and save the
    /// newest cookie before exposing observations. A failed or interrupted call never restores possibly stale authority.
    /// This deliberately adds an identity check to every observation (including private detail fields).
    /// </summary>
    public async Task<RobloxAccountResult> ReadAccountAsync(RobloxAccountContext context, RobloxAccountRead kind,
        RobloxAccountContext? target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context); ct.ThrowIfCancellationRequested();
        if (!context.IsValid || !Enum.IsDefined(kind)) throw new ArgumentException("Select a verified saved account.");
        bool friendship = kind is RobloxAccountRead.FriendshipStatus or RobloxAccountRead.RequestFriendship or RobloxAccountRead.AcceptFriendship;
        if (friendship != (target is not null) || target is not null && (!target.IsValid || target.UserId == context.UserId || target.ProfileId == context.ProfileId))
            throw new ArgumentException("Review two different saved accounts for a friendship action.");
        if (accountData is null) return new(RobloxAccountOutcome.Unavailable, context);
        var operation = Begin(context.ProfileId, SavedSessionOperationKind.AccountObservation, ct); bool succeeded = false;
        try
        {
            lock (_state)
            {
                if (target is not null && _removing.ContainsKey(target.ProfileId)) throw AuthorizationChanged();
                operation.RelatedProfileId = target?.ProfileId;
            }
            await RequireProfile(context.ProfileId, operation.Token).ConfigureAwait(false);
            var snapshot = await ReadSnapshotAsync(operation).ConfigureAwait(false);
            var previous = Reviewed(snapshot.Record, context);
            if (snapshot.HighestAllocated != context.Generation || snapshot.LastCommitted != context.Generation) throw AuthorizationChanged();
            if (target is not null)
            {
                await RequireProfile(target.ProfileId, operation.Token).ConfigureAwait(false);
                var other = await ((ISnapshotAuthorizationVault)vault).ReadWriteSnapshotAsync(target.ProfileId, operation.Token).ConfigureAwait(false);
                Ensure(operation); Reviewed(other.Record, target);
                if (other.HighestAllocated != target.Generation || other.LastCommitted != target.Generation) throw AuthorizationChanged();
                lock (_state) if (_removing.ContainsKey(target.ProfileId)) throw AuthorizationChanged();
            }
            var fenceId = Guid.NewGuid();
            var pending = previous with { Generation = snapshot.NextGeneration, Health = AuthorizationHealth.SignInRequired };
            var receipt = await ((ISnapshotAuthorizationVault)vault).TryCommitSnapshotAsync(snapshot, pending, fenceId, operation.Token).ConfigureAwait(false);
            Ensure(operation); RequireMutation(receipt, pending.Generation);
            var fenced = await ReadSnapshotAsync(operation).ConfigureAwait(false);
            if (fenced.Record != pending || fenced.HighestAllocated != pending.Generation || fenced.LastCommitted != pending.Generation || fenced.HighestMutationId != fenceId)
                throw AuthorizationChanged();
            using var credential = new RobloxSessionCredential(previous.SessionCookie, previous.ExpiresUtc);
            // A social mutation must confirm the actor BEFORE changing friendship state, as well as after any
            // response rotation. Keep this validation under the original durable fence; no new authority is published yet.
            using var mutationCredential = kind is RobloxAccountRead.RequestFriendship or RobloxAccountRead.AcceptFriendship
                ? await ConfirmMutationActorAsync(operation, credential, previous).ConfigureAwait(false) : null;
            if (mutationCredential is not null)
            {
                var current = await ReadSnapshotAsync(operation).ConfigureAwait(false);
                if (current.Record != pending || current.HighestAllocated != pending.Generation || current.LastCommitted != pending.Generation || current.HighestMutationId != fenceId)
                    throw AuthorizationChanged();
                var other = await ((ISnapshotAuthorizationVault)vault).ReadWriteSnapshotAsync(target!.ProfileId, operation.Token).ConfigureAwait(false);
                Ensure(operation); Reviewed(other.Record, target);
                if (other.HighestAllocated != target.Generation || other.LastCommitted != target.Generation) throw AuthorizationChanged();
            }
            var sentCredential = mutationCredential ?? credential;
            using var response = await accountData.ReadAsync(kind, context.UserId, target?.UserId, sentCredential, operation.Token).ConfigureAwait(false);
            Ensure(operation);
            if (response.Outcome == RobloxAccountOutcome.Challenge)
                return new(RobloxAccountOutcome.Challenge, context); // No automatic challenge handling or further requests.
            if (response.Outcome == RobloxAccountOutcome.RateLimited && response.RotatedCredential is null && mutationCredential is null)
            {
                // A refused request did not rotate anything. Restore only this exact durable fence, with a new generation.
                var restored = previous with { Generation = fenced.NextGeneration };
                operation.FinalMutationIssued = true;
                var restore = await ((ISnapshotAuthorizationVault)vault).TryCommitSnapshotAsync(fenced, restored, operation.FinalMutationId, operation.Token).ConfigureAwait(false);
                Ensure(operation); RequireMutation(restore, restored.Generation); succeeded = true;
                return new(response.Outcome, context with { Generation = restored.Generation });
            }
            await VerifyAndSave(operation, response.RotatedCredential ?? sentCredential, pending, writeSnapshot: fenced).ConfigureAwait(false);
            Ensure(operation); succeeded = true;
            // Even a 401 on this endpoint is not proof that the saved identity expired when authenticated-user succeeds.
            var outcome = response.Outcome == RobloxAccountOutcome.SignInRequired ? RobloxAccountOutcome.Unavailable : response.Outcome;
            return new(outcome, context with { Generation = fenced.NextGeneration }, outcome == RobloxAccountOutcome.Success ? response.Observation : null);
        }
        finally { await CompleteMutationAsync(operation, succeeded).ConfigureAwait(false); }
    }
    private async Task<RobloxSessionCredential> ConfirmMutationActorAsync(Operation operation, RobloxSessionCredential original, AuthorizationRecord expected)
    {
        using var first = await client.ValidateAsync(original, operation.Token).ConfigureAwait(false);
        Ensure(operation); ValidateIdentity(first.Identity);
        if (!SameIdentity(expected, first.Identity)) throw AuthorizationChanged();
        if (first.RotatedCredential is not { } rotated) return new(original.CopyValue(), original.ExpiresUtc);
        using var second = await client.ValidateAsync(rotated, operation.Token).ConfigureAwait(false);
        Ensure(operation); ValidateIdentity(second.Identity);
        // An unverified second replacement is not sent to a friendship endpoint. A new explicit sign-in is safer
        // than chasing an unbounded rotation chain or letting an action precede identity confirmation.
        if (!SameIdentity(expected, second.Identity) || second.RotatedCredential is not null) throw AuthorizationChanged();
        return new(rotated.CopyValue(), rotated.ExpiresUtc);
    }
    private static AuthorizationRecord Reviewed(AuthorizationRecord? record, RobloxAccountContext context)
    {
        if (record is null || record.ProfileId != context.ProfileId || record.VerifiedUserId != context.UserId || record.Generation != context.Generation
            || record.Provider != RobloxSessionIdentity.Provider || record.Health != AuthorizationHealth.Linked || record.ExpiresUtc <= DateTimeOffset.UtcNow)
            throw AuthorizationChanged();
        return record.Validated();
    }
}
