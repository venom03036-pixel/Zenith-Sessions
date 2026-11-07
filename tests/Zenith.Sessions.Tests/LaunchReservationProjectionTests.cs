using System.Collections;
using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// Arithmetic over synthetic immutable facts only. No process/account/native/data access.
public sealed class LaunchReservationProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 6, 0, 0, TimeSpan.Zero);
    private static long Bytes(double gib) => checked((long)(gib * CapacityPolicy.GiB));
    private static ulong G(double gib) => checked((ulong)Bytes(gib));
    private static ResourceSnapshot Resources() => new(Now, G(32), G(20), G(64), G(10), 20);
    private static SessionObservation Row(int pid = 42) => new(pid, Now.AddMinutes(-5).UtcTicks, "Synthetic", (nint)100,
        "Synthetic unrelated title", false, true, Bytes(2), Bytes(2), 10, Now.AddMilliseconds(-100), PrivateWorkingSetBytes: Bytes(1));
    private static LaunchOperation Operation(int pid = 42, LaunchState state = LaunchState.ClientStartedUnverified, bool receipt = true)
        => new(new(Guid.NewGuid(), Guid.NewGuid(), 1, 123, 42, 0, Now.AddMinutes(-2), 7), state,
            state == LaunchState.ClientStartedUnverified ? LaunchProblem.ClientStartedUnverified : LaunchProblem.None, Now,
            receipt ? new(pid, Row(pid).ProcessStartUtcTicks, Now.AddMinutes(-1)) : null);
    private static DesktopSnapshot Desktop(params SessionObservation[] rows) => new(Resources(), Array.AsReadOnly(rows),
        ProcessObservedUtc: Now.AddMilliseconds(-50), ProcessRevision: 1, ProcessInventoryComplete: true);
    private static LaunchReservationProjectionResult Project(LaunchOperation operation, DesktopSnapshot? desktop = null, CapacitySettings? settings = null)
        => LaunchReservationProjection.Evaluate(new(false, [operation]), desktop ?? Desktop(), settings ?? new(), Now);
    private static void Full(LaunchReservationProjectionResult result, int count = 1)
    {
        Assert.Equal(count, result.Reservations.Count); Assert.Equal(count, result.FullBudgetReservations); Assert.Equal(0, result.CreditedReservations);
        Assert.All(result.Reservations, entry => { Assert.Equal(G(2.5), entry.PhysicalBytes); Assert.Equal(G(3), entry.CommitBytes); });
    }

    [Fact]
    public void ExactFreshPrivateConsumptionCreditsOnlyUnallocatedGrowthAndCountsTheProcessOnce()
    {
        var operation = Operation(); var desktop = Desktop(Row()); var result = Project(operation, desktop);
        var remaining = Assert.Single(result.Reservations);
        Assert.Equal(operation.Intent.OperationId, remaining.OperationId); Assert.Equal(G(1.5), remaining.PhysicalBytes); Assert.Equal(G(1), remaining.CommitBytes);
        Assert.Equal(1, result.OccupiedForAdmission); Assert.True(result.FreshProcessInventory); Assert.Equal(1, result.CreditedReservations);
        Assert.Contains(operation.Intent.OperationId, result.SafelyObservedOperationIds);
        Assert.Equal(Bytes(1), desktop.Sessions[0].PrivateWorkingSetBytes); Assert.Equal(LaunchState.ClientStartedUnverified, operation.State);
    }

    [Fact]
    public void AlreadyConsumedMemoryIsNotSubtractedFromGlobalAvailabilityAgain()
    {
        var operation = Operation(); var desktop = Desktop(Row()); var projection = Project(operation, desktop);
        var decision = CapacityPolicy.Evaluate(desktop.Resources, new(), projection.Reservations, projection.OccupiedForAdmission, Now);
        Assert.Equal(G(1.5), decision.ReservedPhysical); Assert.Equal(G(1), decision.ReservedCommit);
        Assert.Equal(4, decision.MemoryOnlySlots); // 20GiB available -6.4GiB spare -1.5GiB remaining, not another full/current footprint.
    }

    [Theory]
    [InlineData(LaunchState.Validating)]
    [InlineData(LaunchState.Launching)]
    [InlineData(LaunchState.Stabilizing)]
    [InlineData(LaunchState.Uncertain)]
    [InlineData(LaunchState.ReviewRequired)]
    [InlineData(LaunchState.ClientStartedUnverified)]
    public void EveryReservationStateWithoutAUsableReceiptKeepsItsWholeForecast(LaunchState state)
    {
        var result = Project(Operation(state: state, receipt: false)); Full(result); Assert.Equal(1, result.OccupiedForAdmission);
        Assert.Empty(result.SafelyObservedOperationIds);
    }

    [Theory]
    [InlineData(LaunchState.Cancelled)]
    [InlineData(LaunchState.Failed)]
    [InlineData(LaunchState.Arrived)]
    [InlineData(LaunchState.Queued)]
    public void RetainedReceiptDoesNotDisappearBecauseItsDisplayStateChanged(LaunchState state)
    {
        var result = Project(Operation(state: state)); Full(result); Assert.Equal(1, result.OccupiedForAdmission);
        var observed = Project(Operation(state: state), Desktop(Row())); Assert.Equal(1, observed.OccupiedForAdmission); Assert.Single(observed.Reservations);
        Assert.Equal(G(1.5), observed.Reservations[0].PhysicalBytes);
    }

    [Theory]
    [InlineData(LaunchState.Queued)]
    [InlineData(LaunchState.Blocked)]
    [InlineData(LaunchState.Preparing)]
    [InlineData(LaunchState.Cancelled)]
    public void ReceiptlessPrestartOrTerminalRowsDoNotInventReservations(LaunchState state)
    {
        var result = Project(Operation(state: state, receipt: false), Desktop(Row()));
        Assert.Empty(result.Reservations); Assert.Equal(1, result.OccupiedForAdmission); Assert.Empty(result.SafelyObservedOperationIds);
    }

    [Fact]
    public void ExplicitClosureAcknowledgmentReleasesOnlyItsForecastAndNeverHidesAnObservedOsProcess()
    {
        var operation = Operation(state: LaunchState.Cancelled) with { Problem = LaunchProblem.ClientClosureAcknowledged };
        var result = Project(operation, Desktop(Row())); Assert.Empty(result.Reservations); Assert.Equal(1, result.OccupiedForAdmission);
        Assert.Empty(result.SafelyObservedOperationIds);
        Assert.Equal(0, Project(operation).OccupiedForAdmission); // no exit claim, only explicit release and an empty supplied inventory
        var legacy = Project(operation with { Problem = LaunchProblem.None }); Full(legacy); Assert.Equal(1, legacy.OccupiedForAdmission);
    }

    [Fact]
    public void ReleasedReceiptDoesNotCompeteWithAnotherCurrentReservationForTheSameObservedInstance()
    {
        var active = Operation(); var acknowledged = Operation(state: LaunchState.Cancelled) with { Problem = LaunchProblem.ClientClosureAcknowledged };
        var result = LaunchReservationProjection.Evaluate(new(false, [acknowledged, active]), Desktop(Row()), new(), Now);
        Assert.Single(result.Reservations); Assert.Equal(1, result.OccupiedForAdmission);
        Assert.Contains(active.Intent.OperationId, result.SafelyObservedOperationIds); Assert.DoesNotContain(acknowledged.Intent.OperationId, result.SafelyObservedOperationIds);
    }

    [Fact]
    public void SharedWorkingSetIsNeverPrivatePhysicalCredit()
    {
        var result = Project(Operation(), Desktop(Row() with { WorkingSetBytes = Bytes(8), PrivateWorkingSetBytes = null }));
        var remaining = Assert.Single(result.Reservations); Assert.Equal(G(2.5), remaining.PhysicalBytes); Assert.Equal(G(1), remaining.CommitBytes);
        Assert.Single(result.SafelyObservedOperationIds); Assert.Equal(1, result.OccupiedForAdmission);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(13)]
    public void InvalidPrivateWorkingSetKeepsPhysicalForecastWhileValidCommitRemainsIndependent(int value)
    {
        var row = Row() with { PrivateWorkingSetBytes = value < 0 ? -1 : Bytes(value) };
        var result = Project(Operation(), Desktop(row)); Assert.Equal(G(2.5), result.Reservations[0].PhysicalBytes); Assert.Equal(G(1), result.Reservations[0].CommitBytes);
    }

    [Fact]
    public void SaturationAtZeroStillRetainsReservationAndOccupiedIdentity()
    {
        var row = Row() with { WorkingSetBytes = Bytes(6), PrivateCommitBytes = Bytes(5), PrivateWorkingSetBytes = Bytes(4) };
        var result = Project(Operation(), Desktop(row)); var remaining = Assert.Single(result.Reservations);
        Assert.Equal(0UL, remaining.PhysicalBytes); Assert.Equal(0UL, remaining.CommitBytes); Assert.Equal(1, result.OccupiedForAdmission);
        Assert.Single(result.SafelyObservedOperationIds); Assert.Equal(1, result.CreditedReservations);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("legacy")]
    [InlineData("revision")]
    [InlineData("inventory-stale")]
    [InlineData("inventory-future")]
    [InlineData("row-stale")]
    [InlineData("row-after-inventory")]
    [InlineData("row-future")]
    [InlineData("row-problem")]
    [InlineData("frame-problem")]
    [InlineData("resource-problem")]
    [InlineData("resource-stale")]
    [InlineData("resource-future")]
    [InlineData("resource-before-row")]
    public void IncompleteStaleFutureOrIncoherentEvidenceCannotReduceTheForecast(string condition)
    {
        var desktop = Desktop(Row());
        desktop = condition switch
        {
            "incomplete" => desktop with { ProcessInventoryComplete = false },
            "legacy" => new(desktop.Resources, desktop.Sessions),
            "revision" => desktop with { ProcessRevision = 0 },
            "inventory-stale" => desktop with { ProcessObservedUtc = Now.AddSeconds(-4) },
            "inventory-future" => desktop with { ProcessObservedUtc = Now.AddTicks(1) },
            "row-stale" => desktop with { Sessions = [Row() with { ObservedUtc = Now.AddSeconds(-4) }] },
            "row-after-inventory" => desktop with { Sessions = [Row() with { ObservedUtc = Now.AddMilliseconds(-20) }] },
            "row-future" => desktop with { Sessions = [Row() with { ObservedUtc = Now.AddTicks(1) }] },
            "row-problem" => desktop with { Sessions = [Row() with { Problem = "synthetic access failure" }] },
            "frame-problem" => desktop with { Problem = "synthetic partial inventory" },
            "resource-problem" => desktop with { Resources = desktop.Resources with { Problem = "synthetic unavailable" } },
            "resource-stale" => desktop with { Resources = desktop.Resources with { ObservedUtc = Now.AddSeconds(-4) } },
            "resource-future" => desktop with { Resources = desktop.Resources with { ObservedUtc = Now.AddTicks(1) } },
            _ => desktop with { Resources = desktop.Resources with { ObservedUtc = Row().ObservedUtc.AddTicks(-1) } }
        };
        var result = Project(Operation(), desktop); Full(result); Assert.Equal(2, result.OccupiedForAdmission); Assert.Empty(result.SafelyObservedOperationIds);
    }

    [Theory]
    [InlineData("working-negative")]
    [InlineData("commit-negative")]
    [InlineData("working-over-total")]
    [InlineData("commit-over-system")]
    [InlineData("cpu-nan")]
    [InlineData("cpu-infinity")]
    [InlineData("cpu-range")]
    [InlineData("virtual-negative")]
    [InlineData("fault-rate")]
    public void BadMeasurementsDoNotBecomeCreditsThroughCastingOrAssumedZero(string condition)
    {
        var row = Row(); row = condition switch
        {
            "working-negative" => row with { WorkingSetBytes = -1 }, "commit-negative" => row with { PrivateCommitBytes = -1 },
            "working-over-total" => row with { WorkingSetBytes = Bytes(33) }, "commit-over-system" => row with { PrivateCommitBytes = Bytes(11) },
            "cpu-nan" => row with { CpuPercent = double.NaN }, "cpu-infinity" => row with { CpuPercent = double.PositiveInfinity },
            "cpu-range" => row with { CpuPercent = 101 }, "virtual-negative" => row with { VirtualAddressBytes = -1 },
            _ => row with { PageFaultsPerSecond = double.NaN }
        };
        var result = Project(Operation(), Desktop(row)); Full(result); Assert.Empty(result.SafelyObservedOperationIds); Assert.Equal(2, result.OccupiedForAdmission);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PidReuseOrAChangedPidCannotMatchByWindowTitleUserOrTiming(bool changePid)
    {
        var row = Row(); row = changePid ? row with { ProcessId = 43 } : row with { ProcessStartUtcTicks = row.ProcessStartUtcTicks + 1 };
        var result = Project(Operation(), Desktop(row)); Full(result); Assert.Empty(result.SafelyObservedOperationIds); Assert.Equal(2, result.OccupiedForAdmission);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateObservationsIncludingConflictingStartsInvalidateCompleteFrameCredit(bool conflictingStart)
    {
        var row = Row(); var duplicate = conflictingStart ? row with { ProcessStartUtcTicks = row.ProcessStartUtcTicks + 1 } : row;
        var result = Project(Operation(), Desktop(row, duplicate)); Full(result); Assert.False(result.FreshProcessInventory);
        Assert.Empty(result.SafelyObservedOperationIds); Assert.Equal(3, result.OccupiedForAdmission);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoReceiptsCannotSpendTheSameObservedProcessConsumption(bool conflictingStart)
    {
        var first = Operation(); var second = Operation(state: LaunchState.Cancelled);
        if (conflictingStart) second = second with { ProcessReceipt = second.ProcessReceipt! with { ProcessStartUtcTicks = Row().ProcessStartUtcTicks + 1 } };
        var result = LaunchReservationProjection.Evaluate(new(false, [first, second]), Desktop(Row()), new(), Now);
        Full(result, 2); Assert.Empty(result.SafelyObservedOperationIds); Assert.Equal(3, result.OccupiedForAdmission);
    }

    [Fact]
    public void CollectivePrivateClaimsBeyondTheGlobalSampleRetainThatWholeBudgetChannel()
    {
        var row = Row() with { WorkingSetBytes = Bytes(2), PrivateWorkingSetBytes = Bytes(1.5), PrivateCommitBytes = Bytes(3) };
        var desktop = Desktop(row, row with { ProcessId = 43 }) with { Resources = Resources() with { AvailablePhysical = G(30), CommitUsed = G(4) } };
        var result = LaunchReservationProjection.Evaluate(new(false, [Operation(), Operation(43)]), desktop, new(), Now);
        Full(result, 2); Assert.Equal(2, result.OccupiedForAdmission); Assert.Equal(2, result.SafelyObservedOperationIds.Count);
    }

    [Fact]
    public void ALongRunningOriginalReceiptIsNotRejectedByANearestStartHeuristic()
    {
        var start = Now.AddYears(-1).UtcTicks; var operation = Operation() with { ProcessReceipt = new(42, start, Now.AddMonths(-1)) };
        var result = Project(operation, Desktop(Row() with { ProcessStartUtcTicks = start, CpuPercent = null }));
        Assert.Equal(G(1.5), result.Reservations[0].PhysicalBytes); Assert.Single(result.SafelyObservedOperationIds);
        var future = Project(operation with { ProcessReceipt = operation.ProcessReceipt! with { RecordedUtc = Now.AddTicks(1) } }, Desktop(Row() with { ProcessStartUtcTicks = start }));
        Full(future); Assert.Empty(future.SafelyObservedOperationIds);
    }

    [Fact]
    public void ReusingTheSameSnapshotNeverRenewsItsOriginalMeasurementTimes()
    {
        var operation = Operation(); var desktop = Desktop(Row());
        var fresh = LaunchReservationProjection.Evaluate(new(false, [operation]), desktop, new(), Now.AddSeconds(2));
        Assert.Single(fresh.SafelyObservedOperationIds);
        var stale = LaunchReservationProjection.Evaluate(new(false, [operation]), desktop, new(), Now.AddSeconds(3));
        Full(stale); Assert.Empty(stale.SafelyObservedOperationIds); Assert.Equal(2, stale.OccupiedForAdmission);
        Assert.Equal(Now.AddMilliseconds(-100), desktop.Sessions[0].ObservedUtc); Assert.Equal(Now, desktop.Resources.ObservedUtc);
    }

    [Fact]
    public void ConfiguredForecastsAndZeroMeasurementsRemainIndependentOfFallbackDefaults()
    {
        var result = Project(Operation(), Desktop(Row() with { WorkingSetBytes = 0, PrivateWorkingSetBytes = null, PrivateCommitBytes = 0 }), new(NewClientPhysicalGiB: 4, NewClientCommitGiB: 5));
        Assert.Equal(G(4), result.Reservations[0].PhysicalBytes); Assert.Equal(G(5), result.Reservations[0].CommitBytes);
        Assert.Single(result.SafelyObservedOperationIds); Assert.Equal(1, result.OccupiedForAdmission);
    }

    [Fact]
    public void FractionalGibForecastsRoundUpBeforeSubtractingExactMeasuredBytes()
    {
        var settings = new CapacitySettings(NewClientPhysicalGiB: .1, NewClientCommitGiB: .1);
        var full = Project(Operation(), settings: settings);
        Assert.Equal(107374183UL, full.Reservations[0].PhysicalBytes); Assert.Equal(107374183UL, full.Reservations[0].CommitBytes);
        var measured = Project(Operation(), Desktop(Row() with { WorkingSetBytes = 1, PrivateWorkingSetBytes = 1, PrivateCommitBytes = 1 }), settings);
        Assert.Equal(107374182UL, measured.Reservations[0].PhysicalBytes); Assert.Equal(107374182UL, measured.Reservations[0].CommitBytes);
    }

    [Fact]
    public void MaximumCountsAndLargeCountersStayBoundedWithoutOverflowOrUnboundedEnumeration()
    {
        var operations = Enumerable.Range(0, 1101).Select(_ => Operation(state: LaunchState.Launching, receipt: false)).ToArray();
        var rows = Enumerable.Range(0, 4096).Select(i => Row(1000 + i)).ToArray();
        var queue = new LaunchQueueSnapshot(false, new IndexedOnly<LaunchOperation>(operations));
        var desktop = Desktop() with { Sessions = new IndexedOnly<SessionObservation>(rows) };
        var result = LaunchReservationProjection.Evaluate(queue, desktop, new(NewClientPhysicalGiB: 1024, NewClientCommitGiB: 1024), Now);
        Assert.Equal(1101, result.Reservations.Count); Assert.Equal(5197, result.OccupiedForAdmission);
        Assert.Equal(1101UL * G(1024), result.Reservations.Aggregate(0UL, (sum, entry) => checked(sum + entry.PhysicalBytes)));
        var huge = Desktop(Row() with { WorkingSetBytes = long.MaxValue, PrivateWorkingSetBytes = long.MaxValue, PrivateCommitBytes = long.MaxValue })
            with { Resources = Resources() with { TotalPhysical = ulong.MaxValue, AvailablePhysical = 0, CommitLimit = ulong.MaxValue, CommitUsed = ulong.MaxValue } };
        var saturated = Project(Operation(), huge); Assert.Equal(0UL, saturated.Reservations[0].PhysicalBytes); Assert.Equal(0UL, saturated.Reservations[0].CommitBytes);
    }

    [Fact]
    public void InvalidInputsCannotSilentlyDropReservationsAndReturnedCollectionsCannotBeMutated()
    {
        var operation = Operation(); var queue = new LaunchQueueSnapshot(false, [operation]); var desktop = Desktop(Row());
        Assert.Throws<ArgumentException>(() => LaunchReservationProjection.Evaluate(queue with { Operations = [operation, operation] }, desktop, new(), Now));
        Assert.Throws<ArgumentException>(() => Project(operation with { Intent = operation.Intent with { OperationId = Guid.Empty } }));
        Assert.Throws<ArgumentException>(() => LaunchReservationProjection.Evaluate(queue with { Operations = Enumerable.Repeat(operation, 1102).ToArray() }, desktop, new(), Now));
        Assert.Throws<ArgumentException>(() => Project(operation, desktop with { Sessions = Enumerable.Repeat(Row(), 4097).ToArray() }));
        Assert.Throws<ArgumentException>(() => Project(operation, desktop, new(NewClientPhysicalGiB: double.NaN)));
        var result = Project(operation, desktop);
        Assert.Throws<NotSupportedException>(() => ((IList<ResourceReservation>)result.Reservations).Clear());
        Assert.Throws<NotSupportedException>(() => ((ISet<Guid>)result.SafelyObservedOperationIds).Clear());
        Assert.Contains(operation.Intent.OperationId, result.SafelyObservedOperationIds);
    }

    private sealed class IndexedOnly<T>(IReadOnlyList<T> rows) : IReadOnlyList<T>
    {
        public int Count => rows.Count;
        public T this[int index] => rows[index];
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("An unbounded enumerator must not be consulted.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
