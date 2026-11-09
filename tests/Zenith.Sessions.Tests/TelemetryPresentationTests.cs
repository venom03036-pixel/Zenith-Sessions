using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class TelemetryPresentationTests
{
    private static readonly DateTimeOffset Origin = new(2026, 11, 5, 18, 0, 0, TimeSpan.Zero);
    private static SessionObservation Row(DateTimeOffset at, int pid = 1, long? start = null) => new(pid,
        start ?? Origin.AddHours(-1).UtcTicks, "Synthetic", 0, "", false, true,
        (long)CapacityPolicy.GiB, (long)CapacityPolicy.GiB * 2, 25, at, PageFaultsPerSecond: 3);
    private static SessionProcessKey Key(SessionObservation row) => new(null, row.ProcessId, row.ProcessStartUtcTicks);
    private static DesktopSnapshot Frame(DateTimeOffset at, params SessionObservation[] rows) => new(
        new(at, 16 * CapacityPolicy.GiB, 10 * CapacityPolicy.GiB, 32 * CapacityPolicy.GiB, 8 * CapacityPolicy.GiB, 40), rows,
        ProcessObservedUtc: at, ProcessRevision: at.UtcTicks, ProcessInventoryComplete: true);
    private static ProcessIoRates Io(DateTimeOffset at) => new(at, 1024, 2048, 4096, 3, 4, 5);

    [Fact]
    public void ColdDisplayHasNoInventedPointsOrNumericClientValues()
    {
        var display = new TelemetryDisplay(new());
        display.Update(null, Origin);
        foreach (var points in Series(display)) Assert.Empty(points);
        Assert.Equal("Unavailable", display.ClientCpuValue); Assert.Equal("Unavailable", display.ClientWorkingValue);
        Assert.Equal("Unavailable", display.ClientCommitValue); Assert.Equal("Unavailable", display.ReadValue);
        Assert.Equal("Unavailable", display.WriteValue); Assert.Equal("Unavailable", display.OtherValue);
        Assert.Contains("Waiting", display.SystemStatus); Assert.Contains("Select", display.ClientStatus);
        Assert.True(double.IsFinite(display.PhysicalCeiling) && display.PhysicalCeiling > 0);
        Assert.True(double.IsFinite(display.CommitCeiling) && display.CommitCeiling > 0);
        Assert.True(double.IsFinite(display.ClientMemoryCeiling) && display.ClientMemoryCeiling > 0);
        Assert.True(double.IsFinite(display.IoCeiling) && display.IoCeiling > 0);
    }

    [Fact]
    public void DisplaySeparatesAvailablePhysicalCommitAndClientScalesWithHonestUnits()
    {
        var history = new TelemetryHistory(); var row = Row(Origin) with { IoRates = Io(Origin) };
        history.Observe(Frame(Origin, row), Origin);
        var display = new TelemetryDisplay(history); display.Update(Key(row), Origin);
        Assert.Equal(10, Assert.Single(display.PhysicalPoints).Value);
        Assert.Equal(8, Assert.Single(display.CommitPoints).Value);
        Assert.Equal(40, Assert.Single(display.CpuPoints).Value);
        Assert.Equal(25, Assert.Single(display.ClientCpuPoints).Value);
        Assert.Equal(1, Assert.Single(display.ClientWorkingPoints).Value);
        Assert.Equal(2, Assert.Single(display.ClientCommitPoints).Value);
        Assert.Equal(1, Assert.Single(display.ReadPoints).Value);
        Assert.Equal(2, Assert.Single(display.WritePoints).Value);
        Assert.Equal(4, Assert.Single(display.OtherPoints).Value);
        Assert.Equal(16, display.PhysicalCeiling); Assert.Equal(32, display.CommitCeiling);
        Assert.Equal(2, display.ClientMemoryCeiling); Assert.Equal(4, display.IoCeiling);
        Assert.Contains("available RAM", display.PhysicalRange); Assert.Contains("system commitment", display.CommitRange);
        Assert.Contains("GiB", display.ClientWorkingValue); Assert.Contains("KiB/s", display.ReadValue);
        Assert.Contains("soft + hard combined", display.ClientFaults);
        Assert.Contains("account unverified", display.ClientStatus);
    }

    [Fact]
    public void UnknownMetricsRemainUnavailableWhileActualMeasuredZeroIsPreserved()
    {
        var history = new TelemetryHistory(); var row = Row(Origin) with
        { CpuPercent = null, WorkingSetBytes = 0, PrivateCommitBytes = 0, PageFaultsPerSecond = null, IoRates = new(Origin, null, 0, null, 0, null, null) };
        history.Observe(Frame(Origin, row) with { Resources = Frame(Origin).Resources with { CpuPercent = null } }, Origin);
        var display = new TelemetryDisplay(history); display.Update(Key(row), Origin);
        Assert.Null(Assert.Single(display.CpuPoints).Value); Assert.Null(Assert.Single(display.ClientCpuPoints).Value);
        Assert.Equal("Unavailable", display.ClientCpuValue); Assert.Equal("Unavailable", display.ReadValue);
        Assert.Equal("Unavailable", display.OtherValue); Assert.Null(Assert.Single(display.ReadPoints).Value);
        Assert.Equal(0, Assert.Single(display.ClientWorkingPoints).Value); Assert.Equal(0, Assert.Single(display.WritePoints).Value);
        Assert.NotEqual("Unavailable", display.ClientWorkingValue); Assert.NotEqual("Unavailable", display.WriteValue);
        Assert.Contains("Unavailable", display.ClientFaults); Assert.Contains("Unavailable", display.ClientOperations);
    }

    [Fact]
    public void CachedUpdatesPreserveAllSeriesReferencesAndOriginalTimesWhileStatusAges()
    {
        var history = new TelemetryHistory(); var row = Row(Origin) with { IoRates = Io(Origin) };
        var frame = Frame(Origin, row); history.Observe(frame, Origin);
        var display = new TelemetryDisplay(history); display.Update(Key(row), Origin);
        var first = Series(display);
        Assert.False(history.Observe(frame, Origin.AddSeconds(1)));
        display.Update(Key(row), Origin.AddSeconds(1));
        var cached = Series(display);
        for (int index = 0; index < first.Length; index++)
        { Assert.Same(first[index], cached[index]); Assert.Equal(Origin, Assert.Single(cached[index]).ObservedUtc); }
        display.Update(Key(row), Origin.AddSeconds(4));
        Assert.Contains("last-known/unavailable", display.SystemStatus);
        Assert.Contains("measured", display.ClientStatus);
        display.Update(Key(row), Origin.AddSeconds(11));
        Assert.Contains("last-known/unavailable", display.ClientStatus);
        Assert.Same(first[3], display.ClientCpuPoints); Assert.Same(first[6], display.ReadPoints);
        Assert.Equal(Origin, display.ClientCpuPoints[0].ObservedUtc);
    }

    [Fact]
    public void SwitchingProcessesOrReusingAPidCannotInheritAnotherInstanceHistory()
    {
        var history = new TelemetryHistory(); var first = Row(Origin); var second = Row(Origin, 2) with { CpuPercent = 60 };
        history.Observe(Frame(Origin, first, second), Origin);
        var display = new TelemetryDisplay(history); display.Update(Key(first), Origin);
        var firstSeries = display.ClientCpuPoints;
        display.Update(Key(second), Origin);
        Assert.Equal(60, Assert.Single(display.ClientCpuPoints).Value); Assert.NotSame(firstSeries, display.ClientCpuPoints);
        var reused = first with { ProcessStartUtcTicks = first.ProcessStartUtcTicks + 1 };
        display.Update(Key(reused), Origin);
        Assert.Empty(display.ClientCpuPoints); Assert.Empty(display.ReadPoints); Assert.Equal("Unavailable", display.ClientCpuValue);
        Assert.Contains("no accepted measurements", display.ClientStatus);
        var at = Origin.AddSeconds(1); reused = reused with { ObservedUtc = at, CpuPercent = 5 };
        history.Observe(Frame(at, reused), at); display.Update(Key(reused), at);
        Assert.Equal(5, Assert.Single(display.ClientCpuPoints).Value);
        Assert.Equal(25, Assert.Single(firstSeries).Value);
        display.Update(null, at); Assert.Empty(display.ClientCpuPoints); Assert.Equal("Unavailable", display.ClientWorkingValue);
    }

    [Fact]
    public void EndedClientKeepsItsSeriesAsLastKnownWithoutClaimingACauseOrACurrentRate()
    {
        var history = new TelemetryHistory(); var row = Row(Origin) with { IoRates = Io(Origin) };
        history.Observe(Frame(Origin, row), Origin);
        var display = new TelemetryDisplay(history); display.Update(Key(row), Origin);
        var live = Series(display);
        Assert.Contains("measured", display.ClientStatus); Assert.NotEqual("Unavailable", display.ReadValue);
        // One second later the sample is still within its lease, but the process is no longer observed.
        display.Update(Key(row), Origin.AddSeconds(1), ended: true);
        var ended = Series(display);
        for (int index = 0; index < live.Length; index++) Assert.Same(live[index], ended[index]);
        Assert.Equal(25, Assert.Single(display.ClientCpuPoints).Value);
        Assert.Contains("no longer observed", display.ClientStatus); Assert.Contains("cause unknown", display.ClientStatus);
        Assert.Contains("last-known", display.ClientStatus); Assert.Contains("PID 1", display.ClientStatus);
        Assert.DoesNotContain("· measured", display.ClientStatus); Assert.DoesNotContain("crash", display.ClientStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exit", display.ClientStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("last-known", display.ClientIoStatus); Assert.DoesNotContain("· measured", display.ClientIoStatus);
        Assert.Equal("Unavailable", display.ReadValue); Assert.Equal("Unavailable", display.WriteValue); Assert.Equal("Unavailable", display.OtherValue);
        Assert.Contains("Unavailable", display.ClientOperations);
    }

    [Fact]
    public void IoChartsKeepTheirActualObservationTimeAndNeverRepeatAnOldRateInNewRows()
    {
        var history = new TelemetryHistory(); var ioAt = Origin.AddMilliseconds(-100);
        var row = Row(Origin) with { IoRates = Io(ioAt) }; history.Observe(Frame(Origin, row), Origin);
        var display = new TelemetryDisplay(history); display.Update(Key(row), Origin);
        Assert.Equal(ioAt, Assert.Single(display.ReadPoints).ObservedUtc);
        Assert.Equal(Origin, Assert.Single(display.ClientCpuPoints).ObservedUtc);
        var next = Origin.AddSeconds(1); history.Observe(Frame(next, row with { ObservedUtc = next }), next);
        display.Update(Key(row), next);
        Assert.Equal(ioAt, display.ReadPoints[0].ObservedUtc); Assert.Equal(1, display.ReadPoints[0].Value);
        Assert.Equal(next, display.ReadPoints[1].ObservedUtc); Assert.Null(display.ReadPoints[1].Value);
        Assert.Equal("Unavailable", display.ReadValue); Assert.Equal("Unavailable", display.WriteValue);
        Assert.Equal("Unavailable", display.OtherValue);
        Assert.Single(Geometry(display.ReadPoints, 120, 40)); // One isolated old value; the unknown row creates no connecting line.
    }

    [Fact]
    public void IndependentlyStaleIoHeadlinesBecomeUnavailableWhileHistoricalChartPointsStayUnchanged()
    {
        var history = new TelemetryHistory(); var ioAt = Origin.AddMilliseconds(-9500);
        var row = Row(Origin) with { IoRates = Io(ioAt) }; history.Observe(Frame(Origin, row), Origin);
        var display = new TelemetryDisplay(history); display.Update(Key(row), Origin);
        Assert.NotEqual("Unavailable", display.ReadValue); Assert.NotEqual("Unavailable", display.WriteValue);
        Assert.NotEqual("Unavailable", display.OtherValue);
        var read = display.ReadPoints; var write = display.WritePoints; var other = display.OtherPoints;
        display.Update(Key(row), Origin.AddSeconds(1)); // Fresh client row; independently aged I/O exceeds its lease.
        Assert.Contains("measured", display.ClientStatus);
        Assert.Equal("Unavailable", display.ReadValue); Assert.Equal("Unavailable", display.WriteValue);
        Assert.Equal("Unavailable", display.OtherValue); Assert.Contains("last-known/unavailable", display.ClientIoStatus);
        Assert.Same(read, display.ReadPoints); Assert.Same(write, display.WritePoints); Assert.Same(other, display.OtherPoints);
        Assert.Equal(ioAt, Assert.Single(display.ReadPoints).ObservedUtc); Assert.Equal(1, display.ReadPoints[0].Value);
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(double.NegativeInfinity)] [InlineData(-1)]
    public void InvalidNumericSourcesBecomeGapsInsteadOfZerosOrFormattedNaN(double invalid)
    {
        var history = new TelemetryHistory(); var row = Row(Origin) with { CpuPercent = invalid };
        history.Observe(Frame(Origin, row) with { Resources = Frame(Origin).Resources with { CpuPercent = invalid } }, Origin);
        var display = new TelemetryDisplay(history); display.Update(Key(row), Origin);
        Assert.Null(Assert.Single(display.ClientCpuPoints).Value); Assert.True(display.ClientCpuPoints[0].IsGap);
        Assert.Null(Assert.Single(display.PhysicalPoints).Value); Assert.True(display.PhysicalPoints[0].IsGap);
        Assert.Equal("Unavailable", display.ClientCpuValue); Assert.Equal("Unavailable", display.ClientWorkingValue);
        Assert.Contains("last-known/unavailable", display.ClientStatus); Assert.Empty(Geometry(display.ClientCpuPoints, 120, 40, 100));
    }

    [Fact]
    public void InvalidIoRatesAreIndependentlyUnavailableWithoutDiscardingOtherClientMeasurements()
    {
        var history = new TelemetryHistory(); var row = Row(Origin) with
        { IoRates = new(Origin, double.NaN, -1, double.PositiveInfinity, double.NegativeInfinity, 0, null) };
        history.Observe(Frame(Origin, row), Origin); var display = new TelemetryDisplay(history); display.Update(Key(row), Origin);
        Assert.Equal("Unavailable", display.ReadValue); Assert.Equal("Unavailable", display.WriteValue); Assert.Equal("Unavailable", display.OtherValue);
        Assert.Null(Assert.Single(display.ReadPoints).Value); Assert.Equal(25, Assert.Single(display.ClientCpuPoints).Value);
        Assert.NotEqual("Unavailable", display.ClientWorkingValue); Assert.Contains("Unavailable", display.ClientOperations);
    }

    [Fact]
    public void GeometryReadsOnlyTheLast120PointsAndClipsValuesOlderThanTwoMinutes()
    {
        var source = new BoundedPoints(); var geometry = Geometry(source, 120, 40, 100);
        Assert.Equal(120, geometry.Sum(segment => segment.Count)); Assert.InRange(source.Reads, 120, 240);
        Assert.All(geometry.SelectMany(segment => segment), point => { Assert.InRange(point.X, 1, 121); Assert.InRange(point.Y, 1, 41); });
        var clipped = Geometry([new(Origin.AddMinutes(-3), 10), new(Origin.AddSeconds(-1), 20), new(Origin, 30)], 120, 40, 100);
        Assert.Equal(2, clipped.Sum(segment => segment.Count));
        Assert.All(clipped.SelectMany(segment => segment), point => Assert.True(point.X > 119));
    }

    [Fact]
    public void GeometryBreaksAtUnknownInvalidBackwardAndLongGapMeasurements()
    {
        ResourceChartPoint[] points = [new(Origin, 10), new(Origin.AddSeconds(1), 20), new(Origin.AddSeconds(2), null),
            new(Origin.AddSeconds(3), 30), new(Origin.AddSeconds(4), double.NaN), new(Origin.AddSeconds(5), 40),
            new(Origin.AddSeconds(4), 50), new(Origin.AddSeconds(6), 60), new(Origin.AddSeconds(7), double.PositiveInfinity),
            new(Origin.AddSeconds(8), -1), new(Origin.AddSeconds(9), 80, true), new(Origin.AddSeconds(10), 90), new(Origin.AddSeconds(30), 100)];
        var paths = Geometry(points, 120, 40, 100);
        Assert.Equal(6, paths.Count); Assert.Equal(2, paths[0].Count);
        Assert.All(paths.Skip(1), segment => Assert.Single(segment));
        Assert.All(paths.SelectMany(segment => segment), point => { Assert.True(double.IsFinite(point.X)); Assert.True(double.IsFinite(point.Y)); });
    }

    [Fact]
    public void PercentScaleKeepsZeroAnd100AtOppositeEdgesAndPreservesIsolatedDots()
    {
        var paths = Geometry([new(Origin.AddSeconds(-1), 0), new(Origin, 100)], 120, 40, 100);
        var line = Assert.Single(paths); Assert.Equal(2, line.Count);
        Assert.Equal(41, line[0].Y, 8); Assert.Equal(1, line[1].Y, 8);
        var dot = Assert.Single(Geometry([new(Origin, 0)], 120, 40, 100));
        Assert.Single(dot); Assert.Equal(41, dot[0].Y, 8);
        Assert.Empty(Geometry([], 120, 40)); Assert.Empty(Geometry(null, 120, 40));
        Assert.Empty(Geometry([new(Origin, 1)], double.NaN, 40));
        Assert.Empty(Geometry([new(Origin, 1)], 120, 0));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task ActualSparklineElementDrawsSyntheticPathsOrAnIsolatedDotWithoutOpeningAWindow(bool isolated) => OnSta(() =>
    {
        var points = isolated ? new[] { new ResourceChartPoint(Origin, 50) }
            : Enumerable.Range(0, 13).Select(index => new ResourceChartPoint(Origin.AddSeconds((index - 12) * 10), index * 100d / 12)).ToArray();
        var element = new ResourceSparkline { Width = 120, Height = 48, Points = points, Ceiling = 100,
            GridBrush = Brushes.Transparent, LineBrush = Brushes.Red };
        element.Measure(new(120, 48)); element.Arrange(new Rect(0, 0, 120, 48)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(120, 48, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        byte[] pixels = new byte[120 * 48 * 4]; bitmap.CopyPixels(pixels, 120 * 4, 0);
        int red = 0, middle = 0;
        for (int pixel = 0; pixel < pixels.Length; pixel += 4)
            if (pixels[pixel + 3] > 0 && pixels[pixel + 2] > pixels[pixel + 1] && pixels[pixel + 2] > pixels[pixel])
            { red++; if ((pixel / 4) % 120 is >= 40 and <= 80) middle++; }
        Assert.True(red > 0, "The actual element did not render any series pixels.");
        if (!isolated) Assert.True(middle > 0, "No connected path crossed the middle of the element.");
    });

    private static IReadOnlyList<ResourceChartPoint>[] Series(TelemetryDisplay display) => [display.PhysicalPoints, display.CommitPoints,
        display.CpuPoints, display.ClientCpuPoints, display.ClientWorkingPoints, display.ClientCommitPoints, display.ReadPoints, display.WritePoints, display.OtherPoints];
    private static IReadOnlyList<IReadOnlyList<Point>> Geometry(IReadOnlyList<ResourceChartPoint>? points, double width, double height, double ceiling = double.NaN)
    {
        // Exercise the internal pure layout boundary without adding a production test hook or creating a window.
        var method = typeof(ResourceSparkline).GetMethod("BuildSegments", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (IReadOnlyList<IReadOnlyList<Point>>)method.Invoke(null, [points, width, height, ceiling])!;
    }
    private sealed class BoundedPoints : IReadOnlyList<ResourceChartPoint>
    {
        public int Reads; public int Count => 10000;
        public ResourceChartPoint this[int index]
        {
            get
            {
                Assert.InRange(index, Count - 120, Count - 1); Reads++;
                return new(Origin.AddSeconds(index - (Count - 1)), index % 101);
            }
        }
        public IEnumerator<ResourceChartPoint> GetEnumerator() => throw new InvalidOperationException("The chart must inspect its bounded suffix, not enumerate the source.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private static Task OnSta(Action work)
        => PackagedLayoutCheck.RunOnStaAsync(() => new object(), _ => { work(); return Task.CompletedTask; },
            _ => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
}
