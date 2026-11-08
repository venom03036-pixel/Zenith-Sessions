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
    // applies. Only HTTPS pages and in-page documents (about:blank, about:srcdoc, blob:, data:) load; everything else
    // (roblox-player:, javascript:, http:, file:) is refused.
    public static bool IsAllowedFrame(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "blob" || uri.Scheme == "data") return true;
        return uri.Scheme == "about" && (uri.AbsolutePath.Equals("blank", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Equals("srcdoc", StringComparison.OrdinalIgnoreCase));
    }

    // Schemes the browser loads itself: web pages, local files, in-page documents and scripts, and browser pages.
    private static readonly string[] BrowserSchemes = ["http", "https", "file", "about", "javascript", "data", "blob", "view-source", "edge", "chrome"];

    /// <summary>True when <paramref name="address"/> names an app's protocol (roblox-player:, ms-settings:), which Windows
    /// would hand to that app, rather than something the browser loads itself. Only the status text depends on it: the
    /// frame rule above decides what is refused. An address with no RFC 3986 scheme names no app.</summary>
    public static bool IsAppLaunch(string? address)
    {
        int colon = address?.IndexOf(':') ?? -1;
        if (colon < 1 || !char.IsAsciiLetter(address![0])) return false;
        for (int i = 1; i < colon; i++)
            if (!char.IsAsciiLetterOrDigit(address[i]) && address[i] is not ('+' or '-' or '.')) return false;
        var scheme = address.AsSpan(0, colon);
        foreach (var browser in BrowserSchemes)
            if (scheme.Equals(browser, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
