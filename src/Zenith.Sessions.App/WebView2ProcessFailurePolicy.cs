using Microsoft.Web.WebView2.Core;

namespace Zenith.Sessions.App;

/// <summary>
/// Decides which WebView2 <c>ProcessFailed</c> kinds end an app-owned private browser (sign-in or handoff check).
/// WebView2 recreates GPU and utility processes itself and calls sandbox-helper and plug-in exits not fatal; a crashed
/// subframe renderer leaves the top-level page in place. Those keep the window, its CoreWebView2 and every cancellation
/// guard attached to it. Browser, main-page, unresponsive, unknown and any later-added kinds still end it (fail closed).
/// </summary>
internal static class WebView2ProcessFailurePolicy
{
    internal const string RecoveredNotice = "Part of the Roblox page restarted. Continue here; if the page looks blank or stuck, press F5 to reload it.";

    public static bool EndsSession(CoreWebView2ProcessFailedKind kind) => kind switch
    {
        CoreWebView2ProcessFailedKind.GpuProcessExited
            or CoreWebView2ProcessFailedKind.UtilityProcessExited
            or CoreWebView2ProcessFailedKind.SandboxHelperProcessExited
            or CoreWebView2ProcessFailedKind.PpapiPluginProcessExited
            or CoreWebView2ProcessFailedKind.PpapiBrokerProcessExited
            or CoreWebView2ProcessFailedKind.FrameRenderProcessExited => false,
        _ => true,
    };
}
