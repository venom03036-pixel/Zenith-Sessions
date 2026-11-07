using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class ProcessIoRateTests
{
    private static SessionProcessKey Key(int pid = 1, long start = 100, string? host = null) => new(host, pid, start);
    private static ProcessIoCounters Counters(ulong value = 100) => new(value, value, value, value, value, value);
    private static double?[] Values(ProcessIoRates rates) => [rates.ReadBytesPerSecond, rates.WriteBytesPerSecond,
        rates.OtherBytesPerSecond, rates.ReadOperationsPerSecond, rates.WriteOperationsPerSecond, rates.OtherOperationsPerSecond];
    private static void Unknown(ProcessIoRates rates) => Assert.All(Values(rates), value => Assert.Null(value));

    [Fact]
    public void FirstObservationIsUnknownAndSixIndependentDeltasUseTheActualObservationTime()
    {
        var clock = new Clock(); var tracker = new ProcessIoRateTracker(clock);
        var first = tracker.Observe(Key(), new(100, 200, 300, 10, 20, 30));
        Unknown(first); Assert.Equal(clock.GetUtcNow(), first.ObservedUtc);
        clock.Advance(TimeSpan.FromSeconds(2));
        var rates = tracker.Observe(Key(), new(300, 700, 900, 20, 50, 70));
        Assert.Equal(new double?[] { 100, 250, 300, 5, 15, 20 }, Values(rates));
        Assert.Equal(clock.GetUtcNow(), rates.ObservedUtc);
        Assert.Equal(first.ObservedUtc.AddSeconds(2), rates.ObservedUtc);
        Assert.Equal(1, tracker.TrackedProcessCount);
    }

    [Fact]
    public void SuccessfulUnchangedCountersAreMeasuredZeroAndFailedAccessReturnsToUnknown()
    {
        var clock = new Clock(); var tracker = new ProcessIoRateTracker(clock); var counters = Counters(0);
        tracker.Observe(Key(), counters); clock.Advance(TimeSpan.FromSeconds(1));
        Assert.All(Values(tracker.Observe(Key(), counters)), value => Assert.Equal(0, value));
        tracker.Unavailable(Key()); Assert.Equal(0, tracker.TrackedProcessCount);
        clock.Advance(TimeSpan.FromSeconds(1)); Unknown(tracker.Observe(Key(), counters));
        tracker.Clear(); Assert.Equal(0, tracker.TrackedProcessCount);
        Unknown(tracker.Observe(Key(), counters));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void ADecreasedCounterIsUnknownWithoutDiscardingOtherValidCounters(int reset)
    {
        var clock = new Clock(); var tracker = new ProcessIoRateTracker(clock);
        tracker.Observe(Key(), Counters()); clock.Advance(TimeSpan.FromSeconds(1));
        ulong[] next = [200, 200, 200, 200, 200, 200]; next[reset] = 1;
        var rates = tracker.Observe(Key(), new(next[0], next[1], next[2], next[3], next[4], next[5]));
        for (int index = 0; index < 6; index++)
            if (index == reset) Assert.Null(Values(rates)[index]); else Assert.Equal(100, Values(rates)[index]);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.All(Values(tracker.Observe(Key(), new(next[0] + 10, next[1] + 10, next[2] + 10,
            next[3] + 10, next[4] + 10, next[5] + 10))), value => Assert.Equal(10, value));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(99)] [InlineData(-1)] [InlineData(15001)]
    public void TinyBackwardAndStaleIntervalsAreUnknownAndEstablishANewBaseline(int milliseconds)
    {
        var clock = new Clock(); var tracker = new ProcessIoRateTracker(clock);
        tracker.Observe(Key(), Counters()); clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
        Unknown(tracker.Observe(Key(), Counters(200)));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.All(Values(tracker.Observe(Key(), Counters(210))), value => Assert.Equal(10, value));
    }

    [Theory]
    [InlineData(100, 1000)]
    [InlineData(5000, 20)]
    [InlineData(15000, 100.0 / 15)]
    public void DocumentedIntervalBoundariesSupportTheExistingProcessCadences(int milliseconds, double expected)
    {
        var clock = new Clock(); var tracker = new ProcessIoRateTracker(clock);
        tracker.Observe(Key(), Counters()); clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
        Assert.All(Values(tracker.Observe(Key(), Counters(200))), value => Assert.Equal(expected, value!.Value, 8));
    }

    [Theory]
    [InlineData(-12)] [InlineData(12)]
    public void WallClockChangesDoNotChangeMonotonicRatesOrInventObservationTimes(int hours)
    {
        var clock = new Clock(); var tracker = new ProcessIoRateTracker(clock);
        var original = tracker.Observe(Key(), Counters());
        clock.Advance(TimeSpan.FromSeconds(1)); clock.ShiftWall(TimeSpan.FromHours(hours));
        var rates = tracker.Observe(Key(), Counters(200));
        Assert.All(Values(rates), value => Assert.Equal(100, value));
        Assert.Equal(original.ObservedUtc.AddSeconds(1).AddHours(hours), rates.ObservedUtc);
    }

    [Fact]
    public void UnsignedCountersAreSubtractedBeforeFloatingPointConversionAndNeverWrapToNegativeRates()
    {
        var clock = new Clock(); var tracker = new ProcessIoRateTracker(clock);
        const ulong large = 1UL << 63;
        tracker.Observe(Key(), Counters(large)); clock.Advance(TimeSpan.FromSeconds(1));
        Assert.All(Values(tracker.Observe(Key(), Counters(large + 1))), value => Assert.Equal(1, value));
        tracker.Clear(); tracker.Observe(Key(), Counters(0)); clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.All(Values(tracker.Observe(Key(), Counters(ulong.MaxValue))), value =>
        { Assert.True(value is { } number && double.IsFinite(number) && number > 0); });
        clock.Advance(TimeSpan.FromSeconds(1)); Unknown(tracker.Observe(Key(), Counters(0)));
    }

    [Fact]
    public void ProcessStartReuseInvalidatesTheOldPidBaselineWhileHostsRemainIndependent()
    {
        var clock = new Clock(); var tracker = new ProcessIoRateTracker(clock);
        tracker.Observe(Key(), Counters()); clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(100, tracker.Observe(Key(), Counters(200)).ReadBytesPerSecond);
        Unknown(tracker.Observe(Key(start: 101), Counters(300))); Assert.Equal(1, tracker.TrackedProcessCount);
        clock.Advance(TimeSpan.FromSeconds(1));
        Unknown(tracker.Observe(Key(), Counters(400))); // Delayed old key cannot resurrect its old baseline.
        Unknown(tracker.Observe(Key(host: "fixture-host"), Counters(400)));
        Assert.Equal(2, tracker.TrackedProcessCount);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(100, tracker.Observe(Key(), Counters(500)).ReadBytesPerSecond);
    }

    [Fact]
    public void RetentionAndLruEvictionKeepAtMost128BaselinesAndDropMissingProcesses()
    {
        var clock = new Clock(); var tracker = new ProcessIoRateTracker(clock); var counters = Counters();
        for (int pid = 1; pid <= 10000; pid++)
        { tracker.Observe(Key(pid), counters); Assert.InRange(tracker.TrackedProcessCount, 1, ProcessIoRateTracker.MaximumTrackedProcesses); }
        Assert.Equal(128, tracker.TrackedProcessCount);
        tracker.Clear();
        for (int pid = 1; pid <= 128; pid++) tracker.Observe(Key(pid), counters);
        clock.Advance(TimeSpan.FromSeconds(1)); tracker.Observe(Key(), counters); // Touch oldest key.
        tracker.Observe(Key(129), counters); clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, tracker.Observe(Key(), counters).ReadBytesPerSecond);
        Unknown(tracker.Observe(Key(2), counters));
        tracker.Retain([Key()]); Assert.Equal(1, tracker.TrackedProcessCount);
        tracker.Retain([]); Assert.Equal(0, tracker.TrackedProcessCount);
    }

    [Fact]
    public void RetainInspectsAtMost128KeysEvenWhenTheEnumerableNeverCompletes()
    {
        var tracker = new ProcessIoRateTracker(new Clock()); tracker.Observe(Key(), Counters()); tracker.Observe(Key(2), Counters());
        int moves = 0; bool disposed = false;
        IEnumerable<SessionProcessKey> Infinite()
        {
            try
            {
                while (true)
                { if (++moves > 128) throw new InvalidOperationException("Retention exceeded its enumeration budget."); yield return Key(); }
            }
            finally { disposed = true; }
        }
        tracker.Retain(Infinite());
        Assert.Equal(128, moves); Assert.True(disposed); Assert.Equal(1, tracker.TrackedProcessCount);
    }

    [Fact]
    public void CancelledObservationsAndRetentionDoNotPublishOrReplaceBaselines()
    {
        var clock = new Clock(); var tracker = new ProcessIoRateTracker(clock); tracker.Observe(Key(), Counters());
        clock.Advance(TimeSpan.FromSeconds(1));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => tracker.Observe(Key(), Counters(1000), canceled.Token));
        Assert.Throws<OperationCanceledException>(() => tracker.Retain([], canceled.Token));
        Assert.Equal(1, tracker.TrackedProcessCount);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(100, tracker.Observe(Key(), Counters(300)).ReadBytesPerSecond);
        using var during = new CancellationTokenSource(); clock.OnUtcRead = during.Cancel;
        Assert.Throws<OperationCanceledException>(() => tracker.Observe(Key(), Counters(1000), during.Token));
        clock.OnUtcRead = null; clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(100, tracker.Observe(Key(), Counters(400)).ReadBytesPerSecond);
    }

    [Fact]
    public void InvalidIdentityAndNullCounterInputCannotCreateBaselines()
    {
        var tracker = new ProcessIoRateTracker(new Clock());
        foreach (var key in new[] { Key(0), Key(start: 0), Key(start: long.MaxValue), Key(host: "") })
            Assert.Throws<ArgumentException>(() => tracker.Observe(key, Counters()));
        Assert.Throws<ArgumentNullException>(() => tracker.Observe(Key(), null!));
        Assert.Equal(0, tracker.TrackedProcessCount);
    }

    [Fact]
    public void ResourceOnlyRefreshNeverRenewsCachedIoEvidenceAndLegacyRowsHaveNoInventedIo()
    {
        var clock = new Clock(); var tracker = new ProcessIoRateTracker(clock); var cadence = new DesktopSamplingCadence(clock);
        var legacy = new SessionObservation(1, 100, "Synthetic", 0, "", false, false, 1, 1, null, clock.GetUtcNow());
        Assert.Null(legacy.IoRates);
        ResourceSnapshot System(CancellationToken _) => new(clock.GetUtcNow(), 1, 1, 1, 0, 0);
        IReadOnlyList<SessionObservation> Processes(CancellationToken _) => [legacy with
            { ObservedUtc = clock.GetUtcNow(), IoRates = tracker.Observe(Key(), Counters()) }];
        var first = cadence.Sample(5000, System, Processes, default); var io = first.Sessions[0].IoRates!;
        clock.Advance(TimeSpan.FromSeconds(1));
        var cached = cadence.Sample(5000, System, Processes, default);
        Assert.Same(first.Sessions, cached.Sessions); Assert.NotSame(first.Resources, cached.Resources);
        Assert.Same(io, cached.Sessions[0].IoRates); Assert.Equal(io.ObservedUtc, cached.Sessions[0].IoRates!.ObservedUtc);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 11, 5, 18, 0, 0, TimeSpan.Zero); private long _ticks;
        public Action? OnUtcRead;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() { OnUtcRead?.Invoke(); return _utc; }
        public void Advance(TimeSpan elapsed) { _ticks += elapsed.Ticks; _utc += elapsed; }
        public void ShiftWall(TimeSpan difference) => _utc += difference;
    }
}
