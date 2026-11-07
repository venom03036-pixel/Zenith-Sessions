using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class TelemetryHistoryTests
{
    private static readonly DateTimeOffset Origin = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private static SessionObservation Row(DateTimeOffset at, int pid = 123, long? start = null) =>
        new(pid, start ?? Origin.AddHours(-1).UtcTicks, "Synthetic", 0, "", false, true, 1024, 2048, 25, at,
            PageFaultsPerSecond: 3);
    private static SessionProcessKey Key(SessionObservation row) => new(null, row.ProcessId, row.ProcessStartUtcTicks);
    private static ResourceSnapshot Resources(DateTimeOffset at) => new(at, 10000, 6000, 20000, 7000, 35);
    private static DesktopSnapshot Frame(DateTimeOffset at, params SessionObservation[] rows) =>
        new(Resources(at), rows, ProcessObservedUtc: at, ProcessRevision: at.UtcTicks, ProcessInventoryComplete: true);
    private static ProcessIoRates Io(DateTimeOffset at, double? value = 7) => new(at, value, value, value, value, value, value);

    [Fact]
    public void EmptyAndCachedMeasurementsKeepStableSnapshotReferencesAndOriginalTimes()
    {
        var history = new TelemetryHistory(); var initial = history.GetSystemSnapshot(); var row = Row(Origin);
        Assert.Same(initial, history.GetSystemSnapshot()); Assert.Null(history.GetClientSnapshot(Key(row)));
        var frame = Frame(Origin, row); Assert.True(history.Observe(frame, Origin));
        var system = history.GetSystemSnapshot(); var client = history.GetClientSnapshot(Key(row));
        Assert.False(history.Observe(frame, Origin.AddSeconds(1)));
        Assert.Same(system, history.GetSystemSnapshot()); Assert.Same(client, history.GetClientSnapshot(Key(row)));
        Assert.Equal(Origin, Assert.Single(system.Points).ObservedUtc);
        Assert.Equal(Origin, Assert.Single(client!.Points).ObservedUtc);
        Assert.False(history.Observe(frame, Origin.AddMinutes(20)));
        Assert.Same(system, history.GetSystemSnapshot()); Assert.Same(client, history.GetClientSnapshot(Key(row)));
    }

    [Fact]
    public void ResourceAndProcessHistoriesAdvanceIndependently()
    {
        var history = new TelemetryHistory(); var row = Row(Origin); history.Observe(Frame(Origin, row), Origin);
        var system = history.GetSystemSnapshot(); var client = history.GetClientSnapshot(Key(row));
        var next = Origin.AddSeconds(1);
        history.Observe(new(Resources(next), [row], ProcessObservedUtc: Origin, ProcessRevision: Origin.UtcTicks), next);
        Assert.NotSame(system, history.GetSystemSnapshot()); Assert.Same(client, history.GetClientSnapshot(Key(row)));
        system = history.GetSystemSnapshot(); row = row with { ObservedUtc = Origin.AddSeconds(2) };
        history.Observe(Frame(row.ObservedUtc, row) with { Resources = Resources(next) }, row.ObservedUtc);
        Assert.Same(system, history.GetSystemSnapshot()); Assert.NotSame(client, history.GetClientSnapshot(Key(row)));
    }

    [Fact]
    public void SnapshotsAreDetachedReadOnlyAndBuiltLazilyWithoutChangingEarlierViews()
    {
        var history = new TelemetryHistory(); var row = Row(Origin); history.Observe(Frame(Origin, row), Origin);
        var system = history.GetSystemSnapshot(); var client = history.GetClientSnapshot(Key(row))!;
        for (int i = 1; i <= 5; i++) { var at = Origin.AddSeconds(i); history.Observe(Frame(at, row with { ObservedUtc = at }), at); }
        Assert.Single(system.Points); Assert.Single(client.Points);
        var latestSystem = history.GetSystemSnapshot(); var latestClient = history.GetClientSnapshot(Key(row))!;
        Assert.Equal(6, latestSystem.Revision); Assert.Equal(6, latestClient.Revision);
        Assert.Equal(6, latestSystem.Points.Count); Assert.Equal(6, latestClient.Points.Count);
        Assert.Same(latestClient, history.GetClientSnapshot(Key(row)));
        Assert.Throws<NotSupportedException>(() => ((IList<ClientTelemetryPoint>)latestClient.Points).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<SystemTelemetryPoint>)latestSystem.Points).Clear());
    }

    [Fact]
    public void SystemPointUsesPhysicalDifferenceAndSeparateCommitValues()
    {
        var history = new TelemetryHistory(); history.Observe(Frame(Origin), Origin);
        var point = Assert.Single(history.GetSystemSnapshot().Points);
        Assert.Equal(10000UL, point.TotalPhysicalBytes); Assert.Equal(4000UL, point.UsedPhysicalBytes);
        Assert.Equal(7000UL, point.CommitUsedBytes); Assert.Equal(20000UL, point.CommitLimitBytes);
        Assert.Equal(35d, point.CpuPercent); Assert.False(point.IsGap);
    }

    [Fact]
    public void UnknownRatesStayNullWhileActualZeroIsPreserved()
    {
        var history = new TelemetryHistory(); var row = Row(Origin) with { CpuPercent = null, PageFaultsPerSecond = null, WorkingSetBytes = 0 };
        history.Observe(Frame(Origin, row) with { Resources = Resources(Origin) with { CpuPercent = null } }, Origin);
        var point = Assert.Single(history.GetClientSnapshot(Key(row))!.Points);
        Assert.Null(point.CpuPercent); Assert.Null(point.PageFaultsPerSecond); Assert.Null(point.IoRates);
        Assert.Equal(0L, point.WorkingSetBytes); Assert.False(point.IsGap);
        Assert.Null(Assert.Single(history.GetSystemSnapshot().Points).CpuPercent);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void MalformedCpuBecomesAnExplicitGapWithoutPublishingBadValues(double cpu)
    {
        var history = new TelemetryHistory(); var row = Row(Origin) with { CpuPercent = cpu };
        history.Observe(Frame(Origin, row) with { Resources = Resources(Origin) with { CpuPercent = cpu } }, Origin);
        var system = Assert.Single(history.GetSystemSnapshot().Points); var client = Assert.Single(history.GetClientSnapshot(Key(row))!.Points);
        Assert.True(system.IsGap); Assert.Null(system.CpuPercent); Assert.Null(system.UsedPhysicalBytes);
        Assert.True(client.IsGap); Assert.Null(client.CpuPercent); Assert.Null(client.WorkingSetBytes);
    }

    [Theory]
    [InlineData(-1, 2048, 3)]
    [InlineData(1024, -1, 3)]
    [InlineData(1024, 2048, -1)]
    [InlineData(1024, 2048, double.NaN)]
    public void MalformedClientMemoryOrFaultsCannotEnterCharts(long working, long commit, double faults)
    {
        var history = new TelemetryHistory(); var row = Row(Origin) with { WorkingSetBytes = working, PrivateCommitBytes = commit, PageFaultsPerSecond = faults, IoRates = Io(Origin) };
        history.Observe(Frame(Origin, row), Origin); var point = Assert.Single(history.GetClientSnapshot(Key(row))!.Points);
        Assert.True(point.IsGap); Assert.Null(point.PrivateCommitBytes); Assert.Null(point.PageFaultsPerSecond); Assert.Null(point.IoRates);
    }

    [Fact]
    public void FailedSourcesProduceGapsAndCannotRenewPreviousValues()
    {
        var history = new TelemetryHistory(); var row = Row(Origin); history.Observe(Frame(Origin, row), Origin);
        var at = Origin.AddSeconds(1);
        history.Observe(Frame(at, row with { ObservedUtc = at, Problem = "Synthetic unavailable" }) with { Resources = Resources(at) with { Problem = "Synthetic failure" } }, at);
        Assert.True(history.GetSystemSnapshot().Points[^1].IsGap);
        var client = history.GetClientSnapshot(Key(row))!; Assert.True(client.Points[^1].IsGap); Assert.Null(client.Points[^1].CpuPercent);
    }

    [Theory]
    [InlineData(-10001)]
    [InlineData(1)]
    public void StaleOrFutureClientMeasurementsAreRejected(int offsetMs)
    {
        var history = new TelemetryHistory(); var row = Row(Origin.AddMilliseconds(offsetMs));
        history.Observe(Frame(Origin, row), Origin); Assert.Null(history.GetClientSnapshot(Key(row)));
    }

    [Theory]
    [InlineData(-3001)]
    [InlineData(1)]
    public void StaleOrFutureSystemMeasurementsAreRejected(int offsetMs)
    {
        var history = new TelemetryHistory(); var initial = history.GetSystemSnapshot();
        history.Observe(Frame(Origin) with { Resources = Resources(Origin.AddMilliseconds(offsetMs)) }, Origin);
        Assert.Same(initial, history.GetSystemSnapshot()); Assert.Empty(initial.Points);
    }

    [Fact]
    public void OlderMeasurementsAndBackwardClockCannotReplaceCurrentPoints()
    {
        var history = new TelemetryHistory(); var row = Row(Origin); history.Observe(Frame(Origin, row), Origin);
        var system = history.GetSystemSnapshot(); var client = history.GetClientSnapshot(Key(row));
        var old = Origin.AddSeconds(-1);
        Assert.False(history.Observe(Frame(old, row with { ObservedUtc = old }), Origin));
        Assert.False(history.Observe(Frame(Origin, row), old));
        Assert.Same(system, history.GetSystemSnapshot()); Assert.Same(client, history.GetClientSnapshot(Key(row)));
    }

    [Fact]
    public void InventoryRevisionAndCompletionTimeFenceReplayedRows()
    {
        var history = new TelemetryHistory(); var row = Row(Origin); history.Observe(Frame(Origin, row), Origin);
        var client = history.GetClientSnapshot(Key(row)); var at = Origin.AddSeconds(1);
        history.Observe(Frame(at, row with { ObservedUtc = at }) with { ProcessRevision = Origin.UtcTicks }, at);
        Assert.Same(client, history.GetClientSnapshot(Key(row)));
        history.Observe(Frame(at, row with { ObservedUtc = at }) with { ProcessObservedUtc = Origin.AddMilliseconds(-1) }, at);
        Assert.Same(client, history.GetClientSnapshot(Key(row)));
        history.Observe(Frame(at, row with { ObservedUtc = at.AddMilliseconds(1) }), at.AddMilliseconds(1));
        Assert.Same(client, history.GetClientSnapshot(Key(row)));
    }

    [Fact]
    public void IoUsesItsOwnTimestampAndDoesNotRepeatAnOldRateInNewRows()
    {
        var history = new TelemetryHistory(); var ioTime = Origin.AddMilliseconds(-100); var row = Row(Origin) with { IoRates = Io(ioTime) };
        history.Observe(Frame(Origin, row), Origin); var first = Assert.Single(history.GetClientSnapshot(Key(row))!.Points);
        Assert.Equal(ioTime, first.IoRates!.ObservedUtc); Assert.Equal(7d, first.IoRates.ReadBytesPerSecond);
        var at = Origin.AddSeconds(1); history.Observe(Frame(at, row with { ObservedUtc = at }), at);
        Assert.Null(history.GetClientSnapshot(Key(row))!.Points[^1].IoRates);
        at = at.AddSeconds(1); history.Observe(Frame(at, row with { ObservedUtc = at, IoRates = Io(at) }), at);
        Assert.Equal(at, history.GetClientSnapshot(Key(row))!.Points[^1].IoRates!.ObservedUtc);
        Assert.Equal(ioTime, first.IoRates.ObservedUtc);
    }

    [Theory]
    [InlineData(-10001)]
    [InlineData(1)]
    public void IoStalenessAndFutureTimeAreCheckedIndependently(int offsetMs)
    {
        var history = new TelemetryHistory(); var row = Row(Origin) with { IoRates = Io(Origin.AddMilliseconds(offsetMs)) };
        history.Observe(Frame(Origin, row), Origin);
        Assert.Null(Assert.Single(history.GetClientSnapshot(Key(row))!.Points).IoRates);
    }

    [Fact]
    public void IoAfterCompletedInventoryOrBeforeProcessStartIsRejected()
    {
        var history = new TelemetryHistory(); var row = Row(Origin) with { IoRates = Io(Origin.AddMilliseconds(1)) };
        history.Observe(Frame(Origin, row), Origin.AddSeconds(1));
        Assert.Null(Assert.Single(history.GetClientSnapshot(Key(row))!.Points).IoRates);
        var at = Origin.AddSeconds(1); row = Row(at, start: Origin.UtcTicks) with { IoRates = Io(Origin.AddTicks(-1)) };
        history.Observe(Frame(at, row), at); Assert.Null(Assert.Single(history.GetClientSnapshot(Key(row))!.Points).IoRates);
    }

    [Fact]
    public void EveryIoFieldValidatesIndependentlyAndUnknownNeverBecomesZero()
    {
        var history = new TelemetryHistory(); var row = Row(Origin) with { IoRates = new(Origin, double.NaN, double.PositiveInfinity, -1, 0, null, 12.5) };
        history.Observe(Frame(Origin, row), Origin); var io = Assert.Single(history.GetClientSnapshot(Key(row))!.Points).IoRates!;
        Assert.Null(io.ReadBytesPerSecond); Assert.Null(io.WriteBytesPerSecond); Assert.Null(io.OtherBytesPerSecond);
        Assert.Equal(0d, io.ReadOperationsPerSecond); Assert.Null(io.WriteOperationsPerSecond); Assert.Equal(12.5, io.OtherOperationsPerSecond);
    }

    [Fact]
    public void UnknownAndIncompleteInventoryNeverInferAnExitOrClearPriorHistory()
    {
        var history = new TelemetryHistory(); var row = Row(Origin); history.Observe(Frame(Origin, row), Origin);
        var client = history.GetClientSnapshot(Key(row)); var at = Origin.AddSeconds(1);
        history.Observe(Frame(at) with { ProcessInventoryComplete = false }, at); Assert.Same(client, history.GetClientSnapshot(Key(row)));
        at = at.AddSeconds(1); row = row with { ObservedUtc = at };
        history.Observe(Frame(at, row) with { ProcessInventoryComplete = false }, at);
        Assert.Equal(2, history.GetClientSnapshot(Key(row))!.Points.Count);
        at = at.AddSeconds(1); client = history.GetClientSnapshot(Key(row)); history.Observe(Frame(at), at);
        Assert.Same(client, history.GetClientSnapshot(Key(row)));
    }

    [Fact]
    public void PidReuseGetsSeparateHistoryAndRemoteOrInvalidKeysDoNotAliasLocalRows()
    {
        var history = new TelemetryHistory(); var old = Row(Origin); history.Observe(Frame(Origin, old), Origin);
        var at = Origin.AddSeconds(1); var reused = Row(at, start: Origin.UtcTicks); history.Observe(Frame(at, reused), at);
        Assert.Single(history.GetClientSnapshot(Key(old))!.Points); Assert.Single(history.GetClientSnapshot(Key(reused))!.Points);
        Assert.NotSame(history.GetClientSnapshot(Key(old)), history.GetClientSnapshot(Key(reused)));
        Assert.Null(history.GetClientSnapshot(Key(old) with { HostId = "remote" })); Assert.Null(history.GetClientSnapshot(default));
    }

    [Fact]
    public void ConflictingPidRowsAndOversizedOrFailedInventoriesDoNotPublishClients()
    {
        var history = new TelemetryHistory(); var row = Row(Origin); var other = Row(Origin, start: Origin.UtcTicks);
        history.Observe(Frame(Origin, row, other), Origin); Assert.Null(history.GetClientSnapshot(Key(row)));
        var oversized = Enumerable.Range(1, 129).Select(pid => Row(Origin, pid)).ToArray();
        history.Observe(Frame(Origin, oversized), Origin); Assert.Null(history.GetClientSnapshot(Key(oversized[0])));
        history.Observe(Frame(Origin, row) with { Problem = "Synthetic inventory failure" }, Origin);
        Assert.Null(history.GetClientSnapshot(Key(row)));
    }

    [Fact]
    public void MalformedInventoryCannotPoisonTheRevisionFenceForLaterValidRows()
    {
        var history = new TelemetryHistory(); var row = Row(Origin);
        history.Observe(Frame(Origin, row with { ProcessId = 0 }) with { ProcessRevision = long.MaxValue }, Origin);
        history.Observe(Frame(Origin, row), Origin);
        Assert.Single(history.GetClientSnapshot(Key(row))!.Points);
    }

    [Fact]
    public void InvalidPhysicalOrCommitAccountingProducesAnExplicitSystemGap()
    {
        var history = new TelemetryHistory();
        history.Observe(Frame(Origin) with { Resources = Resources(Origin) with { AvailablePhysical = 10001 } }, Origin);
        Assert.True(Assert.Single(history.GetSystemSnapshot().Points).IsGap);
        var at = Origin.AddSeconds(1);
        history.Observe(Frame(at) with { Resources = Resources(at) with { CommitUsed = 20001 } }, at);
        Assert.True(history.GetSystemSnapshot().Points[^1].IsGap);
        Assert.Null(history.GetSystemSnapshot().Points[^1].CommitUsedBytes);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 100)]
    [InlineData(1, 0)]
    public void MalformedProcessIdentityIsRejected(int pid, long start)
    {
        var history = new TelemetryHistory(); var row = Row(Origin, pid, start);
        history.Observe(Frame(Origin, row), Origin); Assert.Null(history.GetClientSnapshot(Key(row)));
    }

    [Fact]
    public void RowCannotPrecedeItsProcessStart()
    {
        var history = new TelemetryHistory(); var row = Row(Origin, start: Origin.AddTicks(1).UtcTicks);
        history.Observe(Frame(Origin, row), Origin); Assert.Null(history.GetClientSnapshot(Key(row)));
    }

    [Fact]
    public void ClientEvictionIsBoundedAndDoesNotTransferAnOlderProcessHistory()
    {
        var history = new TelemetryHistory(); var rows = new List<SessionObservation>();
        for (int i = 0; i < 130; i++)
        {
            var at = Origin.AddSeconds(i); var row = Row(at, i + 1); rows.Add(row); history.Observe(Frame(at, row), at);
        }
        Assert.Null(history.GetClientSnapshot(Key(rows[0]))); Assert.Null(history.GetClientSnapshot(Key(rows[1])));
        Assert.Equal(TelemetryHistory.MaximumClients, rows.Count(row => history.GetClientSnapshot(Key(row)) is not null));
        Assert.Single(history.GetClientSnapshot(Key(rows[^1]))!.Points);
    }

    [Fact]
    public void CountAndTwoMinuteWindowBoundBothSeriesAndLongGapsAreExplicit()
    {
        var history = new TelemetryHistory(); var row = Row(Origin);
        for (int i = 0; i <= 240; i++) { var at = Origin.AddMilliseconds(i * 500); history.Observe(Frame(at, row with { ObservedUtc = at }), at); }
        Assert.Equal(120, history.GetSystemSnapshot().Points.Count); Assert.Equal(120, history.GetClientSnapshot(Key(row))!.Points.Count);
        var late = Origin.AddMinutes(5); history.Observe(Frame(late, row with { ObservedUtc = late }), late);
        Assert.All(history.GetSystemSnapshot().Points, p => Assert.InRange(p.ObservedUtc, late - TelemetryHistory.Window, late));
        Assert.All(history.GetClientSnapshot(Key(row))!.Points, p => Assert.InRange(p.ObservedUtc, late - TelemetryHistory.Window, late));
        var gapHistory = new TelemetryHistory(); gapHistory.Observe(Frame(Origin, row), Origin);
        late = Origin.AddSeconds(15); gapHistory.Observe(Frame(late, row with { ObservedUtc = late }), late);
        Assert.True(gapHistory.GetSystemSnapshot().Points[1].IsGap); Assert.Null(gapHistory.GetSystemSnapshot().Points[1].CpuPercent);
        Assert.True(gapHistory.GetClientSnapshot(Key(row))!.Points[1].IsGap); Assert.Null(gapHistory.GetClientSnapshot(Key(row))!.Points[1].WorkingSetBytes);
    }

    [Fact]
    public async Task GetAndObserveAreSafeAcrossTheSingleSamplerAndPresentationOwners()
    {
        var history = new TelemetryHistory(); var row = Row(Origin);
        var observe = Task.Run(() => { for (int i = 0; i < 200; i++) { var at = Origin.AddMilliseconds(i * 500); history.Observe(Frame(at, row with { ObservedUtc = at }), at); } });
        var read = Task.Run(() => { for (int i = 0; i < 200; i++) { var system = history.GetSystemSnapshot(); Assert.InRange(system.Points.Count, 0, 120); var client = history.GetClientSnapshot(Key(row)); if (client is not null) Assert.InRange(client.Points.Count, 1, 120); } });
        await Task.WhenAll(observe, read); Assert.Equal(200, history.GetSystemSnapshot().Revision);
        Assert.Equal(200, history.GetClientSnapshot(Key(row))!.Revision);
    }
}
