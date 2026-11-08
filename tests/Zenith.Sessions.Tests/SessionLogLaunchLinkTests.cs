using System.IO;
using System.Text.Json;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

// Launch-to-log. Every tracker line, log and process below is SYNTHETIC; the tag name is invented and the marker
// shapes come from a public-text study, so they do not establish support for any client version.
public sealed partial class SessionLogCorrelatorTests
{
    private const long Tracker = 1234567890;
    // Before both the fixed candidate time and the real creation time of the temp files.
    private static long LaunchStart => Math.Min(SyntheticClientLogFixtures.Time.UtcTicks, DateTime.UtcNow.Ticks) - TimeSpan.TicksPerMinute;
    private static string TrackerLine(long tracker = Tracker, string marker = "websiteBTId is ")
        => SyntheticClientLogFixtures.Line("FLog::SyntheticStartup", marker + tracker);
    private static LaunchLogExpectation Launch(long tracker = Tracker, long? notBefore = null) => new(Guid.NewGuid(), tracker, notBefore ?? LaunchStart);

    [Theory]
    [InlineData("websiteBTId is ")]
    [InlineData("BTID is overriden to ")]
    public async Task AHeadNamingTheLaunchTrackerLinksTheLogToItsCurrentHolderUnderANewPid(string marker)
    {
        // The receipt named a first process that is gone (a bootstrapper); the restarted client 77 holds the log.
        var client = Key(77); Write(TrackerLine(marker: marker) + SyntheticClientLogFixtures.Joining + SyntheticClientLogFixtures.Identity);
        var launch = Launch();
        await using var worker = new WindowsSessionLogCorrelator(new Probe(Log, client));
        worker.Offer(Desktop(client), client.HostId, [launch]);
        var snapshot = await After(worker);
        var link = Assert.Single(snapshot.Launches);
        Assert.Equal(new LaunchLogLink(launch.OperationId, LaunchLogLinkStatus.Linked, client), link);
        Assert.False(link.CanAuthorizeAccountAction);
        Assert.Equal(SessionLogCorrelationStatus.ResourceHolderObserved, Assert.Single(snapshot.Sessions).Correlation);
        string json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain(_root, json); Assert.DoesNotContain(Tracker.ToString(System.Globalization.CultureInfo.InvariantCulture), json);
    }

    [Fact]
    public async Task ALinkedLogWithNoCurrentHolderNamesNoProcess()
    {
        var client = Key(77); Write(TrackerLine());
        var probe = new Probe(Log, client) { Result = new(SessionLogCorrelationStatus.ResourceHolderObserved, []) };
        var launch = Launch();
        await using var worker = new WindowsSessionLogCorrelator(probe);
        worker.Offer(Desktop(client), client.HostId, [launch]);
        Assert.Equal(new LaunchLogLink(launch.OperationId, LaunchLogLinkStatus.Linked), Assert.Single((await After(worker)).Launches));
    }

    [Theory]
    [InlineData("two-logs")]
    [InlineData("two-launches")]
    [InlineData("log-names-two-launches")]
    public async Task ASharedTrackerIsAmbiguousAndNamesNoProcess(string shape)
    {
        var client = Key(77);
        Write(TrackerLine() + (shape == "log-names-two-launches" ? TrackerLine(Tracker + 1, "BTID is overriden to ") : ""));
        var probe = new Probe(Log, client);
        if (shape == "two-logs") probe.Files = [probe.Files[0], Candidate("other", TrackerLine())];
        probe.HolderFor = path => path == Log ? HeldBy(client) : new(SessionLogCorrelationStatus.ResourceHolderObserved, []);
        LaunchLogExpectation[] launches = shape switch
        {
            "two-logs" => [Launch()],
            // Both began at the same instant, so the log could be either's.
            "two-launches" => [Launch(), Launch()],
            _ => [Launch(), Launch(Tracker + 1)]
        };
        await using var worker = new WindowsSessionLogCorrelator(probe);
        worker.Offer(Desktop(client), client.HostId, launches);
        var links = (await After(worker)).Launches;
        Assert.Equal(launches.Select(launch => new LaunchLogLink(launch.OperationId, LaunchLogLinkStatus.Ambiguous)), links);
    }

    [Fact]
    public async Task ALogWhoseHeadCouldNotBeReadKeepsTheLaunchUnlinkedUntilItsHeadIsRead()
    {
        // A second log written since the launch began is locked by another writer, so its head read fails. It could name
        // the tracker too, so the readable log that does is not linked until that head has been read.
        var client = Key(77); var clock = new Clock(); Write(TrackerLine());
        var probe = new Probe(Log, client);
        var locked = Candidate("locked", SyntheticClientLogFixtures.Line("FLog::SyntheticStartup", "starting"));
        probe.Files = [probe.Files[0], locked];
        probe.HolderFor = path => path == Log ? HeldBy(client) : new(SessionLogCorrelationStatus.ResourceHolderObserved, []);
        var launch = Launch();
        await using var worker = new WindowsSessionLogCorrelator(probe, timeProvider: clock);
        SessionLogDiscoverySnapshot first;
        using (new FileStream(locked.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            worker.Offer(Desktop(client), client.HostId, [launch]);
            first = await After(worker);
        }
        Assert.Equal(new LaunchLogLink(launch.OperationId, LaunchLogLinkStatus.NotLinked), Assert.Single(first.Launches));
        clock.Advance(2500); worker.Offer(Desktop(client), client.HostId, [launch]);
        Assert.Equal(new LaunchLogLink(launch.OperationId, LaunchLogLinkStatus.Linked, client), Assert.Single((await After(worker, first.Revision)).Launches));
    }

    [Theory]
    [InlineData("second-ambiguous")]
    [InlineData("second-pending")]
    public async Task ALogThatAlsoNamesAnotherLaunchsTrackerLinksNeitherEvenWhenThatLaunchCannotLink(string shape)
    {
        // The held log names both launches' trackers. The second launch, which began earlier, cannot link: another log
        // written since it began names its tracker too, or one could not be read. The held log is still no more the
        // first launch's than the second's.
        var client = Key(77); var now = DateTimeOffset.UtcNow;
        Write(TrackerLine() + TrackerLine(Tracker + 1, "BTID is overriden to "));
        var other = Candidate("other", shape == "second-ambiguous" ? TrackerLine(Tracker + 1) : "");
        File.SetCreationTimeUtc(other.Path, now.AddMinutes(-100).UtcDateTime);
        var probe = new Probe(Log, client) { Files = [new(Log, ClientLogVariant.Win32, now), other with { ModifiedUtc = now.AddMinutes(-90) }] };
        probe.HolderFor = path => path == Log ? HeldBy(client) : new(SessionLogCorrelationStatus.ResourceHolderObserved, []);
        using var hold = shape == "second-pending" ? new FileStream(other.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        LaunchLogExpectation first = Launch(notBefore: now.AddHours(-1).UtcTicks), second = Launch(Tracker + 1, now.AddHours(-2).UtcTicks);
        await using var worker = new WindowsSessionLogCorrelator(probe);
        worker.Offer(Desktop(client), client.HostId, [first, second]);
        Assert.Equal(new[]
        {
            new LaunchLogLink(first.OperationId, LaunchLogLinkStatus.Ambiguous),
            new LaunchLogLink(second.OperationId, shape == "second-pending" ? LaunchLogLinkStatus.NotLinked : LaunchLogLinkStatus.Ambiguous)
        }, (await After(worker)).Launches);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALogNamingTheTrackerOfALaunchThatBeganAfterItWasCreatedStillLinksTheLaunchItBelongsTo(bool secondBeganAfterTheLog)
    {
        // The held log, created 30 minutes ago, names both launches' trackers. When the second launch began 20 minutes
        // ago, that log cannot be its own (it was created before the launch began), so naming the second's tracker does
        // not make the log any less the first launch's. Had the second launch begun at the instant the log was created,
        // the log could be either's, and neither links.
        var client = Key(77); var now = DateTimeOffset.UtcNow; var created = now.AddMinutes(-30);
        Write(TrackerLine() + TrackerLine(Tracker + 1, "BTID is overriden to "));
        File.SetCreationTimeUtc(Log, created.UtcDateTime);
        var probe = new Probe(Log, client) { Files = [new(Log, ClientLogVariant.Win32, now)] };
        LaunchLogExpectation first = Launch(notBefore: now.AddHours(-1).UtcTicks),
            second = Launch(Tracker + 1, (secondBeganAfterTheLog ? created.AddMinutes(10) : created).UtcTicks);
        await using var worker = new WindowsSessionLogCorrelator(probe);
        worker.Offer(Desktop(client), client.HostId, [first, second]);
        LaunchLogLink[] expected = secondBeganAfterTheLog
            ? [new(first.OperationId, LaunchLogLinkStatus.Linked, client), new(second.OperationId, LaunchLogLinkStatus.NotLinked)]
            : [new(first.OperationId, LaunchLogLinkStatus.Ambiguous), new(second.OperationId, LaunchLogLinkStatus.Ambiguous)];
        Assert.Equal(expected, (await After(worker)).Launches);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARelaunchWithTheSameTrackerLinksEachLogToTheLatestLaunchThatBeganBeforeIt(bool previousLogKept)
    {
        // One browser tracker, launched two hours ago and again an hour ago. The log created since the second launch began
        // is the second's; one created between the two launches can only be the first's. Neither makes the other ambiguous.
        var client = Key(77); var now = DateTimeOffset.UtcNow;
        Write(TrackerLine());
        var probe = new Probe(Log, client) { Files = [new(Log, ClientLogVariant.Win32, now)] };
        if (previousLogKept)
        {
            var previous = Candidate("previous", TrackerLine());
            File.SetCreationTimeUtc(previous.Path, now.AddMinutes(-90).UtcDateTime);
            probe.Files = [probe.Files[0], previous with { ModifiedUtc = now.AddMinutes(-80) }];
        }
        probe.HolderFor = path => path == Log ? HeldBy(client) : new(SessionLogCorrelationStatus.ResourceHolderObserved, []);
        LaunchLogExpectation first = Launch(notBefore: now.AddHours(-2).UtcTicks), second = Launch(notBefore: now.AddHours(-1).UtcTicks);
        await using var worker = new WindowsSessionLogCorrelator(probe);
        worker.Offer(Desktop(client), client.HostId, [second, first]);
        Assert.Equal(new[]
        {
            new LaunchLogLink(second.OperationId, LaunchLogLinkStatus.Linked, client),
            new LaunchLogLink(first.OperationId, previousLogKept ? LaunchLogLinkStatus.Linked : LaunchLogLinkStatus.NotLinked)
        }, (await After(worker)).Launches);
    }

    [Theory]
    [InlineData("other-tracker")]
    [InlineData("after-joining")]
    [InlineData("created-before-launch")]
    [InlineData("partial-line")]
    public async Task OnlyACompleteHeadLineBeforeJoiningInALogCreatedSinceTheLaunchCanLink(string shape)
    {
        var client = Key(77);
        Write(shape switch
        {
            "other-tracker" => TrackerLine(Tracker + 1),
            // After joining, a game's own output can print anything; the head ends at the first join line.
            "after-joining" => SyntheticClientLogFixtures.Joining + SyntheticClientLogFixtures.Line("FLog::Output", "websiteBTId is " + Tracker),
            "partial-line" => TrackerLine().TrimEnd('\n'),
            _ => TrackerLine()
        });
        var probe = new Probe(Log, client);
        long? notBefore = null;
        if (shape == "created-before-launch")
        {
            // The candidate looks freshly written, but the file itself was created before this launch began.
            probe.Files = [new(Log, ClientLogVariant.Win32, DateTimeOffset.UtcNow.AddHours(2))];
            notBefore = DateTime.UtcNow.AddHours(1).Ticks;
        }
        var launch = Launch(notBefore: notBefore);
        await using var worker = new WindowsSessionLogCorrelator(probe);
        worker.Offer(Desktop(client), client.HostId, [launch]);
        Assert.Equal(new LaunchLogLink(launch.OperationId, LaunchLogLinkStatus.NotLinked), Assert.Single((await After(worker)).Launches));
    }

    [Fact]
    public async Task AHeadStillBeingWrittenIsReadAgainUntilItNamesTheTracker()
    {
        var client = Key(77); var clock = new Clock(); Write(SyntheticClientLogFixtures.Line("FLog::SyntheticStartup", "starting"));
        var launch = Launch();
        await using var worker = new WindowsSessionLogCorrelator(new Probe(Log, client), timeProvider: clock);
        worker.Offer(Desktop(client), client.HostId, [launch]);
        var first = await After(worker);
        Assert.Equal(LaunchLogLinkStatus.NotLinked, Assert.Single(first.Launches).Status);
        File.AppendAllText(Log, TrackerLine());
        clock.Advance(2500); worker.Offer(Desktop(client), client.HostId, [launch]);
        Assert.Equal(new LaunchLogLink(launch.OperationId, LaunchLogLinkStatus.Linked, client), Assert.Single((await After(worker, first.Revision)).Launches));
    }

    [Theory]
    [InlineData("ended")]
    [InlineData("still-held")]
    [InlineData("held-again")]
    public async Task AnIncompleteHeadIsNoLongerReopenedOnceNothingHoldsItsLogAndItsLengthSettled(string shape)
    {
        // A second log, written since the launch began, ended before any join line, so its short head never completes.
        // Once a holder query finds no process holding it and two checks since then find it unchanged, it is not opened
        // again: a later exclusive lock on it (which a reopen would fail on, leaving the launch pending) changes nothing.
        // While any process holds it, or once one holds it again, it is still checked.
        var client = Key(77); var clock = new Clock(); Write(TrackerLine() + SyntheticClientLogFixtures.Joining);
        var probe = new Probe(Log, client);
        var ended = Candidate("ended", SyntheticClientLogFixtures.Line("FLog::SyntheticStartup", "starting"));
        probe.Files = [probe.Files[0], ended];
        bool held = shape == "still-held";
        probe.HolderFor = path => path == Log ? HeldBy(client) : held ? HeldBy(Key(99)) : new(SessionLogCorrelationStatus.ResourceHolderObserved, []);
        var launch = Launch();
        await using var worker = new WindowsSessionLogCorrelator(probe, timeProvider: clock);
        var snapshot = new SessionLogDiscoverySnapshot(0, DateTimeOffset.MinValue, [], 0, false);
        async Task Pass(int advance)
        {
            clock.Advance(advance); worker.Offer(Desktop(client), client.HostId, [launch]);
            snapshot = await After(worker, snapshot.Revision);
            Assert.Equal(new LaunchLogLink(launch.OperationId, LaunchLogLinkStatus.Linked, client), Assert.Single(snapshot.Launches));
        }
        await Pass(0); await Pass(2500); await Pass(2500);
        if (shape == "held-again") held = true;
        await Pass(5000);
        using var hold = new FileStream(ended.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        clock.Advance(2500); worker.Offer(Desktop(client), client.HostId, [launch]);
        Assert.Equal(new LaunchLogLink(launch.OperationId, shape == "ended" ? LaunchLogLinkStatus.Linked : LaunchLogLinkStatus.NotLinked, shape == "ended" ? client : null),
            Assert.Single((await After(worker, snapshot.Revision)).Launches));
    }

    [Fact]
    public async Task NoOfferedLaunchPublishesNoLinksAndInvalidLaunchesAreDropped()
    {
        var client = Key(77); Write(TrackerLine());
        await using var worker = new WindowsSessionLogCorrelator(new Probe(Log, client));
        worker.Offer(Desktop(client), client.HostId, [new(Guid.Empty, Tracker, LaunchStart), new(Guid.NewGuid(), 0, LaunchStart),
            new(Guid.NewGuid(), LaunchProcessReceipt.MaximumBrowserTrackerId + 1, LaunchStart)]);
        var snapshot = await After(worker);
        Assert.Empty(snapshot.Launches); Assert.Equal(SessionLogCorrelationStatus.ResourceHolderObserved, Assert.Single(snapshot.Sessions).Correlation);
    }

    [Fact]
    public void ExpectationsComeFromValidReceiptTrackersNewestFirstAndAreBounded()
    {
        var now = DateTimeOffset.UtcNow;
        LaunchOperation Row(int index, long? tracker) => new(new(Guid.NewGuid(), Guid.NewGuid(), 1, 123, 1818, 0, now),
            LaunchState.ClientStartedUnverified, LaunchProblem.ClientStartedUnverified, now,
            new(1000 + index, now.AddMinutes(-1).UtcTicks, now.AddSeconds(-index), tracker));
        var rows = Enumerable.Range(0, 40).Select(index => Row(index, index == 3 ? null : 100 + index)).ToArray();
        rows[5] = rows[5] with { ProcessReceipt = rows[5].ProcessReceipt! with { BrowserTrackerId = 0 } };
        var launches = LaunchLogExpectation.From(new(false, rows.Reverse().ToArray()));
        Assert.Equal(LaunchLogExpectation.MaximumLaunches, launches.Length);
        Assert.Equal(new LaunchLogExpectation(rows[0].Intent.OperationId, 100, rows[0].ProcessReceipt!.ProcessStartUtcTicks), launches[0]);
        Assert.Equal(rows.Where((_, index) => index is not 3 and not 5).Take(LaunchLogExpectation.MaximumLaunches).Select(row => row.Intent.OperationId),
            launches.Select(launch => launch.OperationId));
        Assert.Empty(LaunchLogExpectation.From(null)); Assert.Empty(LaunchLogExpectation.From(LaunchQueueSnapshot.Empty));
    }
}
