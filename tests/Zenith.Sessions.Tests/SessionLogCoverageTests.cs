using System.IO;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed partial class SessionLogCorrelatorTests
{
    private static DesktopSnapshot CurrentDesktop(SessionProcessKey key, Clock clock)
    {
        var now = clock.GetUtcNow();
        var desktop = Desktop(key);
        return desktop with
        {
            Sessions = [desktop.Sessions[0] with { ObservedUtc = now }],
            ProcessObservedUtc = now, ProcessRevision = 1, ProcessInventoryComplete = true
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportedServerLinkIsUnavailableWhileARealFileDisconnectIsStillUnread(bool partialLine)
    {
        var clock = new Clock();
        var key = Key() with { HostId = null };
        Write(SyntheticClientLogFixtures.Joining + SyntheticClientLogFixtures.Identity + SyntheticClientLogFixtures.Connected);
        await using var worker = new WindowsSessionLogCorrelator(new Probe(Log, key),
            new(ReadsPerPass: 1, SharedReadBytes: 1024, BytesPerReader: 1024), clock);
        var projection = new SessionLogProjection();
        var desktop = CurrentDesktop(key, clock);
        worker.Offer(desktop);
        var original = await After(worker);
        Assert.NotNull(projection.BuildCurrentLink(key, desktop, original, clock.GetUtcNow()));

        var disconnect = SyntheticClientLogFixtures.Disconnected;
        string firstPart = partialLine ? disconnect[..(disconnect.Length - 1)]
            : string.Concat(Enumerable.Repeat("ignored log output\n", 150)) + disconnect;
        File.AppendAllText(Log, firstPart);
        clock.Advance(1000);
        desktop = CurrentDesktop(key, clock);
        worker.Offer(desktop);
        var partial = await After(worker, original.Revision);
        var batch = Assert.Single(partial.Sessions).Batch!;
        Assert.Equal(!partialLine, batch.HasMore);
        Assert.True(batch.HasMore || batch.ReadOffset > batch.CompleteLineOffset);
        Assert.Null(projection.BuildCurrentLink(key, desktop, partial, clock.GetUtcNow()));
        var pending = Assert.Single(projection.EvidenceSnapshot(clock.GetUtcNow()));
        Assert.Equal(SyntheticClientLogFixtures.Job, pending.LastKnownDestination?.JobId);
        Assert.Null(projection.BuildCurrentLink(key, desktop, original, clock.GetUtcNow()));

        if (partialLine) File.AppendAllText(Log, "\n");
        for (int pass = 0; pass < 6; pass++)
        {
            clock.Advance(1000);
            desktop = CurrentDesktop(key, clock);
            worker.Offer(desktop);
            partial = await After(worker, partial.Revision);
            Assert.Null(projection.BuildCurrentLink(key, desktop, partial, clock.GetUtcNow()));
            if (Assert.Single(partial.Sessions).Batch is { HasMore: false } tail && tail.ReadOffset == tail.CompleteLineOffset) break;
        }
        var finished = Assert.Single(projection.EvidenceSnapshot(clock.GetUtcNow()));
        Assert.Equal(ObservedSessionLifecycle.Disconnected, finished.Lifecycle);
        Assert.Null(finished.CurrentDestination);
    }

    [Fact]
    public async Task ReplacementWaitRejectsANewerPublicationThatHasNotReadTheAppendedBytes()
    {
        var stamp=new ClientLogStreamId(Guid.NewGuid(),1);var now=SyntheticClientLogFixtures.Time;
        var unread=new SessionLogDiscoverySnapshot(2,now,[new(Key(),SessionLogCorrelationStatus.ResourceHolderObserved,stamp,
            new(stamp,ClientLogReadStatus.Ready,ClientLogDiscontinuity.None,50,50,false,0,[]),now)],1,false);
        var complete=unread with {Revision=3,Sessions=[unread.Sessions[0] with {Batch=unread.Sessions[0].Batch! with {ReadOffset=100,CompleteLineOffset=100}}]};
        int reads=0;
        var result=await After(()=>++reads==1?unread:complete,1,snapshot=>snapshot.Sessions.Count==1
            && snapshot.Sessions[0].Batch is {HasMore:false} tail && tail.CompleteLineOffset>=100);
        Assert.Same(complete,result);Assert.Equal(2,reads);
    }

    [Fact]
    public async Task DroppingADisconnectFromBoundedReplayCannotRetainTheOldServerAfterACoalescedUpdate()
    {
        var clock = new Clock();
        var key = Key() with { HostId = null };
        Write(SyntheticClientLogFixtures.Joining + SyntheticClientLogFixtures.Identity + SyntheticClientLogFixtures.Connected);
        await using var worker = new WindowsSessionLogCorrelator(new Probe(Log, key), new(ReplayFactsPerReader: 3), clock);
        var projection = new SessionLogProjection();
        var desktop = CurrentDesktop(key, clock);
        worker.Offer(desktop);
        var original = await After(worker);
        Assert.NotNull(projection.BuildCurrentLink(key, desktop, original, clock.GetUtcNow()));

        File.AppendAllText(Log, SyntheticClientLogFixtures.Disconnected + string.Concat(Enumerable.Repeat(SyntheticClientLogFixtures.Connected, 3)));
        clock.Advance(1000);
        desktop = CurrentDesktop(key, clock);
        worker.Offer(desktop);
        var skipped = await After(worker, original.Revision);
        Assert.True(Assert.Single(skipped.Sessions).Batch!.HasMore);
        clock.Advance(1000);
        desktop = CurrentDesktop(key, clock);
        worker.Offer(desktop);
        var complete = await After(worker, skipped.Revision);
        var completeBatch = Assert.Single(complete.Sessions).Batch!;
        Assert.False(completeBatch.HasMore);
        Assert.Equal(completeBatch.ReadOffset, completeBatch.CompleteLineOffset);
        Assert.Equal(3, completeBatch.Observations.Count);
        Assert.All(completeBatch.Observations, fact => Assert.Equal(ClientLogFact.Connected, fact.Fact));
        Assert.Null(projection.BuildCurrentLink(key, desktop, complete, clock.GetUtcNow()));
        var current = Assert.Single(projection.EvidenceSnapshot(clock.GetUtcNow()));
        Assert.Null(current.CurrentDestination);
        Assert.Null(current.ObservedUserId);
        Assert.Equal(SyntheticClientLogFixtures.Job, current.LastKnownDestination?.JobId);

        var nextJob = Guid.Parse("12345678-1234-4234-8234-123456789abc");
        const long nextPlace = 110829983956015;
        var joining = SyntheticClientLogFixtures.Line("FLog::Output", $"! Joining game '{nextJob:D}' place {nextPlace} at synthetic-address");
        File.AppendAllText(Log, joining + SyntheticClientLogFixtures.Identity + SyntheticClientLogFixtures.Connected);
        long replacementEnd=new FileInfo(Log).Length;
        clock.Advance(1000);
        desktop = CurrentDesktop(key, clock);
        worker.Offer(desktop);
        // A newer publication alone need not contain the appended replacement. Wait for the reader's
        // completed-byte boundary before asserting what that replacement says about identity/destination.
        var replacement = await After(worker, complete.Revision,snapshot=>snapshot.Sessions.Count==1
            && snapshot.Sessions[0].Batch is {HasMore:false} tail && tail.CompleteLineOffset>=replacementEnd);
        var next = projection.BuildCurrentLink(key, desktop, replacement, clock.GetUtcNow());
        Assert.NotNull(next);
        Assert.Contains($"placeId={nextPlace}", next);
        Assert.Contains(nextJob.ToString("D"), next);
        Assert.DoesNotContain(SyntheticClientLogFixtures.Job.ToString("D"), next);
        var confirmed = Assert.Single(projection.EvidenceSnapshot(clock.GetUtcNow()));
        Assert.Equal(nextPlace, confirmed.CurrentDestination?.PlaceId);
        Assert.Equal(nextJob, confirmed.CurrentDestination?.JobId);
        Assert.Equal(SyntheticClientLogFixtures.User, confirmed.ObservedUserId);
    }
}
