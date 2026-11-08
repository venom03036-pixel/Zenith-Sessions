using Microsoft.Web.WebView2.Core;
using Zenith.Sessions.App;
using Xunit;

namespace Zenith.Sessions.Tests;

/// <summary>
/// WebView2 restarts its GPU, utility, sandbox-helper and plug-in processes by itself, and a crashed subframe renderer
/// leaves the top-level page in place. Ending a sign-in or handoff check for those threw away the user's progress, and
/// native preparation had already marked the account as needing a new sign-in.
/// </summary>
public sealed class WebView2ProcessFailurePolicyTests
{
    private static readonly CoreWebView2ProcessFailedKind[] Recoverable =
    [
        CoreWebView2ProcessFailedKind.GpuProcessExited,
        CoreWebView2ProcessFailedKind.UtilityProcessExited,
        CoreWebView2ProcessFailedKind.SandboxHelperProcessExited,
        CoreWebView2ProcessFailedKind.PpapiPluginProcessExited,
        CoreWebView2ProcessFailedKind.PpapiBrokerProcessExited,
        CoreWebView2ProcessFailedKind.FrameRenderProcessExited,
    ];

    [Theory]
    [InlineData(CoreWebView2ProcessFailedKind.GpuProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.UtilityProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.SandboxHelperProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.PpapiPluginProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.PpapiBrokerProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.FrameRenderProcessExited)]
    public void RecoverableWebView2FailuresDoNotEndTheSession(CoreWebView2ProcessFailedKind kind)
        => Assert.False(WebView2ProcessFailurePolicy.EndsSession(kind), $"{kind} restarts or stays inside a subframe, yet it ends the private browser.");

    [Theory]
    [InlineData(CoreWebView2ProcessFailedKind.BrowserProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)]
    [InlineData(CoreWebView2ProcessFailedKind.UnknownProcessExited)]
    public void FatalOrUnknownFailuresEndTheSession(CoreWebView2ProcessFailedKind kind)
        => Assert.True(WebView2ProcessFailurePolicy.EndsSession(kind), $"{kind} leaves no usable page, yet the private browser stays open.");

    [Fact]
    public void EveryKindOutsideTheRecoverableListIncludingFutureOnesEndsTheSession()
    {
        foreach (var kind in Enum.GetValues<CoreWebView2ProcessFailedKind>())
            if (Array.IndexOf(Recoverable, kind) < 0)
                Assert.True(WebView2ProcessFailurePolicy.EndsSession(kind), $"{kind} is not known to be recoverable, yet it keeps the private browser open.");
        Assert.True(WebView2ProcessFailurePolicy.EndsSession((CoreWebView2ProcessFailedKind)1000), "A process kind added by a later runtime keeps the private browser open.");
    }

    [Fact]
    public void RecoveredNoticeSaysThePageStaysAndHowToReloadIt()
    {
        Assert.Contains("restarted", WebView2ProcessFailurePolicy.RecoveredNotice);
        Assert.Contains("F5", WebView2ProcessFailurePolicy.RecoveredNotice);
    }

    [Theory]
    [InlineData(CoreWebView2ProcessFailedKind.GpuProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.UtilityProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.SandboxHelperProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.PpapiPluginProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.PpapiBrokerProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.FrameRenderProcessExited)]
    public void RecoverableFailureOnlyShowsTheRecoveredNotice(CoreWebView2ProcessFailedKind kind)
    {
        var calls = new List<string>();
        WebView2ProcessFailurePolicy.HandleProcessFailure(kind, text => calls.Add(text), () => calls.Add("end"));
        Assert.Equal(new[] { WebView2ProcessFailurePolicy.RecoveredNotice }, calls);
    }

    [Theory]
    [InlineData(CoreWebView2ProcessFailedKind.BrowserProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessExited)]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)]
    [InlineData(CoreWebView2ProcessFailedKind.UnknownProcessExited)]
    [InlineData((CoreWebView2ProcessFailedKind)1000)]
    public void TerminalFailureEndsTheWindowWithoutClaimingARecovery(CoreWebView2ProcessFailedKind kind)
    {
        var calls = new List<string>();
        WebView2ProcessFailurePolicy.HandleProcessFailure(kind, text => calls.Add(text), () => calls.Add("end"));
        Assert.Equal(new[] { "end" }, calls);
    }

    // The handlers need a real WebView2 process crash to run, so their body is HandleProcessFailure, run above. This
    // supplement checks that each live handler is exactly that call, ending its own window, and is attached before the page opens.
    [Theory]
    [InlineData("ProductionOwnedRobloxSignIn.cs", "()=>completion.TrySetException(newAccountLinkException(\"\"))")]
    [InlineData("ProductionOwnedRobloxHandoffProbe.cs", "()=>completion.TrySetResult(Capture(RobloxHandoffProbeOutcome.Failed))")]
    public void ProcessFailedHandlerEndsTheWindowOnlyForTerminalFailures(string resource, string endsWindow)
    {
        string code = ProductionSourceText.Code(resource);
        Assert.Equal($"WebView2ProcessFailurePolicy.HandleProcessFailure(e.ProcessFailedKind,text=>status.Text=text,{endsWindow});",
            ProductionSourceText.HandlerBody(code, "void ProcessFailed("));
        ProductionSourceText.AssertRunsOnEveryPathTo(code, "core.ProcessFailed += ProcessFailed;", "core.Navigate(",
            $"{resource}: the page can open before ProcessFailed is attached to its CoreWebView2.");
    }
}
