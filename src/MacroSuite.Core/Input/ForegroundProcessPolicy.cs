namespace MacroSuite.Core.Input;

/// <summary>
/// Process-name and integrity policy for synthesized input (Rule 4).
/// A denylist is a floor, not a ceiling — callers must also require an explicit macro run
/// and expected-window checks for AI actions.
/// </summary>
public static class ForegroundProcessPolicy
{
    public const int MediumIntegrityRid = 0x2000;

    private static readonly HashSet<string> BlockedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd",
        "powershell",
        "powershell_ise",
        "pwsh",
        "wt",
        "windowsterminal",
        "bash",
        "wsl",
        "wslhost",
        "wslservice",
        "debian",
        "ubuntu",
        "regedit",
        "reg",
        "mmc",
        "mshta",
        "cscript",
        "wscript",
        "rundll32",
        "certutil",
        "bitsadmin",
        "installutil",
        "curl",
        "wget",
        "ftp",
        "ssh",
        "scp",
        "taskkill",
        "schtasks"
    };

    public static bool IsBlockedProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return false;

        ReadOnlySpan<char> name = processName.AsSpan().Trim();
        int slash = name.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
            name = name[(slash + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        if (name.IsEmpty)
            return false;

        return BlockedNames.Contains(name.ToString());
    }

    public static bool IsHigherIntegrity(int foregroundRid, int selfRid)
        => foregroundRid > selfRid;
}
