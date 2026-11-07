namespace Zenith.Sessions.Core;

/// <summary>
/// Non-secret, last-known identity metadata. It cannot establish current authorization, select a native
/// Roblox account, or associate a public profile with an observed process. Dispatch must read and
/// validate the selected protected record and match its contextual identity and generation again.
/// </summary>
public sealed record AuthorizationMetadata(Guid ProfileId, string CredentialRef, string Provider,
    string Issuer, string Subject, long VerifiedUserId, string VerifiedUsername, long Generation,
    AuthorizationHealth Health, DateTimeOffset ExpiresUtc, DateTimeOffset SavedUtc)
{
    public long KnownUserId => VerifiedUserId;
    public string Username => VerifiedUsername;
    public string EvidenceLabel => "Last-known linked identity; authorization is checked again before use.";
    public bool IsCurrentAuthorization => false;
}

public interface IAuthorizationMetadataReader
{
    Task<AuthorizationMetadata?> GetSummaryAsync(Guid profileId, CancellationToken ct);
    // Implementations accept at most 200 requested profile IDs; never enumerate/decrypt the inventory.
    Task<IReadOnlyList<AuthorizationMetadata>> ReadSummariesAsync(IReadOnlyList<Guid> profileIds, CancellationToken ct);
    Task<AuthorizationMetadata?> FindByIdentityAsync(string provider, string issuer, string subject, CancellationToken ct);
}

public interface IAuthorizationGenerationStore
{
    // Persistently reserves a generation and immediately fences every older protected record.
    // Canceled work may leave gaps; deletion never resets the counter.
    Task<long> AllocateNextGenerationAsync(Guid profileId, long minimumExclusive, CancellationToken ct);
}
