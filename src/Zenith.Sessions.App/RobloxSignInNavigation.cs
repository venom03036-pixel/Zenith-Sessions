using System;

namespace Zenith.Sessions.App;

public static class RobloxSignInNavigation
{
    // Only top-level navigation is restricted here. Roblox controls its own resource/challenge
    // requests. Native launch schemes, downloads and arbitrary new windows are never dispatched.
    public static bool IsAllowed(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0) return false;
        string host = uri.IdnHost;
        return host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase);
    }
}
