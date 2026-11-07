using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed partial class RobloxSessionLinkService : IRobloxShareLinkResolver
{
    /// <summary>
    /// One owned operation: read the saved session, resolve, dispose the credential. No sign-in window opens and no
    /// client is launched. The vault is written only when Roblox refuses the saved session (SignInRequired): that exact
    /// record is marked as needing sign-in, like an interrupted saved-session check, so the account row and launch
    /// eligibility agree with this answer. Provider failures become outcomes, never forwarded text.
    /// </summary>
    public async Task<RobloxShareLinkResolution> ResolveShareLinkAsync(Guid profileId, RobloxShareLink link, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(link); ct.ThrowIfCancellationRequested();
        if (shareLinks is null) return new(RobloxShareLinkOutcome.Unavailable);
        if (profileId == Guid.Empty) return new(RobloxShareLinkOutcome.Invalid);
        Operation operation;
        try { operation = Begin(profileId, ct); }
        catch (AccountLinkException) { return new(RobloxShareLinkOutcome.Busy); }
        try
        {
            var profile = await profiles.FindAsync(profileId, operation.Token).ConfigureAwait(false); Ensure(operation);
            if (profile is null) return new(RobloxShareLinkOutcome.Invalid);
            var record = await vault.ReadAsync(profileId, operation.Token).ConfigureAwait(false); Ensure(operation);
            if (record is null || record.ProfileId != profileId) return new(RobloxShareLinkOutcome.SignInRequired);
            try { record = record.Validated(); }
            catch (ArgumentException) { return new(RobloxShareLinkOutcome.SignInRequired); }
            if (record.Provider != RobloxSessionIdentity.Provider || record.Health != AuthorizationHealth.Linked
                || record.SessionCookie.Length == 0 || record.ExpiresUtc <= DateTimeOffset.UtcNow) return new(RobloxShareLinkOutcome.SignInRequired);
            RobloxShareLinkResolution resolution;
            using (var credential = new RobloxSessionCredential(record.SessionCookie, record.ExpiresUtc == DateTimeOffset.MaxValue ? null : record.ExpiresUtc))
            {
                try { resolution = await shareLinks.ResolveAsync(link, credential, operation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { Ensure(operation); return new(RobloxShareLinkOutcome.Cancelled); }
                catch (Exception) { Ensure(operation); return new(RobloxShareLinkOutcome.Unavailable); } // A transport exception may quote a code: never forward it.
            }
            if (resolution?.Outcome == RobloxShareLinkOutcome.SignInRequired) await FenceRejectedSessionAsync(operation, record).ConfigureAwait(false);
            return resolution!;
        }
        finally { Complete(operation); }
    }

    /// <summary>
    /// Roblox refused the saved session that was sent. Mark exactly that record as needing sign-in, keeping its protected
    /// bytes as CheckSavedAsync does. A newer grant or reservation from another operation is never overwritten: if the
    /// record changed or the conditional write is refused, nothing is written. A storage failure is not forwarded (its
    /// text could quote protected paths); the SignInRequired answer stands either way, and launch preparation still
    /// fences an unchanged record before any use, so no client can start with the refused session.
    /// </summary>
    private async Task FenceRejectedSessionAsync(Operation operation, AuthorizationRecord sent)
    {
        try
        {
            var snapshot = await ReadSnapshotAsync(operation).ConfigureAwait(false);
            if (snapshot.Record != sent || snapshot.HighestAllocated != sent.Generation || snapshot.LastCommitted != sent.Generation
                || snapshot.NextGeneration >= long.MaxValue - 1) return;
            var fenced = sent with { Generation = snapshot.NextGeneration, Health = AuthorizationHealth.SignInRequired };
            await ((ISnapshotAuthorizationVault)vault).TryCommitSnapshotAsync(snapshot, fenced, Guid.NewGuid(), operation.Token).ConfigureAwait(false);
            Ensure(operation);
        }
        catch (OperationCanceledException) { Ensure(operation); }
        catch (Exception) { /* Storage or a changed record: keep the outcome; nothing else to undo, the write is conditional. */ }
    }
}
