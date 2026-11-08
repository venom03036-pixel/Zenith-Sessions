namespace Zenith.Sessions.Windows;

/// <summary>
/// The test-only seam that points a Roblox web client at a loopback test server over plain http, so the production
/// handler settings (no redirects, no cookie jar, header limits) are exercised over a real socket. Only the origin
/// moves; every path and query stays the production one. Only a literal loopback IP is accepted, so the seam can
/// never send a request, or a session cookie, over plain http to another host.
/// </summary>
internal static class LoopbackTestOrigin
{
    /// <summary>The origin without a trailing slash, for example "http://127.0.0.1:50123".</summary>
    internal static string Require(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttp || origin.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6)
            || !origin.IsLoopback || origin.UserInfo.Length != 0 || origin.PathAndQuery != "/" || origin.Fragment.Length != 0)
            throw new ArgumentException("A test origin must be a plain loopback address such as http://127.0.0.1:50123/.", nameof(origin));
        return origin.GetLeftPart(UriPartial.Authority);
    }
}
