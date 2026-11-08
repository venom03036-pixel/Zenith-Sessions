using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// The current user's documented Roblox log roots below LocalApplicationData, in one place so the log probe and the
/// crash-record probe (which reads the crashes\reports folder under each root) always watch the same installs.
/// </summary>
internal static class RobloxClientFolders
{
    /// <summary>The Microsoft Store Roblox package family; its per-user data lives in LocalApplicationData\Packages.</summary>
    internal const string PackageFamilyName = "ROBLOXCorporation.ROBLOX_55nm5eh3cm0pr";

    /// <summary>Win32 first, then the Store install. Paths only; nothing is created, opened or checked here.</summary>
    internal static (string Path, ClientLogVariant Variant)[] LogRoots(string local) =>
        [(Path.Combine(local, "Roblox", "logs"), ClientLogVariant.Win32),
            (Path.Combine(local, "Packages", PackageFamilyName, "LocalState", "logs"), ClientLogVariant.Uwp)];
}
