using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class GpuPressureTests
{
    private static readonly DateTimeOffset Epoch=DateTimeOffset.UnixEpoch.AddDays(1);
    private static PressureDecision Observe(ResourcePressureTracker tracker,int second,double? gpu=20,bool complete=true,int? gpuSecond=null,bool unknown=false)
    {
        var now=Epoch.AddSeconds(second);
        var evidence=unknown?null:new GpuTelemetrySnapshot(Epoch.AddSeconds(gpuSecond??second),
            [new("00000000_00000001",0,[new(0,"3D",gpu)],gpu,null,null,complete)]);
        var sample=new ResourceSnapshot(now,32*CapacityPolicy.GiB,20*CapacityPolicy.GiB,64*CapacityPolicy.GiB,20*CapacityPolicy.GiB,20,Gpu:evidence);
        return tracker.Observe(sample,CapacityPolicy.Evaluate(sample,new(),[],0,now),now);
    }
    [Fact]
    public void SustainedPhysicalEnginePressureHoldsAndRequiresThirtyHealthySeconds()
    {
        var tracker=new ResourcePressureTracker();
        for(int i=0;i<15;i++) Assert.False(Observe(tracker,i,90).Held);
        Assert.True(Observe(tracker,15,90).Held);
        for(int i=16;i<46;i++) Assert.True(Observe(tracker,i).Held);
        Assert.False(Observe(tracker,46).Held);
    }
    [Fact]
    public void MissingGpuIsOptionalUntilAnEstablishedGpuHoldNeedsRecoveryEvidence()
    {
        Assert.False(Observe(new(),0,unknown:true).Held);
        var tracker=new ResourcePressureTracker();
        for(int i=0;i<=15;i++) Observe(tracker,i,90);
        for(int i=16;i<60;i++) Assert.True(Observe(tracker,i,unknown:true).Held);
        for(int i=60;i<90;i++) Assert.True(Observe(tracker,i,20).Held);
        Assert.False(Observe(tracker,90,20).Held);
    }
    [Theory]
    [InlineData(double.NaN,true)] [InlineData(double.PositiveInfinity,true)] [InlineData(101,true)] [InlineData(99,false)]
    public void InvalidOrIncompleteGpuCannotEstablishAHighLoadHold(double load,bool complete)
    {
        var tracker=new ResourcePressureTracker();
        for(int i=0;i<40;i++) Assert.False(Observe(tracker,i,load,complete).Held);
    }
    [Fact]
    public void StaleOrRepeatedCounterEvidenceCannotAccumulateHighTime()
    {
        var tracker=new ResourcePressureTracker();
        for(int i=0;i<20;i++) Assert.False(Observe(tracker,i,99,gpuSecond:0).Held);
        for(int i=20;i<35;i++) Assert.False(Observe(tracker,i,99).Held);
        Assert.True(Observe(tracker,35,99).Held);
    }
    [Fact]
    public void HealthyGpuSampleResetsTheSustainedTimer()
    {
        var tracker=new ResourcePressureTracker();
        for(int i=0;i<14;i++) Assert.False(Observe(tracker,i,90).Held);
        Assert.False(Observe(tracker,14,20).Held);
        for(int i=15;i<30;i++) Assert.False(Observe(tracker,i,90).Held);
        Assert.True(Observe(tracker,30,90).Held);
    }
    [Fact]
    public void UnknownSecondAdapterCannotProveRecoveryFromEstablishedGpuPressure()
    {
        var tracker=new ResourcePressureTracker();
        for(int i=0;i<=15;i++) Observe(tracker,i,90);
        for(int i=16;i<60;i++)
        {
            var now=Epoch.AddSeconds(i);
            var gpu=new GpuTelemetrySnapshot(now,[new("00000000_00000001",0,[],20,null,null,true),new("00000000_00000002",0,[],null,null,null,false)]);
            var sample=new ResourceSnapshot(now,32*CapacityPolicy.GiB,20*CapacityPolicy.GiB,64*CapacityPolicy.GiB,20*CapacityPolicy.GiB,20,Gpu:gpu);
            Assert.True(tracker.Observe(sample,CapacityPolicy.Evaluate(sample,new(),[],0,now),now).Held);
        }
    }
    [Fact]
    public void GpuRecoveryNeedsLowerHysteresisBandRatherThanMerelyBelowHoldThreshold()
    {
        var tracker=new ResourcePressureTracker();
        for(int i=0;i<=15;i++) Observe(tracker,i,90);
        for(int i=16;i<60;i++) Assert.True(Observe(tracker,i,75).Held);
        for(int i=60;i<90;i++) Assert.True(Observe(tracker,i,69).Held);
        Assert.False(Observe(tracker,90,69).Held);
    }
}
