using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed partial class RobloxSessionLinkService : IRobloxShareLinkResolver
{
    /// <summary>
    /// One owned operation: read the saved session, resolve, dispose the credential. No sign-in window opens, nothing
    /// is written to the vault and no client is launched. Provider failures become outcomes, never forwarded text.
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
            using var credential = new RobloxSessionCredential(record.SessionCookie, record.ExpiresUtc == DateTimeOffset.MaxValue ? null : record.ExpiresUtc);
            // The scope binds any kept anti-forgery token to this profile and its verified account, never another identity.
            var scope = new RobloxShareLinkTokenScope(profileId, record.VerifiedUserId);
            try { return await shareLinks.ResolveAsync(link, credential, scope, operation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { Ensure(operation); return new(RobloxShareLinkOutcome.Cancelled); }
            catch (Exception) { Ensure(operation); return new(RobloxShareLinkOutcome.Unavailable); } // A transport exception may quote a code: never forward it.
        }
        finally { Complete(operation); }
    }
}
