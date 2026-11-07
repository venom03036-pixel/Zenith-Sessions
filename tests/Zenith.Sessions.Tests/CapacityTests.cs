using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class CapacityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static ulong G(double value) => (ulong)(value * CapacityPolicy.GiB);
    private static ResourceSnapshot Sample(double available = 20, double usedCommit = 48) => new(Now, G(32), G(available), G(64), G(usedCommit), 20);
    [Theory]
    [InlineData(8,4,0)][InlineData(16,12,3)][InlineData(32,20,5)]
    [InlineData(64,12,0)][InlineData(64,48,14)][InlineData(128,104,31)]
    public void SuppliedPhysicalArithmeticExamplesAreExamplesNotClientCapacity(double total,double available,int expected)
    {
        // Deliberately abundant synthetic commit isolates the requested physical-only arithmetic.
        var sample=new ResourceSnapshot(Now,G(total),G(available),G(10000),G(1),0);
        var result=CapacityPolicy.Evaluate(sample,new(),[],0,Now);
        Assert.Equal(expected,result.MemoryOnlySlots);Assert.True(result.SamplesHealthy);
    }
    [Fact] public void CommitAndPhysicalAreIndependentAndWindowsUsageIsNotSubtractedTwice()
    { Assert.Equal(3, CapacityPolicy.Evaluate(Sample(), new(), [], 0, Now).MemoryOnlySlots); }
    [Fact] public void AbundantRamCannotOverrideLowCommit()
    { Assert.Equal(0, CapacityPolicy.Evaluate(Sample(30, 63), new(), [], 0, Now).MemoryOnlySlots); }
    [Fact] public void ExistingGrowthAndPendingLaunchReservationsReduceHeadroom()
    { Assert.Equal(1, CapacityPolicy.Evaluate(Sample(), new(), [new(Guid.NewGuid(), G(2.5), G(6))], 0, Now).MemoryOnlySlots); }
    [Fact] public void UserMaximumCountsAlreadyOccupiedClients()
    { Assert.Equal(1, CapacityPolicy.Evaluate(Sample(), new(UserMaximum: 4), [], 3, Now).MemoryOnlySlots); }
    [Theory][InlineData(-4)][InlineData(2)] public void StaleAndFutureEssentialMeasurementsHoldAdmission(int seconds)
    { var result = CapacityPolicy.Evaluate(Sample() with { ObservedUtc = Now.AddSeconds(seconds) }, new(), [], 0, Now); Assert.False(result.SamplesHealthy); Assert.Equal(0, result.MemoryOnlySlots); }
    [Fact] public void UnknownMeasurementsCannotBecomeUnlimitedCapacity()
    { Assert.False(CapacityPolicy.Evaluate(Sample() with { Problem = "unavailable" }, new(), [], 0, Now).SamplesHealthy); }
    [Fact] public void ReservationOverflowSaturatesToNoCapacity()
    { Assert.Equal(0, CapacityPolicy.Evaluate(Sample(), new(), [new(Guid.NewGuid(), ulong.MaxValue, ulong.MaxValue),new(Guid.NewGuid(),1,1)], 0, Now).MemoryOnlySlots); }
    [Fact] public void DuplicateReservationsCannotDoubleAdmitOrSilentlyDeduplicate()
    { var id = Guid.NewGuid(); Assert.Throws<ArgumentException>(() => CapacityPolicy.Evaluate(Sample(), new(), [new(id,1,1),new(id,1,1)], 0, Now)); }
    [Theory][InlineData(double.NaN)][InlineData(double.PositiveInfinity)][InlineData(0)][InlineData(-1)]
    public void InvalidCostsAreRejected(double value) => Assert.Throws<ArgumentException>(() => new CapacitySettings(NewClientPhysicalGiB:value).Validated());
}
