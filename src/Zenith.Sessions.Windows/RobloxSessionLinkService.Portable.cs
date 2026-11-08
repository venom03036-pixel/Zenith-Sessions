using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public enum PortableSignInOutcome { Imported, ExistingSignInPreserved, IdentityMismatch, SignInRequired, VerificationUnavailable, Conflict }
public interface IPortableSavedSessionImporter
{
    Task<PortableSignInOutcome> ImportPortableSignInAsync(Guid destinationProfileId, PortableSavedSignIn source, CancellationToken ct);
}

public sealed partial class RobloxSessionLinkService : IPortableSavedSessionImporter
{
    private sealed class PortableIdentityMismatchException : Exception;

    /// <summary>Explicit portable import only: new local profile/generation, verified source player, no existing-grant refresh.</summary>
    public async Task<PortableSignInOutcome> ImportPortableSignInAsync(Guid destinationProfileId, PortableSavedSignIn source, CancellationToken ct)
    {
        source.Validated();
        var operation = Begin(destinationProfileId, SavedSessionOperationKind.SignIn, ct); bool succeeded = false;
        try
        {
            await RequireProfile(destinationProfileId, operation.Token).ConfigureAwait(false);
            var snapshot = await ReadSnapshotAsync(operation).ConfigureAwait(false);
            Ensure(operation);
            if (snapshot.Record is not null || snapshot.HighestAllocated != 0 || snapshot.LastCommitted != 0) return PortableSignInOutcome.Conflict;
            if (vault is not IRobloxAuthorizationIdentityReader identities) return PortableSignInOutcome.VerificationUnavailable;
            // Never contact Roblox with an old backup cookie for an identity already saved locally.
            if (await identities.FindByRobloxUserIdAsync(source.VerifiedUserId, operation.Token).ConfigureAwait(false) is not null)
                return PortableSignInOutcome.ExistingSignInPreserved;
            Ensure(operation);
            if (source.ExpiresUtc <= DateTimeOffset.UtcNow) return PortableSignInOutcome.SignInRequired;
            using var credential = source.Open();
            await VerifyAndSave(operation, credential, null, writeSnapshot: snapshot, expectedPortableUserId: source.VerifiedUserId).ConfigureAwait(false);
            Ensure(operation); succeeded = true; return PortableSignInOutcome.Imported;
        }
        catch (PortableIdentityMismatchException) { Ensure(operation); return PortableSignInOutcome.IdentityMismatch; }
        catch (RobloxSessionException ex)
        {
            Ensure(operation);
            return ex.Failure is RobloxSessionFailure.InvalidSession or RobloxSessionFailure.Challenge
                ? PortableSignInOutcome.SignInRequired : PortableSignInOutcome.VerificationUnavailable;
        }
        catch (AccountLinkException) { Ensure(operation); return PortableSignInOutcome.Conflict; }
        finally { await CompleteMutationAsync(operation, succeeded).ConfigureAwait(false); }
    }
}
