using System.IO;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class WindowsClientInstallationInventoryTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-install-inventory-"+Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task QueryOnlyActualInventoryUsesOnlyAnOwnedSyntheticRootWithRegistryDisabled()
    {
        string version=Path.Combine(_root,"Roblox","Versions","version-fixture"),bootstrapper=Path.Combine(_root,"Bloxstrap");
        Directory.CreateDirectory(version);Directory.CreateDirectory(bootstrapper);
        string player=Path.Combine(version,"RobloxPlayerBeta.exe"),blox=Path.Combine(bootstrapper,"Bloxstrap.exe");
        byte[] fixture=[0x4d,0x5a,0,0];await File.WriteAllBytesAsync(player,fixture);await File.WriteAllBytesAsync(blox,fixture);
        var source=new WindowsClientInstallationInventorySource(_root,includeCurrentUserRegistry:false);
        var result=await new WindowsClientInstallationDiscovery(source).DiscoverAsync(default);
        Assert.Equal(2,result.Candidates.Count);Assert.All(result.Candidates,candidate=>
        {
            Assert.StartsWith(_root,candidate.ExecutablePath);Assert.Equal(ClientInstallationFileState.MetadataUnavailable,candidate.FileState);
            Assert.Null(candidate.FileVersion);Assert.Equal(CapabilityState.Unverified,candidate.Authenticity);Assert.Equal(CapabilityState.Unavailable,candidate.NamedAccountLaunch);
        });
        Assert.Equal(fixture,await File.ReadAllBytesAsync(player));Assert.Equal(fixture,await File.ReadAllBytesAsync(blox));
        Assert.Equal(2,Directory.GetFiles(_root,"*",SearchOption.AllDirectories).Length);
    }
    [Fact]
    public void ActualDirectoryEnumerationIsOneLevelAndReportsItsEntryBound()
    {
        string versions=Path.Combine(_root,"Roblox","Versions");Directory.CreateDirectory(versions);
        for(int i=0;i<12;i++) Directory.CreateDirectory(Path.Combine(versions,"version-"+i));
        var source=new WindowsClientInstallationInventorySource(_root,includeCurrentUserRegistry:false);
        var result=source.ReadVersionDirectories(versions,WindowsClientInstallationDiscovery.MaximumVersionEntriesPerRoot,default);
        Assert.True(result.Truncated);Assert.InRange(result.Names.Count,0,WindowsClientInstallationDiscovery.MaximumVersionEntriesPerRoot);
        Assert.All(result.Names,name=>{Assert.DoesNotContain(Path.DirectorySeparatorChar,name);Assert.StartsWith("version-",name);});
        Assert.Throws<ArgumentException>(()=>source.ReadVersionDirectories(_root,8,default));
        Assert.Throws<ArgumentOutOfRangeException>(()=>source.ReadVersionDirectories(versions,9,default));
    }
    [Fact]
    public void ActualQuerySurfaceRejectsUnknownRegistrySelectorsAndNeverExpandsUntrustedPaths()
    {
        Directory.CreateDirectory(_root);var source=new WindowsClientInstallationInventorySource(_root,includeCurrentUserRegistry:false);
        foreach(var hint in Enum.GetValues<ClientInstallationRegistryHint>()) Assert.Null(source.ReadRegistryValue(hint,default));
        Assert.Throws<ArgumentOutOfRangeException>(()=>source.ReadRegistryValue((ClientInstallationRegistryHint)999,default));
        Assert.Equal(ClientInstallationFileState.SkippedUnsafe,source.InspectExecutable(@"%LOCALAPPDATA%\Bloxstrap\Bloxstrap.exe",default).State);
        Assert.Equal(ClientInstallationFileState.SkippedUnsafe,source.InspectExecutable(@"\\server\share\Bloxstrap.exe",default).State);
        Assert.Equal(ClientInstallationFileState.SkippedUnsafe,source.InspectExecutable(Path.Combine(_root,"unknown.exe"),default).State);
        using var stop=new CancellationTokenSource();stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(()=>source.GetLocalAppData(stop.Token));
        Assert.ThrowsAny<OperationCanceledException>(()=>source.ReadRegistryValue(ClientInstallationRegistryHint.RobloxProtocol,stop.Token));
        Assert.ThrowsAny<OperationCanceledException>(()=>source.InspectExecutable(Path.Combine(_root,"Bloxstrap.exe"),stop.Token));
        Assert.Empty(Directory.GetFiles(_root));
    }
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(4u)]
    public void RemoteUnknownOrUnavailableDriveIsRejectedBeforeAnyFilesystemQuery(uint kind)
    {
        var queried=new List<string>();
        var source=new WindowsClientInstallationInventorySource(@"Z:\Synthetic",false,root=>{queried.Add(root);return kind;});
        Assert.Null(source.GetLocalAppData(default));
        Assert.Equal(ClientInstallationFileState.SkippedUnsafe,source.InspectExecutable(@"Z:\Synthetic\RobloxPlayerBeta.exe",default).State);
        Assert.Throws<InvalidDataException>(()=>source.ReadVersionDirectories(@"Z:\Synthetic\Roblox\Versions",8,default));
        Assert.Equal(new[]{@"Z:\",@"Z:\",@"Z:\"},queried);
    }
    public void Dispose()
    {
        string resolved=Path.GetFullPath(_root),temporary=Path.GetFullPath(Path.GetTempPath());
        if(Path.GetDirectoryName(resolved)!=temporary.TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(resolved).StartsWith("zenith-install-inventory-",StringComparison.Ordinal))
            throw new InvalidOperationException("The owned installation fixture path is invalid.");
        if(Directory.Exists(resolved)) Directory.Delete(resolved,true);
    }
}
