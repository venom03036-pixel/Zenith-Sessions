namespace Zenith.Sessions.Core;

/// <summary>Selected-account dispatch authority; profile labels and cached rows cannot grant permission.</summary>
public sealed class LaunchAuthorizationValidator(IAccountRepository profiles,IAccountCredentialVault vault)
{
    public async Task<bool> ValidateAsync(LaunchIntent intent,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(intent.UserId<=0 || intent.AuthorizationGeneration<=0 || !LaunchCoordinator.HasValidDestination(intent)) return false;
        var profile=await profiles.FindAsync(intent.ProfileId,ct).ConfigureAwait(false);
        if(profile is null || profile.Revision!=intent.ProfileRevision || !intent.HasExplicitDestination && profile.PreferredPlaceId!=intent.PlaceId) return false;
        var authorization=await vault.ReadAsync(profile.Id,ct).ConfigureAwait(false);
        if(authorization is null) return false;
        authorization=authorization.Validated();ct.ThrowIfCancellationRequested();
        return authorization.ProfileId==intent.ProfileId
            && AuthorizationIdentity.HasVerifiedContext(authorization.Provider,authorization.Issuer,authorization.Subject,authorization.VerifiedUserId)
            && authorization.Health==AuthorizationHealth.Linked && authorization.VerifiedUserId==intent.UserId
            && authorization.Generation==intent.AuthorizationGeneration && authorization.ExpiresUtc>DateTimeOffset.UtcNow;
    }
}
