using System.IO;
using System.Text;

namespace Zenith.Sessions.Core;

/// <summary>Named destination metadata, never a credential, reservation or observation of arrival.</summary>
public sealed record FavoriteDestination(Guid Id,string Name,long PlaceId,string? JobId=null,long Revision=0,Guid ChangeId=default)
{
    public const int MaximumNameLength=80;
    public const int MaximumCount=500;
    public FavoriteDestination Validated()
    {
        if(Id==Guid.Empty || PlaceId<=0 || Revision<0) throw new ArgumentException("The favorite identity, place or revision is invalid.");
        if((Revision==0)!=(ChangeId==Guid.Empty)) throw new ArgumentException("The favorite revision and change identity are inconsistent.");
        if(Name is null || Name.Length>MaximumNameLength || string.IsNullOrWhiteSpace(Name)
            || Name.Any(c=>char.IsControl(c) || c is '\u2028' or '\u2029'))
            throw new ArgumentException("Use a favorite name of 1 to 80 characters without line breaks or controls.");
        try {new UTF8Encoding(false,true).GetByteCount(Name);}
        catch(EncoderFallbackException) {throw new ArgumentException("The favorite name contains invalid Unicode.");}
        // This is a display label, never a place to retain the user's original link or grant material.
        if(Name.Contains("://",StringComparison.Ordinal) || Name.Contains("www.",StringComparison.OrdinalIgnoreCase)
            || Name.Contains("roblox:",StringComparison.OrdinalIgnoreCase) || Name.Contains("roblox-player:",StringComparison.OrdinalIgnoreCase)
            || Name.Contains(".ROBLOSECURITY",StringComparison.OrdinalIgnoreCase) || Name.Contains("Bearer ",StringComparison.OrdinalIgnoreCase)
            || SensitiveNameMarkers.Any(marker=>Name.Contains(marker,StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Use a display name, without links, access codes or credentials.");
        if(JobId is not null && !RobloxRequestedDestination.TryNormalizeJobId(JobId,out _))
            throw new ArgumentException("A favorite server ID must be a nonempty dashed GUID.");
        return this with {Name=Name.Trim(),JobId=JobId is null?null:Guid.ParseExact(JobId,"D").ToString("D")};
    }
    private static readonly string[] SensitiveNameMarkers=["privateServerLinkCode=","reservedServerAccessCode=","accessCode=","linkCode=",
        "refresh_token","access_token","client_secret","authorization=","cookie="];
    public RobloxRequestedDestination RequestedDestination=>new(Validated().PlaceId,JobId);
    public string DestinationSummary=>$"Place {PlaceId}"+(JobId is null?" · any eligible server":$" · server {JobId}");
    public override string ToString()=>$"Favorite destination {{ Id = {Id}, PlaceId = {PlaceId}, HasServer = {JobId is not null}, Revision = {Revision}, Arrival = unverified }}";
}

/// <summary>A bounded, immutable view of committed metadata. Revision belongs to the file, not authorization.</summary>
public sealed record FavoriteDestinationSnapshot(long Revision,IReadOnlyList<FavoriteDestination> Destinations,string? Notice=null);

public interface IFavoriteDestinationStore
{
    Task<FavoriteDestinationSnapshot> LoadAsync(CancellationToken ct);
    Task<FavoriteDestination> SaveAsync(FavoriteDestination destination,CancellationToken ct);
    Task DeleteAsync(Guid id,long expectedRevision,Guid expectedChangeId,CancellationToken ct);
}

public sealed class FavoriteDestinationConflictException() : Exception("This favorite changed elsewhere. Refresh favorites before saving or deleting it.");
public sealed class FavoriteDestinationCapacityException() : Exception("The 500-destination favorites storage limit is reached. Remove a favorite before adding another; saved accounts are not limited.");
public enum FavoriteDestinationStorageProblem { Damaged, UnsupportedVersion, RecoveryBlocked }
public sealed class FavoriteDestinationStorageException(FavoriteDestinationStorageProblem problem,string message) : IOException(message)
{
    public FavoriteDestinationStorageProblem Problem {get;}=problem;
}
