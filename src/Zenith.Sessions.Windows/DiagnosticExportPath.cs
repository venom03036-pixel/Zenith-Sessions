namespace Zenith.Sessions.Windows;

/// <summary>Read-only Windows path checks before an explicitly selected report write.</summary>
public static class DiagnosticExportPath
{
    public static string Validate(string path,string dataRoot,string applicationRoot)
    {
        string supplied=path.Replace('/','\\');
        if(!Path.IsPathFullyQualified(supplied) || DirectoryIdentity.IsDevicePath(supplied))
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
        string resolved=Path.Combine(DirectoryIdentity.Canonical(parent),name);
        foreach(string root in new[]{dataRoot,applicationRoot})
        {
            string protectedRoot=Path.TrimEndingDirectorySeparator(DirectoryIdentity.Canonical(root));
            string prefix=Path.EndsInDirectorySeparator(protectedRoot)?protectedRoot:protectedRoot+Path.DirectorySeparatorChar;
            if(resolved.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Save the report outside the app and its data folder, so settings and protected files remain intact.");
        }
        return full;
    }
}
