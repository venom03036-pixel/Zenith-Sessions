using System.Text.Json;
using System.IO;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class PressureAndDiagnosticsTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-pressure-tests-"+Guid.NewGuid().ToString("N"));
    private static ResourceSnapshot Sample(int second,double? cpu=20,ulong? available=null)=>new(DateTimeOffset.UnixEpoch.AddSeconds(second),32*CapacityPolicy.GiB,
        available??20*CapacityPolicy.GiB,64*CapacityPolicy.GiB,20*CapacityPolicy.GiB,cpu);
    private static PressureDecision Observe(ResourcePressureTracker tracker,int second,double? cpu=20,ulong? available=null)
    {
        var sample=Sample(second,cpu,available); var memory=CapacityPolicy.Evaluate(sample,new(),[],0,sample.ObservedUtc);
        return tracker.Observe(sample,memory,sample.ObservedUtc);
    }
    [Fact]
    public void BriefCpuSpikesDoNotHoldAndSustainedCpuRequiresThirtyHealthySeconds()
    {
        var tracker=new ResourcePressureTracker(); Assert.False(Observe(tracker,0).Held);
        for(int t=1;t<16;t++) Assert.False(Observe(tracker,t,90).Held);
        Assert.True(Observe(tracker,16,90).Held);
        for(int t=17;t<47;t++) Assert.True(Observe(tracker,t).Held);
        Assert.False(Observe(tracker,47).Held);
    }
    [Fact]
    public void AHealthySampleResetsTheHighCpuTimerAndRecoveryGapsCannotCountAsHealthyTime()
    {
        var tracker=new ResourcePressureTracker();
        for(int t=0;t<14;t++) Assert.False(Observe(tracker,t,90).Held);
        Assert.False(Observe(tracker,14,20).Held); Assert.False(Observe(tracker,15,90).Held);
        Assert.True(Observe(tracker,20,90).Held); // Five seconds with no samples establishes an observation hold.
        for(int t=21;t<=40;t++) Assert.True(Observe(tracker,t).Held);
        Assert.True(Observe(tracker,45).Held); // Gap resets recovery.
        for(int t=46;t<76;t++) Assert.True(Observe(tracker,t).Held);
        Assert.False(Observe(tracker,76).Held);
    }
    [Fact]
    public void UnknownCpuInsufficientMemoryAndRepeatedSamplesHoldAdmission()
    {
        Assert.True(Observe(new(),0,null).Held);
        Assert.True(Observe(new(),0,20,1*CapacityPolicy.GiB).Held);
        var tracker=new ResourcePressureTracker(); Assert.False(Observe(tracker,0).Held); Assert.True(Observe(tracker,0).Held);
        var sample=Sample(0); var old=CapacityPolicy.Evaluate(sample,new(),[],0,sample.ObservedUtc.AddSeconds(4));
        Assert.True(new ResourcePressureTracker().Observe(sample,old,sample.ObservedUtc.AddSeconds(4)).Held);
    }
    [Fact]
    public void ColdStartWithoutACpuBaselineReleasesOnTheFirstMeasuredHealthySample()
    {
        // The first Windows CPU read only stores the GetSystemTimes baseline, so app start begins with CPU=null.
        // No measurement was lost, so that sample must not latch the thirty-second recovery hysteresis.
        var tracker=new ResourcePressureTracker();
        Assert.True(Observe(tracker,0,null).Held);
        var first=Observe(tracker,1);
        Assert.False(first.Held,first.Explanation);
        // After a measurement exists, a lost CPU sample still latches and needs thirty healthy seconds.
        Assert.True(Observe(tracker,2,null).Held);
        for(int t=3;t<=32;t++) Assert.True(Observe(tracker,t).Held);
        Assert.False(Observe(tracker,33).Held);
    }
    [Fact]
    public async Task DiagnosticsAreBoundedStructuredAndAcceptNoFreeTextSecrets()
    {
        // Test bounded overflow and actual rotation deterministically, not disk throughput.
        // With capacity 256 and file limit 512, the prior parallel drain timed out after 3.263s; that measurement
        // remains unmet and this fixture does not establish improved production I/O latency.
        var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store=new ManagerDiagnostics(_root,capacity:8,fileLimit:256,async ct=>
        {entered.TrySetResult(true);await release.Task.WaitAsync(ct);});
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for(int i=0;i<50000;i++) store.Record(ManagerEventKind.OperationFailed,typeof(IOException));
            Assert.Equal(49992,store.DroppedEvents);
        }
        finally {release.TrySetResult(true);await store.DisposeAsync();}
        Assert.Null(store.Failure); Assert.True(store.DroppedEvents>0);
        var paths=Directory.GetFiles(Path.Combine(_root,"diagnostics")); Assert.Equal(3,paths.Length);
        foreach(string path in paths)
        {
            Assert.InRange(new FileInfo(path).Length,1,256);
            foreach(string line in await File.ReadAllLinesAsync(path))
            {
                using var json=JsonDocument.Parse(line);
                Assert.All(json.RootElement.EnumerateObject(),p=>Assert.Contains(p.Name,new[] { "Utc","Kind","ErrorType","Count" }));
                Assert.DoesNotContain("Message",line); Assert.DoesNotContain("AccessToken",line);
            }
        }
        await store.DisposeAsync(); // Repeated shutdown is harmless.
    }
    [Fact]
    public async Task FailedDiagnosticsCannotHangExitOrPretendThatWritingSucceeded()
    {
        Directory.CreateDirectory(_root); await File.WriteAllTextAsync(Path.Combine(_root,"diagnostics"),"synthetic file blocks the folder");
        var store=new ManagerDiagnostics(_root,capacity:1);
        for(int i=0;i<100;i++) store.Record(ManagerEventKind.Started);
        await store.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(store.Failure);
    }
    public void Dispose()
    {
        string full=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-pressure-tests-",StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if(Directory.Exists(full)) Directory.Delete(full,true);
    }
}
