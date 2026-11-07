using System.ComponentModel;
using System.IO;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class ClientInstallationDiscoveryTests
{
    private const string Local=@"C:\Synthetic\Local";
    private const string Blox=@"C:\Synthetic\Local\Bloxstrap\Bloxstrap.exe";
    [Theory]
    [InlineData("\"C:\\Tools\\Bloxstrap.exe\" \"%1\"",@"C:\Tools\Bloxstrap.exe")]
    [InlineData("  \"C:\\Program Files\\Fishstrap\\Fishstrap.exe\" --launch \"secret-marker\"  ",@"C:\Program Files\Fishstrap\Fishstrap.exe")]
    [InlineData(@"C:\Tools\Froststrap.exe",@"C:\Tools\Froststrap.exe")]
    [InlineData("\"C:\\Roblox\\version-123\\RobloxPlayerBeta.exe\" \"%1\"",@"C:\Roblox\version-123\RobloxPlayerBeta.exe")]
    public void CommandsYieldOnlyOneAllowlistedExecutableAndDiscardAllArguments(string command,string expected)
    {
        Assert.True(WindowsClientInstallationDiscovery.TryParseExecutableCommand(command,out var executable));Assert.Equal(expected,executable);Assert.DoesNotContain("secret-marker",executable!);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"cmd.exe /c C:\Tools\Bloxstrap.exe")]
    [InlineData("\"C:\\Windows\\System32\\cmd.exe\" /c \"C:\\Tools\\Bloxstrap.exe\"")]
    [InlineData("\"C:\\Tools\\Bloxstrap.exe\" & secret-marker")]
    [InlineData("\"C:\\Tools\\Bloxstrap.exe\"|other")]
    [InlineData("\"C:\\Tools\\Bloxstrap.exe\"suffix")]
    [InlineData(@"C:\Program Files\Bloxstrap\Bloxstrap.exe %1")]
    [InlineData("\"%LOCALAPPDATA%\\Bloxstrap\\Bloxstrap.exe\" \"%1\"")]
    [InlineData("\"\\\\server\\share\\Bloxstrap.exe\" \"%1\"")]
    [InlineData("\"\\\\?\\C:\\Tools\\Bloxstrap.exe\" \"%1\"")]
    [InlineData("\"C:\\Tools\\..\\Bloxstrap.exe\" \"%1\"")]
    [InlineData("\"C:\\Tools\\Bloxstrap.exe:extra\" \"%1\"")]
    [InlineData("\"C:\\Tools\\Bloxstrap.exe\"\nsecret-marker")]
    [InlineData("https://example.invalid/Bloxstrap.exe")]
    [InlineData("\"C:\\Tools\\Unknown.exe\" \"%1\"")]
    public void AmbiguousShellNonlocalEnvironmentAndMalformedCommandHintsAreNotInterpreted(string? command)
    {
        Assert.False(WindowsClientInstallationDiscovery.TryParseExecutableCommand(command,out var executable));Assert.Null(executable);
    }
    [Fact]
    public async Task IdenticalPathsMergeTheirEvidenceWithoutPromotingTrustOrKeepingRegistryArguments()
    {
        var source=new FakeSource();source.Registry[ClientInstallationRegistryHint.RobloxProtocol]="\""+Blox+"\" --authentication secret-marker";
        source.Registry[ClientInstallationRegistryHint.BloxstrapInstallLocation]=Path.GetDirectoryName(Blox);
        source.Files[Blox]=new(ClientInstallationFileState.PresentUnverified,"Bloxstrap","2.10.0.0");
        var result=await new WindowsClientInstallationDiscovery(source).DiscoverAsync(default);var candidate=Assert.Single(result.Candidates);
        Assert.Equal(ClientInstallationProduct.Bloxstrap,candidate.Product);Assert.Equal(Blox,candidate.ExecutablePath);Assert.Equal("2.10.0.0",candidate.FileVersion);
        Assert.Equal(1,source.Inspected.Count(path=>string.Equals(path,Blox,StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(candidate.Evidence,e=>e.Source==ClientInstallationHintSource.RobloxProtocol);
        Assert.Contains(candidate.Evidence,e=>e.Source==ClientInstallationHintSource.UninstallRegistry);
        Assert.Contains(candidate.Evidence,e=>e.Source==ClientInstallationHintSource.StandardInstallPath);
        Assert.Equal(CapabilityState.Unverified,candidate.Authenticity);Assert.Equal(CapabilityState.Unverified,candidate.Compatibility);Assert.Equal(CapabilityState.Unavailable,candidate.NamedAccountLaunch);
        Assert.DoesNotContain("secret-marker",JsonSerializer.Serialize(result));Assert.DoesNotContain("authentication",JsonSerializer.Serialize(result));
        Assert.False(result.HasConflicts);Assert.Equal(ClientInstallationSnapshotStatus.Complete,result.Status);
    }
    [Fact]
    public async Task ConflictingProtocolTargetsAndAllClientVersionsRemainVisibleWithoutAutomaticSelection()
    {
        string first=Path.Combine(Local,"Roblox","Versions","version-old","RobloxPlayerBeta.exe");
        string second=Path.Combine(Local,"Roblox","Versions","version-new","RobloxPlayerBeta.exe");
        var source=new FakeSource();source.Registry[ClientInstallationRegistryHint.RobloxProtocol]="\""+first+"\" \"%1\"";
        source.Registry[ClientInstallationRegistryHint.RobloxPlayerProtocol]="\""+Blox+"\" \"%1\"";
        source.Versions[Path.Combine(Local,"Roblox","Versions")]=new(Array.AsReadOnly(new[] {"version-new","version-old"}));
        source.Files[first]=new(ClientInstallationFileState.PresentUnverified,"Roblox","1.0.0.0");
        source.Files[second]=new(ClientInstallationFileState.PresentUnverified,"Roblox","999.0.0.0");source.Files[Blox]=new(ClientInstallationFileState.PresentUnverified);
        var result=await new WindowsClientInstallationDiscovery(source).DiscoverAsync(default);
        Assert.Equal(3,result.Candidates.Count);Assert.Contains(result.Candidates,row=>row.ExecutablePath==first);Assert.Contains(result.Candidates,row=>row.ExecutablePath==second);
        Assert.True(result.HasConflicts);Assert.Equal(ClientInstallationSnapshotStatus.Partial,result.Status);
        Assert.Contains(result.Issues,issue=>issue.Code==ClientInstallationIssueCode.ConflictingProtocols);
        Assert.Contains(result.Issues,issue=>issue.Code==ClientInstallationIssueCode.MultipleCandidates && issue.Product==ClientInstallationProduct.Roblox);
        Assert.All(result.Candidates,row=>Assert.Equal(CapabilityState.Unavailable,row.NamedAccountLaunch));
        Assert.DoesNotContain(typeof(ClientInstallationSnapshot).GetProperties(),property=>property.Name.Contains("Preferred",StringComparison.Ordinal) || property.Name.Contains("Selected",StringComparison.Ordinal));
    }
    [Fact]
    public async Task ARegisteredMissingExecutableIsAStaleHintNotProofOfAnInstalledOrLaunchableClient()
    {
        var source=new FakeSource();source.Registry[ClientInstallationRegistryHint.RobloxProtocol]="\""+Blox+"\" \"%1\"";
        var result=await new WindowsClientInstallationDiscovery(source).DiscoverAsync(default);var candidate=Assert.Single(result.Candidates);
        Assert.Equal(ClientInstallationFileState.Missing,candidate.FileState);Assert.Null(candidate.FileVersion);Assert.Equal(CapabilityState.Unavailable,candidate.NamedAccountLaunch);
    }
    [Fact]
    public async Task EmptyRedirectedMissingAndDeniedInventoriesNeverClaimGlobalAbsence()
    {
        var empty=await new WindowsClientInstallationDiscovery(new FakeSource()).DiscoverAsync(default);Assert.Empty(empty.Candidates);Assert.Contains("does not establish",empty.Summary);
        Assert.Equal(ClientInstallationSnapshotStatus.Partial,empty.Status);Assert.Contains(empty.Issues,issue=>issue.Code==ClientInstallationIssueCode.NoMatchingCandidates);
        var redirected=await new WindowsClientInstallationDiscovery(new FakeSource {Local=@"C:\Synthetic\Packages\sandbox.app\LocalCache\Local"}).DiscoverAsync(default);
        Assert.Equal(ClientInstallationSnapshotStatus.Partial,redirected.Status);Assert.Contains(redirected.Issues,issue=>issue.Code==ClientInstallationIssueCode.EnvironmentRedirected);
        var missing=await new WindowsClientInstallationDiscovery(new FakeSource {Local=null}).DiscoverAsync(default);
        Assert.Contains(missing.Issues,issue=>issue.Code==ClientInstallationIssueCode.LocalAppDataUnavailable);
        var denied=new FakeSource {RegistryFailure=new Win32Exception(5,"secret-marker"),DirectoryFailure=new UnauthorizedAccessException("secret-marker")};
        var unavailable=await new WindowsClientInstallationDiscovery(denied).DiscoverAsync(default);Assert.Equal(ClientInstallationSnapshotStatus.Partial,unavailable.Status);
        Assert.Contains(unavailable.Issues,issue=>issue.Code==ClientInstallationIssueCode.AccessDenied);Assert.DoesNotContain("secret-marker",JsonSerializer.Serialize(unavailable));
        var broken=new FakeSource {RootFailure=new IOException("secret-marker"),RegistryFailure=new IOException("secret-marker")};
        var failed=await new WindowsClientInstallationDiscovery(broken).DiscoverAsync(default);Assert.Equal(ClientInstallationSnapshotStatus.Unavailable,failed.Status);
        Assert.DoesNotContain("secret-marker",JsonSerializer.Serialize(failed));
    }
    [Fact]
    public async Task MalformedOversizedAndForeignRegistryHintsCannotCauseAnArbitraryExecutableQuery()
    {
        foreach(string invalid in new[] {new string('x',WindowsClientInstallationDiscovery.MaximumHintCharacters+1),"\"C:\\Tools\\cmd.exe\" secret-marker",
            "\"C:\\Tools\\Bloxstrap.exe\" & secret-marker","\"\\\\server\\share\\Fishstrap.exe\""})
        {
            var source=new FakeSource();source.Registry[ClientInstallationRegistryHint.RobloxProtocol]=invalid;
            source.Registry[ClientInstallationRegistryHint.FroststrapInstallLocation]=@"C:\Tools\..\secret-marker";
            var result=await new WindowsClientInstallationDiscovery(source).DiscoverAsync(default);
            Assert.Empty(result.Candidates);Assert.Contains(result.Issues,issue=>issue.Code==ClientInstallationIssueCode.InvalidHint);
            Assert.DoesNotContain(source.Inspected,path=>path.Contains("secret-marker",StringComparison.Ordinal) || path.EndsWith("cmd.exe",StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("secret-marker",JsonSerializer.Serialize(result));
        }
    }
    [Fact]
    public async Task AnAcceptedButNearLimitInstallRootRecordsAnInvalidHintInsteadOfFaultingTheScan()
    {
        // A valid 2045-character root passes the hint limit, but its Versions child does not; the real source rejects that child.
        string root="C:\\"+string.Join('\\',Enumerable.Repeat(new string('a',200),10))+"\\"+new string('b',32);
        Assert.Equal(2045,root.Length);Assert.True(WindowsClientInstallationDiscovery.TryNormalizeDirectory(root,out _));
        var source=new FakeSource();source.Registry[ClientInstallationRegistryHint.BloxstrapInstallLocation]=root;
        source.Files[Blox]=new(ClientInstallationFileState.PresentUnverified,"Bloxstrap","2.10.0.0");
        var result=await new WindowsClientInstallationDiscovery(source).DiscoverAsync(default);
        Assert.Equal(ClientInstallationSnapshotStatus.Partial,result.Status);
        Assert.Contains(result.Issues,issue=>issue.Code==ClientInstallationIssueCode.InvalidHint && issue.Product==ClientInstallationProduct.Bloxstrap
            && issue.Source==ClientInstallationHintSource.VersionDirectory);
        Assert.Equal(Blox,Assert.Single(result.Candidates).ExecutablePath);
        Assert.All(result.Candidates,candidate=>Assert.InRange(candidate.ExecutablePath.Length,1,WindowsClientInstallationDiscovery.MaximumHintCharacters));
    }
    [Fact]
    public async Task LargeInventoriesAndMalformedChildrenCannotEscapeOneLevelOrExceedResultAndProbeBounds()
    {
        var source=new FakeSource {AllFiles=new(ClientInstallationFileState.PresentUnverified,"Claimed product",new string('9',1000)),
            AllVersions=new(Enumerable.Range(0,1000).Select(index=>"version-"+index).Prepend(@"..\secret-marker").ToArray(),true)};
        source.Registry[ClientInstallationRegistryHint.BloxstrapInstallLocation]=@"D:\Custom\Bloxstrap";
        source.Registry[ClientInstallationRegistryHint.FishstrapInstallLocation]=@"D:\Custom\Fishstrap";
        source.Registry[ClientInstallationRegistryHint.FroststrapInstallLocation]=@"D:\Custom\Froststrap";
        var result=await new WindowsClientInstallationDiscovery(source).DiscoverAsync(default);
        Assert.InRange(result.Candidates.Count,1,WindowsClientInstallationDiscovery.MaximumCandidates);Assert.InRange(source.Inspected.Count,1,WindowsClientInstallationDiscovery.MaximumFileProbes);
        Assert.InRange(result.Issues.Count,1,WindowsClientInstallationDiscovery.MaximumIssues);Assert.Contains(result.Issues,issue=>issue.Code==ClientInstallationIssueCode.LimitReached);
        Assert.All(result.Candidates,candidate=>{Assert.Null(candidate.FileVersion);Assert.DoesNotContain("..",candidate.ExecutablePath);});
        Assert.DoesNotContain(source.Inspected,path=>path.Contains("secret-marker",StringComparison.Ordinal));Assert.Equal(5,source.RegistryCalls);
        Assert.Throws<NotSupportedException>(()=>((IList<ClientInstallationCandidate>)result.Candidates).Add(result.Candidates[0]));
        Assert.Throws<NotSupportedException>(()=>((IList<ClientInstallationIssue>)result.Issues).Clear());
    }
    [Theory]
    [InlineData(ClientInstallationFileState.AccessDenied,ClientInstallationIssueCode.AccessDenied)]
    [InlineData(ClientInstallationFileState.SkippedUnsafe,ClientInstallationIssueCode.UnsafePath)]
    [InlineData(ClientInstallationFileState.TooLarge,ClientInstallationIssueCode.MetadataUnavailable)]
    [InlineData(ClientInstallationFileState.MetadataUnavailable,ClientInstallationIssueCode.MetadataUnavailable)]
    public async Task FileProblemsRemainExplicitAndDoNotDiscardTheUnverifiedHint(ClientInstallationFileState state,ClientInstallationIssueCode issue)
    {
        var source=new FakeSource();source.Registry[ClientInstallationRegistryHint.RobloxProtocol]="\""+Blox+"\" \"%1\"";source.Files[Blox]=new(state);
        var result=await new WindowsClientInstallationDiscovery(source).DiscoverAsync(default);Assert.Equal(state,Assert.Single(result.Candidates).FileState);
        Assert.Contains(result.Issues,entry=>entry.Code==issue);Assert.Equal(ClientInstallationSnapshotStatus.Partial,result.Status);
    }
    [Fact]
    public async Task OneOwnedSnapshotDefersOverlapAndCancellationDoesNotReleaseOwnershipEarly()
    {
        using var release=new ManualResetEventSlim();var source=new FakeSource {Gate=release};var discovery=new WindowsClientInstallationDiscovery(source);
        using var stop=new CancellationTokenSource();var first=discovery.DiscoverAsync(stop.Token);
        try
        {
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ClientInstallationSnapshotStatus.Deferred,(await discovery.DiscoverAsync(default)).Status);Assert.Equal(1,source.RootCalls);
            stop.Cancel();Assert.False(first.IsCompleted);Assert.Equal(ClientInstallationSnapshotStatus.Deferred,(await discovery.DiscoverAsync(default)).Status);
        }
        finally {release.Set();}
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);source.Gate=null;
        Assert.NotEqual(ClientInstallationSnapshotStatus.Deferred,(await discovery.DiscoverAsync(default)).Status);
    }
    private sealed class FakeSource : IClientInstallationInventorySource
    {
        public string? Local=ClientInstallationDiscoveryTests.Local;
        public Dictionary<ClientInstallationRegistryHint,string?> Registry=[];
        public Dictionary<string,ClientInstallationFileMetadata> Files=new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string,ClientInstallationVersionDirectories> Versions=new(StringComparer.OrdinalIgnoreCase);
        public List<string> Inspected=[];public int RegistryCalls,RootCalls;
        public Exception? RootFailure,RegistryFailure,DirectoryFailure;public ClientInstallationFileMetadata? AllFiles;public ClientInstallationVersionDirectories? AllVersions;
        public ManualResetEventSlim? Gate;public TaskCompletionSource<bool> Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? GetLocalAppData(CancellationToken ct) {RootCalls++;Started.TrySetResult(true);Gate?.Wait();ct.ThrowIfCancellationRequested();if(RootFailure is { } failure) throw failure;return Local;}
        public string? ReadRegistryValue(ClientInstallationRegistryHint hint,CancellationToken ct) {ct.ThrowIfCancellationRequested();RegistryCalls++;if(RegistryFailure is { } failure) throw failure;return Registry.GetValueOrDefault(hint);}
        public ClientInstallationVersionDirectories ReadVersionDirectories(string root,int maximum,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            // Same argument contract as WindowsClientInstallationInventorySource.ReadVersionDirectories.
            if(!WindowsClientInstallationDiscovery.TryNormalizeDirectory(root,out var normalized) || !string.Equals(Path.GetFileName(normalized),"Versions",StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only a known Versions metadata folder may be enumerated.",nameof(root));
            if(DirectoryFailure is { } failure) throw failure;return AllVersions??Versions.GetValueOrDefault(root)??new([]);
        }
        public ClientInstallationFileMetadata InspectExecutable(string path,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();Inspected.Add(path);return AllFiles??Files.GetValueOrDefault(path)??new(ClientInstallationFileState.Missing);}
    }
}
