namespace MacroSuite.Core.Extensibility;

/// <summary>
/// Keeps plugin assembly paths under a canonical plugin root (Release 4).
/// </summary>
public static class PluginPathGuard
{
    public static bool TryResolveEntryAssembly(
        string pluginRoot,
        string pluginFolder,
        string entryAssembly,
        out string fullPath,
        out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(pluginRoot))
        {
            error = "Plugin root is not configured.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(pluginFolder) || string.IsNullOrWhiteSpace(entryAssembly))
        {
            error = "Plugin folder and entry assembly are required.";
            return false;
        }

        if (Path.IsPathRooted(entryAssembly)
            || entryAssembly.Contains("..", StringComparison.Ordinal)
            || entryAssembly.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            error = "Plugin entry assembly must be a relative file name without parent segments.";
            return false;
        }

        string extension = Path.GetExtension(entryAssembly);
        if (!string.IsNullOrEmpty(extension) && !extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            error = "Plugin entry assembly must be a .dll file.";
            return false;
        }

        string rootFull = Path.GetFullPath(pluginRoot);
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar) && !rootFull.EndsWith(Path.AltDirectorySeparatorChar))
            rootFull += Path.DirectorySeparatorChar;

        string folderFull = Path.GetFullPath(pluginFolder);
        if (!folderFull.EndsWith(Path.DirectorySeparatorChar) && !folderFull.EndsWith(Path.AltDirectorySeparatorChar))
            folderFull += Path.DirectorySeparatorChar;

        if (!folderFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            error = "Plugin folder is outside the plugin root.";
            return false;
        }

        string combined = Path.GetFullPath(Path.Combine(folderFull, entryAssembly));
        if (!combined.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            combined += ".dll";

        string combinedDir = Path.GetDirectoryName(combined) ?? combined;
        if (!combinedDir.EndsWith(Path.DirectorySeparatorChar) && !combinedDir.EndsWith(Path.AltDirectorySeparatorChar))
            combinedDir += Path.DirectorySeparatorChar;

        if (!combined.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
            || !combinedDir.StartsWith(folderFull, StringComparison.OrdinalIgnoreCase))
        {
            error = "Resolved plugin assembly is outside the plugin directory.";
            return false;
        }

        fullPath = combined;
        return true;
    }
}
