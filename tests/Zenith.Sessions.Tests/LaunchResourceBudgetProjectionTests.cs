using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

// Deterministic arithmetic over synthetic process facts, never live-client capacity proof.
public sealed class LaunchResourceBudgetProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private static ulong G(double value) => checked((ulong)(value * CapacityPolicy.GiB));
    private static LaunchResourceBudget Budget(double physical, double commit) => new(G(physical), G(commit));
    private static ResourceSnapshot Sample() => new(Now, G(32), G(20), G(64), G(10), 15);
    private static SessionObservation Row(int pid) => new(pid, Now.AddMinutes(-4).UtcTicks, "Fixture", (nint)1,
        "Synthetic process", false, true, (long)G(2), (long)G(2), 5, Now.AddMilliseconds(-100), PrivateWorkingSetBytes: (long)G(1));
    private static DesktopSnapshot Desktop(params SessionObservation[] rows) => new(Sample(), rows,
        ProcessObservedUtc: Now.AddMilliseconds(-50), ProcessRevision: 1, ProcessInventoryComplete: true);
    private static LaunchOperation Operation(LaunchResourceBudget? budget, int pid = 42, bool receipt = false,
        LaunchState state = LaunchState.Uncertain) => new(new(Guid.NewGuid(), Guid.NewGuid(), 1, 123, 42, 0, Now.AddMinutes(-3), 7),
            state, LaunchProblem.None, Now, receipt ? new(pid, Row(pid).ProcessStartUtcTicks, Now.AddMinutes(-2)) : null, budget);
    private static LaunchReservationProjectionResult Project(LaunchOperation[] operations, CapacitySettings? settings = null, DesktopSnapshot? desktop = null)
        => LaunchReservationProjection.Evaluate(new(true, operations), desktop ?? Desktop(), settings ?? new(), Now);

    [Theory]
    [InlineData(.1)]
    [InlineData(2.5)]
    [InlineData(1024)]
    public void ChangingDefaultCostCannotRewriteAnOutstandingPinnedForecast(double changed)
    {
        var operation = Operation(Budget(4, 5));
        var result = Project([operation], new(NewClientPhysicalGiB: changed, NewClientCommitGiB: changed));
        var reservation = Assert.Single(result.Reservations);
        Assert.Equal(G(4), reservation.PhysicalBytes); Assert.Equal(G(5), reservation.CommitBytes);
        Assert.True(result.ForecastsKnown); Assert.Equal(Budget(4, 5), operation.ResourceBudget);
    }

    [Fact]
    public void DistinctPinnedCostsAreCreditedIndividuallyAndNeverUseTheCheapestDefault()
    {
        var first = Operation(Budget(4, 5), 42, true); var second = Operation(Budget(2, 3), 43, true);
        var result = Project([first, second], new(NewClientPhysicalGiB: .1, NewClientCommitGiB: .1), Desktop(Row(42), Row(43)));
        Assert.Equal(new ResourceReservation(first.Intent.OperationId, G(3), G(3)), result.Reservations[0]);
        Assert.Equal(new ResourceReservation(second.Intent.OperationId, G(1), G(1)), result.Reservations[1]);
        Assert.Equal(2, result.OccupiedForAdmission); Assert.Equal(2, result.CreditedReservations); Assert.True(result.ForecastsKnown);
    }

    [Fact]
    public void ConflictingAggregateCreditsRestoreEachRequestsOwnWholeForecast()
    {
        var first = Operation(Budget(3, 8), 42, true); var second = Operation(Budget(5, 10), 43, true);
        var rows = new[] { Row(42), Row(43) }.Select(row => row with
        { WorkingSetBytes = (long)G(5), PrivateWorkingSetBytes = (long)G(4), PrivateCommitBytes = (long)G(6) }).ToArray();
        var desktop = Desktop(rows) with { Resources = Sample() with { AvailablePhysical = G(26) } };
        var result = Project([first, second], new(NewClientPhysicalGiB: .1, NewClientCommitGiB: .1), desktop);
        Assert.Equal(new ResourceReservation(first.Intent.OperationId, G(3), G(8)), result.Reservations[0]);
        Assert.Equal(new ResourceReservation(second.Intent.OperationId, G(5), G(10)), result.Reservations[1]);
        Assert.Equal(2, result.FullBudgetReservations); Assert.Equal(0, result.CreditedReservations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyPossibleClientRetainsProvisionalArithmeticButNeverHasAKnownOriginalBudget(bool observed)
    {
        var legacy = Operation(null, receipt: true); var result = Project([legacy], desktop: observed ? Desktop(Row(42)) : Desktop());
        Assert.False(result.ForecastsKnown); Assert.Single(result.Reservations); Assert.Contains("provisional", result.Explanation);
        Assert.Contains("closed", result.Explanation); Assert.Null(legacy.ResourceBudget);
        Assert.Equal(observed ? 1 : 0, result.SafelyObservedOperationIds.Count); // Process evidence is distinct from forecast authority.
    }

    [Theory]
    [InlineData(LaunchState.Queued)]
    [InlineData(LaunchState.Blocked)]
    [InlineData(LaunchState.Preparing)]
    [InlineData(LaunchState.Cancelled)]
    [InlineData(LaunchState.Failed)]
    public void LegacyNonreservingRowsDoNotPoisonOtherForecasts(LaunchState state)
    {
        var result = Project([Operation(null, state: state)]);
        Assert.True(result.ForecastsKnown); Assert.Empty(result.Reservations);
    }

    [Fact]
    public void ExplicitClosureReviewRemovesUnknownBudgetHoldWithoutHidingOsOccupancy()
    {
        var legacy = Operation(null, receipt: true) with { State = LaunchState.Cancelled, Problem = LaunchProblem.ClientClosureAcknowledged };
        var result = Project([legacy], desktop: Desktop(Row(42)));
        Assert.True(result.ForecastsKnown); Assert.Empty(result.Reservations); Assert.Equal(1, result.OccupiedForAdmission);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1099511627777, 1)]
    [InlineData(1, 1099511627777)]
    public void InvalidPinnedBytesAreRejectedEvenOnAnOtherwiseNonreservingRow(long physical, long commit)
        => Assert.Throws<ArgumentException>(() => Project([Operation(new((ulong)physical, (ulong)commit), state: LaunchState.Cancelled)]));

    [Fact]
    public void ExpensiveCandidateCannotBorrowCheaperDefaultCosts()
    {
        var sample = Sample() with { AvailablePhysical = G(8), CommitUsed = G(55) };
        var policy = new CapacitySettings(NewClientPhysicalGiB: .1, NewClientCommitGiB: .1);
        Assert.True(CapacityPolicy.Evaluate(sample, policy, [], 0, Now).MemoryOnlySlots > 0);
        var result = CapacityPolicy.Evaluate(sample, policy, [], 0, Now, Budget(4, 5));
        Assert.Equal(0, result.MemoryOnlySlots); Assert.True(result.SamplesHealthy);
        Assert.Contains("4.00", result.Explanation); Assert.Contains("5.00", result.Explanation);
    }

    [Fact]
    public void CheapPinnedCandidateIsCheckedAgainstCurrentHeadroomAndUserLimitInsteadOfRaisedDefault()
    {
        var sample = Sample(); var policy = new CapacitySettings(NewClientPhysicalGiB: 1024, NewClientCommitGiB: 1024, UserMaximum: 2);
        Assert.Equal(0, CapacityPolicy.Evaluate(sample, policy, [], 1, Now).MemoryOnlySlots);
        Assert.Equal(1, CapacityPolicy.Evaluate(sample, policy, [], 1, Now, new(1, 1)).MemoryOnlySlots);
        Assert.Equal(0, CapacityPolicy.Evaluate(sample, policy, [], 2, Now, new(1, 1)).MemoryOnlySlots);
        Assert.Equal(0, CapacityPolicy.Evaluate(sample, policy, [new(Guid.NewGuid(), G(32), G(64))], 0, Now, new(1, 1)).MemoryOnlySlots);
    }

    [Fact]
    public void OneByteOddCostsUseExactIntegerDivisionWithoutGiBRounding()
    {
        var policy = new CapacitySettings(SparePhysicalPercent: 0, SparePhysicalGiB: 0, SpareCommitPercent: 0, SpareCommitGiB: 0);
        var sample = new ResourceSnapshot(Now, 1001, 1001, 2003, 0, 10);
        Assert.Equal(7, CapacityPolicy.Evaluate(sample, policy, [], 0, Now, new(143, 251)).MemoryOnlySlots);
        Assert.Equal(6, CapacityPolicy.Evaluate(sample, policy, [], 0, Now, new(144, 251)).MemoryOnlySlots);
    }

    [Fact]
    public void SystemPressureDoesNotLatchBecauseDefaultCostOrUserLimitCannotFitAnotherCandidate()
    {
        var policy = new CapacitySettings(NewClientPhysicalGiB: 1024, NewClientCommitGiB: 1024, UserMaximum: 1);
        var tracker = new ResourcePressureTracker();
        for (int second = 0; second < 35; second++)
        {
            var sample = Sample() with { ObservedUtc = Now.AddSeconds(second) };
            Assert.Equal(0, CapacityPolicy.Evaluate(sample, policy, [], 1, sample.ObservedUtc).MemoryOnlySlots);
            Assert.False(tracker.Observe(sample, CapacityPolicy.EvaluateSystemPressure(sample, policy, sample.ObservedUtc), sample.ObservedUtc).Held);
        }
    }

    [Theory]
    [InlineData("critical")]
    [InlineData("spare")]
    [InlineData("stale")]
    [InlineData("invalid")]
    public void CandidateIndependentPressureStillHoldsForEssentialMemoryFailures(string failure)
    {
        var sample = failure switch
        {
            "critical" => Sample() with { AvailablePhysical = G(.5) },
            "spare" => Sample() with { AvailablePhysical = G(6) },
            "stale" => Sample() with { ObservedUtc = Now.AddSeconds(-4) },
            _ => Sample() with { Problem = "Synthetic unavailable" }
        };
        Assert.True(new ResourcePressureTracker().Observe(sample, CapacityPolicy.EvaluateSystemPressure(sample, new(), Now), Now).Held);
    }

    [Fact]
    public void CandidateIndependentPressurePreservesSustainedCpuAndRecoveryGuards()
    {
        var tracker = new ResourcePressureTracker(); PressureDecision? last = null;
        // A healthy reading first establishes CPU history; a cold start already at 85% holds until it drops.
        var established = Sample() with { ObservedUtc = Now.AddSeconds(-1) };
        Assert.False(tracker.Observe(established, CapacityPolicy.EvaluateSystemPressure(established, new(), established.ObservedUtc), established.ObservedUtc).Held);
        for (int second = 0; second <= 15; second++)
        {
            var sample = Sample() with { ObservedUtc = Now.AddSeconds(second), CpuPercent = 85 };
            last = tracker.Observe(sample, CapacityPolicy.EvaluateSystemPressure(sample, new(), sample.ObservedUtc), sample.ObservedUtc);
            Assert.Equal(second == 15, last.Held);
        }
        for (int second = 16; second <= 46; second++)
        {
            var sample = Sample() with { ObservedUtc = Now.AddSeconds(second), CpuPercent = 10 };
            last = tracker.Observe(sample, CapacityPolicy.EvaluateSystemPressure(sample, new(), sample.ObservedUtc), sample.ObservedUtc);
            Assert.Equal(second < 46, last.Held);
        }
    }
}
