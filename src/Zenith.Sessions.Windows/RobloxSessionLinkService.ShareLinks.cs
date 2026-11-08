using System.Data.Common;
using System.Security.Cryptography;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed partial class RobloxSessionLinkService : IRobloxShareLinkResolver
{
    /// <summary>
    /// One owned operation: read the saved session, resolve, dispose the credential. No sign-in window opens and no
    /// client is launched. The vault is written in two cases only. When Roblox replaced the session while answering, the
    /// replacement is kept the way a sign-in is: only after it verifies as the same player, at the next generation, and
    /// only if the record that was sent is still the saved one. When Roblox refuses the saved session (SignInRequired)
    /// and no verified replacement was kept, that exact record is marked as needing sign-in, like an interrupted
    /// saved-session check, so the account row and launch eligibility agree with this answer. Provider failures become
    /// outcomes, never forwarded text, and the replacement never leaves this service.
    /// </summary>
    public async Task<RobloxShareLinkResolution> ResolveShareLinkAsync(Guid profileId, RobloxShareLink link, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(link); ct.ThrowIfCancellationRequested();
        if (shareLinks is null) return new(RobloxShareLinkOutcome.Unavailable);
        if (profileId == Guid.Empty) return new(RobloxShareLinkOutcome.Invalid);
        Operation operation;
        try { operation = Begin(profileId, SavedSessionOperationKind.ShareLinkCheck, ct); }
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
                // The scope binds any kept anti-forgery token to this profile and its verified account, never another identity.
                var scope = new RobloxShareLinkTokenScope(profileId, record.VerifiedUserId);
                try { resolution = await shareLinks.ResolveAsync(link, credential, scope, operation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { Ensure(operation); return new(RobloxShareLinkOutcome.Cancelled); }
                catch (Exception) { Ensure(operation); return new(RobloxShareLinkOutcome.Unavailable); } // A transport exception may quote a code: never forward it.
            }
            if (resolution is null) return new(RobloxShareLinkOutcome.Unavailable);
            try
            {
                using (var rotated = resolution.TakeRotatedCredential())
                    if (rotated is not null) await KeepRotatedSessionAsync(operation, record, rotated).ConfigureAwait(false);
                // A kept replacement changed the record, so this conditional fence then writes nothing.
                if (resolution.Outcome == RobloxShareLinkOutcome.SignInRequired) await FenceRejectedSessionAsync(operation, record).ConfigureAwait(false);
            }
            catch { resolution.Dispose(); throw; } // Cancellation or a defect: the answer's codes are zeroed with it.
            return resolution;
        }
        finally { Complete(operation); }
    }

    /// <summary>
    /// Roblox sent a replacement session cookie with its answer, so the saved one may stop working. Keep it through the
    /// same verify-and-save path as a sign-in: the identity check must name the same player as the record that was sent,
    /// the record must still be that one with no other write reserved, and the commit is conditional on that snapshot at
    /// the next generation. Any other result leaves the saved record as it was; an interrupted commit is fenced to only
    /// this operation's own write, like an interrupted sign-in. After an expected failure, or a fence that could not run, the
    /// share-link answer stands and only the failure's type is reported, never its text (it could quote protected paths).
    /// Any other exception is a defect: it is not absorbed, so the caller's failure path shows and records it.
    /// </summary>
    private async Task KeepRotatedSessionAsync(Operation operation, AuthorizationRecord sent, RobloxSessionCredential candidate)
    {
        bool kept = false;
        try
        {
            var snapshot = await ReadSnapshotAsync(operation).ConfigureAwait(false);
            if (snapshot.Record != sent || snapshot.HighestAllocated != sent.Generation || snapshot.LastCommitted != sent.Generation) return;
            await VerifyAndSave(operation, candidate, sent, writeSnapshot: snapshot).ConfigureAwait(false);
            kept = true;
        }
        catch (OperationCanceledException) { Ensure(operation); }
        // Mismatch, refusal, network or storage: nothing was kept. The conditional write cannot replace a newer grant.
        catch (Exception error) when (IsExpectedSessionFailure(error)) { failed?.Invoke(error.GetType()); }
        finally
        {
            // A fence failure is not thrown either: the record is then the verified replacement or the original. Like a keep
            // failure, only its type is reported, so a cleanup that could not run is not dropped silently.
            if (!kept && operation.FinalMutationIssued) await FenceFailedSessionMutationAsync(operation.ProfileId, operation.FinalMutationId, failed).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Roblox refused the saved session that was sent. Mark exactly that record as needing sign-in, keeping its protected
    /// bytes as CheckSavedAsync does. A newer grant or reservation from another operation is never overwritten: if the
    /// record changed or the conditional write is refused, nothing is written. After a storage failure only its type is
    /// reported, never its text (it could quote protected paths); the SignInRequired answer stands, and launch preparation
    /// still fences an unchanged record before any use, so no client can start with the refused session. Any other
    /// exception is a defect and is not absorbed.
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
        // Storage or a changed record: keep the outcome; nothing else to undo, the write is conditional.
        catch (Exception error) when (IsExpectedSessionFailure(error)) { failed?.Invoke(error.GetType()); }
    }

    /// <summary>
    /// The failures the saved-session upkeep after a share-link answer expects: Roblox refused or could not be reached
    /// (RobloxSessionException), the identity or record no longer matches (AccountLinkException), or the protected store
    /// or library could not be read or written. Everything else, such as InvalidOperationException, ArgumentException or
    /// NullReferenceException, is a defect.
    /// </summary>
    private static bool IsExpectedSessionFailure(Exception error) => error is RobloxSessionException or AccountLinkException
        or IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException or DbException;
}
