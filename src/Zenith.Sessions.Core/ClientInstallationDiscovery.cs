namespace Zenith.Sessions.Core;

// Voidstrap (a Bloxstrap fork with the same layout) was added for R-launch-17; the bootstrappers are listed in BootstrapperProducts.All.
public enum ClientInstallationProduct { Roblox, Bloxstrap, Fishstrap, Froststrap, Voidstrap }
public enum ClientInstallationSnapshotStatus { Complete, Partial, Unavailable, Deferred }
public enum ClientInstallationFileState { PresentUnverified, Missing, AccessDenied, MetadataUnavailable, SkippedUnsafe, TooLarge }
public enum ClientInstallationRegistryHint { RobloxProtocol, RobloxPlayerProtocol, BloxstrapInstallLocation, FishstrapInstallLocation, FroststrapInstallLocation, VoidstrapInstallLocation }
public enum ClientInstallationHintSource { RobloxProtocol, RobloxPlayerProtocol, UninstallRegistry, StandardInstallPath, VersionDirectory }
public enum ClientInstallationIssueCode { InventoryUnavailable, LocalAppDataUnavailable, EnvironmentRedirected, NoMatchingCandidates, InvalidHint, AccessDenied, MetadataUnavailable, UnsafePath, LimitReached, ConflictingProtocols, MultipleCandidates }
public sealed record ClientInstallationEvidence(ClientInstallationHintSource Source,ClientInstallationProduct Product);
public sealed record ClientInstallationCandidate(ClientInstallationProduct Product,string ExecutablePath,string? ProductName,string? FileVersion,
    ClientInstallationFileState FileState,IReadOnlyList<ClientInstallationEvidence> Evidence)
{
    public CapabilityState Authenticity=>CapabilityState.Unverified;
    public CapabilityState Compatibility=>CapabilityState.Unverified;
    public CapabilityState NamedAccountLaunch=>CapabilityState.Unavailable;
    public string EvidenceLabel=>"Unverified installation metadata. Filename, registry and version hints do not establish authenticity, compatibility, account selection or arrival.";
}
public sealed record ClientInstallationIssue(ClientInstallationIssueCode Code,ClientInstallationProduct? Product=null,ClientInstallationHintSource? Source=null)
{
    public string Summary=>Code switch
    {
        ClientInstallationIssueCode.LocalAppDataUnavailable=>"The current-user application-data location is unavailable; installation absence cannot be established.",
        ClientInstallationIssueCode.EnvironmentRedirected=>"Application data appears redirected by process isolation. Missing files here do not establish absence from the user's normal profile.",
        ClientInstallationIssueCode.NoMatchingCandidates=>"The bounded current-user hints did not establish a present executable. Redirected, portable or other installation locations may still exist.",
        ClientInstallationIssueCode.InvalidHint=>"An installation hint was malformed, unsupported or oversized. Its raw value was discarded.",
        ClientInstallationIssueCode.AccessDenied=>"Windows denied a read-only installation metadata query. Absence cannot be established.",
        ClientInstallationIssueCode.MetadataUnavailable=>"Some executable version metadata could not be read. The candidate remains unverified.",
        ClientInstallationIssueCode.UnsafePath=>"A redirected, nonlocal or otherwise unsafe metadata path was skipped.",
        ClientInstallationIssueCode.LimitReached=>"Discovery reached a bounded query or result limit. Additional candidates may exist.",
        ClientInstallationIssueCode.ConflictingProtocols=>"Roblox protocol registrations point to different executables. No provider was selected.",
        ClientInstallationIssueCode.MultipleCandidates=>"Multiple executable candidates remain for a product. A row is suggested only when exactly one present native Roblox player remains, or the Roblox registrations name one.",
        _=>"Installation discovery was unavailable for part of the inventory. Absence cannot be established."
    };
}
public sealed record ClientInstallationSnapshot(DateTimeOffset ObservedUtc,ClientInstallationSnapshotStatus Status,
    IReadOnlyList<ClientInstallationCandidate> Candidates,IReadOnlyList<ClientInstallationIssue> Issues)
{
    public bool HasConflicts=>Issues.Any(issue=>issue.Code is ClientInstallationIssueCode.ConflictingProtocols or ClientInstallationIssueCode.MultipleCandidates);
    public string Summary=>Status switch
    {
        ClientInstallationSnapshotStatus.Deferred=>"Another installation snapshot is still running. No additional scan was queued.",
        ClientInstallationSnapshotStatus.Unavailable=>"Installation discovery is unavailable. Native account launch remains unavailable.",
        _=>Candidates.Count==0?"No matching candidates were found within the locations checked. This does not establish that no client is installed."
            :"Read-only installation candidates found. Authenticity and compatibility are unverified; native account launch remains unavailable."
    };
}
public interface IClientInstallationDiscovery
{
    Task<ClientInstallationSnapshot> DiscoverAsync(CancellationToken ct);
}

public sealed record ClientInstallationFileMetadata(ClientInstallationFileState State,string? ProductName=null,string? FileVersion=null);
public sealed record ClientInstallationVersionDirectories(IReadOnlyList<string> Names,bool Truncated=false);
/// <summary>Query-only inventory seam. Implementations bound reads; discovery calls it from one owned worker, never the UI thread.</summary>
public interface IClientInstallationInventorySource
{
    string? GetLocalAppData(CancellationToken ct);
    string? ReadRegistryValue(ClientInstallationRegistryHint hint,CancellationToken ct);
    ClientInstallationVersionDirectories ReadVersionDirectories(string root,int maximumEntries,CancellationToken ct);
    ClientInstallationFileMetadata InspectExecutable(string path,CancellationToken ct);
}
