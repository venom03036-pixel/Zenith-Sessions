using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

/// <summary>Current profile metadata and last-known grants, never current execution authority.</summary>
public sealed record BundleMemberReview(Guid ProfileId, AccountProfile? Profile, LaunchAccountBinding? Binding,
    long? KnownUserId, string Username, string SignInState)
{
    public bool IsMissing => Profile is null;
    public string Label => Profile is null ? "Missing saved profile" : Username.Length > 0 ? Username : Profile.Label;
    public string Identity => KnownUserId is { } id ? "Last-known player ID " + id : "Player identity unknown";
    public override string ToString() => "BundleMemberReview { Profile metadata and last-known identity only }";
}

/// <summary>Bounded reads of requested IDs only. Starts no worker/timer and reads no session secret.</summary>
public sealed class BundleRecipientReader(IAccountRepository profiles, IAuthorizationMetadataReader identities)
{
    public Task<IReadOnlyList<BundleMemberReview>> ReadAsync(IReadOnlyList<Guid> profileIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profileIds);
        if(profileIds.Count is <1 or >1000 || profileIds.Any(id=>id==Guid.Empty) || profileIds.Distinct().Count()!=profileIds.Count)
            throw new ArgumentException("Choose a bounded, distinct set of saved profiles.");
        Guid[] requested=profileIds.ToArray();
        return Task.Run(async () =>
        {
            var metadata=new Dictionary<Guid,AuthorizationMetadata>();
            for(int start=0;start<requested.Length;start+=200)
            {
                ct.ThrowIfCancellationRequested();var batch=requested.Skip(start).Take(200).ToArray();
                var summaries=await identities.ReadSummariesAsync(batch,ct).ConfigureAwait(false);
                if(summaries.Count>batch.Length) throw new InvalidDataException("Unexpected identity summary count.");
                foreach(var summary in summaries)
                    if(!batch.Contains(summary.ProfileId)||!metadata.TryAdd(summary.ProfileId,summary))
                        throw new InvalidDataException("Unexpected identity summary ownership.");
            }
            var output=new BundleMemberReview[requested.Length];var now=DateTimeOffset.UtcNow;
            for(int index=0;index<requested.Length;index++)
            {
                ct.ThrowIfCancellationRequested();Guid id=requested[index];
                var profile=await profiles.FindAsync(id,ct).ConfigureAwait(false);
                if(profile is not null && profile.Validated().Id!=id) throw new InvalidDataException("Unexpected profile ownership.");
                metadata.TryGetValue(id,out var summary);
                bool known=summary is not null && summary.Generation>0
                    && AuthorizationIdentity.HasVerifiedContext(summary.Provider,summary.Issuer,summary.Subject,summary.VerifiedUserId);
                bool eligible=known && summary!.Provider==RobloxSessionIdentity.Provider && summary.Health==AuthorizationHealth.Linked && summary.ExpiresUtc>now;
                output[index]=new(id,profile,profile is not null&&eligible?new(summary!.VerifiedUserId,summary.Generation):null,
                    known?summary!.VerifiedUserId:null,known?summary!.VerifiedUsername:"",
                    profile is null?"Profile removed · retained in bundle":eligible?"Saved sign-in · checked again before launch":"Sign-in required");
            }
            ct.ThrowIfCancellationRequested();return (IReadOnlyList<BundleMemberReview>)Array.AsReadOnly(output);
        },ct);
    }
}
