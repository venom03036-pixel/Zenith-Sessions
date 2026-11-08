using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// The current user's documented Roblox log roots below LocalApplicationData, in one place so the log probe, the
/// crash-record probe (which reads the crashes\reports folder under each root) and the log cleanup always use the same installs.
/// </summary>
internal static class RobloxClientFolders
{
    /// <summary>The Microsoft Store Roblox package family; its per-user data lives in LocalApplicationData\Packages.</summary>
    internal const string PackageFamilyName = "ROBLOXCorporation.ROBLOX_55nm5eh3cm0pr";

    /// <summary>Win32 first, then the Store install. Paths only; nothing is created, opened or checked here.</summary>
    internal static (string Path, ClientLogVariant Variant)[] LogRoots(string local) =>
        [(Path.Combine(local, "Roblox", "logs"), ClientLogVariant.Win32),
            (Path.Combine(local, "Packages", PackageFamilyName, "LocalState", "logs"), ClientLogVariant.Uwp)];

    /// <summary>
    /// True when <paramref name="path"/> is a local directory below <paramref name="anchor"/> with no reparse point (junction or
    /// symbolic link) on the way up to it, so reading or deleting inside it cannot reach anywhere else. Shared by the log probe
    /// and the log cleanup; production passes LocalApplicationData as the anchor.
    /// </summary>
    internal static bool IsBelowAnchor(string path, string anchor)
    {
        string local = Path.GetFullPath(anchor);
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || !path.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            if (string.Equals(directory.FullName, local, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
