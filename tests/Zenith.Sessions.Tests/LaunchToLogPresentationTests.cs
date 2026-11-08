using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

// Synthetic receipts and links only; no process, log or browser is touched.
public sealed class LaunchToLogPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly LaunchProcessReceipt Receipt = new(42, Now.AddMinutes(-1).UtcTicks, Now, 1234567890);
    private static readonly Guid Operation = Guid.NewGuid();

    [Fact]
    public void EachLaunchToLogOutcomeHasItsOwnPlainTextAndALinkIsNeverAccountOrArrivalProof()
    {
        var holder = new SessionProcessKey(null, 77, Now.AddSeconds(-30).UtcTicks);
        string[] texts =
        [
            LaunchEvidenceRow.DescribeLaunchToLog(null, null),
            LaunchEvidenceRow.DescribeLaunchToLog(Receipt with { BrowserTrackerId = null }, new(Operation, LaunchLogLinkStatus.Linked, holder)),
            LaunchEvidenceRow.DescribeLaunchToLog(Receipt, null),
            LaunchEvidenceRow.DescribeLaunchToLog(Receipt, new(Operation, LaunchLogLinkStatus.NotLinked)),
            LaunchEvidenceRow.DescribeLaunchToLog(Receipt, new(Operation, LaunchLogLinkStatus.Linked, holder)),
            LaunchEvidenceRow.DescribeLaunchToLog(Receipt, new(Operation, LaunchLogLinkStatus.Linked)),
            LaunchEvidenceRow.DescribeLaunchToLog(Receipt, new(Operation, LaunchLogLinkStatus.Ambiguous)),
            LaunchEvidenceRow.DescribeLaunchToLog(Receipt, new(Operation, LaunchLogLinkStatus.Unavailable))
        ];
        Assert.All(texts, text => Assert.StartsWith("Client log: ", text));
        Assert.Equal(texts[0], texts[1]); // Without a recorded tracker there is nothing to match, whatever a link says.
        Assert.Equal(7, texts.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("PID 77", texts[4]); Assert.DoesNotContain("PID", texts[5]); Assert.DoesNotContain("PID 42", string.Concat(texts));
        Assert.Contains("not account or arrival proof", texts[4]); Assert.Contains("not account or arrival proof", texts[5]);
        Assert.DoesNotContain("1234567890", string.Concat(texts));
    }

    [Fact]
    public void TheAmbiguousTextCoversALogThatAlsoNamesAnotherLaunchsTracker()
    {
        // Ambiguous has two causes: a shared tracker (more than one log or launch), or this launch's own log also naming
        // another launch's tracker. The row must describe both, since the second is not a shared tracker at all.
        string text = LaunchEvidenceRow.DescribeLaunchToLog(Receipt, new(Operation, LaunchLogLinkStatus.Ambiguous));
        Assert.StartsWith("Client log: not matched", text);
        Assert.Contains("more than one log or launch shares this browser tracker", text);
        Assert.Contains("also names another launch's tracker", text);
    }

    [Fact]
    public void TheRowRaisesItsLaunchToLogTextOnlyWhenTheTextChanges()
    {
        var operation = new LaunchOperation(new(Operation, Guid.NewGuid(), 1, 123, 1818, 0, Now), LaunchState.ClientStartedUnverified,
            LaunchProblem.ClientStartedUnverified, Now, Receipt);
        var row = new LaunchEvidenceRow(operation); var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.Equal(LaunchEvidenceRow.DescribeLaunchToLog(Receipt, null), row.LaunchToLog);
        row.UpdateLaunchToLog(new(Operation, LaunchLogLinkStatus.NotLinked)); row.UpdateLaunchToLog(new(Operation, LaunchLogLinkStatus.NotLinked));
        Assert.Equal(new string?[] { nameof(LaunchEvidenceRow.LaunchToLog) }, raised);
        Assert.Equal(LaunchEvidenceRow.DescribeLaunchToLog(Receipt, new(Operation, LaunchLogLinkStatus.NotLinked)), row.LaunchToLog);
    }
}
