using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Zenith.Sessions.Windows;

/// <summary>Identifies a folder by what Windows resolves it to, not by how its path is spelled.</summary>
public static class DirectoryIdentity
{
    /// <summary>True for device and NT object spellings ("\\?\", "\\.\", "\??\"), which GetFullPath leaves unexpanded.</summary>
    public static bool IsDevicePath(string path)
    {
        string supplied=path.Replace('/','\\');
        return supplied.StartsWith("\\\\?\\",StringComparison.Ordinal) || supplied.StartsWith("\\\\.\\",StringComparison.Ordinal) ||
            supplied.StartsWith("\\??\\",StringComparison.Ordinal);
    }
    /// <summary>
    /// The final path of a folder. The deepest existing ancestor is resolved through its handle, so "\\?\", subst drives,
    /// 8.3 short names, junctions and symbolic links give one spelling; parts that do not exist yet are appended as given.
    /// Spellings that reach the folder through a share (for example \\localhost\C$) keep their UNC form.
    /// </summary>
    public static string Canonical(string path)
    {
        string full=Path.GetFullPath(path);var missing=new Stack<string>();
        while(!Directory.Exists(full))
        {
            missing.Push(Path.GetFileName(full));full=Path.GetDirectoryName(full)??throw new DirectoryNotFoundException("The folder is unavailable.");
        }
        // Access=0 reads directory identity only; no client/process/content access or mutation.
        using var handle=CreateFile(full,0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);
        if(handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var text=new StringBuilder(32768);uint length=GetFinalPathNameByHandle(handle,text,(uint)text.Capacity,0);
        if(length==0 || length>=text.Capacity) throw new IOException("The folder could not be resolved.");
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
