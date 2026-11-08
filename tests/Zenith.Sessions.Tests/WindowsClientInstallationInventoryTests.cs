using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
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
    public void ActualDirectoryEnumerationSkipsFilesAndKeepsTheNewestVersionFolders()
    {
        var source=new WindowsClientInstallationInventorySource(_root,includeCurrentUserRegistry:false);
        int cap=WindowsClientInstallationDiscovery.MaximumVersionEntriesPerRoot;
        // Name order is the reverse of age, and a loose installer file and a leftover folder sort first: neither may use a slot.
        string versions=Path.Combine(_root,"Roblox","Versions");Directory.CreateDirectory(Path.Combine(versions,"aaa-leftover"));
        File.WriteAllBytes(Path.Combine(versions,"RobloxStudioInstaller.exe"),[0x4d,0x5a]);
        var baseTime=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc);
        for(int i=0;i<10;i++) Directory.CreateDirectory(Path.Combine(versions,$"version-{i:00}"));
        for(int i=0;i<10;i++) Directory.SetLastWriteTimeUtc(Path.Combine(versions,$"version-{i:00}"),baseTime.AddHours(i));
        Directory.SetLastWriteTimeUtc(Path.Combine(versions,"aaa-leftover"),baseTime.AddDays(30));
        var newest=source.ReadVersionDirectories(versions,cap,default);
        Assert.True(newest.Truncated);
        Assert.Equal(Enumerable.Range(2,8).Reverse().Select(i=>$"version-{i:00}"),newest.Names);
        // Seven version folders plus two loose files fit under the cap: nothing is dropped and no limit is reported.
        string fits=Path.Combine(_root,"Fishstrap","Versions");Directory.CreateDirectory(fits);
        File.WriteAllBytes(Path.Combine(fits,"a-installer.exe"),[0x4d,0x5a]);File.WriteAllBytes(Path.Combine(fits,"b-notes.txt"),[0x41]);
        var seven=Enumerable.Range(0,7).Select(i=>$"version-{i:00}").ToArray();
        foreach(string name in seven) Directory.CreateDirectory(Path.Combine(fits,name));
        var all=source.ReadVersionDirectories(fits,cap,default);
        Assert.False(all.Truncated);Assert.Equal(seven,all.Names.Order(StringComparer.Ordinal));
        // The raw entry scan itself is bounded: past the scan bound the result is reported as truncated.
        string crowded=Path.Combine(_root,"Bloxstrap","Versions");Directory.CreateDirectory(Path.Combine(crowded,"version-only"));
        for(int i=0;i<WindowsClientInstallationDiscovery.MaximumVersionScanEntries;i++) File.WriteAllBytes(Path.Combine(crowded,$"zz-{i:000}.tmp"),[]);
        var bounded=source.ReadVersionDirectories(crowded,cap,default);
        Assert.True(bounded.Truncated);Assert.All(bounded.Names,name=>Assert.Equal("version-only",name));
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
    public static TheoryData<uint,byte[],string?> RegistryStringShapes()
    {
        byte[] player=Encoding.Unicode.GetBytes(@"C:\A\RobloxPlayerBeta.exe"+"\0");
        return new()
        {
            {1u,player,@"C:\A\RobloxPlayerBeta.exe"},
            {1u,[],""},
            {1u,Encoding.Unicode.GetBytes(new string('A',2048)),new string('A',2048)},
            // An embedded NUL survives decoding so the command/path parser rejects it later.
            {1u,Encoding.Unicode.GetBytes("a\0b\0"),"a\0b"},
            {2u,player,null},
            {3u,player,null},
            {1u,[0x41,0x00,0x42],null},
            {1u,Encoding.Unicode.GetBytes(new string('A',2049)),null},
            {1u,[0x00,0xD8],null},
        };
    }
    [Theory]
    [MemberData(nameof(RegistryStringShapes))]
    public void RegistryStringDecoderAcceptsOnlyBoundedStrictRegSz(uint kind,byte[] data,string? expected)
    {
        if(expected is null) Assert.Throws<InvalidDataException>(()=>WindowsClientInstallationInventorySource.DecodeRegistryString(kind,data));
        else Assert.Equal(expected,WindowsClientInstallationInventorySource.DecodeRegistryString(kind,data));
    }
    [Fact]
    public async Task ActualRegistryReadReturnsOwnedSyntheticValuesThroughRealRegQueryValueEx()
    {
        Directory.CreateDirectory(_root);var opened=new List<SafeRegistryHandle>();
        string player=Path.Combine(_root,"Synthetic","Roblox","Versions","version-abc","RobloxPlayerBeta.exe"),command="\""+player+"\" %1";
        using(var hive=new OwnedApplicationHive(Path.Combine(_root,"synthetic.hive")))
        {
            hive.SetValue(@"Software\Classes\roblox-player\shell\open\command",null,command,RegistryValueKind.String);
            hive.SetValue(@"Software\Classes\roblox\shell\open\command",null,@"%LOCALAPPDATA%\Roblox\RobloxPlayerBeta.exe",RegistryValueKind.ExpandString);
            hive.SetValue(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Bloxstrap","InstallLocation",@"C:\"+new string('a',2100),RegistryValueKind.String);
            hive.SetValue(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Fishstrap","InstallLocation",new byte[]{0x43,0,0x3a,0},RegistryValueKind.Binary);
            RegistryKey Open() {var root=hive.OpenFresh();opened.Add(root.Handle);return root;}
            var source=new WindowsClientInstallationInventorySource(_root,true,Open);
            Assert.Equal(command,source.ReadRegistryValue(ClientInstallationRegistryHint.RobloxPlayerProtocol,default));
            Assert.Throws<InvalidDataException>(()=>source.ReadRegistryValue(ClientInstallationRegistryHint.RobloxProtocol,default));
            Assert.Throws<InvalidDataException>(()=>source.ReadRegistryValue(ClientInstallationRegistryHint.BloxstrapInstallLocation,default));
            Assert.Throws<InvalidDataException>(()=>source.ReadRegistryValue(ClientInstallationRegistryHint.FishstrapInstallLocation,default));
            Assert.Null(source.ReadRegistryValue(ClientInstallationRegistryHint.FroststrapInstallLocation,default));
            // Key present without the value: the native ERROR_FILE_NOT_FOUND branch, then the key is removed again.
            hive.SetValue(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Froststrap","DisplayName","Synthetic",RegistryValueKind.String);
            Assert.Null(source.ReadRegistryValue(ClientInstallationRegistryHint.FroststrapInstallLocation,default));
            hive.DeleteKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Froststrap");
            var snapshot=await new WindowsClientInstallationDiscovery(source).DiscoverAsync(default);
            var candidate=Assert.Single(snapshot.Candidates);
            Assert.Equal(player,candidate.ExecutablePath);Assert.Equal(ClientInstallationFileState.Missing,candidate.FileState);
            Assert.Equal([new ClientInstallationEvidence(ClientInstallationHintSource.RobloxPlayerProtocol,ClientInstallationProduct.Roblox)],candidate.Evidence);
            Assert.Contains(new ClientInstallationIssue(ClientInstallationIssueCode.InvalidHint),snapshot.Issues);
            Assert.Equal(ClientInstallationSnapshotStatus.Partial,snapshot.Status);
            // One fresh root per read (six direct, six during discovery: two protocol registrations and the four bootstrappers'
            // install locations, Voidstrap's since R-launch-17), each closed by the source that opened it.
            Assert.Equal(12,opened.Count);Assert.All(opened,handle=>Assert.True(handle.IsClosed));
            var disabled=new WindowsClientInstallationInventorySource(_root,false,Open);
            foreach(var hint in Enum.GetValues<ClientInstallationRegistryHint>()) Assert.Null(disabled.ReadRegistryValue(hint,default));
            Assert.Equal(12,opened.Count);
        }
        Assert.False(File.Exists(player));
    }
    [Fact]
    public async Task VersionResourceOfARealPeIsParsedIntoAFourPartVersionAndProductName()
    {
        // Our own build-output PE (never a system binary), copied inertly under a Roblox-shaped name and never run. Its resource
        // has a product name unlike its description and an asymmetric version, so a wrong string block or field order is visible.
        string version=Path.Combine(_root,"Roblox","Versions","version-fixture"),player=Path.Combine(version,"RobloxPlayerBeta.exe");
        Directory.CreateDirectory(version);File.Copy(typeof(Zenith.Sessions.App.ShellViewModel).Assembly.Location,player);
        byte[] before=await File.ReadAllBytesAsync(player);
        // Independent oracle: the framework's own VS_FIXEDFILEINFO and StringFileInfo reader over the same file.
        var oracle=System.Diagnostics.FileVersionInfo.GetVersionInfo(player);
        string expectedVersion=$"{oracle.FileMajorPart}.{oracle.FileMinorPart}.{oracle.FileBuildPart}.{oracle.FilePrivatePart}";
        Assert.False(string.IsNullOrEmpty(oracle.ProductName));Assert.NotEqual(oracle.FileDescription,oracle.ProductName);
        Assert.NotEqual(oracle.FileMajorPart,oracle.FileMinorPart);
        var source=new WindowsClientInstallationInventorySource(_root,includeCurrentUserRegistry:false);
        var metadata=source.InspectExecutable(player,default);
        Assert.Equal(ClientInstallationFileState.PresentUnverified,metadata.State);
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$",metadata.FileVersion);Assert.Equal(expectedVersion,metadata.FileVersion);
        Assert.Equal(oracle.ProductName,metadata.ProductName);
        var snapshot=await new WindowsClientInstallationDiscovery(source).DiscoverAsync(default);
        var candidate=Assert.Single(snapshot.Candidates);
        Assert.Equal(player,candidate.ExecutablePath);Assert.Equal(ClientInstallationFileState.PresentUnverified,candidate.FileState);
        Assert.Equal(expectedVersion,candidate.FileVersion);Assert.Equal(oracle.ProductName,candidate.ProductName);
        Assert.Equal(CapabilityState.Unverified,candidate.Authenticity);Assert.Equal(CapabilityState.Unavailable,candidate.NamedAccountLaunch);
        Assert.Equal(before,await File.ReadAllBytesAsync(player));Assert.Single(Directory.GetFiles(_root,"*",SearchOption.AllDirectories));
    }
    [Fact]
    public void InventorySizeCapNeverHidesAFileTheVerifierWouldAccept()
    {
        // The inventory reads only the 64 KiB-bounded version resource, so a lower cap of its own would protect nothing: it would only
        // mark a file "too large" that Verify Roblox publisher still accepts, and so disable Verify and the native trial for that row.
        string Player(string folder,long length)
        {
            string directory=Path.Combine(_root,"Roblox","Versions",folder),path=Path.Combine(directory,"RobloxPlayerBeta.exe");
            Directory.CreateDirectory(directory);SparseFixture(path,length);return path;
        }
        string over256=Player("version-over256",256L*1024*1024+1),atCap=Player("version-atcap",WindowsClientExecutableTrust.MaximumFileBytes);
        string overCap=Player("version-overcap",WindowsClientExecutableTrust.MaximumFileBytes+1);
        var source=new WindowsClientInstallationInventorySource(_root,includeCurrentUserRegistry:false);
        // The fixtures carry no version resource, so a file the verifier accepts reads as MetadataUnavailable, never TooLarge.
        Assert.Equal(ClientInstallationFileState.MetadataUnavailable,source.InspectExecutable(over256,default).State);
        Assert.Equal(ClientInstallationFileState.MetadataUnavailable,source.InspectExecutable(atCap,default).State);
        Assert.Equal(ClientInstallationFileState.TooLarge,source.InspectExecutable(overCap,default).State);
        Assert.Equal(WindowsClientExecutableTrust.MaximumFileBytes,WindowsClientInstallationInventorySource.MaximumExecutableBytes);
    }
    /// <summary>An MZ-prefixed file of the given length, marked sparse first so the test never allocates hundreds of MiB of disk.</summary>
    private static void SparseFixture(string path,long length)
    {
        using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None);
        if(!DeviceIoControl(stream.SafeFileHandle,FsctlSetSparse,IntPtr.Zero,0,IntPtr.Zero,0,out _,IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        stream.Write([0x4d,0x5a,0,0]);stream.SetLength(length);stream.Flush();
        Assert.NotEqual((FileAttributes)0,File.GetAttributes(path)&FileAttributes.SparseFile);Assert.Equal(length,new FileInfo(path).Length);
    }
    private const uint FsctlSetSparse=0x000900C4;
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device,uint code,IntPtr input,uint inputBytes,IntPtr output,uint outputBytes,out uint returned,IntPtr overlapped);
    /// <summary>A private application hive (RegLoadAppKey) in the test's owned temp folder, so the user's real HKCU is never opened or written.</summary>
    private sealed class OwnedApplicationHive : IDisposable
    {
        private const int KeyRead=0x20019,KeyAllAccess=0xF003F;
        private readonly string _file;
        private readonly RegistryKey _writer;
        public OwnedApplicationHive(string file) {_file=file;_writer=Load(file,KeyAllAccess);}
        public void SetValue(string path,string? name,object value,RegistryValueKind kind)
        {using var key=_writer.CreateSubKey(path,writable:true);key.SetValue(name,value,kind);}
        public void DeleteKey(string path)=>_writer.DeleteSubKeyTree(path);
        /// <summary>A fresh read-only handle on every call; the caller owns and disposes it.</summary>
        public RegistryKey OpenFresh()=>Load(_file,KeyRead);
        public void Dispose()=>_writer.Dispose();
        private static RegistryKey Load(string file,int access)
        {
            int error=RegLoadAppKey(file,out var handle,access,0,0);
            if(error!=0) {handle.Dispose();throw new Win32Exception(error);}
            return RegistryKey.FromHandle(handle);
        }
        [DllImport("advapi32.dll",EntryPoint="RegLoadAppKeyW",CharSet=CharSet.Unicode)]
        private static extern int RegLoadAppKey(string file,out SafeRegistryHandle key,int desired,int options,int reserved);
    }
    public void Dispose()
    {
        string resolved=Path.GetFullPath(_root),temporary=Path.GetFullPath(Path.GetTempPath());
        if(Path.GetDirectoryName(resolved)!=temporary.TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(resolved).StartsWith("zenith-install-inventory-",StringComparison.Ordinal))
            throw new InvalidOperationException("The owned installation fixture path is invalid.");
        if(Directory.Exists(resolved)) Directory.Delete(resolved,true);
    }
}
