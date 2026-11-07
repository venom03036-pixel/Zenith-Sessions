using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

/// <summary>Reads only the caller-owned private browser's one Roblox session cookie.</summary>
internal static class OwnedRobloxSessionCookie
{
    public static async Task<RobloxSessionCredential> ReadAsync(CoreWebView2CookieManager manager,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var cookies=await manager.GetCookiesAsync("https://www.roblox.com/");ct.ThrowIfCancellationRequested();
        var matches=cookies.Where(c=>c.Name==".ROBLOSECURITY"&&c.IsSecure&&c.IsHttpOnly
            &&c.Domain.TrimStart('.').Equals("roblox.com",StringComparison.OrdinalIgnoreCase)&&c.Path=="/").Take(2).ToArray();
        if(matches.Length!=1)throw new AccountLinkException("A completed Roblox sign-in was not found in this private window. Finish signing in, then choose Save again.");
        var cookie=matches[0];DateTimeOffset? expiry=cookie.IsSession?null:new DateTimeOffset(DateTime.SpecifyKind(cookie.Expires,DateTimeKind.Utc));
        return new RobloxSessionCredential(cookie.Value,expiry);
    }
}
