using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;
using System.ComponentModel;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class DesktopSamplingCadenceTests
{
    private sealed class Clock : TimeProvider
    {
        private long _milliseconds;
        private DateTimeOffset _utc = new(2026, 11, 5, 18, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _milliseconds;
        public override DateTimeOffset GetUtcNow() => _utc;
        public void Advance(int milliseconds) { _milliseconds += milliseconds; _utc = _utc.AddMilliseconds(milliseconds); }
        public void ShiftWallClock(TimeSpan difference) => _utc += difference;
    }

    private sealed class Measurements(Clock clock)
    {
        public int SystemReads, ProcessReads;
        public ResourceSnapshot System(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); SystemReads++;
            return new(clock.GetUtcNow(), 16 * CapacityPolicy.GiB, 12 * CapacityPolicy.GiB,
                32 * CapacityPolicy.GiB, 8 * CapacityPolicy.GiB, SystemReads);
        }
        public IReadOnlyList<SessionObservation> Processes(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); ProcessReads++;
            return new[] { Session(clock.GetUtcNow(), ProcessReads) };
        }
        public DesktopProcessInventory Inventory(CancellationToken ct) => new(Processes(ct), Complete: true);
    }

    private static SessionObservation Session(DateTimeOffset at, int generation = 1) => new(42, at.AddMinutes(-1).UtcTicks,
        "Synthetic", 0, "Synthetic", false, false, generation, generation, generation, at);
    private static ManagerPerformancePolicy Policy(int milliseconds) => new ManagerPerformanceSettings(
        Profile: ManagerPerformanceProfile.Custom, CustomProcessSampleMs: milliseconds).Resolve();

    [Fact]
    public void DueProcessInventoryPrecedesSystemMemoryUsingTheirActualObservationTimes()
    {
        var clock = new Clock(); var measured = new Measurements(clock); var cadence = new DesktopSamplingCadence(clock);
        var order = new List<string>();
        ResourceSnapshot System(CancellationToken ct) { order.Add("system"); return measured.System(ct); }
        DesktopProcessInventory Processes(CancellationToken ct)
        {
            order.Add("process"); clock.Advance(20);
            var inventory = measured.Inventory(ct);
            clock.Advance(20); return inventory;
        }
        for (int sample = 0; sample < 2; sample++)
        {
            if (sample != 0) clock.Advance(1000);
            order.Clear();
            var current = cadence.SampleInventory(1000, System, Processes, default);
            Assert.Equal(new[] { "process", "system" }, order);
            Assert.True(current.ProcessInventoryComplete);
            Assert.True(current.Sessions[0].ObservedUtc < current.ProcessObservedUtc);
            Assert.True(current.ProcessObservedUtc <= current.Resources.ObservedUtc);
            Assert.Equal(clock.GetUtcNow(), current.Resources.ObservedUtc);
        }
        Assert.Equal(2, measured.ProcessReads); Assert.Equal(2, measured.SystemReads);
    }

    [Fact]
    public void ProcessOnlyRefreshDoesNotRenewSystemEvidenceAndNextDueSystemFollowsProcesses()
    {
        var clock = new Clock(); var measured = new Measurements(clock); var cadence = new DesktopSamplingCadence(clock);
        var order = new List<string>();
        ResourceSnapshot System(CancellationToken ct) { order.Add("system"); return measured.System(ct); }
        DesktopProcessInventory Processes(CancellationToken ct)
        {
            order.Add("process"); clock.Advance(20); return measured.Inventory(ct);
        }
        var first = cadence.SampleInventory(500, System, Processes, default);
        clock.Advance(500); order.Clear();
        var processOnly = cadence.SampleInventory(500, System, Processes, default);
        Assert.Equal(new[] { "process" }, order);
        Assert.Same(first.Resources, processOnly.Resources);
        Assert.Equal(first.Resources.ObservedUtc, processOnly.Resources.ObservedUtc);
        Assert.True(processOnly.ProcessObservedUtc > processOnly.Resources.ObservedUtc);
        clock.Advance(500); order.Clear();
        var paired = cadence.SampleInventory(500, System, Processes, default);
        Assert.Equal(new[] { "process", "system" }, order);
        Assert.True(paired.Resources.ObservedUtc >= paired.ProcessObservedUtc);
        Assert.NotSame(first.Resources, paired.Resources);
        Assert.Equal(2, measured.SystemReads); Assert.Equal(3, measured.ProcessReads);
    }

    [Fact]
    public void FiveHundredMillisecondCallsKeepSystemAtOneHertzAndOnlyProcessesAtTwoHertz()
    {
        var clock = new Clock(); var measured = new Measurements(clock); var cadence = new DesktopSamplingCadence(clock);
        DesktopSnapshot previous = cadence.Sample(Policy(500).ProcessSampleMs, measured.System, measured.Processes, default);
        for (int halfSecond = 1; halfSecond <= 8; halfSecond++)
        {
            clock.Advance(500);
            var current = cadence.Sample(500, measured.System, measured.Processes, default);
            if (halfSecond % 2 == 1)
            {
                Assert.Same(previous.Resources, current.Resources);
                Assert.Equal(previous.Resources.ObservedUtc, current.Resources.ObservedUtc);
            }
            Assert.NotSame(previous.Sessions, current.Sessions);
            previous = current;
        }
        Assert.Equal(5, measured.SystemReads); Assert.Equal(9, measured.ProcessReads);
    }

    [Theory]
    [InlineData(2000, 6)]
    [InlineData(5000, 3)]
    public void SlowerProcessIntervalsRemainIndependentOfEssentialSampling(int processMs, int expectedProcessReads)
    {
        var clock = new Clock(); var measured = new Measurements(clock); var cadence = new DesktopSamplingCadence(clock);
        var initial = cadence.Sample(Policy(processMs).ProcessSampleMs, measured.System, measured.Processes, default);
        var previous = initial;
        for (int elapsed = 500; elapsed <= 10000; elapsed += 500)
        {
            clock.Advance(500);
            var current = cadence.Sample(processMs, measured.System, measured.Processes, default);
            if (elapsed % processMs != 0)
            {
                Assert.Same(previous.Sessions, current.Sessions);
                Assert.Equal(previous.Sessions[0].ObservedUtc, current.Sessions[0].ObservedUtc);
                Assert.Equal(previous.Sessions[0].CpuPercent, current.Sessions[0].CpuPercent);
            }
            previous = current;
        }
        Assert.Equal(11, measured.SystemReads); Assert.Equal(expectedProcessReads, measured.ProcessReads);
        Assert.Equal(initial.Resources.ObservedUtc.AddSeconds(10), previous.Resources.ObservedUtc);
    }

    [Fact]
    public void FasterReconfigurationUsesLastActualMeasurementWithoutResettingSystemCadence()
    {
        var clock = new Clock(); var measured = new Measurements(clock); var cadence = new DesktopSamplingCadence(clock);
        var first = cadence.Sample(5000, measured.System, measured.Processes, default);
        clock.Advance(500);
        var cached = cadence.Sample(5000, measured.System, measured.Processes, default);
        Assert.Same(first.Sessions, cached.Sessions);
        var reconfigured = cadence.Sample(Policy(500).ProcessSampleMs, measured.System, measured.Processes, default);
        Assert.NotSame(cached.Sessions, reconfigured.Sessions); Assert.Same(first.Resources, reconfigured.Resources);
        var duplicate = cadence.Sample(500, measured.System, measured.Processes, default);
        Assert.Same(reconfigured.Sessions, duplicate.Sessions);
        Assert.Equal(1, measured.SystemReads); Assert.Equal(2, measured.ProcessReads);
    }

    [Fact]
    public void SlowerReconfigurationDoesNotResampleOrResetElapsedHistory()
    {
        var clock = new Clock(); var measured = new Measurements(clock); var cadence = new DesktopSamplingCadence(clock);
        cadence.Sample(500, measured.System, measured.Processes, default);
        clock.Advance(500);
        var lastProcessRead = cadence.Sample(500, measured.System, measured.Processes, default);
        for (int elapsed = 1000; elapsed < 5500; elapsed += 500)
        {
            clock.Advance(500);
            var cached = cadence.Sample(Policy(5000).ProcessSampleMs, measured.System, measured.Processes, default);
            Assert.Same(lastProcessRead.Sessions, cached.Sessions);
        }
        clock.Advance(500);
        Assert.NotSame(lastProcessRead.Sessions, cadence.Sample(5000, measured.System, measured.Processes, default).Sessions);
        Assert.Equal(3, measured.ProcessReads); Assert.Equal(6, measured.SystemReads);
    }

    [Fact]
    public void BackwardWallClockDoesNotChangeMonotonicCadenceOrRefreshCachedTimestamps()
    {
        var clock = new Clock(); var measured = new Measurements(clock); var cadence = new DesktopSamplingCadence(clock);
        var first = cadence.Sample(2000, measured.System, measured.Processes, default);
        clock.Advance(500); clock.ShiftWallClock(TimeSpan.FromHours(-2));
        var cached = cadence.Sample(2000, measured.System, measured.Processes, default);
        Assert.Same(first.Resources, cached.Resources); Assert.Same(first.Sessions, cached.Sessions);
        clock.Advance(500);
        var system = cadence.Sample(2000, measured.System, measured.Processes, default);
        Assert.NotSame(first.Resources, system.Resources); Assert.Same(first.Sessions, system.Sessions);
        Assert.True(system.Resources.ObservedUtc < first.Resources.ObservedUtc);
        clock.Advance(1000);
        var processes = cadence.Sample(2000, measured.System, measured.Processes, default);
        Assert.NotSame(first.Sessions, processes.Sessions);
        Assert.Equal(3, measured.SystemReads); Assert.Equal(2, measured.ProcessReads);
    }

    [Fact]
    public void CancellationPreventsCallbacksAndCannotPublishAPartialCanceledMeasurement()
    {
        var clock = new Clock(); var measured = new Measurements(clock); var cadence = new DesktopSamplingCadence(clock);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => cadence.Sample(1000, measured.System, measured.Processes, stop.Token));
        Assert.Equal(0, measured.SystemReads); Assert.Equal(0, measured.ProcessReads);
        using var during = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => cadence.Sample(1000, ct =>
        {
            var result = measured.System(ct); during.Cancel(); return result;
        }, measured.Processes, during.Token));
        // Process sampling now completes first. Cancelling the later system measurement
        // cannot publish a DesktopSnapshot or manufacture a successful system cache entry.
        Assert.Equal(1, measured.SystemReads); Assert.Equal(1, measured.ProcessReads);
        var completed = cadence.Sample(1000, measured.System, measured.Processes, default);
        Assert.Equal(2, measured.SystemReads); Assert.Single(completed.Sessions);
        Assert.Equal(1, measured.ProcessReads);
    }

    [Fact]
    public void ProcessCacheIsBoundedOwnedAndRetainsMeasurementTimestamps()
    {
        var clock = new Clock(); var measured = new Measurements(clock); var cadence = new DesktopSamplingCadence(clock);
        var items = Enumerable.Range(0, 200).Select(index => Session(clock.GetUtcNow(), index)).ToList();
        var first = cadence.Sample(5000, measured.System, _ => items, default);
        Assert.Equal(128, first.Sessions.Count);
        items[0] = Session(clock.GetUtcNow().AddHours(1), 999); items.Clear();
        clock.Advance(1000);
        var cached = cadence.Sample(5000, measured.System, _ => throw new InvalidOperationException("Process sample is not due."), default);
        Assert.Same(first.Sessions, cached.Sessions); Assert.Equal(128, cached.Sessions.Count);
        Assert.Equal(0, cached.Sessions[0].WorkingSetBytes);
        Assert.Equal(first.Resources.ObservedUtc, cached.Sessions[0].ObservedUtc);
        cadence.Clear();
        Assert.Single(cadence.Sample(5000, measured.System, measured.Processes, default).Sessions);
    }

    [Fact]
    public void FailedSystemReadDoesNotBecomeCachedSuccessAndCachedSamplesStillExpireForAdmission()
    {
        var clock = new Clock(); var measured = new Measurements(clock); var cadence = new DesktopSamplingCadence(clock);
        Assert.Throws<InvalidOperationException>(() => cadence.Sample(1000,
            _ => throw new InvalidOperationException("Synthetic sampler failure."), measured.Processes, default));
        var completed = cadence.Sample(1000, measured.System, measured.Processes, default);
        clock.Advance(3100);
        var admission = CapacityPolicy.Evaluate(completed.Resources, new(), [], 0, clock.GetUtcNow());
        Assert.False(admission.SamplesHealthy); Assert.Equal(0, admission.MemoryOnlySlots);
        Assert.Equal(1, measured.SystemReads);
    }

    [Fact]
    public void LegacySnapshotsAndListOnlySamplersNeverClaimCompleteInventory()
    {
        var clock = new Clock(); var measured = new Measurements(clock); var cadence = new DesktopSamplingCadence(clock);
        var legacy = new DesktopSnapshot(measured.System(default), []);
        Assert.Null(legacy.ProcessObservedUtc);
        Assert.Equal(0, legacy.ProcessRevision);
        Assert.False(legacy.ProcessInventoryComplete);
        var sampled = cadence.Sample(5000, measured.System, measured.Processes, default);
        Assert.Equal(clock.GetUtcNow(), sampled.ProcessObservedUtc);
        Assert.Equal(1, sampled.ProcessRevision);
        Assert.False(sampled.ProcessInventoryComplete);
    }

    [Fact]
    public void ResourceRefreshRetainsCompleteProcessTimestampRevisionAndListUntilRealEnumeration()
    {
        var clock = new Clock(); var measured = new Measurements(clock); var cadence = new DesktopSamplingCadence(clock);
        var first = cadence.SampleInventory(5000, measured.System, measured.Inventory, default);
        Assert.True(first.ProcessInventoryComplete);
        Assert.Equal(1, first.ProcessRevision);
        Assert.Equal(clock.GetUtcNow(), first.ProcessObservedUtc);
        for (int seconds = 1; seconds < 5; seconds++)
        {
            clock.Advance(1000);
            var cached = cadence.SampleInventory(5000, measured.System, measured.Inventory, default);
            Assert.NotSame(first.Resources, cached.Resources);
            Assert.Same(first.Sessions, cached.Sessions);
            Assert.Equal(first.ProcessObservedUtc, cached.ProcessObservedUtc);
            Assert.Equal(first.ProcessRevision, cached.ProcessRevision);
            Assert.True(cached.ProcessInventoryComplete);
        }
        Assert.Equal(1, measured.ProcessReads);
        clock.Advance(1000);
        var next = cadence.SampleInventory(5000, measured.System, measured.Inventory, default);
        Assert.Equal(2, next.ProcessRevision);
        Assert.Equal(first.ProcessObservedUtc!.Value.AddSeconds(5), next.ProcessObservedUtc);
        Assert.True(next.ProcessInventoryComplete);
        Assert.Equal(2, measured.ProcessReads);
    }

    [Fact]
    public void CompleteEmptyEnumerationHasFreshEvidenceAndClearDoesNotReuseRevision()
    {
        var clock = new Clock(); var measured = new Measurements(clock); var cadence = new DesktopSamplingCadence(clock);
        var first = cadence.SampleInventory(1000, measured.System, measured.Inventory, default);
        clock.Advance(1000);
        var empty = cadence.SampleInventory(1000, measured.System, _ => new([], Complete: true), default);
        Assert.Empty(empty.Sessions);
        Assert.True(empty.ProcessInventoryComplete);
        Assert.Equal(first.ProcessRevision + 1, empty.ProcessRevision);
        Assert.Equal(clock.GetUtcNow(), empty.ProcessObservedUtc);
        Assert.Null(empty.Problem);
        cadence.Clear();
        var reopened = cadence.SampleInventory(1000, measured.System, _ => new([], Complete: true), default);
        Assert.True(reopened.ProcessInventoryComplete);
        Assert.True(reopened.ProcessRevision > empty.ProcessRevision);
    }

    [Theory]
    [InlineData("invalid-operation")]
    [InlineData("access-denied")]
    [InlineData("native-query")]
    public void FailedEnumerationRetainsPriorEvidenceInvalidatesCompletenessAndThrottlesRetry(string failure)
    {
        var clock = new Clock(); var measured = new Measurements(clock); var cadence = new DesktopSamplingCadence(clock);
        var first = cadence.SampleInventory(2000, measured.System, measured.Inventory, default);
        clock.Advance(2000);
        int attempts = 0;
        DesktopProcessInventory Fail(CancellationToken _)
        {
            attempts++;
            throw failure switch
            {
                "access-denied" => new UnauthorizedAccessException("untrusted-inventory-marker"),
                "native-query" => new Win32Exception("untrusted-inventory-marker"),
                _ => new InvalidOperationException("untrusted-inventory-marker")
            };
        }
        var failed = cadence.SampleInventory(2000, measured.System, Fail, default);
        Assert.Equal(clock.GetUtcNow(), failed.Resources.ObservedUtc);
        Assert.Equal(2, measured.SystemReads); // Failed process scans still permit the due essential measurement.
        Assert.Same(first.Sessions, failed.Sessions);
        Assert.Equal(first.ProcessObservedUtc, failed.ProcessObservedUtc);
        Assert.Equal(first.ProcessRevision, failed.ProcessRevision);
        Assert.False(failed.ProcessInventoryComplete);
        Assert.NotNull(failed.Problem);
        Assert.DoesNotContain("untrusted-inventory-marker", failed.Problem);
        clock.Advance(1000);
        var cachedFailure = cadence.SampleInventory(2000, measured.System, Fail, default);
        Assert.Equal(1, attempts);
        Assert.Same(first.Sessions, cachedFailure.Sessions);
        Assert.Equal(first.ProcessObservedUtc, cachedFailure.ProcessObservedUtc);
        Assert.Equal(first.ProcessRevision, cachedFailure.ProcessRevision);
        Assert.False(cachedFailure.ProcessInventoryComplete);
        clock.Advance(1000);
        var recovered = cadence.SampleInventory(2000, measured.System, _ => new([], Complete: true), default);
        Assert.True(recovered.ProcessInventoryComplete);
        Assert.Empty(recovered.Sessions);
        Assert.Equal(first.ProcessRevision + 1, recovered.ProcessRevision);
        Assert.Equal(clock.GetUtcNow(), recovered.ProcessObservedUtc);
        Assert.Null(recovered.Problem);
    }

    [Fact]
    public void FirstFailedScanHasNoInventoryEvidenceAndCompleteEmptyRecoveryIsDistinct()
    {
        var clock = new Clock(); var measured = new Measurements(clock); var cadence = new DesktopSamplingCadence(clock);
        var failed = cadence.SampleInventory(1000, measured.System,
            _ => throw new Win32Exception("Synthetic unavailable inventory."), default);
        Assert.Empty(failed.Sessions);
        Assert.Null(failed.ProcessObservedUtc);
        Assert.Equal(0, failed.ProcessRevision);
        Assert.False(failed.ProcessInventoryComplete);
        clock.Advance(1000);
        var recovered = cadence.SampleInventory(1000, measured.System, _ => new([], Complete: true), default);
        Assert.Empty(recovered.Sessions);
        Assert.Equal(clock.GetUtcNow(), recovered.ProcessObservedUtc);
        Assert.Equal(1, recovered.ProcessRevision);
        Assert.True(recovered.ProcessInventoryComplete);
    }

    [Theory]
    [InlineData("zero-start")]
    [InlineData("invalid-pid")]
    [InlineData("invalid-start")]
    [InlineData("future-start")]
    [InlineData("future-observed")]
    [InlineData("inaccessible")]
    [InlineData("duplicate")]
    [InlineData("conflicting-starts")]
    [InlineData("null-row")]
    [InlineData("budget")]
    [InlineData("truncated")]
    [InlineData("producer-partial")]
    public void PartialUnsafeAndBudgetLimitedInventoriesCannotClaimCompleteAbsenceEvidence(string kind)
    {
        var clock = new Clock(); var measured = new Measurements(clock); var cadence = new DesktopSamplingCadence(clock);
        var valid = Session(clock.GetUtcNow());
        IReadOnlyList<SessionObservation> rows = kind switch
        {
            "zero-start" => [valid, valid with { ProcessId = 43, ProcessStartUtcTicks = 0 }],
            "invalid-pid" => [valid, valid with { ProcessId = 0 }],
            "invalid-start" => [valid, valid with { ProcessId = 43, ProcessStartUtcTicks = long.MaxValue }],
            "future-start" => [valid, valid with { ProcessId = 43, ProcessStartUtcTicks = clock.GetUtcNow().AddMinutes(1).UtcTicks }],
            "future-observed" => [valid, valid with { ProcessId = 43, ObservedUtc = clock.GetUtcNow().AddMinutes(1) }],
            "inaccessible" => [valid, valid with { ProcessId = 43, Problem = "Synthetic unavailable observation." }],
            "duplicate" => [valid, valid],
            "conflicting-starts" => [valid, valid with { ProcessStartUtcTicks = valid.ProcessStartUtcTicks + 1 }],
            "null-row" => [valid, null!],
            "budget" or "truncated" => Enumerable.Range(0, kind == "budget" ? 128 : 129)
                .Select(index => valid with { ProcessId = index + 1 }).ToArray(),
            _ => [valid]
        };
        var result = cadence.SampleInventory(1000, measured.System,
            _ => new(rows, Complete: kind != "producer-partial"), default);
        Assert.False(result.ProcessInventoryComplete);
        Assert.Equal(clock.GetUtcNow(), result.ProcessObservedUtc);
        Assert.Equal(1, result.ProcessRevision);
        Assert.InRange(result.Sessions.Count, 1, DesktopSamplingCadence.MaximumProcessSamples);
        Assert.All(result.Sessions, row => Assert.NotNull(row));
    }

    [Fact]
    public void CancelledEnumerationCannotRenewOrDowngradePreviouslyAcceptedEvidence()
    {
        var clock = new Clock(); var measured = new Measurements(clock); var cadence = new DesktopSamplingCadence(clock);
        var first = cadence.SampleInventory(5000, measured.System, measured.Inventory, default);
        clock.Advance(1000);
        using var stop = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => cadence.SampleInventory(1000, measured.System, _ =>
        {
            stop.Cancel(); return new([], Complete: false);
        }, stop.Token));
        var cached = cadence.SampleInventory(5000, measured.System, measured.Inventory, default);
        Assert.Same(first.Sessions, cached.Sessions);
        Assert.Equal(first.ProcessRevision, cached.ProcessRevision);
        Assert.Equal(first.ProcessObservedUtc, cached.ProcessObservedUtc);
        Assert.True(cached.ProcessInventoryComplete);
        clock.Advance(4000);
        var next = cadence.SampleInventory(5000, measured.System, measured.Inventory, default);
        Assert.Equal(first.ProcessRevision + 1, next.ProcessRevision); // Canceled attempt consumed no revision.
        Assert.Equal(clock.GetUtcNow(), next.ProcessObservedUtc);
        Assert.Equal(2, measured.ProcessReads);
    }

    [Fact]
    public void CompletedProcessEvidenceIncludesRowsObservedDuringEnumerationAndIsNotRenewedByResources()
    {
        var clock = new Clock(); var measured = new Measurements(clock); var cadence = new DesktopSamplingCadence(clock);
        DateTimeOffset began = clock.GetUtcNow();
        var first = cadence.SampleInventory(5000, measured.System, _ =>
        {
            clock.Advance(100);
            var firstRow = Session(clock.GetUtcNow());
            clock.Advance(100);
            var secondRow = Session(clock.GetUtcNow()) with { ProcessId = 43 };
            clock.Advance(100);
            return new([firstRow, secondRow], Complete: true);
        }, default);
        Assert.True(first.ProcessInventoryComplete);
        DateTimeOffset completed = began.AddMilliseconds(300);
        Assert.Equal(completed, first.ProcessObservedUtc);
        Assert.All(first.Sessions, row =>
        {
            Assert.True(row.ObservedUtc > began);
            Assert.True(row.ObservedUtc <= first.ProcessObservedUtc);
        });
        // The first actual system measurement followed enumeration at +300ms.
        clock.Advance(1000);
        var cached = cadence.SampleInventory(5000, measured.System, measured.Inventory, default);
        Assert.Equal(clock.GetUtcNow(), cached.Resources.ObservedUtc);
        Assert.Equal(completed, cached.ProcessObservedUtc);
        Assert.Same(first.Sessions, cached.Sessions);
        Assert.Equal(first.ProcessRevision, cached.ProcessRevision);
    }

    [Fact]
    public void CancellationDuringFailedEnumerationPropagatesInsteadOfCachingFailure()
    {
        var clock = new Clock(); var measured = new Measurements(clock); var cadence = new DesktopSamplingCadence(clock);
        using var stop = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => cadence.SampleInventory(1000, measured.System, _ =>
        {
            stop.Cancel(); throw new Win32Exception("Synthetic failure racing cancellation.");
        }, stop.Token));
        var complete = cadence.SampleInventory(1000, measured.System, _ => new([], Complete: true), default);
        Assert.Equal(1, complete.ProcessRevision);
        Assert.True(complete.ProcessInventoryComplete);
    }

    [Fact]
    public void WallClockChangesNeverRenewCachedProcessEvidenceOrChangeMonotonicCadence()
    {
        var clock = new Clock(); var measured = new Measurements(clock); var cadence = new DesktopSamplingCadence(clock);
        var first = cadence.SampleInventory(5000, measured.System, measured.Inventory, default);
        clock.Advance(1000); clock.ShiftWallClock(TimeSpan.FromHours(-2));
        var cached = cadence.SampleInventory(5000, measured.System, measured.Inventory, default);
        Assert.Equal(first.ProcessObservedUtc, cached.ProcessObservedUtc);
        Assert.Equal(first.ProcessRevision, cached.ProcessRevision);
        Assert.True(cached.ProcessInventoryComplete);
        Assert.Same(first.Sessions, cached.Sessions);
        clock.Advance(4000);
        var next = cadence.SampleInventory(5000, measured.System, measured.Inventory, default);
        Assert.Equal(first.ProcessRevision + 1, next.ProcessRevision);
        Assert.True(next.ProcessObservedUtc < first.ProcessObservedUtc);
    }

    [Theory]
    [InlineData(499)]
    [InlineData(5001)]
    public void InvalidIntervalsCannotDisableEssentialSampling(int milliseconds)
    {
        var clock = new Clock(); var measured = new Measurements(clock); var cadence = new DesktopSamplingCadence(clock);
        Assert.Throws<ArgumentOutOfRangeException>(() => cadence.Sample(milliseconds, measured.System, measured.Processes, default));
        Assert.Equal(0, measured.SystemReads); Assert.Equal(0, measured.ProcessReads);
    }

    [Fact]
    public async Task MonitorConfigurationValidatesTheWholePolicyAndPreCanceledSamplingDoesNoNativeWork()
    {
        using var monitor = new WindowsDesktopMonitor(new Clock());
        IConfigurableDesktopMonitor configurable = monitor;
        configurable.ConfigurePerformance(Policy(500));
        Assert.Throws<ArgumentOutOfRangeException>(() => configurable.ConfigurePerformance(Policy(1000) with { ProcessSampleMs = 499 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => configurable.ConfigurePerformance(Policy(1000) with { PresentationMs = 1 }));
        Assert.Throws<ArgumentNullException>(() => configurable.ConfigurePerformance(null!));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.SampleAsync(stop.Token));
        monitor.Dispose();
        Assert.Throws<ObjectDisposedException>(() => configurable.ConfigurePerformance(Policy(1000)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => monitor.SampleAsync(default));
    }
}
