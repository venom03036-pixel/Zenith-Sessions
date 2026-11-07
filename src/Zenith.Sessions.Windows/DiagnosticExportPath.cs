using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Zenith.Sessions.Windows;

/// <summary>Read-only Windows path checks before an explicitly selected report write.</summary>
public static class DiagnosticExportPath
{
    public static string Validate(string path,string dataRoot,string applicationRoot)
    {
        string supplied=path.Replace('/','\\');
        if(!Path.IsPathFullyQualified(supplied) || supplied.StartsWith("\\\\?\\",StringComparison.Ordinal) ||
            supplied.StartsWith("\\\\.\\",StringComparison.Ordinal) || supplied.StartsWith("\\??\\",StringComparison.Ordinal))
            throw new ArgumentException("Choose a normal, nonredirected report file path.");
        string full=Path.GetFullPath(supplied);
        string name=Path.GetFileName(full);
        if(!name.EndsWith(".json",StringComparison.OrdinalIgnoreCase) || name.Contains(':')) throw new ArgumentException("Save the report as a .json file.");
        for(string? current=full;current is not null;current=Path.GetDirectoryName(current))
        {
            if((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new ArgumentException("Choose a report folder without filesystem links or junctions.");
        }
        string parent=Path.GetDirectoryName(full)!;
        if(!Directory.Exists(parent)) throw new DirectoryNotFoundException("Choose an existing report folder.");
        string resolved=Path.Combine(CanonicalDirectory(parent),name);
        foreach(string root in new[]{dataRoot,applicationRoot})
        {
            string protectedRoot=Path.TrimEndingDirectorySeparator(CanonicalDirectory(root));
            string prefix=Path.EndsInDirectorySeparator(protectedRoot)?protectedRoot:protectedRoot+Path.DirectorySeparatorChar;
            if(resolved.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Save the report outside the app and its data folder, so settings and protected files remain intact.");
        }
        return full;
    }
    private static string CanonicalDirectory(string path)
    {
        string full=Path.GetFullPath(path);var missing=new Stack<string>();
        while(!Directory.Exists(full))
        {
            missing.Push(Path.GetFileName(full));full=Path.GetDirectoryName(full)??throw new DirectoryNotFoundException("The report path is unavailable.");
        }
        // Access=0 reads directory identity only; no client/process/content access or mutation.
        using var handle=CreateFile(full,0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);
        if(handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var text=new StringBuilder(32768);uint length=GetFinalPathNameByHandle(handle,text,(uint)text.Capacity,0);
        if(length==0 || length>=text.Capacity) throw new IOException("The report folder could not be resolved.");
        string resolved=text.ToString();
        resolved=resolved.StartsWith("\\\\?\\UNC\\",StringComparison.OrdinalIgnoreCase)?"\\\\"+resolved[8..]:
            resolved.StartsWith("\\\\?\\",StringComparison.Ordinal)?resolved[4..]:resolved;
        while(missing.Count>0) resolved=Path.Combine(resolved,missing.Pop());
        return resolved;
    }
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",EntryPoint="GetFinalPathNameByHandleW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,StringBuilder path,uint length,uint flags);
}
