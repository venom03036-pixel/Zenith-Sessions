using System;

namespace Zenith.Sessions.App;

public static class RobloxSignInNavigation
{
    // Top-level navigation stays on Roblox's HTTPS pages. Roblox controls its own resource/challenge requests.
    // Native launch schemes are cancelled by the owner's LaunchingExternalUriScheme handler, including those a
    // child frame starts (Roblox's Play button uses a hidden frame); downloads and new windows are refused there too.
    public static bool IsAllowed(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0) return false;
        string host = uri.IdnHost;
        return host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase);
    }

    // Child frames are judged by scheme only: sign-in challenges may load third-party HTTPS frames, so no host rule
    // applies. Everything that is not a web page or an in-page document (roblox-player:, javascript:, http:, file:) is refused.
    public static bool IsAllowedFrame(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "blob" || uri.Scheme == "data") return true;
        return uri.Scheme == "about" && (uri.AbsolutePath.Equals("blank", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Equals("srcdoc", StringComparison.OrdinalIgnoreCase));
    }
}
