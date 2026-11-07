using System.IO;
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

    // The handlers need a real WebView2 process crash to run, so their wiring is checked in the production source.
    [Theory]
    [InlineData("ProductionOwnedRobloxSignIn.cs")]
    [InlineData("ProductionOwnedRobloxHandoffProbe.cs")]
    public void ProcessFailedHandlerEndsTheWindowOnlyForTerminalFailures(string resource)
    {
        string body = Compact(HandlerBody(Read(resource), "void ProcessFailed("));
        int check = body.IndexOf("if(!WebView2ProcessFailurePolicy.EndsSession(e.ProcessFailedKind)){status.Text=WebView2ProcessFailurePolicy.RecoveredNotice;return;}", StringComparison.Ordinal);
        Assert.True(check >= 0, $"{resource}: ProcessFailed ends the window for every process kind instead of keeping it open for a restart WebView2 recovers from.");
        int end = body.IndexOf("completion.TrySet", StringComparison.Ordinal);
        Assert.True(end > check, $"{resource}: ProcessFailed no longer ends the window for a browser, page or unknown process failure.");
    }

    private static string Read(string resource)
    {
        using var stream = typeof(WebView2ProcessFailurePolicyTests).Assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    private static string HandlerBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start > 0, $"No {signature} handler found.");
        int open = source.IndexOf('{', start), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[(open + 1)..i];
        }
        throw new InvalidOperationException($"Unbalanced braces after {signature}");
    }
    private static string Compact(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
}
