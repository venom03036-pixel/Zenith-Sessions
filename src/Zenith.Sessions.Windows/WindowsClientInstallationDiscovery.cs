using System.ComponentModel;
using System.IO;
using System.Security;
using System.Text;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>One-shot, current-user metadata discovery only. No launch, trust verification, binary extraction or credential access.</summary>
public sealed class WindowsClientInstallationDiscovery : IClientInstallationDiscovery
{
    public const int MaximumFileProbes=128,MaximumCandidates=64,MaximumVersionEntriesPerRoot=8,MaximumIssues=32,MaximumHintCharacters=2048;
    /// <summary>Raw entries (files and folders) examined per Versions root before the newest version folders are chosen.</summary>
    public const int MaximumVersionScanEntries=64;
    private readonly IClientInstallationInventorySource _source;
    private readonly TimeProvider _time;
    private int _active;
    public WindowsClientInstallationDiscovery(IClientInstallationInventorySource? source=null,TimeProvider? time=null)
    {_source=source??new WindowsClientInstallationInventorySource();_time=time??TimeProvider.System;}
    public Task<ClientInstallationSnapshot> DiscoverAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(Interlocked.CompareExchange(ref _active,1,0)!=0)
            return Task.FromResult(new ClientInstallationSnapshot(_time.GetUtcNow(),ClientInstallationSnapshotStatus.Deferred,[],[]));
        return Task.Run(()=>
        {
            try {ct.ThrowIfCancellationRequested();return Capture(ct);}
            finally {Volatile.Write(ref _active,0);}
        },CancellationToken.None);
    }
    private ClientInstallationSnapshot Capture(CancellationToken ct)
    {
        var candidates=new Dictionary<string,(ClientInstallationProduct Product,ClientInstallationFileMetadata Metadata,List<ClientInstallationEvidence> Evidence)>(StringComparer.OrdinalIgnoreCase);
        var inspected=new Dictionary<string,ClientInstallationFileMetadata>(StringComparer.OrdinalIgnoreCase);
        var issues=new List<ClientInstallationIssue>();var roots=new List<(ClientInstallationProduct Product,string Root)>();
        var protocolPaths=new List<string>();bool partial=false;int successfulQueries=0;
        void Issue(ClientInstallationIssueCode code,ClientInstallationProduct? product=null,ClientInstallationHintSource? source=null)
        {
            partial=true;var issue=new ClientInstallationIssue(code,product,source);
            if(issues.Count<MaximumIssues && !issues.Contains(issue)) issues.Add(issue);
        }
        void Probe(string path,ClientInstallationEvidence evidence,bool retainMissing=false)
        {
            ct.ThrowIfCancellationRequested();
            if(!TryNormalizeExecutablePath(path,out var normalized,out var product)) {Issue(ClientInstallationIssueCode.InvalidHint,evidence.Product,evidence.Source);return;}
            if(candidates.TryGetValue(normalized!,out var known))
            {if(!known.Evidence.Contains(evidence) && known.Evidence.Count<16) known.Evidence.Add(evidence);return;}
            if(!inspected.TryGetValue(normalized!,out var metadata))
            {
                if(inspected.Count>=MaximumFileProbes) {Issue(ClientInstallationIssueCode.LimitReached);return;}
                try {metadata=_source.InspectExecutable(normalized!,ct);successfulQueries++;}
                catch(Exception error) when(IsMetadataFailure(error)) {metadata=new(FailureState(error));}
                if(metadata is null || !Enum.IsDefined(metadata.State)) metadata=new(ClientInstallationFileState.MetadataUnavailable);
                metadata=metadata with {ProductName=Display(metadata.ProductName,80),FileVersion=VersionText(metadata.FileVersion)};inspected.Add(normalized!,metadata);
            }
            if(metadata.State is ClientInstallationFileState.AccessDenied) Issue(ClientInstallationIssueCode.AccessDenied,product,evidence.Source);
            else if(metadata.State is ClientInstallationFileState.SkippedUnsafe) Issue(ClientInstallationIssueCode.UnsafePath,product,evidence.Source);
            else if(metadata.State is ClientInstallationFileState.MetadataUnavailable or ClientInstallationFileState.TooLarge) Issue(ClientInstallationIssueCode.MetadataUnavailable,product,evidence.Source);
            if(metadata.State==ClientInstallationFileState.Missing && !retainMissing) return;
            if(candidates.Count>=MaximumCandidates) {Issue(ClientInstallationIssueCode.LimitReached);return;}
            candidates.Add(normalized!,(product,metadata,[evidence]));
        }
        string? local=null;
        try {local=_source.GetLocalAppData(ct);successfulQueries++;}
        catch(Exception error) when(IsMetadataFailure(error)) {Issue(ClientInstallationIssueCode.LocalAppDataUnavailable);}
        if(!TryNormalizeDirectory(local,out local)) Issue(ClientInstallationIssueCode.LocalAppDataUnavailable);
        else
        {
            if(local!.Contains("\\Packages\\",StringComparison.OrdinalIgnoreCase) || local.Contains("\\LocalCache\\",StringComparison.OrdinalIgnoreCase))
                Issue(ClientInstallationIssueCode.EnvironmentRedirected);
            foreach(var product in Enum.GetValues<ClientInstallationProduct>()) roots.Add((product,Path.Combine(local!,product.ToString())));
        }
        foreach(var hint in Enum.GetValues<ClientInstallationRegistryHint>())
        {
            ct.ThrowIfCancellationRequested();string? value;
            try {value=_source.ReadRegistryValue(hint,ct);successfulQueries++;}
            catch(Exception error) when(IsMetadataFailure(error)) {Issue(IsDenied(error)?ClientInstallationIssueCode.AccessDenied:ClientInstallationIssueCode.InvalidHint);continue;}
            if(value is null) continue;
            if(hint is ClientInstallationRegistryHint.RobloxProtocol or ClientInstallationRegistryHint.RobloxPlayerProtocol)
            {
                var source=hint==ClientInstallationRegistryHint.RobloxProtocol?ClientInstallationHintSource.RobloxProtocol:ClientInstallationHintSource.RobloxPlayerProtocol;
                if(!TryParseExecutableCommand(value,out var executable)) {Issue(ClientInstallationIssueCode.InvalidHint,source:source);continue;}
                TryNormalizeExecutablePath(executable!,out executable,out var product);protocolPaths.Add(executable!);
                Probe(executable!,new(source,product),retainMissing:true);
            }
            else
            {
                var product=hint switch {ClientInstallationRegistryHint.BloxstrapInstallLocation=>ClientInstallationProduct.Bloxstrap,
                    ClientInstallationRegistryHint.FishstrapInstallLocation=>ClientInstallationProduct.Fishstrap,_=>ClientInstallationProduct.Froststrap};
                if(!TryNormalizeDirectory(value,out var root)) {Issue(ClientInstallationIssueCode.InvalidHint,product,ClientInstallationHintSource.UninstallRegistry);continue;}
                if(!roots.Any(known=>known.Product==product && string.Equals(known.Root,root,StringComparison.OrdinalIgnoreCase))) roots.Add((product,root!));
                Probe(Path.Combine(root!,product+".exe"),new(ClientInstallationHintSource.UninstallRegistry,product),retainMissing:true);
            }
        }
        if(protocolPaths.Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any()) Issue(ClientInstallationIssueCode.ConflictingProtocols);
        foreach(var root in roots)
        {
            ct.ThrowIfCancellationRequested();var evidence=new ClientInstallationEvidence(ClientInstallationHintSource.StandardInstallPath,root.Product);
            if(root.Product!=ClientInstallationProduct.Roblox) Probe(Path.Combine(root.Root,root.Product+".exe"),evidence);
            foreach(string executable in RobloxNativePlayerFiles.InventoryNames) Probe(Path.Combine(root.Root,"Roblox","Player",executable),evidence);
            if(root.Product==ClientInstallationProduct.Roblox) foreach(string executable in RobloxNativePlayerFiles.InventoryNames) Probe(Path.Combine(root.Root,"Player",executable),evidence);
            // An accepted root near the hint limit can yield an oversized Versions path: that is one invalid hint, not a failed scan.
            if(!TryNormalizeDirectory(Path.Combine(root.Root,"Versions"),out var versions))
            {Issue(ClientInstallationIssueCode.InvalidHint,root.Product,ClientInstallationHintSource.VersionDirectory);continue;}
            ClientInstallationVersionDirectories directories;
            try {directories=_source.ReadVersionDirectories(versions,MaximumVersionEntriesPerRoot,ct);successfulQueries++;}
            catch(Exception error) when(IsMetadataFailure(error)) {Issue(IsDenied(error)?ClientInstallationIssueCode.AccessDenied:error is InvalidDataException?ClientInstallationIssueCode.UnsafePath:ClientInstallationIssueCode.InventoryUnavailable,root.Product);continue;}
            if(directories is null || directories.Names is null) {Issue(ClientInstallationIssueCode.InventoryUnavailable,root.Product);continue;}
            if(directories.Truncated || directories.Names.Count>MaximumVersionEntriesPerRoot) Issue(ClientInstallationIssueCode.LimitReached,root.Product);
            for(int index=0;index<Math.Min(directories.Names.Count,MaximumVersionEntriesPerRoot);index++)
            {
                string name=directories.Names[index];
                if(!ValidVersionName(name)) {Issue(ClientInstallationIssueCode.InvalidHint,root.Product,ClientInstallationHintSource.VersionDirectory);continue;}
                foreach(string executable in RobloxNativePlayerFiles.InventoryNames) Probe(Path.Combine(root.Root,"Versions",name,executable),new(ClientInstallationHintSource.VersionDirectory,root.Product));
            }
        }
        foreach(var product in Enum.GetValues<ClientInstallationProduct>())
            if(candidates.Values.Count(candidate=>candidate.Product==product)>1) Issue(ClientInstallationIssueCode.MultipleCandidates,product);
        if(!candidates.Values.Any(candidate=>candidate.Metadata.State is ClientInstallationFileState.PresentUnverified or ClientInstallationFileState.MetadataUnavailable or ClientInstallationFileState.TooLarge))
            Issue(ClientInstallationIssueCode.NoMatchingCandidates);
        var rows=candidates.OrderBy(pair=>pair.Value.Product).ThenBy(pair=>pair.Key,StringComparer.OrdinalIgnoreCase)
            .Select(pair=>new ClientInstallationCandidate(pair.Value.Product,pair.Key,pair.Value.Metadata.ProductName,pair.Value.Metadata.FileVersion,
                pair.Value.Metadata.State,Array.AsReadOnly(pair.Value.Evidence.ToArray()))).ToArray();
        ct.ThrowIfCancellationRequested();
        return new(_time.GetUtcNow(),successfulQueries==0?ClientInstallationSnapshotStatus.Unavailable:partial?ClientInstallationSnapshotStatus.Partial:ClientInstallationSnapshotStatus.Complete,
            Array.AsReadOnly(rows),Array.AsReadOnly(issues.ToArray()));
    }
    public static bool TryParseExecutableCommand(string? command,out string? executable)
    {
        executable=null;if(command is null || command.Length is <1 or >MaximumHintCharacters || command.Any(char.IsControl)) return false;
        string value=command.Trim();string token;
        if(value.StartsWith('"'))
        {
            int end=value.IndexOf('"',1);if(end<2 || end+1<value.Length && !char.IsWhiteSpace(value[end+1])) return false;
            token=value[1..end];
            // Arguments are discarded, never interpreted, expanded, exposed or used for launching.
            if(value.AsSpan(end+1).IndexOfAny("&|<>^;".AsSpan())>=0) return false;
        }
        else {if(value.Any(char.IsWhiteSpace)) return false;token=value;}
        return TryNormalizeExecutablePath(token,out executable,out _);
    }
    internal static bool TryNormalizeExecutablePath(string? value,[System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? normalized,out ClientInstallationProduct product)
    {
        normalized=null;product=default;if(!TryNormalizeDirectory(value,out var path)) return false;
        string name=Path.GetFileName(path);product=name.ToLowerInvariant() switch
        {"bloxstrap.exe"=>ClientInstallationProduct.Bloxstrap,"fishstrap.exe"=>ClientInstallationProduct.Fishstrap,"froststrap.exe"=>ClientInstallationProduct.Froststrap,_=>ClientInstallationProduct.Roblox};
        if(product==ClientInstallationProduct.Roblox && !RobloxNativePlayerFiles.IsInventoryPlayer(path)) return false;
        normalized=path;return true;
    }
    internal static bool TryNormalizeDirectory(string? value,[System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? normalized)
    {
        normalized=null;
        if(value is null || value.Length is <3 or >MaximumHintCharacters || value!=value.Trim() || value.Any(char.IsControl)
            || !char.IsAsciiLetter(value[0]) || value[1]!=':' || value[2]!='\\' || value.AsSpan(2).Contains(':')
            || value.IndexOfAny(['"','%','&','|','<','>','^',';','?','*','/'])>=0) return false;
        try {StrictUtf8.GetByteCount(value);}
        catch(EncoderFallbackException) {return false;}
        foreach(string segment in value[3..].Split('\\'))
            if(segment is "." or ".." || segment.Length>255 || segment.EndsWith('.') || segment.EndsWith(' ')) return false;
        try {normalized=Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));return true;}
        catch(ArgumentException) {return false;}
    }
    internal static bool ValidVersionName(string? value)=>value is {Length:>8 and <=88} && value.StartsWith("version-",StringComparison.OrdinalIgnoreCase)
        && value.All(c=>char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static readonly UTF8Encoding StrictUtf8=new(false,true);
    private static string? Display(string? value,int maximum)
    {
        if(value is not {Length:>0} || value.Length>maximum || value.Any(char.IsControl)) return null;
        try {StrictUtf8.GetByteCount(value);return value;}
        catch(EncoderFallbackException) {return null;}
    }
    private static string? VersionText(string? value)=>value is {Length:>0 and <=80} && value.All(c=>char.IsAsciiDigit(c) || c is '.' or ',' or ' ')?value:null;
    private static bool IsMetadataFailure(Exception error)=>error is IOException or InvalidDataException or UnauthorizedAccessException or SecurityException or InvalidOperationException or Win32Exception or NotSupportedException;
    private static bool IsDenied(Exception error)=>error is UnauthorizedAccessException or SecurityException || error is Win32Exception {NativeErrorCode:5};
    private static ClientInstallationFileState FailureState(Exception error)=>IsDenied(error)?ClientInstallationFileState.AccessDenied:ClientInstallationFileState.MetadataUnavailable;
}
