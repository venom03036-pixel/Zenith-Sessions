using System.Xml.Linq;
using Xunit;
using Zenith.Sessions.App;

namespace Zenith.Sessions.Tests;

// M-L2-4: the Guide must describe the queue as it behaves now. After Z1251 a restart brings a request that
// never started back as a cancellable row that reserves nothing, not as a review row; only a request that may
// have started (or one read from the backup) asks for review. R-launch-1's refusal backoff and the pause after
// two refused starts in a row were not described anywhere the user can read.
public sealed class LaunchQueueGuideTests
{
    private static List<string> GuideTexts()
    {
        using var stream=typeof(LaunchQueueGuideTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        var document=XDocument.Load(stream);
        var guide=Assert.Single(document.Descendants().Where(e=>e.Name.LocalName=="ScrollViewer"
            && ((string?)e.Attribute("Visibility"))?.Contains("IsGuide",StringComparison.Ordinal)==true));
        return guide.Descendants().Where(e=>e.Name.LocalName=="TextBlock").Select(e=>(string?)e.Attribute("Text")??"").ToList();
    }

    private static string Section(List<string> texts,string title)
    {
        int start=texts.IndexOf(title);
        Assert.True(start>=0,$"The Guide has no section titled '{title}'.");
        return string.Join(" ",texts.Skip(start+1).TakeWhile(text=>!System.Text.RegularExpressions.Regex.IsMatch(text,@"^\d+\. ")));
    }

    [Fact]
    public void TheRecoverySectionSaysANeverStartedRequestReservesNothingAndCanOnlyBeCancelled()
    {
        var texts=GuideTexts();
        string recovery=Section(texts,"8. Interrupted saves and recovery");
        Assert.Contains("After any restart the queue opens paused and nothing starts on its own.",recovery,StringComparison.Ordinal);
        Assert.Contains($"comes back as {LaunchRow.NotStartedInterrupted}",recovery,StringComparison.Ordinal);
        Assert.Contains("it never started a client, reserves no resources and can only be cancelled",recovery,StringComparison.Ordinal);
        Assert.Contains("Review preparation",recovery,StringComparison.Ordinal);
        Assert.Contains("Review required",recovery,StringComparison.Ordinal);
        // Backup rows are one generation behind, so the Guide must not promise they never started.
        Assert.Contains("the lost main file may already have started them",recovery,StringComparison.Ordinal);
        // The old claim that a restart turns all pending work into review is gone everywhere in the Guide.
        Assert.DoesNotContain(texts,text=>text.Contains("Restart restores pending work for review",StringComparison.Ordinal));
    }

    [Fact]
    public void TheSetAsideGuideAndTooltipSayAQueueThatWasRunningNeedsARestart()
    {
        // N-B2-2: setting the save aside reopens the queue only when it never opened; a queue that was
        // already running stays stopped until a restart (M-L2-3), so neither text may promise otherwise.
        string recovery=Section(GuideTexts(),"8. Interrupted saves and recovery");
        Assert.Contains("keeps it, and reopens the last completed queue paused.",recovery,StringComparison.Ordinal);
        Assert.Contains("If the queue was already running, setting the save aside keeps it stopped until you restart Zenith Sessions.",recovery,StringComparison.Ordinal);
        using var stream=typeof(LaunchQueueGuideTests).Assembly.GetManifestResourceStream("ProductionMainWindow.xaml")!;
        var button=Assert.Single(XDocument.Load(stream).Descendants(),e=>e.Name.LocalName=="Button"
            && (string?)e.Attribute("Content")=="Set aside interrupted save");
        string tooltip=(string?)button.Attribute("ToolTip")??"";
        Assert.Contains("Nothing is deleted.",tooltip,StringComparison.Ordinal);
        Assert.Contains("The queue reopens paused; if it was already running, it reopens after you restart Zenith Sessions.",tooltip,StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuideNamesTheRefusalBackoffAndThePauseAfterTwoInARow()
    {
        var texts=GuideTexts();
        string pacing=Assert.Single(texts,text=>text.Contains("twice the Launch spacing",StringComparison.Ordinal));
        Assert.Contains("If the provider refused a start before any client started",pacing,StringComparison.Ordinal);
        Assert.Contains("doubles again for each such start in a row, up to about 1 minute",pacing,StringComparison.Ordinal);
        Assert.Contains("up to 1 second of random variation",pacing,StringComparison.Ordinal);
        Assert.Contains("Two in a row pause the queue",pacing,StringComparison.Ordinal);
        Assert.Contains("the remaining requests stay queued",pacing,StringComparison.Ordinal);
        Assert.Contains("until a client starts or 10 minutes pass, one more such start pauses it again",pacing,StringComparison.Ordinal);
        // Memory-only: nothing about the wait or the count is saved.
        Assert.Contains("kept only while Zenith runs",pacing,StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuideSaysOnlyARefusedStartLengthensTheWaitNotATimeoutExpiryStopOrChangedAccount()
    {
        // N-B2-3: only the provider's own Rejected answer before any client started backs off. A timed-out,
        // expired or stopped start, or one whose account changed first, starts no longer wait, so the Guide must
        // not promise one for every such start. A page the user closed or cancelled is answered Rejected by the
        // native provider, so it does count.
        string pacing=Assert.Single(GuideTexts(),text=>text.Contains("twice the Launch spacing",StringComparison.Ordinal));
        Assert.DoesNotContain("If a start ended before any client started",pacing,StringComparison.Ordinal);
        Assert.Contains("If the provider refused a start before any client started, the next one waits twice the Launch spacing",pacing,StringComparison.Ordinal);
        Assert.Contains("Closing or cancelling a request's private Roblox page counts as a refusal too.",pacing,StringComparison.Ordinal);
        // O-C2-1: a timeout or stop during native startup is recorded Uncertain (TimedOut or
        // CancelledDuringStartup), whose rows say a client may exist, so the Guide may not say that every
        // such start ended before any client started. Only the wait claim is true of all of them.
        Assert.DoesNotContain("also ended before any client started",pacing,StringComparison.Ordinal);
        Assert.Contains("A start that timed out, expired or was stopped in Zenith Sessions, or whose account changed first, does not lengthen the wait; a start that timed out or was stopped during startup may still have left a client, so reconcile it before retrying.",pacing,StringComparison.Ordinal);
    }
}
