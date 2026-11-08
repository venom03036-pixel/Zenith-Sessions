using System.IO;

namespace Zenith.Sessions.Windows;

/// <summary>
/// The one filename rule for native Roblox player files. Discovery lists the direct players and the
/// launcher; publisher checks and native trial selection accept only the direct players. Only the file
/// name is compared (ordinal, ignoring case); path safety stays with each caller because each one
/// validates a different input.
/// </summary>
public static class RobloxNativePlayerFiles
{
    public const string LauncherName = "RobloxPlayerLauncher.exe";
    public static IReadOnlyList<string> DirectPlayerNames { get; } = Array.AsReadOnly(new[] { "RobloxPlayerBeta.exe", "RobloxPlayer.exe" });
    /// <summary>Every Roblox-product filename discovery may list: the direct players, then the launcher.</summary>
    public static IReadOnlyList<string> InventoryNames { get; } = Array.AsReadOnly<string>([.. DirectPlayerNames, LauncherName]);

    /// <summary>True for a file that runs the Roblox player itself.</summary>
    public static bool IsDirectPlayer(string? path) => Matches(path, DirectPlayerNames);
    /// <summary>True for RobloxPlayerLauncher.exe, which installs and updates Roblox and is not the player.</summary>
    public static bool IsLauncher(string? path) => !string.IsNullOrEmpty(path) && Path.GetFileName(path).Equals(LauncherName, StringComparison.OrdinalIgnoreCase);
    /// <summary>True for any Roblox-product filename that installation discovery lists.</summary>
    public static bool IsInventoryPlayer(string? path) => Matches(path, InventoryNames);
    /// <summary>
    /// True for a Microsoft Store (packaged) client: the image lives under a WindowsApps folder. Only a whole path component
    /// counts, so a folder merely named like it does not. The log probe and the installation suggestion share this one rule.
    /// </summary>
    public static bool IsPackagedPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        foreach (string component in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (component.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool Matches(string? path, IReadOnlyList<string> names)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string name = Path.GetFileName(path);
        foreach (string candidate in names)
            if (name.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
