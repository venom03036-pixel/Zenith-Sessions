using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class PressureRecoveryTests
{
    private static ResourceSnapshot Sample(int t,double cpu=20,ulong? available=null,ulong? commit=null,ulong? total=null)
        =>new(DateTimeOffset.UnixEpoch.AddDays(1).AddSeconds(t),total??32*CapacityPolicy.GiB,available??20*CapacityPolicy.GiB,
            64*CapacityPolicy.GiB,commit??20*CapacityPolicy.GiB,cpu);
    private static PressureDecision Observe(ResourcePressureTracker tracker,ResourceSnapshot sample,CapacitySettings? settings=null)
        =>tracker.Observe(sample,CapacityPolicy.Evaluate(sample,settings??new(),[],0,sample.ObservedUtc),sample.ObservedUtc);
    [Fact]
    public void CpuRecoveryBelowHoldThresholdButAboveRecoveryBandCannotCountAsHealthy()
    {
        var tracker=new ResourcePressureTracker();for(int i=0;i<=15;i++) Observe(tracker,Sample(i,90));
        for(int i=16;i<60;i++) Assert.True(Observe(tracker,Sample(i,70)).Held);
        for(int i=60;i<90;i++) Assert.True(Observe(tracker,Sample(i,64)).Held);
        Assert.False(Observe(tracker,Sample(90,64)).Held);
    }
    [Fact]
    public void CriticalPressureIsExplicitEvenWithPermissiveConfiguredHeadroom()
    {
        var permissive=new CapacitySettings(0,0,0,0,.1,.1);
        Assert.Contains("Critical",Observe(new(),Sample(0,available:CapacityPolicy.GiB),permissive).Explanation);
        Assert.Contains("Critical",Observe(new(),Sample(0,commit:62*CapacityPolicy.GiB),permissive).Explanation);
    }
    [Fact]
    public void RecoveryMarginBlocksNearGuardAndRemainsReachableOnSmallMachines()
    {
        var settings=new CapacitySettings(0,1,0,1,.1,.1);var tracker=new ResourcePressureTracker();
        Assert.True(Observe(tracker,Sample(0,available:CapacityPolicy.GiB/2,total:4*CapacityPolicy.GiB),settings).Held);
        // A tenthGiB fits above the base guard, but does not meet its capped recovery margin.
        for(int t=1;t<40;t++)Assert.True(Observe(tracker,Sample(t,available:CapacityPolicy.GiB+CapacityPolicy.GiB/8,total:4*CapacityPolicy.GiB),settings).Held);
        for(int t=40;t<70;t++)Assert.True(Observe(tracker,Sample(t,available:2*CapacityPolicy.GiB,total:4*CapacityPolicy.GiB),settings).Held);
        Assert.False(Observe(tracker,Sample(70,available:2*CapacityPolicy.GiB,total:4*CapacityPolicy.GiB),settings).Held);
    }
}
