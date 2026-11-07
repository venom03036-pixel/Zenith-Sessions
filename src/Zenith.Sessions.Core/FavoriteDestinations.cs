using System.IO;

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
        // This is a display label, never a place to retain the user's original link or grant material. A new favorite's name meets
        // the whole shared rule; a saved revision keeps the name it was saved under, and the store checks a rename with ValidateName.
        if(!(Revision==0?DisplayNameRules.IsPlain(Name,MaximumNameLength):DisplayNameRules.IsReadable(Name,MaximumNameLength)))
            throw new ArgumentException(NameRule);
        if(JobId is not null && !RobloxRequestedDestination.TryNormalizeJobId(JobId,out _))
            throw new ArgumentException("A favorite server ID must be a nonempty dashed GUID.");
        return this with {Name=Name.Trim(),JobId=JobId is null?null:Guid.ParseExact(JobId,"D").ToString("D")};
    }
    /// <summary>Checks a new or changed favorite name against the shared display-name rule and returns it trimmed.</summary>
    public static string ValidateName(string? name)=>DisplayNameRules.IsPlain(name,MaximumNameLength)?name.Trim():throw new ArgumentException(NameRule);
    private const string NameRule="Use a favorite name of 1 to 80 characters on one line, without links, access codes or credentials.";
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
