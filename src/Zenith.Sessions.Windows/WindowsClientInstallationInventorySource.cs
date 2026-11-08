using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>Allowlisted HKCU values and bounded local file/version metadata only. Never opens bootstrapper settings, cookies, logs or process memory.</summary>
public sealed class WindowsClientInstallationInventorySource : IClientInstallationInventorySource
{
    public const int MaximumRegistryValueBytes=4096,MaximumVersionResourceBytes=65536;
    // Only the version resource is read (bounded above), so this cap guards nothing of its own: it must never mark a file
    // too large that the publisher verifier would still check, or Verify and the native trial go dark for that row.
    public const long MaximumExecutableBytes=WindowsClientExecutableTrust.MaximumFileBytes;
    private readonly string? _localOverride;
    private readonly bool _includeRegistry;
    private readonly Func<string,uint> _driveType;
    private readonly Func<RegistryKey> _openRegistryRoot;
    private static readonly UnicodeEncoding StrictUtf16=new(false,false,true);
    /// <summary>Tests may provide an owned application-data root and disable registry reads. No registry or filesystem write API is used.</summary>
    public WindowsClientInstallationInventorySource(string? localAppDataOverride=null,bool includeCurrentUserRegistry=true)
        :this(localAppDataOverride,includeCurrentUserRegistry,GetDriveType,OpenCurrentUserRoot) { }
    internal WindowsClientInstallationInventorySource(string? localAppDataOverride,bool includeCurrentUserRegistry,Func<string,uint> driveType)
        :this(localAppDataOverride,includeCurrentUserRegistry,driveType,OpenCurrentUserRoot) { }
    /// <summary>Points the real RegQueryValueEx read at an owned root (tests use a private application hive). The opener must
    /// return a fresh key on every call: each read owns and disposes the root it opened. The fixed relative paths are unchanged.</summary>
    internal WindowsClientInstallationInventorySource(string? localAppDataOverride,bool includeCurrentUserRegistry,Func<RegistryKey> openRegistryRoot)
        :this(localAppDataOverride,includeCurrentUserRegistry,GetDriveType,openRegistryRoot) { }
    private WindowsClientInstallationInventorySource(string? localAppDataOverride,bool includeCurrentUserRegistry,Func<string,uint> driveType,Func<RegistryKey> openRegistryRoot)
    {
        if(localAppDataOverride is not null && !WindowsClientInstallationDiscovery.TryNormalizeDirectory(localAppDataOverride,out _))
            throw new ArgumentException("Use a fully qualified local metadata root.",nameof(localAppDataOverride));
        _localOverride=localAppDataOverride;_includeRegistry=includeCurrentUserRegistry;_driveType=driveType??throw new ArgumentNullException(nameof(driveType));
        _openRegistryRoot=openRegistryRoot??throw new ArgumentNullException(nameof(openRegistryRoot));
    }
    private static RegistryKey OpenCurrentUserRoot()=>RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Default);
    public string? GetLocalAppData(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();EnsureWindows();string path=_localOverride??Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if(!WindowsClientInstallationDiscovery.TryNormalizeDirectory(path,out var normalized) || !IsLocalVolume(normalized)) return null;
        try {return (File.GetAttributes(path)&FileAttributes.Directory)!=0?path:null;}
        catch(FileNotFoundException) {return null;}
        catch(DirectoryNotFoundException) {return null;}
    }
    public string? ReadRegistryValue(ClientInstallationRegistryHint hint,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();EnsureWindows();
        (string path,string? name)=RegistryLocation(hint);if(!_includeRegistry) return null;
        using var root=_openRegistryRoot();
        using var key=root.OpenSubKey(path,writable:false);if(key is null) return null;
        uint bytes=0;int error=RegQueryValueEx(key.Handle,name,IntPtr.Zero,out uint kind,null,ref bytes);
        if(error==2) return null;
        if(error!=0) throw new Win32Exception(error);
        if(!IsBoundedRegSz(kind,bytes)) throw UnsupportedShape();
        var buffer=new byte[bytes];ct.ThrowIfCancellationRequested();
        error=RegQueryValueEx(key.Handle,name,IntPtr.Zero,out kind,buffer,ref bytes);ct.ThrowIfCancellationRequested();
        if(error!=0 || kind!=1 || bytes>buffer.Length || bytes%2!=0) throw new InvalidDataException("The installation registry value changed during its bounded read.");
        return DecodeRegistryString(kind,buffer.AsSpan(0,(int)bytes));
    }
    /// <summary>Accepts only a bounded, even-length REG_SZ of strict UTF-16LE and trims trailing NULs. An embedded NUL is kept so the
    /// command/path parsers reject it. Throws <see cref="InvalidDataException"/> for any other shape.</summary>
    internal static string DecodeRegistryString(uint kind,ReadOnlySpan<byte> data)
    {
        if(!IsBoundedRegSz(kind,(uint)data.Length)) throw UnsupportedShape();
        try {return StrictUtf16.GetString(data).TrimEnd('\0');}
        catch(DecoderFallbackException) {throw new InvalidDataException("The installation registry value is not valid Unicode.");}
    }
    // REG_EXPAND_SZ is deliberately not expanded; only an actual bounded REG_SZ (type 1) is accepted.
    private static bool IsBoundedRegSz(uint kind,uint bytes)=>kind==1 && bytes<=MaximumRegistryValueBytes && bytes%2==0;
    private static InvalidDataException UnsupportedShape()=>new("The installation registry value has an unsupported shape.");
    private static (string Path,string? Name) RegistryLocation(ClientInstallationRegistryHint hint)=>hint switch
    {
        ClientInstallationRegistryHint.RobloxProtocol=>(@"Software\Classes\roblox\shell\open\command",null),
        ClientInstallationRegistryHint.RobloxPlayerProtocol=>(@"Software\Classes\roblox-player\shell\open\command",null),
        ClientInstallationRegistryHint.BloxstrapInstallLocation=>(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Bloxstrap","InstallLocation"),
        ClientInstallationRegistryHint.FishstrapInstallLocation=>(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Fishstrap","InstallLocation"),
        ClientInstallationRegistryHint.FroststrapInstallLocation=>(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Froststrap","InstallLocation"),
        ClientInstallationRegistryHint.VoidstrapInstallLocation=>(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Voidstrap","InstallLocation"),
        _=>throw new ArgumentOutOfRangeException(nameof(hint))
    };
    /// <summary>Returns at most <paramref name="maximumEntries"/> plain version-* folder names, newest last-write time first (name breaks ties).
    /// Loose files, reparse points and other folders never use a slot. At most <see cref="WindowsClientInstallationDiscovery.MaximumVersionScanEntries"/>
    /// raw entries are examined; Truncated is set when that bound is passed or more version folders exist than were returned.</summary>
    public ClientInstallationVersionDirectories ReadVersionDirectories(string root,int maximumEntries,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();EnsureWindows();
        if(maximumEntries is <1 or >WindowsClientInstallationDiscovery.MaximumVersionEntriesPerRoot) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        if(!WindowsClientInstallationDiscovery.TryNormalizeDirectory(root,out var normalized) || !string.Equals(Path.GetFileName(normalized),"Versions",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only a known Versions metadata folder may be enumerated.",nameof(root));
        if(!IsLocalVolume(normalized)) throw new InvalidDataException("Nonlocal or unavailable installation volumes are skipped.");
        try {if((File.GetAttributes(normalized!)&FileAttributes.Directory)==0) return new([]);}
        catch(FileNotFoundException) {return new([]);}
        catch(DirectoryNotFoundException) {return new([]);}
        if(HasReparseAncestor(normalized!)) throw new InvalidDataException("Redirected installation metadata folders are skipped.");
        // Version folder names are random hashes, so enumeration (name) order says nothing about age: rank by last-write time instead.
        var folders=new List<(string Name,DateTime Written)>();int examined=0;bool truncated=false;
        using var entries=Directory.EnumerateFileSystemEntries(normalized!,"*",SearchOption.TopDirectoryOnly).GetEnumerator();
        while(entries.MoveNext())
        {
            ct.ThrowIfCancellationRequested();if(++examined>WindowsClientInstallationDiscovery.MaximumVersionScanEntries) {truncated=true;break;}
            string name=Path.GetFileName(entries.Current);
            if(!WindowsClientInstallationDiscovery.ValidVersionName(name)) continue;
            var attributes=File.GetAttributes(entries.Current);
            if((attributes&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=FileAttributes.Directory) continue;
            folders.Add((name,Directory.GetLastWriteTimeUtc(entries.Current)));
        }
        ct.ThrowIfCancellationRequested();
        folders.Sort((left,right)=>{int byTime=right.Written.CompareTo(left.Written);return byTime!=0?byTime:StringComparer.OrdinalIgnoreCase.Compare(left.Name,right.Name);});
        if(folders.Count>maximumEntries) truncated=true;
        var names=new string[Math.Min(folders.Count,maximumEntries)];
        for(int index=0;index<names.Length;index++) names[index]=folders[index].Name;
        return new(Array.AsReadOnly(names),truncated);
    }
    public ClientInstallationFileMetadata InspectExecutable(string path,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();EnsureWindows();
        if(!WindowsClientInstallationDiscovery.TryNormalizeExecutablePath(path,out var normalized,out _)) return new(ClientInstallationFileState.SkippedUnsafe);
        if(!IsLocalVolume(normalized)) return new(ClientInstallationFileState.SkippedUnsafe);
        try
        {
            var attributes=File.GetAttributes(normalized!);
            if((attributes&FileAttributes.Directory)!=0 || HasReparseAncestor(normalized!)) return new(ClientInstallationFileState.SkippedUnsafe);
            if(new FileInfo(normalized!).Length>MaximumExecutableBytes) return new(ClientInstallationFileState.TooLarge);
            ct.ThrowIfCancellationRequested();return ReadVersion(normalized!,ct);
        }
        catch(FileNotFoundException) {return new(ClientInstallationFileState.Missing);}
        catch(DirectoryNotFoundException) {return new(ClientInstallationFileState.Missing);}
        catch(Exception error) when(error is UnauthorizedAccessException or SecurityException) {return new(ClientInstallationFileState.AccessDenied);}
        catch(Exception error) when(error is IOException or Win32Exception or InvalidOperationException) {return new(ClientInstallationFileState.MetadataUnavailable);}
    }
    private static bool HasReparseAncestor(string path)
    {
        int count=0;
        for(string? current=path;current is not null;current=Path.GetDirectoryName(current))
        {
            if(++count>64 || (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0) return true;
        }
        return false;
    }
    // Query the drive mapping before attributes, enumeration or version APIs. A drive-letter
    // spelling can still be a network share; syntax/reparse checks alone do not make it local.
    private bool IsLocalVolume(string path) => Path.GetPathRoot(path) is {Length:3} root && _driveType(root) is 2 or 3 or 5 or 6;
    private static ClientInstallationFileMetadata ReadVersion(string path,CancellationToken ct)
    {
        uint size=GetFileVersionInfoSize(path,out _);ct.ThrowIfCancellationRequested();
        if(size==0 || size>MaximumVersionResourceBytes) return new(ClientInstallationFileState.MetadataUnavailable);
        IntPtr buffer=Marshal.AllocHGlobal((int)size);
        try
        {
            if(!GetFileVersionInfo(path,0,size,buffer)) return new(ClientInstallationFileState.MetadataUnavailable);ct.ThrowIfCancellationRequested();
            if(!VerQueryValue(buffer,@"\",out var fixedData,out uint length) || length<52 || !Within(buffer,size,fixedData,52)
                || unchecked((uint)Marshal.ReadInt32(fixedData))!=0xFEEF04BD) return new(ClientInstallationFileState.MetadataUnavailable);
            uint high=unchecked((uint)Marshal.ReadInt32(fixedData,8)),low=unchecked((uint)Marshal.ReadInt32(fixedData,12));
            string version=FormattableString.Invariant($"{high>>16}.{high&65535}.{low>>16}.{low&65535}");string? product=null;
            if(VerQueryValue(buffer,@"\VarFileInfo\Translation",out var translations,out uint translationBytes) && translationBytes>=4 && Within(buffer,size,translations,Math.Min(translationBytes,32)))
            {
                for(int index=0;index<Math.Min(translationBytes/4,8);index++)
                {
                    ct.ThrowIfCancellationRequested();ushort language=unchecked((ushort)Marshal.ReadInt16(translations,index*4)),codePage=unchecked((ushort)Marshal.ReadInt16(translations,index*4+2));
                    string block="\\StringFileInfo\\"+language.ToString("x4",CultureInfo.InvariantCulture)+codePage.ToString("x4",CultureInfo.InvariantCulture)+"\\ProductName";
                    if(VerQueryValue(buffer,block,out var text,out uint characters) && characters is >0 and <=81 && Within(buffer,size,text,characters*2))
                    {product=Marshal.PtrToStringUni(text,(int)characters)?.TrimEnd('\0');break;}
                }
            }
            return new(ClientInstallationFileState.PresentUnverified,product,version);
        }
        finally {Marshal.FreeHGlobal(buffer);}
    }
    private static bool Within(IntPtr start,uint bytes,IntPtr address,uint length)
    {long offset=address.ToInt64()-start.ToInt64();return offset>=0 && length<=bytes && offset<=bytes-length;}
    private static void EnsureWindows() {if(!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows installation metadata is unavailable.");}
    [DllImport("kernel32.dll",EntryPoint="GetDriveTypeW",CharSet=CharSet.Unicode)]
    private static extern uint GetDriveType(string root);
    [DllImport("advapi32.dll",EntryPoint="RegQueryValueExW",CharSet=CharSet.Unicode)]
    private static extern int RegQueryValueEx(SafeRegistryHandle key,string? name,IntPtr reserved,out uint type,[Out]byte[]? data,ref uint bytes);
    [DllImport("version.dll",EntryPoint="GetFileVersionInfoSizeW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern uint GetFileVersionInfoSize(string path,out uint handle);
    [DllImport("version.dll",EntryPoint="GetFileVersionInfoW",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileVersionInfo(string path,uint handle,uint length,IntPtr data);
    [DllImport("version.dll",EntryPoint="VerQueryValueW",CharSet=CharSet.Unicode)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool VerQueryValue(IntPtr block,string subBlock,out IntPtr value,out uint length);
}
